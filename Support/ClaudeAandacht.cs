using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Brug tussen Claude Code en WorkManager: de Notification-hook van Claude Code roept
/// "WorkManager.exe --claude-aandacht" aan met het hook-JSON op stdin. Dat korte proces
/// schrijft een signaalbestand mét het proces én het vensterhandle van het terminalvenster
/// waarin de sessie draait (de eerste voorouder met een echt venster — Windows Terminal,
/// VS Code, …).
/// De tray-app leest de spoolmap elke paar seconden en toont een klikbare
/// <see cref="TrayMelding"/>; de klik haalt dat terminalvenster naar de voorgrond, precies
/// waar getypt moet worden. Sneller en betrouwbaarder dan de Windows-ballon die Claude Code
/// zelf toonde, en mét rechtstreekse doorklik.
/// </summary>
public static class ClaudeAandacht
{
    private static readonly string SpoolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "claude-aandacht");

    private sealed class Signaal
    {
        public string Boodschap { get; set; } = "";
        public string Map { get; set; } = "";
        public string Event { get; set; } = "";
        public bool Headless { get; set; }
        public int VensterPid { get; set; }
        public long VensterHandle { get; set; }
        public DateTimeOffset Moment { get; set; }
    }

    /// <summary>
    /// Hook-kant (het korte --claude-aandacht-proces): leest het hook-JSON van Claude Code
    /// en zet het signaal in de spoolmap. Fouten blijven stil — een haperende melding mag
    /// de Claude-sessie zelf nooit storen. Retourneert eventueel structured hook-output
    /// (JSON voor stdout) die de tabtitel op "Klant — map · status" zet.
    /// </summary>
    public static string SchrijfSignaal(string hookJson)
    {
        try
        {
            var boodschap = "";
            var map = "";
            var hookEvent = "";
            try
            {
                using var doc = JsonDocument.Parse(hookJson);
                boodschap = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "" : "";
                map = doc.RootElement.TryGetProperty("cwd", out var c)
                    ? c.GetString() ?? "" : "";
                hookEvent = doc.RootElement.TryGetProperty("hook_event_name", out var e)
                    ? e.GetString() ?? "" : "";
                // Het Stop-event (sessie afgerond) heeft geen message-veld; zonder eigen
                // tekst zou de melding misleidend "Aandacht gevraagd" zeggen.
                if (boodschap.Length == 0 && hookEvent == "Stop")
                {
                    boodschap = "Klaar — de opdracht is afgerond";
                }
            }
            catch
            {
                // Geen of kapot JSON: dan een kale melding zonder details.
            }
            var headless = IsHeadlessSessie();
            // Elke interactieve opdracht telt mee in het dagvoorstel (minstens 20 min);
            // headless 'claude -p'-runs zijn WorkManagers eigen automatiek en tellen niet.
            if (!headless && hookEvent == "UserPromptSubmit" && map.Length > 0)
            {
                ActiviteitenLog.NoteerClaudeRequest(map);
            }
            Directory.CreateDirectory(SpoolDir);
            var pid = headless ? 0 : VindTerminalPid();
            File.WriteAllText(
                Path.Combine(SpoolDir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}.json"),
                JsonSerializer.Serialize(new Signaal
                {
                    Boodschap = boodschap,
                    Map = map,
                    Event = hookEvent,
                    Headless = headless,
                    VensterPid = pid,
                    VensterHandle = headless ? 0 : VindTerminalVenster(pid, map),
                    Moment = DateTimeOffset.Now,
                }));
            return headless ? "" : TitelOutput(hookEvent, map);
        }
        catch
        {
            // Best effort.
            return "";
        }
    }

    /// <summary>
    /// Structured hook-output die de tab op "Klant — map · status" zet (terminalSequence,
    /// OSC 0). Alleen bij Notification en Stop: dáár is stdout-JSON gegarandeerd puur
    /// besturing; tijdens het werken laat Claude Code zijn eigen taakomschrijving staan.
    /// </summary>
    private static string TitelOutput(string hookEvent, string map)
    {
        if (map.Length == 0 || hookEvent is not ("Notification" or "Stop"))
        {
            return "";
        }
        var label = ClientLauncher.SessieLabel(map);
        var titel = hookEvent == "Stop" ? $"{label} · ✅ klaar" : $"{label} · 🔔 wacht";
        return JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = hookEvent,
                terminalSequence = $"\u001b]0;{titel}\u0007",
            },
        });
    }

    /// <summary>
    /// Draait deze hook onder een headless 'claude -p'-run (mailconcepten, weekmail, …)?
    /// Die runs horen niet in het sessiepaneel en hebben geen terminalvenster. Detectie:
    /// de ouderketen van dit hook-proces bevat het claude-proces zelf; zijn commandoregel
    /// verraadt de print-modus. (WSL-sessies hebben geen zichtbare Windows-ouderketen,
    /// maar WorkManagers headless runs draaien allemaal op Windows.)
    /// </summary>
    private static bool IsHeadlessSessie()
    {
        try
        {
            var ouders = OuderTabel();
            var pid = Environment.ProcessId;
            for (var stap = 0; stap < 15 && ouders.TryGetValue(pid, out var ouder) && ouder > 4; stap++)
            {
                pid = ouder;
                try
                {
                    if (!Process.GetProcessById(pid).ProcessName
                        .Equals("claude", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var cmd = ProcessInspector.GetCommandLine(pid) ?? "";
                    return cmd.Contains("--print", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Contains("stream-json", StringComparison.OrdinalIgnoreCase) ||
                        System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\s-p(\s|$)");
                }
                catch
                {
                    break; // ouder is al weg
                }
            }
        }
        catch
        {
            // Detectie is best effort; dan maar als interactief behandelen.
        }
        return false;
    }

    /// <summary>
    /// Tray-app-kant: kijkt elke twee seconden in de spoolmap en toont voor het nieuwste
    /// signaal een klikbare melding. Aanroepen op de UI-thread (de timer tikt in de
    /// berichtenlus, dus de TrayMelding-vensters komen vanzelf goed terecht).
    /// </summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(SpoolDir);
        }
        catch
        {
            // Zonder spoolmap valt er niets te melden.
        }
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => Controleer();
        timer.Start();
    }

    private static void Controleer()
    {
        List<string> bestanden;
        try
        {
            bestanden = Directory.EnumerateFiles(SpoolDir, "*.json")
                .OrderBy(p => p, StringComparer.Ordinal) // naam begint met het tijdstip
                .ToList();
        }
        catch
        {
            return;
        }
        if (bestanden.Count == 0)
        {
            HerinnerVergeten();
            return;
        }
        // Alle signalen voeden het sessieoverzicht; alleen het nieuwste toastbare signaal
        // wordt getoond (meer dan één toast tegelijk kan toch niet) — de rest is
        // achterhaald en gaat gewoon weg.
        Signaal? signaal = null;
        foreach (var pad in bestanden)
        {
            try
            {
                if (JsonSerializer.Deserialize<Signaal>(File.ReadAllText(pad)) is { } s)
                {
                    if (!s.Headless)
                    {
                        ClaudeSessies.Verwerk(
                            s.Map, s.Event.Length > 0 ? s.Event : "Notification",
                            s.Boodschap, s.VensterPid, s.VensterHandle, s.Moment);
                        // Statuswissels (SessionStart/UserPromptSubmit/SessionEnd) zijn
                        // stille updates; alleen echte aandacht verdient een toast.
                        if (s.Event is "" or "Notification" or "Stop")
                        {
                            signaal = s;
                        }
                    }
                }
            }
            catch
            {
                // Half geschreven of kapot: overslaan (en hieronder opruimen).
            }
            try
            {
                File.Delete(pad);
            }
            catch
            {
                // Volgende beurt nog eens; dubbele meldingen vangt de leeftijdstoets af.
            }
        }
        // Oude signalen (WorkManager stond dicht) niet alsnog als vers brengen.
        if (signaal is null || DateTimeOffset.Now - signaal.Moment > TimeSpan.FromMinutes(10))
        {
            HerinnerVergeten();
            return;
        }
        var project = signaal.Map.Length > 0 ? Path.GetFileName(signaal.Map.TrimEnd('\\', '/')) : "";
        var pid = signaal.VensterPid;
        var handle = signaal.VensterHandle;
        var map = signaal.Map;
        TrayMelding.Toon(
            $"🤖 Claude Code{(project.Length > 0 ? $" — {project}" : "")}",
            signaal.Boodschap.Length > 0 ? signaal.Boodschap : "Aandacht gevraagd",
            () => ActiveerTerminal(pid, handle, map),
            duurMs: 20000);
    }

    /// <summary>
    /// Vergeten-sessie-bewaking: een sessie die al ≥ 15 minuten op Maarten wacht (input
    /// gevraagd of klaar) krijgt opnieuw een klikbare melding, en daarna hooguit elk
    /// kwartier nog één. Sessies waarvan het terminalproces weg is worden opgeruimd
    /// (het SessionEnd-event kan bij hard sluiten gemist zijn).
    /// </summary>
    private static void HerinnerVergeten()
    {
        foreach (var s in ClaudeSessies.Snapshot())
        {
            if (s.Status is not (ClaudeSessies.Wacht or ClaudeSessies.Klaar))
            {
                continue;
            }
            var wachttijd = DateTimeOffset.Now - s.Sinds;
            // Na 2 uur stoppen met herinneren: dan is de sessie bewust geparkeerd.
            if (wachttijd < TimeSpan.FromMinutes(15) || wachttijd > TimeSpan.FromHours(2) ||
                DateTimeOffset.Now - s.LaatstHerinnerd < TimeSpan.FromMinutes(15))
            {
                continue;
            }
            if (!SessieLeeftNog(s))
            {
                ClaudeSessies.Verwijder(s.Map);
                continue;
            }
            ClaudeSessies.MarkeerHerinnerd(s.Map);
            var sessie = s;
            TrayMelding.Toon(
                $"🤖 {ClientLauncher.SessieLabel(s.Map)} wacht al {(int)wachttijd.TotalMinutes} min",
                s.Boodschap.Length > 0 ? s.Boodschap : "Wacht op je input",
                () => ActiveerTerminal(sessie.VensterPid, sessie.VensterHandle, sessie.Map),
                duurMs: 20000);
            return; // hooguit één herinnering per tik
        }
    }

    /// <summary>
    /// Leeft het terminalproces van de sessie nog? WSL-sessies hebben geen Windows-pid;
    /// die vertrouwen op hun SessionEnd-event (en de 12-uursopruiming in de store).
    /// </summary>
    private static bool SessieLeeftNog(ClaudeSessies.Sessie s)
    {
        if (s.VensterPid <= 0)
        {
            return true;
        }
        try
        {
            return !Process.GetProcessById(s.VensterPid).HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Haalt het terminalvenster van de sessie naar de voorgrond. Het bij de hook bewaarde
    /// vensterhandle gaat voor: dat is het venster van de sessie zelf, ook als de titel
    /// intussen veranderd is (Claude Code zet er een taakomschrijving in) en ook bij
    /// meerdere vensters van één terminalproces. Pas als dat handle weg is zoeken we op
    /// titel — eerst binnen het bewaarde proces, dan pas bij andere terminalprocessen,
    /// want een VS Code-venster met de projectmap in de titel is niet de terminal.
    /// </summary>
    public static void ActiveerTerminal(int pid, long handle, string map)
    {
        var leaf = map.Length > 0 ? Path.GetFileName(map.TrimEnd('\\', '/')) : "";
        var vensters = AlleTopVensters();
        var vanPid = vensters.Where(v => v.Pid == pid).ToList();

        var venster = IntPtr.Zero;
        var bewaard = new IntPtr(handle);
        if (handle != 0 && vanPid.Any(v => v.Handle == bewaard))
        {
            venster = bewaard;
        }
        if (venster == IntPtr.Zero && leaf.Length > 0)
        {
            venster = vanPid.FirstOrDefault(v => HeeftInTitel(v, leaf)).Handle;
        }
        if (venster == IntPtr.Zero && vanPid.Count == 1)
        {
            venster = vanPid[0].Handle;
        }
        if (venster == IntPtr.Zero && leaf.Length > 0)
        {
            venster = vensters.FirstOrDefault(v => IsTerminalProces(v.Pid) && HeeftInTitel(v, leaf)).Handle;
        }
        if (venster == IntPtr.Zero && pid > 0)
        {
            try
            {
                venster = Process.GetProcessById(pid).MainWindowHandle;
            }
            catch
            {
                // Proces intussen weg: op titel zoeken.
            }
        }
        if (venster == IntPtr.Zero)
        {
            venster = vensters.FirstOrDefault(v => IsTerminalProces(v.Pid) && HeeftInTitel(v, "claude")).Handle;
        }
        if (venster == IntPtr.Zero)
        {
            return;
        }
        if (IsIconic(venster))
        {
            ShowWindow(venster, SwRestore);
        }
        WindowPositioner.BringToFront(venster);
    }

    private static bool HeeftInTitel((IntPtr Handle, int Pid, string Titel) v, string tekst) =>
        v.Titel.Contains(tekst, StringComparison.OrdinalIgnoreCase);

    /// <summary>Namen van processen die als terminal (of terminal-houder) kunnen dienen.</summary>
    private static readonly string[] TerminalNamen =
    {
        "WindowsTerminal", "Code", "powershell", "pwsh", "cmd", "conhost",
        "wezterm-gui", "alacritty", "ubuntu", "wsl",
    };

    private static readonly Dictionary<int, bool> TerminalPidCache = new();

    private static bool IsTerminalProces(int pid)
    {
        if (TerminalPidCache.TryGetValue(pid, out var bekend))
        {
            return bekend;
        }
        var terminal = false;
        try
        {
            terminal = TerminalNamen.Contains(
                Process.GetProcessById(pid).ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Proces net weg: geen terminal.
        }
        // Cache per klik legen kan niet stuk: de lijst wordt per activering vers opgebouwd
        // en de cache groeit hooguit met de handvol pids van zichtbare topvensters.
        TerminalPidCache[pid] = terminal;
        return terminal;
    }

    /// <summary>Alle zichtbare topvensters mét titel, als (handle, proces, titel).</summary>
    private static List<(IntPtr Handle, int Pid, string Titel)> AlleTopVensters()
    {
        TerminalPidCache.Clear();
        var lijst = new List<(IntPtr, int, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }
            var lengte = GetWindowTextLength(hwnd);
            if (lengte == 0)
            {
                return true;
            }
            var titel = new System.Text.StringBuilder(lengte + 1);
            GetWindowText(hwnd, titel, lengte + 1);
            GetWindowThreadProcessId(hwnd, out var pid);
            lijst.Add((hwnd, (int)pid, titel.ToString()));
            return true;
        }, IntPtr.Zero);
        return lijst;
    }

    /// <summary>
    /// Hook-kant: bepaalt meteen bij het signaal wélk venster van het terminalproces de
    /// sessie toont. Eén handvat is goud waard, want het terminalproces (Windows Terminal,
    /// VS Code) kan meerdere vensters hebben en de titel verandert voortdurend. Keuze:
    /// het enige venster van het proces, anders het venster met de projectmap in de titel,
    /// anders het voorgrondvenster als dat van het proces is; lukt niets, dan 0 en beslist
    /// de klik-kant met haar terugvalopties.
    /// </summary>
    private static long VindTerminalVenster(int pid, string map)
    {
        if (pid <= 0)
        {
            return 0;
        }
        var leaf = map.Length > 0 ? Path.GetFileName(map.TrimEnd('\\', '/')) : "";
        var vanPid = AlleTopVensters().Where(v => v.Pid == pid).ToList();
        if (vanPid.Count == 1)
        {
            return vanPid[0].Handle.ToInt64();
        }
        if (leaf.Length > 0)
        {
            var metLeaf = vanPid.FirstOrDefault(v => HeeftInTitel(v, leaf)).Handle;
            if (metLeaf != IntPtr.Zero)
            {
                return metLeaf.ToInt64();
            }
        }
        var voorgrond = GetForegroundWindow();
        if (vanPid.Any(v => v.Handle == voorgrond))
        {
            return voorgrond.ToInt64();
        }
        return 0;
    }

    /// <summary>
    /// Zoekt vanuit dit (hook-)proces omhoog door de ouderketen naar het eerste proces met
    /// een echt hoofdvenster: de shell zelf heeft er geen, de terminal (Windows Terminal,
    /// VS Code, …) wél. Bij Verkenner stoppen we — dan draaide de keten door tot de desktop
    /// en is er geen terminalvenster te vinden.
    /// </summary>
    private static int VindTerminalPid()
    {
        var ouders = OuderTabel();
        var pid = Environment.ProcessId;
        for (var stap = 0; stap < 15 && ouders.TryGetValue(pid, out var ouder) && ouder > 4; stap++)
        {
            pid = ouder;
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    return pid;
                }
            }
            catch
            {
                break; // ouder is al weg: dan maar zonder venster-PID
            }
        }
        return 0;
    }

    /// <summary>Tabel proces → ouderproces via een Toolhelp-snapshot.</summary>
    private static Dictionary<int, int> OuderTabel()
    {
        var tabel = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return tabel;
        }
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snap, ref entry))
            {
                do
                {
                    tabel[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
                while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return tabel;
    }

    private const uint Th32csSnapprocess = 0x00000002;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
