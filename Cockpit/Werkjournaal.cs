using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Blijvend werkjournaal: wat Maarten doet, samengevat per dag en per week, zodat Claude hem
/// later op basis van maanden gedrag kan adviseren (werkritme, uren die niet geboekt raken,
/// welke WorkManager-functies hij gebruikt, waar zijn Claude-werk naartoe gaat).
/// <para>De ruwe sporen (minuutlog van het voorgrondvenster, Claude-opdrachten) worden na 21
/// dagen gewist; dit journaal bewaart er compacte samenvattingen van, voor altijd.</para>
/// <para>Map %APPDATA%\WorkManager\journaal\:
/// gebeurtenissen.jsonl (Claude-prompts, geopende vensters, knoppen/menu's; 400 dagen),
/// dagen\yyyy-MM-dd.json (dagsamenvatting), weken\yyyy-Www.md (leesbaar weekoverzicht),
/// LEESMIJ.md (uitleg voor een latere Claude-sessie).</para>
/// </summary>
public static class Werkjournaal
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    public static readonly string Map = Path.Combine(DataDir, "journaal");
    private static readonly string Gebeurtenissen = Path.Combine(Map, "gebeurtenissen.jsonl");
    private static readonly string DagenMap = Path.Combine(Map, "dagen");
    private static readonly string WekenMap = Path.Combine(Map, "weken");

    private static readonly string ActiviteitenBestand = Path.Combine(DataDir, "activiteiten-log.jsonl");
    private static readonly string ClaudeBestand = Path.Combine(DataDir, "claude-requests.jsonl");
    private static readonly string SwitchBestand = Path.Combine(DataDir, "switch-log.jsonl");

    private static readonly TimeSpan BewaarGebeurtenissen = TimeSpan.FromDays(400);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly CultureInfo Nl = new("nl-BE");
    private static int _bezig;
    private static DateOnly _opgeruimd = DateOnly.MinValue;

    // ---------------------------------------------------------------- vastleggen

    /// <summary>
    /// Een interactieve Claude Code-opdracht (UserPromptSubmit-hook). De prompt wordt ingekort
    /// bewaard: genoeg om later te zien wát er gevraagd werd, niet de hele lap tekst.
    /// </summary>
    public static void NoteerClaude(string map, string sessie, string prompt)
    {
        prompt = Regex.Replace(prompt ?? "", @"\s+", " ").Trim();
        // Geplakte wachtwoorden/tokens horen niet in een blijvend journaal.
        prompt = Regex.Replace(prompt,
            @"(?i)\b(wachtwoord|paswoord|password|passwd|pwd|pass|token|secret|api[-_ ]?key)\b(\s*(is|=|:)?\s*)\S+",
            "$1$2[verborgen]");
        // Het startprompt "/color …" zet WorkManager zelf; dat is geen werk. Berichten die met
        // een tag beginnen (<task-notification>, <local-command-…>) zijn systeemverkeer.
        if (prompt.Length == 0 || prompt.StartsWith("/color", StringComparison.OrdinalIgnoreCase) ||
            prompt.StartsWith('<'))
        {
            return;
        }
        Schrijf(new Dictionary<string, object?>
        {
            ["soort"] = "claude",
            ["map"] = map,
            ["sessie"] = sessie,
            ["prompt"] = Kort(prompt, 400),
        });
    }

    private static readonly ConditionalWeakTable<Form, object> GelogdeVensters = new();
    private static readonly ConditionalWeakTable<ToolStripDropDown, object> GehaakteMenus = new();

    /// <summary>Een WorkManager-venster ging open (aangeroepen vanuit Theme.Apply; 1× per venster).</summary>
    public static void NoteerVenster(Form form)
    {
        try
        {
            if (GelogdeVensters.TryGetValue(form, out _))
            {
                return;
            }
            GelogdeVensters.Add(form, new object());
            Schrijf(new Dictionary<string, object?>
            {
                ["soort"] = "venster",
                ["naam"] = form.GetType().Name,
                ["titel"] = Kort(form.Text, 80),
            });
        }
        catch
        {
            // Nooit het venster hinderen.
        }
    }

    /// <summary>Een knop in een WorkManager-venster werd aangeklikt.</summary>
    public static void NoteerKnop(string tekst, Form? venster) =>
        NoteerActie("knop", tekst, venster?.GetType().Name);

    /// <summary>Laat alle items van dit menu (ook later bijgemaakte) hun klik loggen; 1× per menu.</summary>
    public static void HaakMenu(ToolStripDropDown menu)
    {
        try
        {
            if (GehaakteMenus.TryGetValue(menu, out _))
            {
                return;
            }
            GehaakteMenus.Add(menu, new object());
            menu.ItemClicked += (_, e) =>
            {
                // Een item met een submenu opent alleen dat submenu: dat is nog geen actie.
                if (e.ClickedItem is ToolStripMenuItem { HasDropDownItems: true })
                {
                    return;
                }
                NoteerActie("menu", e.ClickedItem?.Text ?? "", Form.ActiveForm?.GetType().Name);
            };
        }
        catch
        {
            // Best effort.
        }
    }

    private static void NoteerActie(string soort, string tekst, string? venster)
    {
        tekst = NormaliseerActie(tekst);
        if (tekst.Length == 0)
        {
            return;
        }
        Schrijf(new Dictionary<string, object?>
        {
            ["soort"] = soort,
            ["tekst"] = tekst,
            ["venster"] = venster,
        });
    }

    /// <summary>"🟢 Berichten (12) ▾" → "Berichten": tellers, lampjes en pijltjes eraf.</summary>
    private static string NormaliseerActie(string tekst)
    {
        tekst = Regex.Replace(tekst ?? "", @"\(\d+\)|\d+", "");
        tekst = Regex.Replace(tekst, @"[▾▸►✓✔🟢🔴⏹◆📂]", "");
        tekst = Regex.Replace(tekst, @"\s+", " ").Trim(' ', '·', '-', '—', ':', '&');
        return Kort(tekst, 60);
    }

    private static void Schrijf(Dictionary<string, object?> velden)
    {
        var regel = new Dictionary<string, object?> { ["t"] = DateTimeOffset.Now };
        foreach (var (k, v) in velden)
        {
            regel[k] = v;
        }
        var json = JsonSerializer.Serialize(regel, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine;
        // De Claude-hook schrijft vanuit een apart kortlevend proces: bij een botsing met
        // de tray-app even opnieuw proberen in plaats van het spoor te verliezen.
        for (var poging = 0; poging < 5; poging++)
        {
            try
            {
                Directory.CreateDirectory(Map);
                File.AppendAllText(Gebeurtenissen, json);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(40);
            }
            catch
            {
                return;
            }
        }
    }

    // ---------------------------------------------------------------- samenvatten

    /// <summary>
    /// Werkt het journaal bij (tray-timer, elk uur): de dagsamenvatting van vandaag opnieuw,
    /// ontbrekende of onvoltooide eerdere dagen alsnog, en de weekoverzichten van de
    /// betrokken weken. Draait nooit twee keer tegelijk; fouten blijven stil.
    /// </summary>
    public static int WerkBij()
    {
        if (Interlocked.Exchange(ref _bezig, 1) == 1)
        {
            return 0;
        }
        try
        {
            Directory.CreateDirectory(DagenMap);
            Directory.CreateDirectory(WekenMap);
            SchrijfLeesmij();
            RuimOpAlsNodig();

            var samples = LeesSamples();
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var dagen = samples.Select(s => DateOnly.FromDateTime(s.T.LocalDateTime))
                .Concat(LeesClaudeRequests().Select(c => DateOnly.FromDateTime(c.T.LocalDateTime)))
                .Append(vandaag)
                .Distinct()
                .Where(d => d <= vandaag)
                .OrderBy(d => d)
                .ToList();

            var weken = new HashSet<(int Jaar, int Week)>();
            var gemaakt = 0;
            foreach (var dag in dagen)
            {
                var bestaand = LaadDag(dag);
                // Een eerdere dag die tijdens die dag zelf samengevat werd, mist het einde:
                // na middernacht nog één keer definitief opnieuw.
                var definitief = bestaand is not null && bestaand.Berekend.LocalDateTime.Date > dag.ToDateTime(TimeOnly.MinValue);
                if (dag != vandaag && definitief)
                {
                    continue;
                }
                var samenvatting = MaakDag(dag, samples, bestaand);
                File.WriteAllText(Path.Combine(DagenMap, $"{dag:yyyy-MM-dd}.json"),
                    JsonSerializer.Serialize(samenvatting, JsonOpts));
                weken.Add((ISOWeek.GetYear(dag.ToDateTime(TimeOnly.MinValue)),
                           ISOWeek.GetWeekOfYear(dag.ToDateTime(TimeOnly.MinValue))));
                gemaakt++;
            }
            foreach (var (jaar, week) in weken)
            {
                SchrijfWeek(jaar, week);
            }
            return gemaakt;
        }
        catch
        {
            return 0; // het journaal mag de tray-app nooit hinderen
        }
        finally
        {
            Interlocked.Exchange(ref _bezig, 0);
        }
    }

    public sealed class DagSamenvatting
    {
        public string Datum { get; set; } = "";
        public string Weekdag { get; set; } = "";
        public DateTimeOffset Berekend { get; set; }
        public int ActiefMinuten { get; set; }
        public string? Eerste { get; set; }
        public string? Laatste { get; set; }
        /// <summary>Aaneengesloten werkblokken (gat &gt; 15 min = nieuw blok).</summary>
        public int Blokken { get; set; }
        public int LangstePauzeMinuten { get; set; }
        public int VoorAchtMinuten { get; set; }
        public int NaZevenMinuten { get; set; }
        /// <summary>Schatting per klant/context op basis van proces + venstertitel.</summary>
        public Dictionary<string, int> PerContext { get; set; } = new();
        public List<Teller> PerProces { get; set; } = new();
        public List<Teller> TopTitels { get; set; } = new();
        public ClaudeDeel Claude { get; set; } = new();
        public List<string> Meetings { get; set; } = new();
        public int MeetingMinuten { get; set; }
        public TimesheetDeel Timesheets { get; set; } = new();
        public List<string> TakenAfgevinkt { get; set; } = new();
        public int TakenAangemaakt { get; set; }
        public int ContextSwitches { get; set; }
        public Dictionary<string, int> WorkManagerVensters { get; set; } = new();
        public Dictionary<string, int> WorkManagerActies { get; set; } = new();
        public List<string> Plekken { get; set; } = new();
    }

    public sealed class Teller
    {
        public string Naam { get; set; } = "";
        public int Minuten { get; set; }
    }

    public sealed class ClaudeDeel
    {
        public int Opdrachten { get; set; }
        public Dictionary<string, int> PerMap { get; set; } = new();
        public List<string> Prompts { get; set; } = new();
    }

    public sealed class TimesheetDeel
    {
        public int Minuten { get; set; }
        public int Regels { get; set; }
        public int ZonderOmschrijving { get; set; }
        public Dictionary<string, int> PerProject { get; set; } = new();
    }

    private sealed record Sample(DateTimeOffset T, string Proces, string Titel);

    private sealed record ClaudeRequest(DateTimeOffset T, string Map);

    private static DagSamenvatting MaakDag(DateOnly dag, List<Sample> alleSamples, DagSamenvatting? vorige)
    {
        var samples = alleSamples.Where(s => DateOnly.FromDateTime(s.T.LocalDateTime) == dag)
            .OrderBy(s => s.T).ToList();
        var s = new DagSamenvatting
        {
            Datum = dag.ToString("yyyy-MM-dd"),
            Weekdag = dag.ToString("dddd", Nl),
            Berekend = DateTimeOffset.Now,
            ActiefMinuten = samples.Count, // één sample per actieve minuut
        };
        if (samples.Count > 0)
        {
            s.Eerste = samples[0].T.LocalDateTime.ToString("HH:mm");
            s.Laatste = samples[^1].T.LocalDateTime.ToString("HH:mm");
            s.Blokken = 1;
            for (var i = 1; i < samples.Count; i++)
            {
                var gat = (int)(samples[i].T - samples[i - 1].T).TotalMinutes;
                if (gat > 15)
                {
                    s.Blokken++;
                }
                s.LangstePauzeMinuten = Math.Max(s.LangstePauzeMinuten, gat > 1 ? gat : 0);
            }
            s.VoorAchtMinuten = samples.Count(x => x.T.LocalDateTime.Hour < 8);
            s.NaZevenMinuten = samples.Count(x => x.T.LocalDateTime.Hour >= 19);
            // Terminaltabs tonen Claudes taaktitel, niet het project: koppel die minuten aan
            // de map van de laatste Claude-opdracht (hoogstens 90 minuten eerder).
            var dagRequests = LeesClaudeRequests().OrderBy(r => r.T).ToList();
            string? LaatsteMap(DateTimeOffset t) =>
                dagRequests.LastOrDefault(r => r.T <= t && t - r.T <= TimeSpan.FromMinutes(90))?.Map;
            s.PerContext = samples.GroupBy(x => Context(x.Proces, x.Titel,
                    x.Proces.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ? LaatsteMap(x.T) : null))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
            s.PerProces = samples.GroupBy(x => x.Proces, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Teller { Naam = g.Key, Minuten = g.Count() })
                .OrderByDescending(t => t.Minuten).Take(12).ToList();
            s.TopTitels = samples.GroupBy(x => $"{x.Proces}: {NormaliseerTitel(x.Titel)}")
                .Select(g => new Teller { Naam = Kort(g.Key, 110), Minuten = g.Count() })
                .Where(t => t.Minuten >= 3)
                .OrderByDescending(t => t.Minuten).Take(25).ToList();
        }

        // Claude: het aantal uit claude-requests.jsonl (ook voor dagen van vóór het journaal),
        // de prompts zelf uit het journaal.
        var requests = LeesClaudeRequests().Where(c => DateOnly.FromDateTime(c.T.LocalDateTime) == dag).ToList();
        var gebeurtenissen = LeesGebeurtenissen(dag);
        var prompts = gebeurtenissen.Where(g => g.Soort == "claude").ToList();
        s.Claude.Opdrachten = Math.Max(requests.Count, prompts.Count);
        s.Claude.PerMap = (requests.Count >= prompts.Count
                ? requests.Select(r => r.Map)
                : prompts.Select(p => p.Veld("map")))
            .GroupBy(KorteMap).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        s.Claude.Prompts = prompts.Take(80)
            .Select(p => $"{p.T.LocalDateTime:HH:mm} [{KorteMap(p.Veld("map"))}] {Kort(p.Veld("prompt"), 200)}")
            .ToList();

        s.WorkManagerVensters = gebeurtenissen.Where(g => g.Soort == "venster")
            .GroupBy(g => g.Veld("naam")).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        s.WorkManagerActies = gebeurtenissen.Where(g => g.Soort is "knop" or "menu")
            .GroupBy(g => g.Veld("tekst")).OrderByDescending(g => g.Count()).Take(30)
            .ToDictionary(g => g.Key, g => g.Count());

        // Meetings uit de agendacache — die bevat alleen recente dagen, dus bij een latere
        // herberekening de eerder vastgelegde lijst behouden.
        var meetings = Meetings(dag);
        if (meetings.Count == 0 && vorige is not null)
        {
            s.Meetings = vorige.Meetings;
            s.MeetingMinuten = vorige.MeetingMinuten;
        }
        else
        {
            s.Meetings = meetings.Select(m => $"{m.Start.LocalDateTime:HH:mm}-{m.Einde.LocalDateTime:HH:mm} {Kort(m.Titel, 80)}").ToList();
            s.MeetingMinuten = (int)meetings.Sum(m => (m.Einde - m.Start).TotalMinutes);
        }

        try
        {
            var regels = TimesheetStore.Load().Where(r => r.Datum == dag).ToList();
            s.Timesheets = new TimesheetDeel
            {
                Minuten = regels.Sum(r => r.Minuten),
                Regels = regels.Count,
                ZonderOmschrijving = regels.Count(r => r.Omschrijving.Trim().Length == 0),
                PerProject = regels.GroupBy(r => ProjectCatalogus.LabelVoor(r.Klant))
                    .OrderByDescending(g => g.Sum(r => r.Minuten))
                    .ToDictionary(g => g.Key, g => g.Sum(r => r.Minuten)),
            };
        }
        catch
        {
            // Zonder timesheetdeel verder.
        }

        try
        {
            var taken = MijnTaakStore.Load().Taken;
            s.TakenAfgevinkt = taken
                .Where(t => t.Klaar && t.KlaarOp is { } k && DateOnly.FromDateTime(k.LocalDateTime) == dag)
                .Select(t => $"[{t.Categorie}] {Kort(t.Tekst, 90)}").Take(30).ToList();
            s.TakenAangemaakt = taken.Count(t => DateOnly.FromDateTime(t.AangemaaktOp.LocalDateTime) == dag);
        }
        catch
        {
            // Zonder takendeel verder.
        }

        s.ContextSwitches = LeesRegels(SwitchBestand)
            .Count(l => TijdUit(l, "timestamp") is { } t && DateOnly.FromDateTime(t.LocalDateTime) == dag);

        try
        {
            s.Plekken = LocatieLog.Laad().Punten
                .Where(p => DateOnly.FromDateTime(p.Moment.LocalDateTime) == dag)
                .Select(p => p.Plek.Length > 0 ? p.Plek : "onbekende plek")
                .Distinct().ToList();
        }
        catch
        {
            // Geen locatiedata.
        }
        return s;
    }

    private static List<AgendaClient.AgendaItem> Meetings(DateOnly dag)
    {
        try
        {
            if (MeetingsCache.Load() is not { } cache)
            {
                return new();
            }
            var ced = cache.Ced.TryGetValue(dag.ToString("yyyy-MM-dd"), out var c) ? c : new();
            return cache.Eigen.Concat(ced)
                .Where(m => !m.HeleDag && DateOnly.FromDateTime(m.Start.LocalDateTime) == dag &&
                            !GeenWerktijd.Is(m.Titel))
                .GroupBy(m => (m.Start, m.Titel)).Select(g => g.First())
                .OrderBy(m => m.Start).ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Ruwe schatting van de klant/context achter een voorgrondvenster. De Claude-tabs dragen
    /// "Klant — map" als titel, IDE's de projectnaam, de browser de sitetitel.
    /// </summary>
    private static readonly (string Trefwoord, string Context)[] Contexten =
    {
        ("aqurat", "Aqurat"),
        ("bloom", "RadiologyPartners"), ("ximeo", "RadiologyPartners"), ("radiology", "RadiologyPartners"),
        ("cellaware", "Vriesveem"), ("movaware", "Vriesveem"), ("vriesveem", "Vriesveem"), ("nemijtek", "Vriesveem"),
        ("laurapp", "Lauryssens"), ("lauryssens", "Lauryssens"), ("glascalculator", "Lauryssens"),
        ("totalloss", "CED"), ("topdesk", "CED"), ("carex", "CED"), ("ispnext", "CED"),
        ("repalink", "CED"), ("ced ", "CED"), ("ced-", "CED"), ("ced.", "CED"), ("automaticmail", "CED"),
        ("urbanadmin", "UrbanIT"), ("timesheets.urbanit", "UrbanIT"),
        ("citroenloos", "Garage Loos"), ("garage loos", "Garage Loos"),
        ("smartschool", "Privé"), ("ah.be", "Privé"), ("albert heijn", "Privé"), ("netflix", "Privé"),
        ("mail van urbanit", "Mail (Gmail)"), ("gmail", "Mail (Gmail)"),
        ("whatsapp", "Berichten (WhatsApp)"),
        ("meet - ", "Meeting (Google Meet)"),
        ("hln", "Nieuws en sociale media"), ("het laatste nieuws", "Nieuws en sociale media"),
        ("standaard.be", "Nieuws en sociale media"), ("vrt nws", "Nieuws en sociale media"),
        ("youtube", "Nieuws en sociale media"), ("facebook", "Nieuws en sociale media"),
        ("linkedin", "Nieuws en sociale media"),
        ("fritz!box", "Eigen IT en infrastructuur"),
        ("urbanit", "UrbanIT"),
        ("workmanager", "WorkManager-ontwikkeling"),
    };

    private static string Context(string proces, string titel, string? claudeMap = null)
    {
        if (claudeMap is { Length: > 0 })
        {
            // Een Claude-map ("…\projecten\aqurat\webapp") zegt meer dan de tabtitel.
            var uitMap = Context("", claudeMap.Replace('\\', ' ').Replace('/', ' '));
            if (uitMap is not ("Overig" or "Ontwikkelen (project onbekend)"))
            {
                return uitMap;
            }
        }
        var tekst = $"{titel} ".ToLowerInvariant();
        if (proces.Equals("WorkManager", StringComparison.OrdinalIgnoreCase))
        {
            // Elk WorkManager-venster heet "… – WorkManager": dat is de cockpit gebruiken,
            // geen ontwikkelwerk. Andere trefwoorden (Smartschool, Taken team) tellen wél.
            tekst = tekst.Replace("workmanager", "");
        }
        foreach (var (trefwoord, context) in Contexten)
        {
            if (tekst.Contains(trefwoord))
            {
                return context;
            }
        }
        return proces.ToLowerInvariant() switch
        {
            "olk" or "outlook" or "ms-teams" or "teams" or "msrdc" or "windowsapp" => "CED",
            "workmanager" => "WorkManager-cockpit",
            "claude" => "Claude (desktop-app)",
            "excel" or "winword" or "powerpnt" => "Office (overig)",
            "phpstorm64" or "datagrip64" or "devenv" or "code" => "Ontwikkelen (project onbekend)",
            "windowsterminal" or "wt" => "Terminal (project onbekend)",
            "chrome" or "firefox" or "msedge" => "Browser (overig)",
            _ => "Overig",
        };
    }

    private static string NormaliseerTitel(string titel)
    {
        titel = Regex.Replace(titel ?? "", @"\s+[-—–]\s+(Google Chrome|Mozilla Firefox|Microsoft\s?Edge|Brave)$", "",
            RegexOptions.IgnoreCase);
        // Tellers in titels ("Inbox (12)", "(3) WhatsApp") maken dezelfde plek anders verschillend.
        titel = Regex.Replace(titel, @"\(\d+\)\s*", "");
        return titel.Trim();
    }

    private static string KorteMap(string map)
    {
        var naam = (map ?? "").TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? "";
        return naam.Length > 0 ? naam : "?";
    }

    // ---------------------------------------------------------------- weekoverzicht

    private static void SchrijfWeek(int jaar, int week)
    {
        var maandag = DateOnly.FromDateTime(ISOWeek.ToDateTime(jaar, week, DayOfWeek.Monday));
        var dagen = Enumerable.Range(0, 7).Select(i => maandag.AddDays(i))
            .Select(d => (Dag: d, S: LaadDag(d)))
            .Where(x => x.S is not null)
            .Select(x => (x.Dag, S: x.S!))
            .ToList();
        if (dagen.Count == 0)
        {
            return;
        }
        static string U(int minuten) => minuten == 0 ? "–" : $"{minuten / 60.0:0.0} u";

        var md = new StringBuilder();
        md.AppendLine($"# Week {week} van {jaar} ({maandag:d MMM} – {maandag.AddDays(6):d MMM yyyy})");
        md.AppendLine();
        md.AppendLine($"_Automatisch gemaakt door WorkManager op {DateTime.Now:yyyy-MM-dd HH:mm}. Actief = minuten met toetsenbord/muis (samples van het voorgrondvenster); contexten zijn een schatting op venstertitels._");
        md.AppendLine();
        md.AppendLine("| Dag | Actief | Van–tot | Geboekt | Meetings | Claude-opdrachten | Buiten 8–19 u | Taken klaar |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (dag, s) in dagen)
        {
            md.AppendLine($"| {s.Weekdag} {dag:d/M} | {U(s.ActiefMinuten)} | {s.Eerste ?? "–"}–{s.Laatste ?? "–"} | " +
                          $"{U(s.Timesheets.Minuten)} | {s.Meetings.Count} ({U(s.MeetingMinuten)}) | {s.Claude.Opdrachten} | " +
                          $"{U(s.VoorAchtMinuten + s.NaZevenMinuten)} | {s.TakenAfgevinkt.Count} |");
        }
        var totActief = dagen.Sum(d => d.S.ActiefMinuten);
        var totGeboekt = dagen.Sum(d => d.S.Timesheets.Minuten);
        md.AppendLine($"| **Totaal** | **{U(totActief)}** | | **{U(totGeboekt)}** | {dagen.Sum(d => d.S.Meetings.Count)} | {dagen.Sum(d => d.S.Claude.Opdrachten)} | {U(dagen.Sum(d => d.S.VoorAchtMinuten + d.S.NaZevenMinuten))} | {dagen.Sum(d => d.S.TakenAfgevinkt.Count)} |");
        md.AppendLine();

        void Tabel(string titel, IEnumerable<KeyValuePair<string, int>> rijen, bool minuten, int max = 15)
        {
            var lijst = rijen.GroupBy(kv => kv.Key).Select(g => (g.Key, Som: g.Sum(kv => kv.Value)))
                .OrderByDescending(x => x.Som).Take(max).ToList();
            if (lijst.Count == 0)
            {
                return;
            }
            md.AppendLine($"## {titel}");
            md.AppendLine();
            foreach (var (naam, som) in lijst)
            {
                md.AppendLine($"- {naam}: {(minuten ? U(som) : som.ToString())}");
            }
            md.AppendLine();
        }
        Tabel("Actieve tijd per context (schatting)", dagen.SelectMany(d => d.S.PerContext), true);
        Tabel("Geboekt per project", dagen.SelectMany(d => d.S.Timesheets.PerProject), true);
        Tabel("Programma's", dagen.SelectMany(d => d.S.PerProces.Select(t => new KeyValuePair<string, int>(t.Naam, t.Minuten))), true, 10);
        Tabel("Claude-opdrachten per projectmap", dagen.SelectMany(d => d.S.Claude.PerMap), false);
        Tabel("WorkManager-vensters geopend", dagen.SelectMany(d => d.S.WorkManagerVensters), false);
        Tabel("WorkManager-knoppen en menu's", dagen.SelectMany(d => d.S.WorkManagerActies), false, 20);

        // Signalen die later advies waard zijn.
        var opvallend = new List<string>();
        foreach (var (dag, s) in dagen)
        {
            if (s.ActiefMinuten >= 240 && s.Timesheets.Minuten < s.ActiefMinuten / 2 && dag.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                opvallend.Add($"{s.Weekdag} {dag:d/M}: {U(s.ActiefMinuten)} actief maar {U(s.Timesheets.Minuten)} geboekt.");
            }
            if (s.Timesheets.ZonderOmschrijving > 0)
            {
                opvallend.Add($"{s.Weekdag} {dag:d/M}: {s.Timesheets.ZonderOmschrijving} timesheetregel(s) zonder omschrijving.");
            }
            if (s.NaZevenMinuten >= 60)
            {
                opvallend.Add($"{s.Weekdag} {dag:d/M}: {U(s.NaZevenMinuten)} actief na 19 u (laatste activiteit {s.Laatste}).");
            }
        }
        if (opvallend.Count > 0)
        {
            md.AppendLine("## Opvallend");
            md.AppendLine();
            opvallend.ForEach(o => md.AppendLine($"- {o}"));
            md.AppendLine();
        }

        var prompts = dagen.SelectMany(d => d.S.Claude.Prompts.Select(p => $"{d.S.Weekdag[..2]} {p}")).ToList();
        if (prompts.Count > 0)
        {
            md.AppendLine("## Claude-opdrachten (ingekort)");
            md.AppendLine();
            prompts.Take(120).ToList().ForEach(p => md.AppendLine($"- {p}"));
            md.AppendLine();
        }
        var taken = dagen.SelectMany(d => d.S.TakenAfgevinkt.Select(t => $"{d.S.Weekdag[..2]} {t}")).ToList();
        if (taken.Count > 0)
        {
            md.AppendLine("## Afgevinkte taken");
            md.AppendLine();
            taken.ForEach(t => md.AppendLine($"- {t}"));
            md.AppendLine();
        }
        File.WriteAllText(Path.Combine(WekenMap, $"{jaar}-W{week:00}.md"), md.ToString());
    }

    private static void SchrijfLeesmij()
    {
        var pad = Path.Combine(Map, "LEESMIJ.md");
        const string tekst = """
            # Werkjournaal van Maarten (WorkManager)

            Bedoeld voor Claude: lees dit om Maarten te adviseren over zijn werk, zijn uren en
            WorkManager zelf. Alles wordt automatisch bijgehouden door de tray-app (elk uur).

            - `weken/yyyy-Www.md` — leesbaar weekoverzicht: actieve tijd, geboekte uren, meetings,
              Claude-opdrachten, contexten, gebruikte WorkManager-functies en opvallende signalen.
              **Begin hier** (lees de laatste 4–8 weken).
            - `dagen/yyyy-MM-dd.json` — dagsamenvatting met meer detail (top venstertitels,
              programma's, prompts, meetings, timesheets per project, afgevinkte taken, plekken).
            - `gebeurtenissen.jsonl` — ruwe gebeurtenissen (400 dagen): `claude` (map, sessie,
              ingekorte prompt), `venster` (WorkManager-venster geopend), `knop`/`menu` (klik).

            Kanttekeningen: "actief" = minuten met invoer (geen sample bij >5 min inactiviteit);
            contexten zijn een schatting op procesnaam + venstertitel; meetings komen uit de
            agendacache (privé-items zoals recepten en AH-levering niet); Claude-werk loopt vaak
            door terwijl het voorgrondvenster iets anders toont. De ruwe minuutlog
            (`../activiteiten-log.jsonl`) wordt na 21 dagen gewist — dit journaal niet.
            """;
        if (!File.Exists(pad) || File.ReadAllText(pad) != tekst)
        {
            File.WriteAllText(pad, tekst);
        }
    }

    // ---------------------------------------------------------------- lezen

    /// <summary>De dagsamenvatting van een dag (null als die er niet is).</summary>
    public static DagSamenvatting? Dag(DateOnly dag) => LaadDag(dag);

    /// <summary>De bestaande dagsamenvattingen van de laatste <paramref name="dagen"/> dagen, oudste eerst.</summary>
    public static List<DagSamenvatting> Dagen(int dagen)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return Enumerable.Range(0, dagen).Select(i => vandaag.AddDays(-dagen + 1 + i))
            .Select(LaadDag).OfType<DagSamenvatting>().ToList();
    }

    private static DagSamenvatting? LaadDag(DateOnly dag)
    {
        try
        {
            var pad = Path.Combine(DagenMap, $"{dag:yyyy-MM-dd}.json");
            return File.Exists(pad) ? JsonSerializer.Deserialize<DagSamenvatting>(File.ReadAllText(pad), JsonOpts) : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<Sample> LeesSamples()
    {
        var lijst = new List<Sample>();
        foreach (var regel in LeesRegels(ActiviteitenBestand))
        {
            try
            {
                using var doc = JsonDocument.Parse(regel);
                var r = doc.RootElement;
                lijst.Add(new Sample(r.GetProperty("t").GetDateTimeOffset(),
                    r.TryGetProperty("proces", out var p) ? p.GetString() ?? "" : "",
                    r.TryGetProperty("titel", out var t) ? t.GetString() ?? "" : ""));
            }
            catch
            {
                // Kapotte regel overslaan.
            }
        }
        return lijst;
    }

    private static List<ClaudeRequest>? _requestsCache;
    private static DateTime _requestsGelezen;

    private static List<ClaudeRequest> LeesClaudeRequests()
    {
        if (_requestsCache is not null && DateTime.Now - _requestsGelezen < TimeSpan.FromSeconds(30))
        {
            return _requestsCache;
        }
        var lijst = new List<ClaudeRequest>();
        foreach (var regel in LeesRegels(ClaudeBestand))
        {
            try
            {
                using var doc = JsonDocument.Parse(regel);
                lijst.Add(new ClaudeRequest(doc.RootElement.GetProperty("t").GetDateTimeOffset(),
                    doc.RootElement.TryGetProperty("map", out var m) ? m.GetString() ?? "" : ""));
            }
            catch
            {
                // Overslaan.
            }
        }
        _requestsCache = lijst;
        _requestsGelezen = DateTime.Now;
        return lijst;
    }

    private sealed record Gebeurtenis(DateTimeOffset T, string Soort, Dictionary<string, string> Velden)
    {
        public string Veld(string naam) => Velden.TryGetValue(naam, out var v) ? v : "";
    }

    private static List<Gebeurtenis> LeesGebeurtenissen(DateOnly dag)
    {
        var lijst = new List<Gebeurtenis>();
        foreach (var regel in LeesRegels(Gebeurtenissen))
        {
            try
            {
                using var doc = JsonDocument.Parse(regel);
                var t = doc.RootElement.GetProperty("t").GetDateTimeOffset();
                if (DateOnly.FromDateTime(t.LocalDateTime) != dag)
                {
                    continue;
                }
                var velden = doc.RootElement.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                lijst.Add(new Gebeurtenis(t, velden.GetValueOrDefault("soort", ""), velden));
            }
            catch
            {
                // Overslaan.
            }
        }
        return lijst;
    }

    private static IEnumerable<string> LeesRegels(string pad)
    {
        try
        {
            if (!File.Exists(pad))
            {
                return Array.Empty<string>();
            }
            // Gedeeld lezen: de tray-app en de Claude-hook schrijven er ondertussen in.
            using var stream = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var regels = new List<string>();
            while (reader.ReadLine() is { } regel)
            {
                if (regel.Length > 0)
                {
                    regels.Add(regel);
                }
            }
            return regels;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static DateTimeOffset? TijdUit(string regel, string veld)
    {
        try
        {
            using var doc = JsonDocument.Parse(regel);
            return doc.RootElement.GetProperty(veld).GetDateTimeOffset();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Knipt (1×/dag) gebeurtenissen ouder dan 400 dagen weg.</summary>
    private static void RuimOpAlsNodig()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (_opgeruimd == vandaag || !File.Exists(Gebeurtenissen))
        {
            return;
        }
        _opgeruimd = vandaag;
        try
        {
            var grens = DateTimeOffset.Now - BewaarGebeurtenissen;
            var regels = LeesRegels(Gebeurtenissen).ToList();
            var vers = regels.Where(l => TijdUit(l, "t") is { } t && t >= grens).ToList();
            if (vers.Count < regels.Count)
            {
                File.WriteAllLines(Gebeurtenissen, vers);
            }
        }
        catch
        {
            // Morgen opnieuw.
        }
    }

    private static string Kort(string? tekst, int max)
    {
        tekst = (tekst ?? "").Trim();
        return tekst.Length <= max ? tekst : tekst[..(max - 1)].TrimEnd() + "…";
    }
}
