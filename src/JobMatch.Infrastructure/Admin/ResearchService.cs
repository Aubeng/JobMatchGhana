using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Nlu;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JobMatch.Infrastructure.Admin;

/// <summary>Research data handling.</summary>
public class ResearchOptions
{
    public const string SectionName = "Research";

    /// <summary>
    /// Secret key for pseudonymous participant ids in exports (HMAC-SHA256 of the WhatsApp id).
    /// The same key always gives the same id, so exports can be joined; without it the ids
    /// can't be reversed. Keep it out of the repo, like any other secret (DL-007).
    /// </summary>
    public string? PseudonymKey { get; set; }
}

// ---- Analytics: the S&D §4.2 quantitative criteria, with their pre-specified targets ----

public record Metric(double? Value, double Target, bool HigherIsBetter, int N, string Note)
{
    public bool? MeetsTarget => Value is null ? null : HigherIsBetter ? Value >= Target : Value <= Target;
}

/// <summary>Worker journey counts; <see cref="CvsMade"/> is the optional CV builder (Slice 6), reported separately.</summary>
public record Funnel(int Consented, int ProfilesConfirmed, int Matched, int Rated, int Interested, int Connected, int CvsMade);

public record Analytics(
    DateTimeOffset GeneratedUtc,
    Funnel Funnel,
    Metric TaskCompletionRate,
    Metric ConversationEfficiency,
    Metric IntentAccuracy,
    Metric EntityExtractionPrecision,
    Metric MatchingRelevance,
    Metric ResponseLatencyP95Seconds,
    double? RepairRate,
    int OpenHandoffs,
    int HandoffCases,
    double? NluFallbackRate,
    double? NluLatencyP95Ms,
    IReadOnlyDictionary<string, int> NluIntents,
    int SyntheticMatchesRated);

// ---- Skills supply (FR-17), anonymised ----

public record SupplyCell(string Trade, string District, int? Workers);
public record SkillSupply(string Trade, string Skill, int? Workers);
public record Supply(int MinimumCount, int? TotalWorkers, IReadOnlyList<SupplyCell> ByTradeAndDistrict, IReadOnlyList<SkillSupply> BySkill);

// ---- Annotation (intent accuracy / entity precision by manual coding) ----

public record AnnotationProgress(int Annotated, int Eligible, int Target);
public record AnnotationTask(long MessageId, string Text, string State, IReadOnlyList<string> ExtractedSkills, AnnotationProgress Progress);
public record AnnotationInput(string Intent, int? SkillsCorrect);
public record IntentScore(string Intent, int Annotated, int Correct);
public record Confusion(string Annotated, string Predicted, int Count);
public record AnnotationSummary(AnnotationProgress Progress, Metric IntentAccuracy, Metric EntityExtractionPrecision,
    int OtherCount, IReadOnlyList<IntentScore> PerIntent, IReadOnlyList<Confusion> Confusions);

/// <summary>
/// Computes the evaluation metrics the S&D pre-specified (§4.2), the anonymised skills supply
/// view (FR-17), the blind annotation workflow behind the intent-accuracy and entity-precision
/// metrics, and pseudonymised CSV exports (NFR-12). Nothing here returns a phone number or a
/// name; small counts are suppressed.
/// </summary>
public class ResearchService
{
    /// <summary>The 9 S&D intents, plus OTHER for messages that are none of them (names, menu numbers).</summary>
    public static readonly string[] Intents =
    {
        "SKILL_DECLARE", "TRADE_IDENTIFY", "LOCATION_SHARE", "AVAILABILITY_STATE", "CONFIRMATION",
        "CORRECTION", "HELP_REQUEST", "FEEDBACK_PROVIDE", "EXIT", "OTHER"
    };

    /// <summary>Counts below this are shown as "fewer than 3" (small-cell suppression).</summary>
    public const int MinimumCount = 3;
    /// <summary>S&D: intent accuracy is coded on a 20% sample of pilot messages.</summary>
    public const double SampleShare = 0.2;

