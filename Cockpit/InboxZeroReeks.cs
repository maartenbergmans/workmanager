using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De inbox-zero-reeks: aaneengesloten wérkdagen waarop de berichtenlijst leeg raakte. Het
/// weekend telt niet mee maar breekt de reeks ook niet (vrijdag → maandag sluit gewoon aan).
/// In de cockpit groeit een plantje met de reeks mee: 🌱 → 🌿 → ☘️ → 🍀 → 🪴 → 🌾 → 🌳 → 🌸 → 🌺 → 🌻.
/// Persistent in %APPDATA%\WorkManager\inbox-zero-reeks.json; alles best effort.
/// </summary>
public static class InboxZeroReeks
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "inbox-zero-reeks.json");

    private sealed class State
    {
        public string LaatsteDag { get; set; } = "";
        public int Dagen { get; set; }
        public int Record { get; set; }
    }

    /// <summary>
    /// Registreert dat de inbox vandaag leeg raakte. Geeft de reekslengte terug en of dit de
    /// eerste keer vandaag is (dan verdient het een feestje). In het weekend verandert er
    /// niets — de reeks leeft van werkdagen.
    /// </summary>
    public static (int Dagen, bool NieuwVandaag) Registreer()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var state = Laad();
        if (vandaag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return (Huidig(), false);
        }
        if (state.LaatsteDag == vandaag.ToString("yyyy-MM-dd"))
        {
            return (state.Dagen, false);
        }
        // Aansluitend op de vorige werkdag telt door; een gemiste werkdag begint opnieuw.
        state.Dagen = DateOnly.TryParse(state.LaatsteDag, out var vorige) &&
                      vorige == VorigeWerkdag(vandaag)
            ? state.Dagen + 1
            : 1;
        state.LaatsteDag = vandaag.ToString("yyyy-MM-dd");
        state.Record = Math.Max(state.Record, state.Dagen);
        Bewaar(state);
        return (state.Dagen, true);
    }

    /// <summary>
    /// De levende reeks: vandaag al gehaald, of de vorige werkdag gehaald en vandaag nog
    /// haalbaar. Anders 0 (het plantje verdwijnt dan uit de titelbalk).
    /// </summary>
    public static int Huidig()
    {
        var state = Laad();
        if (!DateOnly.TryParse(state.LaatsteDag, out var laatste))
        {
            return 0;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return laatste == vandaag ||
               laatste == VorigeWerkdag(vandaag) ||
               // In het weekend blijft de reeks van vrijdag gewoon staan.
               (vandaag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday &&
                laatste >= VorigeWerkdag(vandaag))
            ? state.Dagen
            : 0;
    }

    public static int Record() => Laad().Record;

    /// <summary>
    /// Het symbool bij een reekslengte: standaard een plantje dat in kleine stapjes groeit,
    /// maar elk kleurenschema heeft zijn eigen reeks (007 klimt van dossier naar kroon).
    /// Zie <see cref="ThemaStem.Streak"/>.
    /// </summary>
    public static string Plant(int dagen) => ThemaStem.Streak(dagen);

    private static DateOnly VorigeWerkdag(DateOnly dag)
    {
        do
        {
            dag = dag.AddDays(-1);
        }
        while (dag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return dag;
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
            // Onleesbaar: als "nog geen reeks" behandelen.
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
            // Best effort.
        }
    }
}
