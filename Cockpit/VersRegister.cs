using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Verse chatberichten (WhatsApp én Teams) die eerst volledig geladen worden vóór ze in de
/// cockpit verschijnen (klikken toont dan meteen het hele gesprek). Het laden opent de chat,
/// en daarmee ziet de bron hem als gelezen — de zijbalk noemt hem daarna dus niet meer.
/// Daarom onthoudt dit register de rij tot Maarten hem in de cockpit archiveert; anders
/// zou het bericht na het voorladen spoorloos verdwijnen.
/// Persistent in %APPDATA%\WorkManager\wa-vers.json respectievelijk teams-vers.json.
/// </summary>
public sealed class VersRegister
{
    public static readonly VersRegister WaVers = new("wa-vers.json");
    public static readonly VersRegister TeamsVers = new("teams-vers.json");

    public sealed class Rij
    {
        public string MessageId { get; set; } = "";
        public string Chat { get; set; } = "";
        public string Onderwerp { get; set; } = "";
        public string Tekst { get; set; } = "";
        public string Html { get; set; } = "";
        /// <summary>Voorlaadpoging afgerond (ook bij mislukking: dan toont de rij de preview).</summary>
        public bool Geladen { get; set; }
        public DateTimeOffset Datum { get; set; }
    }

    private readonly string _dataFile;

    private VersRegister(string bestandsnaam)
    {
        _dataFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorkManager", bestandsnaam);
    }

    public Dictionary<string, Rij> Load()
    {
        try
        {
            if (File.Exists(_dataFile) &&
                JsonSerializer.Deserialize<Dictionary<string, Rij>>(
                    File.ReadAllText(_dataFile)) is { } data)
            {
                return new Dictionary<string, Rij>(data, StringComparer.Ordinal);
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen — hooguit verschijnt een chat weer via de zijbalk.
        }
        return new Dictionary<string, Rij>(StringComparer.Ordinal);
    }

    public void Bewaar(Dictionary<string, Rij> data)
    {
        try
        {
            // Rijen ouder dan een week zijn allang afgehandeld of achterhaald.
            foreach (var sleutel in data
                .Where(p => p.Value.Datum < DateTimeOffset.Now.AddDays(-7))
                .Select(p => p.Key).ToList())
            {
                data.Remove(sleutel);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFile)!);
            File.WriteAllText(_dataFile, JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best effort.
        }
    }

    public void Verwijder(string messageId)
    {
        var data = Load();
        if (data.Remove(messageId))
        {
            Bewaar(data);
        }
    }
}
