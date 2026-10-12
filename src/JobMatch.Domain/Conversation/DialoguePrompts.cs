using System.Globalization;

namespace JobMatch.Domain.Conversation;

/// <summary>
/// Every message the dialogue sends, kept apart from the transition logic so the wording can
/// be reviewed for the CEFR A2–B1 reading level (NFR-06) without touching the state machine.
/// Each prompt keeps its options readable in plain text as well as in buttons or a list (NFR-07).
/// </summary>
public static class DialoguePrompts
{
    // WhatsApp list row titles hold 24 chars (descriptions 72; button titles 20, all kept short here).
    private const int RowTitleMax = 24;

    public const string Done = "done";

    public static readonly ReplyOption[] YesNo = { new("1", "Yes"), new("2", "No") };

    // S3 experience buttons: ids are not digits, so a typed "4" still means 4 years.
    public static readonly (string Id, string Title, double Years)[] ExperienceOptions =
    {
        ("exp_lt1", "Less than 1 year", 0.5),
        ("exp_1to5", "1 to 5 years", 3),
        ("exp_gt5", "More than 5 years", 6)
    };

    public static readonly (string Id, string Title, string[] Typed)[] AvailabilityOptions =
    {
        ("1", "Full-time", new[] { "full-time", "full time", "fulltime" }),
        ("2", "Part-time", new[] { "part-time", "part time", "parttime" }),
        ("3", "Weekends only", new[] { "weekends", "weekends only", "weekend" })
    };

    public static readonly (string Id, string Title)[] EditOptions =
    {
        ("1", "My work"), ("2", "My skills"), ("3", "Experience"), ("4", "When I can work"), ("5", "My area")
    };

