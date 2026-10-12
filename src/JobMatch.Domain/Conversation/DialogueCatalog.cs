using System.Text.RegularExpressions;

namespace JobMatch.Domain.Conversation;

/// <summary>A taxonomy skill as the dialogue needs it: display name + the phrases that name it.</summary>
public record SkillInfo(string Id, string Name, IReadOnlyList<string> Synonyms);

/// <summary>A trade category (taxonomy level 1) with its skills and occupation terms.</summary>
public record TradeInfo(string Id, string Name, IReadOnlyList<string> OccupationTerms, IReadOnlyList<SkillInfo> Skills);

/// <summary>A skill found in text, with its trade.</summary>
public record SkillHit(TradeInfo Trade, SkillInfo Skill);

/// <summary>A selectable area (S4 list) and the district it resolves to.</summary>
public record LocationOption(string Label, string District, string Region);

/// <summary>
/// Read-only reference data for the dialogue: the skills taxonomy (D4), the trade and area
/// menus, and the locality -> district gazetteer. Built once from the files in
/// <c>nlp/data</c> by Infrastructure and passed into the pure FSM, so menus always match the
/// taxonomy the NLU models resolve against.
///
/// The exact-match lookups here are the gateway's own fallback when the NLU service is down;
/// they deliberately do no fuzzy matching (that lives in the NLU service).
/// </summary>
public class DialogueCatalog
{
    private readonly Dictionary<string, TradeInfo> _tradeByTerm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkillHit> _skillByPhrase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocationOption> _locationByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _neighbours = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TradeInfo> Trades { get; }
    /// <summary>Trades offered as list rows in S1 (WhatsApp lists hold at most 10).</summary>
    public IReadOnlyList<TradeInfo> TradeMenu { get; }
    /// <summary>Areas offered as list rows in S4.</summary>
    public IReadOnlyList<LocationOption> LocationMenu { get; }
    public string TaxonomyVersion { get; }

    public DialogueCatalog(
        IReadOnlyList<TradeInfo> trades,
        IReadOnlyList<string> tradeMenuIds,
        IEnumerable<(string Name, IReadOnlyList<string> Aliases, string District, string Region)> localities,
        IReadOnlyDictionary<string, IReadOnlyList<string>> districtsByRegion,
        IReadOnlyList<string> locationMenuLabels,
        string taxonomyVersion = "test",
        IReadOnlyDictionary<string, IReadOnlyList<string>>? adjacency = null)
    {
        // District adjacency is made symmetric: A next to B means B next to A.
        foreach (var (district, near) in adjacency ?? new Dictionary<string, IReadOnlyList<string>>())
            foreach (var other in near)
            {
                Neighbour(district).Add(other);
                Neighbour(other).Add(district);
            }

        Trades = trades;
        TaxonomyVersion = taxonomyVersion;
        foreach (var trade in trades)
        {
            Add(_tradeByTerm, trade.Name, trade);
            foreach (var term in trade.OccupationTerms) Add(_tradeByTerm, term, trade);
            foreach (var skill in trade.Skills)
            {
                var hit = new SkillHit(trade, skill);
                Add(_skillByPhrase, skill.Name, hit);
                foreach (var syn in skill.Synonyms) Add(_skillByPhrase, syn, hit);
            }
        }
        TradeMenu = tradeMenuIds.Select(id => trades.Single(t => t.Id == id)).ToList();

        foreach (var (region, districts) in districtsByRegion)
            foreach (var d in districts)
                Add(_locationByName, d, new LocationOption(d, d, region));
        foreach (var (name, aliases, district, region) in localities)
        {
            Add(_locationByName, name, new LocationOption(name, district, region));
            foreach (var a in aliases) Add(_locationByName, a, new LocationOption(name, district, region));
        }
        LocationMenu = locationMenuLabels
            .Select(label => FindLocation(label) is { } loc ? loc with { Label = label }
                : throw new ArgumentException($"Location menu label '{label}' is not a known locality."))
            .ToList();
    }

    public TradeInfo? TradeById(string id) => Trades.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// S&D L5 stage 1: the district itself plus its adjacent districts. A district with no
    /// adjacency entry yields just itself.
    /// </summary>
    public IReadOnlySet<string> Neighbourhood(string district)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { district };
        if (_neighbours.TryGetValue(district, out var near)) set.UnionWith(near);
        return set;
    }

    public bool AreNeighbours(string a, string b) => _neighbours.TryGetValue(a, out var near) && near.Contains(b);

    private HashSet<string> Neighbour(string district)
    {
        if (!_neighbours.TryGetValue(district, out var set))
            _neighbours[district] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return set;
    }

    /// <summary>Exact (normalised) trade name or occupation term: "mason", "Carpentry & Woodwork".</summary>
    public TradeInfo? FindTrade(string text) => _tradeByTerm.GetValueOrDefault(Normalise(text));

    /// <summary>Exact (normalised) skill name or synonym: "lay blocks" -> Block laying.</summary>
    public SkillHit? FindSkill(string text) => _skillByPhrase.GetValueOrDefault(Normalise(text));

    /// <summary>Exact locality, alias or district name: "madina" -> La Nkwantanang Madina.</summary>
    public LocationOption? FindLocation(string text) => _locationByName.GetValueOrDefault(Normalise(text));

    /// <summary>
    /// Every skill phrase occurring in free text, longest first and non-overlapping
    /// ("i can lay blocks and do tiling" -> Block laying, Tiling). Used by the offline stub.
    /// </summary>
    public IReadOnlyList<SkillHit> ScanSkills(string text) => Scan(text, FindSkill).ToList();

    public TradeInfo? ScanTrade(string text) => Scan(text, FindTrade).FirstOrDefault();

    public LocationOption? ScanLocation(string text) => Scan(text, FindLocation).FirstOrDefault();

    private static IEnumerable<T> Scan<T>(string text, Func<string, T?> lookup) where T : class
    {
        var words = Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taken = new bool[words.Length];
        var found = new List<(int Index, T Hit)>();
        for (var n = Math.Min(5, words.Length); n >= 1; n--)
            for (var i = 0; i + n <= words.Length; i++)
            {
                if (Enumerable.Range(i, n).Any(k => taken[k])) continue;
                var phrase = string.Join(' ', words, i, n);
                if (phrase.Length < 3 || lookup(phrase) is not { } hit) continue;
                found.Add((i, hit));
                for (var k = i; k < i + n; k++) taken[k] = true;
            }
        return found.OrderBy(f => f.Index).Select(f => f.Hit);
    }

    public static string Normalise(string text)
    {
        var t = Regex.Replace(text.ToLowerInvariant().Replace('’', '\''), @"[^\w\s'&-]", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static void Add<T>(Dictionary<string, T> index, string phrase, T value)
    {
        var key = Normalise(phrase);
        if (key.Length > 0) index.TryAdd(key, value);
    }
}
