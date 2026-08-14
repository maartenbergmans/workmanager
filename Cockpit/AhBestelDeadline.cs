using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Sluit de bestelketen: staat er voor morgen een AH-levering in de agenda, dan herinnert de
/// app je daar 's avonds (vanaf 17 u, één keer per leverdatum) aan — het mandje aanvullen kan
/// bij AH meestal nog tot laat op de avond vóór de levering. Maximaal één agenda-check per
/// dag; state in %APPDATA%\WorkManager\ah-besteldeadline.json.
/// </summary>
public static class AhBestelDeadline
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-besteldeadline.json");

    private static bool _bezig;

    /// <summary>Draait de avondcheck als hij aan de beurt is; stil bij elke vorm van tegenslag.</summary>
    public static async Task ZorgVoorAsync(Form eigenaar, CancellationToken ct)
    {
        if (_bezig || !CalendarClient.Beschikbaar || DateTime.Now.Hour < 17)
        {
            return;
        }
        var vandaag = DateTime.Today.ToString("O");
        var state = LaadState();
        if (state.LaatsteCheck == vandaag)
        {
            return;
        }
        _bezig = true;
        try
        {
            state.LaatsteCheck = vandaag;
            BewaarState(state);

            var morgen = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
            var levering = (await CalendarClient.ZoekOpDagAsync(morgen, "AH-levering", ct))
                .FirstOrDefault();
            if (levering is null || state.GemeldVoor == morgen.ToString("O"))
            {
                return;
            }
            state.GemeldVoor = morgen.ToString("O");
            BewaarState(state);
            if (!eigenaar.IsDisposed)
            {
                eigenaar.BeginInvoke(() => Toast.Toon(eigenaar,
                    $"AH-levering morgen {levering.Start:HH:mm}–{levering.Einde:HH:mm} — " +
                    "mandje aanvullen kan meestal nog tot vanavond laat",
                    Fluent.Winkelwagen));
            }
        }
        catch
        {
            // Best effort; morgen opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    // ---------- state ----------

    private sealed class State
    {
        public string LaatsteCheck { get; set; } = "";
        public string GemeldVoor { get; set; } = "";
    }

    private static State LaadState()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Als "nog nooit" behandelen.
        }
        return new State();
    }

    private static void BewaarState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
