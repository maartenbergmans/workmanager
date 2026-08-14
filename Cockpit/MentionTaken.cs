using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// @maarten-mentions in Teams-chats en CED-mails: het bericht kleurt direct rood (urgent)
/// en er verschijnt automatisch een taak om te reageren. Per bericht wordt de taak maar
/// één keer aangemaakt (%APPDATA%\WorkManager\mention-taken.json); de rode kleur blijft
/// zolang de mention in de lijst staat.
/// </summary>
public static class MentionTaken
{
    private static readonly string StatusFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "mention-taken.json");

    private static readonly Regex MentionRegex = new(
        @"@\s?maarten", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Waar in een reply de geciteerde historie begint: Outlook-kopjes ("Van: …",
    // "-----Oorspronkelijk bericht-----", lange lijnen), Gmail-stijl ("Op … schreef …:"),
    // klassieke >-citaten en de eigen historiekop van de cockpitweergave.
    private static readonly Regex[] HistorieMarkers =
    {
        new(@"^\s*(Van|From|De)\s*:\s", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*-{2,}\s*(Oorspronkelijk bericht|Original Message|Message d'origine)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*(Op|On|Le)\s.{5,120}(schreef|wrote|a écrit)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*>", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"_{10,}", RegexOptions.Compiled),
        new(@"Eerdere berichten", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Alleen het nieuwe (eigen) deel van het bericht: alles vanaf de eerste citatie- of
    /// forwardmarker valt weg. Een @mention in de historie van een reply telt dus niet.
    /// </summary>
    private static string EigenDeel(string tekst)
    {
        var knip = tekst.Length;
        foreach (var marker in HistorieMarkers)
        {
            if (marker.Match(tekst) is { Success: true } m && m.Index < knip)
            {
                knip = m.Index;
            }
        }
        return tekst[..knip];
    }

    /// <summary>Markeert mentions als urgent en maakt er (één keer) een reageer-taak voor aan.</summary>
    public static void Verwerk(List<MailBericht> berichten)
    {
        var kandidaten = berichten.Where(m =>
            (m.TeamsChat.Length > 0 || m.OutlookMail.Length > 0) &&
            MentionRegex.IsMatch(m.Onderwerp + "\n" + EigenDeel(m.Tekst))).ToList();
        if (kandidaten.Count == 0)
        {
            return;
        }

        var verwerkt = LaadVerwerkt();
        var gewijzigd = false;
        foreach (var m in kandidaten)
        {
            m.Urgent = true; // elke beurt opnieuw: rood zolang de mention zichtbaar is

            if (m.MessageId.Length == 0 || verwerkt.Contains(m.MessageId))
            {
                continue;
            }
            var bron = m.TeamsChat.Length > 0 ? "Teams" : "CED-mail";
            var tekst = $"Reageren op {m.Van} ({bron}, @mention)";
            var taken = MijnTaakStore.Load();
            if (!taken.Taken.Any(t => !t.Klaar &&
                t.Tekst.Equals(tekst, StringComparison.OrdinalIgnoreCase)))
            {
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = tekst,
                    Categorie = "Werk",
                    Prioriteit = 1,
                    Deadline = DateOnly.FromDateTime(DateTime.Now),
                    // Het bronbericht meebewaren: aanklikken van de taak toont dan meteen
                    // de mail of chat waarin de mention stond (ToonTaakMail).
                    Mail = new TaakMail
                    {
                        Van = m.Van,
                        VanAdres = m.VanAdres,
                        AntwoordAan = m.AntwoordAan,
                        Onderwerp = m.Onderwerp,
                        Tekst = m.Tekst.Length > 8000 ? m.Tekst[..8000] + "…" : m.Tekst,
                        Link = CockpitForm.BerichtUrl(m),
                        Datum = m.Datum,
                        MessageId = m.MessageId,
                        Referenties = m.Referenties.ToList(),
                        ChatSpace = m.ChatSpace,
                        WhatsAppChat = m.WhatsAppChat,
                    },
                });
                MijnTaakStore.Save(taken);
            }
            verwerkt.Add(m.MessageId);
            gewijzigd = true;
        }
        if (gewijzigd)
        {
            BewaarVerwerkt(verwerkt);
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
            // Onleesbaar: opnieuw beginnen (hooguit één dubbele taak).
        }
        return new HashSet<string>();
    }

    private static void BewaarVerwerkt(HashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFile)!);
        File.WriteAllText(StatusFile, JsonSerializer.Serialize(ids));
    }
}
