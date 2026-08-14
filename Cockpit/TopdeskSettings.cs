using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// TopDesk-inloggegevens voor de CED-servicedesk (%APPDATA%\WorkManager\topdesk-login.json).
/// De behandelaarslogin op ced.topdesk.net; het wachtwoord staat DPAPI-versleuteld voor de
/// huidige Windows-gebruiker, net als bij AH en het Gmail-app-wachtwoord.
/// </summary>
public sealed class TopdeskSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "topdesk-login.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Url { get; set; } = "https://ced.topdesk.net";
    public string Gebruikersnaam { get; set; } = "";
    public string WachtwoordVersleuteld { get; set; } = "";

    [JsonIgnore]
    public string Wachtwoord
    {
        get => Decrypt(WachtwoordVersleuteld);
        set => WachtwoordVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public bool Compleet =>
        Url.Length > 0 && Gebruikersnaam.Length > 0 && Wachtwoord.Length > 0;

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

    public static TopdeskSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<TopdeskSettings>(
                    File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: als niet ingesteld behandelen.
        }
        return new TopdeskSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
