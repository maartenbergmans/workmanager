using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Gemiste dagen inhalen: werkdagen van de voorbije twee weken met veel activiteit maar
/// (bijna) niets geboekt krijgen vanaf 5 u 's ochtends alvast een dagvoorstel in de
/// <see cref="DagvoorstelCache"/> (hoogstens drie per ochtend: elke run is een Claude-run
/// van enkele minuten). Daarna één melding; een klik opent het voorstel van de oudste dag,
/// en via de dagwissel in het venster staan de andere meteen klaar.
/// <para>Aanleiding: op 2, 10 en 11 september 2026 samen ±24 u actief en 0 geboekt.</para>
/// <para>State: %APPDATA%\WorkManager\uren-inhaler.json.</para>
/// </summary>
public static class UrenInhaler
{
    private const int MinActief = 240;       // minuten actief voor een dag telt
    private const double MaxGeboektDeel = 0.4;
    private const int TerugDagen = 14;
    private const int MaxPerRun = 3;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "uren-inhaler.json");

    private sealed class State
    {
        public string LaatsteRun { get; set; } = "";
        public string LaatsteMelding { get; set; } = "";
        public List<string> Leeg { get; set; } = new();   // dagen zonder bruikbaar voorstel
    }

    private static bool _bezig;

    public sealed record GemisteDag(DateOnly Dag, int Actief, int Geboekt);

    /// <summary>Werkdagen met veel activiteit en (bijna) niets geboekt, oudste eerst.</summary>
    public static List<GemisteDag> Kandidaten()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var geboekt = TimesheetStore.Load()
            .Where(r => r.Datum >= vandaag.AddDays(-TerugDagen))
            .GroupBy(r => r.Datum).ToDictionary(g => g.Key, g => g.Sum(r => r.Minuten));
        return Werkjournaal.Dagen(TerugDagen + 1)
            .Select(d => (Dag: DateOnly.Parse(d.Datum), d.ActiefMinuten))
            .Where(d => d.Dag < vandaag && d.Dag.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Where(d => d.ActiefMinuten >= MinActief)
            .Select(d => new GemisteDag(d.Dag, d.ActiefMinuten, geboekt.GetValueOrDefault(d.Dag)))
            .Where(g => g.Geboekt < g.Actief * MaxGeboektDeel)
            .OrderBy(g => g.Dag)
            .ToList();
    }

    /// <summary>
    /// Vanuit de 10-minutentik (UI-thread): vanaf 5 u één run per dag die ontbrekende
    /// voorstellen maakt; daarna — vanaf 8 u, als je achter de pc zit — één melding.
    /// </summary>
    public static async Task ZorgVoorAsync(Action<DateOnly> openVoorstel)
    {
        var nu = DateTime.Now;
        if (_bezig || nu.Hour < 5)
        {
            return;
        }
        var state = Laad();
        var vandaag = DateOnly.FromDateTime(nu).ToString("yyyy-MM-dd");
        _bezig = true;
        try
        {
            if (state.LaatsteRun != vandaag)
            {
                var todo = Kandidaten()
                    .Where(g => DagvoorstelCache.Laad(g.Dag) is null && !state.Leeg.Contains(g.Dag.ToString("yyyy-MM-dd")))
                    .OrderByDescending(g => g.Dag) // recentste eerst: die sporen zijn het rijkst
                    .Take(MaxPerRun).ToList();
                foreach (var gemist in todo)
                {
                    try
                    {
                        using var afbreken = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                        var (regels, toelichting) = await ActiviteitenLog.VoorstelAsync(
                            gemist.Dag, MeetingsUitJournaal(gemist.Dag), afbreken.Token);
                        if (regels.Count > 0)
                        {
                            DagvoorstelCache.Bewaar(gemist.Dag, regels,
                                $"Klaargezet door de uren-inhaler: {gemist.Actief / 60.0:0.#} u actief, " +
                                $"{gemist.Geboekt / 60.0:0.#} u geboekt. " + toelichting);
                        }
                        else
                        {
                            state.Leeg.Add(gemist.Dag.ToString("yyyy-MM-dd"));
                        }
                    }
                    catch
                    {
                        // Claude of mail hapert: morgen opnieuw.
                    }
                }
                state.LaatsteRun = vandaag;
                if (state.Leeg.Count > 60)
                {
                    state.Leeg.RemoveRange(0, state.Leeg.Count - 60);
                }
                Bewaar(state);
            }

            if (state.LaatsteMelding == vandaag || nu.Hour < 8 || nu.Hour >= 19 || NietStoren.Actief)
            {
                return;
            }
            var klaar = Kandidaten().Where(g => DagvoorstelCache.Laad(g.Dag) is not null).ToList();
            if (klaar.Count == 0)
            {
                return;
            }
            state.LaatsteMelding = vandaag;
            Bewaar(state);
            var nl = new CultureInfo("nl-BE");
            var lijst = string.Join(", ", klaar.Take(4).Select(g =>
                $"{g.Dag.ToString("dddd d/M", nl)} ({g.Actief / 60.0:0.#} u actief, {g.Geboekt / 60.0:0.#} u geboekt)"));
            var totaal = klaar.Sum(g => g.Actief - g.Geboekt);
            TrayMelding.Toon($"⏰ Uren inhalen: ±{totaal / 60} u nog niet geboekt",
                $"{lijst}. Het voorstel staat klaar; klik om te beginnen bij de oudste dag.",
                () => openVoorstel(klaar[0].Dag), 20000);
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>De meetings van die dag zoals het werkjournaal ze vastlegde ("HH:mm-HH:mm titel").</summary>
    private static List<AgendaClient.AgendaItem> MeetingsUitJournaal(DateOnly dag)
    {
        var lijst = new List<AgendaClient.AgendaItem>();
        if (Werkjournaal.Dag(dag) is not { } samenvatting)
        {
            return lijst;
        }
        foreach (var regel in samenvatting.Meetings)
        {
            var m = Regex.Match(regel, @"^(\d\d):(\d\d)-(\d\d):(\d\d) (.+)$");
            if (!m.Success)
            {
                continue;
            }
            var basis = dag.ToDateTime(TimeOnly.MinValue);
            var start = new DateTimeOffset(basis.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)));
            var einde = new DateTimeOffset(basis.AddHours(int.Parse(m.Groups[3].Value)).AddMinutes(int.Parse(m.Groups[4].Value)));
            lijst.Add(new AgendaClient.AgendaItem(start, einde, false, m.Groups[5].Value));
        }
        return lijst;
    }

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
