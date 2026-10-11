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

/// <summary>Result of a <c>POST /nlu</c> call: intent + extracted skill candidates.</summary>
public record NluResult(
    string Intent,
    double Confidence,
    IReadOnlyList<SkillCandidate> Skills);
