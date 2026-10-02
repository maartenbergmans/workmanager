using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Algemene activiteitenlog: elke minuut wordt het voorgrondvenster (proces + titel)
/// weggeschreven naar %APPDATA%\WorkManager\activiteiten-log.jsonl. Samen met de
/// contextswitches, de launcher-log, de meetings, de Claude Code-opdrachten en de
/// verzonden mails vormt dat het bronmateriaal voor het dagelijkse timesheetvoorstel
/// (knop "Dagvoorstel…" in de cockpit): Claude clustert de sporen tot regels die na
/// controle in de timesheetwachtrij gaan.
/// </summary>
public static class ActiviteitenLog
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string LogBestand = Path.Combine(DataDir, "activiteiten-log.jsonl");
    private static readonly string SwitchLog = Path.Combine(DataDir, "switch-log.jsonl");
    private static readonly string LauncherLog = Path.Combine(DataDir, "launcher.log");
    private static readonly string ClaudeLog = Path.Combine(DataDir, "claude-requests.jsonl");

    /// <summary>Ouder dan dit wordt bij de dagelijkse opruiming uit de log geknipt.</summary>
    private static readonly TimeSpan Bewaartermijn = TimeSpan.FromDays(21);

    /// <summary>Geen samples zolang de gebruiker langer dan dit niets aanraakt (lunch, weg).</summary>
    private static readonly TimeSpan IdleGrens = TimeSpan.FromMinutes(5);

    private static DateOnly _opgeruimd = DateOnly.MinValue;

    private sealed record Sample(DateTimeOffset T, string Proces, string Titel);

    // ---------------------------------------------------------------- vastleggen

    /// <summary>Eén minuutsample: het voorgrondvenster bijschrijven. Stil bij elke tegenslag.</summary>
    public static void Noteer()
    {
        try
        {
            if (IdleTijd() > IdleGrens)
            {
                return; // niemand aan het toetsenbord: gat in de log = afwezig
            }
            var venster = GetForegroundWindow();
            if (venster == IntPtr.Zero)
            {
                return;
            }
            GetWindowThreadProcessId(venster, out var pid);
            if (pid == 0)
            {
                return;
            }
            string proces;
            try
            {
                proces = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                return;
            }
            var sb = new StringBuilder(260);
            _ = GetWindowText(venster, sb, sb.Capacity);

            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogBestand, JsonSerializer.Serialize(new
            {
                t = DateTimeOffset.Now,
                proces,
                titel = sb.ToString(),
            }) + Environment.NewLine);

            RuimOpAlsNodig();
        }
        catch
        {
            // De log is een hulpmiddel; hij mag de tray-app nooit hinderen.
        }
    }

    /// <summary>
    /// Eén interactieve Claude Code-opdracht (UserPromptSubmit-hook) bijschrijven; de map
    /// verraadt de klant. Elke opdracht staat in het dagvoorstel voor minstens 20 minuten
    /// werk, ook als het voorgrondvenster intussen iets anders toonde.
    /// </summary>
    public static void NoteerClaudeRequest(string map)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(ClaudeLog, JsonSerializer.Serialize(new
            {
                t = DateTimeOffset.Now,
                map,
            }) + Environment.NewLine);
        }
        catch
        {
            // Best effort — de hook mag de Claude-sessie nooit storen.
        }
    }

    /// <summary>Knipt (1×/dag) regels ouder dan de bewaartermijn uit de logbestanden.</summary>
    private static void RuimOpAlsNodig()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (_opgeruimd == vandaag)
        {
            return;
        }
        _opgeruimd = vandaag;
        var grens = DateTimeOffset.Now - Bewaartermijn;
        try
        {
            var vers = File.ReadAllLines(LogBestand)
                .Where(l => ParseSample(l) is { } s && s.T >= grens)
                .ToList();
            File.WriteAllLines(LogBestand, vers);
        }
        catch
        {
            // Volgende dag opnieuw.
        }
        try
        {
            if (File.Exists(ClaudeLog))
            {
                File.WriteAllLines(ClaudeLog, File.ReadAllLines(ClaudeLog)
                    .Where(l => ParseTijd(l) is { } t && t >= grens));
            }
        }
        catch
        {
            // Volgende dag opnieuw.
        }
    }

    private static DateTimeOffset? ParseTijd(string regel)
    {
        try
        {
            using var doc = JsonDocument.Parse(regel);
            return doc.RootElement.GetProperty("t").GetDateTimeOffset();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- voorstel

    /// <summary>
    /// Zijn er voor deze dag al werksporen (venstersamples, contextswitches, launcher-starts
    /// of Claude-opdrachten)? Zonder sporen heeft een dagvoorstel-run geen zin.
    /// </summary>
    public static bool HeeftSporen(DateOnly dag) =>
        VensterBlokken(dag).Count > 0 || SwitchRegels(dag).Count > 0 ||
        LauncherRegels(dag).Count > 0 || ClaudeRegels(dag).Count > 0;

    /// <summary>
    /// Laat Claude van alle sporen van de dag een timesheetvoorstel maken, met een losse
    /// toelichting over de gemaakte keuzes. Geeft een lege lijst als er niets bruikbaars
    /// uit komt.
    /// </summary>
    public static async Task<(List<TimesheetRegel> Regels, string Toelichting)> VoorstelAsync(
        DateOnly dag, List<AgendaClient.AgendaItem> meetings, CancellationToken ct)
    {
        var klokTotaal = System.Diagnostics.Stopwatch.StartNew();
        var bestaand = TimesheetStore.Load().Where(r => r.Datum == dag && r.Minuten > 0).ToList();

        // De drie externe ophalers (Gmail-IMAP, OWA-scrape, Teams-scrape) kunnen elk
        // tientallen seconden duren; in serie liep dat op tot minuten wachten vóór de
        // Claude-run überhaupt begon. Ze starten daarom tegelijk (géén Task.Run: de
        // WebView2-clients zijn thread-gebonden en interleaven prima via async op de
        // UI-thread). Elke ophaler vangt zijn eigen fouten: het voorstel moet ook zonder
        // die bron blijven werken.
        //
        // Elke ophaler heeft bovendien een eigen deadline: een hangende OWA- of Teams-scrape
        // (niet aangemeld, gewijzigde DOM, traag ladende pagina) mocht vroeger het hele
        // voorstel minutenlang ophouden. Na de deadline gaat het voorstel gewoon door zonder
        // dat signaal — dat kost hooguit een regel in het voorstel, geen kwartier wachten.
        // Teams krijgt de kortste: daar moet bij een koud gestarte app eerst een WebView2
        // met de hele Teams-webapp opgebouwd worden (ruim een minuut), terwijl het signaal
        // zelf klein is — "in die chat heb ik vandaag nog gereageerd". Is de sessie warm,
        // dan is hij ruim op tijd; is hij dat niet, dan wachten we er niet op.
        var duren = new List<string>();
        async Task<List<string>> BinnenDeadlineAsync(
            string naam, Func<CancellationToken, Task<List<string>>> ophalen, int seconden)
        {
            var start = DateTimeOffset.Now;
            using var klok = CancellationTokenSource.CreateLinkedTokenSource(ct);
            klok.CancelAfter(TimeSpan.FromSeconds(seconden));
            List<string> uit;
            try
            {
                uit = await ophalen(klok.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                uit = new List<string>(); // te traag: zonder dit signaal verder
            }
            catch
            {
                uit = new List<string>();
            }
            duren.Add($"{naam} {(DateTimeOffset.Now - start).TotalSeconds:0.0}s ({uit.Count})");
            return uit;
        }

        async Task<List<string>> GmailVerzondenAsync(CancellationToken bronCt)
        {
            try
            {
                var mailSettings = MailReplySettings.Load();
                return mailSettings.AppWachtwoord.Length > 0
                    ? (await GmailClient.VerzondenVanDagAsync(mailSettings, dag, bronCt))
                        .Select(r => $"{r} [Gmail]").ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>(); // dan zonder maillijst
            }
        }
        // CED-Outlook: mails die dáár verstuurd zijn, ziet Gmail niet.
        async Task<List<string>> OutlookVerzondenAsync(CancellationToken bronCt)
        {
            try
            {
                return OutlookClient.OoitGekoppeld
                    ? (await OutlookClient.Instance.VerzondenVanDagAsync(dag, bronCt))
                        .Select(r => $"{r} [CED-Outlook]").ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>(); // niet aangemeld (MFA) of OWA hapert
            }
        }
        // Teams toont alleen het laatste bericht per chat, en alleen vandaag is uit de
        // chatlijst af te lezen — voor een eerdere dag valt dit signaal gewoon weg.
        async Task<List<string>> TeamsChatsAsync(CancellationToken bronCt)
        {
            try
            {
                return TeamsClient.OoitGekoppeld && dag == DateOnly.FromDateTime(DateTime.Now)
                    ? await TeamsClient.Instance.MijnChatsVanVandaagAsync(bronCt)
                    : new List<string>();
            }
            catch
            {
                return new List<string>(); // niet ingelogd of DOM gewijzigd
            }
        }
        var gmailTaak = BinnenDeadlineAsync("gmail", GmailVerzondenAsync, 45);
        var outlookTaak = BinnenDeadlineAsync("owa", OutlookVerzondenAsync, 45);
        var teamsTaak = BinnenDeadlineAsync("teams", TeamsChatsAsync, 25);
        // De projectenlijst (met gebruik en omschrijvingen van de laatste 60 dagen) meteen
        // mee verversen: daarop kiest Claude het project per regel.
        var catalogusTaak = ProjectCatalogus.VernieuwAlsNodigAsync(ct);
        await Task.WhenAll(gmailTaak, outlookTaak, teamsTaak, catalogusTaak);
        var verzonden = gmailTaak.Result.Concat(outlookTaak.Result).ToList();
        var teamsChats = teamsTaak.Result;
        var prompt = $$"""
            Je zet de werkdag van Maarten (freelance IT'er, UrbanIT) om in timesheetregels.

            Datum: {{dag:dddd d MMMM yyyy}}

            SIGNALEN VAN DIE DAG

            1) Voorgrondvensters (per blok, uit de minuutlog):
            {{Blok(VensterBlokken(dag), "nog geen samples — de activiteitenlog is pas net gestart")}}

            2) Werkcontexten aan/uit gezet:
            {{Blok(SwitchRegels(dag), "geen contextswitches")}}

            3) Gestarte tools (launcher):
            {{Blok(LauncherRegels(dag), "niets gestart")}}

            4) Meetings (agenda):
            {{Blok(meetings.Where(m => !m.HeleDag && !GeenWerktijd.Is(m.Titel))
                .Select(m => $"{m.Start.LocalDateTime:HH:mm}–{m.Einde.LocalDateTime:HH:mm} {m.Titel}"),
                "geen meetings")}}

            5) Opdrachten aan Claude Code, per projectmap gebundeld in blokken van 20 minuten
            (tijdvak, map, aantal opdrachten — parallelle sessies en snelle vervolgvragen
            vallen zo niet dubbel):
            {{Blok(ClaudeBlokken(dag), "geen Claude-opdrachten")}}

            6) Verzonden mails (Gmail én CED-Outlook):
            {{Blok(verzonden, "geen verzonden mails (of niet op te halen)")}}

            7) Teams-chats waarin ik vandaag het laatste woord had (alleen het laatste
            bericht per chat is zichtbaar — er kan dus méér gestuurd zijn):
            {{Blok(teamsChats, "geen Teams-chats (of niet op te halen)")}}

            8) Al geboekte timesheetregels van die dag:
            {{Blok(bestaand.Select(r =>
                $"{(r.Van is { } v ? v.ToString("HH:mm") : "??:??")} {r.Klant} {r.Minuten} min — {r.Omschrijving}"),
                "nog niets geboekt")}}

            PROJECTEN — kies per regel exact één project uit deze urbanadmin-lijst en geef
            het id terug. Achter elk project staat hoeveel er de laatste tijd op geboekt
            werd en wat de recentste omschrijvingen waren: kies het project waarop dit soort
            werk normaal terechtkomt. Meest gebruikte projecten staan bovenaan.
            {{ProjectCatalogus.PromptBlok()}}

            Vuistregels (projectmap, tool of onderwerp → project):
            - CED: TopDesk, CED-Outlook, Teams, ISPnext, Azure DevOps/CAREX, CED-meetings,
              automaticmail → CED Belgium · consultancy (dagbasis). Repalink-support voor
              CED Nederland (repalink-backend/-frontend, vakantie-upload, schades,
              meldingen) → CED Nederland · Repalink Support. totalloss-cednl → een CED
              Nederland-project als er één over Totalloss gaat, anders CED Belgium ·
              consultancy (dagbasis).
            - Aqurat (map aqurat, Asana): meetings → Vergaderingen nieuwe app; analysewerk →
              Analyse; ontwikkelwerk → het ontwikkelproject waarvan de recente
              omschrijvingen het best passen (bij twijfel het meest recent gebruikte).
            - bloom, bloom-datawarehouse, BloomDataUploader, Ximeo → Radiology Partners
              Europe · IT advies.
            - Vriesveem (Nemijtek valt sinds de fusie onder Vriesveem — nooit de klant
              "Nemijtek Vrieshuizen OUD"): movaware(-backend/-frontend) → Doorontwikkeling
              Movaware (nieuwe functies, rapporten) of Support Movaware (fouten, vragen);
              cellaware(-backend/-frontend/-klantportaal) → Doorontwikkeling Cellaware
              (nieuwe functies, analyses, rapporten) of Support Cellaware Vriesveem /
              Support Cellaware Nemijtek (supportvraag van die vestiging); algemeen
              ICT-overleg, SAP, fusie → ICT management; website → website aanpassingen.
            - Lauryssens: laurapp(-backend) → ontwikkeling LaurApp;
              lauryssens-herstel-calculator → herstel calculator; glascalculator, advies,
              overleg en mails → advies en consultancy.
            - citroenloos, Garage Loos → Garage Loos · Integratie planning.
            - vakantiehuis-bourgogne → Ellu-Invest · Vakantiehuis Bourgogne.
            - UrbanIT: urbanadmin (timesheets.urbanit.be) → UrbanIT · ontwikkeling UrbanAdmin;
              WorkManager → UrbanIT · ontwikkeling WorkManager als dat in de lijst staat,
              anders UrbanIT · administratie; facturatie en boekhouding → UrbanIT ·
              administratie; intern overleg → UrbanIT · vergadering.
            - Privézaken (AH-boodschappen, agenda gezin, …) → project {{ProjectCatalogus.NietFactureerbaarId}}.
            Geplande maaltijden (🍴-recepten, avondeten, koken) en de AH-levering zijn géén
            werktijd: daar komt helemaal geen regel voor — ook niet als niet-factureerbaar.

            MEETREGELS — zo meet je de tijd:
            - Elke meeting uit de agenda hoort als regel in het voorstel (duur = de
              agendaduur, bij de juiste klant), tenzij die tijd al door een geboekte regel
              gedekt is.
            - Elk blok van 20 minuten met Claude-opdrachten (signaal 5) telt voor minstens
              20 minuten in dat project, ook als het voorgrondvenster intussen iets anders
              toonde: het werk loopt op de achtergrond door. Aaneengesloten blokken in
              hetzelfde project voeg je samen tot één regel.
            - Elke verzonden mail telt voor minstens 15 minuten; een langere mail
              (± 150 woorden of meer) voor 20 minuten. Ook hier mag je bundelen per klant,
              met dezelfde ondergrens. Van CED-Outlookmails is geen woordental bekend
              (alleen een stukje preview): reken daar 15 minuten, of 20 als onderwerp en
              preview duidelijk een lange inhoudelijke mail verraden.
            - Elke Teams-chat waarin ik reageerde telt voor minstens 15 minuten CED-werk
              (per chat, niet per berichtje); meerdere chats mag je bundelen tot één
              regel met dezelfde ondergrens.
            - Twee dingen tegelijk doen is normaal (een meeting bijwonen terwijl een
              Claude-opdracht doorloopt): regels mogen dan in tijd overlappen.
            - De al geboekte regels zijn al gedekt: die tijd niet opnieuw voorstellen,
              alleen aanvullen wat nog ontbreekt.

            BOVENGRENS: die dag telde {{ActieveMinuten(dag)}} actieve minuten achter de pc en
            {{meetings.Where(m => !m.HeleDag && !GeenWerktijd.Is(m.Titel)).Sum(m => (int)(m.Einde - m.Start).TotalMinutes)}}
            minuten meetings. Het totaal van alle regels (inclusief de al geboekte) mag niet
            hoger liggen dan die twee samen: wat daarboven komt is dubbel geteld.

            OPDRACHT: maak een beknopt, realistisch dagvoorstel dat de gewerkte tijd dekt.
            Geef elke regel een starttijd "van" in 24-uursnotatie (HH:mm).
            Blokken van minstens 15 min, afgerond op 15 min, aaneensluitend waar dat logisch
            is. Korte zakelijke omschrijving in het Nederlands per regel; gelijkaardig werk
            samenvoegen in plaats van versnipperen. In "toelichting" mag je gerust wat
            uitgebreider uitleggen welke keuzes en aannames je maakte (wat je bundelde, wat
            je wegliet, waar signalen elkaar overlapten) — die uitleg komt níét in de
            timesheets terecht.

            Antwoord uitsluitend met JSON, exact dit formaat (geen extra tekst):
            {"regels": [{"van": "HH:mm", "minuten": 60, "project_id": 1, "omschrijving": "…"}], "toelichting": "…"}
            """;

        var klokClaude = System.Diagnostics.Stopwatch.StartNew();
        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        klokClaude.Stop();
        NoteerDuur(dag, duren, klokClaude.Elapsed, klokTotaal.Elapsed);
        using var doc = ClaudeDrafter.ParseJson(output);
        var voorstel = new List<TimesheetRegel>();
        var toelichting = doc.RootElement.TryGetProperty("toelichting", out var uitleg) &&
            uitleg.ValueKind == JsonValueKind.String ? uitleg.GetString() ?? "" : "";
        if (!doc.RootElement.TryGetProperty("regels", out var lijst) ||
            lijst.ValueKind != JsonValueKind.Array)
        {
            return (voorstel, toelichting);
        }
        foreach (var el in lijst.EnumerateArray())
        {
            // project_id is de afspraak; een los label of klantnaam wordt ook nog herkend.
            var project = el.TryGetProperty("project_id", out var pid) &&
                (pid.TryGetInt32(out var pidv) ||
                 (pid.ValueKind == JsonValueKind.String && int.TryParse(pid.GetString(), out pidv)))
                ? ProjectCatalogus.Zoek(pidv)
                : null;
            project ??= ProjectCatalogus.Zoek(
                el.TryGetProperty("klant", out var k) ? k.GetString() : null);
            project ??= ProjectCatalogus.Zoek(ProjectCatalogus.NietFactureerbaarId);
            var minuten = el.TryGetProperty("minuten", out var m) &&
                m.TryGetInt32(out var mv) ? Math.Clamp(mv, 5, 600) : 0;
            var omschrijving = el.TryGetProperty("omschrijving", out var o) ? o.GetString() ?? "" : "";
            TimeOnly? van = el.TryGetProperty("van", out var v) &&
                TimeOnly.TryParse(v.GetString(), out var vt) ? vt : null;
            if (minuten > 0 && omschrijving.Length > 0)
            {
                voorstel.Add(new TimesheetRegel
                {
                    Datum = dag,
                    Van = van,
                    Klant = project?.Label ?? ProjectCatalogus.NietFactureerbaar,
                    ProjectId = project?.Id,
                    Minuten = minuten,
                    Omschrijving = omschrijving,
                    Bron = "dagvoorstel",
                });
            }
        }
        return (voorstel.OrderBy(r => r.Van ?? TimeOnly.MaxValue).ToList(), toelichting);
    }

    /// <summary>
    /// Eén regel per dagvoorstel-run in dagvoorstel-timing.log: hoe lang elke bron deed en
    /// hoe lang de Claude-run zelf. Bij "het duurt weer lang" is daarmee in één oogopslag te
    /// zien of het aan een scrape ligt of aan de run.
    /// </summary>
    private static void NoteerDuur(
        DateOnly dag, List<string> bronnen, TimeSpan claude, TimeSpan totaal)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "dagvoorstel-timing.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {dag:yyyy-MM-dd} " +
                $"bronnen[{string.Join(", ", bronnen)}] claude {claude.TotalSeconds:0.0}s " +
                $"totaal {totaal.TotalSeconds:0.0}s" + Environment.NewLine);
        }
        catch
        {
            // Meten mag het voorstel nooit in de weg zitten.
        }
    }

    /// <summary>De interactieve Claude Code-opdrachten van één dag, als "HH:mm projectmap".</summary>
    private static List<string> ClaudeRegels(DateOnly dag)
    {
        try
        {
            if (!File.Exists(ClaudeLog))
            {
                return new List<string>();
            }
            var regels = new List<string>();
            foreach (var lijn in File.ReadLines(ClaudeLog))
            {
                try
                {
                    using var doc = JsonDocument.Parse(lijn);
                    var t = doc.RootElement.GetProperty("t").GetDateTimeOffset();
                    if (DateOnly.FromDateTime(t.LocalDateTime) != dag)
                    {
                        continue;
                    }
                    var map = doc.RootElement.TryGetProperty("map", out var m)
                        ? m.GetString() ?? "" : "";
                    regels.Add($"{t.LocalDateTime:HH:mm} {MapLabel(map)}");
                }
                catch
                {
                    // Kapotte regel overslaan.
                }
            }
            return regels;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// De Claude-opdrachten van één dag per projectmap gebundeld in blokken van 20 minuten;
    /// aaneengesloten blokken worden één tijdvak: "10:00–11:00 aqurat (7 opdrachten)".
    /// </summary>
    private static List<string> ClaudeBlokken(DateOnly dag)
    {
        var perMap = new Dictionary<string, SortedDictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var regel in ClaudeRegels(dag))
        {
            // "HH:mm map"
            if (regel.Length < 7 || !TimeOnly.TryParse(regel[..5], out var t))
            {
                continue;
            }
            var map = regel[6..];
            if (!perMap.TryGetValue(map, out var slots))
            {
                perMap[map] = slots = new SortedDictionary<int, int>();
            }
            var slot = (t.Hour * 60 + t.Minute) / 20;
            slots[slot] = slots.GetValueOrDefault(slot) + 1;
        }
        var uit = new List<(int Start, string Tekst)>();
        foreach (var (map, slots) in perMap)
        {
            int? start = null, vorige = null, aantal = 0;
            void Sluit()
            {
                if (start is { } s && vorige is { } v)
                {
                    var van = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(s * 20));
                    var tot = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Min((v + 1) * 20, 24 * 60 - 1)));
                    uit.Add((s, $"{van:HH:mm}–{tot:HH:mm} {map} ({aantal} opdracht{(aantal == 1 ? "" : "en")})"));
                }
            }
            foreach (var (slot, n) in slots)
            {
                if (vorige is { } v && slot != v + 1)
                {
                    Sluit();
                    start = null;
                    aantal = 0;
                }
                start ??= slot;
                vorige = slot;
                aantal += n;
            }
            Sluit();
        }
        return uit.OrderBy(u => u.Start).Select(u => u.Tekst).ToList();
    }

    /// <summary>Het aantal minuten met invoer (één sample per actieve minuut) op die dag.</summary>
    private static int ActieveMinuten(DateOnly dag)
    {
        try
        {
            return File.Exists(LogBestand)
                ? File.ReadLines(LogBestand).Count(l => ParseSample(l) is { } s && DateOnly.FromDateTime(s.T.LocalDateTime) == dag)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// De projectmap als label; generieke submappen ("webapp", "backend") krijgen hun
    /// bovenliggende map erbij, anders is niet te zien bij welk project ze horen.
    /// </summary>
    private static string MapLabel(string map)
    {
        var delen = map.TrimEnd('\\', '/').Split('\\', '/').Where(d => d.Length > 0).ToArray();
        if (delen.Length == 0)
        {
            return "?";
        }
        var naam = delen[^1];
        return delen.Length > 1 && naam.ToLowerInvariant() is "webapp" or "backend" or "frontend" or "src" or "app" or "api" or "html"
            ? $"{delen[^2]}/{naam}"
            : naam;
    }

    private static string Blok(IEnumerable<string> regels, string leeg)
    {
        var lijst = regels.Take(150).ToList();
        return lijst.Count == 0 ? "(" + leeg + ")" : string.Join("\n", lijst);
    }

    /// <summary>
    /// Clustert de minuutsamples van één dag tot blokken: nieuw blok bij een ander proces of
    /// een gat van meer dan vijf minuten. Blokjes korter dan drie minuten zijn ruis.
    /// </summary>
    private static List<string> VensterBlokken(DateOnly dag)
    {
        var samples = LeesSamples(dag);
        var blokken = new List<string>();
        for (var i = 0; i < samples.Count;)
        {
            var start = i;
            while (i + 1 < samples.Count &&
                   samples[i + 1].Proces == samples[start].Proces &&
                   samples[i + 1].T - samples[i].T <= TimeSpan.FromMinutes(5))
            {
                i++;
            }
            var minuten = (int)(samples[i].T - samples[start].T).TotalMinutes + 1;
            if (minuten >= 3)
            {
                var titel = samples.Skip(start).Take(i - start + 1)
                    .Select(s => s.Titel)
                    .Where(t => t.Length > 0)
                    .GroupBy(t => t)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? "";
                blokken.Add($"{samples[start].T:HH:mm}–{samples[i].T:HH:mm} " +
                    $"{samples[start].Proces} — {Kort(titel)} ({minuten} min)");
            }
            i++;
        }
        return blokken;
    }

    private static string Kort(string tekst) =>
        tekst.Length <= 90 ? tekst : tekst[..87] + "…";

    private static List<Sample> LeesSamples(DateOnly dag)
    {
        try
        {
            if (!File.Exists(LogBestand))
            {
                return new List<Sample>();
            }
            return File.ReadAllLines(LogBestand)
                .Select(ParseSample)
                .OfType<Sample>()
                .Where(s => DateOnly.FromDateTime(s.T.LocalDateTime) == dag)
                .OrderBy(s => s.T)
                .ToList();
        }
        catch
        {
            return new List<Sample>();
        }
    }

    private static Sample? ParseSample(string regel)
    {
        try
        {
            using var doc = JsonDocument.Parse(regel);
            return new Sample(
                doc.RootElement.GetProperty("t").GetDateTimeOffset(),
                doc.RootElement.TryGetProperty("proces", out var p) ? p.GetString() ?? "" : "",
                doc.RootElement.TryGetProperty("titel", out var t) ? t.GetString() ?? "" : "");
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SwitchRegels(DateOnly dag)
    {
        var regels = new List<string>();
        try
        {
            if (!File.Exists(SwitchLog))
            {
                return regels;
            }
            foreach (var lijn in File.ReadAllLines(SwitchLog))
            {
                try
                {
                    using var doc = JsonDocument.Parse(lijn);
                    var tijd = doc.RootElement.GetProperty("timestamp").GetDateTimeOffset();
                    if (DateOnly.FromDateTime(tijd.LocalDateTime) != dag)
                    {
                        continue;
                    }
                    regels.Add($"{tijd:HH:mm} {doc.RootElement.GetProperty("client").GetString()} " +
                        $"{doc.RootElement.GetProperty("action").GetString()}");
                }
                catch
                {
                    // Kapotte regel overslaan.
                }
            }
        }
        catch
        {
            // Geen switch-log: dan zonder.
        }
        return regels;
    }

    private static List<string> LauncherRegels(DateOnly dag)
    {
        try
        {
            if (!File.Exists(LauncherLog))
            {
                return new List<string>();
            }
            var prefix = dag.ToString("yyyy-MM-dd");
            return File.ReadLines(LauncherLog)
                .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
                .Select(l => l[prefix.Length..].Trim())
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---------------------------------------------------------------- win32

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder tekst, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private static TimeSpan IdleTijd()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime))
            : TimeSpan.Zero;
    }
}
