namespace JobMatch.Domain.Matching;

/// <summary>
/// What the ranker sees of a worker. Trade and skills only: no name, phone, gender, age or
/// ethnicity exists in the request (NFR-13 fairness by construction).
/// </summary>
public record MatchProfile(string? TradeName, IReadOnlyList<string> Skills);

/// <summary>A job that passed stage 1 (geographic + availability filter).</summary>
public record MatchCandidate(int JobId, string Title, string? Description, IReadOnlyList<string> RequiredSkills);

/// <summary>Stage 2 output for one job: similarity and the explanation inputs (FR-13).</summary>
public record RankedMatch(
    int JobId,
    int Rank,
    double Score,
    string? BestSkill,
    double? BestSkillScore,
    IReadOnlyList<string> SkillOverlap);

/// <summary>A ranking call's result; <see cref="Ranker"/> says which method produced it.</summary>
public record RankingResult(string Ranker, string? ProfileText, IReadOnlyList<RankedMatch> Matches, double? LatencyMs = null);

/// <summary>
/// Stage 2 of the S&D matching engine (L5): rank geo-filtered jobs for one worker and return
/// the top <c>topK</c> scoring at least <c>minScore</c>.
/// </summary>
public interface IMatchRanker
{
    Task<RankingResult> RankAsync(MatchProfile profile, IReadOnlyList<MatchCandidate> candidates,
        int topK, double minScore, CancellationToken ct);
}

/// <summary>
/// Availability compatibility (stage 1, with location): only skills, location and availability
/// may decide a match (NFR-13). A worker who can work full-time can take any job; part-time
/// fits part-time, weekend and flexible jobs; weekends-only fits weekend and flexible jobs.
/// Free-text availability ("mornings only") or a job without one never excludes.
/// </summary>
public static class AvailabilityRules
{
    public static bool Compatible(string? worker, string? job)
    {
        if (string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(job) || job == "Flexible")
            return true;
        return worker switch
        {
            "Full-time" => true,
            "Part-time" => job is "Part-time" or "Weekends only",
            "Weekends only" => job is "Weekends only",
            _ => true
        };
    }
}
