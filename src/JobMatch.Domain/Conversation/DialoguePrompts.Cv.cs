namespace JobMatch.Domain.Conversation;

/// <summary>The CV builder's messages (Slice 6, DL-019); same reading-level and length rules.</summary>
public static partial class DialoguePrompts
{
    public static readonly (string Id, string Title, string CvText, string[] Typed)[] EducationOptions =
    {
        ("edu_none", "No formal school", "No formal schooling", new[] { "none", "no school", "no formal school", "never" }),
        ("edu_primary", "Primary school", "Primary school", new[] { "primary", "p6" }),
        ("edu_jhs", "JHS", "Junior High School (JHS)", new[] { "jhs", "junior high", "junior high school", "jss", "middle school" }),
        ("edu_shs", "SHS", "Senior High School (SHS)", new[] { "shs", "senior high", "senior high school", "sss", "wassce" }),
        ("edu_tvet", "Technical / vocational", "Technical / vocational school", new[] { "tvet", "technical", "vocational", "technical school", "vocational school" }),
        ("edu_tertiary", "University / polytechnic", "University / polytechnic", new[] { "university", "tertiary", "polytechnic", "college", "degree", "diploma", "hnd" })
    };

    public static readonly string[] LanguageOptions =
        { "English", "Twi", "Ga", "Ewe", "Fante", "Dagbani", "Hausa", "Nzema", "Dagaare" };

    public static readonly (string Id, string Title)[] CvEditOptions =
    {
        ("1", "Name"), ("2", "Phone number"), ("3", "Past jobs"), ("4", "Training"), ("5", "School"),
        ("6", "Languages"), ("7", "About me line")
    };

