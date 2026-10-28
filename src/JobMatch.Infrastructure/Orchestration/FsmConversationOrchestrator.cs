using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Cv;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using JobMatch.Infrastructure.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobMatch.Infrastructure.Orchestration;

/// <summary>
/// The impure shell around the pure dialogue state machine (the FSM itself does no I/O):
///   1. Idempotency: dedupe on <c>MessageLog.WaMessageId</c> (at-least-once delivery).
///   2. Load the Redis session, run NLU when allowed (post-consent free text), step the FSM.
///   3. Persistence: the worker row at consent; the whole skills profile when the worker
///      confirms the S5 summary (FR-10); "delete my data" removes everything (FR-16).
///   4. Instrument every turn: FSM state and intent, NLU prediction, repair/handoff events.
///   5. Matching: when the FSM asks for it, run the engine and let the FSM deliver the results
///      (S6); store S7 ratings, comments and "I'm interested" on the match rows.
///   6. Send replies (interactive where offered), split to the 500-character limit (NFR-05).
/// </summary>
public class FsmConversationOrchestrator : IConversationOrchestrator
{
    private readonly JobMatchDbContext _db;
    private readonly ISessionStore _sessions;
    private readonly IWhatsAppClient _whatsapp;
    private readonly INluClient _nlu;
    private readonly DialogueCatalog _catalog;
    private readonly MatchingService _matching;
    private readonly ICvRenderer _cvRenderer;
    private readonly ILogger<FsmConversationOrchestrator> _logger;

    public FsmConversationOrchestrator(
        JobMatchDbContext db,
        ISessionStore sessions,
        IWhatsAppClient whatsapp,
        INluClient nlu,
        DialogueCatalog catalog,
        MatchingService matching,
        ICvRenderer cvRenderer,
        ILogger<FsmConversationOrchestrator> logger)
    {
        _db = db;
        _sessions = sessions;
        _whatsapp = whatsapp;
        _nlu = nlu;
        _catalog = catalog;
        _matching = matching;
        _cvRenderer = cvRenderer;
        _logger = logger;
    }

    public async Task HandleAsync(InboundMessage message, CancellationToken ct)
    {
        // 1) Idempotency: fast-path check for the common redelivery case, backed by the
        //    unique filtered index on MessageLog.WaMessageId as the hard guarantee.
        if (await AlreadyProcessedAsync(message.WaMessageId, ct))
        {
            _logger.LogInformation("Duplicate inbound {MessageId} ignored (idempotent).", message.WaMessageId);
            return;
        }

        var inboundLog = new MessageLog
        {
            WaMessageId = message.WaMessageId,
            WaId = message.WaId,
            Direction = MessageDirection.Inbound,
            Body = message.Text,
            MessageType = message.MessageType,
            Timestamp = message.Timestamp
        };
        _db.MessageLogs.Add(inboundLog);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent/redelivered copy already claimed this message id (unique index).
            if (await AlreadyProcessedAsync(message.WaMessageId, ct))
            {
                _logger.LogInformation("Duplicate inbound {MessageId} ignored (idempotent).", message.WaMessageId);
                _db.ChangeTracker.Clear();
                return;
            }
            throw;
        }

        // Best-effort read receipt.
        try { await _whatsapp.MarkReadAsync(message.WaMessageId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "mark-read failed for {Id}", message.WaMessageId); }

        // 2) Load the session and run one FSM step.
        var session = await _sessions.GetAsync(message.WaId, ct);
        if (session is not null && !Enum.IsDefined(session.State))
        {
            // A session from before DL-015 (retired Slice 1 state): start the new flow cleanly.
            _logger.LogInformation("Session for {WaId} was in retired state {State}; restarting.", message.WaId, (int)session.State);
            session = null;
        }
        session ??= new ConversationSession { WaId = message.WaId, State = ConversationState.New };
        var stateBefore = session.State;
        if (session.ProfileConfirmed && session.Cv is null)
            session.Cv = await LoadCvAsync(message.WaId, ct);   // a CV saved before this session

        var isTap = message.MessageType is "interactive" or "button";
        NluResult? nlu = null;
        if (ConversationFsm.ShouldAnalyze(session, message.Text, isTap))
            nlu = await _nlu.AnalyzeAsync(message.Text, session.State.ToString(), ct);

        var result = ConversationFsm.Step(session, message.Text, nlu, _catalog);

