namespace JobMatch.Domain.Enums;

/// <summary>Direction of a logged WhatsApp message relative to the gateway.</summary>
public enum MessageDirection
{
    Inbound = 0,
    Outbound = 1
}

/// <summary>
/// Dialogue states of the S&D hierarchical state machine (Design §6.2.2, Figure 3):
/// S0 WELCOME -> S1 ELICIT_TRADE -> S2 ELICIT_SKILLS -> S3 ELICIT_EXPERIENCE ->
/// S4 ELICIT_LOCATION -> S5 CONFIRM_PROFILE -> S6 DELIVER_MATCH -> S7 FEEDBACK.
/// </summary>
/// <remarks>
/// Values are persisted (MessageLog.SessionState, Redis sessions). The Slice 1 states 1-6
/// (Consent, Language, ProfileName, ProfileLocation, SkillsElicitation, Availability) were
/// retired in DL-015; their numbers are not reused, and a session holding one is restarted.
/// </remarks>
public enum ConversationState
{
    /// <summary>No message handled yet.</summary>
    New = 0,
    /// <summary>Profile confirmed; idle between matches (after S5, or S7 once matching exists).</summary>
    Active = 7,
    /// <summary>Opted out ("stop" or consent refused); silent until the worker says "start".</summary>
    OptedOut = 8,
    /// <summary>S0: greeted, waiting for consent.</summary>
    Welcome = 10,
    /// <summary>S1: what kind of work?</summary>
    ElicitTrade = 11,
    /// <summary>S2: which skills within the trade?</summary>
    ElicitSkills = 12,
    /// <summary>S3: years of experience, then availability (a two-slot sub-machine).</summary>
    ElicitExperience = 13,
    /// <summary>S4: working area, resolved to a district.</summary>
    ElicitLocation = 14,
    /// <summary>S5: summary shown; confirm or go back to any slot.</summary>
    ConfirmProfile = 15,
    /// <summary>S6: top-3 matches delivered (Slice 3).</summary>
    DeliverMatch = 16,
    /// <summary>S7: rating the matches (Slice 3).</summary>
    Feedback = 17
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
    UpdateSkills = 3,      // "skills": jump to S2 to change skills
    ListMatches = 4,
    // State-driven intents.
    ConsentYes = 10,
    ConsentNo = 11,
    ProvideLanguage = 12,  // retired (DL-015): English-only, no language step
    ProvideName = 13,      // retired (DL-015): name is optional (NFR-09), not asked
    ProvideLocation = 14,
    ProvideSkill = 15,
    ConfirmSkill = 16,
    RefineSkill = 17,
    ProvideAvailability = 18,
    FinishSkills = 19,
    // Named a trade ("I'm a carpenter") but no specific skill: the FSM probes for skills.
    ProvideTrade = 20,
    // ---- S&D HSM (DL-015) ----
    ProvideExperience = 21,
    /// <summary>S5 summary accepted (FR-10).</summary>
    ConfirmProfile = 22,
    /// <summary>S5 "change something", or a correction applied from S5.</summary>
    ChangeProfile = 23,
    /// <summary>3rd consecutive failed turn in a state: offered human help (S&D 3-strike rule).</summary>
    HumanHandoff = 24,
    /// <summary>FR-16 "delete my data" confirmed.</summary>
    DeleteData = 25,
    /// <summary>A low-confidence value put back to the worker ("Do you work in X?").</summary>
    Clarify = 26,
    /// <summary>A pending action (delete/exit) declined.</summary>
    Cancel = 27,
    /// <summary>Free-text feedback while active (S7 proper arrives with matching).</summary>
    ProvideFeedback = 28,
    /// <summary>FR-15 "update my profile".</summary>
    UpdateProfile = 29,
    // Anything the FSM could not map -> triggers a repair prompt (logged).
    Repair = 99
}
