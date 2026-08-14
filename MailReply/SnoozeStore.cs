using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Bijhouden van gesnoozde mails (tot wanneer) en van de historiek van snoozekeuzes.
/// Het voorstel voor een snoozetijd leert uit die historiek: de vaakst gekozen combinatie
/// van "aantal dagen vooruit + uur" wordt het nieuwe voorstel. Persistent in
/// %APPDATA%\WorkManager\mail-snoozes.json en mail-snooze-history.json.
/// </summary>
public static class SnoozeStore
{
    public class SnoozeItem
    {
        public string MessageId { get; set; } = "";
        public string Van { get; set; } = "";
        public string Onderwerp { get; set; } = "";
        public DateTimeOffset Tot { get; set; }
    }

    public class HistoriekItem
    {
        public DateTimeOffset Moment { get; set; }
        public int DagenOffset { get; set; }
        public int Uur { get; set; }
        public bool VolgdeVoorstel { get; set; }
    }

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SnoozeFile = Path.Combine(DataDir, "mail-snoozes.json");
    private static readonly string HistoriekFile = Path.Combine(DataDir, "mail-snooze-history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<SnoozeItem> LoadSnoozes() => LoadList<SnoozeItem>(SnoozeFile);

    public static void SaveSnoozes(List<SnoozeItem> snoozes)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SnoozeFile, JsonSerializer.Serialize(snoozes, JsonOpts));
    }

    /// <summary>
    /// Suggestie voor de snoozetijd: de vaakst gekozen combinatie van dagen-vooruit en uur
    /// uit de historiek (bij gelijke stand wint de recentste); zonder historiek morgen 08:00.
    /// </summary>
    public static DateTimeOffset Voorstel()
    {
        var dagen = 1;
        var uur = 8;

        var historiek = LoadList<HistoriekItem>(HistoriekFile);
        if (historiek.Count > 0)
        {
            (dagen, uur) = historiek
                .GroupBy(h => (h.DagenOffset, h.Uur))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(h => h.Moment))
                .First().Key;
        }

        var nu = DateTimeOffset.Now;
        var voorstel = new DateTimeOffset(nu.Year, nu.Month, nu.Day, uur, 0, 0, nu.Offset)
            .AddDays(dagen);
        while (voorstel <= nu)
        {
            voorstel = voorstel.AddDays(1);
        }
        return voorstel;
    }

    /// <summary>Registreert de gemaakte keuze (t.o.v. het voorstel) zodat het voorstel bijleert.</summary>
    public static void RegistreerKeuze(DateTimeOffset voorstel, DateTimeOffset keuze)
    {
        var historiek = LoadList<HistoriekItem>(HistoriekFile);
        historiek.Add(new HistoriekItem
        {
            Moment = DateTimeOffset.Now,
            DagenOffset = (keuze.Date - DateTime.Today).Days,
            Uur = keuze.Hour,
            VolgdeVoorstel = keuze.Date == voorstel.Date && keuze.Hour == voorstel.Hour,
        });
        if (historiek.Count > 100)
        {
            historiek.RemoveRange(0, historiek.Count - 100);
        }

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(HistoriekFile, JsonSerializer.Serialize(historiek, JsonOpts));
    }

    private static List<T> LoadList<T>(string pad)
    {
        try
        {
            if (File.Exists(pad))
            {
                var lijst = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(pad), JsonOpts);
                if (lijst is not null)
                {
                    return lijst;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: start leeg (wordt bij de eerstvolgende save hersteld).
        }
        return new List<T>();
    }
}
