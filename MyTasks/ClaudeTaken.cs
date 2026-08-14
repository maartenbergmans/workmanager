using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Haalt met de Claude Code CLI ('claude -p', zoals de mailassistent) concrete taken uit
/// ruwe input: notities, een braindump, een mail of vergaderverslag.
/// </summary>
public static class ClaudeTaken
{
    public sealed record Voorstel(string Tekst, string Categorie, int Prioriteit, DateOnly? Deadline);

    public static async Task<List<Voorstel>> GenereerAsync(
        string ruweTekst, List<string> categorieen, CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var prompt =
            $$"""
            Je helpt Maarten zijn persoonlijke takenlijst bijwerken. Hieronder staat ruwe input
            (notities, een braindump, een mail of vergaderverslag). Haal er de concrete,
            uitvoerbare taken uit.

            Regels:
            - Formuleer elke taak kort en actiegericht in het Nederlands (max. één regel).
            - Kies per taak de best passende categorie uit exact deze lijst: {{string.Join(", ", categorieen)}}.
            - prioriteit: 0 = hoog (dringend of blokkerend), 1 = normaal, 2 = laag.
            - deadline: alleen als de input er echt een noemt of impliceert; vandaag is
              {{vandaag:yyyy-MM-dd}} ({{vandaag.DayOfWeek}}). Anders null.
            - Geen dubbele of triviale taken; splits opsommingen in aparte taken.

            Antwoord UITSLUITEND met één JSON-array, zonder verdere tekst of markdown eromheen:
            [{"tekst": "…", "categorie": "…", "prioriteit": 0, "deadline": "yyyy-MM-dd" }]
            (deadline mag ook null zijn)

            Ruwe input:
            ---
            {{ruweTekst}}
            ---
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Geen JSON-lijst in het antwoord van Claude.");
        }

        using var doc = JsonDocument.Parse(output[start..(end + 1)]);
        var lijst = new List<Voorstel>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var tekst = (el.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "").Trim();
            if (tekst.Length == 0)
            {
                continue;
            }
            var categorie = el.TryGetProperty("categorie", out var c) ? c.GetString() ?? "" : "";
            categorie = categorieen.FirstOrDefault(k =>
                    string.Equals(k, categorie, StringComparison.OrdinalIgnoreCase))
                ?? categorieen.FirstOrDefault() ?? "";
            var prio = el.TryGetProperty("prioriteit", out var p) && p.ValueKind == JsonValueKind.Number
                ? Math.Clamp(p.GetInt32(), 0, 2) : 1;
            DateOnly? deadline = null;
            if (el.TryGetProperty("deadline", out var d) && d.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(d.GetString(), out var datum))
            {
                deadline = datum;
            }
            lijst.Add(new Voorstel(tekst, categorie, prio, deadline));
        }
        return lijst;
    }
}
