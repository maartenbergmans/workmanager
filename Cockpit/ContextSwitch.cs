using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De context-switch-teller: telt hoe vaak Maarten op één dag tussen klanten springt
/// (CED-mail afhandelen → Aqurat-taak afvinken → weer CED = 2 sprongen). Rond 16:00 volgt
/// één dagrapport met droog commentaar. Puur zelfkennis met een knipoog; state in
/// %APPDATA%\WorkManager\context-switch.json.
/// </summary>
public static class ContextSwitch
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "context-switch.json");

    private sealed class State
    {
        public string Dag { get; set; } = "";
        public string Laatste { get; set; } = "";
        public int Sprongen { get; set; }
        public int Acties { get; set; }
        public string GemeldDag { get; set; } = "";
    }

    /// <summary>
    /// Registreert een actie in een klantcontext (CED, Aqurat, …). Onbekende of lege
    /// contexten tellen niet mee — liever te weinig sprongen dan valse.
    /// </summary>
    public static void Registreer(string? klant)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(klant))
            {
                return;
            }
            var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            var state = Laad();
            if (state.Dag != vandaag)
            {
                state.Dag = vandaag;
                state.Laatste = "";
                state.Sprongen = 0;
                state.Acties = 0;
            }
            state.Acties++;
            if (state.Laatste.Length > 0 &&
                !state.Laatste.Equals(klant, StringComparison.OrdinalIgnoreCase))
            {
                state.Sprongen++;
            }
            state.Laatste = klant.Trim();
            Bewaar(state);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Het dagrapport, één keer per werkdag vanaf 16:00; anders null.</summary>
    public static string? DagRapport()
    {
        try
        {
            var nu = DateTime.Now;
            if (nu.Hour < 16 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return null;
            }
            var vandaag = DateOnly.FromDateTime(nu).ToString("yyyy-MM-dd");
            var state = Laad();
            if (state.Dag != vandaag || state.GemeldDag == vandaag || state.Acties < 5)
            {
                return null; // te weinig gedaan om iets zinnigs over te zeggen
            }
            state.GemeldDag = vandaag;
            Bewaar(state);
            return state.Sprongen switch
            {
                <= 3 => $"🧘 {state.Sprongen} klantsprongen vandaag — monnikenwerk",
                <= 8 => $"🎛️ {state.Sprongen} klantsprongen vandaag — keurig gedoseerd",
                <= 15 => $"🤹 {state.Sprongen} klantsprongen vandaag — het jongleren ging je af",
                _ => $"🪩 {state.Sprongen} klantsprongen vandaag — je bent een flipperkast",
            };
        }
        catch
        {
            return null;
        }
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
            // Onleesbaar: vandaag opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
    }
}
