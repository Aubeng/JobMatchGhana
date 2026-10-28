using System.Globalization;
using System.Text.Json;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Domain.Matching;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobMatch.Infrastructure.Matching;

/// <summary>
/// The S&D matching engine (L5; Design §6.5) for one worker:
///   Stage 1 (here, in SQL): open, unexpired jobs in the worker's district or an adjacent one,
///           with compatible availability, not already shown to them in conversation.
///   Stage 2 (<see cref="IMatchRanker"/>): MiniLM cosine similarity, top 3 above the threshold.
///   Fallback (DL-020): when nothing in or near the worker's areas passes both stages, the same two
///           stages run over every other area, and the cards say the jobs are outside their areas.
/// Each result is stored as a <see cref="Match"/> with its full score breakdown (research data)
/// and returned as a <see cref="MatchCard"/> carrying a plain-language reason (FR-13).
/// </summary>
public class MatchingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly JobMatchDbContext _db;
    private readonly IMatchRanker _ranker;
    private readonly DialogueCatalog _catalog;
    private readonly MatchingOptions _options;
    private readonly ILogger<MatchingService> _logger;

    public MatchingService(JobMatchDbContext db, IMatchRanker ranker, DialogueCatalog catalog,
        IOptions<MatchingOptions> options, ILogger<MatchingService> logger)
    {
        _db = db;
        _ranker = ranker;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MatchCard>> FindForWorkerAsync(Worker worker, CancellationToken ct)
    {
        var areas = await AreasAsync(worker, ct);
        if (areas.Count == 0) return Array.Empty<MatchCard>();
        var profile = await ProfileAsync(worker, ct);
        if (profile.Skills.Count == 0) return Array.Empty<MatchCard>();

        // Stage 1: geographic + availability filter, over every area the worker named (DL-018).
        var districts = areas.SelectMany(_catalog.Neighbourhood).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cards = await RankAndRecordAsync(worker, areas, profile, districts, nearby: true, ct);

        // Fallback (DL-020): location is the only thing missing, so look further afield and say so.
        if (cards.Count == 0 && _options.SearchOtherAreas)
            cards = await RankAndRecordAsync(worker, areas, profile, districts, nearby: false, ct);
        return cards;
    }

    /// <summary>
    /// Stage 1 inside (<paramref name="nearby"/>) or outside the worker's neighbourhood, then stage 2,
    /// recording each delivered match.
    /// </summary>
    private async Task<List<MatchCard>> RankAndRecordAsync(Worker worker, IReadOnlyList<string> areas, MatchProfile profile,
        List<string> districts, bool nearby, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Matches.Where(m => m.WorkerId == worker.Id).ToListAsync(ct);
        var shown = existing.Where(m => m.Channel == "conversation").Select(m => m.JobPostingId).ToHashSet();
        var open = _db.JobPostings.Include(j => j.Skills).ThenInclude(s => s.Skill)
            .Where(j => j.Status == "Open" && j.District != null);
        var jobs = (await (nearby ? open.Where(j => districts.Contains(j.District!)) : open.Where(j => !districts.Contains(j.District!)))
                .ToListAsync(ct))
            .Where(j => (j.ExpiresUtc == null || j.ExpiresUtc > now)
                        && !shown.Contains(j.Id)
                        && AvailabilityRules.Compatible(worker.Availability, j.Availability))
            .ToList();
        if (jobs.Count == 0) return new List<MatchCard>();

        // Stage 2: semantic ranking.
        var result = await _ranker.RankAsync(profile, jobs.Select(Candidate).ToList(), _options.TopK, _options.MinScore, ct);

        var cards = new List<MatchCard>();
        foreach (var ranked in result.Matches)
        {
            var job = jobs.Single(j => j.Id == ranked.JobId);
            // A job first offered by alert is upgraded to a conversation delivery, not duplicated.
            var match = existing.SingleOrDefault(m => m.JobPostingId == job.Id);
            if (match is null)
            {
                match = new Match { WorkerId = worker.Id, JobPostingId = job.Id };
                _db.Matches.Add(match);
            }
            Record(match, worker, areas, job, ranked, result, "conversation", now);
            await _db.SaveChangesAsync(ct);
            cards.Add(Card(match.Id, ranked, job, areas));
        }
        _logger.LogInformation("Matching ({Scope}): {Candidates} candidates -> {Matches} matches ({Ranker}).",
            nearby ? "worker's areas" : "other areas", jobs.Count, cards.Count, result.Ranker);
        return cards;
    }

    internal async Task<MatchProfile> ProfileAsync(Worker worker, CancellationToken ct)
    {
        var skills = await _db.WorkerSkills.Where(ws => ws.WorkerId == worker.Id && ws.RemovedUtc == null)
            .Select(ws => ws.Skill!.CanonicalName).ToListAsync(ct);
        return new MatchProfile(worker.TradeName, skills);
    }

    /// <summary>The worker's area districts, main area first (DL-018).</summary>
    internal async Task<IReadOnlyList<string>> AreasAsync(Worker worker, CancellationToken ct)
    {
        var areas = await _db.WorkerAreas.Where(a => a.WorkerId == worker.Id)
            .OrderBy(a => a.SortOrder).Select(a => a.District).ToListAsync(ct);
        if (areas.Count == 0 && worker.LocationDistrict is { } main)
            areas.Add(main);   // profiles confirmed before worker areas existed
        return areas;
    }

    internal static MatchCandidate Candidate(JobPosting j) => new(
        j.Id, j.Title, j.Description, j.Skills.Select(s => s.Skill!.CanonicalName).ToList());

    /// <summary>Fill a match row with its scores and the full, explainable breakdown.</summary>
    internal void Record(Match match, Worker worker, IReadOnlyList<string> areas, JobPosting job, RankedMatch ranked,
        RankingResult result, string channel, DateTimeOffset now)
    {
        match.SemanticScore = ranked.Score;
        match.FinalScore = ranked.Score;   // S&D: ranked by cosine similarity alone
        match.Rank = ranked.Rank;
        match.Channel = channel;
        match.Status = MatchStatus.Notified;
        match.DeliveredUtc = now;
        match.UpdatedUtc = now;
        match.ScoreBreakdownJson = JsonSerializer.Serialize(new
        {
            ranker = result.Ranker,
            profileText = result.ProfileText,
            score = ranked.Score,
            bestSkill = ranked.BestSkill,
            bestSkillScore = ranked.BestSkillScore,
            skillOverlap = ranked.SkillOverlap,
            location = Placement(job, areas) switch
            {
                Place.InArea => "same district",
                Place.Nearby => "adjacent district",
                _ => "outside worker's areas (fallback)"
            },
            workerDistrict = worker.LocationDistrict,
            workerDistricts = areas,
            jobDistrict = job.District,
            workerAvailability = worker.Availability,
            jobAvailability = job.Availability,
            minScore = _options.MinScore,
            jobIsSynthetic = job.IsSynthetic
        }, Json);
    }

    internal MatchCard Card(int matchId, RankedMatch ranked, JobPosting job, IReadOnlyList<string> areas) => new(
        matchId,
        ranked.Rank,
        job.Title,
        Area(job),
        Pay(job),
        job.Availability,
        Why(ranked, job, areas),
        job.EmployerName,
        OutsideAreas: Placement(job, areas) == Place.Outside);

    /// <summary>FR-13: why this job, in plain words.</summary>
    internal string Why(RankedMatch ranked, JobPosting job, IReadOnlyList<string> areas)
    {
        var skill = ranked.SkillOverlap.Count > 0
            ? $"Needs your skill: {string.Join(", ", ranked.SkillOverlap)}"
            : ranked.BestSkill is not null ? $"Close to your skill: {ranked.BestSkill}" : "Fits your work";
        var place = Placement(job, areas) switch
        {
            Place.InArea => "in your area",
            Place.Nearby => "near your area",
            _ => $"in {job.LocationText ?? job.District}, outside your areas"
        };
        return $"{skill}; {place}";
    }

    private enum Place { InArea, Nearby, Outside }

    private Place Placement(JobPosting job, IReadOnlyList<string> areas)
    {
        if (job.District is null) return Place.Outside;
        if (areas.Contains(job.District, StringComparer.OrdinalIgnoreCase)) return Place.InArea;
        return areas.Any(a => _catalog.Neighbourhood(a).Contains(job.District)) ? Place.Nearby : Place.Outside;
    }

    private static string Area(JobPosting job)
        => job.LocationText is { } t && !string.Equals(t, job.District, StringComparison.OrdinalIgnoreCase)
            ? $"{t} ({job.District})" : job.District ?? job.LocationText ?? "Greater Accra";

    internal static string? Pay(JobPosting job)
    {
        if (job.PayMin is null && job.PayMax is null) return null;
        static string G(decimal v) => v.ToString("#,0", CultureInfo.InvariantCulture);
        var amount = job.PayMin is { } lo && job.PayMax is { } hi && lo != hi ? $"GHS {G(lo)}–{G(hi)}"
            : $"GHS {G(job.PayMin ?? job.PayMax!.Value)}";
        return job.PayPeriod switch
        {
            "day" => amount + " per day",
            "week" => amount + " per week",
            "month" => amount + " per month",
            "job" => amount + " for the job",
            _ => amount
        };
    }
}

