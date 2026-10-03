using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De dobbelsteen: voor als je niet kunt kiezen. Het 🎲-knopje in de cockpit laat de selectie
/// als een roulette over de open taken lopen en stopt op één taak — die mag je nu doen, en
/// daarmee is de keuze van de tafel. Deze klasse houdt alleen het geheugen bij: welke taak er
/// als laatste uitgedobbeld is (zodat afvinken binnen het uur een prestatie oplevert) en hoe
/// vaak je het lot effectief gevolgd hebt. State in %APPDATA%\WorkManager\dobbel.json.
/// </summary>
public static class Dobbel
{
    /// <summary>Zo lang blijft een worp "vers": vink je binnen dit venster af, dan volgde je het lot.</summary>
    private static readonly TimeSpan Venster = TimeSpan.FromHours(1);

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "dobbel.json");

    private sealed class State
    {
        public string LaatsteTaakId { get; set; } = "";
        public DateTimeOffset? LaatsteWorp { get; set; }
        public int Worpen { get; set; }
        public int Gevolgd { get; set; }
    }

    private static readonly Random Willekeur = new();

    /// <summary>Wisselende aankondiging bij de uitkomst — elke keer hetzelfde zinnetje is geen feest.</summary>
    public static string Zinnetje() => new[]
    {
        "🎲 Het lot heeft gesproken:",
        "🎲 De dobbelsteen zegt:",
        "🎲 Deze dan:",
        "🎲 Geen excuses meer:",
        "🎲 Zes gegooid:",
        "🎲 En de winnaar is:",
    }[Willekeur.Next(6)];

    /// <summary>Kiest er willekeurig één uit; leeg = niets te kiezen.</summary>
    public static int Kies(int aantal) => aantal <= 0 ? -1 : Willekeur.Next(aantal);

    /// <summary>Onthoudt de uitgedobbelde taak (lege id mag: dan telt alleen de worp).</summary>
    public static void Onthoud(string taakId)
    {
        var state = Laad();
        state.LaatsteTaakId = taakId;
        state.LaatsteWorp = DateTimeOffset.Now;
        state.Worpen++;
        Bewaar(state);
    }

    /// <summary>
    /// Is deze taak kort geleden uitgedobbeld? Zo ja, dan telt het afvinken als "het lot
    /// gevolgd" (en wordt dat maar één keer per worp geteld).
    /// </summary>
    public static bool VolgdeHetLot(string taakId)
    {
        if (taakId.Length == 0)
        {
            return false;
        }
        var state = Laad();
        if (state.LaatsteTaakId != taakId || state.LaatsteWorp is not { } worp ||
            DateTimeOffset.Now - worp > Venster)
        {
            return false;
        }
        state.LaatsteTaakId = ""; // één keer per worp
        state.Gevolgd++;
        Bewaar(state);
        return true;
    }

    /// <summary>Regel voor de tooltip op de knop: hoe vaak je het lot al gevolgd hebt.</summary>
    public static string Stand()
    {
        var state = Laad();
        return state.Worpen == 0
            ? "Dobbelsteen: laat het lot een taak kiezen"
            : $"Dobbelsteen — {state.Gevolgd} van de {state.Worpen} worpen gevolgd";
    }

    private static State Laad()
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
            // Onleesbaar: begin opnieuw te tellen, het is maar een spel.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: zonder geheugen werkt de dobbelsteen nog altijd.
        }
    }
}
