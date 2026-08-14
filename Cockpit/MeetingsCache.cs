using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Schijfcache voor de agenda (eigen + Hilke + CED per dag), zodat de meetings-lijst bij een
/// herstart of verversing meteen gevuld is met de laatst bekende afspraken in plaats van leeg
/// te blijven tot de (trage) agenda-fetch en CED-scrape klaar zijn. Zelfde idee als
/// <see cref="CockpitCache"/> voor de berichten. Opslag: %APPDATA%\WorkManager\meetings-cache.json.
/// </summary>
public static class MeetingsCache
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "meetings-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Data
    {
        public List<AgendaClient.AgendaItem> Eigen { get; set; } = new();
        public List<AgendaClient.AgendaItem> Hilke { get; set; } = new();
        /// <summary>CED-afspraken per dag (sleutel: yyyy-MM-dd).</summary>
        public Dictionary<string, List<AgendaClient.AgendaItem>> Ced { get; set; } = new();
        public DateOnly Tot { get; set; }
        public DateTimeOffset Bewaard { get; set; }
    }

    public static Data? Load()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<Data>(File.ReadAllText(Bestand), JsonOpts) is { } d &&
                // Verouderde cache (ouder dan twee dagen) niet meer tonen: dan liever even leeg.
                d.Bewaard > DateTimeOffset.Now.AddDays(-2))
            {
                return d;
            }
        }
        catch
        {
            // Onleesbaar: zonder cache starten.
        }
        return null;
    }

    public static void Save(
        List<AgendaClient.AgendaItem> eigen, List<AgendaClient.AgendaItem> hilke,
        IEnumerable<KeyValuePair<DateOnly, List<AgendaClient.AgendaItem>>> ced, DateOnly tot)
    {
        try
        {
            var data = new Data
            {
                Eigen = eigen,
                Hilke = hilke,
                Ced = ced.ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd"), kv => kv.Value),
                Tot = tot,
                Bewaard = DateTimeOffset.Now,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort; de volgende poll probeert opnieuw.
        }
    }
}
