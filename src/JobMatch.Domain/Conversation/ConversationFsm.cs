using System.Globalization;
using System.Text.RegularExpressions;
using JobMatch.Domain.Enums;
using P = JobMatch.Domain.Conversation.DialoguePrompts;

namespace JobMatch.Domain.Conversation;

/// <summary>
/// Result of a single FSM step. Pure data: the orchestrator is responsible for
/// persistence (session, worker, message log) and outbound sends.
/// </summary>
public class FsmStepResult
{
    public required ConversationSession Session { get; init; }
    public required IReadOnlyList<OutboundMessage> Replies { get; init; }
    public required Intent Intent { get; init; }
    public bool IsRepair { get; init; }
    public double? NluConfidence { get; init; }

    /// <summary>The worker opted out this turn (mark inactive).</summary>
    public bool OptedOut { get; init; }
    /// <summary>The worker confirmed the S5 summary: persist the whole profile (FR-10).</summary>
    public bool ProfileConfirmed { get; init; }
    /// <summary>FR-16: permanently delete this worker's profile, messages and session.</summary>
    public bool DeleteRequested { get; init; }
    /// <summary>3rd consecutive failure in a state: a person should follow up.</summary>
    public bool HandoffRequested { get; init; }
}

/// <summary>
/// The S&D hierarchical state machine (Design §6.2, Figure 3): WELCOME -> ELICIT_TRADE ->
/// ELICIT_SKILLS -> ELICIT_EXPERIENCE (years + availability) -> ELICIT_LOCATION ->
/// CONFIRM_PROFILE, with correction paths back to any slot, a 3-strike clarification loop
/// ending in human handoff, and global commands in every state.
///
/// Completely side-effect free and I/O free: given a session, the raw text, the NLU analysis
/// and the reference catalog, it returns the next session and the replies. This purity is
/// what makes every transition unit-testable, a core research requirement (DL-008).
///
/// Mixed initiative: the system leads slot by slot, but anything the worker volunteers early
/// ("I'm a mason in Madina, 5 years") fills its slot and that question is skipped.
/// </summary>
public static class ConversationFsm
{
    /// <summary>S&D L4: an NLU value at or above this is acted on directly.</summary>
    public const double ConfidenceThreshold = 0.75;
    /// <summary>Below this a candidate is ignored; between the two it is put back as a question.</summary>
    public const double MinCandidateConfidence = 0.45;
    /// <summary>S&D: "up to 3 attempts per slot before human handoff".</summary>
    public const int MaxStrikes = 3;

