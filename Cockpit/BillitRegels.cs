using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Eén regel die zegt welke inkomende mails een leveranciersfactuur zijn en welke bijlage
/// daarvan naar Billit moet. Afzender en onderwerp matchen op "bevat" (hoofdletter-
/// ongevoelig, net als bij <see cref="ArchiveerRegel"/>); het bijlagepatroon gebruikt *
/// als jokerteken ("Invoice-*.pdf"), zodat het betalingsbewijs naast de factuur blijft
/// liggen.
/// </summary>
public sealed class BillitRegel
{
    public string Afzender { get; set; } = ""; // leeg = elke afzender
    public string Onderwerp { get; set; } = ""; // leeg = elk onderwerp

    /// <summary>Welke bijlage doorgestuurd wordt; * is jokerteken. Leeg = elke PDF.</summary>
    public string Bijlage { get; set; } = "*.pdf";

    /// <summary>Matcht de mail zelf (nog zonder naar de bijlagen te kijken)?</summary>
    public bool MatchtMail(MailBericht m)
    {
        if (Afzender.Length == 0 && Onderwerp.Length == 0)
        {
            return false; // lege regel matcht bewust niets
        }
        var afzenderOk = Afzender.Length == 0 ||
            m.Van.Contains(Afzender, StringComparison.OrdinalIgnoreCase) ||
            m.VanAdres.Contains(Afzender, StringComparison.OrdinalIgnoreCase);
        var onderwerpOk = Onderwerp.Length == 0 ||
            m.Onderwerp.Contains(Onderwerp, StringComparison.OrdinalIgnoreCase);
        return afzenderOk && onderwerpOk;
    }

    /// <summary>
    /// De index van de bijlage die op het patroon past, of -1. De index loopt 1-op-1 met
    /// <see cref="MailBericht.Bijlagen"/> én met de bijlagen van de originele mail, wat
    /// precies is wat <see cref="GmailClient.DoorsturenAsync"/> verwacht.
    /// </summary>
    public int BijlageIndex(MailBericht m)
    {
        var patroon = Bijlage.Trim().Length > 0 ? Bijlage.Trim() : "*.pdf";
        var regex = new Regex(
            "^" + Regex.Escape(patroon).Replace("\\*", ".*") + "$",
            RegexOptions.IgnoreCase);
        return m.Bijlagen.FindIndex(naam => regex.IsMatch(naam.Trim()));
    }

    public override string ToString() =>
        (Afzender.Length > 0 ? $"van \"{Afzender}\"" : "") +
        (Afzender.Length > 0 && Onderwerp.Length > 0 ? "  én  " : "") +
        (Onderwerp.Length > 0 ? $"onderwerp bevat \"{Onderwerp}\"" : "") +
        $"  →  bijlage \"{Bijlage}\"";
}

/// <summary>
/// De regels voor "factuur naar Billit", in %APPDATA%\WorkManagerillit-regels.json.
/// Zelfde opzet als <see cref="ArchiveerRegels"/>, maar dan sturend in plaats van
/// opruimend: een match levert een taak in de cockpit op die met één klik de factuur naar
/// het Billit-inboxadres stuurt. Bestaat het bestand nog niet, dan wordt het met de
/// Anthropic-regel aangemaakt — dan is meteen zichtbaar hoe een regel eruitziet.
/// </summary>
public static class BillitRegels
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "billit-regels.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// De Claude-abonnementsfactuur komt elke maand van Stripe namens Anthropic, met twee
    /// PDF's: de factuur (Invoice-*.pdf) en het betalingsbewijs (Receipt-*.pdf). Alleen de
    /// eerste hoort in de boekhouding.
    /// </summary>
    private static List<BillitRegel> Standaard() => new()
    {
        new BillitRegel
        {
            Afzender = "invoice+statements@mail.anthropic.com",
            Onderwerp = "receipt from Anthropic",
            Bijlage = "Invoice-*.pdf",
        },
    };

    public static List<BillitRegel> Load()
    {
        try
        {
            if (File.Exists(Bestand))
            {
                if (JsonSerializer.Deserialize<List<BillitRegel>>(
                        File.ReadAllText(Bestand), JsonOpts) is { } r)
                {
                    return r;
                }
            }
            else
            {
                var standaard = Standaard();
                Save(standaard);
                return standaard;
            }
        }
        catch
        {
            // Onleesbaar: zonder regels verder — nooit ongevraagd facturen versturen.
        }
        return new List<BillitRegel>();
    }

    public static void Save(List<BillitRegel> regels)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(regels, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// De eerste regel die op deze mail past én waarvan de bijlage er ook echt in zit,
    /// met de index van die bijlage. Null als de mail geen factuur voor Billit is.
    /// </summary>
    public static (BillitRegel Regel, int Index)? Match(MailBericht m, List<BillitRegel> regels)
    {
        // Alleen echte Gmail-mails: chats, CED-Outlook en Smartschool hebben geen Uid en
        // dus ook geen bijlagen die via IMAP door te sturen zijn.
        if (m.IsChat || m.Uid == 0 || m.Bijlagen.Count == 0)
        {
            return null;
        }
        foreach (var regel in regels.Where(r => r.MatchtMail(m)))
        {
            if (regel.BijlageIndex(m) is >= 0 and var index)
            {
                return (regel, index);
            }
        }
        return null;
    }
}
