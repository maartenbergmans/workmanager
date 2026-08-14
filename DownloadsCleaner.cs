using Microsoft.VisualBasic.FileIO;

namespace WorkManager;

/// <summary>
/// Ruimt één keer per maand automatisch de map Downloads op: bestanden ouder dan een week
/// gaan naar de prullenbak (veilig terug te halen, dus geen definitief verlies). Draait vanuit
/// de tray-timer; onthoudt de laatste maand in %APPDATA%\WorkManager\downloads-cleaner.json.
/// </summary>
public static class DownloadsCleaner
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "downloads-cleaner.json");

    private static readonly string DownloadsMap = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Ruimt Downloads op als dat deze maand nog niet gebeurd is (bestanden &gt; 1 week oud).</summary>
    public static void ZorgVoorMaandelijks()
    {
        var maand = DateTime.Now.ToString("yyyy-MM");
        if (LaatsteMaand() == maand || !Directory.Exists(DownloadsMap))
        {
            return;
        }
        // Meteen registreren zodat een fout de opruiming niet elke tick opnieuw start.
        BewaarMaand(maand);

        var grens = DateTime.Now.AddDays(-7);
        var verwijderd = 0;
        try
        {
            foreach (var pad in Directory.EnumerateFiles(DownloadsMap))
            {
                try
                {
                    var info = new FileInfo(pad);
                    // .crdownload/.tmp/.part = nog bezig; en alleen echt oude bestanden.
                    if (info.Extension is ".crdownload" or ".part" or ".tmp")
                    {
                        continue;
                    }
                    if (info.LastWriteTime < grens && info.CreationTime < grens)
                    {
                        FileSystem.DeleteFile(pad, UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin);
                        verwijderd++;
                    }
                }
                catch
                {
                    // Bestand in gebruik of geen rechten: overslaan.
                }
            }
        }
        catch
        {
            // Downloads-map niet leesbaar: volgende maand opnieuw.
        }
        Log($"{DateTime.Now:yyyy-MM-dd HH:mm} {verwijderd} bestand(en) naar de prullenbak");
    }

    private static string LaatsteMaand()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) ?? "";
            }
        }
        catch
        {
            // Onleesbaar: als "nog nooit" behandelen.
        }
        return "";
    }

    private static void BewaarMaand(string maand)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(maand));
        }
        catch
        {
            // Best effort.
        }
    }

    private static void Log(string melding)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "downloads-cleaner-log.txt"), melding + Environment.NewLine);
        }
        catch
        {
            // Alleen diagnose.
        }
    }
}