    private static readonly string[] NotModel = { StubNluClient.ModelVersion, FallbackNluClient.FallbackVersion };
    private readonly JobMatchDbContext _db;
    private readonly ResearchOptions _options;

    public ResearchService(JobMatchDbContext db, IOptions<ResearchOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    // =====================================================================================
    // Analytics
    // =====================================================================================

    public async Task<Analytics> AnalyticsAsync(CancellationToken ct)
    {
        var logs = await _db.MessageLogs.AsNoTracking()
            .Select(m => new LogRow(m.Id, m.WaId, m.Direction, m.Timestamp, m.IntentDetected, m.IsRepair,
                m.NluIntent, m.NluModelVersion, m.NluLatencyMs, m.AnnotatedIntent, m.AnnotatedSkillsCorrect,
                m.AnnotatedSkillsExtracted))
            .ToListAsync(ct);
        var workers = await _db.Workers.AsNoTracking().Where(w => w.ConsentGiven)
            .Select(w => new { w.Id, w.WaId, Confirmed = w.ProfileConfirmedUtc != null }).ToListAsync(ct);
        var matches = await _db.Matches.AsNoTracking()
            .Select(m => new { m.WorkerId, m.Channel, m.Rating, m.InterestedUtc, m.Status, m.JobPosting!.IsSynthetic })
            .ToListAsync(ct);

        var matchedWorkers = matches.Where(m => m.Channel == "conversation").Select(m => m.WorkerId).ToHashSet();
        var funnel = new Funnel(
            workers.Count,
            workers.Count(w => w.Confirmed),
            workers.Count(w => matchedWorkers.Contains(w.Id)),
            matches.Where(m => m.Rating != null).Select(m => m.WorkerId).Distinct().Count(),
            matches.Count(m => m.InterestedUtc != null),
            matches.Count(m => m.Status == MatchStatus.Connected),
            await _db.WorkerCvs.CountAsync(ct));

        // Conversation efficiency: worker messages up to and including the first profile confirmation.
        var turns = logs.Where(l => l.Direction == MessageDirection.Inbound)
            .GroupBy(l => l.WaId)
            .Select(g => g.OrderBy(l => l.Id).ToList())
            .Select(list => list.FindIndex(l => l.IntentDetected == Intent.ConfirmProfile))
            .Where(i => i >= 0).Select(i => i + 1.0).ToList();

        // Response latency: inbound message -> first reply logged after it.
        var latencies = new List<double>();
        foreach (var group in logs.GroupBy(l => l.WaId))
        {
            var ordered = group.OrderBy(l => l.Id).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Direction != MessageDirection.Inbound) continue;
                var reply = ordered.Skip(i + 1).FirstOrDefault(l => l.Direction == MessageDirection.Outbound);
                if (reply is not null) latencies.Add(Math.Max(0, (reply.Timestamp - ordered[i].Timestamp).TotalSeconds));
            }
        }

        var inbound = logs.Where(l => l.Direction == MessageDirection.Inbound).ToList();
        var analysed = inbound.Where(l => l.NluModelVersion is not null).ToList();
        var real = matches.Where(m => !m.IsSynthetic && m.Rating != null).ToList();
        var (intentAccuracy, entityPrecision, _, _, _) = Annotation(logs);

