using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Verwerkt AH-leveringsbevestigingen die in de inbox opduiken: de taak "Albert Heijn
/// bestelling plaatsen" schuift vier dagen de toekomst in, en het levermoment (als het
/// uit de mail te lezen valt) gaat als afspraak in de Google-agenda. Elke mail wordt
/// maar één keer verwerkt (Message-ID's in ah-levering-status.json).
/// </summary>
public static class AhLevering
{
    private static readonly string StatusFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-levering-status.json");

    private static readonly string[] Maanden =
    {
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december",
    };

    /// <summary>Retourneert een korte melding als er iets verwerkt is, anders een lege string.</summary>
    public static async Task<string> VerwerkAsync(
        IEnumerable<MailBericht> mails, CancellationToken ct)
    {
        var kandidaten = mails.Where(m => !m.IsChat && m.MessageId.Length > 0 &&
            (m.VanAdres.Contains("ah.nl", StringComparison.OrdinalIgnoreCase) ||
             m.VanAdres.Contains("ah.be", StringComparison.OrdinalIgnoreCase) ||
             m.Van.Contains("albert heijn", StringComparison.OrdinalIgnoreCase)) &&
            Regex.IsMatch(m.Onderwerp, "bezorg|lever|bestell", RegexOptions.IgnoreCase)).ToList();
        if (kandidaten.Count == 0)
        {
            return "";
        }

        var verwerkt = LaadVerwerkt();
        var melding = "";
        var teArchiveren = new List<MailBericht>();
        foreach (var mail in kandidaten.Where(m => !verwerkt.Contains(m.MessageId)))
        {
            teArchiveren.Add(mail);
            // 1. AH-taak vier dagen opschuiven (of opnieuw aanmaken als hij al weg is).
            var nieuweDeadline = DateOnly.FromDateTime(DateTime.Now).AddDays(4);
            var data = MijnTaakStore.Load();
            var taak = data.Taken.FirstOrDefault(t => !t.Klaar &&
                t.Tekst.Contains("Albert Heijn", StringComparison.OrdinalIgnoreCase));
            if (taak is not null)
            {
                taak.Deadline = nieuweDeadline;
            }
            else
            {
                data.Taken.Add(new MijnTaak
                {
                    Tekst = "Albert Heijn bestelling plaatsen",
                    Categorie = "Privé",
                    Deadline = nieuweDeadline,
                });
            }
            MijnTaakStore.Save(data);
            melding = $"AH-levering herkend: besteltaak verschoven naar {nieuweDeadline:ddd d/M}";

            // 2. Levermoment in de Google-agenda (best effort; AH zet het moment vaak in
            // het onderwerp: "… dinsdag 28 juli 2026 16:00-20:00").
            if (ParseLevermoment(mail.Onderwerp + "\n" + mail.Tekst) is var (start, einde) &&
                start is not null)
            {
                var eind = einde ?? start.Value.AddHours(1);
                var gelukt = false;

                // Nooit dubbel: over één levering komen meerdere mails binnen (bevestiging,
                // herinnering op de dag zelf) en elke aanmaak krijgt een nieuw UID. Staat er
                // op die dag al een AH-levering in de agenda, dan blijft die gewoon staan.
                if (CalendarClient.Beschikbaar)
                {
                    try
                    {
                        if ((await CalendarClient.ZoekOpDagAsync(
                            DateOnly.FromDateTime(start.Value.LocalDateTime), "AH-levering", ct))
                            .Count > 0)
                        {
                            melding += "; levermoment stond al in de agenda";
                            verwerkt.Add(mail.MessageId);
                            continue;
                        }
                    }
                    catch
                    {
                        // Controle mislukt: liever het risico op een dubbele dan geen afspraak.
                    }
                }

                // Eerst via CalDAV (hetzelfde Gmail-app-wachtwoord als de mailkoppeling): dat is
                // betrouwbaarder dan de Google Chat-koppeling, die vaak de agenda-scope mist.
                if (CalendarClient.Beschikbaar)
                {
                    try
                    {
                        gelukt = await CalendarClient.MaakAfspraakAsync(
                            "AH-levering 🛒", start.Value.LocalDateTime, eind - start.Value,
                            "Je Albert Heijn-bestelling wordt geleverd.", ct);
                    }
                    catch
                    {
                        // Val hieronder terug op de Google Chat-koppeling.
                    }
                }
                if (!gelukt)
                {
                    var chat = GoogleChatSettings.Load();
                    if (chat.Gekoppeld)
                    {
                        try
                        {
                            await GoogleChatClient.MaakAgendaEventAsync(
                                chat, "AH-levering 🛒", start.Value, eind, ct);
                            gelukt = true;
                        }
                        catch
                        {
                            // Beide routes mislukt.
                        }
                    }
                }
                melding += gelukt
                    ? "; levermoment in de agenda gezet"
                    : "; levermoment niet in de agenda gekregen";
            }

            verwerkt.Add(mail.MessageId);
        }
        BewaarVerwerkt(verwerkt);

        // De bevestiging is nu verwerkt (taak verschoven, levermoment in de agenda): de mail
        // zelf mag uit de inbox — archiveren, zodat hij niet blijft slingeren.
        if (teArchiveren.Count > 0)
        {
            try
            {
                var s = MailReplySettings.Load();
                if (s.Email.Length > 0 && s.AppWachtwoord.Length > 0)
                {
                    await GmailClient.ArchiveerAsync(s, teArchiveren, ct);
                    melding += "; mail gearchiveerd";
                }
            }
            catch
            {
                // Archiveren is een extraatje; de verwerking zelf is al gelukt.
            }
        }
        return melding;
    }

    /// <summary>Zoekt "dinsdag 29 juli … 16:00 [tot/–] 18:00"-achtige levermomenten in de mailtekst.</summary>
    private static (DateTimeOffset? Start, DateTimeOffset? Einde) ParseLevermoment(string tekst)
    {
        var datum = Regex.Match(tekst, @"\b(\d{1,2})\s+(" + string.Join('|', Maanden) + @")\b",
            RegexOptions.IgnoreCase);
        var tijden = Regex.Match(tekst,
            @"\b(\d{1,2})[:.](\d{2})\s*(?:-|–|tot|en)\s*(\d{1,2})[:.](\d{2})\b");
        if (!datum.Success || !tijden.Success)
        {
            return (null, null);
        }

        var dag = int.Parse(datum.Groups[1].Value);
        var maand = Array.FindIndex(Maanden,
            m => m.Equals(datum.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        var jaar = DateTime.Now.Year;
        if (maand < DateTime.Now.Month - 1)
        {
            jaar++; // decembermail over een januarilevering
        }
        try
        {
            var offset = DateTimeOffset.Now.Offset;
            var start = new DateTimeOffset(jaar, maand, dag,
                int.Parse(tijden.Groups[1].Value), int.Parse(tijden.Groups[2].Value), 0, offset);
            var einde = new DateTimeOffset(jaar, maand, dag,
                int.Parse(tijden.Groups[3].Value), int.Parse(tijden.Groups[4].Value), 0, offset);
            return (start, einde > start ? einde : start.AddHours(1));
        }
        catch
        {
            return (null, null);
        }
    }

    private static HashSet<string> LaadVerwerkt()
    {
        try
        {
            if (File.Exists(StatusFile) &&
                JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatusFile)) is { } ids)
            {
                return ids;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen (hooguit één dubbele verwerking).
        }
        return new HashSet<string>();
    }

    private static void BewaarVerwerkt(HashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFile)!);
        File.WriteAllText(StatusFile, JsonSerializer.Serialize(ids));
    }
}
