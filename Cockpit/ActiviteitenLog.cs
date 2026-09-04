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
    /// Laat Claude van alle sporen van de dag een timesheetvoorstel maken, met een losse
    /// toelichting over de gemaakte keuzes. Geeft een lege lijst als er niets bruikbaars
    /// uit komt.
    /// </summary>
    public static async Task<(List<TimesheetRegel> Regels, string Toelichting)> VoorstelAsync(
        DateOnly dag, List<AgendaClient.AgendaItem> meetings, CancellationToken ct)
    {
        var bestaand = TimesheetStore.Load().Where(r => r.Datum == dag && r.Minuten > 0).ToList();
        // Verzonden mails zijn een hard signaal (elke mail = minstens een kwartier), maar
        // het voorstel moet ook zonder Gmail-verbinding blijven werken.
        var verzonden = new List<string>();
        try
        {
            var mailSettings = MailReplySettings.Load();
            if (mailSettings.AppWachtwoord.Length > 0)
            {
                verzonden = (await GmailClient.VerzondenVanDagAsync(mailSettings, dag, ct))
                    .Select(r => $"{r} [Gmail]").ToList();
            }
        }
        catch
        {
            // Dan zonder maillijst.
        }
        // Zelfde verhaal voor CED-Outlook: mails die dáár verstuurd zijn, ziet Gmail niet.
        try
        {
            if (OutlookClient.OoitGekoppeld)
            {
                verzonden.AddRange((await OutlookClient.Instance.VerzondenVanDagAsync(dag, ct))
                    .Select(r => $"{r} [CED-Outlook]"));
            }
        }
        catch
        {
            // Outlook niet aangemeld (dagelijkse MFA) of OWA hapert: dan zonder.
        }
        // Teams toont alleen het laatste bericht per chat, en alleen vandaag is uit de
        // chatlijst af te lezen — voor een eerdere dag valt dit signaal gewoon weg.
        var teamsChats = new List<string>();
        try
        {
            if (TeamsClient.OoitGekoppeld && dag == DateOnly.FromDateTime(DateTime.Now))
            {
                teamsChats = await TeamsClient.Instance.MijnChatsVanVandaagAsync(ct);
            }
        }
        catch
        {
            // Teams niet ingelogd of DOM gewijzigd: dan zonder.
        }
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
            {{Blok(meetings.Where(m => !m.HeleDag)
                .Select(m => $"{m.Start.LocalDateTime:HH:mm}–{m.Einde.LocalDateTime:HH:mm} {m.Titel}"),
                "geen meetings")}}

            5) Opdrachten aan Claude Code (tijdstip + projectmap):
            {{Blok(ClaudeRegels(dag), "geen Claude-opdrachten")}}

            6) Verzonden mails (Gmail én CED-Outlook):
            {{Blok(verzonden, "geen verzonden mails (of niet op te halen)")}}

            7) Teams-chats waarin ik vandaag het laatste woord had (alleen het laatste
            bericht per chat is zichtbaar — er kan dus méér gestuurd zijn):
            {{Blok(teamsChats, "geen Teams-chats (of niet op te halen)")}}

            8) Al geboekte timesheetregels van die dag:
            {{Blok(bestaand.Select(r =>
                $"{(r.Van is { } v ? v.ToString("HH:mm") : "??:??")} {r.Klant} {r.Minuten} min — {r.Omschrijving}"),
                "nog niets geboekt")}}

            KLANTEN — kies per regel exact één van: {{string.Join(", ", TimesheetStore.Klanten)}}.
            Vuistregels: TopDesk, Outlook, CED-meetings en ced.topdesk.net → CED. aqurat → Aqurat.
            bloom, datawarehouse, BloomDataUploader, RadiologyPartners → RadiologyPartners.
            Lauryssens-ontwikkelwerk (laurapp, herstel-calculator, glascalculator) →
            Lauryssens laurapp; Lauryssens-advies, -overleg of -mails → Lauryssens advies.
            WorkManager-ontwikkeling, urbanadmin, facturatie/administratie → UrbanIT.
            Privézaken (AH-boodschappen, agenda gezin, …) → Niet factureerbaar.
            Geplande maaltijden (🍴-recepten, avondeten, koken) zijn géén werktijd: daar komt
            helemaal geen regel voor — ook niet als "Niet factureerbaar".

            MEETREGELS — zo meet je de tijd:
            - Elke meeting uit de agenda hoort als regel in het voorstel (duur = de
              agendaduur, bij de juiste klant), tenzij die tijd al door een geboekte regel
              gedekt is.
            - Elke opdracht aan Claude Code telt voor minstens 20 minuten, ook als het
              voorgrondvenster intussen iets anders toonde: het werk loopt op de
              achtergrond door. Meerdere opdrachten in hetzelfde project mag je bundelen,
              maar de som blijft minstens het aantal opdrachten × 20 minuten.
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

            OPDRACHT: maak een beknopt, realistisch dagvoorstel dat de gewerkte tijd dekt.
            Blokken van minstens 15 min, afgerond op 15 min, aaneensluitend waar dat logisch
            is. Korte zakelijke omschrijving in het Nederlands per regel; gelijkaardig werk
            samenvoegen in plaats van versnipperen. In "toelichting" mag je gerust wat
            uitgebreider uitleggen welke keuzes en aannames je maakte (wat je bundelde, wat
            je wegliet, waar signalen elkaar overlapten) — die uitleg komt níét in de
            timesheets terecht.

            Antwoord uitsluitend met JSON, exact dit formaat (geen extra tekst):
            {"regels": [{"van": "HH:mm", "minuten": 60, "klant": "CED", "omschrijving": "…"}], "toelichting": "…"}
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
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
            var klant = el.TryGetProperty("klant", out var k) ? k.GetString() ?? "" : "";
            klant = TimesheetStore.Klanten.FirstOrDefault(
                    c => c.Equals(klant, StringComparison.OrdinalIgnoreCase))
                ?? "Niet factureerbaar";
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
                    Klant = klant,
                    Minuten = minuten,
                    Omschrijving = omschrijving,
                    Bron = "dagvoorstel",
                });
            }
        }
        return (voorstel.OrderBy(r => r.Van ?? TimeOnly.MaxValue).ToList(), toelichting);
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
                    regels.Add($"{t.LocalDateTime:HH:mm} {Path.GetFileName(map.TrimEnd('\\', '/'))}");
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
