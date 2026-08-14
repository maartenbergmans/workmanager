using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Lokale lijst van afspraken die je agenda niet blokkeren ("ik kan ondertussen werken"):
/// voor afspraken die je niet zelf kunt bewerken — uitnodigingen van anderen, herhalende
/// Google-afspraken, CED-meetings. Aangevinkt via rechtsklik in de meetinglijst; de
/// dagplanner slaat ze dan over als anker. Persistent in werkbaar-meetings.json; verlopen
/// afspraken worden bij het bewaren opgeruimd.
/// </summary>
public static class WerkbaarStore
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "werkbaar-meetings.json");

    /// <summary>Dezelfde sleutel als de meeting-snoozes: titel + exact startmoment.</summary>
    public static string Sleutel(AgendaClient.AgendaItem m) => $"{m.Titel}|{m.Start:O}";

    public static bool Is(AgendaClient.AgendaItem m) => Laad().Contains(Sleutel(m));

    /// <summary>
    /// Zet de markering expliciet aan of uit op titel + startmoment — voor de afspraakdialoog,
    /// die de vlag ook lokaal vastlegt: zo blijft "blokkeert mijn agenda niet" behouden óók
    /// als de omschrijving met de [werkbaar]-marker niet naar Google weggeschreven raakt.
    /// </summary>
    public static void Zet(string titel, DateTimeOffset start, bool aan)
    {
        var sleutels = Laad();
        var sleutel = $"{titel}|{start:O}";
        if (aan ? sleutels.Add(sleutel) : sleutels.Remove(sleutel))
        {
            Bewaar(sleutels);
        }
    }

    /// <summary>Zet de markering aan of uit; geeft de nieuwe stand terug.</summary>
    public static bool Wissel(AgendaClient.AgendaItem m)
    {
        var sleutels = Laad();
        var sleutel = Sleutel(m);
        var aan = !sleutels.Remove(sleutel);
        if (aan)
        {
            sleutels.Add(sleutel);
        }
        Bewaar(sleutels);
        return aan;
    }

    private static HashSet<string> Laad()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DataFile)) is { } lijst)
            {
                return lijst.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch
        {
            // Onleesbaar: als leeg behandelen.
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static void Bewaar(HashSet<string> sleutels)
    {
        try
        {
            // Afspraken uit het verleden hoeven de lijst niet te vervuilen: de startdatum
            // zit achteraan in de sleutel.
            var grens = DateTimeOffset.Now.AddDays(-2);
            sleutels.RemoveWhere(s =>
                s.LastIndexOf('|') is var p and >= 0 &&
                DateTimeOffset.TryParse(s[(p + 1)..], out var start) && start < grens);
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(sleutels.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