    public static OutboundMessage Welcome() => new(
        "Welcome to *JobMatch Ghana* 👋\n" +
        "We help you find work through a short WhatsApp chat.\n\n" +
        "To match you to jobs, we need your *consent* to save your work details: your trade, " +
        "skills, experience, when you can work and your area. We use them only for job matching " +
        "and research. Type *delete* any time to remove them, or *stop* to leave.\n\n" +
        "Do you agree?\n1. Yes, I agree\n2. No")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, I agree"), new ReplyOption("2", "No") }
    };

    public static OutboundMessage AskConsent(string prefix = "") => new(
        prefix + "Do you agree that we save your work details for job matching?\n1. Yes, I agree\n2. No")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, I agree"), new ReplyOption("2", "No") }
    };

    public static OutboundMessage AskTrade(DialogueCatalog catalog, string prefix = "") => new(
        prefix + "What kind of work do you do?\n" +
        "Tap *Choose your work*, or tell me in your own words (e.g. \"I am a mason\").")
    {
        ListButton = "Choose your work",
        List = catalog.TradeMenu.Select((t, i) => Row((i + 1).ToString(CultureInfo.InvariantCulture), t.Name)).ToList()
    };

    public static OutboundMessage ConfirmTrade(string tradeName) => new(
        $"Do you work in *{tradeName}*?\n1. Yes\n2. No") { Buttons = YesNo };

    public static OutboundMessage AskSkills(TradeInfo trade, IReadOnlyCollection<string> captured, string prefix = "")
    {
        var remaining = trade.Skills.Select((s, i) => (Skill: s, Number: i + 1))
            .Where(x => !captured.Contains(x.Skill.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => Row(x.Number.ToString(CultureInfo.InvariantCulture), x.Skill.Name))
            .ToList();
        if (captured.Count == 0)
            return new OutboundMessage(prefix +
                $"Which work can you do in *{trade.Name}*?\n" +
                "Tap *Choose skills*, type numbers like 1,3 or tell me in your own words.")
            { ListButton = "Choose skills", List = remaining.Take(10).ToList() };

        // A "Done" row replaces one skill row if the list is full (WhatsApp allows 10 rows).
        var rows = new List<ReplyOption> { new(Done, "Done, that's all") };
        rows.AddRange(remaining.Take(9));
        return new OutboundMessage(prefix +
            $"Your skills: {string.Join(", ", captured)}.\n\n" +
            "Anything else? Choose more, describe it, or reply *done*.")
        { ListButton = "More skills or done", List = rows };
    }

    public static OutboundMessage ConfirmSkill(string skill, string prefix = "") => new(
        prefix + $"Did you mean: *{skill}*?\n1. Yes\n2. No") { Buttons = YesNo };

    public static OutboundMessage AskExperience(string prefix = "") => new(
        prefix + "How many years have you done this work?\nTap a button or type a number, e.g. 4.")
    {
        Buttons = ExperienceOptions.Select(o => new ReplyOption(o.Id, o.Title)).ToList()
    };

    public static OutboundMessage AskAvailability(string prefix = "") => new(
        prefix + "When can you work?\n1. Full-time\n2. Part-time\n3. Weekends only\n(Or tell me, e.g. \"mornings only\".)")
    {
        Buttons = AvailabilityOptions.Select(o => new ReplyOption(o.Id, o.Title)).ToList()
    };

    public static OutboundMessage AskLocation(DialogueCatalog catalog, string prefix = "") => new(
        prefix + "Which area do you mainly work in?\nTap *Choose your area*, or type your town or area (e.g. Madina).")
    {
        ListButton = "Choose your area",
        List = catalog.LocationMenu.Select((l, i) => Row((i + 1).ToString(CultureInfo.InvariantCulture), l.Label, l.District)).ToList()
    };

    public static OutboundMessage ConfirmLocation(string label, string district) => new(
        $"Do you mean *{label}* ({district})?\n1. Yes\n2. No") { Buttons = YesNo };

    public static OutboundMessage Summary(ConversationSession s, string prefix = "")
    {
        var area = s.LocationText is { } t && !string.Equals(t, s.LocationDistrict, StringComparison.OrdinalIgnoreCase)
            ? $"{t} ({s.LocationDistrict})" : s.LocationDistrict;
        return new OutboundMessage(prefix +
            "Please check your profile:\n" +
            $"• Work: {s.TradeName}\n" +
            $"• Skills: {string.Join(", ", s.Skills.Select(k => k.CanonicalName))}\n" +
            $"• Experience: {s.ExperienceText}\n" +
            $"• When: {s.Availability}\n" +
            $"• Area: {area}\n\n" +
            "Is this correct?\n1. Yes, that's right\n2. Change something")
        {
            Buttons = new[] { new ReplyOption("1", "Yes, that's right"), new ReplyOption("2", "Change something") }
        };
    }

    public static OutboundMessage ChooseEdit() => new(
        "What do you want to change?\n" + string.Join("\n", EditOptions.Select(o => $"{o.Id}. {o.Title}")))
    {
        ListButton = "Choose",
        List = EditOptions.Select(o => new ReplyOption(o.Id, o.Title)).ToList()
    };

    public static OutboundMessage ProfileSaved() => new(
        "Your profile is saved ✅\n" +
        "We will message you when we find jobs that match your skills.\n\n" +
        "Any time you can type:\n" +
        "• *matches* to see your matches\n• *update* to change your profile\n• *help* for help");

    public static OutboundMessage ActiveMenu() => new(
        "Your profile is saved ✅\nType *matches* to see your matches, *update* to change your " +
        "profile, *delete* to delete your data or *help* for help.");

    public static OutboundMessage Help() => new(
        "*JobMatch Ghana: Help*\nI match you to jobs that fit your skills.\n\n" +
        "Any time you can type:\n" +
        "• *update* to change your profile\n" +
        "• *matches* to see your job matches\n" +
        "• *delete* to delete your data\n" +
        "• *stop* to stop messages");

    public static OutboundMessage Handoff() => new(
        "This seems hard to do by chat, sorry 🙏\n" +
        "Someone from the JobMatch team will message you to help.\nYou can also try again:");

    public static OutboundMessage AskDelete() => new(
        "Do you want to *delete* your profile and all your messages with JobMatch? This cannot be undone.\n" +
        "1. Yes, delete\n2. No, keep")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, delete"), new ReplyOption("2", "No, keep") }
    };

    public static OutboundMessage Deleted() => new(
        "Your data has been deleted. 🗑️\nThank you for using JobMatch Ghana. " +
        "Send any message if you want to start again.");

    public static OutboundMessage AskExit() => new(
        "Do you want to stop getting messages from JobMatch?\n1. Yes, stop\n2. No, continue")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, stop"), new ReplyOption("2", "No, continue") }
    };

    public static OutboundMessage OptedOut() => new(
        "You will not get more messages from JobMatch. Your saved details stay unless you type *delete*.\n" +
        "Reply *start* any time to come back. Thank you 🙏");

    public static OutboundMessage ConsentRefused() => new(
        "No problem. We won't save your details.\nReply *start* any time if you change your mind. 🙏");

    public static OutboundMessage MatchesComingSoon() => new(
        "Job matching is coming soon. We'll message you when a job fits your skills. ✅");

    public static OutboundMessage FinishFirst() => new("Let's finish your profile first.");

    public static OutboundMessage Thanks() => new("Thank you for your feedback 🙏 It helps us improve JobMatch.");

    private static ReplyOption Row(string id, string name, string? description = null)
    {
        if (name.Length <= RowTitleMax)
            return new ReplyOption(id, name, description);
        // "Domestic, Cleaning & Security" -> "Domestic, Cleaning", full name in the description.
        var cut = name.LastIndexOfAny(new[] { '&', ',', '(' }, RowTitleMax);
        var title = (cut > 0 ? name[..cut] : name[..RowTitleMax]).TrimEnd(' ', ',', '&');
        return new ReplyOption(id, title, description ?? name);
    }
}
