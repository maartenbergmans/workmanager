using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Start bij het aanzetten van een context de bijbehorende werkomgeving (IDE's, browser, Claude,
/// overige programma's) — alleen de onderdelen die nog niet open staan — en sluit die weer
/// bij het uitzetten.
/// </summary>
public static class ClientLauncher
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager", "launcher.log");

    /// <summary>
    /// Commando voor een interactieve Claude Code-sessie. Bewust zónder permission-mode:
    /// sessies starten in de standaardmodus en vragen dus gewoon toestemming (Maarten wil
    /// geen automodus, 2026-08-07).
    /// </summary>
    private const string ClaudeCommando = "claude";

    /// <summary>Per context: annulering van een nog lopende launch (bv. wachten op de app-URL).</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> PendingLaunches =
        new(StringComparer.OrdinalIgnoreCase);

    public static void LaunchFor(string client, bool dryRun = false)
    {
        var config = LaunchConfig.LoadOrCreate();
        var actions = config.Clients.GetValueOrDefault(client);
        if (actions is null)
        {
            Log($"{client}: geen acties geconfigureerd");
            return;
        }

        Run(client, "Timesheet", dryRun, () => StartTimesheet(client, config, actions, dryRun));

        var cts = new CancellationTokenSource();
        PendingLaunches.AddOrUpdate(client, cts, (_, old) =>
        {
            old.Cancel();
            return cts;
        });

        var windows = WindowInspector.GetVisibleWindows();

        Run(client, "PhpStorm", dryRun, () =>
        {
            if (actions.PhpStorm is not { } ps)
            {
                return null;
            }
            var result = HasWindow(windows, "phpstorm64", ps.WindowTitleMatch)
                ? "al open"
                : Start(dryRun, FindJetBrainsExe("PhpStorm", "phpstorm64.exe"), $"\"{ps.ProjectPath}\"");
            SchedulePosition(client, "PhpStorm", "phpstorm64", ps.WindowTitleMatch, ps.Monitor, dryRun, cts.Token);
            return result;
        });

        Run(client, "DataGrip", dryRun, () =>
        {
            if (actions.DataGrip is not { } dg)
            {
                return null;
            }
            var result = HasWindow(windows, "datagrip64", dg.WindowTitleMatch)
                ? "al open"
                : Start(dryRun, FindJetBrainsExe("DataGrip", "datagrip64.exe"), $"\"{dg.ProjectPath}\"");
            SchedulePosition(client, "DataGrip", "datagrip64", dg.WindowTitleMatch, dg.Monitor, dryRun, cts.Token);
            return result;
        });

        foreach (var program in actions.Programs ?? [])
        {
            Run(client, program.ProcessName, dryRun, () =>
            {
                var result = HasWindow(windows, program.ProcessName, program.WindowTitleMatch)
                    ? "al open"
                    : Start(dryRun, program.Path, program.Args);
                SchedulePosition(
                    client, program.ProcessName, program.ProcessName, program.WindowTitleMatch,
                    program.Monitor, dryRun, cts.Token);
                return result;
            });
        }

        Run(client, "Claude", dryRun, () =>
        {
            if (actions.Claude is not { } cl)
            {
                return null;
            }
            if (FindClaudeProcesses(cl.WorkingDirectory).Any())
            {
                return "al open";
            }
            return Start(
                dryRun, "wt.exe",
                $"-d \"{cl.WorkingDirectory}\" powershell -NoLogo -NoExit -Command {ClaudeCommando}");
        });

        // Als laatste, want deze stap kan wachten tot de app (gestart vanuit PhpStorm) bereikbaar is.
        Run(client, "Browser", dryRun, () =>
        {
            if (actions.Browser is not { } br)
            {
                return null;
            }

            var args = br.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                ? $"-new-tab \"{br.Url}\""
                : $"\"{br.Url}\"";

            string result;
            if (HasWindow(windows, br.ProcessName, br.WindowTitleMatch))
            {
                result = "al open";
            }
            else if (!br.WaitForApp)
            {
                result = Start(dryRun, br.Path, args);
            }
            else if (dryRun)
            {
                result = $"[dry-run] zou wachten tot {br.Url} bereikbaar is en dan {br.ProcessName} starten";
            }
            else if (!WaitUntilReachable(br.Url, timeout: TimeSpan.FromMinutes(10), cts.Token))
            {
                return cts.Token.IsCancellationRequested
                    ? "wachten op app geannuleerd (context uitgezet) – browser niet gestart"
                    : $"app niet bereikbaar binnen 10 min ({br.Url}) – browser niet gestart";
            }
            // Tijdens het wachten kan de gebruiker de pagina zelf al geopend hebben.
            else if (HasWindow(WindowInspector.GetVisibleWindows(), br.ProcessName, br.WindowTitleMatch))
            {
                result = "al open";
            }
            else
            {
                result = Start(dryRun, br.Path, args);
            }

            SchedulePosition(client, "Browser", br.ProcessName, br.WindowTitleMatch, br.Monitor, dryRun, cts.Token);
            return result;
        });

        // Extra browservensters (bv. Mailpit, Asana) na het hoofdvenster.
        foreach (var (extra, index) in (actions.Browser?.ExtraWindows ?? []).Select((w, i) => (w, i)))
        {
            var br = actions.Browser!;
            Run(client, $"Browser venster {index + 2}", dryRun, () =>
            {
                var result = HasWindow(WindowInspector.GetVisibleWindows(), br.ProcessName, extra.WindowTitleMatch)
                    ? "al open"
                    : Start(dryRun, br.Path, br.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                        ? $"-new-window \"{extra.Url}\""
                        : $"--new-window \"{extra.Url}\"");
                SchedulePosition(
                    client, $"Browser venster {index + 2}", br.ProcessName, extra.WindowTitleMatch,
                    br.Monitor, dryRun, cts.Token);
                return result;
            });
        }
    }

    // ---------------------------------------------------------------- dev-launchers (cockpit)

    /// <summary>
    /// Start een interactieve Claude Code-sessie in de gegeven projectmap. WSL-mappen
    /// (\\wsl.localhost\Distro\… of \\wsl$\Distro\…) worden ín WSL geopend zodat Claude
    /// native in Linux draait; Windows-mappen via PowerShell in Windows Terminal. De sessie
    /// start in de standaardmodus (zie <see cref="ClaudeCommando"/>).
    /// </summary>
    public static void StartClaude(string werkmap)
    {
        if (TryWslPad(werkmap, out var distro, out var linux))
        {
            Start(false, "wt.exe", $"wsl.exe -d {distro} --cd \"{linux}\" -- {ClaudeCommando}");
        }
        else
        {
            Start(false, "wt.exe", $"-d \"{werkmap}\" powershell -NoLogo -NoExit -Command {ClaudeCommando}");
        }
        Log($"Claude gestart in {werkmap}");
    }

    /// <summary>Draait er een interactieve Claude-sessie in deze projectmap?</summary>
    public static bool IsClaudeActief(string werkmap) => FindClaudeProcesses(werkmap).Any();

    /// <summary>Sluit de interactieve Claude-sessie(s) in deze projectmap (undo van StartClaude).</summary>
    public static void StopClaude(string werkmap)
    {
        Run(werkmap, "Claude sluiten", dryRun: false, () => CloseClaude(werkmap, dryRun: false));
    }

    /// <summary>Opent een project(map) in PhpStorm (ondersteunt ook \\wsl.localhost-paden).</summary>
    public static void StartPhpStorm(string projectPad)
    {
        Start(false, FindJetBrainsExe("PhpStorm", "phpstorm64.exe"), $"\"{projectPad}\"");
        Log($"PhpStorm gestart voor {projectPad}");
    }

    /// <summary>
    /// Opent een Visual Studio-project/solution. Bij een map wordt de eerste .sln gezocht;
    /// via shell-execute opent die in de standaard-Visual Studio (geen devenv-pad nodig).
    /// </summary>
    public static void StartVisualStudio(string projectPad)
    {
        var sln = projectPad;
        if (Directory.Exists(projectPad))
        {
            sln = Directory.EnumerateFiles(projectPad, "*.sln", SearchOption.AllDirectories)
                .OrderBy(p => p.Length) // de sln het dichtst bij de root
                .FirstOrDefault() ?? projectPad;
        }
        Process.Start(new ProcessStartInfo { FileName = sln, UseShellExecute = true });
        Log($"Visual Studio gestart voor {sln}");
    }

    /// <summary>
    /// Opent een URL in Firefox (de dev-browser) in een nieuwe tab. Staat Firefox niet op de
    /// gebruikelijke plek, dan valt dit terug op de standaardbrowser — beter een tab in Chrome
    /// dan een foutmelding.
    /// </summary>
    public static void StartFirefox(string url)
    {
        var exe = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Mozilla Firefox", "firefox.exe"),
            }
            .FirstOrDefault(File.Exists);
        if (exe is null)
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Log($"Firefox niet gevonden; {url} geopend in de standaardbrowser");
            return;
        }
        Start(false, exe, $"-new-tab \"{url}\"");
        Log($"Firefox geopend op {url}");
    }

    /// <summary>Opent een DataGrip-project (map onder C:\Users\...\DataGripProjects).</summary>
    public static void StartDataGrip(string projectPad)
    {
        Start(false, FindJetBrainsExe("DataGrip", "datagrip64.exe"), $"\"{projectPad}\"");
        Log($"DataGrip gestart voor {projectPad}");
    }

    /// <summary>
    /// Opent een console in de projectmap met "deploytool &lt;profiel&gt; push" al ingetikt maar
    /// bewust nog niét uitgevoerd: Maarten leest het commando na en drukt zelf op Enter (of
    /// Ctrl-C om af te breken). Daarna blijft de shell gewoon openstaan voor vervolgwerk.
    /// </summary>
    public static void StartDeploytool(string werkmap, string profiel)
    {
        var commando = $"deploytool {profiel} push";
        if (TryWslPad(werkmap, out var distro, out var linux))
        {
            // read -e -i zet het commando alvast op de regel; eval voert het pas uit na Enter.
            // Daarna een interactieve shell zodat de uitvoer blijft staan.
            // Bewust wsl.exe rechtstreeks (niet via wt.exe): Windows Terminal splitst zijn
            // commandoregel op ';' en zou het script in stukken hakken.
            // -e (niet --): zonder -e wikkelt wsl.exe het script nog in de loginshell en
            // expandeert die $c al (leeg) vóór de binnenste bash draait — eval doet dan niets.
            var script = $"read -e -i '{commando}' -p '> ' c ; eval $c ; exec bash -i";
            Start(false, "wsl.exe", $"-d {distro} --cd \"{linux}\" -e bash -i -c \"{script}\"");
        }
        else
        {
            Start(false, "wt.exe",
                $"-d \"{werkmap}\" powershell -NoLogo -NoExit -Command Set-Clipboard '{commando}'");
        }
        Log($"Deploytool-console geopend in {werkmap} ({commando}, nog niet uitgevoerd)");
    }

    /// <summary>
    /// Splitst een WSL-UNC-pad (\\wsl.localhost\Ubuntu\home\... of \\wsl$\Ubuntu\home\...) in
    /// de distronaam en het Linux-pad. Geeft false voor gewone Windows-paden.
    /// </summary>
    public static bool TryWslPad(string pad, out string distro, out string linux)
    {
        distro = "";
        linux = "";
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (pad.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = pad[prefix.Length..].Replace('\\', '/');
                var schuin = rest.IndexOf('/');
                if (schuin <= 0)
                {
                    return false;
                }
                distro = rest[..schuin];
                linux = rest[schuin..]; // begint met '/'
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- timesheets

    /// <summary>Voert alleen de timesheet-stap uit (voor testen/scripting via de CLI).</summary>
    public static void TimesheetCli(string actie, string client)
    {
        var config = LaunchConfig.LoadOrCreate();
        var actions = config.Clients.GetValueOrDefault(client);
        if (actions is null)
        {
            Log($"{client}: geen acties geconfigureerd");
            return;
        }
        Run(client, "Timesheet", dryRun: false, () => actie == "start"
            ? StartTimesheet(client, config, actions, dryRun: false)
            : StopTimesheet(client, config, actions, dryRun: false));
    }

    private static readonly string TimesheetStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager", "timesheet-state.json");

    private static readonly HttpClient TimesheetHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Maakt in UrbanAdmin een werkuur aan met van=tot=nu en onthoudt de id per context.</summary>
    private static string? StartTimesheet(string client, LaunchConfig config, ClientActions actions, bool dryRun)
    {
        if (config.Timesheets is not { } settings || actions.Timesheet is not { } timesheet)
        {
            return null;
        }
        if (settings.Token.Length == 0)
        {
            return "geen token geconfigureerd – overgeslagen";
        }

        var state = LoadTimesheetState();
        if (state.TryGetValue(client, out var lopend))
        {
            return $"werkuur {lopend} loopt al";
        }
        if (dryRun)
        {
            return $"[dry-run] zou werkuur starten voor project {timesheet.ProjectId}";
        }

        var omschrijving = timesheet.Omschrijving.Length > 0 ? timesheet.Omschrijving : $"WorkManager {client}";
        using var response = TimesheetPost(settings, "start", new
        {
            project_id = timesheet.ProjectId,
            gebruiker_id = settings.GebruikerId,
            extra = omschrijving,
        });
        var id = response.RootElement.GetProperty("id").GetInt64();

        state[client] = id;
        SaveTimesheetState(state);
        return $"werkuur {id} gestart (project {timesheet.ProjectId}, \"{omschrijving}\")";
    }

    /// <summary>Zet in UrbanAdmin de eindtijd van het bij de start onthouden werkuur.</summary>
    private static string? StopTimesheet(string client, LaunchConfig config, ClientActions actions, bool dryRun)
    {
        if (config.Timesheets is not { } settings || actions.Timesheet is null)
        {
            return null;
        }

        var state = LoadTimesheetState();
        if (!state.TryGetValue(client, out var id))
        {
            return "geen lopend werkuur";
        }
        if (dryRun)
        {
            return $"[dry-run] zou werkuur {id} stoppen";
        }

        using var response = TimesheetPost(settings, "stop", new { werkuur_id = id });
        var tot = response.RootElement.GetProperty("tot").GetString();

        state.Remove(client);
        SaveTimesheetState(state);
        return $"werkuur {id} gestopt (tot {tot})";
    }

    private static JsonDocument TimesheetPost(TimesheetSettings settings, string actie, object body)
    {
        var url = $"{settings.BaseUrl.TrimEnd('/')}/workmanager/werkuur/{actie}/{settings.Token}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = TimesheetHttp.Send(request);
        using var stream = response.Content.ReadAsStream();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"UrbanAdmin antwoordde HTTP {(int)response.StatusCode}");
        }
        return JsonDocument.Parse(stream);
    }

    private static Dictionary<string, long> LoadTimesheetState()
    {
        try
        {
            if (File.Exists(TimesheetStateFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(TimesheetStateFile))
                    ?? new(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Onleesbare state telt als: geen lopende werkuren.
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTimesheetState(Dictionary<string, long> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TimesheetStateFile)!);
        File.WriteAllText(TimesheetStateFile, JsonSerializer.Serialize(
            state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Wacht op de achtergrond tot het venster verschijnt, maximaliseert het (op het
    /// geconfigureerde scherm, 1 = meest links, als dat aanwezig is; anders waar het nu staat)
    /// en haalt het naar de voorgrond — ook als het al open stond.
    /// </summary>
    private static void SchedulePosition(
        string client, string component, string processName, string titleMatch,
        int? monitor, bool dryRun, CancellationToken cancellation)
    {
        if (dryRun)
        {
            Log($"{client} – {component}: [dry-run] zou venster maximaliseren"
                + (monitor is { } m ? $" op scherm {m}" : "") + " en naar de voorgrond halen");
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var hWnd = WaitForWindow(processName, titleMatch, TimeSpan.FromMinutes(3), cancellation);
                if (hWnd == IntPtr.Zero)
                {
                    Log($"{client} – {component}: venster niet gevonden om te positioneren"
                        + (cancellation.IsCancellationRequested ? " (geannuleerd)" : ""));
                    return;
                }

                string where;
                if (monitor is { } target && WindowPositioner.ScreenCount >= target)
                {
                    WindowPositioner.MaximizeOnMonitor(hWnd, target);
                    where = $"op scherm {target}";
                }
                else
                {
                    WindowPositioner.Maximize(hWnd);
                    where = monitor is { } missing
                        ? $"op huidig scherm (scherm {missing} niet aanwezig)"
                        : "op huidig scherm";
                }

                WindowPositioner.BringToFront(hWnd);
                Log($"{client} – {component}: gemaximaliseerd {where} en naar voorgrond gehaald");
            }
            catch (Exception ex)
            {
                Log($"{client} – {component}: FOUT bij positioneren – {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private static IntPtr WaitForWindow(
        string processName, string titleMatch, TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline && !cancellation.IsCancellationRequested)
        {
            var match = WindowInspector.GetVisibleWindows().FirstOrDefault(w =>
                w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
                && (titleMatch.Length == 0 || w.Title.Contains(titleMatch, StringComparison.OrdinalIgnoreCase)));
            if (match.Handle != IntPtr.Zero)
            {
                return match.Handle;
            }
            if (cancellation.WaitHandle.WaitOne(1000))
            {
                break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Sluit bij het uitzetten van een context de bijbehorende vensters en processen.</summary>
    public static void CloseFor(string client, bool dryRun = false)
    {
        if (PendingLaunches.TryRemove(client, out var pending))
        {
            pending.Cancel();
        }

        var config = LaunchConfig.LoadOrCreate();
        var actions = config.Clients.GetValueOrDefault(client);
        if (actions is null)
        {
            Log($"{client}: geen acties geconfigureerd");
            return;
        }

        Run(client, "Timesheet", dryRun, () => StopTimesheet(client, config, actions, dryRun));

        var windows = WindowInspector.GetVisibleWindows();

        Run(client, "PhpStorm sluiten", dryRun, () =>
            actions.PhpStorm is { } ps ? CloseWindows(windows, "phpstorm64", ps.WindowTitleMatch, dryRun) : null);

        Run(client, "DataGrip sluiten", dryRun, () =>
            actions.DataGrip is { } dg ? CloseWindows(windows, "datagrip64", dg.WindowTitleMatch, dryRun) : null);

        Run(client, "Browser sluiten", dryRun, () =>
            actions.Browser is { } br ? CloseWindows(windows, br.ProcessName, br.WindowTitleMatch, dryRun) : null);

        foreach (var (extra, index) in (actions.Browser?.ExtraWindows ?? []).Select((w, i) => (w, i)))
        {
            var br = actions.Browser!;
            Run(client, $"Browser venster {index + 2} sluiten", dryRun, () =>
                CloseWindows(windows, br.ProcessName, extra.WindowTitleMatch, dryRun));
        }

        foreach (var program in actions.Programs ?? [])
        {
            Run(client, $"{program.ProcessName} sluiten", dryRun, () =>
                CloseWindows(windows, program.ProcessName, program.WindowTitleMatch, dryRun));
        }

        Run(client, "Claude sluiten", dryRun, () =>
            actions.Claude is { } cl ? CloseClaude(cl.WorkingDirectory, dryRun) : null);
    }

    private static string CloseWindows(
        List<(string ProcessName, string Title, IntPtr Handle)> windows,
        string processName, string titleMatch, bool dryRun)
    {
        var matches = windows.Where(w =>
            w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
            && (titleMatch.Length == 0 || w.Title.Contains(titleMatch, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
        {
            return "niet open";
        }

        foreach (var window in matches)
        {
            if (!dryRun)
            {
                WindowInspector.CloseWindow(window.Handle);
            }
        }
        var titles = string.Join("; ", matches.Select(w => w.Title));
        return dryRun ? $"[dry-run] zou sluiten: {titles}" : $"gesloten: {titles}";
    }

    private static string CloseClaude(string workingDirectory, bool dryRun)
    {
        var processes = FindClaudeProcesses(workingDirectory).ToList();
        if (processes.Count == 0)
        {
            return "niet open";
        }

        foreach (var claude in processes)
        {
            // De sessie draait in een shell (powershell) binnen Windows Terminal; de shell
            // beëindigen sluit ook de terminaltab. Val terug op claude zelf als de ouder geen shell is.
            var target = claude;
            if (ProcessInspector.GetParentProcessId(claude.Id) is { } parentPid)
            {
                try
                {
                    var parent = Process.GetProcessById(parentPid);
                    if (parent.ProcessName is "powershell" or "pwsh" or "cmd")
                    {
                        target = parent;
                    }
                }
                catch
                {
                    // Ouder bestaat niet meer; sluit claude zelf.
                }
            }

            if (!dryRun)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        return dryRun
            ? $"[dry-run] zou {processes.Count} sessie(s) beëindigen"
            : $"{processes.Count} sessie(s) beëindigd";
    }

    private static void Run(string client, string name, bool dryRun, Func<string?> action)
    {
        try
        {
            var result = action();
            if (result is not null)
            {
                Log($"{client} – {name}: {result}");
            }
        }
        catch (Exception ex)
        {
            Log($"{client} – {name}: FOUT – {ex.Message}");
        }
    }

    private static string Start(bool dryRun, string fileName, string arguments)
    {
        if (dryRun)
        {
            return $"[dry-run] zou starten: {fileName} {arguments}";
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
        });
        return $"gestart: {fileName} {arguments}";
    }

    /// <summary>
    /// Pollt de URL tot de server antwoordt (elke HTTP-status telt: de app draait dan).
    /// </summary>
    private static bool WaitUntilReachable(string url, TimeSpan timeout, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.Now + timeout;
        var logged = false;
        while (DateTimeOffset.Now < deadline && !cancellation.IsCancellationRequested)
        {
            try
            {
                using var _ = http.Send(new HttpRequestMessage(HttpMethod.Get, url), cancellation);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (!logged)
                {
                    Log($"Browser: wacht tot app bereikbaar is op {url}");
                    logged = true;
                }
            }

            if (cancellation.WaitHandle.WaitOne(2000))
            {
                break;
            }
        }
        return false;
    }

    private static bool HasWindow(
        List<(string ProcessName, string Title, IntPtr Handle)> windows, string processName, string titleMatch)
    {
        return windows.Any(w =>
            w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
            && (titleMatch.Length == 0 || w.Title.Contains(titleMatch, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<Process> FindClaudeProcesses(string workingDirectory)
    {
        var target = Normalize(workingDirectory);
        foreach (var process in Process.GetProcessesByName("claude"))
        {
            var cwd = ProcessInspector.GetWorkingDirectory(process.Id);
            if (cwd is null || Normalize(cwd) != target)
            {
                continue;
            }

            // Headless sessies (o.a. door Claude Desktop gestart met stream-json/--print)
            // zijn geen interactieve terminalsessie en tellen niet mee.
            var commandLine = ProcessInspector.GetCommandLine(process.Id) ?? "";
            if (commandLine.Contains("stream-json", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("--print", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return process;
        }
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private static string FindJetBrainsExe(string productPrefix, string exeName)
    {
        var root = @"C:\Program Files\JetBrains";
        var exe = Directory.Exists(root)
            ? Directory.GetDirectories(root, productPrefix + "*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin", exeName))
                .FirstOrDefault(File.Exists)
            : null;

        return exe ?? throw new FileNotFoundException($"{exeName} niet gevonden onder {root}");
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging mag nooit de launcher breken.
        }
    }
}
