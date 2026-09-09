using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Terugkerende maaitaak: om de ~3 weken verschijnt "Gras maaien" in "Mijn taken", maar
/// alleen in het maaiseizoen (15 maart t/m 31 oktober). Het interval schuift mee met het
/// weer van de voorbije twee weken (Open-Meteo, zelfde bron als de briefing): warm én nat
/// gras groeit snel (2 weken), koud of kurkdroog gras nauwelijks (4 weken). De klok start
/// telkens bij het afvinken van de vorige maaitaak; bij de allereerste run geldt "vandaag
/// gemaaid" als vertrekpunt. Zonder thuiscoördinaten (of zonder internet) blijft het
/// gewoon om de 3 weken.
/// De taak komt bovendien pas op een dag waarop maaien ook echt kan: vandaag én morgen
/// (zo goed als) droog, en op beide dagen geen overvolle agenda. Is dat niet zo, dan
/// schuift de beslissing gewoon een dag op — de dagelijkse check probeert het morgen
/// opnieuw tot er een geschikt venster is.
/// </summary>
public static class GrasTaken
{
    public const string TaakTekst = "Gras maaien";

    /// <summary>Maaiseizoen: buiten deze periode groeit er niets en komt er geen taak.</summary>
    private static readonly DateOnly SeizoenStart = new(2000, 3, 15);
    private static readonly DateOnly SeizoenEinde = new(2000, 10, 31);

    private const int SnelInterval = 14;      // warm + nat: het gras staat zo weer hoog
    private const int StandaardInterval = 21; // de gevraagde 3 weken
    private const int TraagInterval = 28;     // koud of droog: het gras groeit amper

    /// <summary>Zoveel dagen weer kijken we terug om de groeisnelheid in te schatten.</summary>
    private const int WeerDagen = 14;

    /// <summary>Tot zoveel mm verwachte neerslag telt een dag nog als droog genoeg om te maaien.</summary>
    private const double MaxNeerslagMm = 2.0;

    /// <summary>Vanaf zoveel uur afspraken op een dag is de agenda te vol om ook nog te maaien.</summary>
    private const double VolleAgendaUren = 6;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "gras-maaien.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class State
    {
        public string LaatstGemaaid { get; set; } = ""; // yyyy-MM-dd van de laatste maaibeurt
        public bool TaakStaatOpen { get; set; }         // er staat al een maaitaak in de lijst
        public string LaatsteCheck { get; set; } = "";  // dag waarop al beslist is (met weer)
    }

    /// <summary>
    /// Periodiek aangeroepen vanuit de tray-timer. Het afvinken wordt bij elke ronde
    /// gedetecteerd (goedkoop, lokaal bestand); de beslissing of er een nieuwe taak moet
    /// komen — met eventueel een weerraadpleging — valt hooguit één keer per dag.
    /// </summary>
    public static void ZorgVoorMaaitaak()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var state = LoadState();

        if (state.LaatstGemaaid == "")
        {
            // Eerste run ooit: neem aan dat er recent gemaaid is, anders zou de taak
            // meteen na de deploy opduiken terwijl het gras net kort staat.
            state.LaatstGemaaid = vandaag.ToString("yyyy-MM-dd");
            SaveState(state);
        }

        if (state.TaakStaatOpen)
        {
            var taken = MijnTaakStore.Load().Taken;
            if (taken.Any(t => !t.Klaar && t.Tekst.StartsWith(TaakTekst, StringComparison.OrdinalIgnoreCase)))
            {
                return; // taak staat nog open, klok loopt pas na het afvinken
            }
            // Afgevinkt (of opgeruimd): de klok herstart op de afvinkdag.
            var klaar = taken
                .Where(t => t.Klaar && t.Tekst.StartsWith(TaakTekst, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.KlaarOp)
                .FirstOrDefault();
            var gemaaid = klaar?.KlaarOp is { } op ? DateOnly.FromDateTime(op.LocalDateTime) : vandaag;
            state.LaatstGemaaid = gemaaid.ToString("yyyy-MM-dd");
            state.TaakStaatOpen = false;
            state.LaatsteCheck = "";
            SaveState(state);
        }

        var sleutel = vandaag.ToString("yyyy-MM-dd");
        if (state.LaatsteCheck == sleutel || !InSeizoen(vandaag))
        {
            return;
        }
        if (!DateOnly.TryParse(state.LaatstGemaaid, out var laatst))
        {
            return;
        }
        var dagen = vandaag.DayNumber - laatst.DayNumber;
        if (dagen < SnelInterval)
        {
            return; // zelfs bij topgroei nog niet aan de beurt; weer opvragen is dan zinloos
        }

        state.LaatsteCheck = sleutel;
        SaveState(state);
        _ = MaakIndienNodigAsync(vandaag, dagen);
    }

    private static bool InSeizoen(DateOnly dag)
    {
        var d = new DateOnly(2000, dag.Month, dag.Day == 29 && dag.Month == 2 ? 28 : dag.Day);
        return d >= SeizoenStart && d <= SeizoenEinde;
    }

