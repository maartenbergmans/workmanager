using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De timesheet-gatendetector: vergelijkt aan het einde van de werkdag (vanaf 17:00) de
/// geboekte timesheets met de meetings van vandaag. Een voorbije, blokkerende meeting van
/// minstens 25 minuten waar geen boeking overheen valt, is vermoedelijk vergeten — dat is
/// letterlijk geld. Meldt hooguit één keer per dag; state in timesheet-gaten.json.
/// </summary>
public static class TimesheetGaten
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "timesheet-gaten.json");

    /// <summary>
    /// De meetings van vandaag die (nog) niet in de timesheets terug te vinden zijn.
    /// Leeg = niets te melden of vandaag al gemeld. Roept dit één keer per dag raak.
    /// </summary>
    public static List<AgendaClient.AgendaItem> Controleer(List<AgendaClient.AgendaItem> meetings)
    {
        var leeg = new List<AgendaClient.AgendaItem>();
        try
        {
            var nu = DateTime.Now;
            if (nu.Hour < 17 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return leeg;
            }
            var vandaag = DateOnly.FromDateTime(nu);
            if (GemeldeDag() == vandaag.ToString("yyyy-MM-dd"))
            {
                return leeg;
            }

            var geboekt = TimesheetStore.Load().Where(r => r.Datum == vandaag).ToList();
            bool Gedekt(AgendaClient.AgendaItem m)
            {
                var mStart = m.Start.LocalDateTime.TimeOfDay;
                var mEind = m.Einde.LocalDateTime.TimeOfDay;
                return geboekt.Any(g =>
                {
                    var s = (g.Van ?? new TimeOnly(9, 0)).ToTimeSpan();
                    var e = s + TimeSpan.FromMinutes(g.Minuten);
                    return s < mEind && e > mStart; // elke overlap telt als "geboekt"
                });
            }

            var missend = meetings
                .Where(m => !m.HeleDag && m.Einde <= DateTimeOffset.Now &&
                            DateOnly.FromDateTime(m.Start.LocalDateTime) == vandaag &&
                            (m.Einde - m.Start).TotalMinutes >= 25 &&
                            !DagPlan.KanDoorwerken(m) && !Gedekt(m))
                .OrderBy(m => m.Start)
                .ToList();
            if (missend.Count > 0)
            {
                BewaarGemeld(vandaag);
            }
            return missend;
        }
        catch
        {
            return leeg;
        }
    }

    private static string GemeldeDag()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void BewaarGemeld(DateOnly dag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile,
                JsonSerializer.Serialize(dag.ToString("yyyy-MM-dd")));
        }
        catch
        {
            // Best effort: hooguit meldt hij morgen nog een keer.
        }
    }
}
