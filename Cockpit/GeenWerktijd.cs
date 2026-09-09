namespace WorkManager;

/// <summary>
/// Agenda-afspraken die geen werktijd zijn: geplande avondmaaltijden (🍴-recepten) en de
/// AH-levering. Die horen nooit in timesheetvoorstellen, de gatendetector of de
/// dagafsluiting — hoe blokkerend ze ook in de agenda staan.
/// </summary>
public static class GeenWerktijd
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    /// <summary>Gerechtnamen uit ah-gerechten.json, voor het recept-icoon in de meetinglijst.</summary>
    private static readonly Lazy<HashSet<string>> GerechtNamen = new(() =>
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(DataDir, "ah-gerechten.json")));
            return doc.RootElement.GetProperty("gerechten").EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    });

    /// <summary>Is deze agenda-afspraak een gepland avondeten (AH-gerecht)?</summary>
    public static bool IsRecept(string titel) =>
        titel.StartsWith("🍴", StringComparison.Ordinal) ||
        GerechtNamen.Value.Contains(titel.Trim());

    /// <summary>Is dit de AH-leveringsafspraak (het bezorgvenster van de boodschappen)?</summary>
    public static bool IsAhLevering(string titel) =>
        titel.Contains("AH-levering", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hoort deze afspraak níét in de gewerkte tijd thuis?</summary>
    public static bool Is(string titel) => IsRecept(titel) || IsAhLevering(titel);
}
