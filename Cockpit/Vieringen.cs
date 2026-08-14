using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De leuke kant van afwerken: een streak van dagen waarop je de takenlijst leeg kreeg, en
/// wisselende felicitaties zodat het niet elke keer hetzelfde zinnetje is. Alles best effort —
/// een onleesbaar bestand kost je hooguit je streak, nooit een foutmelding.
/// </summary>
public static class Vieringen
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "streak.json");

    private sealed class Streak
    {
        public string LaatsteDag { get; set; } = "";
        public int Dagen { get; set; }
        public int Record { get; set; }
    }

    private static readonly string[] Felicitaties =
    {
        "Lijst leeg. Ga iets onnuttigs doen 🎉",
        "Alles afgevinkt — de dag is van jou 🏆",
        "Nul open taken. Dit voelt verdacht goed 😎",
        "Klaar! Zelfs je toekomstige zelf is jaloers ✨",
        "Leeg. Je mag nu officieel koffie halen ☕",
        "Taken: 0. Maarten: 1 🥇",
        "Opgeruimd staat netjes — en snel 🚀",
    };

    private static readonly string[] StreakZinnen =
    {
        "{0} dagen op rij. Wie doet je wat 🔥",
        "{0} dagen streak — niet meer stoppen nu 🔥",
        "Dag {0} op rij afgewerkt. Machine 🤖🔥",
    };

    private static readonly Random Willekeur = new();

    /// <summary>
    /// Registreert dat de lijst vandaag leeg is en geeft de tekst voor het feestmoment terug.
    /// Bij een streak van 2 of meer komt daar de streakzin bij.
    /// </summary>
    public static string VierLegeLijst()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var state = Laad();
        var alGevierd = state.LaatsteDag == vandaag.ToString("yyyy-MM-dd");
        if (!alGevierd)
        {
            // Aansluitend op gisteren telt door; een gemiste dag begint opnieuw bij 1.
            state.Dagen = DateOnly.TryParse(state.LaatsteDag, out var vorige) &&
                          vorige == vandaag.AddDays(-1)
                ? state.Dagen + 1
                : 1;
            state.LaatsteDag = vandaag.ToString("yyyy-MM-dd");
            state.Record = Math.Max(state.Record, state.Dagen);
            Bewaar(state);
        }

        var tekst = Felicitaties[Willekeur.Next(Felicitaties.Length)];
        if (state.Dagen >= 2)
        {
            tekst += "  ·  " + string.Format(
                StreakZinnen[Willekeur.Next(StreakZinnen.Length)], state.Dagen);
        }
        return tekst;
    }

    /// <summary>De huidige streak (0 als er vandaag noch gisteren iets leeggewerkt is).</summary>
    public static int HuidigeStreak()
    {
        var state = Laad();
        if (!DateOnly.TryParse(state.LaatsteDag, out var laatste))
        {
            return 0;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return laatste == vandaag || laatste == vandaag.AddDays(-1) ? state.Dagen : 0;
    }

    /// <summary>Het beste dat je ooit haalde — leuk om te weten als de streak breekt.</summary>
    public static int Record() => Laad().Record;

    private static Streak Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Streak>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: als "nog geen streak" behandelen.
        }
        return new Streak();
    }

    private static void Bewaar(Streak state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
