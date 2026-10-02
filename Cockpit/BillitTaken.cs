using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Leveranciersfacturen die per mail binnenkomen (zie <see cref="BillitRegels"/>) krijgen
/// automatisch één taak in de cockpit. Doorsturen gebeurt bewust niet vanzelf: pas als de
/// taak aangeklikt wordt gaat de factuurbijlage naar het Billit-inboxadres — zo zie je
/// eerst wát er weggaat. Per mail wordt de taak maar één keer aangemaakt
/// (%APPDATA%\WorkManager\billit-taken.json).
/// </summary>
public static class BillitTaken
{
    /// <summary>Waaraan de cockpit deze taak herkent (dubbelklik = doorsturen).</summary>
    public const string TaakPrefix = "🧾 Factuur naar Billit:";

    private static readonly string StatusFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "billit-taken.json");

    /// <summary>Maakt (één keer per mail) een taak voor elke factuur in de lijst.</summary>
    public static void Verwerk(List<MailBericht> berichten)
    {
        var regels = BillitRegels.Load();
        if (regels.Count == 0)
        {
            return;
        }
        var verwerkt = LaadVerwerkt();
        var gewijzigd = false;
        foreach (var m in berichten.Where(b => !b.Genegeerd))
        {
            if (m.MessageId.Length == 0 || verwerkt.Contains(m.MessageId) ||
                BillitRegels.Match(m, regels) is not { } treffer)
            {
                continue;
            }
            var tekst = $"{TaakPrefix} {m.Van} — {m.Bijlagen[treffer.Index]}";
            var taken = MijnTaakStore.Load();
            if (!taken.Taken.Any(t => !t.Klaar &&
                t.Tekst.Equals(tekst, StringComparison.OrdinalIgnoreCase)))
            {
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = tekst,
                    // Bestaande categorie uit de takenlijst; "Administratie" bestaat daar niet.
                    Categorie = "Urban IT",
                    Prioriteit = 1,
                    Deadline = DateOnly.FromDateTime(DateTime.Now),
                    // Het bronbericht meebewaren: aanklikken toont de factuurmail, en het
                    // message-id is waarmee de doorstuuractie de mail terugvindt.
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusFile)!);
            File.WriteAllText(StatusFile, JsonSerializer.Serialize(ids));
        }
        catch
        {
            // Best effort: dan komt de taak hooguit nog een keer terug.
        }
    }
}
