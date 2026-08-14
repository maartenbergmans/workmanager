using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Bewaakt de Cellaware data-checkpagina's (nemijtek en vriesveem): staat er één check op
/// "Niet OK", dan komt er een taak met hoge prioriteit — met de foutdetails erbij en de
/// pagina als link. Staat alles weer op OK, dan wordt de taak automatisch afgevinkt.
/// Draait hooguit één keer per 6 uur, meeliftend op de takenverversing.
/// </summary>
public static class DataCheckRadar
{
    public const string TaakPrefix = "Cellaware data-check ";

    private static readonly (string Naam, string Url)[] Sites =
    {
        ("nemijtek", "https://cellaware.nemijtek.nl/data-check"),
        ("vriesveem", "https://cellaware.vriesveem.nl/data-check"),
    };

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "datacheck.json");

    private static bool _bezig;

    /// <summary>
    /// Alleen buiten de werkuren checken: doordeweeks pas na 18:00, in het weekend de hele
    /// dag. Overdag draait het magazijn volop en zijn tussentijdse verschillen normaal.
    /// </summary>
    private static bool BinnenVenster(DateTime nu) =>
        nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || nu.Hour >= 18;

    /// <summary>Voert de check uit (met intervalbewaking). True = de takenlijst is gewijzigd.</summary>
    public static async Task<bool> ZorgVoorAsync(CancellationToken ct)
    {
        if (_bezig || !BinnenVenster(DateTime.Now))
        {
            return false;
        }
        if (LaatsteRun() is { } vorige && DateTimeOffset.Now - vorige < Interval)
        {
            return false;
        }
        _bezig = true;
        try
        {
            BewaarRun(DateTimeOffset.Now); // ook bij fouten: volgende poging pas over 6 uur

            var gewijzigd = false;
            foreach (var (naam, url) in Sites)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var html = await Http.GetStringAsync(url, ct);
                    var fouten = LeesFouten(html);
                    gewijzigd |= fouten.Count > 0
                        ? ZetTaak(naam, url, fouten)
                        : VinkAf(naam);
                }
                catch (HttpRequestException)
                {
                    // Site even niet bereikbaar: geen conclusie trekken, volgende ronde opnieuw.
                }
                catch (TaskCanceledException)
                {
                    // Timeout: idem.
                }
            }
            return gewijzigd;
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>De rode checks plus hun detailregels (de divs zonder kleur eronder).</summary>
    private static List<string> LeesFouten(string html)
    {
        var fouten = new List<string>();
        foreach (Match m in Regex.Matches(html,
            @"<div style=""color: red;"">(?<check>[^<]+)</div>(?<details>(\s*<div>[^<]+</div>)*)",
            RegexOptions.IgnoreCase))
        {
            var regel = System.Net.WebUtility.HtmlDecode(m.Groups["check"].Value.Trim());
            var details = Regex.Matches(m.Groups["details"].Value, @"<div>([^<]+)</div>")
                .Select(d => System.Net.WebUtility.HtmlDecode(d.Groups[1].Value.Trim()))
                .Where(d => d.Length > 0)
                .ToList();
            fouten.Add(details.Count > 0 ? $"{regel}\n  {string.Join("\n  ", details)}" : regel);
        }
        return fouten;
    }

    /// <summary>Zet (of ververst) de taak voor deze site. True = takenlijst gewijzigd.</summary>
    private static bool ZetTaak(string naam, string url, List<string> fouten)
    {
        var tekst = $"{TaakPrefix}{naam}: {fouten.Count} check{(fouten.Count == 1 ? "" : "s")} niet OK";
        var data = MijnTaakStore.Load();
        var bestaande = data.Taken.FirstOrDefault(t => !t.Klaar &&
            t.Tekst.StartsWith(TaakPrefix + naam, StringComparison.OrdinalIgnoreCase));
        if (bestaande is not null && bestaande.Tekst == tekst)
        {
            // Zelfde stand; alleen de details verversen.
            if (bestaande.Mail is { } mail)
            {
                mail.Tekst = string.Join("\n\n", fouten);
            }
            MijnTaakStore.Save(data);
            return false;
        }
        // Oude versie (ander aantal fouten) vervangen door de actuele.
        data.Taken.RemoveAll(t => !t.Klaar &&
            t.Tekst.StartsWith(TaakPrefix + naam, StringComparison.OrdinalIgnoreCase));
        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "Urban IT",
            Prioriteit = 0, // hoog: datafouten in een draaiend magazijn wachten niet
            Deadline = DateOnly.FromDateTime(DateTime.Now),
            Mail = new TaakMail
            {
                Onderwerp = tekst,
                Tekst = string.Join("\n\n", fouten),
                Link = url, // "Bron openen" springt rechtstreeks naar de checkpagina
            },
        });
        MijnTaakStore.Save(data);
        return true;
    }

    /// <summary>Alles weer OK: de open taak voor deze site automatisch afvinken.</summary>
    private static bool VinkAf(string naam)
    {
        var data = MijnTaakStore.Load();
        var open = data.Taken.Where(t => !t.Klaar &&
            t.Tekst.StartsWith(TaakPrefix + naam, StringComparison.OrdinalIgnoreCase)).ToList();
        if (open.Count == 0)
        {
            return false;
        }
        foreach (var t in open)
        {
            t.Klaar = true;
            t.KlaarOp = DateTimeOffset.Now;
        }
        MijnTaakStore.Save(data);
        return true;
    }

    private static DateTimeOffset? LaatsteRun()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<DateTimeOffset?>(File.ReadAllText(StateFile)) is { } t)
            {
                return t;
            }
        }
        catch
        {
            // Onleesbaar: als "nog nooit" behandelen.
        }
        return null;
    }

    private static void BewaarRun(DateTimeOffset moment)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize((DateTimeOffset?)moment));
        }
        catch
        {
            // Best effort.
        }
    }
}
