using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Donderdagroutine: haalt op de achtergrond de teamvakanties uit SD Worx op en zet de
/// samenvatting in de weekmail-opmerking, zodat die klaarstaat voor de vrijdagmail. Er wordt
/// hooguit één keer per donderdag geprobeerd (ook bij mislukken), zodat er nooit herhaalde
/// SD Worx-loginpogingen gebeuren (accountblokkering vermijden). De laatste geslaagde check
/// wordt onthouden voor de statusweergave in het teamtakenvenster.
/// </summary>
public static class TeamVakantieCheck
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "team-vakantie-check.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class State
    {
        public DateOnly? LaatstePoging { get; set; }
        public DateOnly? LaatsteSucces { get; set; }
    }

    /// <summary>De dag waarop de vakanties het laatst met succes opgehaald zijn (voor de status).</summary>
    public static DateOnly? LaatsteSucces => Laad().LaatsteSucces;

    /// <summary>
    /// Start de achtergrondophaling als het donderdag is en er vandaag nog geen poging was.
    /// Niet-blokkerend; veilig om vaak (bv. elke 10 min vanuit de tray-timer) aan te roepen.
    /// </summary>
    public static void ProbeerOpDonderdag()
    {
        if (DateTime.Now.DayOfWeek != DayOfWeek.Thursday || !SdWorxSettingsBeschikbaar())
        {
            return;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var state = Laad();
        if (state.LaatstePoging == vandaag)
        {
            return; // vandaag al één poging gedaan
        }
        // Poging meteen registreren zodat een mislukking niet elke tick opnieuw probeert.
        state.LaatstePoging = vandaag;
        Bewaar(state);
        _ = RunAsync(vandaag);
    }

    private static async Task RunAsync(DateOnly vandaag)
    {
        try
        {
            var tekst = await VakantiesForm.ProbeerAchtergrondAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(tekst))
            {
                return; // MFA/geen sessie: laat de handmatige knop het overnemen
            }
            // Samenvatting in de weekmail-opmerking zetten: eerder ingevoegde afwezigheidsregels
            // eerst verwijderen (zelfde logica als de handmatige 'Vakanties ophalen').
            var data = TeamTaskStore.Load();
            var behouden = string.Join(Environment.NewLine, data.Opmerking
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !SdWorxVakanties.IsAfwezigheidsRegel(l))).Trim();
            data.Opmerking = behouden.Length == 0
                ? tekst
                : behouden + Environment.NewLine + tekst;
            TeamTaskStore.Save(data);

            var state = Laad();
            state.LaatsteSucces = vandaag;
            Bewaar(state);
        }
        catch
        {
            // Best effort; volgende donderdag opnieuw.
        }
    }

    private static bool SdWorxSettingsBeschikbaar()
    {
        var s = SdWorxSettings.Load();
        return s.Gebruiker.Length > 0 && s.Wachtwoord.Length > 0;
    }

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }
}