    public static OutboundMessage OfferCv() => new(
        "📄 Would you like a free CV to show employers? It takes about 3 minutes.\n1. Yes, make my CV\n2. Not now")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, make my CV"), new ReplyOption("2", "Not now") }
    };

    public static OutboundMessage CvDeclined() => new("No problem 👍 Type *cv* any time if you want one.");

    public static OutboundMessage AskCvConsent(string prefix = "") => new(prefix +
        "To make your CV we need a few more details: your *name*, past work, training, school and languages. " +
        "We use them *only* to make your CV. Type *delete* any time to remove all your data.\n\n" +
        "Do you agree?\n1. Yes, continue\n2. No")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, continue"), new ReplyOption("2", "No") }
    };

    public static OutboundMessage AskCvName(string prefix = "") => new(prefix +
        "What name should be on your CV? (e.g. *Kwame Asante*)");

    public static OutboundMessage AskCvPhone(string phone, string prefix = "") => new(prefix +
        $"Put your WhatsApp number *{phone}* on your CV so employers can call you?\n1. Yes\n2. Another number\n3. No number")
    {
        Buttons = new[] { new ReplyOption("1", "Yes"), new ReplyOption("2", "Another number"), new ReplyOption("3", "No number") }
    };

    public static OutboundMessage AskCvOtherPhone(string prefix = "") => new(prefix +
        "Type the number to put on your CV, e.g. 024 123 4567.");

    public static OutboundMessage AskCvWork(IReadOnlyList<CvJob> jobs, string prefix = "")
    {
        if (jobs.Count == 0)
            return new OutboundMessage(prefix +
                "Tell me about a job you have done: the work, where, and when.\n" +
                "(e.g. *Mason for Asante Builders, Madina, 2019 to 2023*)\nOr tap *Skip*.")
            { Buttons = new[] { new ReplyOption("skip", "Skip") } };
        return new OutboundMessage(prefix +
            "Added ✅ Your jobs:\n" + string.Join("\n", jobs.Select(j => "• " + JobLine(j))) +
            "\n\nAnother job? Type it, or tap *Done*.")
        { Buttons = new[] { new ReplyOption(Done, "Done") } };
    }

    public static OutboundMessage AskCvTraining(IReadOnlyList<string> training, string prefix = "")
    {
        if (training.Count == 0)
            return new OutboundMessage(prefix +
                "Any training or certificate?\n(e.g. *NVTI Grade 1 Masonry*, *apprenticeship with a master mason*)\n" +
                "Type it, or tap *Skip*.")
            { Buttons = new[] { new ReplyOption("skip", "Skip") } };
        return new OutboundMessage(prefix +
            $"Your training: {string.Join("; ", training)}.\n\nAnother one? Type it, or tap *Done*.")
        { Buttons = new[] { new ReplyOption(Done, "Done") } };
    }

    public static OutboundMessage AskCvEducation(string prefix = "") => new(prefix +
        "What is the highest school you finished?\nTap *Choose school*.")
    {
        ListButton = "Choose school",
        List = EducationOptions.Select(o => new ReplyOption(o.Id, o.Title)).ToList()
    };

    public static OutboundMessage AskCvLanguages(IReadOnlyList<string> chosen, string prefix = "")
    {
        var remaining = LanguageOptions.Where(l => !chosen.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Select(l => new ReplyOption(l.ToLowerInvariant(), l)).ToList();
        if (chosen.Count == 0)
            return new OutboundMessage(prefix +
                "Which languages do you speak? Choose one at a time, or type them (e.g. *Twi, English*).")
            { ListButton = "Choose languages", List = remaining.Take(10).ToList() };
        var rows = new List<ReplyOption> { new(Done, "Done, that's all") };
        rows.AddRange(remaining.Take(9));
        return new OutboundMessage(prefix +
            $"Your languages: {string.Join(", ", chosen)}.\n\nAny other? Choose more, type it, or reply *done*.")
        { ListButton = "More or done", List = rows };
    }

    public static OutboundMessage AskCvAbout(string line, string prefix = "") => new(prefix +
        $"Here is a short line about you for the top of your CV:\n\n_{line}_\n\nKeep it?\n1. Yes, keep it\n2. Write my own")
    {
        Buttons = new[] { new ReplyOption("1", "Yes, keep it"), new ReplyOption("2", "Write my own") }
    };

    public static OutboundMessage AskCvOwnAbout(string prefix = "") => new(prefix +
        "Type one or two sentences about your work (up to 200 characters).");

    public static OutboundMessage CvSummary(ConversationSession s, string prefix = "")
    {
        var cv = s.Cv!;
        var lines = new List<string>
        {
            "Please check your CV:",
            $"• Name: {cv.FullName}",
            $"• Phone: {cv.Phone ?? "not shown"}",
            $"• Jobs: {(cv.Jobs.Count == 0 ? "none" : string.Join("; ", cv.Jobs.Select(JobLine)))}",
            $"• Training: {(cv.Training.Count == 0 ? "none" : string.Join("; ", cv.Training))}",
            $"• School: {cv.Education}",
            $"• Languages: {string.Join(", ", cv.Languages)}",
            $"• About: {cv.About}"
        };
        return new OutboundMessage(prefix + string.Join("\n", lines) +
            "\n\nYour work, skills, experience and areas come from your profile.\n\n" +
            "Is this correct?\n1. Yes, make my CV\n2. Change something")
        {
            Buttons = new[] { new ReplyOption("1", "Yes, make my CV"), new ReplyOption("2", "Change something") }
        };
    }

    public static OutboundMessage ChooseCvEdit() => new(
        "What do you want to change?\n" + string.Join("\n", CvEditOptions.Select(o => $"{o.Id}. {o.Title}")))
    {
        ListButton = "Choose",
        List = CvEditOptions.Select(o => new ReplyOption(o.Id, o.Title)).ToList()
    };

    public static OutboundMessage CvSent() => new(
        "Here is your CV 📄 Show it to employers or forward it to them.\nType *cv* any time to change it.");

    public static OutboundMessage CvSendFailed() => new(
        "Sorry, I couldn't send your CV just now 🙏 Type *cv* to try again.");

    public static string JobLine(CvJob job) => job.Period is null ? job.Description : $"{job.Description} ({job.Period})";
}
