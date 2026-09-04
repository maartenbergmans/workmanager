namespace WorkManager;

/// <summary>
/// Live statusoverzicht van de interactieve Claude Code-sessies, opgebouwd uit de
/// hook-events die via de spoolmap binnenkomen (SessionStart, UserPromptSubmit,
/// Notification, Stop en SessionEnd — zie <see cref="ClaudeAandacht"/>). Bewust puur
/// event-gedreven: procesdetectie ziet WSL-sessies niet, hooks vuren overal.
/// De cockpit toont dit in de "Claude ▾"-knop; de tray bewaakt er vergeten sessies mee.
/// </summary>
public static class ClaudeSessies
{
    public const string Gestart = "gestart";
    public const string Bezig = "bezig";
    public const string Wacht = "wacht";
    public const string Klaar = "klaar";

    /// <summary>Eén draaiende sessie; Map is het cwd uit het hook-JSON (Windows- of WSL-pad).</summary>
    public sealed record Sessie(
        string Map, string Status, string Boodschap, DateTimeOffset Sinds,
        int VensterPid, long VensterHandle, DateTimeOffset LaatstHerinnerd);

    private static readonly Dictionary<string, Sessie> Actief = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Verwerkt één hook-event; onbekende events en lege mappen doen niets.</summary>
    public static void Verwerk(
        string map, string hookEvent, string boodschap,
        int vensterPid, long vensterHandle, DateTimeOffset moment)
    {
        if (map.Length == 0)
        {
            return;
        }
        lock (Actief)
        {
            if (hookEvent == "SessionEnd")
            {
                Actief.Remove(map);
                return;
            }
            var status = hookEvent switch
            {
                "SessionStart" => Gestart,
                "UserPromptSubmit" => Bezig,
                "Notification" => Wacht,
                "Stop" => Klaar,
                _ => "",
            };
            if (status.Length == 0)
            {
                return;
            }
            var oud = Actief.GetValueOrDefault(map);
            Actief[map] = new Sessie(map, status,
                boodschap.Length > 0 ? boodschap : oud?.Boodschap ?? "",
                moment,
                vensterPid > 0 ? vensterPid : oud?.VensterPid ?? 0,
                vensterHandle != 0 ? vensterHandle : oud?.VensterHandle ?? 0,
                oud?.LaatstHerinnerd ?? DateTimeOffset.MinValue);
        }
    }

    /// <summary>
    /// Momentopname voor paneel en bewaking: wachtende sessies eerst, daarbinnen de
    /// langst wachtende bovenaan. Sessies zonder event in de laatste 12 uur zijn
    /// verweesd (SessionEnd gemist) en worden stilletjes opgeruimd.
    /// </summary>
    public static List<Sessie> Snapshot()
    {
        lock (Actief)
        {
            foreach (var oud in Actief.Values
                .Where(s => DateTimeOffset.Now - s.Sinds > TimeSpan.FromHours(12)).ToList())
            {
                Actief.Remove(oud.Map);
            }
            return Actief.Values
                .OrderByDescending(s => s.Status is Wacht or Klaar)
                .ThenBy(s => s.Sinds)
                .ToList();
        }
    }

    /// <summary>Aantal sessies dat op Maarten wacht (input gevraagd of klaar) — de badge.</summary>
    public static int AantalWachtend() =>
        Snapshot().Count(s => s.Status is Wacht or Klaar);

    public static void Verwijder(string map)
    {
        lock (Actief)
        {
            Actief.Remove(map);
        }
    }

    public static void MarkeerHerinnerd(string map)
    {
        lock (Actief)
        {
            if (Actief.TryGetValue(map, out var s))
            {
                Actief[map] = s with { LaatstHerinnerd = DateTimeOffset.Now };
            }
        }
    }
}
