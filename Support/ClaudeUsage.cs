using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Leest het verbruik van het Claude-abonnement (5-uursvenster, weeklimieten) via het
/// OAuth-endpoint dat ook /usage in de CLI voedt, met het token van de lokale Claude
/// Code-installatie (~/.claude/.credentials.json). Alleen-lezen; er wordt niets gewijzigd.
/// </summary>
public static class ClaudeUsage
{
    public sealed record Limiet(string Naam, int Percent, DateTimeOffset? Reset, string Kind)
    {
        /// <summary>Vriendelijke resettekst: vandaag alleen het uur, anders dag + uur.</summary>
        public string ResetTekst => Reset is not { } r
            ? ""
            : r.ToLocalTime().Date == DateTime.Today
                ? $"reset om {r.ToLocalTime():HH:mm}"
                : $"reset {r.ToLocalTime():ddd d MMM HH:mm}";
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Haalt de actuele limieten op. Gooit met een duidelijke boodschap als het token
    /// ontbreekt of verlopen is (dan lost een nieuwe Claude-sessie het op).
    /// </summary>
    public static async Task<List<Limiet>> OphalenAsync(CancellationToken ct)
    {
        var pad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
        if (!File.Exists(pad))
        {
            throw new InvalidOperationException("Geen Claude CLI-login gevonden (~/.claude).");
        }
        string token;
        using (var creds = JsonDocument.Parse(File.ReadAllText(pad)))
        {
            token = creds.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                    oauth.TryGetProperty("accessToken", out var t)
                ? t.GetString() ?? ""
                : "";
        }
        if (token.Length == 0)
        {
            throw new InvalidOperationException("Geen toegangstoken in de Claude CLI-login.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "Claude-token verlopen — open even een Claude Code-sessie, dan ververst het vanzelf.");
        }
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var lijst = new List<Limiet>();
        if (doc.RootElement.TryGetProperty("limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in limits.EnumerateArray())
            {
                var kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                var percent = el.TryGetProperty("percent", out var p) &&
                              p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                DateTimeOffset? reset = el.TryGetProperty("resets_at", out var r) &&
                    r.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(r.GetString(), out var rd) ? rd : null;
                var model = el.TryGetProperty("scope", out var scope) &&
                            scope.ValueKind == JsonValueKind.Object &&
                            scope.TryGetProperty("model", out var mo) &&
                            mo.ValueKind == JsonValueKind.Object &&
                            mo.TryGetProperty("display_name", out var dn)
                    ? dn.GetString() ?? "" : "";
                var naam = kind switch
                {
                    "session" => "Sessie (5-uursvenster)",
                    "weekly_all" => "Week — alle modellen",
                    "weekly_scoped" => model.Length > 0 ? $"Week — {model}" : "Week — model",
                    _ => kind,
                };
                lijst.Add(new Limiet(naam, percent, reset, kind + "|" + model));
            }
        }
        // Terugval voor het geval de limits-lijst ooit wegvalt uit het antwoord.
        if (lijst.Count == 0)
        {
            foreach (var (veld, naam) in new[]
            {
                ("five_hour", "Sessie (5-uursvenster)"), ("seven_day", "Week — alle modellen"),
            })
            {
                if (doc.RootElement.TryGetProperty(veld, out var blok) &&
                    blok.ValueKind == JsonValueKind.Object &&
                    blok.TryGetProperty("utilization", out var u) &&
                    u.ValueKind == JsonValueKind.Number)
                {
                    DateTimeOffset? reset = blok.TryGetProperty("resets_at", out var r) &&
                        DateTimeOffset.TryParse(r.GetString(), out var rd) ? rd : null;
                    lijst.Add(new Limiet(naam, (int)u.GetDouble(), reset, veld));
                }
            }
        }
        return lijst;
    }
}
