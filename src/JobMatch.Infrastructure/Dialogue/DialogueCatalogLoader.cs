using System.Text.Json;
using JobMatch.Domain.Conversation;

namespace JobMatch.Infrastructure.Dialogue;

/// <summary>
/// Builds the <see cref="DialogueCatalog"/> from the same files the NLU service resolves
/// against (<c>nlp/data</c>: taxonomy_v1.json, localities_v1.json, ghana_districts.json), so the
/// dialogue's menus and the models can never disagree about trades, skills or places.
/// The API project copies the files into its output under <c>Data/</c>.
/// </summary>
public static class DialogueCatalogLoader
{
    public const string TaxonomyFile = "taxonomy_v1.json";
    public const string LocalitiesFile = "localities_v1.json";
    public const string DistrictsFile = "ghana_districts.json";

    public static DialogueCatalog Load(string dataDirectory)
    {
        using var taxonomy = Read(dataDirectory, TaxonomyFile);
        using var localities = Read(dataDirectory, LocalitiesFile);
        using var districts = Read(dataDirectory, DistrictsFile);

        var trades = taxonomy.RootElement.GetProperty("categories").EnumerateArray()
            .Select(c => new TradeInfo(
                c.GetProperty("id").GetString()!,
                c.GetProperty("name").GetString()!,
                Strings(c.GetProperty("occupation_terms")),
                c.GetProperty("skills").EnumerateArray()
                    .Select(s => new SkillInfo(s.GetProperty("id").GetString()!, s.GetProperty("name").GetString()!,
                                               Strings(s.GetProperty("synonyms"))))
                    .ToList()))
            .ToList();

        var places = localities.RootElement.GetProperty("localities").EnumerateArray()
            .Select(l => (l.GetProperty("name").GetString()!, Strings(l.GetProperty("aliases")),
                          l.GetProperty("district").GetString()!, l.GetProperty("region").GetString()!))
            .ToList();

        var byRegion = districts.RootElement.EnumerateObject()
            .ToDictionary(r => r.Name, r => Strings(r.Value));

        return new DialogueCatalog(
            trades,
            Strings(taxonomy.RootElement.GetProperty("trade_menu")),
            places,
            byRegion,
            Strings(localities.RootElement.GetProperty("location_menu")),
            taxonomy.RootElement.GetProperty("version").GetString() ?? "unknown");
    }

    private static JsonDocument Read(string directory, string file)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dialogue data file not found: {path}. Set Dialogue:DataPath.", path);
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static IReadOnlyList<string> Strings(JsonElement array)
        => array.EnumerateArray().Select(e => e.GetString()!).ToList();
}
