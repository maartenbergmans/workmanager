using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Cache van gegenereerde (en eventueel handmatig bewerkte) conceptantwoorden, per Message-ID.
/// Bij het herladen van de inbox worden gekende mails niet opnieuw door Claude beoordeeld.
/// Persistent in %APPDATA%\WorkManager\mail-reply-concepts.json.
/// </summary>
public static class ConceptCache
{
    public class Entry
    {
        public bool ConceptKlaar { get; set; }
        public string Concept { get; set; } = "";
        public string Reden { get; set; } = "";
        public bool AlleBeantwoorden { get; set; }
        public bool Genegeerd { get; set; } // chat zonder antwoord/actie: niet meer tonen
        public bool Urgent { get; set; } // vandaag best beantwoorden (rood in de lijsten)
        public DateTimeOffset Datum { get; set; }
    }

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string CacheFile = Path.Combine(DataDir, "mail-reply-concepts.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(CacheFile), JsonOpts);
                if (cache is not null)
                {
                    return cache;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: start met een lege cache (wordt bij de eerstvolgende save hersteld).
        }
        return new Dictionary<string, Entry>();
    }

    public static void Save(Dictionary<string, Entry> cache)
    {
        // Oude vermeldingen opruimen zodat het bestand niet eindeloos groeit.
        var grens = DateTimeOffset.Now.AddDays(-90);
        foreach (var key in cache.Where(e => e.Value.Datum < grens).Select(e => e.Key).ToList())
        {
            cache.Remove(key);
        }

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, JsonOpts));
    }
}
