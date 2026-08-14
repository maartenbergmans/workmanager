using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén map in Google Drive, zoals de API ze teruggeeft.</summary>
public sealed record DriveMap(string Id, string Naam);

/// <summary>
/// Bestanden naar Google Drive schrijven en door de mappenboom bladeren. Lift mee op de
/// OAuth-koppeling van Google Chat: dezelfde client-id en hetzelfde refresh-token, alleen
/// met de Drive-scope erbij. Er is dus geen tweede koppeling om te onderhouden.
///
/// Bewust de REST API en niet de gesynchroniseerde Drive-map op schijf: de snelkoppelingen
/// die Maarten al gebruikt zijn folder-id's, en die zijn niet betrouwbaar naar een lokaal
/// pad te vertalen (gedeelde mappen staan er soms niet eens tussen).
/// </summary>
public static class GoogleDriveClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3";

    /// <summary>Gedeelde schijven meenemen; zonder dit ziet de API alleen "Mijn Drive".</summary>
    private const string Gedeeld = "supportsAllDrives=true&includeItemsFromAllDrives=true";

    /// <summary>
    /// Uploadt een bestand van schijf naar de map met dit id en geeft de naam terug waaronder
    /// het in Drive staat. Multipart: metadata en inhoud in één request, want de bijlagen zijn
    /// klein genoeg om een hervatbare upload niet te rechtvaardigen.
    /// </summary>
    public static async Task<string> UploadAsync(
        GoogleChatSettings s, string mapId, string bestandspad, CancellationToken ct)
    {
        var token = await GoogleChatClient.AccessTokenAsync(s, ct);
        var naam = Path.GetFileName(bestandspad);

        var metadata = JsonSerializer.Serialize(new { name = naam, parents = new[] { mapId } });
        using var inhoud = new MultipartContent("related")
        {
            new StringContent(metadata, Encoding.UTF8, "application/json"),
            new ByteArrayContent(await File.ReadAllBytesAsync(bestandspad, ct))
            {
                Headers = { ContentType = new MediaTypeHeaderValue(MimeType(bestandspad)) },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{Upload}/files?uploadType=multipart&supportsAllDrives=true")
        {
            Content = inhoud,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? naam : naam;
    }

    /// <summary>De submappen van een map; "root" staat voor de wortel van Mijn Drive.</summary>
    public static async Task<List<DriveMap>> SubmappenAsync(
        GoogleChatSettings s, string mapId, CancellationToken ct)
    {
        var query = $"'{mapId}' in parents and mimeType = 'application/vnd.google-apps.folder' " +
                    "and trashed = false";
        return await ZoekAsync(s, query, ct);
    }

    /// <summary>Mappen waarvan de naam deze tekst bevat, over de hele Drive.</summary>
    public static async Task<List<DriveMap>> ZoekMappenAsync(
        GoogleChatSettings s, string tekst, CancellationToken ct)
    {
        var veilig = tekst.Replace("\\", "\\\\").Replace("'", "\\'");
        var query = $"name contains '{veilig}' and mimeType = 'application/vnd.google-apps.folder' " +
                    "and trashed = false";
        return await ZoekAsync(s, query, ct);
    }

    /// <summary>De naam van één map; gebruikt om een onthouden id weer leesbaar te maken.</summary>
    public static async Task<string> MapNaamAsync(
        GoogleChatSettings s, string mapId, CancellationToken ct)
    {
        var token = await GoogleChatClient.AccessTokenAsync(s, ct);
        using var doc = await GetAsync(token, $"{Api}/files/{mapId}?fields=name&supportsAllDrives=true", ct);
        return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    }

    private static async Task<List<DriveMap>> ZoekAsync(
        GoogleChatSettings s, string query, CancellationToken ct)
    {
        var token = await GoogleChatClient.AccessTokenAsync(s, ct);
        var url = $"{Api}/files?q={Uri.EscapeDataString(query)}" +
                  $"&fields=files(id,name)&orderBy=name&pageSize=200&{Gedeeld}";

        using var doc = await GetAsync(token, url, ct);
        var mappen = new List<DriveMap>();
        if (doc.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                mappen.Add(new DriveMap(
                    f.GetProperty("id").GetString() ?? "",
                    f.GetProperty("name").GetString() ?? ""));
            }
        }
        return mappen;
    }

    private static async Task<JsonDocument> GetAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
        var body = await response.Content.ReadAsStringAsync(ct);
        // 403 met "insufficientPermissions" betekent bijna altijd: de koppeling dateert van vóór
        // de Drive-scope. Dat is met een herkoppeling opgelost, dus zeg dat er meteen bij.
        var hint = body.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("insufficient authentication", StringComparison.OrdinalIgnoreCase)
            ? " — koppel Google opnieuw in Instellingen; de Drive-toestemming ontbreekt nog."
            : "";
        throw new InvalidOperationException(
            $"Google Drive {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}{hint}");
    }

    private static string MimeType(string pad) => Path.GetExtension(pad).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".txt" or ".log" => "text/plain",
        ".csv" => "text/csv",
        ".xml" => "application/xml",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".eml" => "message/rfc822",
        _ => "application/octet-stream",
    };
}
