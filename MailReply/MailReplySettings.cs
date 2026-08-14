using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de Gmail-mailassistent: IMAP/SMTP-gegevens en het app-wachtwoord
/// (DPAPI-versleuteld voor de huidige Windows-gebruiker). Persistent in
/// %APPDATA%\WorkManager\mail-reply-settings.json; de instructies voor Claude staan
/// als vrije tekst in mail-reply-instructions.txt.
/// </summary>
public class MailReplySettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "mail-reply-settings.json");
    private static readonly string InstructionsFile = Path.Combine(DataDir, "mail-reply-instructions.txt");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Email { get; set; } = "maarten@urbanit.be";
    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 465;
    public int MaxMails { get; set; } = 25;
    public bool AlleenOngelezen { get; set; }
    public string BillitAdres { get; set; } = "bermacon-uminyqd-nosplit@my.billit.be";
    public List<int> KolomBreedtes { get; set; } = new(); // maillijst; leeg = defaults
    public string AppWachtwoordVersleuteld { get; set; } = "";

    [JsonIgnore]
    public string AppWachtwoord
    {
        get => Decrypt(AppWachtwoordVersleuteld);
        set => AppWachtwoordVersleuteld = Encrypt(value);
    }

    private static string Encrypt(string value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Andere gebruiker/machine of corrupt: behandel als niet ingesteld.
            return "";
        }
    }

    public static MailReplySettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<MailReplySettings>(File.ReadAllText(SettingsFile), JsonOpts);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults (bestand wordt bij eerstvolgende save hersteld).
        }
        return new MailReplySettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static string LoadInstructies()
    {
        try
        {
            if (File.Exists(InstructionsFile))
            {
                return File.ReadAllText(InstructionsFile);
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op de defaulttekst.
        }

        SaveInstructies(DefaultInstructies);
        return DefaultInstructies;
    }

    public static void SaveInstructies(string tekst)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(InstructionsFile, tekst);
    }

    private const string DefaultInstructies =
        """
        Je schrijft conceptantwoorden namens Maarten (maarten@urbanit.be, Urban IT).

        Toon: vriendelijk, professioneel en beknopt. Schrijf in de taal van de afzender
        (meestal Nederlands). Geen overdreven formaliteit, geen holle frasen.

        Onderteken elk antwoord met:

        Met vriendelijke groeten,
        Maarten

        Beantwoord geen nieuwsbrieven, reclame, automatische meldingen of no-reply-afzenders.
        Kan je een vraag niet zeker beantwoorden, stel dan een kort concept op dat om
        verduidelijking vraagt of aangeeft dat Maarten er later inhoudelijk op terugkomt.
        """;
}
