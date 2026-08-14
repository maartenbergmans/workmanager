using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Deploy-vreugde: elke geslaagde push in een projectrepo verdient een kleine 🚀-viering met
/// jaarteller. De detectie is puur lokaal — <c>git push</c> verschuift de lokale
/// upstream-ref (origin/branch), dus als die opschuift tussen twee peilingen was er een push;
/// er is geen netwerk of fetch voor nodig. Draait mee op de takenverversing, hooguit elke
/// 10 minuten. State in %APPDATA%\WorkManager\deploy-vreugde.json.
/// </summary>
public static class DeployVreugde
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "deploy-vreugde.json");

    private sealed class State
    {
        public DateTimeOffset? LaatsteRun { get; set; }
        /// <summary>Per repo de laatst geziene upstream-commit.</summary>
        public Dictionary<string, string> Upstreams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Per jaar het aantal gevierde pushes.</summary>
        public Dictionary<string, int> Tellers { get; set; } = new();
    }

    /// <summary>Repo's die niet in de opruimradar zitten maar wél deploys krijgen.</summary>
    private static readonly string[] ExtraRepos =
    {
        @"\\wsl.localhost\Ubuntu\home\maarten\projecten\urbanadmin\backend",
    };

    private static bool _bezig;

    /// <summary>Peilt de repo's en geeft de vieringsteksten terug (leeg = niets te vieren).</summary>
    public static async Task<List<string>> CheckAsync(CancellationToken ct)
    {
        var meldingen = new List<string>();
        if (_bezig)
        {
            return meldingen;
        }
        var state = Laad();
        if (state.LaatsteRun is { } vorige && DateTimeOffset.Now - vorige < Interval)
        {
            return meldingen;
        }
        _bezig = true;
        try
        {
            state.LaatsteRun = DateTimeOffset.Now;
            var jaar = DateTime.Now.Year.ToString();
            foreach (var map in GitTaken.Projecten.Concat(ExtraRepos))
            {
                ct.ThrowIfCancellationRequested();
                var hash = await GitStatus.KaleUitvoerAsync(map, "rev-parse --verify @{u}", ct);
                if (hash.Length == 0)
                {
                    continue; // geen upstream/geen repo: niets te vieren
                }
                var naam = Path.GetFileName(map.TrimEnd('\\', '/'));
                if (state.Upstreams.TryGetValue(map, out var oud) && oud != hash)
                {
                    state.Tellers[jaar] = state.Tellers.GetValueOrDefault(jaar) + 1;
                    meldingen.Add(
                        $"🚀 Push naar {naam} — deploy #{state.Tellers[jaar]} dit jaar");
                }
                // Eerste keer: alleen onthouden (een oude stand vieren zou vals juichen zijn).
                state.Upstreams[map] = hash;
            }
            Bewaar(state);
            return meldingen;
        }
        finally
        {
            _bezig = false;
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
            // Onleesbaar: opnieuw beginnen — de volgende push begint de telling.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
