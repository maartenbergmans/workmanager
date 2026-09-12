using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Cache voor het Claude-dagvoorstel (timesheets): de generatie duurt makkelijk minuten
/// (Gmail/OWA/Teams ophalen + een claude-run), dus vanaf 16:30 wordt het voorstel alvast
/// op de achtergrond klaargezet. De cockpitknop "Dagvoorstel…" toont dan meteen het
/// klaarstaande voorstel; "Vernieuwen" in het venster forceert alsnog een verse run.
/// <para>Opslag: %APPDATA%\WorkManager\dagvoorstel-cache.json — sinds 2026-09-12 een lijst
/// met één voorstel per dag (hoogstens 21), zodat ook voorstellen voor gemiste dagen
/// (<see cref="UrenInhaler"/>) naast dat van vandaag klaar kunnen staan. Het oude formaat
/// (één object) wordt nog gelezen.</para>
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

    private static readonly object Slot = new();

    private static List<Inhoud> LaadAlles()
    {
        try
        {
            if (!File.Exists(Bestand))
            {
                return new List<Inhoud>();
            }
            var json = File.ReadAllText(Bestand).TrimStart();
            if (json.StartsWith('['))
            {
                return JsonSerializer.Deserialize<List<Inhoud>>(json) ?? new List<Inhoud>();
            }
            // Oud formaat: één voorstel.
            return JsonSerializer.Deserialize<Inhoud>(json) is { } enkel ? new List<Inhoud> { enkel } : new List<Inhoud>();
        }
        catch
        {
            return new List<Inhoud>(); // onleesbaar: dan is er gewoon geen voorbereid voorstel
        }
    }

    /// <summary>Het klaarstaande voorstel voor deze dag, of null als er niets (meer) ligt.</summary>
    public static (List<TimesheetRegel> Regels, string Toelichting, DateTimeOffset GemaaktOp)? Laad(
        DateOnly dag)
    {
        lock (Slot)
        {
            return LaadAlles().FirstOrDefault(i => i.Dag == dag.ToString("yyyy-MM-dd")) is { } inhoud
                ? (inhoud.Regels, inhoud.Toelichting, inhoud.GemaaktOp)
                : null;
        }
    }

    /// <summary>De dagen waarvoor een voorstel klaarstaat.</summary>
    public static List<DateOnly> Dagen()
    {
        lock (Slot)
        {
            return LaadAlles().Where(i => i.Regels.Count > 0)
                .Select(i => DateOnly.TryParse(i.Dag, out var d) ? d : (DateOnly?)null)
                .OfType<DateOnly>().OrderBy(d => d).ToList();
        }
    }

    public static void Bewaar(DateOnly dag, List<TimesheetRegel> regels, string toelichting)
    {
        lock (Slot)
        {
            try
            {
                var alles = LaadAlles();
                alles.RemoveAll(i => i.Dag == dag.ToString("yyyy-MM-dd"));
                alles.Add(new Inhoud
                {
                    Dag = dag.ToString("yyyy-MM-dd"),
                    GemaaktOp = DateTimeOffset.Now,
                    Toelichting = toelichting,
                    Regels = regels,
                });
                var grens = DateOnly.FromDateTime(DateTime.Now).AddDays(-21).ToString("yyyy-MM-dd");
                alles = alles.Where(i => string.CompareOrdinal(i.Dag, grens) >= 0).OrderBy(i => i.Dag).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
                File.WriteAllText(Bestand, JsonSerializer.Serialize(alles));
            }
            catch
            {
                // Cache is gemak, geen voorwaarde.
            }
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
