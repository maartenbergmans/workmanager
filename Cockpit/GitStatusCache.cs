using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Het bewaarde beeld van alle repo's: per map de laatst gepeilde stand (branch, aantal
/// ongecommit, staged, voor/achter op de remote en de bestandenlijst met ouderdom), plus welke
/// repo's er gevonden zijn en welke bewust niet gevolgd worden. Gevuld door
/// <see cref="GitRadar"/> (één ronde per uur); gelezen door het Projecten-menu, het
/// git-overzichtsvenster, de teller in de werkbalk en het uurlijkse snapshot voor de online
/// tabel. Opslag: %APPDATA%\WorkManager\git-status-cache.json.
/// </summary>
public static class GitStatusCache
{
    private static readonly string CacheBestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "git-status-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Eén ongecommit bestand zoals het bewaard wordt.</summary>
    public sealed class Bestand
    {
        /// <summary>De ruwe porcelain-code ("??", " M", "A ", …).</summary>
        public string Code { get; set; } = "";

        /// <summary>Leesbare status ("gewijzigd", "nieuw (untracked)", …).</summary>
        public string Omschrijving { get; set; } = "";

        public string Pad { get; set; } = "";

        public bool Gestaged { get; set; }

        /// <summary>Hoeveel dagen het bestand al niet meer aangeraakt is (−1 = onbekend).</summary>
        public int Dagen { get; set; }
    }

    /// <summary>De laatst bekende stand van één repo.</summary>
    public sealed class Stand
    {
        /// <summary>Korte samenvatting voor een menu-item ("3 ongecommit · 1 achter").</summary>
        public string Kort { get; set; } = "";

        public string Branch { get; set; } = "";

        /// <summary>Aantal ongecommitte bestanden.</summary>
        public int Aantal { get; set; }

        /// <summary>Hoeveel daarvan al in de index staan.</summary>
        public int Staged { get; set; }

        public int Voor { get; set; }

        public int Achter { get; set; }

        /// <summary>Ouderdom van de langst openstaande wijziging, in dagen.</summary>
        public int OudsteDagen { get; set; }

        /// <summary>Leeg als het peilen lukte; anders de reden (geen repo, git-fout, time-out).</summary>
        public string Fout { get; set; } = "";

        public List<Bestand> Bestanden { get; set; } = new();

        public DateTimeOffset Moment { get; set; }
    }

    public sealed class Data
    {
        public Dictionary<string, Stand> PerMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>De repo's die de radar zelf gevonden heeft (bovenop de vaste lijst).</summary>
        public List<string> Repos { get; set; } = new();

        /// <summary>Repo's die bewust buiten beeld blijven (rechtsklik in het overzicht).</summary>
        public List<string> Genegeerd { get; set; } = new();

        public DateTimeOffset LaatsteControle { get; set; }

        public DateTimeOffset LaatsteOntdekking { get; set; }
    }

    public static Data Load()
    {
        try
        {
            if (File.Exists(CacheBestand) &&
                JsonSerializer.Deserialize<Data>(File.ReadAllText(CacheBestand), JsonOpts) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: zonder cache starten; de eerstvolgende ronde vult hem opnieuw.
        }
        return new Data();
    }

    public static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheBestand)!);
            File.WriteAllText(CacheBestand, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort: zonder bestand vergeet alleen de radar zijn laatste moment.
        }
    }
}
