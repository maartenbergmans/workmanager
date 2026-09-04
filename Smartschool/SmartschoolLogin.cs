using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Inloggegevens voor Smartschool (het ouderaccount bij GIBO Mariaburg), DPAPI-versleuteld
/// in %APPDATA%\WorkManager\smartschool-login.json — zelfde opzet als <see cref="CedLogin"/>.
/// Smartschool kent voor dit account geen MFA, dus de aanmelding verloopt volledig stil.
/// Het wachtwoord staat nooit in code of git; instellen gebeurt eenmalig in het bestand.
/// </summary>
public sealed class SmartschoolLogin
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "smartschool-login.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Gebruiker { get; set; } = "";

    public string WachtwoordVersleuteld { get; set; } = "";

    /// <summary>
    /// Antwoorden op de verificatievraag "geboortedatum van het kind" (ISO, yyyy-MM-dd)
    /// die Smartschool bij een eerste aanmelding op een nieuw apparaat stelt. De eerste
    /// datum is die van het kind van het login-account; de rest is reserve.
    /// </summary>
    public List<string> Geboortedata { get; set; } = new();

    [JsonIgnore]
    public bool Compleet => Gebruiker.Length > 0 && WachtwoordVersleuteld.Length > 0;

    /// <summary>Is er een login ingesteld? Zo niet, dan slaat de cockpit Smartschool over.</summary>
    public static bool Geconfigureerd => Load().Compleet;

    public static string Gebruikersnaam() => Load().Gebruiker;

    /// <summary>Het wachtwoord om in te vullen, of "" als het niet ingesteld is.</summary>
    public static string Wachtwoord() => Decrypt(Load().WachtwoordVersleuteld);

    /// <summary>Bewaart (nieuwe) inloggegevens, DPAPI-versleuteld.</summary>
    public static void Zet(string gebruiker, string wachtwoord)
    {
        var s = Load();
        s.Gebruiker = gebruiker.Trim();
        s.WachtwoordVersleuteld = string.IsNullOrEmpty(wachtwoord)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(wachtwoord), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, JsonOpts));
    }

    private static string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return ""; // andere gebruiker/machine of corrupt: als niet ingesteld behandelen
        }
    }

    public static SmartschoolLogin Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<SmartschoolLogin>(
                    File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: als niet ingesteld behandelen.
        }
        return new SmartschoolLogin();
    }
}
