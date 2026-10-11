using System.Net.Http.Json;
using System.Text.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

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
        return Task.FromResult(new NluResult(intent, overall, found) { ModelVersion = ModelVersion });
    }

    public const string ModelVersion = "stub";
}

/// <summary>
/// Slice 2 NLU client: POSTs to the Python FastAPI service's <c>/nlu</c> endpoint
/// (fine-tuned XLM-R intents + BERT+CRF entities). The JSON contract is pinned by
/// <c>nlp/tests/fixtures/nlu_response.json</c>, which both the Python and .NET tests read.
/// </summary>
public class HttpNluClient : INluClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HttpNluClient(HttpClient http) => _http = http;

    private record NluRequest(string utterance, string state);

    public async Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("/nlu", new NluRequest(utterance, state), ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    /// <summary>Map the service's JSON to <see cref="NluResult"/> (public for contract tests).</summary>
    public static NluResult Parse(JsonElement root)
    {
        var skills = root.GetProperty("skills").EnumerateArray()
            .Select(s => new SkillCandidate(s.GetProperty("canonical_name").GetString()!,
                                            s.GetProperty("confidence").GetDouble()))
            .ToList();

        TradeCandidate? trade = null;
        if (root.TryGetProperty("trade_category", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            var examples = t.TryGetProperty("example_skills", out var ex) && ex.ValueKind == JsonValueKind.Array
                ? ex.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();
            trade = new TradeCandidate(t.GetProperty("category_id").GetString()!, t.GetProperty("name").GetString()!,
                                       t.GetProperty("confidence").GetDouble(), examples);
        }

        string? district = null;
        if (root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            district = loc.GetProperty("district").GetString();

        double? years = null;
        if (root.TryGetProperty("experience", out var exp) && exp.ValueKind == JsonValueKind.Object
            && exp.TryGetProperty("years", out var y) && y.ValueKind == JsonValueKind.Number)
            years = y.GetDouble();

        return new NluResult(root.GetProperty("intent").GetString()!, root.GetProperty("confidence").GetDouble(), skills)
        {
            Trade = trade,
            LocationDistrict = district,
            ExperienceYears = years,
            BelowThreshold = root.TryGetProperty("below_threshold", out var b) && b.GetBoolean(),
            EntitiesJson = root.TryGetProperty("entities", out var ents) ? ents.GetRawText() : null,
            ModelVersion = root.TryGetProperty("model_version", out var v) ? v.GetString() : null,
            LatencyMs = root.TryGetProperty("latency_ms", out var l) ? l.GetDouble() : null
        };
    }
}

/// <summary>
/// Graceful degradation (S&D interface principle v): if the NLP service is down, slow or
/// returns an error, the turn is analysed by the keyword stub instead of failing. The
/// fallback is recorded in <c>MessageLog.NluModelVersion</c> as "stub-fallback" so such
/// turns can be excluded or analysed separately.
/// </summary>
public class FallbackNluClient : INluClient
{
    public const string FallbackVersion = "stub-fallback";

    private readonly INluClient _primary;
    private readonly INluClient _fallback;
    private readonly ILogger<FallbackNluClient> _logger;

    public FallbackNluClient(INluClient primary, INluClient fallback, ILogger<FallbackNluClient> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
    {
        try
        {
            return await _primary.AnalyzeAsync(utterance, state, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "NLU service unavailable; falling back to the keyword stub.");
            var result = await _fallback.AnalyzeAsync(utterance, state, ct);
            return result with { ModelVersion = FallbackVersion };
        }
    }
}
