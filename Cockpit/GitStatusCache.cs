using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Dagcache voor de git-status van alle projectmappen. De cockpit controleert automatisch
/// één keer per dag (meeliftend op de poll) en op verzoek via "Git controleren" (▾-menu);
/// het Projecten-menu toont de laatst bekende stand meteen, zonder bij het openen op
/// git/WSL te wachten. Opslag: %APPDATA%\WorkManager\git-status-cache.json.
/// </summary>
public static class GitStatusCache
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "git-status-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>De laatst bekende stand van één repo: samenvatting + controlemoment.</summary>
    public sealed class Stand
    {
        public string Kort { get; set; } = "";
        public int Achter { get; set; }
        public DateTimeOffset Moment { get; set; }
    }

    public sealed class Data
    {
        public Dictionary<string, Stand> PerMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LaatsteControle { get; set; }
    }

    public static Data Load()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<Data>(File.ReadAllText(Bestand), JsonOpts) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: zonder cache starten; de eerstvolgende controle vult hem opnieuw.
        }
        return new Data();
    }

    public static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort: zonder bestand vergeet alleen de dagcontrole zijn laatste moment.
        }
    }
}