public record AlertResult(int Alerted, int Failed);

/// <summary>
/// New-job alerts: when a job is published, find confirmed workers it suits (the same two
/// stages, run in reverse: workers whose district neighbours the job's) and send each a
/// WhatsApp utility template. Templates are the only business-initiated messages (cost
/// discipline), at most one per worker per <see cref="MatchingOptions.AlertCooldownHours"/>.
/// The template's "See jobs" button replies "matches", which delivers the job in conversation.
/// </summary>
public class JobAlertService
{
    private readonly JobMatchDbContext _db;
    private readonly IMatchRanker _ranker;
    private readonly MatchingService _matching;
    private readonly DialogueCatalog _catalog;
    private readonly Domain.Abstractions.IWhatsAppClient _whatsapp;
    private readonly MatchingOptions _options;
    private readonly ILogger<JobAlertService> _logger;

    public JobAlertService(JobMatchDbContext db, IMatchRanker ranker, MatchingService matching, DialogueCatalog catalog,
        Domain.Abstractions.IWhatsAppClient whatsapp, IOptions<MatchingOptions> options, ILogger<JobAlertService> logger)
    {
        _db = db;
        _ranker = ranker;
        _matching = matching;
        _catalog = catalog;
        _whatsapp = whatsapp;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns how many workers were alerted, and how many sends failed.</summary>
    public async Task<AlertResult> AlertForJobAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.JobPostings.Include(j => j.Skills).ThenInclude(s => s.Skill).SingleAsync(j => j.Id == jobId, ct);
        if (job.Status != "Open" || job.District is null) return new AlertResult(0, 0);

        var now = DateTimeOffset.UtcNow;
        var districts = _catalog.Neighbourhood(job.District).ToList();
        var cooldownStart = now.AddHours(-_options.AlertCooldownHours);
        var workers = (await _db.Workers
                .Where(w => w.IsActive && w.ConsentGiven && w.ProfileConfirmedUtc != null
                            && (w.Areas.Any(a => districts.Contains(a.District))
                                || (w.LocationDistrict != null && districts.Contains(w.LocationDistrict))))
                .ToListAsync(ct))
            .Where(w => AvailabilityRules.Compatible(w.Availability, job.Availability))
            .ToList();

        int alerted = 0, failed = 0;
        foreach (var worker in workers)
        {
            var matches = await _db.Matches.Where(m => m.WorkerId == worker.Id).ToListAsync(ct);
            if (matches.Any(m => m.JobPostingId == job.Id)) continue;
            if (matches.Any(m => m.Channel == "alert" && m.DeliveredUtc > cooldownStart)) continue;

            var profile = await _matching.ProfileAsync(worker, ct);
            if (profile.Skills.Count == 0) continue;
            var result = await _ranker.RankAsync(profile, new[] { MatchingService.Candidate(job) }, 1, _options.MinScore, ct);
            if (result.Matches.Count == 0) continue;

            var match = new Match { WorkerId = worker.Id, JobPostingId = job.Id };
            _matching.Record(match, worker, await _matching.AreasAsync(worker, ct), job, result.Matches[0], result, "alert", now);
            _db.Matches.Add(match);
            await _db.SaveChangesAsync(ct);

            var area = job.LocationText ?? job.District;
            try
            {
                await _whatsapp.SendTemplateAsync(worker.WaId, _options.AlertTemplate, _options.AlertTemplateLanguage,
                    new[] { job.Title, area }, quickReplyPayload: "matches", ct);
                alerted++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One failed send must not stop the others. The match is removed so it isn't
                // counted as delivered and a later publish or alert run can try again.
                _logger.LogError(ex, "Alert for job {JobId} to worker {WorkerId} failed.", job.Id, worker.Id);
                _db.Matches.Remove(match);
                await _db.SaveChangesAsync(ct);
                failed++;
            }
        }
        _logger.LogInformation("Job {JobId}: {Alerted} of {Eligible} nearby workers alerted, {Failed} failed.",
            job.Id, alerted, workers.Count, failed);
        return new AlertResult(alerted, failed);
    }
}