        // 3) Instrument the inbound turn (research metrics).
        inboundLog.IntentDetected = result.Intent;
        inboundLog.SessionState = stateBefore;
        inboundLog.NluConfidence = result.NluConfidence;
        inboundLog.IsRepair = result.IsRepair;
        if (nlu is not null)
        {
            inboundLog.NluIntent = nlu.Intent;
            inboundLog.NluIntentConfidence = nlu.Confidence;
            inboundLog.NluEntitiesJson = nlu.EntitiesJson;
            inboundLog.NluModelVersion = nlu.ModelVersion;
            inboundLog.NluLatencyMs = nlu.LatencyMs;
        }

        if (result.DeleteRequested)
        {
            await DeleteEverythingAsync(message.WaId, ct);
            await SendAsync(message.WaId, result.Replies, logAs: null, ct);
            return;
        }

        if (result.HandoffRequested)
        {
            _logger.LogWarning("Human handoff requested: wa={WaId} state={State} (3 failed turns).", message.WaId, stateBefore);
            // One open case per worker: a person follows up from the admin SPA.
            if (!await _db.HandoffCases.AnyAsync(h => h.WaId == message.WaId && h.ResolvedUtc == null, ct))
                _db.HandoffCases.Add(new HandoffCase { WaId = message.WaId, State = stateBefore });
        }

        // 4) Worker + profile (consent-gated; profile only once confirmed).
        var worker = await SyncWorkerAsync(result, message.WaId, ct);
        await RecordFeedbackAsync(result, ct);
        if (result.SendCv && worker is not null)
            await SaveCvAsync(worker, result.Session.Cv!, ct);

        // 5) S6: run the matching engine and let the FSM present the results.
        var replies = result.Replies.ToList();
        if (result.RequestMatches && worker is not null)
        {
            var cards = await _matching.FindForWorkerAsync(worker, ct);
            replies.AddRange(ConversationFsm.DeliverMatches(result.Session, cards).Replies);
        }

        // 6) Persist the session (Redis, 72h TTL per FR-09).
        await _sessions.SaveAsync(result.Session, ct);

