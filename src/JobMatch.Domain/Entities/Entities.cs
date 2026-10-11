using JobMatch.Domain.Enums;

namespace JobMatch.Domain.Entities;

/// <summary>An informal-sector worker enrolled via WhatsApp.</summary>
public class Worker
{
    public int Id { get; set; }
    /// <summary>WhatsApp id (phone in wa_id form). Unique.</summary>
    public string WaId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string? LocationText { get; set; }

    // Consent gate (research ethics condition): no profiling/matching before consent.
    public bool ConsentGiven { get; set; }
    public DateTimeOffset? ConsentUtc { get; set; }

    /// <summary>Set false on opt-out ("stop"); halts matching/notifications.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<WorkerSkill> Skills { get; set; } = new List<WorkerSkill>();
    public ICollection<Match> Matches { get; set; } = new List<Match>();
}

/// <summary>Canonical skill in the taxonomy. Embedding populated by the NLP service.</summary>
public class Skill
{
    public int Id { get; set; }
    public string CanonicalName { get; set; } = string.Empty;
    public string? Category { get; set; }
    /// <summary>float32[384] MiniLM embedding, stored as raw bytes. Null until embedded.</summary>
    public byte[]? Embedding { get; set; }

    public ICollection<WorkerSkill> WorkerSkills { get; set; } = new List<WorkerSkill>();
    public ICollection<JobPostingSkill> JobPostingSkills { get; set; } = new List<JobPostingSkill>();
}

/// <summary>
/// Link between a worker and a skill. <see cref="SourceUtterance"/> and
/// <see cref="ExtractionConfidence"/> are preserved research data — never drop them.
/// </summary>
public class WorkerSkill
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public Worker? Worker { get; set; }
    public int SkillId { get; set; }
    public Skill? Skill { get; set; }

    /// <summary>Raw free-text the worker typed that produced this skill (research data).</summary>
    public string? SourceUtterance { get; set; }
    /// <summary>NLU confidence 0..1 for the extraction (research data).</summary>
    public double ExtractionConfidence { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A job opportunity posted by an employer via the admin SPA.</summary>
public class JobPosting
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? LocationText { get; set; }
    public string EmployerContact { get; set; } = string.Empty;
    public byte[]? Embedding { get; set; }
    /// <summary>e.g. Draft / Open / Closed.</summary>
    public string Status { get; set; } = "Open";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<JobPostingSkill> Skills { get; set; } = new List<JobPostingSkill>();
    public ICollection<Match> Matches { get; set; } = new List<Match>();
}

/// <summary>Required/desired skill for a job posting.</summary>
public class JobPostingSkill
{
    public int Id { get; set; }
    public int JobPostingId { get; set; }
    public JobPosting? JobPosting { get; set; }
    public int SkillId { get; set; }
    public Skill? Skill { get; set; }
    public double Weight { get; set; } = 1.0;
}

/// <summary>A proposed match between a worker and a job posting, with explainable scoring.</summary>
public class Match
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public Worker? Worker { get; set; }
    public int JobPostingId { get; set; }
    public JobPosting? JobPosting { get; set; }

    /// <summary>Cosine-based semantic component.</summary>
    public double SemanticScore { get; set; }
    /// <summary>Combined score after weighting/thresholds.</summary>
    public double FinalScore { get; set; }
    /// <summary>Full explainable per-component breakdown (research data).</summary>
    public string? ScoreBreakdownJson { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.Proposed;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Immutable audit of every inbound/outbound message. Captures FSM state and detected
/// intent per turn — this is the quantitative dataset for the mixed-methods evaluation.
/// </summary>
public class MessageLog
{
    public long Id { get; set; }
    /// <summary>Meta message id. Unique for inbound (idempotency); may be null for outbound pre-send.</summary>
    public string? WaMessageId { get; set; }
    public string WaId { get; set; } = string.Empty;
    public MessageDirection Direction { get; set; }
    public string? Body { get; set; }
    /// <summary>WhatsApp message type (text, interactive, image, ...).</summary>
    public string? MessageType { get; set; }

    /// <summary>Detected intent for this turn (research metric).</summary>
    public Intent? IntentDetected { get; set; }
    /// <summary>FSM state at the time this message was processed (research metric).</summary>
    public ConversationState? SessionState { get; set; }
    /// <summary>NLU confidence for the turn, when applicable (research metric).</summary>
    public double? NluConfidence { get; set; }
    /// <summary>True when this turn produced a repair/fallback prompt (research metric).</summary>
    public bool IsRepair { get; set; }

    // ---- NLU telemetry (Slice 2). Null when the turn wasn't analysed (e.g. before consent). ----
    /// <summary>The NLU service's predicted intent (one of the 9 S&D intents), before the FSM acts.</summary>
    public string? NluIntent { get; set; }
    /// <summary>Confidence of <see cref="NluIntent"/> (softmax probability).</summary>
    public double? NluIntentConfidence { get; set; }
    /// <summary>Extracted entities with their resolutions, as returned by the service (JSON).</summary>
    public string? NluEntitiesJson { get; set; }
    /// <summary>Model versions that produced the analysis, or "stub" / "stub-fallback".</summary>
    public string? NluModelVersion { get; set; }
    /// <summary>NLU service inference time in milliseconds.</summary>
    public double? NluLatencyMs { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
