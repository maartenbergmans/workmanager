using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// AH-inloggegevens voor de bezorgradar (%APPDATA%\WorkManager\ah-login.json). Het wachtwoord
/// staat DPAPI-versleuteld voor de huidige Windows-gebruiker, net als het Gmail-app-wachtwoord.
/// De hCaptcha op AH's loginpagina blijft Maartens werk: de app vult alleen de velden in en
/// klikt op Inloggen; komt er een captcha-challenge, dan toont de sessie het venster.
/// </summary>
public sealed class AhLoginSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-login.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Email { get; set; } = "";
    public string WachtwoordVersleuteld { get; set; } = "";

    [JsonIgnore]
    public string Wachtwoord
    {
        get => Decrypt(WachtwoordVersleuteld);
        set => WachtwoordVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public bool Compleet => Email.Length > 0 && Wachtwoord.Length > 0;

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

    /// <summary>
    /// Zet het bewaarde AH-wachtwoord op het klembord, voor als ah.be er tóch opnieuw om
    /// vraagt (dan plakt Maarten het zelf in het loginformulier).
    /// </summary>
    public static void WachtwoordNaarKlembord(Form eigenaar)
    {
        var login = Load();
        if (login.Wachtwoord.Length == 0)
        {
            Toast.Toon(eigenaar, "Geen AH-wachtwoord bewaard (ah-login.json)", Fluent.Copy);
            return;
        }
        Clipboard.SetText(login.Wachtwoord);
        Toast.Toon(eigenaar, "AH-wachtwoord op het klembord — plak het in het loginveld", Fluent.Copy);
    }

    public static AhLoginSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<AhLoginSettings>(
                    File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: als niet ingesteld behandelen.
        }
        return new AhLoginSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
