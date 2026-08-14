using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Instellingen voor de reisassistent: het vertrekadres (meestal thuis), hoeveel marge je
/// bovenop de rijtijd wil en hoe lang op voorhand je gewaarschuwd wil worden. Persistent in
/// %APPDATA%\WorkManager\reis-settings.json. Bewust onversleuteld: het gaat om een adres en
/// wat getallen, geen inloggegevens — de DPAPI-versleuteling elders is er voor wachtwoorden
/// en tokens.
/// </summary>
public class ReisSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "reis-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Waar je normaal vertrekt; leeg = reisassistent staat uit.</summary>
    public string ThuisAdres { get; set; } = "";

    /// <summary>Coördinaten van <see cref="ThuisAdres"/>, één keer opgezocht en daarna hergebruikt.</summary>
    public double ThuisLat { get; set; }

    public double ThuisLon { get; set; }

    /// <summary>Extra marge bovenop de rijtijd (parkeren, binnenlopen, uitloop van de vorige afspraak).</summary>
    public int BufferMinuten { get; set; } = 10;

    /// <summary>Hoeveel minuten vóór het vertrekmoment de melding komt.</summary>
    public int WaarschuwMinuten { get; set; } = 15;

    /// <summary>Afspraken korter dan deze rijtijd zijn de moeite van een melding niet waard.</summary>
    public int MinimumRijMinuten { get; set; } = 8;

    public bool Aan { get; set; } = true;

    /// <summary>Coördinaten voor de weersverwachting (valt terug op het thuisadres).</summary>
    public bool HeeftThuis => ThuisLat != 0 || ThuisLon != 0;

    public static ReisSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<ReisSettings>(File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults.
        }
        return new ReisSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
