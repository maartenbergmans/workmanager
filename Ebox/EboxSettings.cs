using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Inloggegevens voor e-Box Enterprise: CSAM-aanmelding met gebruikersnaam + wachtwoord +
/// beveiligingscode via mobiele app (TOTP). Het wachtwoord en het TOTP-geheim staan
/// DPAPI-versleuteld in %APPDATA%\WorkManager\ebox-login.json. De beveiligingscode wordt
/// lokaal berekend met de bestaande <see cref="Totp"/>-helper — hetzelfde algoritme als de
/// authenticator-app — zodat de hele login zonder telefoon kan.
/// </summary>
public class EboxSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ebox-login.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Gebruiker { get; set; } = "";
    public string WachtwoordVersleuteld { get; set; } = "";
    public string TotpSecretVersleuteld { get; set; } = "";

    [JsonIgnore]
    public string Wachtwoord
    {
        get => Decrypt(WachtwoordVersleuteld);
        set => WachtwoordVersleuteld = Encrypt(value);
    }

    /// <summary>Het base32-geheim waarmee de beveiligingscodes berekend worden.</summary>
    [JsonIgnore]
    public string TotpSecret
    {
        get => Decrypt(TotpSecretVersleuteld);
        set => TotpSecretVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public bool Compleet =>
        Gebruiker.Length > 0 && Wachtwoord.Length > 0 && TotpSecret.Length > 0;

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
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    public static EboxSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<EboxSettings>(File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: zonder gegevens verder (het venster meldt dan "handmatig inloggen").
        }
        return new EboxSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
