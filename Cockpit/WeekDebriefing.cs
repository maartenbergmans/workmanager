using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De weekafsluiter: op vrijdag vanaf 16:00 één keer een korte terugblik — hoeveel taken je
/// deze week afvinkte, hoeveel uur er geboekt is en hoe vaak je inbox zero haalde. De kop
/// komt uit de stem van het kleurenschema ("Debriefing, 007"). Eén keer per week, bewaard in
/// %APPDATA%\WorkManager\week-debriefing.json.
/// </summary>
public static class WeekDebriefing
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "week-debriefing.json");

    /// <summary>De terugblik voor deze week, of null als hij nu niet aan de beurt is.</summary>
    public static string? Voorstel()
    {
        try
        {
            var nu = DateTime.Now;
            if (nu.DayOfWeek != DayOfWeek.Friday || nu.Hour < 16)
            {
                return null;
            }
            var week = System.Globalization.ISOWeek.GetWeekOfYear(nu);
            var sleutel = $"{nu.Year}-W{week:00}";
            if (LaatsteWeek() == sleutel)
            {
                return null;
            }

            // Maandag van deze week als startpunt.
            var maandag = DateOnly.FromDateTime(nu.AddDays(
                -(((int)nu.DayOfWeek + 6) % 7)));
            var afgevinkt = MijnTaakStore.Load().Taken.Count(t =>
                t.Klaar && t.KlaarOp is { } klaar &&
                DateOnly.FromDateTime(klaar.LocalDateTime) >= maandag);
            var minuten = TimesheetStore.Load()
                .Where(r => r.Datum >= maandag)
                .Sum(r => r.Minuten);
            var streak = InboxZeroReeks.Huidig();

            BewaarWeek(sleutel);
            var delen = new List<string>();
            if (afgevinkt > 0)
            {
                delen.Add($"{afgevinkt} {(afgevinkt == 1 ? "taak" : "taken")} afgewerkt");
            }
            if (minuten > 0)
            {
                delen.Add($"{minuten / 60}u{minuten % 60:00} geboekt");
            }
            if (streak > 0)
            {
                delen.Add($"{InboxZeroReeks.Plant(streak)} {streak} dagen inbox zero");
            }
            return delen.Count == 0
                ? $"{ThemaStem.DebriefingKop()}: rustige week — fijn weekend."
                : $"{ThemaStem.DebriefingKop()}: {string.Join(" · ", delen)}.";
        }
        catch
        {
            return null; // een terugblik mag nooit iets breken
        }
    }

    private static string LaatsteWeek()
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

    private static void BewaarWeek(string sleutel)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(sleutel));
        }
        catch
        {
            // Best effort: hooguit verschijnt hij nog een keer.
        }
    }
}
