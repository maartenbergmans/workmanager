using System.Diagnostics;

namespace WorkManager;

/// <summary>
/// Kijkt of de lokale Docker-engine (Docker Desktop met WSL-backend) draait en kan hem
/// starten. De devenv-mysql-containers (zie <see cref="ProdDbKopie"/>) en de projectstacks
/// hangen ervan af, dus de cockpit toont een rode startknop zolang de engine plat ligt.
/// </summary>
public static class DockerStatus
{
    /// <summary>Bestaat alleen zolang de engine echt luistert — dé snelle draai-check.</summary>
    private const string EnginePipe = @"\\.\pipe\docker_engine";

    private static readonly string DesktopExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Docker", "Docker", "Docker Desktop.exe");

    public static bool Draait
    {
        get
        {
            try
            {
                return File.Exists(EnginePipe);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Geinstalleerd => File.Exists(DesktopExe);

    /// <summary>
    /// Start Docker Desktop en wacht tot de engine bereikbaar is (hooguit 2 minuten —
    /// een koude start met WSL erbij duurt gerust een halve minuut). True zodra hij draait.
    /// </summary>
    public static async Task<bool> StartAsync(CancellationToken ct)
    {
        if (Draait)
        {
            return true;
        }
        if (!Geinstalleerd)
        {
            return false;
        }
        Process.Start(new ProcessStartInfo { FileName = DesktopExe, UseShellExecute = true });
        var tot = DateTimeOffset.Now.AddMinutes(2);
        while (DateTimeOffset.Now < tot)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (Draait)
            {
                return true;
            }
        }
        return false;
    }
}
