using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace JobMatch.Infrastructure.Nlu;

/// <summary>
/// Offline NLU: exact phrase matching against the taxonomy and gazetteer (no models). Used
/// when <c>Nlu:UseStub</c> is true (local development) and as the fallback whenever the NLU
/// service is down (<see cref="FallbackNluClient"/>). It returns the same canonical skill,
/// trade and district names as the service, so profiles stay consistent either way. It
/// predicts no intent ("unknown"); typos and paraphrases need the real service.
/// </summary>
public class StubNluClient : INluClient
{
    public const string ModelVersion = "stub";
    /// <summary>Exact matches are certain enough to act on (>= the 0.75 threshold).</summary>
    private const double ExactConfidence = 0.8;
    private static readonly Regex Duration = new(
        @"\b(\d{1,2})\s*(?:\+\s*)?(?:years?|yrs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DialogueCatalog _catalog;

    public StubNluClient(DialogueCatalog catalog) => _catalog = catalog;

    public Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
    {
        var text = utterance ?? string.Empty;
        var hits = _catalog.ScanSkills(text);
        var skills = hits.Select(h => new SkillCandidate(h.Skill.Name, ExactConfidence)).ToList();

        TradeCandidate? trade = null;
        if (_catalog.ScanTrade(text) is { } named)
            trade = new TradeCandidate(named.Id, named.Name, ExactConfidence, named.Skills.Take(3).Select(k => k.Name).ToList());
        else if (hits.Count > 0)
            trade = new TradeCandidate(hits[0].Trade.Id, hits[0].Trade.Name, ExactConfidence,
                                       hits[0].Trade.Skills.Take(3).Select(k => k.Name).ToList(), "skill");

        var place = _catalog.ScanLocation(text);
        var years = Duration.Match(text);

        var result = new NluResult("unknown", skills.Count > 0 ? ExactConfidence : 0.2, skills)
        {
            Trade = trade,
            LocationDistrict = place?.District,
            LocationRegion = place?.Region,
            LocationText = place?.Label,
            LocationConfidence = place is null ? 0 : ExactConfidence,
            ExperienceYears = years.Success ? double.Parse(years.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            ExperienceText = years.Success ? years.Value : null,
            ModelVersion = ModelVersion
        };
        return Task.FromResult(result);
    }
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
                                       t.GetProperty("confidence").GetDouble(), examples,
                                       t.TryGetProperty("via", out var via) ? via.GetString() ?? "trade" : "trade");
        }

        string? district = null, region = null, place = null;
        double locConfidence = 0;
        if (root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            district = loc.GetProperty("district").GetString();
            region = loc.TryGetProperty("region", out var r) ? r.GetString() : null;
            place = loc.TryGetProperty("source_text", out var st) ? st.GetString() : null;
            locConfidence = loc.TryGetProperty("confidence", out var lc) ? lc.GetDouble() : 0;
        }

        double? years = null;
        string? expText = null;
        if (root.TryGetProperty("experience", out var exp) && exp.ValueKind == JsonValueKind.Object)
        {
            if (exp.TryGetProperty("years", out var y) && y.ValueKind == JsonValueKind.Number)
                years = y.GetDouble();
            expText = exp.TryGetProperty("text", out var et) ? et.GetString() : null;
        }

        return new NluResult(root.GetProperty("intent").GetString()!, root.GetProperty("confidence").GetDouble(), skills)
        {
            Trade = trade,
            LocationDistrict = district,
            LocationRegion = region,
            LocationText = place,
            LocationConfidence = locConfidence,
            ExperienceYears = years,
            ExperienceText = expText,
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
