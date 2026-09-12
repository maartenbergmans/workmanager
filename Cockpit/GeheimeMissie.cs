using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Elke dag een geheime missie, verstopt achter de Konami-code in de cockpit
/// (↑ ↑ ↓ ↓ ← → ← → B A). Een deel controleert WorkManager de volgende ochtend zelf uit het
/// werkjournaal (geen activiteit na 19:30, uren volledig geboekt, …); de rest vink je zelf af
/// met "Gedaan!". De missie van de dag ligt vast (zelfde dag = zelfde missie) en herhaalt zich
/// niet binnen twee weken.
/// <para>State: %APPDATA%\WorkManager\geheime-missie.json.</para>
/// </summary>
public static class GeheimeMissie
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "geheime-missie.json");

    /// <summary>Een missie; <see cref="Check"/> = automatisch te controleren uit de dagsamenvatting.</summary>
    private sealed record Missie(string Id, string Tekst, Func<Werkjournaal.DagSamenvatting, bool>? Check = null);

    private static readonly Missie[] Pool =
    {
        // Automatisch gecontroleerd (de volgende ochtend, uit het journaal).
        new("stop-1930", "Na 19:30 raak je je pc niet meer aan.",
            d => string.CompareOrdinal(d.Laatste ?? "00:00", "19:30") <= 0),
        new("uren-90", "Boek vandaag minstens 90% van je actieve tijd in je timesheets.",
            d => d.ActiefMinuten > 0 && d.Timesheets.Minuten >= d.ActiefMinuten * 0.9),
        new("pauze-30", "Neem vandaag één echte pauze van minstens 30 minuten.",
            d => d.LangstePauzeMinuten >= 30),
        new("taken-5", "Vink vandaag minstens vijf taken af.",
            d => d.TakenAfgevinkt.Count >= 5),
        new("omschrijving", "Geen enkele timesheetregel zonder omschrijving vandaag.",
            d => d.Timesheets.Regels > 0 && d.Timesheets.ZonderOmschrijving == 0),
        new("claude-20", "Na 20:00 geen enkele opdracht meer aan Claude.",
            d => d.Claude.Prompts.All(p => string.CompareOrdinal(p[..Math.Min(5, p.Length)], "20:00") < 0)),
        new("nieuws-0", "Vandaag geen minuut nieuws of sociale media tijdens de werkuren.",
            d => !d.PerContext.ContainsKey("Nieuws en sociale media")),
        // Zelf af te vinken.
        new("compliment", "Stuur één teamlid een oprecht compliment over iets concreets."),
        new("kinderen", "Vraag Emilia en Lisa vanavond wat het beste moment van hun dag was."),
        new("oudste-mail", "Beantwoord de oudste mail die nog op jou wacht."),
        new("claude-sluiten", "Sluit elke Claude-sessie af die al uren niets meer doet."),
        new("commit", "Commit alles wat openstaat in één repo, met een fatsoenlijke boodschap."),
        new("buiten", "Wandel tien minuten buiten, zonder scherm en zonder oortjes."),
        new("uitgesteld", "Pak de taak die je het vaakst uitstelde en doe ze als eerste."),
        new("kennis", "Leer Claude iets: voeg één kennisvoorstel toe aan een CLAUDE.md."),
        new("espresso", "Drink een espresso zonder naar een scherm te kijken."),
        new("bellen", "Bel iemand die je anders een mail zou sturen."),
        new("hilke", "Stuur Hilke een berichtje zonder aanleiding."),
        new("blok", "Plan morgen een blok van twee uur zonder meetings in je agenda."),
        new("opruimen", "Gooi drie dingen van je bureau (of bureaublad) weg."),
        new("bedanken", "Bedank een klant of collega voor iets wat je normaal vanzelfsprekend vindt."),
    };

    private sealed class State
    {
        public Dictionary<string, string> PerDag { get; set; } = new();  // datum → missie-id
        public List<string> Onthuld { get; set; } = new();               // datums
        public List<string> Gedaan { get; set; } = new();                // datums
        public List<string> Gecontroleerd { get; set; } = new();         // datums (auto)
        public string LaatsteHint { get; set; } = "";
    }

    public static bool OoitOnthuld => Laad().Onthuld.Count > 0;

    /// <summary>De missie van vandaag onthullen (Konami-code in de cockpit).</summary>
    public static void Onthul(Form cockpit)
    {
        var state = Laad();
        var vandaag = Dag(DateOnly.FromDateTime(DateTime.Now));
        var missie = VanDag(state, vandaag);
        if (!state.Onthuld.Contains(vandaag))
        {
            state.Onthuld.Add(vandaag);
        }
        Bewaar(state);
        var intro = Intro();
        if (state.Gedaan.Contains(vandaag))
        {
            Toast.Toon(cockpit, $"{intro} Missie van vandaag al volbracht: {missie.Tekst} ✅", Fluent.Ster);
            return;
        }
        if (missie.Check is not null)
        {
            Toast.ToonActie(cockpit, $"{intro} {missie.Tekst} — morgenvroeg hoor je of het gelukt is.",
                "Aanvaard", () => Toast.Toon(cockpit, "🕵️ Missie aanvaard. Deze boodschap vernietigt zichzelf.", Fluent.Ster),
                Fluent.Ster);
            return;
        }
        Toast.ToonActie(cockpit, $"{intro} {missie.Tekst}", "Gedaan!", () =>
        {
            var s = Laad();
            if (!s.Gedaan.Contains(vandaag))
            {
                s.Gedaan.Add(vandaag);
                Bewaar(s);
            }
            Confetti.Vier(cockpit);
            Prestaties.Gebeurtenis(cockpit, "missie");
            Toast.Toon(cockpit, $"🕵️ Missie volbracht! Al {s.Gedaan.Count} geheime missie{(s.Gedaan.Count == 1 ? "" : "s")} op je naam.",
                Fluent.Ster);
        }, Fluent.Ster);
    }

    /// <summary>
    /// 's Ochtends (vanaf 7 u): de automatisch controleerbare missie van gisteren beoordelen,
    /// als die onthuld was. Retourneert (titel, tekst) voor een melding, of null.
    /// </summary>
    public static (string Titel, string Tekst)? ControleerGisteren()
    {
        if (DateTime.Now.Hour < 7)
        {
            return null;
        }
        var state = Laad();
        var gisteren = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        var sleutel = Dag(gisteren);
        if (!state.Onthuld.Contains(sleutel) || state.Gecontroleerd.Contains(sleutel) ||
            !state.PerDag.TryGetValue(sleutel, out var id) ||
            Pool.FirstOrDefault(m => m.Id == id) is not { Check: { } check } missie)
        {
            return null;
        }
        // Wacht op de definitieve samenvatting (na middernacht herberekend).
        if (Werkjournaal.Dag(gisteren) is not { } dag || dag.Berekend.LocalDateTime.Date <= gisteren.ToDateTime(TimeOnly.MinValue))
        {
            return null;
        }
        state.Gecontroleerd.Add(sleutel);
        var gelukt = check(dag);
        if (gelukt && !state.Gedaan.Contains(sleutel))
        {
            state.Gedaan.Add(sleutel);
            Prestaties.Gebeurtenis(null, "missie");
        }
        Bewaar(state);
        return gelukt
            ? ("🕵️ Missie van gisteren: geslaagd ✅", $"{missie.Tekst}\nAl {state.Gedaan.Count} missies volbracht.")
            : ("🕵️ Missie van gisteren: net niet", $"{missie.Tekst}\n{Waarom(missie.Id, dag)} Vandaag ligt er een nieuwe klaar.");
    }

    /// <summary>Eén keer per week een hint zolang de missies nooit ontdekt werden.</summary>
    public static string? Hint()
    {
        var state = Laad();
        if (state.Onthuld.Count > 0)
        {
            return null;
        }
        var week = $"{System.Globalization.ISOWeek.GetYear(DateTime.Now)}-W{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now):00}";
        if (state.LaatsteHint == week)
        {
            return null;
        }
        state.LaatsteHint = week;
        Bewaar(state);
        return "In de cockpit ligt elke dag een geheime missie voor je klaar. De code ken je nog van vroeger: " +
               "↑ ↑ ↓ ↓ ← → ← → B A (typ ze gewoon terwijl de cockpit open staat).";
    }

    private static string Waarom(string id, Werkjournaal.DagSamenvatting d) => id switch
    {
        "stop-1930" => $"Je laatste activiteit was om {d.Laatste}.",
        "uren-90" => $"Geboekt: {d.Timesheets.Minuten / 60.0:0.#} u van {d.ActiefMinuten / 60.0:0.#} u actief.",
        "pauze-30" => $"Je langste pauze was {d.LangstePauzeMinuten} minuten.",
        "taken-5" => $"Je vinkte er {d.TakenAfgevinkt.Count} af.",
        "omschrijving" => d.Timesheets.Regels == 0 ? "Er werd niets geboekt." : $"{d.Timesheets.ZonderOmschrijving} regel(s) zonder omschrijving.",
        "claude-20" => "Er gingen nog opdrachten naar Claude na 20 u.",
        "nieuws-0" => $"{d.PerContext.GetValueOrDefault("Nieuws en sociale media")} minuten nieuws of sociale media.",
        _ => "",
    };

    private static string Intro() => Theme.Palet.Naam switch
    {
        "Godfather" => "🌹 De Don heeft een opdracht voor je:",
        "007" => "🍸 M heeft een missie voor je, 007:",
        "Espresso" => "☕ Geheime missie, vers gezet:",
        "Neon" => "🌃 Inkomende transmissie:",
        "Zomer" => "🌴 Geheime missie onder de parasol:",
        _ => "🕵️ Geheime missie van vandaag:",
    };

    /// <summary>De missie van een dag: vast per dag, geen herhaling binnen 14 dagen.</summary>
    private static Missie VanDag(State state, string dag)
    {
        if (state.PerDag.TryGetValue(dag, out var id) && Pool.FirstOrDefault(m => m.Id == id) is { } bekend)
        {
            return bekend;
        }
        var recent = state.PerDag.OrderByDescending(kv => kv.Key).Take(14).Select(kv => kv.Value).ToHashSet();
        var kandidaten = Pool.Where(m => !recent.Contains(m.Id)).ToList();
        if (kandidaten.Count == 0)
        {
            kandidaten = Pool.ToList();
        }
        // Weekend: geen werkmissies die op timesheets of teamleden draaien.
        var datum = DateOnly.Parse(dag);
        if (datum.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            var weekend = kandidaten.Where(m => m.Id is "kinderen" or "buiten" or "espresso" or "hilke" or "stop-1930" or "opruimen" or "nieuws-0").ToList();
            if (weekend.Count > 0)
            {
                kandidaten = weekend;
            }
        }
        var index = (int)((uint)dag.GetHashCode() % (uint)kandidaten.Count);
        // string.GetHashCode is per proces willekeurig: daarom meteen vastleggen.
        var missie = kandidaten[index];
        state.PerDag[dag] = missie.Id;
        foreach (var oud in state.PerDag.Keys.OrderBy(k => k).SkipLast(60).ToList())
        {
            state.PerDag.Remove(oud);
        }
        Bewaar(state);
        return missie;
    }

    private static string Dag(DateOnly d) => d.ToString("yyyy-MM-dd");

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
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
