using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Slim snooze-voorstel: Claude leest de mail en kiest het logische moment om hem terug te
/// laten komen — "factuur vervalt eind de maand" → de 28e 's ochtends, "zie je dinsdag" → de
/// maandag ervoor. Draait via 'claude -p' (het abonnement), net als de mailassistent.
/// </summary>
public static class ClaudeSnooze
{
    public static async Task<(DateTimeOffset Moment, string Reden)?> VoorstelAsync(
        MailBericht mail, CancellationToken ct)
    {
        var nu = DateTimeOffset.Now;
        var tekst = mail.Tekst.Length > 1800 ? mail.Tekst[..1800] + "…" : mail.Tekst;
        var prompt =
            $$"""
            Maarten snoozet een mail (tijdelijk uit de inbox tot een gekozen moment). Kies op
            basis van de inhoud het logische moment waarop de mail moet terugkomen.

            Denk aan: genoemde deadlines of vervaldata (kom een dag of twee eerder terug),
            afspraken of events (kom de werkdag ervóór terug), "volgende week" e.d. Niets
            concreets in de mail? Kies dan morgen om 08:00. Kies nooit een moment in het
            verleden en nooit in een weekend; werkdagen om 08:00 hebben de voorkeur.
            Nu is het {{nu:dddd d MMMM yyyy HH:mm}}.

            Antwoord UITSLUITEND met één JSON-object, zonder tekst eromheen:
            {"moment": "yyyy-MM-dd HH:mm", "reden": "max 8 woorden Nederlands"}

            De mail:
            ---
            Van: {{mail.Van}}
            Onderwerp: {{mail.Onderwerp}}
            Datum: {{mail.Datum.ToLocalTime():d MMMM yyyy}}

            {{tekst}}
            ---
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        var start = output.IndexOf('{');
        var einde = output.LastIndexOf('}');
        if (start < 0 || einde <= start)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(output[start..(einde + 1)]);
            var reden = doc.RootElement.TryGetProperty("reden", out var r)
                ? r.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("moment", out var m) &&
                DateTime.TryParse(m.GetString(), out var moment))
            {
                var gekozen = new DateTimeOffset(moment, nu.Offset);
                // Vangnet op wat het model ook teruggeeft: in de toekomst en max. 3 maanden ver.
                if (gekozen > nu.AddMinutes(10) && gekozen < nu.AddMonths(3))
                {
                    return (gekozen, reden);
                }
            }
        }
        catch (JsonException)
        {
            // Onbruikbaar antwoord: dan gewoon geen slim voorstel.
        }
        return null;
    }
}
