using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De dagafsluiter: één keer per werkdag, vanaf 17:00, een korte terugblik op wat er af is —
/// afgevinkte taken, geboekte uren, afgehandelde berichten — plus wat er nog niet geboekt is.
/// Dat laatste is het punt: een vergeten uurtje is geld, en aan het einde van de dag weet je
/// nog waar het heen ging. De weekversie hiervan is <see cref="WeekDebriefing"/>.
///
/// <para>State in %APPDATA%\WorkManager\dag-afsluiter.json, zodat het bij één keer blijft.</para>
/// </summary>
public static class DagAfsluiter
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "dag-afsluiter.json");

    /// <summary>Wat er van de terugblik te melden valt.</summary>
    public sealed record Terugblik(string Kop, string Tekst, int OngeboekteMinuten);

    /// <summary>
    /// De terugblik voor vandaag, of null als hij nu niet aan de beurt is (te vroeg, weekend,
    /// of vandaag al getoond).
    /// </summary>
    public static Terugblik? Voorstel(List<AgendaClient.AgendaItem> meetingsVandaag)
    {
        try
        {
            var nu = DateTime.Now;
            if (nu.Hour < 17 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return null;
            }
            var vandaag = DateOnly.FromDateTime(nu);
            if (LaatsteDag() == vandaag.ToString("yyyy-MM-dd"))
            {
                return null;
            }
            BewaarDag(vandaag.ToString("yyyy-MM-dd"));

            var afgevinkt = MijnTaakStore.Load().Taken.Count(t =>
                t.Klaar && t.KlaarOp is { } klaar &&
                DateOnly.FromDateTime(klaar.LocalDateTime) == vandaag);
            var geboekt = TimesheetStore.Load().Where(r => r.Datum == vandaag).ToList();
            var minuten = geboekt.Sum(r => r.Minuten);

            // Meetings van vandaag waar geen boeking overheen valt: het echte werk van deze
            // melding. De gatendetector meldt zelf hooguit één keer per dag, dus hier rekenen
            // we het opnieuw uit in plaats van hem te verbruiken.
            var ongeboekt = meetingsVandaag
                .Where(m => !m.HeleDag && m.Einde <= nu &&
                            (m.Einde - m.Start).TotalMinutes >= 25 &&
                            !m.Titel.StartsWith("🍴", StringComparison.Ordinal) &&
                            !geboekt.Any(r => Overlapt(r, m, vandaag)))
                .ToList();
            var ongeboekteMinuten = ongeboekt.Sum(m => (int)(m.Einde - m.Start).TotalMinutes);

            var delen = new List<string>();
            delen.Add(afgevinkt switch
            {
                0 => "geen taken afgevinkt",
                1 => "1 taak afgevinkt",
                _ => $"{afgevinkt} taken afgevinkt",
            });
            delen.Add(minuten == 0
                ? "nog niets geboekt"
                : $"{minuten / 60}u{minuten % 60:00} geboekt");

            var tekst = string.Join(", ", delen) + ".";
            if (ongeboekt.Count > 0)
            {
                tekst += $"\nNog niet geboekt: {string.Join(", ", ongeboekt.Take(4)
                    .Select(m => $"{m.Start.ToLocalTime():HH:mm} {Kort(m.Titel, 32)}"))}" +
                    (ongeboekt.Count > 4 ? $" en nog {ongeboekt.Count - 4}" : "") +
                    $" — samen {ongeboekteMinuten / 60}u{ongeboekteMinuten % 60:00}.";
            }
            return new Terugblik(ThemaStem.DebriefingKop() + " — vandaag", tekst, ongeboekteMinuten);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Valt er een boeking over deze meeting heen? Zonder starttijd telt de dag mee.</summary>
    private static bool Overlapt(TimesheetRegel regel, AgendaClient.AgendaItem meeting, DateOnly dag)
    {
        if (regel.Datum != dag)
        {
            return false;
        }
        if (regel.Van is not { } van)
        {
            // Geen starttijd bekend: als de omschrijving de meeting noemt, tellen we hem mee.
            return regel.Omschrijving.Contains(meeting.Titel, StringComparison.OrdinalIgnoreCase);
        }
        var start = dag.ToDateTime(van);
        var einde = start.AddMinutes(regel.Minuten);
        return start < meeting.Einde.LocalDateTime && einde > meeting.Start.LocalDateTime;
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }

    private static string LaatsteDag()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(StateFile)) is { } data &&
                data.TryGetValue("dag", out var dag))
            {
                return dag;
            }
        }
        catch
        {
            // Onleesbaar: dan maar één keer extra.
        }
        return "";
    }

    private static void BewaarDag(string dag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(
                new Dictionary<string, string> { ["dag"] = dag }));
        }
        catch
        {
            // Best effort.
        }
    }
}