        // 7) Send the CV (Slice 6) first, then the replies; log each outbound message.
        if (result.SendCv && worker is not null && !await SendCvAsync(message.WaId, worker, result.Session, ct))
            replies = new List<OutboundMessage> { DialoguePrompts.CvSendFailed() };
        await SendAsync(message.WaId, replies, result.Session.State, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task SendAsync(string waId, IReadOnlyList<OutboundMessage> replies, ConversationState? logAs, CancellationToken ct)
    {
        foreach (var part in replies.SelectMany(r => MessageRendering.Split(r)))
        {
            string? outId = null;
            try
            {
                outId = await _whatsapp.SendAsync(waId, part, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound send failed to {WaId}", waId);
            }

            if (logAs is null) continue;   // after a deletion nothing more is stored
            _db.MessageLogs.Add(new MessageLog
            {
                WaMessageId = outId,
                WaId = waId,
                Direction = MessageDirection.Outbound,
                Body = MessageRendering.ForLog(part),
                MessageType = part.Buttons is not null || part.List is not null ? "interactive" : "text",
                SessionState = logAs,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>Render the CV from the session and send it as a WhatsApp document.</summary>
    private async Task<bool> SendCvAsync(string waId, Worker worker, ConversationSession session, CancellationToken ct)
    {
        try
        {
            var content = CvContent.From(session);
            var pdf = _cvRenderer.Render(content);
            var outId = await _whatsapp.SendDocumentAsync(waId, pdf, content.FileName, "application/pdf", "Your CV 📄", ct);
            _db.MessageLogs.Add(new MessageLog
            {
                WaMessageId = outId, WaId = waId, Direction = MessageDirection.Outbound, Body = $"[document] {content.FileName}",
                MessageType = "document", SessionState = session.State, Timestamp = DateTimeOffset.UtcNow
            });
            if (await _db.WorkerCvs.FirstOrDefaultAsync(c => c.WorkerId == worker.Id, ct) is { } saved)
            {
                saved.LastSentUtc = DateTimeOffset.UtcNow;
                saved.TimesSent++;
            }
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Sending the CV to worker {WorkerId} failed.", worker.Id);
            return false;
        }
    }

    /// <summary>Save the confirmed CV answers (replacing any earlier CV).</summary>
    private async Task SaveCvAsync(Worker worker, CvDraft draft, CancellationToken ct)
    {
        var cv = await _db.WorkerCvs.Include(c => c.Jobs).FirstOrDefaultAsync(c => c.WorkerId == worker.Id, ct);
        if (cv is null)
        {
            cv = new WorkerCv { WorkerId = worker.Id };
            _db.WorkerCvs.Add(cv);
        }
        cv.FullName = draft.FullName ?? "";
        cv.Phone = draft.Phone;
        cv.Training = draft.Training.Count == 0 ? null : string.Join("\n", draft.Training);
        cv.Education = draft.Education;
        cv.Languages = draft.Languages.Count == 0 ? null : string.Join(", ", draft.Languages);
        cv.About = draft.About is { Length: > 300 } about ? about[..300] : draft.About;
        cv.ConsentUtc = draft.ConsentUtc ?? DateTimeOffset.UtcNow;
        cv.CompletedUtc = DateTimeOffset.UtcNow;
        _db.WorkerCvJobs.RemoveRange(cv.Jobs);
        cv.Jobs = draft.Jobs.Select((j, i) => new WorkerCvJob
        {
            Description = j.Description.Length <= 160 ? j.Description : j.Description[..160],
            Period = j.Period, SourceUtterance = j.SourceUtterance, SortOrder = i
        }).ToList();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>A saved CV, as a completed draft, so "cv" shows it again after the session has expired.</summary>
    private async Task<CvDraft?> LoadCvAsync(string waId, CancellationToken ct)
    {
        var cv = await _db.WorkerCvs.AsNoTracking().Include(c => c.Jobs)
            .FirstOrDefaultAsync(c => c.Worker!.WaId == waId, ct);
        if (cv is null) return null;
        return new CvDraft
        {
            ConsentUtc = cv.ConsentUtc, FullName = cv.FullName, PhoneAnswered = true, Phone = cv.Phone,
            Jobs = cv.Jobs.OrderBy(j => j.SortOrder)
                .Select(j => new CvJob { Description = j.Description, Period = j.Period, SourceUtterance = j.SourceUtterance }).ToList(),
            JobsDone = true,
            Training = cv.Training?.Split('\n').ToList() ?? new List<string>(), TrainingDone = true,
            Education = cv.Education,
            Languages = cv.Languages?.Split(", ").ToList() ?? new List<string>(), LanguagesDone = true,
            About = cv.About, Completed = true
        };
    }

    private Task<bool> AlreadyProcessedAsync(string waMessageId, CancellationToken ct) =>
        _db.MessageLogs.AsNoTracking()
            .AnyAsync(m => m.WaMessageId == waMessageId && m.Direction == MessageDirection.Inbound, ct);

    /// <summary>
    /// Consent gate: the worker row is created when consent is given (never before). The skills
    /// profile is written only when the worker confirms the S5 summary (FR-10).
    /// </summary>
    private async Task<Worker?> SyncWorkerAsync(FsmStepResult result, string waId, CancellationToken ct)
    {
        var session = result.Session;
        if (!session.ConsentGiven && !result.OptedOut)
            return null;

        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.WaId == waId, ct);
        if (worker is null)
        {
            if (!session.ConsentGiven) return null;   // opted out before consenting: nothing to store
            worker = new Worker { WaId = waId, CreatedUtc = DateTimeOffset.UtcNow };
            _db.Workers.Add(worker);
        }

        if (session.ConsentGiven && !worker.ConsentGiven)
        {
            worker.ConsentGiven = true;
            worker.ConsentUtc = DateTimeOffset.UtcNow;
        }
        if (result.OptedOut)
            worker.IsActive = false;

        if (result.ProfileConfirmed)
        {
            worker.IsActive = true;
            worker.TradeCategoryId = session.TradeId;
            worker.TradeName = session.TradeName;
            worker.ExperienceYears = session.ExperienceYears;
            worker.ExperienceText = session.ExperienceText;
            worker.Availability = session.Availability;
            worker.LocationDistrict = session.LocationDistrict;
            worker.LocationRegion = session.LocationRegion;
            worker.LocationText = session.LocationText;
            worker.ProfileConfirmedUtc = DateTimeOffset.UtcNow;
        }
        worker.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);   // worker.Id is needed for skill links

        if (result.ProfileConfirmed)
        {
            await SyncSkillsAsync(worker, session.Skills, ct);
            await SyncAreasAsync(worker, session.Areas, ct);
        }
        return worker;
    }

    /// <summary>Make the worker's areas equal the confirmed list, main area first (DL-018).</summary>
    private async Task SyncAreasAsync(Worker worker, IReadOnlyList<AreaChoice> areas, CancellationToken ct)
    {
        _db.WorkerAreas.RemoveRange(await _db.WorkerAreas.Where(a => a.WorkerId == worker.Id).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);   // free the (worker, district) keys before re-adding
        _db.WorkerAreas.AddRange(areas.Select((a, i) => new WorkerArea
        {
            WorkerId = worker.Id, District = a.District, Region = a.Region, LocationText = a.Label, SortOrder = i
        }));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>S7 answers onto the delivered match rows (the Matching Relevance metric's data).</summary>
    private async Task RecordFeedbackAsync(FsmStepResult result, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (result.InterestedMatchId is { } interestedId
            && await _db.Matches.FirstOrDefaultAsync(m => m.Id == interestedId, ct) is { } interested)
        {
            interested.Status = MatchStatus.Interested;
            interested.InterestedUtc ??= now;
            interested.UpdatedUtc = now;
        }
        if (result.FeedbackMatchIds.Count > 0 && (result.Rating is not null || result.FeedbackText is not null))
        {
            var rows = await _db.Matches.Where(m => result.FeedbackMatchIds.Contains(m.Id)).ToListAsync(ct);
            foreach (var m in rows)
            {
                if (result.Rating is { } rating) { m.Rating = rating; m.RatedUtc = now; }
                if (result.FeedbackText is { } text) m.FeedbackText = text;
                m.UpdatedUtc = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        if (result.InterestedMatchId is not null)
            _logger.LogInformation("Worker interested in match {MatchId}: BOOST team to connect.", result.InterestedMatchId);
    }

    /// <summary>
    /// Make the worker's active skills equal the confirmed set. New skills keep the utterance
    /// and confidence that produced them (research data); dropped skills are marked removed,
    /// not deleted.
    /// </summary>
    private async Task SyncSkillsAsync(Worker worker, IReadOnlyList<CapturedSkill> confirmed, CancellationToken ct)
    {
        var active = await _db.WorkerSkills.Include(ws => ws.Skill)
            .Where(ws => ws.WorkerId == worker.Id && ws.RemovedUtc == null)
            .ToListAsync(ct);
        var wanted = confirmed.Select(c => c.CanonicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var ws in active.Where(ws => !wanted.Contains(ws.Skill!.CanonicalName)))
            ws.RemovedUtc = DateTimeOffset.UtcNow;

        var have = active.Select(ws => ws.Skill!.CanonicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var captured in confirmed.Where(c => !have.Contains(c.CanonicalName)))
        {
            var name = captured.CanonicalName.Trim();
            var skill = await _db.Skills.FirstOrDefaultAsync(s => s.CanonicalName == name, ct);
            if (skill is null)
            {
                skill = new Skill { CanonicalName = name, Category = _catalog.FindSkill(name)?.Trade.Name };
                _db.Skills.Add(skill);
                await _db.SaveChangesAsync(ct);
            }
            _db.WorkerSkills.Add(new WorkerSkill
            {
                WorkerId = worker.Id,
                SkillId = skill.Id,
                SourceUtterance = captured.SourceUtterance,   // research data, preserved
                ExtractionConfidence = captured.Confidence
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// FR-16: permanently remove the worker's profile, skills, matches and every logged message,
    /// plus the session. The research consent covers data only while the worker keeps it there.
    /// </summary>
    private async Task DeleteEverythingAsync(string waId, CancellationToken ct)
    {
        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.WaId == waId, ct);
        if (worker is not null)
        {
            _db.WorkerSkills.RemoveRange(await _db.WorkerSkills.Where(ws => ws.WorkerId == worker.Id).ToListAsync(ct));
            _db.WorkerAreas.RemoveRange(await _db.WorkerAreas.Where(a => a.WorkerId == worker.Id).ToListAsync(ct));
            var cvs = await _db.WorkerCvs.Include(c => c.Jobs).Where(c => c.WorkerId == worker.Id).ToListAsync(ct);
            _db.WorkerCvJobs.RemoveRange(cvs.SelectMany(c => c.Jobs));
            _db.WorkerCvs.RemoveRange(cvs);
            _db.Matches.RemoveRange(await _db.Matches.Where(m => m.WorkerId == worker.Id).ToListAsync(ct));
            _db.Workers.Remove(worker);
        }
        _db.MessageLogs.RemoveRange(await _db.MessageLogs.Where(m => m.WaId == waId).ToListAsync(ct));
        _db.HandoffCases.RemoveRange(await _db.HandoffCases.Where(h => h.WaId == waId).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        await _sessions.DeleteAsync(waId, ct);
        _logger.LogInformation("Deleted all data for a worker on request (FR-16).");
    }
}
