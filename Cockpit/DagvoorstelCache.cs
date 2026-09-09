using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Cache voor het Claude-dagvoorstel (timesheets): de generatie duurt makkelijk minuten
/// (Gmail/OWA/Teams ophalen + een claude-run), dus vanaf 16:30 wordt het voorstel alvast
/// op de achtergrond klaargezet. De cockpitknop "Dagvoorstel…" toont dan meteen het
/// klaarstaande voorstel; "Vernieuwen" in het venster forceert alsnog een verse run.
/// <para>Opslag: %APPDATA%\WorkManager\dagvoorstel-cache.json.</para>
/// </summary>
public static class DagvoorstelCache
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "dagvoorstel-cache.json");

    private sealed class Inhoud
    {
        public string Dag { get; set; } = "";
        public DateTimeOffset GemaaktOp { get; set; }
        public string Toelichting { get; set; } = "";
        public List<TimesheetRegel> Regels { get; set; } = new();
    }

    /// <summary>Het klaarstaande voorstel voor deze dag, of null als er niets (meer) ligt.</summary>
    public static (List<TimesheetRegel> Regels, string Toelichting, DateTimeOffset GemaaktOp)? Laad(
        DateOnly dag)
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<Inhoud>(File.ReadAllText(Bestand)) is { } inhoud &&
                inhoud.Dag == dag.ToString("yyyy-MM-dd"))
            {
                return (inhoud.Regels, inhoud.Toelichting, inhoud.GemaaktOp);
            }
        }
        catch
        {
            // Onleesbaar: dan is er gewoon geen voorbereid voorstel.
        }
        return null;
    }

    public static void Bewaar(DateOnly dag, List<TimesheetRegel> regels, string toelichting)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(new Inhoud
            {
                Dag = dag.ToString("yyyy-MM-dd"),
                GemaaktOp = DateTimeOffset.Now,
                Toelichting = toelichting,
                Regels = regels,
            }));
        }
        catch
        {
            // Cache is gemak, geen voorwaarde.
        }
    }

    // ---------------------------------------------------------------- voorbereiding

    private static bool _bezig;

    /// <summary>
    /// Zet vanaf 16:30 het dagvoorstel van vandaag alvast klaar. De periodieke tray-timer
    /// roept dit elke tien minuten aan: zonder werksporen of met een al gevuld voorstel
    /// gebeurt er niets, en na een mislukte run probeert de volgende tick het opnieuw.
    /// Draait op de UI-thread (de OWA/Teams-clients zijn thread-gebonden), net als de
    /// webversie-variant in WmWebSync.
    /// </summary>
    public static async Task ZorgVoorVoorbereidingAsync()
    {
        var nu = DateTime.Now;
        var dag = DateOnly.FromDateTime(nu);
        if (_bezig || nu.TimeOfDay < new TimeSpan(16, 30, 0))
        {
            return;
        }
        if (Laad(dag) is { } klaar && klaar.Regels.Count > 0)
        {
            return;
        }
        if (!ActiviteitenLog.HeeftSporen(dag))
        {
            return; // vrije dag of pc net aan: niets om een voorstel van te maken
        }

        _bezig = true;
        try
        {
            List<AgendaClient.AgendaItem> meetings;
            try
            {
                meetings = MeetingsCache.Load() is { } cache
                    ? cache.Eigen.Where(m => DateOnly.FromDateTime(m.Start.LocalDateTime) == dag).ToList()
                    : new List<AgendaClient.AgendaItem>();
            }
            catch
            {
                meetings = new List<AgendaClient.AgendaItem>();
            }
            using var afbreken = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (regels, toelichting) = await ActiviteitenLog.VoorstelAsync(dag, meetings, afbreken.Token);
            if (regels.Count > 0)
            {
                Bewaar(dag, regels, toelichting);
            }
        }
        catch
        {
            // Volgende tick opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }
}
