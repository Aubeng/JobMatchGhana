namespace JobMatch.Domain.Enums;

/// <summary>Direction of a logged WhatsApp message relative to the gateway.</summary>
public enum MessageDirection
{
    Inbound = 0,
    Outbound = 1
}

/// <summary>
/// Deterministic conversation states for the skills-elicitation dialogue.
/// Progression: NEW -> CONSENT -> LANGUAGE -> PROFILE -> SKILLS_ELICITATION -> AVAILABILITY -> ACTIVE.
/// </summary>
public enum ConversationState
{
    New = 0,
    Consent = 1,
    Language = 2,
    ProfileName = 3,
    ProfileLocation = 4,
    SkillsElicitation = 5,
    Availability = 6,
    Active = 7,
    // Terminal opt-out state; no further processing until the worker re-engages.
    OptedOut = 8
}

/// <summary>Lifecycle of a proposed worker/job match.</summary>
public enum MatchStatus
{
    Proposed = 0,
    Notified = 1,
    Interested = 2,
    Declined = 3,
    Connected = 4
}

/// <summary>
/// Detected intent for a single inbound turn. Persisted on <c>MessageLog.IntentDetected</c>
/// and used as a research metric (esp. <see cref="Repair"/> / <see cref="Unknown"/>).
/// </summary>
public enum Intent
{
    Unknown = 0,
    // Global commands (recognised in any state).
    Help = 1,
    Stop = 2,
    UpdateSkills = 3,
    ListMatches = 4,
    // State-driven intents.
    ConsentYes = 10,
    ConsentNo = 11,
    ProvideLanguage = 12,
    ProvideName = 13,
    ProvideLocation = 14,
    ProvideSkill = 15,
    ConfirmSkill = 16,
    RefineSkill = 17,
    ProvideAvailability = 18,
    FinishSkills = 19,
    // Named a trade ("I'm a carpenter") but no specific skill: the FSM probes for skills.
    ProvideTrade = 20,
    // Anything the FSM could not map -> triggers a repair prompt (logged).
    Repair = 99
}
