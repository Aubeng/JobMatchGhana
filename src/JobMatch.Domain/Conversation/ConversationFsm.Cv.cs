using System.Globalization;
using System.Text.RegularExpressions;
using JobMatch.Domain.Enums;
using P = JobMatch.Domain.Conversation.DialoguePrompts;

namespace JobMatch.Domain.Conversation;

/// <summary>
/// The optional CV builder (Slice 6, DL-019): a sub-machine entered from Active with "cv" or
/// the one-time offer, returning to Active when the CV is sent. C0 consent -> C1 name ->
/// C2 phone -> C3 past jobs -> C4 training -> C5 school -> C6 languages -> C7 about line ->
/// C8 review. Like the profile, every answer can be changed from the C8 summary, and global
/// commands keep working. Trade, skills, experience, availability and areas come from the
/// confirmed profile and are not asked again.
/// </summary>
public static partial class ConversationFsm
{
    public const int MaxCvJobs = 3;
    public const int MaxCvTraining = 3;
    public const int MaxCvLanguages = 6;

    private static readonly Regex YearRange = new(
        @"(?:\bfrom\s+)?\b((?:19|20)\d{2})\s*(?:-|–|to|till|until)\s*((?:19|20)\d{2}|now|date|present|today)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex Since = new(@"\bsince\s+((?:19|20)\d{2})\b", RegexOptions.IgnoreCase);
    private static readonly Regex ForYears = new(@"\bfor\s+(\d{1,2})\s*(?:years?|yrs?)\b", RegexOptions.IgnoreCase);
    private static readonly Regex PersonName = new(@"^[\p{L}][\p{L} .'-]{1,59}$");
    private static readonly Regex ListSeparators = new(@"\s*(?:,|/|&|\band\b)\s*", RegexOptions.IgnoreCase);

    public static bool IsCvState(ConversationState state)
        => state is >= ConversationState.CvConsent and <= ConversationState.CvReview;

    // ---- Entry --------------------------------------------------------------------------------

    /// <summary>"cv", or yes to the offer: start (or resume) the CV; an existing CV goes to its summary.</summary>
    private static FsmStepResult StartCv(ConversationSession session, DialogueCatalog catalog)
    {
        session.Pending = null;
        if (!session.ProfileConfirmed)
            return Reply(session, Intent.CvRequest, P.FinishFirst(), Prompt(session, catalog));
        session.CvOffered = true;
        session.Cv ??= new CvDraft();
        if (session.Cv.Completed)
        {
            Move(session, ConversationState.CvReview);
            return Reply(session, Intent.CvRequest, P.CvSummary(session, "Here is your CV 📄\n\n"));
        }
        Move(session, NextCvState(session.Cv));
        return Reply(session, Intent.CvRequest, CvPrompt(session));
    }

    /// <summary>The one-time offer, after the first matches are rated (or none were found).</summary>
    private static OutboundMessage? OfferCvOnce(ConversationSession session)
    {
        if (session.CvOffered || !session.ProfileConfirmed || session.Cv?.Completed == true) return null;
        session.CvOffered = true;
        session.Pending = new PendingQuestion { Kind = PendingKind.OfferCv };
        return P.OfferCv();
    }

    // ---- C0-C8 --------------------------------------------------------------------------------

    private static FsmStepResult HandleCv(ConversationSession session, string text, DialogueCatalog catalog)
    {
        var cv = session.Cv ??= new CvDraft();
        var lowered = text.Trim().ToLowerInvariant();
        switch (session.State)
        {
            case ConversationState.CvConsent:
                if (IsYes(text))
                {
                    cv.ConsentUtc = DateTimeOffset.UtcNow;
                    return CvAdvance(session, Intent.CvConsentYes, "Thank you! ✅\n\n");
                }
                if (IsNo(text))
                {
                    if (!cv.Completed) session.Cv = null;   // nothing kept without consent
                    Move(session, ConversationState.Active);
                    return Reply(session, Intent.CvDeclined, P.CvDeclined());
                }
                return Fail(session, catalog, "Please reply *1* (Yes, continue) or *2* (No).");

            case ConversationState.CvName:
                if (IsYes(text) || IsNo(text) || !PersonName.IsMatch(text) || text.Count(char.IsLetter) < 2)
                    return Fail(session, catalog, "Please type your name, e.g. Kwame Asante.");
                cv.FullName = TidyName(text);
                return CvAdvance(session, Intent.CvAnswer);

            case ConversationState.CvPhone:
                if (lowered is "3" or "no number" or "none" || (!cv.AwaitingPhone && lowered is "no"))
                {
                    cv.Phone = null;
                    cv.PhoneAnswered = true;
                    cv.AwaitingPhone = false;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                if (!cv.AwaitingPhone && (lowered is "1" or "yes" or "ok" or "okay"))
                {
                    cv.Phone = FormatPhone(session.WaId);
                    cv.PhoneAnswered = true;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                if (!cv.AwaitingPhone && lowered is "2" or "another number" or "other number")
                {
                    cv.AwaitingPhone = true;
                    return Reply(session, Intent.CvAnswer, P.AskCvOtherPhone());
                }
                if (ParsePhone(text) is { } typed)
                {
                    cv.Phone = typed;
                    cv.PhoneAnswered = true;
                    cv.AwaitingPhone = false;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                return Fail(session, catalog, cv.AwaitingPhone
                    ? "Please type a Ghana number, e.g. 024 123 4567, or *3* for no number."
                    : "Please tap a button.");

            case ConversationState.CvWork:
                if (IsCvDone(text))
                {
                    cv.JobsDone = true;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                if (!IsFreeAnswer(text, maxLength: 150))
                    return Fail(session, catalog, "Please describe the job in a few words, or tap *Skip*.");
                var job = ParseJob(text);
                job.Description = TidyLine(job.Description, catalog);
                cv.Jobs.Add(job);
                if (cv.Jobs.Count >= MaxCvJobs)
                {
                    cv.JobsDone = true;
                    return CvAdvance(session, Intent.CvAnswer, "Got it ✅\n\n");
                }
                return Reply(session, Intent.CvAnswer, P.AskCvWork(cv.Jobs));

            case ConversationState.CvTraining:
                if (IsCvDone(text))
                {
                    cv.TrainingDone = true;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                if (!IsFreeAnswer(text, maxLength: 100))
                    return Fail(session, catalog, "Please type the training or certificate, or tap *Skip*.");
                cv.Training.Add(TidyLine(text.Trim().TrimEnd('.'), catalog));
                if (cv.Training.Count >= MaxCvTraining)
                {
                    cv.TrainingDone = true;
                    return CvAdvance(session, Intent.CvAnswer, "Got it ✅\n\n");
                }
                return Reply(session, Intent.CvAnswer, P.AskCvTraining(cv.Training));

            case ConversationState.CvEducation:
                var school = P.EducationOptions.FirstOrDefault(o => lowered == o.Id || lowered == o.Title.ToLowerInvariant()
                                                                    || o.Typed.Contains(lowered));
                if (school.Id is null)
                    return Fail(session, catalog, "Please choose from the list.");
                cv.Education = school.CvText;
                return CvAdvance(session, Intent.CvAnswer);

            case ConversationState.CvLanguages:
                return HandleLanguages(session, cv, text, catalog);

            case ConversationState.CvAbout:
                if (!cv.AwaitingAbout && (IsYes(text) || lowered is "keep" or "keep it" or "yes, keep it"))
                {
                    cv.About = AboutLine(session);
                    return CvAdvance(session, Intent.CvAnswer);
                }
                if (!cv.AwaitingAbout && lowered is "2" or "write my own" or "my own")
                {
                    cv.AwaitingAbout = true;
                    return Reply(session, Intent.CvAnswer, P.AskCvOwnAbout());
                }
                if (IsFreeAnswer(text, maxLength: 200) && text.Trim().Length >= 15)
                {
                    cv.About = Capitalise(text.Trim());
                    cv.AwaitingAbout = false;
                    return CvAdvance(session, Intent.CvAnswer);
                }
                return Fail(session, catalog, cv.AwaitingAbout
                    ? "Please type one or two sentences (15 to 200 characters)."
                    : "Please tap a button.");

            default:   // C8 review
                if (IsYes(text))
                {
                    cv.Completed = true;
                    Move(session, ConversationState.Active);
                    return new FsmStepResult
                    {
                        Session = session, Intent = Intent.CvCompleted, SendCv = true, Replies = new[] { P.CvSent() }
                    };
                }
                if (IsNo(text) || lowered is "change" or "change something")
                {
                    session.Pending = new PendingQuestion { Kind = PendingKind.ChooseCvEdit };
                    return Reply(session, Intent.CvAnswer, P.ChooseCvEdit());
                }
                return Fail(session, catalog, "Please tap *Yes, make my CV* or *Change something*.");
        }
    }

    private static FsmStepResult HandleLanguages(ConversationSession session, CvDraft cv, string text, DialogueCatalog catalog)
    {
        if (IsCvDone(text))
        {
            if (cv.Languages.Count == 0)
                return Fail(session, catalog, "Please choose at least one language.");
            cv.LanguagesDone = true;
            return CvAdvance(session, Intent.CvAnswer);
        }
        var added = false;
        foreach (var part in ListSeparators.Split(text.Trim()).Where(p => p.Length > 0))
        {
            var known = P.LanguageOptions.FirstOrDefault(l => l.Equals(part, StringComparison.OrdinalIgnoreCase));
            var name = known ?? (part.Length is >= 2 and <= 20 && part.All(c => char.IsLetter(c) || c == ' ') ? Capitalise(part) : null);
            if (name is null || cv.Languages.Contains(name, StringComparer.OrdinalIgnoreCase) || cv.Languages.Count >= MaxCvLanguages)
                continue;
            cv.Languages.Add(name);
            added = true;
        }
        if (!added && cv.Languages.Count < MaxCvLanguages)
            return Fail(session, catalog, "Please choose a language from the list, or type it.");
        if (cv.Languages.Count >= MaxCvLanguages)
        {
            cv.LanguagesDone = true;
            return CvAdvance(session, Intent.CvAnswer);
        }
        return Reply(session, Intent.CvAnswer, P.AskCvLanguages(cv.Languages));
    }

    /// <summary>C8 "Change something": clear that part and ask it again; the flow then returns to C8.</summary>
    private static FsmStepResult? HandleCvEditChoice(ConversationSession session, string text, DialogueCatalog catalog)
    {
        session.Pending = null;
        var choice = P.CvEditOptions.FirstOrDefault(o => o.Id == text || o.Title.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (choice.Id is null)
            return null;   // not a menu choice: handle it as an answer in C8
        var cv = session.Cv!;
        switch (choice.Id)
        {
            case "1": cv.FullName = null; break;
            case "2": cv.PhoneAnswered = false; cv.Phone = null; cv.AwaitingPhone = false; break;
            case "3": cv.Jobs.Clear(); cv.JobsDone = false; break;
            case "4": cv.Training.Clear(); cv.TrainingDone = false; break;
            case "5": cv.Education = null; break;
            case "6": cv.Languages.Clear(); cv.LanguagesDone = false; break;
            case "7": cv.About = null; cv.AwaitingAbout = false; break;
        }
        return CvAdvance(session, Intent.CvAnswer);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static ConversationState NextCvState(CvDraft cv) =>
        cv.ConsentUtc is null ? ConversationState.CvConsent
        : cv.FullName is null ? ConversationState.CvName
        : !cv.PhoneAnswered ? ConversationState.CvPhone
        : !cv.JobsDone ? ConversationState.CvWork
        : !cv.TrainingDone ? ConversationState.CvTraining
        : cv.Education is null ? ConversationState.CvEducation
        : !cv.LanguagesDone ? ConversationState.CvLanguages
        : cv.About is null ? ConversationState.CvAbout
        : ConversationState.CvReview;

    private static FsmStepResult CvAdvance(ConversationSession session, Intent intent, string prefix = "")
    {
        session.Strikes = 0;
        Move(session, NextCvState(session.Cv!));
        return Reply(session, intent, CvPrompt(session, prefix));
    }

    private static OutboundMessage CvPrompt(ConversationSession session, string prefix = "")
    {
        var cv = session.Cv ??= new CvDraft();
        return session.State switch
        {
            ConversationState.CvConsent => P.AskCvConsent(prefix),
            ConversationState.CvName => P.AskCvName(prefix),
            ConversationState.CvPhone when cv.AwaitingPhone => P.AskCvOtherPhone(prefix),
            ConversationState.CvPhone => P.AskCvPhone(FormatPhone(session.WaId), prefix),
            ConversationState.CvWork => P.AskCvWork(cv.Jobs, prefix),
            ConversationState.CvTraining => P.AskCvTraining(cv.Training, prefix),
            ConversationState.CvEducation => P.AskCvEducation(prefix),
            ConversationState.CvLanguages => P.AskCvLanguages(cv.Languages, prefix),
            ConversationState.CvAbout when cv.AwaitingAbout => P.AskCvOwnAbout(prefix),
            ConversationState.CvAbout => P.AskCvAbout(AboutLine(session), prefix),
            _ => P.CvSummary(session, prefix)
        };
    }

    private static bool IsCvDone(string text)
        => IsDone(text) || text.Trim().ToLowerInvariant() is "skip" or "no" or "none" or "nothing" or "no more" or "no thanks";

    /// <summary>A free-text answer: has letters, isn't a bare yes/no or menu number, fits the length.</summary>
    private static bool IsFreeAnswer(string text, int maxLength)
    {
        var t = text.Trim();
        return t.Length >= 3 && t.Length <= maxLength && t.Count(char.IsLetter) >= 2 && !IsYes(t) && !IsNo(t);
    }

    /// <summary>"mason for asante builders at madina 2019 to 2023" -> description + "2019 – 2023".</summary>
    public static CvJob ParseJob(string text)
    {
        var t = text.Trim();
        string? period = null;
        Match? m;
        if ((m = YearRange.Match(t)).Success)
        {
            var end = m.Groups[2].Value.ToLowerInvariant() is "date" or "present" or "today" or "now" ? "now" : m.Groups[2].Value;
            period = $"{m.Groups[1].Value} – {end}";
        }
        else if ((m = Since.Match(t)).Success)
            period = $"since {m.Groups[1].Value}";
        else if ((m = ForYears.Match(t)).Success)
            period = m.Groups[1].Value == "1" ? "1 year" : $"{m.Groups[1].Value} years";

        var description = m is { Success: true } ? t.Remove(m.Index, m.Length) : t;
        description = Regex.Replace(description, @"\s+", " ").Trim().Trim(',', '.', '-', ' ');
        description = Regex.Replace(description, @"\s+(?:from|in|at)$", "", RegexOptions.IgnoreCase);
        return new CvJob { Description = Capitalise(description), Period = period, SourceUtterance = text };
    }

    /// <summary>The C7 suggestion, built only from the confirmed profile (no generated claims).</summary>
    public static string AboutLine(ConversationSession s)
    {
        var skills = s.Skills.Take(3).Select(k => k.CanonicalName.ToLowerInvariant()).ToList();
        var line = skills.Count > 0 ? $"Skilled in {JoinAnd(skills)}" : $"Experienced in {s.TradeName}";
        var exp = s.ExperienceText?.Trim();
        if (!string.IsNullOrEmpty(exp))
            line += exp.StartsWith("since", StringComparison.OrdinalIgnoreCase) ? $", working {exp}"
                : exp.Contains("year", StringComparison.OrdinalIgnoreCase) ? $", with {exp.ToLowerInvariant()} of experience"
                : s.ExperienceYears is { } y ? $", with about {y:0.#} years of experience" : "";
        line += ".";
        var areas = s.Areas.Select(a => a.Label).ToList();
        if (s.Availability is { } when)
            line += $" Available {when.ToLowerInvariant()}{(areas.Count > 0 ? $" in {JoinAnd(areas)}" : "")}.";
        return line;
    }

    /// <summary>"233241234567" -> "024 123 4567" (how Ghanaians write numbers).</summary>
    public static string FormatPhone(string waId)
    {
        var digits = new string(waId.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("233") && digits.Length == 12) digits = "0" + digits[3..];
        return digits.Length == 10 ? $"{digits[..3]} {digits[3..6]} {digits[6..]}" : waId;
    }

    /// <summary>A typed Ghana number (024 123 4567, +233 24 123 4567), formatted; null if it isn't one.</summary>
    public static string? ParsePhone(string text)
    {
        if (text.Any(char.IsLetter)) return null;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("233") && digits.Length == 12) digits = "0" + digits[3..];
        return digits.Length == 10 && digits[0] == '0' ? FormatPhone(digits) : null;
    }

    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvti", "tvet", "ctvet", "cotvet", "jhs", "shs", "bece", "wassce", "hnd", "ges", "ecg", "gwcl", "vra", "cbt", "nabptex"
    };

    /// <summary>
    /// Light tidying of a typed CV line: known places get their proper names ("madina" -> "Madina")
    /// and common Ghanaian abbreviations are upper-cased ("nvti" -> "NVTI"). Everything else stays
    /// as typed; the worker sees the result in the C8 summary.
    /// </summary>
    private static string TidyLine(string text, DialogueCatalog catalog)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (Acronyms.Contains(w))
                words[i] = w.ToUpperInvariant();
            else if (w.Length >= 3 && w == w.ToLowerInvariant() && catalog.FindLocation(w) is { } place
                     && place.Label.Equals(w, StringComparison.OrdinalIgnoreCase))
                words[i] = place.Label;
        }
        return Capitalise(string.Join(' ', words));
    }

    private static string TidyName(string text)
    {
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        // "kwame asante" / "KWAME ASANTE" -> "Kwame Asante"; mixed case is kept as typed.
        return t == t.ToLowerInvariant() || t == t.ToUpperInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant())
            : t;
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
    };
}
