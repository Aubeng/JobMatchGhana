using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobMatch.Infrastructure.Orchestration;

/// <summary>
/// Slice 1 orchestrator: replaces the Phase 1 echo with the deterministic FSM + Redis
/// sessions. Responsibilities (the FSM itself stays pure):
///   1. Idempotency — dedupe on <c>MessageLog.WaMessageId</c> (at-least-once delivery).
///   2. Load/create the Redis session and run one FSM step (calling the NLU stub when needed).
///   3. Persist worker profile + confirmed skills once consent is given (consent gate).
///   4. Instrument every turn: FSM state, detected intent, NLU confidence, repair flag.
///   5. Send outbound replies and mark the inbound message read.
/// </summary>
public class FsmConversationOrchestrator : IConversationOrchestrator
{
    private readonly JobMatchDbContext _db;
    private readonly ISessionStore _sessions;
    private readonly IWhatsAppClient _whatsapp;
    private readonly INluClient _nlu;
    private readonly ILogger<FsmConversationOrchestrator> _logger;

    public FsmConversationOrchestrator(
        JobMatchDbContext db,
        ISessionStore sessions,
        IWhatsAppClient whatsapp,
        INluClient nlu,
        ILogger<FsmConversationOrchestrator> logger)
    {
        _db = db;
        _sessions = sessions;
        _whatsapp = whatsapp;
        _nlu = nlu;
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

        // 2) Load or create the session and run one FSM step.
        var session = await _sessions.GetAsync(message.WaId, ct)
                      ?? new ConversationSession { WaId = message.WaId, State = ConversationState.New };
        var stateBefore = session.State;

        NluResult? nlu = null;
        if (ConversationFsm.RequiresNlu(session, message.Text))
            nlu = await _nlu.AnalyzeAsync(message.Text, session.State.ToString(), ct);

        var result = ConversationFsm.Step(session, message.Text, nlu);

        // 3) Instrument the inbound turn (research metrics).
        inboundLog.IntentDetected = result.Intent;
        inboundLog.SessionState = stateBefore;
        inboundLog.NluConfidence = result.NluConfidence;
        inboundLog.IsRepair = result.IsRepair;

        // 4) Sync worker + skills (only after consent — consent gate).
        await SyncWorkerAsync(result, message, ct);

        // 5) Persist the session (Redis, 24h TTL).
        await _sessions.SaveAsync(result.Session, ct);

        // 6) Send replies and log each outbound turn.
        foreach (var reply in result.Replies)
        {
            string? outId = null;
            try
            {
                outId = await _whatsapp.SendTextAsync(message.WaId, reply.Text, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound send failed to {WaId}", message.WaId);
            }

            _db.MessageLogs.Add(new MessageLog
            {
                WaMessageId = outId,
                WaId = message.WaId,
                Direction = MessageDirection.Outbound,
                Body = reply.Text,
                MessageType = "text",
                SessionState = result.Session.State,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);

        if (result.IsRepair)
            _logger.LogInformation("Repair event: wa={WaId} state={State} count={Count}",
                message.WaId, stateBefore, result.Session.RepairCount);
    }

    private Task<bool> AlreadyProcessedAsync(string waMessageId, CancellationToken ct) =>
        _db.MessageLogs.AsNoTracking()
            .AnyAsync(m => m.WaMessageId == waMessageId && m.Direction == MessageDirection.Inbound, ct);

    /// <summary>
    /// Upsert the Worker row from the session state — but never before consent. Also persists
    /// a newly confirmed skill (with its source utterance + confidence) and handles opt-out.
    /// </summary>
    private async Task SyncWorkerAsync(FsmStepResult result, InboundMessage message, CancellationToken ct)
    {
        var session = result.Session;

        // Consent gate: no worker profile is written until the worker has consented.
        if (!session.ConsentGiven && !result.OptedOut)
            return;

        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.WaId == message.WaId, ct);
        if (worker is null)
        {
            if (!session.ConsentGiven) return; // opt-out before any consent → nothing to store
            worker = new Worker { WaId = message.WaId, CreatedUtc = DateTimeOffset.UtcNow };
            _db.Workers.Add(worker);
        }

        worker.DisplayName = session.DisplayName ?? worker.DisplayName;
        worker.PreferredLanguage = session.PreferredLanguage ?? worker.PreferredLanguage;
        worker.LocationText = session.LocationText ?? worker.LocationText;
        worker.UpdatedUtc = DateTimeOffset.UtcNow;

        if (session.ConsentGiven && !worker.ConsentGiven)
        {
            worker.ConsentGiven = true;
            worker.ConsentUtc = DateTimeOffset.UtcNow;
        }

        if (result.OptedOut)
            worker.IsActive = false;

        await _db.SaveChangesAsync(ct); // ensure worker.Id is available for skill linkage

        if (result.NewlyConfirmedSkill is { } pending)
            await AddWorkerSkillAsync(worker, pending, ct);
    }

    private async Task AddWorkerSkillAsync(Worker worker, PendingSkill pending, CancellationToken ct)
    {
        var canonical = pending.CanonicalName.Trim();
        var skill = await _db.Skills.FirstOrDefaultAsync(s => s.CanonicalName == canonical, ct);
        if (skill is null)
        {
            skill = new Skill { CanonicalName = canonical };
            _db.Skills.Add(skill);
            await _db.SaveChangesAsync(ct);
        }

        var exists = await _db.WorkerSkills
            .AnyAsync(ws => ws.WorkerId == worker.Id && ws.SkillId == skill.Id, ct);
        if (exists) return;

        _db.WorkerSkills.Add(new WorkerSkill
        {
            WorkerId = worker.Id,
            SkillId = skill.Id,
            SourceUtterance = pending.SourceUtterance, // research data — preserved
            ExtractionConfidence = pending.Confidence
        });
        await _db.SaveChangesAsync(ct);
    }
}
