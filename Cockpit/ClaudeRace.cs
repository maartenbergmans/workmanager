using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Paardenrace van de Claude-sessies: zodra er minstens twee tegelijk bezig zijn, kun je in het
/// "🤖 Claude ▾"-menu inzetten op de sessie die als eerste klaar is. De paardjes lopen naarmate
/// een sessie langer bezig is; de eerste die "klaar" of "wacht op input" meldt, wint. Een
/// juiste gok levert een melding, een puntje in je score en na drie winsten een prestatie op.
/// <para>State: %APPDATA%\WorkManager\claude-race.json (score); de lopende race leeft in het geheugen.</para>
/// </summary>
public static class ClaudeRace
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "claude-race.json");

    private sealed class Score
    {
        public int Races { get; set; }
        public int Gewonnen { get; set; }
    }

    private sealed class Race
    {
        public required List<string> Deelnemers { get; init; }
        public required string Inzet { get; init; }
        public DateTimeOffset Start { get; } = DateTimeOffset.Now;
    }

    private static Race? _race;
    private static bool _gekoppeld;

    /// <summary>Melding bij de uitslag (gezet door de tray-app/cockpit).</summary>
    public static Action<string, string>? ToonUitslag { get; set; }

    /// <summary>Koppelt de race één keer aan de sessie-events.</summary>
    public static void Koppel()
    {
        if (_gekoppeld)
        {
            return;
        }
        _gekoppeld = true;
        ClaudeSessies.Afgerond += Finish;
    }

    /// <summary>De sessies die nu bezig zijn (de paarden), langst bezig eerst.</summary>
    public static List<ClaudeSessies.Sessie> Paarden() =>
        ClaudeSessies.Snapshot().Where(s => s.Status == ClaudeSessies.Bezig).OrderBy(s => s.Sinds).ToList();

    public static string? Inzet => _race is { } r && DateTimeOffset.Now - r.Start < TimeSpan.FromHours(2) ? r.Inzet : null;

    /// <summary>Menu-items voor bovenaan het Claude-menu (leeg als er geen race kan lopen).</summary>
    public static List<ToolStripItem> MenuItems()
    {
        var items = new List<ToolStripItem>();
        var paarden = Paarden();
        var inzet = Inzet;
        if (paarden.Count < 2 && inzet is null)
        {
            return items;
        }
        var score = Laad();
        items.Add(new ToolStripMenuItem(inzet is null
            ? "🏁 Paardenrace — wie is eerst klaar? Klik om in te zetten:"
            : $"🏁 Paardenrace — jouw paard: {ClientLauncher.SessieLabel(inzet)}   (score {score.Gewonnen}/{score.Races})")
        { Enabled = false });
        foreach (var p in paarden)
        {
            var minuten = Math.Max(0, (int)(DateTimeOffset.Now - p.Sinds).TotalMinutes);
            var baan = new string('·', Math.Min(24, minuten)) + (p.Map.Equals(inzet, StringComparison.OrdinalIgnoreCase) ? "🏇" : "🐎");
            var mi = new ToolStripMenuItem($"   {baan}  {ClientLauncher.SessieLabel(p.Map)} ({minuten} min)");
            var map = p.Map;
            if (inzet is null)
            {
                mi.Click += (_, _) => ZetIn(map);
            }
            else
            {
                mi.Enabled = false;
            }
            items.Add(mi);
        }
        items.Add(new ToolStripSeparator());
        return items;
    }

    private static void ZetIn(string map)
    {
        var deelnemers = Paarden().Select(p => p.Map).ToList();
        if (deelnemers.Count < 2)
        {
            return;
        }
        _race = new Race { Deelnemers = deelnemers, Inzet = map };
    }

    private static void Finish(string map)
    {
        if (_race is not { } race || !race.Deelnemers.Contains(map, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        _race = null;
        if (DateTimeOffset.Now - race.Start > TimeSpan.FromHours(2))
        {
            return; // verlopen race: geen uitslag meer
        }
        var score = Laad();
        score.Races++;
        var gewonnen = map.Equals(race.Inzet, StringComparison.OrdinalIgnoreCase);
        if (gewonnen)
        {
            score.Gewonnen++;
            if (score.Gewonnen >= 3)
            {
                Prestaties.Gebeurtenis(null, "race");
            }
        }
        Bewaar(score);
        var winnaar = ClientLauncher.SessieLabel(map);
        ToonUitslag?.Invoke(
            gewonnen ? $"🏆 {winnaar} wint de race!" : $"🏁 {winnaar} is eerst binnen",
            gewonnen
                ? $"Goed gegokt — je score staat op {score.Gewonnen} uit {score.Races}."
                : $"Jouw paard {ClientLauncher.SessieLabel(race.Inzet)} loopt nog. Score: {score.Gewonnen} uit {score.Races}.");
    }

    private static Score Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<Score>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Opnieuw beginnen.
        }
        return new Score();
    }

    private static void Bewaar(Score score)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(score));
        }
        catch
        {
            // Best effort.
        }
    }
}
