using JobMatch.Domain.Enums;

namespace JobMatch.Domain.Conversation;

/// <summary>
/// Result of a single FSM step. Pure data — the orchestrator is responsible for
/// persistence (session, worker, message log) and outbound sends.
/// </summary>
public class FsmStepResult
{
    public required ConversationSession Session { get; init; }
    public required IReadOnlyList<OutboundMessage> Replies { get; init; }
    public required Intent Intent { get; init; }
    public bool IsRepair { get; init; }
    public double? NluConfidence { get; init; }

    /// <summary>Set when the worker opted out this turn (mark inactive).</summary>
    public bool OptedOut { get; init; }

    /// <summary>A skill confirmed this turn to persist as a WorkerSkill (research data).</summary>
    public PendingSkill? NewlyConfirmedSkill { get; init; }
}

/// <summary>
/// Deterministic conversation state machine driving the skills-elicitation dialogue.
/// Completely side-effect free and I/O free: given a session, the raw utterance, and
/// (optionally) an NLU result, it returns the next session and the replies to send.
/// This purity is what makes FSM transitions unit-testable — a core research requirement.
/// </summary>
public static class ConversationFsm
{
    /// <summary>Minimum NLU confidence to treat an extracted skill as a candidate.</summary>
    public const double MinSkillConfidence = 0.45;

    /// <summary>
    /// Advance the conversation by one turn.
    /// </summary>
    /// <param name="session">Current session (mutated in place and returned).</param>
    /// <param name="rawText">The inbound message text.</param>
    /// <param name="nlu">
    /// NLU analysis of <paramref name="rawText"/>. Only required/consulted while in
    /// <see cref="ConversationState.SkillsElicitation"/> for a free-text utterance.
    /// </param>
    public static FsmStepResult Step(ConversationSession session, string rawText, NluResult? nlu = null)
    {
        var text = (rawText ?? string.Empty).Trim();
        session.UpdatedUtc = DateTimeOffset.UtcNow;

        // 1) Global commands are honoured in any state (subject to consent gates below).
        var global = GlobalCommands.TryParse(text);
        if (global is Intent.Help)
            return Reply(session, Intent.Help, HelpText(session));

        if (global is Intent.Stop)
        {
            session.State = ConversationState.OptedOut;
            session.PendingSkill = null;
            session.QueuedSkills.Clear();
            return new FsmStepResult
            {
                Session = session,
                Intent = Intent.Stop,
                OptedOut = true,
                Replies = new[] { new OutboundMessage(
                    "You have been opted out and will not receive more messages. " +
                    "Reply *start* any time to come back. Thank you 🙏") }
            };
        }

        if (global is Intent.UpdateSkills)
        {
            if (session.State == ConversationState.Active)
            {
                session.State = ConversationState.SkillsElicitation;
                session.PendingSkill = null;
                session.QueuedSkills.Clear();
                return Reply(session, Intent.UpdateSkills,
                    "Let's update your skills. Tell me a skill in your own words, or type *done* when finished.");
            }
            // Not set up yet — nudge without derailing the current step.
            return Reply(session, Intent.UpdateSkills,
                "Let's finish setting up your profile first.\n\n" + Prompt(session));
        }

        if (global is Intent.ListMatches)
        {
            // Matching engine arrives in Slice 3; acknowledge for now.
            return Reply(session, Intent.ListMatches,
                "Matching is coming soon — we'll message you as soon as a job fits your skills. ✅");
        }

        // 2) State-driven handling.
        return session.State switch
        {
            ConversationState.New => HandleNew(session),
            ConversationState.Consent => HandleConsent(session, text),
            ConversationState.Language => HandleLanguage(session, text),
            ConversationState.ProfileName => HandleName(session, text),
            ConversationState.ProfileLocation => HandleLocation(session, text),
            ConversationState.SkillsElicitation => HandleSkills(session, text, nlu),
            ConversationState.Availability => HandleAvailability(session, text),
            ConversationState.Active => HandleActive(session),
            ConversationState.OptedOut => HandleOptedOut(session, text),
            _ => Repair(session)
        };
    }

    /// <summary>Whether this turn (in this state) needs an NLU call before stepping.</summary>
    public static bool RequiresNlu(ConversationSession session, string rawText)
        => session.State == ConversationState.SkillsElicitation
           && GlobalCommands.TryParse(rawText) is null
           && !IsDone(rawText)
           && !(session.PendingSkill is not null && (IsYes(rawText) || IsNo(rawText)));

