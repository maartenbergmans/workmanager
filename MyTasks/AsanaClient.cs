using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de Asana-koppeling: Personal Access Token (DPAPI-versleuteld) en de
/// gekozen workspace. Persistent in %APPDATA%\WorkManager\asana-settings.json.
/// </summary>
public class AsanaSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "asana-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string TokenVersleuteld { get; set; } = "";
    public string WorkspaceGid { get; set; } = "";
    public string WorkspaceNaam { get; set; } = "";

    [JsonIgnore]
    public string Token
    {
        get => Decrypt(TokenVersleuteld);
        set => TokenVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public bool Compleet => Token.Length > 0 && WorkspaceGid.Length > 0;

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

    public static AsanaSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AsanaSettings>(File.ReadAllText(SettingsFile), JsonOpts);
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
        return new AsanaSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Dunne client op de Asana REST-API (Personal Access Token): open taken van de gebruiker
/// ophalen en taken op voltooid zetten.
/// </summary>
public static class AsanaClient
{
    public sealed record AsanaTaak(
        string Gid, string Naam, DateOnly? Deadline, string Url, string Omschrijving = "");
    public sealed record Workspace(string Gid, string Naam);

    private const string ApiBasis = "https://app.asana.com/api/1.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Workspaces van de tokenhouder (om er in de instellingen één te kiezen).</summary>
    public static async Task<List<Workspace>> WorkspacesAsync(string token, CancellationToken ct)
    {
        var lijst = new List<Workspace>();
        using var doc = await GetAsync(token, "/workspaces?limit=100", ct);
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            lijst.Add(new Workspace(
                el.GetProperty("gid").GetString() ?? "",
                el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        }
        return lijst;
    }

    /// <summary>Alle openstaande taken die aan de tokenhouder toegewezen zijn.</summary>
    public static async Task<List<AsanaTaak>> OpenTakenAsync(
        AsanaSettings settings, CancellationToken ct)
    {
        var taken = new List<AsanaTaak>();
        var pad = $"/tasks?assignee=me&workspace={settings.WorkspaceGid}&completed_since=now" +
                  "&opt_fields=name,due_on,permalink_url,notes&limit=100";
        while (pad.Length > 0)
        {
            using var doc = await GetAsync(settings.Token, pad, ct);
            foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var naam = (el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Trim();
                if (naam.Length == 0)
                {
                    continue; // lege secties/placeholder-taken overslaan
                }
                DateOnly? deadline = null;
                if (el.TryGetProperty("due_on", out var d) && d.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParse(d.GetString(), out var datum))
                {
                    deadline = datum;
                }
                taken.Add(new AsanaTaak(
                    el.GetProperty("gid").GetString() ?? "",
                    naam,
                    deadline,
                    el.TryGetProperty("permalink_url", out var u) ? u.GetString() ?? "" : "",
                    el.TryGetProperty("notes", out var no) ? no.GetString() ?? "" : ""));
            }

            // Paginering: next_page.uri bevat de volledige vervolg-URL.
            pad = "";
            if (doc.RootElement.TryGetProperty("next_page", out var volgende) &&
                volgende.ValueKind == JsonValueKind.Object &&
                volgende.TryGetProperty("uri", out var uri) &&
                uri.GetString() is { Length: > 0 } volledig)
            {
                pad = volledig.Replace(ApiBasis, "");
            }
        }
        return taken;
    }

    /// <summary>Zet een Asana-taak op voltooid.</summary>
    public static async Task VoltooiAsync(AsanaSettings settings, string taakGid, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBasis}/tasks/{taakGid}")
        {
            Content = new StringContent("""{"data":{"completed":true}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
    }

    /// <summary>
    /// Past de deadline en/of de omschrijving van een Asana-taak aan. Een veld dat je niet
    /// meegeeft (null) blijft ongemoeid; <paramref name="deadline"/> expliciet wissen doe je met
    /// <paramref name="deadlineWissen"/>.
    /// </summary>
    public static async Task WijzigAsync(
        AsanaSettings settings, string taakGid, DateOnly? deadline, bool deadlineWissen,
        string? omschrijving, CancellationToken ct)
    {
        var velden = new List<string>();
        if (deadlineWissen)
        {
            velden.Add("\"due_on\":null");
        }
        else if (deadline is { } d)
        {
            velden.Add($"\"due_on\":\"{d:yyyy-MM-dd}\"");
        }
        if (omschrijving is not null)
        {
            velden.Add($"\"notes\":{JsonSerializer.Serialize(omschrijving)}");
        }
        if (velden.Count == 0)
        {
            return; // niets gewijzigd
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBasis}/tasks/{taakGid}")
        {
            Content = new StringContent(
                $"{{\"data\":{{{string.Join(",", velden)}}}}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
    }

    private static async Task<JsonDocument> GetAsync(string token, string pad, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBasis + pad);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ControleerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Asana weigert het token (401) — controleer de koppeling.");
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        var kort = body.Length > 200 ? body[..200] + "…" : body;
        throw new InvalidOperationException($"Asana-API gaf {(int)response.StatusCode}: {kort}");
    }
}
