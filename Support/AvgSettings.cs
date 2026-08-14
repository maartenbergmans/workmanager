using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Inloggegevens voor de AVG/Avast CloudCare-console (de.cloudcare.avg.com), bewaard in
/// %APPDATA%\WorkManager\avg-settings.json. Wordt gebruikt om bij een support-mail van een
/// klant automatisch de console te openen en (bij een persoonlijke afzender) meteen op de
/// voornaam te zoeken. Zelfde opzet als de SD Worx-instellingen.
/// </summary>
public sealed class AvgSettings
{
    public string Gebruiker { get; set; } = "maarten@urbanit.be";
    public string Wachtwoord { get; set; } = "InitPWD1!";

    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "avg-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static AvgSettings Load()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<AvgSettings>(File.ReadAllText(Bestand), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: met de standaardgegevens beginnen.
        }
        // Eerste keer: de standaardgegevens meteen wegschrijven zodat ze te bewerken zijn.
        var standaard = new AvgSettings();
        standaard.Save();
        return standaard;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }
}
