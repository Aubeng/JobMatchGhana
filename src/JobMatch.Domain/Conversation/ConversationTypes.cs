using JobMatch.Domain.Enums;

namespace JobMatch.Domain.Conversation;

/// <summary>A normalised inbound WhatsApp message handed to the orchestrator.</summary>
public record InboundMessage(
    string WaId,
    string WaMessageId,
    string Text,
    string MessageType,
    string? ContactName,
    DateTimeOffset Timestamp);

/// <summary>An outbound message the FSM wants to send. Text-only for the pilot skeleton.</summary>
public record OutboundMessage(string Text);

/// <summary>
/// Mutable conversation session persisted in Redis (keyed by wa_id, 24h TTL).
/// The gateway owns all state; the NLP service is stateless.
/// </summary>
public class ConversationSession
{
    public string WaId { get; set; } = string.Empty;
    public ConversationState State { get; set; } = ConversationState.New;

    // Collected profile data during the flow.
    public string? DisplayName { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? LocationText { get; set; }
    public bool ConsentGiven { get; set; }
    public string? Availability { get; set; }

    /// <summary>Skills confirmed so far this session (canonical names).</summary>
    public List<string> ConfirmedSkills { get; set; } = new();

    /// <summary>A skill candidate awaiting the worker's yes/no confirmation.</summary>
    public PendingSkill? PendingSkill { get; set; }

    /// <summary>
    /// Further skills from the same utterance ("I do roofing and make furniture"), confirmed
    /// one at a time after <see cref="PendingSkill"/> so none is silently dropped.
    /// </summary>
    public List<PendingSkill> QueuedSkills { get; set; } = new();

    public int RepairCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A skill extracted from an utterance, held pending confirm/refine.</summary>
public class PendingSkill
{
    public string CanonicalName { get; set; } = string.Empty;
    public string SourceUtterance { get; set; } = string.Empty;
    public double Confidence { get; set; }
}

/// <summary>A skill candidate returned by the NLU service.</summary>
public record SkillCandidate(string CanonicalName, double Confidence);

/// <summary>A trade category recognised in an utterance, with a few of its skills to suggest.</summary>
public record TradeCandidate(string CategoryId, string Name, double Confidence, IReadOnlyList<string> ExampleSkills);

/// <summary>
/// Result of a <c>POST /nlu</c> call: intent + extracted skill candidates, plus the richer
/// Slice 2 analysis (trade, location, experience) and the telemetry the study logs per turn.
/// </summary>
public record NluResult(
    string Intent,
    double Confidence,
    IReadOnlyList<SkillCandidate> Skills)
{
    public TradeCandidate? Trade { get; init; }
    /// <summary>Resolved district (MMDA) for a mentioned location, e.g. "La Nkwantanang Madina".</summary>
    public string? LocationDistrict { get; init; }
    public double? ExperienceYears { get; init; }
    /// <summary>Intent confidence under the S&D 0.75 threshold: the dialogue should clarify.</summary>
    public bool BelowThreshold { get; init; }
    /// <summary>Raw entity list as JSON, logged verbatim for post-hoc evaluation.</summary>
    public string? EntitiesJson { get; init; }
    /// <summary>Which models produced this result; "stub" / "stub-fallback" when the service wasn't used.</summary>
    public string? ModelVersion { get; init; }
    public double? LatencyMs { get; init; }
}