        return new Analytics(
            DateTimeOffset.UtcNow,
            funnel,
            new Metric(Ratio(funnel.Matched, funnel.Consented), 0.70, true, funnel.Consented,
                "Consented workers who completed the profile and received at least one match."),
            new Metric(turns.Count == 0 ? null : Math.Round(turns.Average(), 1), 12, false, turns.Count,
                "Mean worker messages from first contact to a confirmed profile."),
            intentAccuracy,
            entityPrecision,
            new Metric(Ratio(real.Count(m => m.Rating >= 4), real.Count), 0.60, true, real.Count,
                "Rated matches scored 4 or 5 (real jobs only; synthetic demo jobs excluded)."),
            new Metric(latencies.Count == 0 ? null : Math.Round(Percentile(latencies, 0.95), 2), 5, false, latencies.Count,
                "95th percentile seconds from a worker's message to the bot's reply."),
            Ratio(inbound.Count(l => l.IsRepair), inbound.Count),
            await _db.HandoffCases.CountAsync(h => h.ResolvedUtc == null, ct),
            await _db.HandoffCases.CountAsync(ct),
            Ratio(analysed.Count(l => l.NluModelVersion == FallbackNluClient.FallbackVersion), analysed.Count),
            analysed.Where(l => l.NluLatencyMs != null).Select(l => l.NluLatencyMs!.Value).ToList() is { Count: > 0 } nl
                ? Math.Round(Percentile(nl, 0.95), 1) : null,
            analysed.Where(l => l.NluIntent != null).GroupBy(l => l.NluIntent!).OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count()),
            matches.Count(m => m.IsSynthetic && m.Rating != null));
    }

    // =====================================================================================
    // Skills supply (FR-17)
    // =====================================================================================

    public async Task<Supply> SupplyAsync(CancellationToken ct)
    {
        var workers = await _db.Workers.AsNoTracking()
            .Where(w => w.IsActive && w.ConsentGiven && w.ProfileConfirmedUtc != null)
            .Select(w => new { w.Id, w.TradeName, w.LocationDistrict }).ToListAsync(ct);
        var skills = await _db.WorkerSkills.AsNoTracking().Where(ws => ws.RemovedUtc == null)
            .Select(ws => new { ws.WorkerId, ws.Skill!.CanonicalName, ws.Skill.Category }).ToListAsync(ct);
        var ids = workers.Select(w => w.Id).ToHashSet();

        var cells = workers.GroupBy(w => (Trade: w.TradeName ?? "Unknown", District: w.LocationDistrict ?? "Unknown"))
            .OrderBy(g => g.Key.Trade).ThenBy(g => g.Key.District)
            .Select(g => new SupplyCell(g.Key.Trade, g.Key.District, Suppress(g.Count()))).ToList();
        var bySkill = skills.Where(s => ids.Contains(s.WorkerId))
            .GroupBy(s => (Trade: s.Category ?? "Unknown", Skill: s.CanonicalName))
            .OrderBy(g => g.Key.Trade).ThenByDescending(g => g.Count())
            .Select(g => new SkillSupply(g.Key.Trade, g.Key.Skill, Suppress(g.Select(s => s.WorkerId).Distinct().Count())))
            .ToList();
        return new Supply(MinimumCount, Suppress(workers.Count), cells, bySkill);
    }

    private static int? Suppress(int count) => count < MinimumCount ? null : count;

    // =====================================================================================
    // Annotation
    // =====================================================================================

    /// <summary>
    /// A random, not-yet-coded worker message the trained models analysed. The model's predicted
    /// intent is deliberately not returned, so coding is blind. The extracted skills are shown,
    /// because entity precision asks whether each extracted skill is right.
    /// </summary>
    public async Task<AnnotationTask?> NextAnnotationAsync(CancellationToken ct)
    {
        var next = await Eligible().Where(m => m.AnnotatedIntent == null)
            .OrderBy(_ => Guid.NewGuid())
            .Select(m => new { m.Id, m.Body, m.SessionState, m.NluEntitiesJson })
            .FirstOrDefaultAsync(ct);
        if (next is null) return null;
        return new AnnotationTask(next.Id, next.Body ?? "", next.SessionState?.ToString() ?? "", ExtractedSkills(next.NluEntitiesJson),
            await ProgressAsync(ct));
    }

    public async Task<bool> AnnotateAsync(long messageId, AnnotationInput input, string annotator, CancellationToken ct)
    {
        if (!Intents.Contains(input.Intent))
            throw new ArgumentException($"Intent must be one of: {string.Join(", ", Intents)}.");
        var message = await Eligible().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return false;
        var extracted = ExtractedSkills(message.NluEntitiesJson).Count;
        if (input.SkillsCorrect is { } c && (c < 0 || c > extracted))
            throw new ArgumentException($"Skills correct must be between 0 and {extracted}.");

        message.AnnotatedIntent = input.Intent;
        message.AnnotatedSkillsExtracted = extracted == 0 ? null : extracted;
        message.AnnotatedSkillsCorrect = extracted == 0 ? null : input.SkillsCorrect;
        message.AnnotatedBy = annotator;
        message.AnnotatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AnnotationSummary> AnnotationSummaryAsync(CancellationToken ct)
    {
        var logs = await Eligible().AsNoTracking().Where(m => m.AnnotatedIntent != null)
            .Select(m => new LogRow(m.Id, m.WaId, m.Direction, m.Timestamp, m.IntentDetected, m.IsRepair, m.NluIntent,
                m.NluModelVersion, m.NluLatencyMs, m.AnnotatedIntent, m.AnnotatedSkillsCorrect, m.AnnotatedSkillsExtracted))
            .ToListAsync(ct);
        var (accuracy, precision, other, perIntent, confusions) = Annotation(logs);
        return new AnnotationSummary(await ProgressAsync(ct), accuracy, precision, other, perIntent, confusions);
    }

    /// <summary>Messages the trained models analysed: free-text worker turns after consent.</summary>
    private IQueryable<MessageLog> Eligible() => _db.MessageLogs.Where(m =>
        m.Direction == MessageDirection.Inbound && m.NluIntent != null && m.NluModelVersion != null
        && !NotModel.Contains(m.NluModelVersion));

    private async Task<AnnotationProgress> ProgressAsync(CancellationToken ct)
    {
        var eligible = await Eligible().CountAsync(ct);
        var annotated = await Eligible().CountAsync(m => m.AnnotatedIntent != null, ct);
        return new AnnotationProgress(annotated, eligible, (int)Math.Ceiling(eligible * SampleShare));
    }

    private static (Metric Accuracy, Metric Precision, int Other, IReadOnlyList<IntentScore> PerIntent, IReadOnlyList<Confusion> Confusions)
        Annotation(IReadOnlyList<LogRow> logs)
    {
        var coded = logs.Where(l => l.AnnotatedIntent is not null && l.NluIntent is not null
                                    && l.NluModelVersion is not null && !NotModel.Contains(l.NluModelVersion)).ToList();
        // OTHER (a name, a stray word) has no correct intent among the 9: reported, not scored.
        var scored = coded.Where(l => l.AnnotatedIntent != "OTHER").ToList();
        var correct = scored.Count(l => l.AnnotatedIntent == l.NluIntent);
        var withSkills = coded.Where(l => l.AnnotatedSkillsExtracted is > 0 && l.AnnotatedSkillsCorrect is not null).ToList();
        var extracted = withSkills.Sum(l => l.AnnotatedSkillsExtracted!.Value);
        return (
            new Metric(Ratio(correct, scored.Count), 0.85, true, scored.Count,
                "Coded worker messages whose NLU intent matched the coder's (blind manual coding; OTHER excluded)."),
            new Metric(Ratio(withSkills.Sum(l => l.AnnotatedSkillsCorrect!.Value), extracted), 0.80, true, extracted,
                "Extracted skills the coder judged correct."),
            coded.Count - scored.Count,
            scored.GroupBy(l => l.AnnotatedIntent!).OrderBy(g => Array.IndexOf(Intents, g.Key))
                .Select(g => new IntentScore(g.Key, g.Count(), g.Count(l => l.NluIntent == g.Key))).ToList(),
            scored.Where(l => l.AnnotatedIntent != l.NluIntent).GroupBy(l => (l.AnnotatedIntent!, l.NluIntent!))
                .Select(g => new Confusion(g.Key.Item1, g.Key.Item2, g.Count())).OrderByDescending(c => c.Count).ToList());
    }

    private static IReadOnlyList<string> ExtractedSkills(string? entitiesJson)
    {
        if (string.IsNullOrWhiteSpace(entitiesJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(entitiesJson);
            return doc.RootElement.EnumerateArray()
                .Where(e => e.GetProperty("label").GetString() == "SKILL_ITEM")
                .Select(e => e.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.Object
                             && r.TryGetProperty("name", out var n)
                    ? $"{n.GetString()} (\"{e.GetProperty("text").GetString()}\")"
                    : $"\"{e.GetProperty("text").GetString()}\" (not in taxonomy)")
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // =====================================================================================
    // Pseudonymised exports (NFR-12)
    // =====================================================================================

    /// <summary>One row per logged message. No message text, no phone numbers.</summary>
    public async Task<string> MessagesCsvAsync(CancellationToken ct)
    {
        var key = PseudonymKey();
        var rows = await _db.MessageLogs.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        var sb = new StringBuilder("pid,timestamp_utc,direction,session_state,fsm_intent,is_repair,nlu_intent," +
                                   "nlu_confidence,nlu_model_version,nlu_latency_ms,annotated_intent,skills_correct,skills_extracted\n");
        foreach (var m in rows)
            sb.AppendLine(string.Join(',', Pid(key, m.WaId), m.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                m.Direction, m.SessionState, m.IntentDetected, m.IsRepair ? 1 : 0, m.NluIntent,
                m.NluIntentConfidence?.ToString("0.####", CultureInfo.InvariantCulture), Csv(m.NluModelVersion),
                m.NluLatencyMs?.ToString("0.#", CultureInfo.InvariantCulture), m.AnnotatedIntent,
                m.AnnotatedSkillsCorrect, m.AnnotatedSkillsExtracted));
        return sb.ToString();
    }

    /// <summary>One row per match: scores, delivery and feedback. Comments are left out (free text).</summary>
    public async Task<string> MatchesCsvAsync(CancellationToken ct)
    {
        var key = PseudonymKey();
        var rows = await _db.Matches.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new
            {
                m.Worker!.WaId, m.JobPostingId, m.JobPosting!.IsSynthetic, m.Rank, m.Channel, m.SemanticScore, m.Status,
                m.DeliveredUtc, m.Rating, Interested = m.InterestedUtc != null, HasComment = m.FeedbackText != null
            })
            .ToListAsync(ct);
        var sb = new StringBuilder("pid,job_id,job_synthetic,rank,channel,semantic_score,status,delivered_utc,rating,interested,has_comment\n");
        foreach (var m in rows)
            sb.AppendLine(string.Join(',', Pid(key, m.WaId), m.JobPostingId, m.IsSynthetic ? 1 : 0, m.Rank, m.Channel,
                m.SemanticScore.ToString("0.####", CultureInfo.InvariantCulture), m.Status,
                m.DeliveredUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), m.Rating,
                m.Interested ? 1 : 0, m.HasComment ? 1 : 0));
        return sb.ToString();
    }

    private byte[] PseudonymKey()
    {
        if (string.IsNullOrWhiteSpace(_options.PseudonymKey) || _options.PseudonymKey.Length < 16)
            throw new InvalidOperationException(
                "Set Research:PseudonymKey (at least 16 characters, kept secret) before exporting research data.");
        return Encoding.UTF8.GetBytes(_options.PseudonymKey);
    }

    /// <summary>Stable pseudonymous participant id: first 16 hex chars of HMAC-SHA256(key, wa_id).</summary>
    public static string Pid(byte[] key, string waId)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(waId)))[..16].ToLowerInvariant();

    private static string? Csv(string? s) => s is null ? null : s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    // =====================================================================================

    private static double? Ratio(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 4);

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private sealed record LogRow(long Id, string WaId, MessageDirection Direction, DateTimeOffset Timestamp, Intent? IntentDetected,
        bool IsRepair, string? NluIntent, string? NluModelVersion, double? NluLatencyMs, string? AnnotatedIntent,
        int? AnnotatedSkillsCorrect, int? AnnotatedSkillsExtracted);
}
