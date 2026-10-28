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

/// <summary>A tappable option: a Quick Reply button (title &lt;= 20 chars) or a list row (&lt;= 24).</summary>
/// <remarks>
/// <see cref="Id"/> is what comes back when it is tapped, and it is the same text the user could
/// type ("1", "done"), so typed and tapped replies take one code path. NFR-07.
/// </remarks>
public record ReplyOption(string Id, string Title, string? Description = null);

/// <summary>
/// An outbound message. Plain text, or interactive with up to 3 <see cref="Buttons"/> or a
/// <see cref="List"/> of up to 10 rows (WhatsApp limits). Interactive messages always keep the
/// options readable in <see cref="Text"/> too, so a typed number works on any client.
/// </summary>
public record OutboundMessage(string Text)
{
    public IReadOnlyList<ReplyOption>? Buttons { get; init; }
    public IReadOnlyList<ReplyOption>? List { get; init; }
    /// <summary>Label of the button that opens <see cref="List"/> (&lt;= 20 chars).</summary>
    public string? ListButton { get; init; }

    /// <summary>NFR-05: every outbound message is at most this many characters.</summary>
    public const int MaxLength = 500;
}

/// <summary>A skill the worker has given, kept with its provenance (research data).</summary>
public class CapturedSkill
{
    public string CanonicalName { get; set; } = string.Empty;
    /// <summary>What the worker typed, or "list:3" when picked from the S2 list.</summary>
    public string SourceUtterance { get; set; } = string.Empty;
    /// <summary>NLU confidence; 1.0 for a list pick.</summary>
    public double Confidence { get; set; }
}

/// <summary>One job as delivered in S6 (FR-12: type, location, pay, how to proceed; FR-13: why).</summary>
public record MatchCard(int MatchId, int Rank, string Title, string Area, string? Pay, string? Availability,
    string Why, string? Employer = null, bool OutsideAreas = false);

/// <summary>A match shown to the worker, kept on the session so S7 answers refer to it.</summary>
public class DeliveredMatch
{
    public int MatchId { get; set; }
    public int Rank { get; set; }
    public string Title { get; set; } = string.Empty;
}

/// <summary>What a pending yes/no or menu question is about.</summary>
public enum PendingKind
{
    ConfirmTrade = 1,      // low-confidence trade: "Do you work in X?"
    ConfirmLocation = 2,   // low-confidence area: "Do you mean X?"
    ConfirmExit = 3,       // NLU EXIT intent: "Do you want to stop?" (a pause shouldn't unsubscribe)
    ConfirmDelete = 4,     // FR-16 "delete my data"
    ChooseEdit = 5,        // S5 "Change something": which slot?
    OfferCv = 6,           // "Would you like a free CV?" (Slice 6)
    ChooseCvEdit = 7       // C8 "Change something": which part of the CV?
}

/// <summary>A question the next reply answers; cleared once answered.</summary>
public class PendingQuestion
{
    public PendingKind Kind { get; set; }
    /// <summary>The proposed value: trade id, or district for a location.</summary>
    public string? Value { get; set; }
    public string? Label { get; set; }
    public string? Region { get; set; }
    public double Confidence { get; set; }
}

/// <summary>
/// Mutable conversation session persisted in Redis (keyed by wa_id, 72h TTL per FR-09).
/// The gateway owns all state; the NLP service is stateless. The slots mirror the S&D
/// skills profile: trade (S1), skills (S2), experience + availability (S3), location (S4).
/// </summary>
public class ConversationSession
{
    public string WaId { get; set; } = string.Empty;
    public ConversationState State { get; set; } = ConversationState.New;
    public bool ConsentGiven { get; set; }

    // ---- Profile slots ----
    public string? TradeId { get; set; }
    public string? TradeName { get; set; }
    public List<CapturedSkill> Skills { get; set; } = new();
    public double? ExperienceYears { get; set; }
    public string? ExperienceText { get; set; }
    public string? Availability { get; set; }
    /// <summary>
    /// S4: every area the worker can work in, main area first, at most
    /// <see cref="ConversationFsm.MaxAreas"/> (DL-018). Matching searches all of them.
    /// </summary>
    public List<AreaChoice> Areas { get; set; } = new();
    /// <summary>S4: the worker said "done" adding areas (or reached the maximum).</summary>
    public bool AreasDone { get; set; }
    /// <summary>The main area's district (= Areas[0]); kept in step with <see cref="Areas"/>.</summary>
    public string? LocationDistrict { get; set; }
    public string? LocationRegion { get; set; }
    /// <summary>The main area as the worker named it ("Madina"); the district is what matching uses.</summary>
    public string? LocationText { get; set; }

