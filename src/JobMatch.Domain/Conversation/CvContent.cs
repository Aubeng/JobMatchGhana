namespace JobMatch.Domain.Conversation;

/// <summary>
/// Everything printed on a CV (Slice 6): the CV answers plus the confirmed profile. Built from
/// the session so the PDF always matches what the worker last confirmed.
/// </summary>
public record CvContent(
    string FullName,
    string? Phone,
    string? Trade,
    IReadOnlyList<string> Skills,
    string? Experience,
    string? Availability,
    IReadOnlyList<string> Areas,
    string? About,
    IReadOnlyList<CvJob> Jobs,
    IReadOnlyList<string> Training,
    string? Education,
    IReadOnlyList<string> Languages)
{
    public static CvContent From(ConversationSession s)
    {
        var cv = s.Cv ?? throw new InvalidOperationException("The session has no CV.");
        return new CvContent(
            cv.FullName ?? "",
            cv.Phone,
            s.TradeName,
            s.Skills.Select(k => k.CanonicalName).ToList(),
            s.ExperienceText,
            s.Availability,
            s.Areas.Select(a => a.Label).ToList(),
            cv.About,
            cv.Jobs,
            cv.Training,
            cv.Education,
            cv.Languages);
    }

    /// <summary>"CV - Kwame Asante.pdf", with anything unsafe in a file name removed.</summary>
    public string FileName
    {
        get
        {
            var safe = new string(FullName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'').ToArray()).Trim();
            return $"CV - {(safe.Length > 0 ? safe : "JobMatch")}.pdf";
        }
    }
}
