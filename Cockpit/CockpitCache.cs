using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Cache van de laatst opgehaalde cockpit-berichten in %APPDATA%\WorkManager\cockpit-berichten.json:
/// na een herstart toont de cockpit meteen de laatst bekende lijst, tot de eerste verse
/// ophaalbeurt klaar is. Alleen volledig geslaagde ophaalbeurten overschrijven de cache.
/// </summary>
public static class CockpitCache
{
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "cockpit-berichten.json");

    // MailBericht gebruikt velden (geen properties), dus IncludeFields is nodig.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public static List<MailBericht> Load()
    {
        try
        {
            if (File.Exists(CacheFile) &&
                JsonSerializer.Deserialize<List<MailBericht>>(
                    File.ReadAllText(CacheFile), JsonOpts) is { } berichten)
            {
                return berichten;
            }
        }
        catch
        {
            // Onleesbaar: gewoon zonder cache starten.
        }
        return new List<MailBericht>();
    }

    public static void Save(List<MailBericht> berichten)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(berichten, JsonOpts));
        }
        catch
        {
            // Cache is best effort; een mislukte save mag de cockpit niet storen.
        }
    }
}
