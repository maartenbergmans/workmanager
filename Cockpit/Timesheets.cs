using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén timesheetregel, klaar om naar urbanadmin doorgeboekt te worden.</summary>
public sealed class TimesheetRegel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Datum { get; set; }
    public TimeOnly? Van { get; set; } // starttijd (bekend bij meetings); leeg = 09:00
    public string Klant { get; set; } = "";
    public int Minuten { get; set; }
    public string Omschrijving { get; set; } = "";
    public string Bron { get; set; } = ""; // "meeting" of "mail"
    public DateTimeOffset Aangemaakt { get; set; } = DateTimeOffset.Now;
    public bool Doorgeboekt { get; set; } // naar urbanadmin weggeschreven
}

/// <summary>
/// Lokale wachtrij van timesheetregels (%APPDATA%\WorkManager\timesheets.json). De regels
/// worden hier verzameld tot de urbanadmin-koppeling ze doorboekt.
/// </summary>
public static class TimesheetStore
{
    public static readonly string[] Klanten =
    {
        "CED", "Aqurat", "RadiologyPartners", "Lauryssens advies", "Lauryssens laurapp",
        "UrbanIT", "Niet factureerbaar",
    };

    /// <summary>Klant → project-id in urbanadmin (zie ook launch-config.json voor de contexten).</summary>
    private static readonly Dictionary<string, int> ProjectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CED"] = 1,
        ["Aqurat"] = 114,
        ["RadiologyPartners"] = 99,
        ["Lauryssens advies"] = 2,    // Lauryssens — Advies en consultancy
        ["Lauryssens laurapp"] = 9,   // Lauryssens — Ontwikkeling laurapp
        ["UrbanIT"] = 31,             // UrbanIT administratie
        ["Niet factureerbaar"] = 30,
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "timesheets.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<TimesheetRegel> Load()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<List<TimesheetRegel>>(
                    File.ReadAllText(DataFile), JsonOpts) is { } regels)
            {
                return regels;
            }
        }
        catch
        {
            // Onleesbaar: met een lege lijst verder (bestand wordt bij de save hersteld).
        }
        return new List<TimesheetRegel>();
    }

    public static void Voeg(TimesheetRegel regel)
    {
        var regels = Load();
        regels.Add(regel);
        Bewaar(regels);
    }

    private static void Bewaar(List<TimesheetRegel> regels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(regels, JsonOpts));
    }

    /// <summary>
    /// Boekt alle nog niet doorgeboekte regels als werkuren in urbanadmin (endpoint
    /// /workmanager/werkuur/registreer, zelfde token als de contextswitch-werkuren).
    /// Retourneert het aantal geboekte regels; 0 als de koppeling niet geconfigureerd is.
    /// Bij een fout blijft de regel in de wachtrij staan voor een volgende poging.
    /// </summary>
    public static async Task<int> BoekDoorAsync(CancellationToken ct)
    {
        var regels = Load();
        if (regels.All(r => r.Doorgeboekt))
        {
            return 0;
        }
        if (LaunchConfig.LoadOrCreate().Timesheets is not { Token.Length: > 0 } settings)
        {
            return 0;
        }
        var geboekt = 0;
        try
        {
            foreach (var regel in regels.Where(r => !r.Doorgeboekt))
            {
                if (!ProjectIds.TryGetValue(regel.Klant, out var projectId))
                {
                    continue; // onbekende klant: laten staan, valt op in timesheets.json
                }
                var url = $"{settings.BaseUrl.TrimEnd('/')}/workmanager/werkuur/registreer/{settings.Token}";
                using var content = new StringContent(JsonSerializer.Serialize(new
                {
                    project_id = projectId,
                    gebruiker_id = settings.GebruikerId,
                    datum = regel.Datum.ToString("yyyy-MM-dd"),
                    van = (regel.Van ?? new TimeOnly(9, 0)).ToString("HH:mm"),
                    minuten = regel.Minuten,
                    extra = regel.Omschrijving,
                }), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(url, content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"urbanadmin antwoordde HTTP {(int)response.StatusCode}");
                }
                regel.Doorgeboekt = true;
                geboekt++;
            }
        }
        finally
        {
            if (geboekt > 0)
            {
                Bewaar(regels);
            }
        }
        return geboekt;
    }
}
