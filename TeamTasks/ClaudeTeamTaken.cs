using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Haalt met de Claude Code CLI ('claude -p') teamtaken uit ruwe input en verdeelt ze over
/// de teamleden: noemt de tekst iemand (expliciet of impliciet), dan gaat de taak naar dat
/// lid; anders naar het opgegeven standaardlid.
/// </summary>
public static class ClaudeTeamTaken
{
    public sealed record Voorstel(string Tekst, string Lid, int Prioriteit, DateOnly? Deadline = null);

    public static async Task<List<Voorstel>> GenereerAsync(
        string ruweTekst, List<string> leden, string standaardLid, CancellationToken ct)
    {
        var prompt =
            $$"""
            Je helpt Maarten taken verdelen over zijn team. Hieronder staat ruwe input
            (notities, een mail, een verslag of een opsomming). Haal er de concrete,
            uitvoerbare taken uit en wijs elke taak aan een teamlid toe.

            Teamleden (kies exact uit deze lijst): {{string.Join(", ", leden)}}.

            Regels:
            - Formuleer elke taak kort en actiegericht in het Nederlands (max. één regel);
              neem de naam van het teamlid NIET op in de taaktekst.
            - Noemt de input een teamlid bij naam of duidelijk impliciet ("Wim pakt…",
              "voor Kris"), wijs de taak dan aan dat lid toe.
            - Is er geen aanwijsbaar teamlid, gebruik dan {{standaardLid}}.
            - prioriteit: 0 alleen als de input echt urgentie aangeeft (dringend, blokkerend,
              deadline deze week), 1 = normaal, 2 = laag.
            - Geen dubbele of triviale taken; splits opsommingen in aparte taken.
            - deadline: alleen als de input een moment noemt ("tegen vrijdag", "voor 20/9",
              "deze week" = vrijdag van deze week, "volgende week" = vrijdag van volgende
              week, "eind september" = laatste werkdag), als "yyyy-MM-dd"; anders null.
              Vandaag is {{DateTime.Now:yyyy-MM-dd}} ({{DateTime.Now:dddd}}).

            Antwoord UITSLUITEND met één JSON-array, zonder verdere tekst of markdown eromheen:
            [{"tekst": "…", "lid": "…", "prioriteit": 1, "deadline": null}]

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
            var lid = el.TryGetProperty("lid", out var l) ? l.GetString() ?? "" : "";
            lid = leden.FirstOrDefault(k => string.Equals(k, lid, StringComparison.OrdinalIgnoreCase))
                  ?? standaardLid;
            var prio = el.TryGetProperty("prioriteit", out var p) && p.ValueKind == JsonValueKind.Number
                ? Math.Clamp(p.GetInt32(), 0, 2) : 1;
            DateOnly? deadline = el.TryGetProperty("deadline", out var dl) && dl.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(dl.GetString(), "yyyy-MM-dd", out var datum)
                ? datum : null;
            lijst.Add(new Voorstel(tekst, lid, prio, deadline));
        }
        return lijst;
    }
}