    /// <summary>
    /// Whether to run NLU on this turn at all. Beyond the turns the FSM needs it for, every
    /// free-text turn after consent is analysed so its predicted intent and entities are logged
    /// (the intent-accuracy metric is computed post hoc on real pilot messages). Never before
    /// consent: no profiling without consent (DL-010). Bare menu digits ("1", "2") are skipped:
    /// the FSM resolves them deterministically and the models are not trained on them.
    /// </summary>
    public static bool ShouldAnalyze(ConversationSession session, string rawText)
        => RequiresNlu(session, rawText)
           || (session.ConsentGiven
               && session.State is not ConversationState.OptedOut
               && !string.IsNullOrWhiteSpace(rawText)
               && !rawText.Trim().All(char.IsDigit));

    // ---- State handlers ---------------------------------------------------

    private static FsmStepResult HandleNew(ConversationSession session)
    {
        session.State = ConversationState.Consent;
        return Reply(session, Intent.Unknown,
            "Welcome to *JobMatch Ghana* 👋\n" +
            "We help you find work through a simple WhatsApp chat.\n\n" +
            "First, we need your consent to save your details and match you to jobs. " +
            "Your information is used only for job matching.\n\n" +
            "Reply:\n1. Yes, I agree\n2. No");
    }

    private static FsmStepResult HandleConsent(ConversationSession session, string text)
    {
        if (IsYes(text))
        {
            session.ConsentGiven = true;
            session.State = ConversationState.Language;
            return Reply(session, Intent.ConsentYes,
                "Thank you! ✅\n\nWhich language do you prefer?\n1. English\n(More languages coming soon.)");
        }
        if (IsNo(text))
        {
            session.State = ConversationState.OptedOut;
            return new FsmStepResult
            {
                Session = session,
                Intent = Intent.ConsentNo,
                OptedOut = true,
                Replies = new[] { new OutboundMessage(
                    "No problem. We won't save your details. " +
                    "Reply *start* any time if you change your mind. 🙏") }
            };
        }
        return Repair(session);
    }

    private static FsmStepResult HandleLanguage(ConversationSession session, string text)
    {
        var t = text.ToLowerInvariant();
        if (t is "1" or "english" or "en")
        {
            session.PreferredLanguage = "en";
            session.State = ConversationState.ProfileName;
            return Reply(session, Intent.ProvideLanguage, "Great! What is your name?");
        }
        return Repair(session);
    }

    private static FsmStepResult HandleName(ConversationSession session, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
            return Repair(session);

        session.DisplayName = text;
        session.State = ConversationState.ProfileLocation;
        return Reply(session, Intent.ProvideName,
            $"Nice to meet you, {text}! Where are you located? (For example: Accra, Kumasi, Tema)");
    }

    private static FsmStepResult HandleLocation(ConversationSession session, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120)
            return Repair(session);

