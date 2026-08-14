using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Het dagvoorstel dat op de webversie klaarstaat: Claude zet de activiteitenlog om in
/// timesheetregels, en die blijven hier wachten tot je ze op de gsm goedkeurt of weggooit.
/// Bewust een apart bestand en niet meteen in <see cref="TimesheetStore"/>: een voorstel is
/// nog geen boeking, en blind laten boeken vanaf een telefoon is precies wat je niet wilt.
///
/// <para>Opslag: %APPDATA%\WorkManager\uren-voorstel.json.</para>
/// </summary>
public static class VoorstelStore
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "uren-voorstel.json");

    public static List<TimesheetRegel> Laad()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<List<TimesheetRegel>>(File.ReadAllText(Bestand)) is { } regels)
            {
                // Een voorstel van gisteren is niet meer bruikbaar: dat gaat over een dag
                // die je intussen al afgesloten hebt.
                var vandaag = DateOnly.FromDateTime(DateTime.Now);
                return regels.Where(r => r.Datum == vandaag).ToList();
            }
        }
        catch
        {
            // Onleesbaar: dan is er gewoon geen voorstel.
        }
        return new List<TimesheetRegel>();
    }

    public static void Bewaar(List<TimesheetRegel> regels)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(regels));
        }
        catch
        {
            // Best effort.
        }
    }

    public static void Wis() => Bewaar(new List<TimesheetRegel>());
}
