using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De voorraadkast: ingrediënten die je vrijwel altijd in huis hebt (olijfolie, kruiden,
/// knoflook, …). Ze blijven gewoon in de recepten en de keuzestap staan, maar worden daar
/// standaard afgevinkt en gemarkeerd — zoals HelloFresh z'n "zelf in huis"-lijstje.
/// Beheer gebeurt in de keuzestap zelf (knop "Voorraadkast"); bewaard in
/// %APPDATA%\WorkManager\ah-voorraadkast.json als lijst van ingrediëntnamen.
/// </summary>
public static class AhVoorraadkast
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-voorraadkast.json");

    private static HashSet<string>? _cache;

    private static HashSet<string> Namen => _cache ??= Laad();

    /// <summary>Of dit ingrediënt in de voorraadkast zit (vergelijking hoofdletterongevoelig).</summary>
    public static bool Bevat(string naam) => Namen.Contains(naam.Trim());

    /// <summary>Zet een ingrediënt in of uit de voorraadkast; geeft de nieuwe status terug.</summary>
    public static bool Wissel(string naam)
    {
        naam = naam.Trim();
        var erin = !Namen.Remove(naam);
        if (erin)
        {
            Namen.Add(naam);
        }
        Bewaar();
        return erin;
    }

    private static HashSet<string> Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StateFile)) is { } lijst)
            {
                return new HashSet<string>(lijst, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Onleesbaar: lege voorraadkast.
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Bewaar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(
                Namen.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
