using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén timesheetregel, klaar om naar urbanadmin doorgeboekt te worden.</summary>
public sealed class TimesheetRegel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Datum { get; set; }
    public TimeOnly? Van { get; set; } // starttijd (bekend bij meetings); leeg = 09:00
    /// <summary>
    /// Het projectlabel uit <see cref="ProjectCatalogus"/> ("Vriesveem · ICT management").
    /// Oudere regels bevatten nog een vaste klantnaam ("CED", "Aqurat"): die werkt als alias.
    /// </summary>
    public string Klant { get; set; } = "";
    /// <summary>Het urbanadmin-project; leeg bij oudere regels (dan via <see cref="Klant"/>).</summary>
    public int? ProjectId { get; set; }
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
    /// <summary>
    /// De boekbare projecten als labels, meest gebruikte eerst (zie <see cref="ProjectCatalogus"/>).
    /// </summary>
    public static IReadOnlyList<string> Klanten => ProjectCatalogus.Labels;

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
        // Oude klantnaam of los label → vast project + het actuele label, zodat de regel
        // ook na een hernoeming in urbanadmin op hetzelfde project terechtkomt.
        if (regel.ProjectId is null && ProjectCatalogus.Zoek(regel.Klant) is { } project)
        {
            regel.ProjectId = project.Id;
            regel.Klant = project.Label;
        }
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
                if ((regel.ProjectId ?? ProjectCatalogus.Zoek(regel.Klant)?.Id) is not { } projectId)
                {
                    continue; // onbekend project: laten staan, valt op in timesheets.json
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