    /// <summary>
    /// Bepaalt (op de achtergrond, zodat de tray-timer niet op internet wacht) het interval
    /// op basis van het recente weer en zet de taak klaar zodra dat interval verstreken is
    /// én er een geschikt maaivenster is (droog + agenda met ruimte, vandaag en morgen).
    /// </summary>
    private static async Task MaakIndienNodigAsync(DateOnly vandaag, int dagen)
    {
        // Boven het traagste interval is het weer niet meer relevant: maaien sowieso.
        var interval = dagen >= TraagInterval ? TraagInterval : await IntervalVolgensWeerAsync();
        if (dagen < interval)
        {
            return;
        }
        if (!await DagIsGeschiktAsync(vandaag) || !await DagIsGeschiktAsync(vandaag.AddDays(1)))
        {
            return; // regen of te vol: schuift op, morgen kijkt de dagelijkse check opnieuw
        }

        var data = MijnTaakStore.Load();
        if (!data.Taken.Any(t => !t.Klaar && t.Tekst.StartsWith(TaakTekst, StringComparison.OrdinalIgnoreCase)))
        {
            data.Taken.Add(new MijnTaak
            {
                Tekst = $"{TaakTekst} (laatst {dagen / 7} weken geleden)",
                Categorie = "Privé",
                Prioriteit = 1,
                Deadline = vandaag.AddDays(1), // het gevalideerde venster: vandaag of morgen
            });
            MijnTaakStore.Save(data);
        }

        // State herladen: de timer kan intussen LaatsteCheck geschreven hebben.
        var state = LoadState();
        state.TaakStaatOpen = true;
        SaveState(state);
    }

    /// <summary>
    /// Kan er op deze dag gemaaid worden? (Zo goed als) droog volgens de verwachting én een
    /// agenda met ruimte. Ontbrekende gegevens (geen coördinaten, geen internet, agenda even
    /// niet leesbaar) tellen als geschikt — anders zou de taak eindeloos blijven opschuiven.
    /// </summary>
    private static async Task<bool> DagIsGeschiktAsync(DateOnly dag) =>
        await DagIsDroogAsync(dag) && await AgendaHeeftRuimteAsync(dag);

    /// <summary>Hooguit <see cref="MaxNeerslagMm"/> mm verwachte neerslag op die dag.</summary>
    private static async Task<bool> DagIsDroogAsync(DateOnly dag)
    {
        var reis = ReisSettings.Load();
        if (!reis.HeeftThuis)
        {
            return true;
        }
        try
        {
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Inv(reis.ThuisLat)}&longitude={Inv(reis.ThuisLon)}" +
                      "&daily=precipitation_sum&timezone=Europe%2FBrussels&forecast_days=3";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var daily = doc.RootElement.GetProperty("daily");
            var dagen = daily.GetProperty("time").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToList();
            var regen = Reeks(daily, "precipitation_sum");
            var i = dagen.IndexOf(dag.ToString("yyyy-MM-dd"));
            return i < 0 || i >= regen.Count || regen[i] <= MaxNeerslagMm;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Minder dan <see cref="VolleAgendaUren"/> uur afspraken in de hoofdagenda.</summary>
    private static async Task<bool> AgendaHeeftRuimteAsync(DateOnly dag)
    {
        try
        {
            using var afbreken = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var uren = (await CalendarClient.ZoekInPeriodeAsync(dag, dag, afbreken.Token))
                .Sum(a => Math.Max(0, (a.Einde - a.Start).TotalHours));
            return uren < VolleAgendaUren;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Maai-interval volgens het weer van de voorbije twee weken op het thuisadres:
    /// gemiddeld ≥ 10° én ≥ 20 mm regen = snelle groei; onder 8° of onder 5 mm = trage
    /// groei. Geen coördinaten of geen antwoord: gewoon de standaard 3 weken.
    /// </summary>
    private static async Task<int> IntervalVolgensWeerAsync()
    {
        var reis = ReisSettings.Load();
        if (!reis.HeeftThuis)
        {
            return StandaardInterval;
        }
        try
        {
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Inv(reis.ThuisLat)}&longitude={Inv(reis.ThuisLon)}" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum" +
                      $"&timezone=Europe%2FBrussels&past_days={WeerDagen}&forecast_days=1";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var daily = doc.RootElement.GetProperty("daily");
            var max = Reeks(daily, "temperature_2m_max");
            var min = Reeks(daily, "temperature_2m_min");
            var regen = Reeks(daily, "precipitation_sum");
            // Alleen de voorbije dagen tellen mee, de forecast-dag (vandaag) niet.
            var aantal = Math.Min(WeerDagen, Math.Min(max.Count, min.Count));
            if (aantal < 7)
            {
                return StandaardInterval; // te weinig data om op te sturen
            }
            var gemTemp = Enumerable.Range(0, aantal).Average(i => (max[i] + min[i]) / 2);
            var somRegen = regen.Take(aantal).Sum();
            if (gemTemp < 8 || somRegen < 5)
            {
                return TraagInterval;
            }
            if (gemTemp >= 10 && somRegen >= 20)
            {
                return SnelInterval;
            }
        }
        catch
        {
            // Weer is bijzaak: zonder verwachting geldt gewoon het standaardinterval.
        }
        return StandaardInterval;
    }

    private static List<double> Reeks(JsonElement daily, string naam) =>
        daily.TryGetProperty(naam, out var reeks) && reeks.ValueKind == JsonValueKind.Array
            ? reeks.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0)
                .ToList()
            : new List<double>();

    private static string Inv(double waarde) => waarde.ToString("0.####", CultureInfo.InvariantCulture);

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } state)
            {
                return state;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen; hooguit start de klok één keer opnieuw op vandaag.
        }
        return new State();
    }

    private static void SaveState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(
            state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
