using System.Text.Json;

namespace WorkManager;

/// <summary>Eén goedgekeurde factuur (logboek van het ISPnext-venster).</summary>
public sealed record Goedkeuring(
    DateTimeOffset Moment, string Leverancier, string Factuurnummer, decimal? Bedrag, bool Auto);

/// <summary>
/// Logboek van alles wat via WorkManager in ISPnext goedgekeurd is
/// (%APPDATA%\WorkManager\ispnext-goedkeuringen.json). Twee doelen: terugkijken ("wat
/// keurde ik vorige week goed?") en dubbels vangen — dezelfde leverancier + hetzelfde
/// factuurnummer nog eens in de lijst is verdacht en wordt nooit automatisch aangevinkt.
/// </summary>
public static class ApprovalLog
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ispnext-goedkeuringen.json");

    public static List<Goedkeuring> Load()
    {
        try
        {
            if (File.Exists(LogFile) &&
                JsonSerializer.Deserialize<List<Goedkeuring>>(File.ReadAllText(LogFile)) is { } lijst)
            {
                return lijst;
            }
        }
        catch
        {
            // Onleesbaar logboek: opnieuw beginnen.
        }
        return new List<Goedkeuring>();
    }

    public static void Voeg(IEnumerable<Goedkeuring> goedkeuringen)
    {
        try
        {
            var lijst = Load();
            lijst.AddRange(goedkeuringen);
            // Ouder dan twee jaar hoeft niet meer mee.
            lijst.RemoveAll(g => g.Moment < DateTimeOffset.Now.AddYears(-2));
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.WriteAllText(LogFile, JsonSerializer.Serialize(lijst,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Het logboek mag het goedkeuren zelf nooit breken.
        }
    }

    /// <summary>Wanneer deze factuur (leverancier + nummer) eerder goedgekeurd werd, of null.</summary>
    public static DateTimeOffset? EerderGoedgekeurd(string leverancier, string factuurnummer)
    {
        if (factuurnummer.Trim().Length == 0)
        {
            return null;
        }
        return Load()
            .Where(g => string.Equals(g.Factuurnummer.Trim(), factuurnummer.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(g.Leverancier.Trim(), leverancier.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(g => (DateTimeOffset?)g.Moment)
            .FirstOrDefault();
    }

    /// <summary>Korte samenvatting van de laatste goedkeuringsronde ("10/9: 12 facturen, € 8.400").</summary>
    public static string LaatsteRonde()
    {
        var lijst = Load();
        if (lijst.Count == 0)
        {
            return "";
        }
        var laatste = lijst.Max(g => g.Moment);
        var ronde = lijst.Where(g => (laatste - g.Moment).TotalHours < 6).ToList();
        var totaal = ronde.Sum(g => g.Bedrag ?? 0);
        return string.Create(System.Globalization.CultureInfo.GetCultureInfo("nl-BE"),
            $"{laatste.LocalDateTime:d/M}: {ronde.Count} facturen, € {totaal:N2}");
    }
}
