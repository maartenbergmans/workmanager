using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// De centrale CED-login (maarten.bergmans@ced.be) voor de Microsoft-aanmeldschermen van
/// Outlook, Teams, Azure DevOps en ISPnext; TopDesk gebruikt hetzelfde wachtwoord met de
/// aparte gebruiker mber-admin@cedcloud.com. Het wachtwoord staat DPAPI-versleuteld in
/// %APPDATA%\WorkManager\ced-login.json; MFA blijft altijd handwerk.
///
/// <para>Weigert Microsoft het wachtwoord (verlopen of gewijzigd), dan zet
/// <see cref="MarkeerGeweigerd"/> het invullen blijvend uit — anders zou de assistent met
/// herhaalde foute pogingen een account-lockout veroorzaken. Een nieuw wachtwoord bewaren
/// (via <see cref="ZetWachtwoord"/>) heft dat weer op.</para>
/// </summary>
public sealed class CedLogin
{
    public const string Email = "maarten.bergmans@ced.be";
    public const string TopdeskGebruiker = "mber-admin@cedcloud.com";

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ced-login.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string WachtwoordVersleuteld { get; set; } = "";

    /// <summary>Wanneer het wachtwoord voor het laatst geweigerd werd; leeg = alles oké.</summary>
    public string GeweigerdOp { get; set; } = "";

    [JsonIgnore]
    public bool Geweigerd => GeweigerdOp.Length > 0;

    /// <summary>Het wachtwoord om in te vullen; leeg als het geweigerd of niet ingesteld is.</summary>
    public static string Wachtwoord()
    {
        var s = Load();
        return s.Geweigerd ? "" : Decrypt(s.WachtwoordVersleuteld);
    }

    /// <summary>Bewaart een (nieuw) wachtwoord en heft een eerdere weigering op.</summary>
    public static void ZetWachtwoord(string wachtwoord)
    {
        var s = Load();
        s.WachtwoordVersleuteld = string.IsNullOrEmpty(wachtwoord)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(wachtwoord), null, DataProtectionScope.CurrentUser));
        s.GeweigerdOp = "";
        s.Save();
    }

    /// <summary>Zet het automatisch invullen uit na een geweigerd wachtwoord.</summary>
    public static void MarkeerGeweigerd()
    {
        var s = Load();
        s.GeweigerdOp = DateTimeOffset.Now.ToString("O");
        s.Save();
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

    public static CedLogin Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<CedLogin>(File.ReadAllText(SettingsFile), JsonOpts)
                    is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: als niet ingesteld behandelen.
        }
        return new CedLogin();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
