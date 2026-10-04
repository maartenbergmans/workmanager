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

    /// <summary>
    /// De laatst bekende lijst. Berichten die intussen onder een archiveerregel vallen komen
    /// er niet meer uit: de cache wordt op veel plaatsen gelezen (de tussenstanden van de
    /// pollronde, de webversie, de pushmeldingen, de dagbriefing en het dagplan), en een
    /// regel die net is toegevoegd of nog niet uitgevoerd kon worden mag nergens nog even
    /// een rij opleveren.
    /// </summary>
    public static List<MailBericht> Load()
    {
        try
        {
            if (File.Exists(CacheFile) &&
                JsonSerializer.Deserialize<List<MailBericht>>(
                    File.ReadAllText(CacheFile), JsonOpts) is { } berichten)
            {
                BerichtFilter.Verberg(berichten);
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
            // Alleen wat zichtbaar mag zijn gaat de cache in; de lijst van de aanroeper
            // blijft ongemoeid (die heeft de rest nog nodig, bv. voor het zelfherstel van
            // Outlook-mails die niet verplaatst bleken).
            var zichtbaar = berichten.Where(m => !BerichtFilter.Verbergen(m)).ToList();
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(zichtbaar, JsonOpts));
        }
        catch
        {
            // Cache is best effort; een mislukte save mag de cockpit niet storen.
        }
    }
}
