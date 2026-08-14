using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de Google Chat-koppeling: OAuth-client (uit de Google Cloud Console)
/// en het refresh-token na koppeling, DPAPI-versleuteld. Persistent in
/// %APPDATA%\WorkManager\google-chat-settings.json.
/// </summary>
public class GoogleChatSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "google-chat-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ClientId { get; set; } = "";
    public string ClientSecretVersleuteld { get; set; } = "";
    public string RefreshTokenVersleuteld { get; set; } = "";
    public string MijnUserId { get; set; } = ""; // Google-account-id (sub uit het id-token)
    public int DagenTerug { get; set; } = 3; // hoe ver terug berichten opgehaald worden

    /// <summary>Cache van users/123… naar weergavenaam, zodat namen maar één keer opgezocht worden.</summary>
    public Dictionary<string, string> NaamCache { get; set; } = new();

    /// <summary>Cache van persoonsnaam (lowercase) naar DM-space ("spaces/…") voor snelkoppelingen.</summary>
    public Dictionary<string, string> DmCache { get; set; } = new();

    [JsonIgnore]
    public string ClientSecret
    {
        get => Decrypt(ClientSecretVersleuteld);
        set => ClientSecretVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public string RefreshToken
    {
        get => Decrypt(RefreshTokenVersleuteld);
        set => RefreshTokenVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public bool Gekoppeld => ClientId.Length > 0 && RefreshTokenVersleuteld.Length > 0;

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

    public static GoogleChatSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<GoogleChatSettings>(
                    File.ReadAllText(SettingsFile), JsonOpts);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults.
        }
        return new GoogleChatSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
