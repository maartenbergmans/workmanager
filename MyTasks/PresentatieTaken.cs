using System.Diagnostics;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Aqurat-presentatietaak: staat er een (bi-weekly) Aqurat-meeting op vrijdag in de agenda,
/// dan verschijnt op woensdag automatisch de taak om de presentatie voor te bereiden.
/// Dubbelklikken op die taak in de cockpit opent Claude Desktop met de opdracht op het
/// klembord. Per meetingdatum wordt de taak maar één keer aangemaakt
/// (%APPDATA%\WorkManager\presentatie-taken.json).
/// </summary>
public static class PresentatieTaken
{
    public const string TaakPrefix = "Aqurat-presentatie maken";

    // AUMID van de Claude Desktop Store-app op deze machine.
    private const string ClaudeDesktopAppId = @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude";

    private static readonly string StatusFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "presentatie-taken.json");

    /// <summary>Checkt de agenda en maakt (per meeting één keer) de woensdagtaak aan.</summary>
    public static async Task ZorgVoorTaakAsync(CancellationToken ct)
    {
        var agenda = AgendaSettings.Load();
        if (!agenda.Compleet)
        {
            return;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        List<AgendaClient.AgendaItem> items;
        try
        {
            items = await AgendaClient.OphalenAsync(agenda.Urls, vandaag, vandaag.AddDays(9), ct);
        }
        catch
        {
            return; // agenda even niet bereikbaar: de volgende poll probeert opnieuw
        }

        var verwerkt = LaadVerwerkt();
        var gewijzigd = false;
        foreach (var meeting in items.Where(i => !i.HeleDag &&
            i.Titel.Contains("aqurat", StringComparison.OrdinalIgnoreCase) &&
            i.Start.DayOfWeek == DayOfWeek.Friday))
        {
            var meetingDag = DateOnly.FromDateTime(meeting.Start.LocalDateTime);
            var woensdag = meetingDag.AddDays(-2);
            var sleutel = meetingDag.ToString("yyyy-MM-dd");
            // Pas vanaf woensdag; stond de app die dag uit, dan alsnog t/m de meetingdag zelf.
            if (vandaag < woensdag || vandaag > meetingDag || verwerkt.Contains(sleutel))
            {
                continue;
            }

            var taken = MijnTaakStore.Load();
            if (!taken.Taken.Any(t => !t.Klaar &&
                t.Tekst.StartsWith(TaakPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = $"{TaakPrefix} (meeting {meetingDag:ddd d/M})",
                    Categorie = "Aqurat",
                    Prioriteit = 1,
                    Deadline = woensdag,
                });
                MijnTaakStore.Save(taken);
            }
            verwerkt.Add(sleutel);
            gewijzigd = true;
        }
        if (gewijzigd)
        {
            BewaarVerwerkt(verwerkt);
        }
    }

    /// <summary>
    /// Opent Claude Desktop met de presentatie-opdracht op het klembord (plakken met Ctrl+V —
    /// Claude Desktop heeft geen commandline-argument om een prompt mee te geven).
    /// </summary>
    public static void OpenClaudeDesktop(string taakTekst)
    {
        var meeting = taakTekst.Contains('(')
            ? taakTekst[(taakTekst.IndexOf('(') + 1)..].TrimEnd(')')
            : "de eerstvolgende bi-weekly meeting";
        Clipboard.SetText(
            $"Maak de Aqurat-presentatie voor de bi-weekly {meeting}. " +
            "Baseer je op de vorige bi-weekly presentatie en de recente Aqurat-voortgang, " +
            "en zet de belangrijkste updates, cijfers en actiepunten van de afgelopen twee weken erin.");
        Process.Start(new ProcessStartInfo("explorer.exe", ClaudeDesktopAppId));
    }

    private static HashSet<string> LaadVerwerkt()
    {
        try
        {
            if (File.Exists(StatusFile) &&
                JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatusFile)) is { } ids)
            {
                return ids;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen (hooguit één dubbele taak).
        }
        return new HashSet<string>();
    }

    private static void BewaarVerwerkt(HashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFile)!);
        File.WriteAllText(StatusFile, JsonSerializer.Serialize(ids));
    }
}
