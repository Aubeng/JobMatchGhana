using System.Net.Http.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace JobMatch.Infrastructure.Nlu;

/// <summary>
/// Deterministic keyword-based NLU stub for Slice 1. Maps common Ghanaian informal-sector
/// phrases to canonical skills so the FSM can be demonstrated end-to-end before the Python
/// service (Slice 2) is wired in. Returns realistic confidences so the confirm/refine and
/// repair paths are exercised.
/// </summary>
public class StubNluClient : INluClient
{
    // Ordered keyword -> canonical skill. First matching keyword per canonical skill wins.
    private static readonly (string Keyword, string Canonical)[] Lexicon =
    {
        ("mason", "Masonry"), ("block", "Masonry"), ("plaster", "Masonry"), ("brick", "Masonry"),
        ("carpenter", "Carpentry"), ("carpentry", "Carpentry"), ("wood", "Carpentry"), ("furniture", "Carpentry"),
        ("plumb", "Plumbing"), ("pipe", "Plumbing"), ("drain", "Plumbing"),
        ("electric", "Electrical Work"), ("wiring", "Electrical Work"), ("wire", "Electrical Work"),
        ("paint", "Painting"),
        ("weld", "Welding"), ("fabricat", "Welding"),
        ("drive", "Driving"), ("driver", "Driving"), ("taxi", "Driving"), ("trotro", "Driving"),
        ("tro-tro", "Driving"), ("car", "Driving"), ("okada", "Driving"), ("motor", "Driving"),
        ("cook", "Cooking"), ("chef", "Cooking"), ("food", "Cooking"), ("chop", "Cooking"), ("kitchen", "Cooking"),
        ("clean", "Cleaning"), ("wash", "Cleaning"), ("laundry", "Cleaning"), ("sweep", "Cleaning"),
        ("house help", "Domestic Work"), ("housemaid", "Domestic Work"), ("nanny", "Domestic Work"),
        ("maid", "Domestic Work"), ("domestic", "Domestic Work"),
        ("tailor", "Tailoring"), ("sew", "Tailoring"), ("dressmak", "Tailoring"), ("seamstress", "Tailoring"),
        ("hair", "Hairdressing"), ("braid", "Hairdressing"), ("barber", "Hairdressing"), ("salon", "Hairdressing"),
        ("farm", "Farming"), ("crop", "Farming"), ("garden", "Farming"),
        ("sell", "Retail Sales"), ("trade", "Retail Sales"), ("shop", "Retail Sales"),
        ("retail", "Retail Sales"), ("market", "Retail Sales"), ("vendor", "Retail Sales"),
        ("security", "Security"), ("guard", "Security"), ("watchman", "Security"),
        ("mechanic", "Auto Mechanics"), ("fitter", "Auto Mechanics"), ("vulcaniz", "Auto Mechanics"),
        ("tile", "Tiling"), ("tiling", "Tiling"),
        ("weav", "Weaving"), ("kente", "Weaving"),
        ("phone repair", "Phone Repair"), ("bead", "Beadwork")
    };

    public Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
    {
        var text = (utterance ?? string.Empty).ToLowerInvariant();
        var found = new List<SkillCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (keyword, canonical) in Lexicon)
        {
            if (text.Contains(keyword, StringComparison.Ordinal) && seen.Add(canonical))
            {
                // Simple length-based pseudo-confidence: longer, more specific keywords score higher.
                var confidence = Math.Min(0.95, 0.6 + keyword.Length * 0.03);
                found.Add(new SkillCandidate(canonical, Math.Round(confidence, 2)));
            }
        }

        var intent = found.Count > 0 ? "provide_skill" : "unknown";
        var overall = found.Count > 0 ? found.Max(f => f.Confidence) : 0.2;
        return Task.FromResult(new NluResult(intent, overall, found));
    }
}

/// <summary>
/// Real NLU client for Slice 2: POSTs to the Python FastAPI service's <c>/nlu</c> endpoint.
/// </summary>
public class HttpNluClient : INluClient
{
    private readonly HttpClient _http;

    public HttpNluClient(HttpClient http) => _http = http;

    private record NluRequest(string utterance, string state);
    private record NluResponse(string intent, double confidence, List<SkillDto> skills);
    private record SkillDto(string canonical_name, double confidence);

    public async Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("/nlu", new NluRequest(utterance, state), ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<NluResponse>(cancellationToken: ct)
                  ?? new NluResponse("unknown", 0, new());
        var skills = dto.skills.Select(s => new SkillCandidate(s.canonical_name, s.confidence)).ToList();
        return new NluResult(dto.intent, dto.confidence, skills);
    }
}
