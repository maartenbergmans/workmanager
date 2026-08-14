using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Storingsmails van IT-support (onderwerp bevat "MailMobility" of "MailProperty"): de mails
/// zelf worden automatisch gearchiveerd, maar er komt één rode taak met hoogste prioriteit in
/// de lijst zolang de storing loopt. Blijft het 20 minuten stil (geen nieuwe mail), dan wordt
/// de taak automatisch weer afgevinkt. Komt er daarna tóch weer een mail, dan verschijnt een
/// nieuwe taak. De laatste mailtijd per trefwoord staat in alarm-mails.json.
/// </summary>
public static class AlarmMails
{
    private sealed record Regel(string Trefwoord, string Afzender);

    private static readonly Regel[] Regels =
    {
        new("MailMobility", "it-support"),
        new("MailProperty", "it-support"),
    };

    /// <summary>Na zoveel minuten zonder nieuwe mail is de storing voorbij.</summary>
    private const int StilNaMinuten = 20;

    public const string TaakPrefix = "🔴 Storing ";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "alarm-mails.json");

    /// <summary>Is dit zo'n storingsmail (en dus: archiveren + taak)?</summary>
    public static bool Matcht(MailBericht m) => RegelVoor(m) is not null;

    private static Regel? RegelVoor(MailBericht m) => Regels.FirstOrDefault(r =>
        m.Onderwerp.Contains(r.Trefwoord, StringComparison.OrdinalIgnoreCase) &&
        (m.Van.Contains(r.Afzender, StringComparison.OrdinalIgnoreCase) ||
         m.VanAdres.Contains(r.Afzender, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Registreert binnengekomen storingsmails: werkt de laatste-mailtijd per trefwoord bij en
    /// zet (of vernieuwt) de rode taak. Aanroepen vóór het archiveren, dan maakt een mislukte
    /// archivering niets uit voor de bewaking.
    /// </summary>
    public static void Registreer(IEnumerable<MailBericht> mails)
    {
        var perTrefwoord = mails
            .Select(m => (Mail: m, Regel: RegelVoor(m)))
            .Where(x => x.Regel is not null)
            .GroupBy(x => x.Regel!.Trefwoord, StringComparer.OrdinalIgnoreCase);
        var state = Laad();
        var stateGewijzigd = false;
        foreach (var groep in perTrefwoord)
        {
            // De mailtijd zelf gebruiken (nauwkeuriger dan "nu" bij een poll om de 5 min),
            // maar nooit terug in de tijd.
            var nieuwste = groep.Max(x => x.Mail.Datum);
            if (nieuwste > DateTimeOffset.Now)
            {
                nieuwste = DateTimeOffset.Now;
            }
            if (!state.TryGetValue(groep.Key, out var bekend) || nieuwste > bekend)
            {
                state[groep.Key] = nieuwste;
                stateGewijzigd = true;
            }
            ZorgVoorTaak(groep.Key);
        }
        if (stateGewijzigd)
        {
            Bewaar(state);
        }
    }

    /// <summary>
    /// Vinkt storingstaken automatisch af zodra het 20 minuten stil is. True = er is iets
    /// afgevinkt (de takenlijst mag verversen).
    /// </summary>
    public static bool VinkStilleAf()
    {
        var state = Laad();
        var data = MijnTaakStore.Load();
        var afgevinkt = false;
        foreach (var taak in data.Taken.Where(t => !t.Klaar &&
                     t.Tekst.StartsWith(TaakPrefix, StringComparison.Ordinal)))
        {
            var trefwoord = Regels.FirstOrDefault(r =>
                taak.Tekst.Contains(r.Trefwoord, StringComparison.OrdinalIgnoreCase))?.Trefwoord;
            if (trefwoord is null)
            {
                continue;
            }
            if (!state.TryGetValue(trefwoord, out var laatste) ||
                DateTimeOffset.Now - laatste >= TimeSpan.FromMinutes(StilNaMinuten))
            {
                taak.Klaar = true;
                taak.KlaarOp = DateTimeOffset.Now;
                afgevinkt = true;
            }
        }
        if (afgevinkt)
        {
            MijnTaakStore.Save(data);
        }
        return afgevinkt;
    }

    /// <summary>Zet de rode taak klaar als er nog geen open exemplaar voor dit trefwoord is.</summary>
    private static void ZorgVoorTaak(string trefwoord)
    {
        var data = MijnTaakStore.Load();
        var tekst = $"{TaakPrefix}{trefwoord} — storingsmails van IT-support";
        if (data.Taken.Any(t => !t.Klaar &&
                t.Tekst.StartsWith(TaakPrefix + trefwoord, StringComparison.OrdinalIgnoreCase)))
        {
            return; // loopt al
        }
        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "CED",
            Prioriteit = 0, // hoogste prioriteit: rood in alle lijsten
            Deadline = DateOnly.FromDateTime(DateTime.Now),
        });
        MijnTaakStore.Save(data);
    }

    private static Dictionary<string, DateTimeOffset> Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                    File.ReadAllText(StateFile)) is { } s)
            {
                return new Dictionary<string, DateTimeOffset>(s, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Bewaar(Dictionary<string, DateTimeOffset> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
