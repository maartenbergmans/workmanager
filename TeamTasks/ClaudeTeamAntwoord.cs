using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Leest met Claude een antwoord van een teamlid op de weekmail (of een statusmail) en
/// vertaalt dat naar de takenlijst: welke open taken zijn blijkbaar af, en welke nieuwe
/// taken noemt het lid. De uitkomst is een voorstel — bevestigen gebeurt in
/// <see cref="TeamAntwoordForm"/>.
/// </summary>
public static class ClaudeTeamAntwoord
{
    public sealed record Uitkomst(
        List<TeamTaak> Klaar, List<ClaudeTeamTaken.Voorstel> Nieuw, string Samenvatting);

    public static async Task<Uitkomst> GenereerAsync(
        string afzender, string mailTekst, TeamTasksData data, CancellationToken ct)
    {
        // Het lid uit de afzender halen (voornaam in de naam); anders alle open taken meegeven.
        var lid = data.Leden.FirstOrDefault(l => afzender.Contains(l, StringComparison.OrdinalIgnoreCase)) ?? "";
        var open = data.Taken
            .Where(t => !t.Klaar && (lid.Length == 0 || t.Lid.Equals(lid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var lijst = new StringBuilder();
        for (var i = 0; i < open.Count; i++)
        {
            lijst.AppendLine($"{i + 1}. [{open[i].Lid}] {open[i].Tekst}" +
                (open[i].Subtaken.Count > 0
                    ? " (subtaken: " + string.Join("; ", open[i].Subtaken.Where(s => !s.Klaar).Select(s => s.Tekst)) + ")"
                    : ""));
        }
        var prompt =
            $$"""
            Maarten (teamlead) kreeg een mail van teamlid {{(lid.Length > 0 ? lid : afzender)}}, als
            antwoord op de wekelijkse prioriteitenmail. Hieronder de open taken van dat lid
            (genummerd) en de mailtekst. Bepaal:
            - "klaar": de nummers van de taken die volgens de mail afgewerkt zijn (alleen bij
              een duidelijke uitspraak: "is gedaan", "staat in productie", "afgerond"; niet
              bij "mee bezig" of "volgende week").
            - "nieuw": concrete nieuwe taken die het lid zelf noemt en die nog niet in de
              lijst staan (kort, actiegericht, Nederlands; prioriteit 0 alleen bij urgentie,
              anders 1). Teamleden: {{string.Join(", ", data.Leden)}}.
            - "samenvatting": één zin over de stand van zaken volgens deze mail (voor Maarten).

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"klaar": [1, 4], "nieuw": [{"tekst": "…", "lid": "…", "prioriteit": 1}], "samenvatting": "…"}

            Open taken:
            ---
            {{lijst}}
            ---

            Mail van {{afzender}}:
            ---
            {{mailTekst}}
            ---
            """;
        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        using var doc = ClaudeDrafter.ParseJson(output);
        var klaar = new List<TeamTaak>();
        if (doc.RootElement.TryGetProperty("klaar", out var k) && k.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in k.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.Number && n.GetInt32() is var i && i >= 1 && i <= open.Count)
                {
                    klaar.Add(open[i - 1]);
                }
            }
        }
        var nieuw = new List<ClaudeTeamTaken.Voorstel>();
        if (doc.RootElement.TryGetProperty("nieuw", out var nw) && nw.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in nw.EnumerateArray())
            {
                var tekst = (el.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "").Trim();
                if (tekst.Length == 0)
                {
                    continue;
                }
                var wie = el.TryGetProperty("lid", out var l) ? l.GetString() ?? "" : "";
                wie = data.Leden.FirstOrDefault(x => x.Equals(wie, StringComparison.OrdinalIgnoreCase))
                      ?? (lid.Length > 0 ? lid : data.Leden.FirstOrDefault() ?? "");
                var prio = el.TryGetProperty("prioriteit", out var p) && p.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(p.GetInt32(), 0, 2) : 1;
                nieuw.Add(new ClaudeTeamTaken.Voorstel(tekst, wie, prio));
            }
        }
        var samenvatting = doc.RootElement.TryGetProperty("samenvatting", out var s) ? s.GetString() ?? "" : "";
        return new Uitkomst(klaar, nieuw, samenvatting);
    }

    /// <summary>
    /// Gesprekspunten per teamlid (voor een 1-op-1 of de teammeeting): wat sleept, wat is
    /// afgewerkt, waar zit de druk. Tekst in Markdown-achtige puntjes.
    /// </summary>
    public static async Task<string> GesprekspuntenAsync(TeamTasksData data, CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var sinds = data.LaatsteMailVerzonden ?? DateTimeOffset.Now.AddDays(-14);
        var sb = new StringBuilder();
        foreach (var lid in data.Leden.Where(l => !data.NietInMail.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            var eigen = data.Taken.Where(t => t.Lid.Equals(lid, StringComparison.OrdinalIgnoreCase)).ToList();
            sb.AppendLine($"## {lid}");
            foreach (var t in eigen.Where(t => !t.Klaar))
            {
                var kenmerken = new List<string> { t.Prioriteit == 0 ? "★★★" : t.Prioriteit == 1 ? "★★" : "★", $"{t.Leeftijd} dagen open" };
                if (t.InMail > 0) kenmerken.Add($"{t.InMail}× gemaild");
                if (t.Deadline is { } d) kenmerken.Add(d < vandaag ? $"deadline {d:d/M} VOORBIJ" : $"deadline {d:d/M}");
                sb.AppendLine($"- open: {t.Tekst} ({string.Join(", ", kenmerken)})");
            }
            foreach (var t in eigen.Where(t => t.Klaar && t.KlaarOp > sinds))
            {
                sb.AppendLine($"- afgewerkt {t.KlaarOp:d/M}: {t.Tekst}");
            }
            sb.AppendLine();
        }
        var prompt =
            $$"""
            Je helpt Maarten (teamlead IT bij CED) zijn wekelijkse gesprekken met zijn teamleden
            voor te bereiden. Hieronder per teamlid de open taken (met prioriteit, hoe lang ze
            al openstaan, hoe vaak ze al in de weekmail stonden, deadlines) en wat recent
            afgewerkt is.

            Schrijf per teamlid 2 tot 4 korte gesprekspunten in het Nederlands: wat sleept en
            waarom dat een vraag verdient, waar een concrete afspraak (deadline) nodig is, wat
            een compliment verdient, en of de werklast er te hoog of te laag uitziet. Wees
            concreet (noem de taak), niet belerend. Geen inleiding, geen afsluiting.
            Formaat: per lid een kop met de naam, daaronder puntjes met "- ".

            Gegevens:
            ---
            {{sb}}
            ---
            """;
        return (await ClaudeDrafter.RunClaudeAsync(prompt, ct)).Trim();
    }
}
