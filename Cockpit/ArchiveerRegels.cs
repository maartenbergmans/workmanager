using System.Text.Json;

namespace WorkManager;

/// <summary>Eén zelfgemaakte archiveerregel: matcht op afzender en/of onderwerp (bevat, hoofdletterongevoelig).</summary>
public sealed class ArchiveerRegel
{
    public string Afzender { get; set; } = ""; // leeg = elke afzender
    public string Onderwerp { get; set; } = ""; // leeg = elk onderwerp

    public bool Match(MailBericht m)
    {
        if (Afzender.Length == 0 && Onderwerp.Length == 0)
        {
            return false; // lege regel matcht bewust niets
        }
        var afzenderOk = Afzender.Length == 0 ||
            m.Van.Contains(Afzender, StringComparison.OrdinalIgnoreCase) ||
            m.VanAdres.Contains(Afzender, StringComparison.OrdinalIgnoreCase);
        var onderwerpOk = Onderwerp.Length == 0 ||
            m.Onderwerp.Contains(Onderwerp, StringComparison.OrdinalIgnoreCase);
        return afzenderOk && onderwerpOk;
    }

    public override string ToString() =>
        (Afzender.Length > 0 ? $"van \"{Afzender}\"" : "") +
        (Afzender.Length > 0 && Onderwerp.Length > 0 ? "  én  " : "") +
        (Onderwerp.Length > 0 ? $"onderwerp bevat \"{Onderwerp}\"" : "");
}

/// <summary>
/// Zelf te beheren auto-archiveerregels (naast de vaste in code): mails die matchen worden
/// bij elke poll automatisch gearchiveerd en niet in de cockpit getoond. Beheer via het
/// venster "Archiveerregels…" of rechtsklik op een mail ("Regel maken van dit bericht").
/// Opslag: %APPDATA%\WorkManager\archiveer-regels.json.
/// </summary>
public static class ArchiveerRegels
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "archiveer-regels.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<ArchiveerRegel> Load()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<List<ArchiveerRegel>>(File.ReadAllText(Bestand), JsonOpts) is { } r)
            {
                return r;
            }
        }
        catch
        {
            // Onleesbaar: zonder regels verder.
        }
        return new List<ArchiveerRegel>();
    }

    private static readonly object CacheSlot = new();

    private static List<ArchiveerRegel>? _cache;

    private static DateTime _cacheTijd;

    /// <summary>
    /// Zelfde als <see cref="Load"/>, maar leest het bestand alleen opnieuw als het echt
    /// gewijzigd is. De weergavefilter (<see cref="BerichtFilter"/>) draait bij elke herbouw
    /// van de berichtenlijst — ook per toetsaanslag in het zoekveld — en dat hoeft geen
    /// schijfactie te zijn. Een regel die net is toegevoegd komt er wel meteen door, want
    /// het tijdstempel van het bestand verandert dan.
    /// </summary>
    public static List<ArchiveerRegel> LoadGecached()
    {
        lock (CacheSlot)
        {
            try
            {
                var tijd = File.Exists(Bestand) ? File.GetLastWriteTimeUtc(Bestand) : DateTime.MinValue;
                if (_cache is not null && tijd == _cacheTijd)
                {
                    return _cache;
                }
                _cacheTijd = tijd;
                return _cache = Load();
            }
            catch
            {
                return _cache ?? new List<ArchiveerRegel>();
            }
        }
    }

    public static void Save(List<ArchiveerRegel> regels)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(regels, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
        lock (CacheSlot)
        {
            _cache = null; // volgende weergave leest de nieuwe regels
        }
    }

    /// <summary>Matcht het bericht op één van de bewaarde regels?</summary>
    public static bool Matcht(MailBericht m, List<ArchiveerRegel> regels) =>
        regels.Any(r => r.Match(m));
}
