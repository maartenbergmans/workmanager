using System.Text.Json;

namespace WorkManager;

/// <summary>Een onthouden Drive-map: het id plus de naam zoals we die tonen.</summary>
public sealed class DriveDoelmap
{
    public string Id { get; set; } = "";
    public string Naam { get; set; } = "";
}

/// <summary>
/// De doelmappen voor bijlagen: de vaste favorieten (dezelfde boekhoudmappen waar het
/// Drive-menu in de cockpit al naar linkt) en de mappen die Maarten recent koos.
///
/// De favorieten staan in code omdat ze al in code stonden — één plek om de jaarmappen te
/// verversen is beter dan twee. De recente lijst leeft in
/// %APPDATA%\WorkManager\drive-doelmappen.json en houdt de laatste acht bij.
/// </summary>
public static class DriveMappen
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string RecentFile = Path.Combine(DataDir, "drive-doelmappen.json");

    private const int MaxRecent = 8;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// De vaste snelkoppelingen. Een lege id betekent een scheidingslijn in het menu — zelfde
    /// afspraak als in het Drive-menu van de cockpit, waar deze id's vandaan komen.
    /// </summary>
    public static readonly (string Naam, string Id)[] Favorieten =
    {
        ("Maarten 2026", "14jONt7j2NCspWl5WVP0y7SOjG7cwgFNO"),
        ("Hilke 2026", "1GPjtPmul_4aMz4NlbXis_wBdAjuer89X"),
        ("Lisa 2026", "1eBRNXbvddqwJlZffwPvob0sAd91bGAc_"),
        ("Emilia 2026", "1zkzbyIX28qdebkWwcFQECuibljnJKKE9"),
        ("—", ""),
        ("Bermacon", "1nTVmBt7srHBj1A5LhuI1ySiNEDNK44a3"),
        ("Urbanit", "1xY1_6-f9AxHiyraHjB9q0BYgUyeUQqmd"),
    };

    public static List<DriveDoelmap> Recent()
    {
        try
        {
            if (File.Exists(RecentFile))
            {
                var lijst = JsonSerializer.Deserialize<List<DriveDoelmap>>(
                    File.ReadAllText(RecentFile), JsonOpts);
                if (lijst is not null)
                {
                    return lijst.Where(m => m.Id.Length > 0).Take(MaxRecent).ToList();
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: begin met een lege lijst.
        }
        return new List<DriveDoelmap>();
    }

    /// <summary>
    /// Zet deze map vooraan in de recente lijst. Favorieten slaan we bewust over: die staan al
    /// bovenaan het menu en zouden er anders dubbel in komen.
    /// </summary>
    public static void OnthoudGebruik(string id, string naam)
    {
        if (id.Length == 0 || Favorieten.Any(f => f.Id == id))
        {
            return;
        }

        var lijst = Recent();
        lijst.RemoveAll(m => m.Id == id);
        lijst.Insert(0, new DriveDoelmap { Id = id, Naam = naam.Length > 0 ? naam : id });

        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(RecentFile,
                JsonSerializer.Serialize(lijst.Take(MaxRecent).ToList(), JsonOpts));
        }
        catch
        {
            // De lijst onthouden is comfort, geen voorwaarde: nooit de opslag laten mislukken.
        }
    }
}