        session.LocationText = text;
        session.State = ConversationState.SkillsElicitation;
        return Reply(session, Intent.ProvideLocation,
            "Now tell me about your work skills, in your own words.\n" +
            "For example: \"I can lay blocks and plaster walls\" or \"I cook and clean\".\n\n" +
            "Type *done* when you have finished.");
    }

    private static FsmStepResult HandleSkills(ConversationSession session, string text, NluResult? nlu)
    {
        // Finishing the loop.
        if (IsDone(text))
        {
            if (session.ConfirmedSkills.Count == 0)
                return ReplyKeepState(session, Intent.Repair,
                    "Please share at least one skill before we continue. " +
                    "Tell me one thing you can do.", isRepair: true);

            session.PendingSkill = null;
            session.QueuedSkills.Clear();
            session.State = ConversationState.Availability;
            return Reply(session, Intent.FinishSkills,
                "Thanks! When are you available to work?\n1. Full-time\n2. Part-time\n3. Weekends only");
        }

        // Confirming / refining a pending candidate.
        if (session.PendingSkill is not null)
        {
            if (IsYes(text))
            {
                var confirmed = session.PendingSkill;
                if (!session.ConfirmedSkills.Contains(confirmed.CanonicalName, StringComparer.OrdinalIgnoreCase))
                    session.ConfirmedSkills.Add(confirmed.CanonicalName);
                var next = NextQueued(session);
                return new FsmStepResult
                {
                    Session = session,
                    Intent = Intent.ConfirmSkill,
                    NewlyConfirmedSkill = confirmed,
                    Replies = new[] { new OutboundMessage(next is null
                        ? $"Got it ✅ (*{confirmed.CanonicalName}*). Tell me another skill, or type *done*."
                        : $"Got it ✅ (*{confirmed.CanonicalName}*).\n\nYou also said: *{next.CanonicalName}*. Is that right?\n1. Yes\n2. No") }
                };
            }
            if (IsNo(text))
            {
                var next = NextQueued(session);
                return Reply(session, Intent.RefineSkill, next is null
                    ? "No problem. Tell me the skill again in different words."
                    : $"No problem. Did you mean: *{next.CanonicalName}*?\n1. Yes\n2. No");
            }
            // Neither confirm nor a new utterance we understood -> repair.
            return ReplyKeepState(session, Intent.Repair,
                $"Please reply *1* (yes) or *2* (no) for *{session.PendingSkill.CanonicalName}*, " +
                "or type *done*.", isRepair: true);
        }

        // A fresh utterance: consult the NLU result for skill candidates (best first).
        var candidates = Candidates(session, nlu);
        var top = candidates.FirstOrDefault();
        if (top is null && nlu?.Trade is { } trade && trade.Confidence >= MinSkillConfidence)
        {
            // A trade but no specific skill ("I'm a carpenter"): probe for skills within the
            // trade (S&D S2 ELICIT_SKILLS) instead of treating it as a failed turn.
            var examples = trade.ExampleSkills.Count > 0
                ? $"For example: {string.Join(", ", trade.ExampleSkills.Select(s => s.ToLowerInvariant()))}.\n"
                : string.Empty;
            return new FsmStepResult
            {
                Session = session,
                Intent = Intent.ProvideTrade,
                NluConfidence = trade.Confidence,
                Replies = new[] { new OutboundMessage(
                    $"You work in *{trade.Name}* 👍\nWhat work can you do in this trade? {examples}" +
                    "Tell me in your own words, or type *done*.") }
            };
        }
        if (top is null)
        {
            return ReplyKeepState(session, Intent.Repair,
                "Sorry, I didn't catch a skill there. Try naming the work you do, " +
                "e.g. \"driving\", \"cooking\" or \"masonry\".", isRepair: true,
                nluConfidence: nlu?.Confidence);
        }

        var pending = candidates
            .Select(c => new PendingSkill { CanonicalName = c.CanonicalName, SourceUtterance = text, Confidence = c.Confidence })
            .ToList();
        session.PendingSkill = pending[0];
        session.QueuedSkills = pending.Skip(1).ToList();
        return new FsmStepResult
        {
            Session = session,
            Intent = Intent.ProvideSkill,
            NluConfidence = top.Confidence,
            Replies = new[] { new OutboundMessage(
                $"Did you mean: *{top.CanonicalName}*?\n1. Yes\n2. No\n(Or type *done* when finished.)") }
        };
    }

    private static FsmStepResult HandleAvailability(ConversationSession session, string text)
    {
        var t = text.ToLowerInvariant();
        string? availability = t switch
        {
            "1" or "full-time" or "full time" or "fulltime" => "Full-time",
            "2" or "part-time" or "part time" or "parttime" => "Part-time",
            "3" or "weekends" or "weekends only" or "weekend" => "Weekends only",
            _ => null
        };
        if (availability is null)
            return Repair(session);

        session.Availability = availability;
        session.State = ConversationState.Active;
        return Reply(session, Intent.ProvideAvailability,
            $"You're all set, {session.DisplayName ?? "friend"}! ✅\n" +
            "We'll message you when a job matches your skills.\n\n" +
            "Any time, you can reply:\n" +
            "• *skills* — update your skills\n" +
            "• *matches* — see your matches\n" +
            "• *help* — get help\n" +
            "• *stop* — opt out");
    }

    private static FsmStepResult HandleActive(ConversationSession session)
    {
        // Unrecognised free text while active -> gentle menu (counts as repair for metrics).
        return ReplyKeepState(session, Intent.Repair,
            "You're active ✅. You can reply *matches*, *skills*, or *help*.", isRepair: true);
    }

    private static FsmStepResult HandleOptedOut(ConversationSession session, string text)
    {
        if (text.Trim().ToLowerInvariant() is "start" or "restart" or "begin")
        {
            // Re-enrol from scratch.
            var waId = session.WaId;
            var fresh = new ConversationSession { WaId = waId, State = ConversationState.New };
            return HandleNew(fresh);
        }
        return new FsmStepResult
        {
            Session = session,
            Intent = Intent.Unknown,
            Replies = Array.Empty<OutboundMessage>() // silent while opted out
        };
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>Usable skill candidates, best first: confident enough, not yet confirmed, no repeats.</summary>
    private static List<SkillCandidate> Candidates(ConversationSession session, NluResult? nlu)
    {
        if (nlu is null) return new List<SkillCandidate>();
        return nlu.Skills
            .Where(c => c.Confidence >= MinSkillConfidence)
            .Where(c => !session.ConfirmedSkills.Contains(c.CanonicalName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Confidence)
            .DistinctBy(c => c.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Promote the next queued candidate (if any) to pending, and return it.</summary>
    private static PendingSkill? NextQueued(ConversationSession session)
    {
        session.PendingSkill = null;
        while (session.QueuedSkills.Count > 0)
        {
            var next = session.QueuedSkills[0];
            session.QueuedSkills.RemoveAt(0);
            if (session.ConfirmedSkills.Contains(next.CanonicalName, StringComparer.OrdinalIgnoreCase)) continue;
            session.PendingSkill = next;
            return next;
        }
        return null;
    }

    private static FsmStepResult Reply(ConversationSession session, Intent intent, string text) => new()
    {
        Session = session,
        Intent = intent,
        Replies = new[] { new OutboundMessage(text) }
    };

    private static FsmStepResult ReplyKeepState(ConversationSession session, Intent intent, string text,
        bool isRepair = false, double? nluConfidence = null)
    {
        if (isRepair) session.RepairCount++;
        return new FsmStepResult
        {
            Session = session,
            Intent = intent,
            IsRepair = isRepair,
            NluConfidence = nluConfidence,
            Replies = new[] { new OutboundMessage(text) }
        };
    }

    /// <summary>Generic repair: re-ask the current state's prompt and log the repair event.</summary>
    private static FsmStepResult Repair(ConversationSession session)
    {
        session.RepairCount++;
        return new FsmStepResult
        {
            Session = session,
            Intent = Intent.Repair,
            IsRepair = true,
            Replies = new[] { new OutboundMessage("Sorry, I didn't understand that.\n\n" + Prompt(session)) }
        };
    }

    /// <summary>The re-prompt for the current state (used by repair and nudges).</summary>
    private static string Prompt(ConversationSession session) => session.State switch
    {
        ConversationState.Consent =>
            "Do you consent to save your details for job matching?\n1. Yes, I agree\n2. No",
        ConversationState.Language => "Which language do you prefer?\n1. English",
        ConversationState.ProfileName => "What is your name?",
        ConversationState.ProfileLocation => "Where are you located? (For example: Accra)",
        ConversationState.SkillsElicitation =>
            "Tell me a work skill in your own words, or type *done*.",
        ConversationState.Availability =>
            "When are you available?\n1. Full-time\n2. Part-time\n3. Weekends only",
        _ => "Reply *help* to see what you can do."
    };

    private static string HelpText(ConversationSession session) =>
        "*JobMatch Ghana — Help*\n" +
        "I match you to jobs based on your skills.\n\n" +
        "You can reply:\n" +
        "• *skills* — add or update your skills\n" +
        "• *matches* — see your job matches\n" +
        "• *stop* — opt out at any time\n\n" +
        (session.State == ConversationState.Active
            ? "You're all set up. ✅"
            : "Let's continue:\n" + Prompt(session));

    private static bool IsYes(string t)
    {
        var s = t.Trim().ToLowerInvariant();
        return s is "1" or "yes" or "y" or "yeah" or "yep" or "ok" or "okay" or "sure" or "agree" or "i agree";
    }

    private static bool IsNo(string t)
    {
        var s = t.Trim().ToLowerInvariant();
        return s is "2" or "no" or "n" or "nope" or "nah";
    }

    private static bool IsDone(string t)
    {
        var s = t.Trim().ToLowerInvariant();
        return s is "done" or "finish" or "finished" or "that's all" or "thats all" or "no more";
    }
}
