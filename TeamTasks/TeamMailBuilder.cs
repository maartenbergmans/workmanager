using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace WorkManager;

/// <summary>
/// Bouwt de wekelijkse prioriteitenmail voor het team met een vaste structuur: alleen de
/// taken met hoge prioriteit (★★★), met opsommingstekens per teamlid. Claude komt er
/// alleen nog aan te pas voor het herwerken op feedback; versturen gaat via de
/// Gmail-SMTP-instellingen van de mailassistent.
/// </summary>
public static class TeamMailBuilder
{
    public sealed record WeekMail(string Onderwerp, string Tekst);

    /// <summary>Herwerkt de mailtekst op basis van feedback, opnieuw in de stijl van de voorbeelden.</summary>
    public static async Task<string> ReviseAsync(
        string huidigeTekst, string feedback, string stijl, CancellationToken ct)
    {
        var prompt =
            $$"""
            Hieronder staan de wekelijkse prioriteitenmail die Maarten naar zijn team wil sturen
            en feedback van Maarten op die tekst. Herschrijf de mail volgens de feedback en blijf
            daarbij in de stijl van deze eerdere weekmails:
            ---
            {{stijl}}
            ---

            Huidige mailtekst:
            ---
            {{huidigeTekst}}
            ---

            Feedback van Maarten:
            ---
            {{feedback}}
            ---

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"tekst": "de volledige herschreven mailtekst"}
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        using var doc = ClaudeDrafter.ParseJson(output);
        return doc.RootElement.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "";
    }

    /// <summary>Vakantiedagen (ma t/m vr) van de gegeven werkweek voor deze persoon.</summary>
    private static List<DateOnly> AfwezigeDagen(TeamTasksData data, string persoon, DateOnly maandag) =>
        Enumerable.Range(0, 5).Select(maandag.AddDays)
            .Where(dag => data.Vakanties.Any(v =>
                string.Equals(v.Persoon, persoon, StringComparison.OrdinalIgnoreCase) &&
                v.Van <= dag && dag <= v.Tot))
            .ToList();

    /// <summary>
    /// Bouwt de weekmail: vaste aanhef en afsluiting, de opmerking van de week, en per
    /// teamlid (in de ledenvolgorde) de open taken met hoge prioriteit als opsomming.
    /// Normale en lage prioriteit blijven bewust uit de mail. Handmatig ingegeven
    /// vakanties tellen mee: wie de hele werkweek afwezig is krijgt geen taken (dat wordt
    /// bovenaan vermeld), deels afwezigen krijgen de dagen achter hun naam.
    /// </summary>
    public static WeekMail BouwZelf(TeamTasksData data)
    {
        var maandag = SdWorxVakanties.VolgendeMaandag(DateOnly.FromDateTime(DateTime.Now));
        // De openingstekst (tot waar de namen beginnen) wisselt per week af tussen Nederlands,
        // Frans en Engels op basis van het ISO-weeknummer, zodat de mail rouleert over de drie
        // teamtalen. 0 = NL, 1 = FR, 2 = EN.
        var taal = System.Globalization.ISOWeek.GetWeekOfYear(
            maandag.ToDateTime(TimeOnly.MinValue)) % 3;
        var begroeting = taal switch
        {
            1 => "Bonjour à tous,",
            2 => "Hi all,",
            _ => "Beste collega's,",
        };
        var intro = taal switch
        {
            1 => "Voici les priorités pour la semaine prochaine :",
            2 => "Please find below the priorities for next week:",
            _ => "Hieronder de prioriteiten voor volgende week:",
        };
        var sb = new StringBuilder();
        sb.AppendLine(begroeting);
        sb.AppendLine();
        sb.AppendLine(intro);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(data.Opmerking))
        {
            sb.AppendLine(data.Opmerking.Trim());
            sb.AppendLine();
        }

        // Handmatig ingegeven vakanties (Maarten zelf, teamleden of anderen) vermelden.
        var afwezigZinnen = new List<string>();
        foreach (var persoon in data.Vakanties.Select(v => v.Persoon)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dagen = AfwezigeDagen(data, persoon, maandag);
            if (dagen.Count == 0)
            {
                continue;
            }
            var ikzelf = persoon.Equals("Maarten", StringComparison.OrdinalIgnoreCase);
            var dagenTekst = string.Join(", ", dagen.Select(d => d.ToString("dddd d/M")));
            afwezigZinnen.Add((ikzelf, heleWeek: dagen.Count == 5, taal) switch
            {
                // Frans
                (true, true, 1) => "Je suis absent toute la semaine.",
                (true, false, 1) => $"Je suis absent le {dagenTekst}.",
                (false, true, 1) => $"{persoon} est absent toute la semaine.",
                (false, false, 1) => $"{persoon} est absent le {dagenTekst}.",
                // Engels
                (true, true, 2) => "I'm away all week.",
                (true, false, 2) => $"I'm away on {dagenTekst}.",
                (false, true, 2) => $"{persoon} is away all week.",
                (false, false, 2) => $"{persoon} is away on {dagenTekst}.",
                // Nederlands
                (true, true, _) => "Ik ben zelf heel de week afwezig.",
                (true, false, _) => $"Ik ben zelf afwezig op {dagenTekst}.",
                (false, true, _) => $"{persoon} is heel de week afwezig.",
                _ => $"{persoon} is afwezig op {dagenTekst}.",
            });
        }
        if (afwezigZinnen.Count > 0)
        {
            sb.AppendLine(string.Join(" ", afwezigZinnen));
            sb.AppendLine();
        }

        var leden = data.Leden
            .Concat(data.Taken.Select(t => t.Lid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Sommige leden (bv. Ludo) krijgen wel taken maar horen nooit in de weekmail.
            .Where(l => !data.NietInMail.Contains(l, StringComparer.OrdinalIgnoreCase));
        foreach (var lid in leden)
        {
            // Heel de werkweek afwezig: dan is taken toewijzen zinloos — sectie overslaan
            // (de afwezigheid staat hierboven al vermeld).
            var afwezig = AfwezigeDagen(data, lid, maandag);
            if (afwezig.Count == 5)
            {
                continue;
            }
            var taken = data.Taken
                .Where(t => !t.Klaar && t.Prioriteit == 0 &&
                            string.Equals(t.Lid, lid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (taken.Count == 0)
            {
                continue;
            }

            sb.AppendLine(afwezig.Count > 0
                ? $"{lid} (afwezig {string.Join(", ", afwezig.Select(d => d.ToString("dddd d/M")))})"
                : lid);
            foreach (var taak in taken)
            {
                sb.AppendLine($"  • {taak.Tekst}");
                foreach (var sub in taak.Subtaken
                             .Where(s => !s.Klaar && !string.IsNullOrWhiteSpace(s.Tekst)))
                {
                    sb.AppendLine($"      ◦ {sub.Tekst.Trim()}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine(taal switch
        {
            1 => "Bien à vous,",
            2 => "Kind regards,",
            _ => "Met vriendelijke groeten,",
        });
        sb.AppendLine("Maarten");
        return new WeekMail("Prioriteiten volgende week", sb.ToString());
    }

    /// <summary>Verstuurt de weekmail via de Gmail-SMTP-instellingen van de mailassistent.</summary>
    public static async Task VerstuurAsync(
        MailReplySettings s, string aan, string onderwerp, string tekst, CancellationToken ct)
    {
        var bericht = new MimeMessage();
        bericht.From.Add(MailboxAddress.Parse(s.Email));
        foreach (var adres in aan.Split(new[] { ';', ',' },
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            bericht.To.Add(MailboxAddress.Parse(adres));
        }
        bericht.Subject = onderwerp;
        bericht.Body = new TextPart("plain") { Text = tekst };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort, SecureSocketOptions.SslOnConnect, ct);
        await smtp.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        await smtp.SendAsync(bericht, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
