using System.Net.Http.Json;
using System.Text.Json;
using JobMatch.Domain.Matching;
using Microsoft.Extensions.Logging;

namespace JobMatch.Infrastructure.Matching;

/// <summary>
/// Stage 2 via the Python service's <c>POST /match</c>: MiniLM cosine similarity (S&D L5).
/// The contract is pinned by <c>nlp/tests/fixtures/match_response.json</c>.
/// </summary>
public class HttpMatchRanker : IMatchRanker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HttpMatchRanker(HttpClient http) => _http = http;

    public async Task<RankingResult> RankAsync(MatchProfile profile, IReadOnlyList<MatchCandidate> candidates,
        int topK, double minScore, CancellationToken ct)
    {
        var request = new
        {
            trade = profile.TradeName,
            skills = profile.Skills,
            jobs = candidates.Select(c => new { id = c.JobId, title = c.Title, description = c.Description, required_skills = c.RequiredSkills }),
            top_k = topK,
            min_score = minScore
        };
        using var resp = await _http.PostAsJsonAsync("/match", request, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    /// <summary>Map the service's JSON to <see cref="RankingResult"/> (public for contract tests).</summary>
    public static RankingResult Parse(JsonElement root) => new(
        root.GetProperty("model").GetString() ?? "unknown",
        root.TryGetProperty("profile_text", out var p) ? p.GetString() : null,
        root.GetProperty("matches").EnumerateArray().Select(m => new RankedMatch(
            m.GetProperty("id").GetInt32(),
            m.GetProperty("rank").GetInt32(),
            m.GetProperty("score").GetDouble(),
            m.TryGetProperty("best_skill", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            m.TryGetProperty("best_skill_score", out var bs) && bs.ValueKind == JsonValueKind.Number ? bs.GetDouble() : null,
            m.GetProperty("skill_overlap").EnumerateArray().Select(s => s.GetString()!).ToList())).ToList(),
        root.TryGetProperty("latency_ms", out var l) ? l.GetDouble() : null);
}

/// <summary>
/// Offline stage 2: ranks by exact overlap between the worker's skills and the job's required
/// skills (share of required skills covered). Used when the Python service is unavailable,
/// or in local development (<c>Nlu:UseStub</c>). Only jobs sharing at least one skill qualify,
/// so it is stricter than the semantic ranker and never offers an unrelated job.
/// </summary>
public class OfflineMatchRanker : IMatchRanker
{
    public const string Name = "offline-skill-overlap";

    public Task<RankingResult> RankAsync(MatchProfile profile, IReadOnlyList<MatchCandidate> candidates,
        int topK, double minScore, CancellationToken ct)
    {
        var mine = profile.Skills.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranked = candidates
            .Select(c =>
            {
                var overlap = c.RequiredSkills.Where(mine.Contains).ToList();
                var score = c.RequiredSkills.Count == 0 ? 0 : (double)overlap.Count / c.RequiredSkills.Count;
                return (c, overlap, score);
            })
            .Where(x => x.overlap.Count > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.overlap.Count).ThenBy(x => x.c.JobId)
            .Take(topK)
            .Select((x, i) => new RankedMatch(x.c.JobId, i + 1, Math.Round(x.score, 4), x.overlap[0], 1.0, x.overlap))
            .ToList();
        return Task.FromResult(new RankingResult(Name, null, ranked));
    }
}

/// <summary>Graceful degradation: the semantic ranker first, skill overlap if it fails.</summary>
public class FallbackMatchRanker : IMatchRanker
{
    private readonly IMatchRanker _primary;
    private readonly IMatchRanker _fallback;
    private readonly ILogger<FallbackMatchRanker> _logger;

    public FallbackMatchRanker(IMatchRanker primary, IMatchRanker fallback, ILogger<FallbackMatchRanker> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<RankingResult> RankAsync(MatchProfile profile, IReadOnlyList<MatchCandidate> candidates,
        int topK, double minScore, CancellationToken ct)
    {
        try
        {
            return await _primary.RankAsync(profile, candidates, topK, minScore, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Matching service unavailable; ranking by skill overlap instead.");
            return await _fallback.RankAsync(profile, candidates, topK, minScore, ct);
        }
    }
}
