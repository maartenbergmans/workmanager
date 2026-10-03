using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de online git-tabel (git.php op de hosting): het adres en het gedeelde
/// token (DPAPI-versleuteld). Persistent in %APPDATA%\WorkManager\git-web-settings.json.
///
/// <para>Eigen token, los van dat van de persoonlijke webpagina: deze link gaat naar een
/// collega en mag dus niets anders kunnen dan de git-stand laten lezen.</para>
/// </summary>
public class GitWebSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "git-web-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Url { get; set; } = "";

    public string TokenVersleuteld { get; set; } = "";

    /// <summary>
    /// Of de bestandsnamen meegestuurd worden. Staat aan: een collega die meevolgt, wil juist
    /// zien wélke bestanden al dagen openstaan. Uit stuurt alleen de aantallen per project.
    /// </summary>
    public bool MetBestandsnamen { get; set; } = true;

    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenVersleuteld))
            {
                return "";
            }
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(TokenVersleuteld), null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }
        set => TokenVersleuteld = string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    [JsonIgnore]
    public bool Compleet =>
        Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Token.Length > 0;

    /// <summary>De link om aan een collega te geven (token erin, zodat er niets in te stellen valt).</summary>
    [JsonIgnore]
    public string Link => Compleet ? $"{Url}?t={Uri.EscapeDataString(Token)}" : "";

    public static GitWebSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<GitWebSettings>(File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar bestand: koppeling staat dan gewoon uit.
        }
        return new GitWebSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Zet elk uur de git-stand van alle repo's online (git.php), zodat Maarten én zijn collega
/// in een tabel kunnen meevolgen wat er nog ongecommit openstaat. Eén richting: er komt niets
/// terug van de pagina — die kan alleen lezen.
///
/// <para>De tray-timer klopt elke vijf minuten aan; deze klasse beslist zelf of het uur om is.
/// Vlak voor het versturen zorgt hij dat de radar een verse ronde gedraaid heeft, zodat wat er
/// online staat ook echt de stand van dat uur is.</para>
/// </summary>
public class GitWebSync
{
    /// <summary>Hoe vaak de stand omhoog gaat.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Wanneer de stand voor het laatst gelukt online gezet is (null = nog niet deze sessie).</summary>
    public static DateTimeOffset? LaatsteUpload { get; private set; }

    private static bool _bezig;

    /// <summary>
    /// Eén ronde: is het uur om (of wordt er geforceerd), dan gaat de verse stand online.
    /// Stil bij fouten — geen netwerk betekent gewoon dat de pagina nog even de vorige stand
    /// toont, en dat zegt ze er ook bij.
    /// </summary>
    public async Task PollAsync(bool forceren = false, CancellationToken ct = default)
    {
        var settings = GitWebSettings.Load();
        if (_bezig || !settings.Compleet)
        {
            return;
        }
        if (!forceren && LaatsteUpload is { } vorige && DateTimeOffset.Now - vorige < Interval)
        {
            return;
        }
        _bezig = true;
        try
        {
            // Een snapshot van een uur oud heeft geen zin: eerst peilen, dan versturen. Met
            // wat marge, zodat een ronde die net gedraaid is niet meteen overgedaan wordt.
            await GitRadar.ZorgVersAsync(ct, Interval - TimeSpan.FromMinutes(10));
            await PostAsync(settings, "snapshot", new { snapshot = BouwSnapshot(settings) }, ct);
            LaatsteUpload = DateTimeOffset.Now;
        }
        catch
        {
            // Netwerk-/serverfout: volgende ronde opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Het beeld dat online komt: per project de stand en (als dat aan staat) de bestanden met
    /// hun ouderdom. Bewust zonder de volledige mappen van deze pc — de projectnaam zegt
    /// genoeg, en padnamen van de pc horen niet op een pagina die een collega opent.
    /// </summary>
    private static object BouwSnapshot(GitWebSettings settings) => new
    {
        pc = Environment.MachineName,
        moment = DateTimeOffset.Now,
        ronde = GitRadar.Cache.LaatsteControle,
        totaal = GitRadar.TotaalOngecommit,
        metWerk = GitRadar.ProjectenMetWerk,
        achter = GitRadar.ProjectenAchter,
        projecten = GitRadar.Standen().Select(p => new
        {
            naam = GitRadar.Naam(p.Key),
            branch = p.Value.Branch,
            aantal = p.Value.Aantal,
            staged = p.Value.Staged,
            voor = p.Value.Voor,
            achter = p.Value.Achter,
            oudsteDagen = p.Value.OudsteDagen,
            fout = p.Value.Fout,
            gepeild = p.Value.Moment,
            bestanden = settings.MetBestandsnamen
                ? p.Value.Bestanden.Take(300).Select(b => new
                {
                    status = b.Omschrijving,
                    pad = b.Pad,
                    gestaged = b.Gestaged,
                    dagen = b.Dagen,
                }).ToList<object>()
                : new List<object>(),
        }).ToList(),
    };

    private static async Task PostAsync(
        GitWebSettings settings, string actie, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}?actie={actie}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Wm-Token", settings.Token);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
