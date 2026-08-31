using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Brug tussen Claude Code en WorkManager: de Notification-hook van Claude Code roept
/// "WorkManager.exe --claude-aandacht" aan met het hook-JSON op stdin. Dat korte proces
/// schrijft een signaalbestand mét het proces van het terminalvenster waarin de sessie
/// draait (de eerste voorouder met een echt venster — Windows Terminal, VS Code, …).
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
        public int VensterPid { get; set; }
        public DateTimeOffset Moment { get; set; }
    }

    /// <summary>
    /// Hook-kant (het korte --claude-aandacht-proces): leest het hook-JSON van Claude Code
    /// en zet het signaal in de spoolmap. Fouten blijven stil — een haperende melding mag
    /// de Claude-sessie zelf nooit storen.
    /// </summary>
    public static void SchrijfSignaal(string hookJson)
    {
        try
        {
            var boodschap = "";
            var map = "";
            try
            {
                using var doc = JsonDocument.Parse(hookJson);
                boodschap = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "" : "";
                map = doc.RootElement.TryGetProperty("cwd", out var c)
                    ? c.GetString() ?? "" : "";
                // Het Stop-event (sessie afgerond) heeft geen message-veld; zonder eigen
                // tekst zou de melding misleidend "Aandacht gevraagd" zeggen.
                if (boodschap.Length == 0 &&
                    doc.RootElement.TryGetProperty("hook_event_name", out var e) &&
                    e.GetString() == "Stop")
                {
                    boodschap = "Klaar — de opdracht is afgerond";
                }
            }
            catch
            {
                // Geen of kapot JSON: dan een kale melding zonder details.
            }
            Directory.CreateDirectory(SpoolDir);
            File.WriteAllText(
                Path.Combine(SpoolDir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}.json"),
                JsonSerializer.Serialize(new Signaal
                {
                    Boodschap = boodschap,
                    Map = map,
                    VensterPid = VindTerminalPid(),
                    Moment = DateTimeOffset.Now,
                }));
        }
        catch
        {
            // Best effort.
        }
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
            return;
        }
        // Alleen het nieuwste signaal tonen (meer dan één toast tegelijk kan toch niet);
        // de rest is achterhaald en gaat gewoon weg.
        Signaal? signaal = null;
        foreach (var pad in bestanden)
        {
            try
            {
                signaal = JsonSerializer.Deserialize<Signaal>(File.ReadAllText(pad)) ?? signaal;
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
            return;
        }
        var project = signaal.Map.Length > 0 ? Path.GetFileName(signaal.Map.TrimEnd('\\', '/')) : "";
        var pid = signaal.VensterPid;
        var map = signaal.Map;
        TrayMelding.Toon(
            $"🤖 Claude Code{(project.Length > 0 ? $" — {project}" : "")}",
            signaal.Boodschap.Length > 0 ? signaal.Boodschap : "Aandacht gevraagd",
            () => ActiveerTerminal(pid, map),
            duurMs: 20000);
    }

    /// <summary>
    /// Haalt het terminalvenster van de sessie naar de voorgrond. Eén proces kan meerdere
    /// vensters hebben (Windows Terminal deelt standaard één proces voor al z'n vensters),
    /// dus het bewaarde proces alleen is niet genoeg: we sommen alle topvensters op en
    /// kiezen op titel — Claude Code zet de projectmap in de terminaltitel. Volgorde:
    /// projectmap in de titel (liefst binnen het bewaarde proces), dan het hoofdvenster
    /// van het bewaarde proces, dan een terminalvenster met "claude" in de titel.
    /// </summary>
    private static void ActiveerTerminal(int pid, string map)
    {
        var leaf = map.Length > 0 ? Path.GetFileName(map.TrimEnd('\\', '/')) : "";
        var vensters = AlleTopVensters();

        var venster = IntPtr.Zero;
        if (leaf.Length > 0)
        {
            venster = vensters.FirstOrDefault(v => v.Pid == pid && HeeftInTitel(v, leaf)).Handle;
            if (venster == IntPtr.Zero)
            {
                venster = vensters.FirstOrDefault(v => IsTerminalProces(v.Pid) && HeeftInTitel(v, leaf)).Handle;
            }
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