    /// <summary>
    /// True once the worker has confirmed the S5 summary (FR-10). A changed slot is re-asked
    /// and, because every other slot is still filled, the flow returns to the summary.
    /// </summary>
    public bool ProfileConfirmed { get; set; }

    // ---- Clarification ----
    /// <summary>A low-confidence skill awaiting yes/no.</summary>
    public PendingSkill? PendingSkill { get; set; }
    /// <summary>
    /// Further skills from the same utterance ("I do roofing and make furniture"), confirmed
    /// one at a time after <see cref="PendingSkill"/> so none is silently dropped.
    /// </summary>
    public List<PendingSkill> QueuedSkills { get; set; } = new();
    public PendingQuestion? Pending { get; set; }

    // ---- S6/S7 ----
    /// <summary>The matches last delivered (S6); ratings, comments and "interested" apply to these.</summary>
    public List<DeliveredMatch> DeliveredMatches { get; set; } = new();
    /// <summary>S7: rated; now waiting for an optional comment.</summary>
    public bool AwaitingComment { get; set; }
    // ---- Optional CV builder (Slice 6, DL-019) ----
    /// <summary>The CV being built or last made; null until the worker first asks for one.</summary>
    public CvDraft? Cv { get; set; }
    /// <summary>The CV offer was shown once; it is not repeated automatically ("cv" still works).</summary>
    public bool CvOffered { get; set; }

    /// <summary>Consecutive failed turns in the current state; the 3rd triggers human handoff.</summary>
    public int Strikes { get; set; }

    public int RepairCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    // ---- Retired Slice 1 fields: still on the model so older Redis sessions deserialise. ----
    public string? DisplayName { get; set; }
    public string? PreferredLanguage { get; set; } = "en";
}

/// <summary>An area the worker can work in: the district matching uses, and the name they gave.</summary>
public class AreaChoice
{
    public string District { get; set; } = string.Empty;
    public string? Region { get; set; }
    /// <summary>The area as named or picked ("Madina").</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// The CV builder's answers (C1-C7). Held in the session while the worker answers, saved to the
/// database when they confirm the C8 summary, and reloaded from there when they type "cv" later.
/// </summary>
public class CvDraft
{
    public DateTimeOffset? ConsentUtc { get; set; }
    public string? FullName { get; set; }
    /// <summary>C2 answered. <see cref="Phone"/> null afterwards means "no number on the CV".</summary>
    public bool PhoneAnswered { get; set; }
    public string? Phone { get; set; }
    /// <summary>C2: "another number" chosen; the next message should be that number.</summary>
    public bool AwaitingPhone { get; set; }
    public List<CvJob> Jobs { get; set; } = new();
    public bool JobsDone { get; set; }
    public List<string> Training { get; set; } = new();
    public bool TrainingDone { get; set; }
    public string? Education { get; set; }
    public List<string> Languages { get; set; } = new();
    public bool LanguagesDone { get; set; }
    public string? About { get; set; }
    /// <summary>C7: "write my own" chosen; the next message is the line.</summary>
    public bool AwaitingAbout { get; set; }
    /// <summary>The worker has confirmed C8 at least once (a CV exists).</summary>
    public bool Completed { get; set; }
}

/// <summary>A past job as the worker described it; <see cref="Period"/> is pulled out for layout.</summary>
public class CvJob
{
    /// <summary>"Mason for Asante Builders at Madina".</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>"2019 – 2023", "since 2020", or null.</summary>
    public string? Period { get; set; }
    /// <summary>Exactly what was typed (research data, as for skills).</summary>
    public string SourceUtterance { get; set; } = string.Empty;
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
/// <remarks><see cref="Via"/> is "trade" when the worker named it, "skill" when inferred from a skill.</remarks>
public record TradeCandidate(string CategoryId, string Name, double Confidence, IReadOnlyList<string> ExampleSkills,
    string Via = "trade");

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
    public string? LocationRegion { get; init; }
    /// <summary>The place as written ("madina").</summary>
    public string? LocationText { get; init; }
    public double LocationConfidence { get; init; }
    public double? ExperienceYears { get; init; }
    /// <summary>The duration as written ("6 yrs", "since 2018").</summary>
    public string? ExperienceText { get; init; }
    /// <summary>Intent confidence under the S&D 0.75 threshold: the dialogue should clarify.</summary>
    public bool BelowThreshold { get; init; }
    /// <summary>Raw entity list as JSON, logged verbatim for post-hoc evaluation.</summary>
    public string? EntitiesJson { get; init; }
    /// <summary>Which models produced this result; "stub" / "stub-fallback" when the service wasn't used.</summary>
    public string? ModelVersion { get; init; }
    public double? LatencyMs { get; init; }
}