    private static readonly Regex Years = new(@"^\s*(\d{1,2}(?:\.\d)?)\s*(?:\+|years?|yrs?)?\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex NumberList = new(@"^\s*\d{1,2}(?:\s*(?:,|and|&|\s)\s*\d{1,2})*\s*$", RegexOptions.IgnoreCase);

    public static FsmStepResult Step(ConversationSession session, string rawText, NluResult? nlu, DialogueCatalog catalog)
    {
        var text = (rawText ?? string.Empty).Trim();
        session.UpdatedUtc = DateTimeOffset.UtcNow;

        // FR-01: any first message starts the conversation.
        if (session.State == ConversationState.New)
            return Start(session);

        // 0) A pending "delete my data?" is answered before anything else.
        if (session.Pending is { Kind: PendingKind.ConfirmDelete })
        {
            session.Pending = null;
            if (IsYes(text))
                return new FsmStepResult { Session = session, Intent = Intent.DeleteData, DeleteRequested = true, Replies = new[] { P.Deleted() } };
            if (IsNo(text))
                return Reply(session, Intent.Cancel, new OutboundMessage("OK, nothing was deleted. ✅"),
                    session.State == ConversationState.OptedOut ? null : Prompt(session, catalog));
        }

        var command = GlobalCommands.TryParse(text);
        // Opted out: silent, except "start" and "delete my data" (FR-16 must always work).
        if (session.State == ConversationState.OptedOut && command != Intent.DeleteData)
            return HandleOptedOut(session, text);

        // 1) Global commands, honoured in any state.
        switch (command)
        {
            case Intent.Help:
                return Reply(session, Intent.Help, P.Help(), Prompt(session, catalog));
            case Intent.Stop:
                return OptOut(session, P.OptedOut(), Intent.Stop);
            case Intent.DeleteData:
                session.Pending = new PendingQuestion { Kind = PendingKind.ConfirmDelete };
                return Reply(session, Intent.DeleteData, P.AskDelete());
            case Intent.UpdateProfile:
                if (!session.ProfileConfirmed)
                    return Reply(session, Intent.UpdateProfile, P.FinishFirst(), Prompt(session, catalog));
                session.State = ConversationState.ConfirmProfile;
                session.Pending = new PendingQuestion { Kind = PendingKind.ChooseEdit };
                return Reply(session, Intent.UpdateProfile, P.ChooseEdit());
            case Intent.UpdateSkills:
                if (!session.ConsentGiven || session.TradeId is null)
                    return Reply(session, Intent.UpdateSkills, P.FinishFirst(), Prompt(session, catalog));
                return EditSlot(session, catalog, slot: 2, Intent.UpdateSkills);
            case Intent.ListMatches:
                return Reply(session, Intent.ListMatches,
                    session.ProfileConfirmed ? P.MatchesComingSoon() : P.FinishFirst(),
                    session.ProfileConfirmed ? null : Prompt(session, catalog));
        }

        // 2) Answers to a pending yes/no or menu question.
        if (session.Pending is not null && HandlePending(session, text, nlu, catalog) is { } answered)
            return answered;

        // 3) NLU-recognised help / exit requests, in free text ("i no understand", "leave me alone").
        if (Confident(nlu, "HELP_REQUEST"))
            return Reply(session, Intent.Help, P.Help(), Prompt(session, catalog));
        if (Confident(nlu, "EXIT"))
        {
            // A pause ("later", "not now") shouldn't unsubscribe anyone: ask first.
            session.Pending = new PendingQuestion { Kind = PendingKind.ConfirmExit };
            return Reply(session, Intent.Clarify, P.AskExit());
        }

        // 4) The current dialogue state.
        return session.State switch
        {
            ConversationState.Welcome => HandleWelcome(session, text, catalog),
            ConversationState.ElicitTrade => HandleTrade(session, text, nlu, catalog),
            ConversationState.ElicitSkills => HandleSkills(session, text, nlu, catalog),
            ConversationState.ElicitExperience => HandleExperience(session, text, nlu, catalog),
            ConversationState.ElicitLocation => HandleLocation(session, text, nlu, catalog),
            ConversationState.ConfirmProfile => HandleConfirm(session, text, nlu, catalog),
            _ => HandleActive(session, nlu, catalog)   // Active, and S6/S7 until matching exists
        };
    }

    /// <summary>
    /// Whether to run NLU on this turn. Every free-text turn after consent is analysed, so its
    /// predicted intent and entities are logged for the post-hoc accuracy metrics. Not analysed:
    /// anything before consent (no profiling without consent, DL-010), tapped buttons/list rows,
    /// and bare menu numbers, which the FSM resolves itself and the models aren't trained on.
    /// </summary>
    public static bool ShouldAnalyze(ConversationSession session, string rawText, bool isInteractiveReply = false)
        => session.ConsentGiven
           && session.State is not (ConversationState.OptedOut or ConversationState.New)
           && !isInteractiveReply
           && !string.IsNullOrWhiteSpace(rawText)
           && !NumberList.IsMatch(rawText);

    // ---- S0 WELCOME -----------------------------------------------------------------------

    private static FsmStepResult Start(ConversationSession session)
    {
        session.State = ConversationState.Welcome;
        return Reply(session, Intent.Unknown, P.Welcome());
    }

    private static FsmStepResult HandleWelcome(ConversationSession session, string text, DialogueCatalog catalog)
    {
        if (IsYes(text))
        {
            session.ConsentGiven = true;
            return Advance(session, catalog, Intent.ConsentYes, "Thank you! ✅\n\n");
        }
        if (IsNo(text))
            return OptOut(session, P.ConsentRefused(), Intent.ConsentNo);
        return Fail(session, catalog, "Please reply *1* (Yes, I agree) or *2* (No).");
    }

    private static FsmStepResult HandleOptedOut(ConversationSession session, string text)
    {
        if (text.ToLowerInvariant() is "start" or "restart" or "begin")
        {
            var fresh = new ConversationSession { WaId = session.WaId };
            return Start(fresh);
        }
        return new FsmStepResult { Session = session, Intent = Intent.Unknown, Replies = Array.Empty<OutboundMessage>() };
    }

    // ---- S1 ELICIT_TRADE ------------------------------------------------------------------

    private static FsmStepResult HandleTrade(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        // A tapped/typed menu number, or the exact name of a trade.
        var picked = MenuPick(text, catalog.TradeMenu) ?? catalog.FindTrade(text);
        if (picked is not null)
        {
            SetTrade(session, picked);
            return Advance(session, catalog, Intent.ProvideTrade);
        }

        AbsorbExtras(session, nlu);

        // Skills named straight away ("i lay blocks and plaster") imply the trade.
        var gotSkills = nlu is not null && CaptureSkills(session, nlu, text, catalog, out _);
        var trade = nlu?.Trade;
        if (trade is null && gotSkills && catalog.FindSkill(session.Skills.Concat(session.QueuedSkills.Select(q =>
                new CapturedSkill { CanonicalName = q.CanonicalName })).First().CanonicalName) is { } bySkill)
            trade = new TradeCandidate(bySkill.Trade.Id, bySkill.Trade.Name, 1.0, Array.Empty<string>(), "skill");
        if (trade is not null)
        {
            if (trade.Confidence >= ConfidenceThreshold || (gotSkills && trade.Confidence >= MinCandidateConfidence))
            {
                SetTrade(session, catalog.TradeById(trade.CategoryId), trade);
                if (gotSkills)
                    session.State = ConversationState.ElicitSkills;
                return gotSkills
                    ? NextSkillTurn(session, catalog, Intent.ProvideSkill, trade.Confidence)
                    : Advance(session, catalog, Intent.ProvideTrade, nluConfidence: trade.Confidence);
            }
            if (trade.Confidence >= MinCandidateConfidence)
            {
                session.Pending = new PendingQuestion
                {
                    Kind = PendingKind.ConfirmTrade, Value = trade.CategoryId, Label = trade.Name, Confidence = trade.Confidence
                };
                return Reply(session, Intent.Clarify, trade.Confidence, P.ConfirmTrade(trade.Name));
            }
        }
        return Fail(session, catalog, "Sorry, I didn't catch your work.", nlu?.Confidence);
    }

    // ---- S2 ELICIT_SKILLS -----------------------------------------------------------------

    private static FsmStepResult HandleSkills(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        var trade = catalog.TradeById(session.TradeId!)!;

        // Confirming a low-confidence candidate.
        if (session.PendingSkill is { } pending)
        {
            if (IsYes(text))
            {
                AddSkill(session, pending.CanonicalName, pending.SourceUtterance, pending.Confidence);
                return NextSkillTurn(session, catalog, Intent.ConfirmSkill, null, $"Added *{pending.CanonicalName}* ✅\n\n");
            }
            if (IsNo(text))
                return NextSkillTurn(session, catalog, Intent.RefineSkill, null, "OK.\n\n");
            session.PendingSkill = null;
            session.QueuedSkills.Clear();   // a new answer replaces the unconfirmed ones
        }

        if (IsDone(text))
        {
            if (session.Skills.Count == 0)
                return Fail(session, catalog, "Please choose at least one skill first.");
            return Advance(session, catalog, Intent.FinishSkills);
        }

        // Numbers from the list: "3" or "1, 4 and 7".
        if (NumberList.IsMatch(text))
        {
            var numbers = Regex.Matches(text, @"\d{1,2}").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                .Where(n => n >= 1 && n <= trade.Skills.Count).Distinct().ToList();
            if (numbers.Count == 0)
                return Fail(session, catalog, $"Please choose numbers from 1 to {trade.Skills.Count}.");
            foreach (var n in numbers)
                AddSkill(session, trade.Skills[n - 1].Name, $"list:{n}", 1.0);
            return NextSkillTurn(session, catalog, Intent.ProvideSkill, 1.0);
        }

        if (catalog.FindSkill(text) is { } exact)
        {
            AddSkill(session, exact.Skill.Name, text, 1.0);
            return NextSkillTurn(session, catalog, Intent.ProvideSkill, 1.0);
        }

        AbsorbExtras(session, nlu);
        if (nlu is not null && CaptureSkills(session, nlu, text, catalog, out var top))
            return NextSkillTurn(session, catalog, Intent.ProvideSkill, top);

        // A (different) trade but no skills: switch the list to that trade.
        if (nlu?.Trade is { } t && t.Confidence >= ConfidenceThreshold && t.CategoryId != session.TradeId
            && catalog.TradeById(t.CategoryId) is { } other)
        {
            SetTrade(session, other, t);
            return Reply(session, Intent.ProvideTrade, t.Confidence, P.AskSkills(other, Names(session)));
        }
        return Fail(session, catalog, "Sorry, I didn't catch a skill.", nlu?.Confidence);
    }

    /// <summary>After skills change: confirm the next queued candidate, or offer more / done.</summary>
    private static FsmStepResult NextSkillTurn(ConversationSession session, DialogueCatalog catalog, Intent intent,
        double? confidence, string prefix = "")
    {
        session.Strikes = 0;
        session.PendingSkill = null;
        while (session.QueuedSkills.Count > 0)
        {
            var next = session.QueuedSkills[0];
            session.QueuedSkills.RemoveAt(0);
            if (Names(session).Contains(next.CanonicalName, StringComparer.OrdinalIgnoreCase)) continue;
            session.PendingSkill = next;
            return Reply(session, intent, confidence, P.ConfirmSkill(next.CanonicalName, prefix));
        }
        var trade = catalog.TradeById(session.TradeId!)!;
        return Reply(session, intent, confidence, P.AskSkills(trade, Names(session), prefix));
    }

    /// <summary>
    /// Adds confident NLU skills directly and queues the uncertain ones for a yes/no. Skills
    /// keep the utterance that produced them and their confidence (research data).
    /// </summary>
    private static bool CaptureSkills(ConversationSession session, NluResult nlu, string text, DialogueCatalog catalog, out double? top)
    {
        var candidates = nlu.Skills
            .Where(c => c.Confidence >= MinCandidateConfidence)
            .Where(c => !Names(session).Contains(c.CanonicalName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Confidence)
            .DistinctBy(c => c.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        top = candidates.FirstOrDefault()?.Confidence;
        foreach (var c in candidates)
        {
            if (c.Confidence >= ConfidenceThreshold)
                AddSkill(session, c.CanonicalName, text, c.Confidence);
            else
                session.QueuedSkills.Add(new PendingSkill { CanonicalName = c.CanonicalName, SourceUtterance = text, Confidence = c.Confidence });
        }
        return candidates.Count > 0;
    }

    // ---- S3 ELICIT_EXPERIENCE (years, then availability) ----------------------------------

    private static FsmStepResult HandleExperience(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        if (session.ExperienceYears is null)
        {
            var button = P.ExperienceOptions.FirstOrDefault(o =>
                text.Equals(o.Id, StringComparison.OrdinalIgnoreCase) || text.Equals(o.Title, StringComparison.OrdinalIgnoreCase));
            if (button.Id is not null)
                SetExperience(session, button.Years, button.Title);
            else if (Years.Match(text) is { Success: true } m)
                SetExperience(session, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), text);
            else if (nlu?.ExperienceYears is { } years)
                SetExperience(session, years, nlu.ExperienceText ?? text);
            else
                return Fail(session, catalog, "Please tap a button or type the number of years, e.g. 4.", nlu?.Confidence);

            AbsorbExtras(session, nlu);
            return Advance(session, catalog, Intent.ProvideExperience, "Got it ✅\n\n");
        }

        var lowered = text.ToLowerInvariant();
        var option = P.AvailabilityOptions.FirstOrDefault(o => lowered == o.Id || o.Typed.Contains(lowered)
                                                                || lowered == o.Title.ToLowerInvariant());
        if (option.Id is not null)
            session.Availability = option.Title;
        else if (Confident(nlu, "AVAILABILITY_STATE"))
            session.Availability = text.Length <= 60 ? text : text[..60];   // "mornings only", kept as said
        else
            return Fail(session, catalog, "Please choose 1, 2 or 3, or tell me when you can work.", nlu?.Confidence);

        AbsorbExtras(session, nlu);
        return Advance(session, catalog, Intent.ProvideAvailability);
    }

    // ---- S4 ELICIT_LOCATION ---------------------------------------------------------------

    private static FsmStepResult HandleLocation(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        var picked = MenuPick(text, catalog.LocationMenu) ?? catalog.FindLocation(text);
        if (picked is not null)
        {
            SetLocation(session, picked.District, picked.Region, picked.Label);
            return Advance(session, catalog, Intent.ProvideLocation);
        }
        if (nlu?.LocationDistrict is { } district)
        {
            var label = Capitalise(nlu.LocationText ?? district);
            if (nlu.LocationConfidence >= ConfidenceThreshold)
            {
                SetLocation(session, district, nlu.LocationRegion, label);
                return Advance(session, catalog, Intent.ProvideLocation, nluConfidence: nlu.LocationConfidence);
            }
            if (nlu.LocationConfidence >= MinCandidateConfidence)
            {
                session.Pending = new PendingQuestion
                {
                    Kind = PendingKind.ConfirmLocation, Value = district, Region = nlu.LocationRegion,
                    Label = label, Confidence = nlu.LocationConfidence
                };
                return Reply(session, Intent.Clarify, nlu.LocationConfidence, P.ConfirmLocation(label, district));
            }
        }
        // Without NLU (service down), still find a known place inside a sentence.
        if (catalog.ScanLocation(text) is { } found)
        {
            SetLocation(session, found.District, found.Region, found.Label);
            return Advance(session, catalog, Intent.ProvideLocation);
        }
        return Fail(session, catalog, "Sorry, I couldn't find that area.", nlu?.Confidence);
    }

    // ---- S5 CONFIRM_PROFILE ---------------------------------------------------------------

    private static FsmStepResult HandleConfirm(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        if (IsYes(text) || (Confident(nlu, "CONFIRMATION") && !HasSlotValues(nlu)))
        {
            session.ProfileConfirmed = true;
            Move(session, ConversationState.Active);   // S6 DELIVER_MATCH arrives with Slice 3
            return new FsmStepResult
            {
                Session = session, Intent = Intent.ConfirmProfile, ProfileConfirmed = true, Replies = new[] { P.ProfileSaved() }
            };
        }

        // A correction that carries the new value ("no, my area is Tema") is applied directly.
        if (nlu is not null && ApplyCorrection(session, nlu, text, catalog) is { } corrected)
            return corrected;

        if (IsNo(text) || text.Equals("change", StringComparison.OrdinalIgnoreCase)
            || text.Equals("change something", StringComparison.OrdinalIgnoreCase) || Confident(nlu, "CORRECTION"))
        {
            session.Pending = new PendingQuestion { Kind = PendingKind.ChooseEdit };
            return Reply(session, Intent.ChangeProfile, P.ChooseEdit());
        }
        return Fail(session, catalog, "Please tap *Yes, that's right* or *Change something*.", nlu?.Confidence);
    }

    private static FsmStepResult? ApplyCorrection(ConversationSession session, NluResult nlu, string text, DialogueCatalog catalog)
    {
        var changed = false;
        if (nlu.LocationDistrict is { } d && nlu.LocationConfidence >= ConfidenceThreshold && d != session.LocationDistrict)
        {
            SetLocation(session, d, nlu.LocationRegion, Capitalise(nlu.LocationText ?? d));
            changed = true;
        }
        if (nlu.ExperienceYears is { } y && y != session.ExperienceYears)
        {
            SetExperience(session, y, nlu.ExperienceText ?? text);
            changed = true;
        }
        if (nlu.Trade is { } t && t.Confidence >= ConfidenceThreshold && t.Via == "trade" && t.CategoryId != session.TradeId
            && catalog.TradeById(t.CategoryId) is { } trade)
        {
            // A new trade invalidates the skills: re-elicit them, then come back here.
            SetTrade(session, trade, t);
            Move(session, ConversationState.ElicitSkills);
            return Reply(session, Intent.ChangeProfile, t.Confidence, P.AskSkills(trade, Names(session), "Updated ✅\n\n"));
        }
        if (nlu.Skills.Any(s => s.Confidence >= ConfidenceThreshold))
        {
            // Skill changes go through the skills list so removals are explicit.
            return EditSlot(session, catalog, slot: 2, Intent.ChangeProfile);
        }
        return changed ? Reply(session, Intent.ChangeProfile, P.Summary(session, "Updated ✅\n\n")) : null;
    }

    /// <summary>Go back to one slot from S5 (Figure 3 correction path), clearing its value.</summary>
    private static FsmStepResult EditSlot(ConversationSession session, DialogueCatalog catalog, int slot, Intent intent)
    {
        session.Pending = null;
        switch (slot)
        {
            case 1: session.TradeId = null; session.TradeName = null; session.Skills.Clear(); break;
            case 2: session.Skills.Clear(); break;
            case 3: session.ExperienceYears = null; session.ExperienceText = null; break;
            case 4: session.Availability = null; break;
            case 5: session.LocationDistrict = null; session.LocationRegion = null; session.LocationText = null; break;
        }
        session.PendingSkill = null;
        session.QueuedSkills.Clear();
        Move(session, NextSlot(session));
        return Reply(session, intent, Prompt(session, catalog));
    }

    // ---- Active (profile saved) -----------------------------------------------------------

    private static FsmStepResult HandleActive(ConversationSession session, NluResult? nlu, DialogueCatalog catalog)
    {
        if (Confident(nlu, "FEEDBACK_PROVIDE"))
            return Reply(session, Intent.ProvideFeedback, nlu!.Confidence, P.Thanks());
        return Fail(session, catalog, null, nlu?.Confidence);
    }

    // ---- Pending questions ----------------------------------------------------------------

    private static FsmStepResult? HandlePending(ConversationSession session, string text, NluResult? nlu, DialogueCatalog catalog)
    {
        var q = session.Pending!;
        if (q.Kind == PendingKind.ChooseEdit)
        {
            var choice = P.EditOptions.FirstOrDefault(o => o.Id == text || o.Title.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (choice.Id is not null)
                return EditSlot(session, catalog, int.Parse(choice.Id, CultureInfo.InvariantCulture), Intent.ChangeProfile);
            session.Pending = null;
            return null;   // not a menu choice: handle it as a normal answer in S5
        }

        var yes = IsYes(text) || (Confident(nlu, "CONFIRMATION") && !HasSlotValues(nlu));
        var no = IsNo(text);
        session.Pending = null;
        switch (q.Kind)
        {
            case PendingKind.ConfirmExit when yes:
                return OptOut(session, P.OptedOut(), Intent.Stop);
            case PendingKind.ConfirmExit when no:
                return Reply(session, Intent.Cancel, new OutboundMessage("Great, let's continue. ✅"), Prompt(session, catalog));
            case PendingKind.ConfirmTrade when yes && catalog.TradeById(q.Value!) is { } trade:
                SetTrade(session, trade);
                return Advance(session, catalog, Intent.ProvideTrade, nluConfidence: q.Confidence);
            case PendingKind.ConfirmLocation when yes:
                SetLocation(session, q.Value!, q.Region, q.Label);
                return Advance(session, catalog, Intent.ProvideLocation, nluConfidence: q.Confidence);
            case PendingKind.ConfirmTrade or PendingKind.ConfirmLocation when no && !HasSlotValues(nlu):
                return Reply(session, Intent.Clarify, Prompt(session, catalog, "OK. "));
        }
        return null;   // anything else is a fresh answer for the current state
    }

    // ---- Slot helpers ---------------------------------------------------------------------

    private static ConversationState NextSlot(ConversationSession s) =>
        s.TradeId is null ? ConversationState.ElicitTrade
        : s.Skills.Count == 0 ? ConversationState.ElicitSkills
        : s.ExperienceYears is null || s.Availability is null ? ConversationState.ElicitExperience
        : s.LocationDistrict is null ? ConversationState.ElicitLocation
        : ConversationState.ConfirmProfile;

    /// <summary>Move to the next unfilled slot (or back to the summary) and ask for it.</summary>
    private static FsmStepResult Advance(ConversationSession session, DialogueCatalog catalog, Intent intent,
        string prefix = "", double? nluConfidence = null)
    {
        session.Strikes = 0;
        Move(session, NextSlot(session));
        return Reply(session, intent, nluConfidence, Prompt(session, catalog, prefix));
    }

    /// <summary>The question for the current state (also used to re-ask after help or a repair).</summary>
    private static OutboundMessage Prompt(ConversationSession session, DialogueCatalog catalog, string prefix = "") => session.State switch
    {
        ConversationState.Welcome => P.AskConsent(prefix),
        ConversationState.ElicitTrade => P.AskTrade(catalog, prefix),
        ConversationState.ElicitSkills when session.PendingSkill is { } p => P.ConfirmSkill(p.CanonicalName, prefix),
        ConversationState.ElicitSkills => P.AskSkills(catalog.TradeById(session.TradeId!)!, Names(session), prefix),
        ConversationState.ElicitExperience when session.ExperienceYears is null => P.AskExperience(prefix),
        ConversationState.ElicitExperience => P.AskAvailability(prefix),
        ConversationState.ElicitLocation => P.AskLocation(catalog, prefix),
        ConversationState.ConfirmProfile => P.Summary(session, prefix),
        _ => P.ActiveMenu()
    };

    /// <summary>Fill empty slots from anything else the worker mentioned (mixed initiative).</summary>
    private static void AbsorbExtras(ConversationSession session, NluResult? nlu)
    {
        if (nlu is null) return;
        if (session.ExperienceYears is null && nlu.ExperienceYears is { } y)
            SetExperience(session, y, nlu.ExperienceText ?? $"{y} years");
        if (session.LocationDistrict is null && nlu.LocationDistrict is { } d && nlu.LocationConfidence >= ConfidenceThreshold)
            SetLocation(session, d, nlu.LocationRegion, Capitalise(nlu.LocationText ?? d));
    }

    private static void SetTrade(ConversationSession session, TradeInfo? trade, TradeCandidate? fromNlu = null)
    {
        if (trade is null) return;
        if (session.TradeId is not null && session.TradeId != trade.Id)
        {
            session.Skills.Clear();
            session.PendingSkill = null;
            session.QueuedSkills.RemoveAll(q => trade.Skills.All(s => s.Name != q.CanonicalName));
        }
        session.TradeId = trade.Id;
        session.TradeName = fromNlu?.Name ?? trade.Name;
    }

    private static void AddSkill(ConversationSession session, string name, string source, double confidence)
    {
        if (!Names(session).Contains(name, StringComparer.OrdinalIgnoreCase))
            session.Skills.Add(new CapturedSkill { CanonicalName = name, SourceUtterance = source, Confidence = confidence });
    }

    private static void SetExperience(ConversationSession session, double years, string text)
    {
        session.ExperienceYears = years;
        session.ExperienceText = Years.IsMatch(text) ? $"{years} years" : text;
    }

    private static void SetLocation(ConversationSession session, string district, string? region, string? label)
    {
        session.LocationDistrict = district;
        session.LocationRegion = region;
        session.LocationText = label ?? district;
    }

    private static IReadOnlyCollection<string> Names(ConversationSession s) => s.Skills.Select(k => k.CanonicalName).ToList();

    private static T? MenuPick<T>(string text, IReadOnlyList<T> menu) where T : class
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= menu.Count
            ? menu[n - 1] : null;

    private static void Move(ConversationSession session, ConversationState state)
    {
        if (session.State != state) session.Strikes = 0;
        session.State = state;
    }

    // ---- Replies --------------------------------------------------------------------------

    private static FsmStepResult OptOut(ConversationSession session, OutboundMessage message, Intent intent)
    {
        session.State = ConversationState.OptedOut;
        session.Pending = null;
        session.PendingSkill = null;
        session.QueuedSkills.Clear();
        return new FsmStepResult { Session = session, Intent = intent, OptedOut = true, Replies = new[] { message } };
    }

    /// <summary>
    /// A failed turn: a targeted clarification (FR-08), and on the 3rd consecutive failure in
    /// the same state, a human-handoff offer (S&D 3-strike rule). Logged as a repair event.
    /// </summary>
    private static FsmStepResult Fail(ConversationSession session, DialogueCatalog catalog, string? why, double? nluConfidence = null)
    {
        session.RepairCount++;
        session.Strikes++;
        if (session.Strikes >= MaxStrikes)
        {
            session.Strikes = 0;
            return new FsmStepResult
            {
                Session = session, Intent = Intent.HumanHandoff, IsRepair = true, HandoffRequested = true,
                NluConfidence = nluConfidence, Replies = new[] { P.Handoff(), Prompt(session, catalog) }
            };
        }
        return new FsmStepResult
        {
            Session = session, Intent = Intent.Repair, IsRepair = true, NluConfidence = nluConfidence,
            Replies = new[] { Prompt(session, catalog, why is null ? "" : why + "\n\n") }
        };
    }

    private static FsmStepResult Reply(ConversationSession session, Intent intent, params OutboundMessage?[] messages)
        => Reply(session, intent, null, messages);

    private static FsmStepResult Reply(ConversationSession session, Intent intent, double? nluConfidence, params OutboundMessage?[] messages)
        => new()
        {
            Session = session, Intent = intent, NluConfidence = nluConfidence,
            Replies = messages.Where(m => m is not null).Select(m => m!).ToList()
        };

    // ---- Input classification ---------------------------------------------------------------

    private static bool Confident(NluResult? nlu, string intent)
        => nlu is not null && nlu.Intent == intent && nlu.Confidence >= ConfidenceThreshold;

    private static bool HasSlotValues(NluResult? nlu)
        => nlu is not null && (nlu.Skills.Count > 0 || nlu.LocationDistrict is not null || nlu.ExperienceYears is not null
                               || nlu.Trade is { Via: "trade" });

    private static bool IsYes(string t) => t.Trim().ToLowerInvariant() is
        "1" or "yes" or "y" or "yeah" or "yep" or "yes please" or "ok" or "okay" or "sure" or "agree" or "i agree"
        or "yes, i agree" or "correct" or "that's right" or "thats right" or "yes, that's right" or "yes, delete" or "yes, stop";

    private static bool IsNo(string t) => t.Trim().ToLowerInvariant() is
        "2" or "no" or "n" or "nope" or "nah" or "no, keep" or "no, continue";

    private static bool IsDone(string t) => t.Trim().ToLowerInvariant() is
        P.Done or "done, that's all" or "finish" or "finished" or "that's all" or "thats all" or "no more" or "nothing else";

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
