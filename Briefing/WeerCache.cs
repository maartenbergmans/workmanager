using System.Globalization;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Houdt de weersverwachting van twee weken vooruit (en een paar dagen terug) bij de hand, in
/// het geheugen én op schijf (<c>weer-cache.json</c>). Reden: de kalender vroeg het weer per
/// dag opnieuw op, dus bij elk klikje naar de volgende dag stond het weer te wachten op een
/// netwerkverzoek. Nu staat het er meteen — en klopt het na een herstart ook al voor je iets
/// hebt opgehaald. Eén verzoek dekt de hele reeks; na twee uur wordt hij vernieuwd.
/// </summary>
public static class WeerCache
{
    private const int DagenTerug = 2;
    private const int DagenVooruit = 14;
    private static readonly TimeSpan Versheid = TimeSpan.FromHours(2);

    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "weer-cache.json");

    private static readonly object Grendel = new();
    private static Dictionary<DateOnly, Weer.Verwachting>? _dagen;
    private static DateTimeOffset _opgehaald;
    private static Task? _lopend;

    /// <summary>De verwachting voor een dag uit de cache, zonder netwerk (null = nog niet bekend).</summary>
    public static Weer.Verwachting? Uit(DateOnly dag)
    {
        lock (Grendel)
        {
            Laad();
            return _dagen!.TryGetValue(dag, out var verwachting) ? verwachting : null;
        }
    }

    /// <summary>
    /// De verwachting voor een dag; ververst de hele reeks als die oud is of de dag nog niet
    /// bevat (bv. na middernacht, of bij het vooruitbladeren voorbij het opgehaalde venster).
    /// </summary>
    public static async Task<Weer.Verwachting?> VoorDagAsync(
        double lat, double lon, DateOnly dag, CancellationToken ct)
    {
        bool verversen;
        lock (Grendel)
        {
            Laad();
            verversen = DateTimeOffset.Now - _opgehaald > Versheid || !_dagen!.ContainsKey(dag);
        }
        if (verversen)
        {
            await VerversAsync(lat, lon, ct);
        }
        return Uit(dag);
    }

    /// <summary>
    /// Haalt de hele reeks opnieuw op. Loopt er al een ophaling, dan wordt daarop gewacht in
    /// plaats van een tweede verzoek te sturen — de kalender en de dagstart vragen het vaak
    /// vlak na elkaar.
    /// </summary>
    public static Task VerversAsync(double lat, double lon, CancellationToken ct)
    {
        lock (Grendel)
        {
            if (_lopend is { IsCompleted: false } bezig)
            {
                return bezig;
            }
            return _lopend = HaalAsync(lat, lon, ct);
        }
    }

    /// <summary>Ververst met het ingestelde thuisadres; zonder adres gebeurt er niets.</summary>
    public static Task VerversAsync(CancellationToken ct)
    {
        var reis = ReisSettings.Load();
        return reis.HeeftThuis ? VerversAsync(reis.ThuisLat, reis.ThuisLon, ct) : Task.CompletedTask;
    }

    private static async Task HaalAsync(double lat, double lon, CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var dagen = await Weer.ReeksAsync(
            lat, lon, vandaag.AddDays(-DagenTerug), vandaag.AddDays(DagenVooruit), ct);
        if (dagen.Count == 0)
        {
            return; // niets binnen: de vorige inhoud blijft staan
        }
        lock (Grendel)
        {
            _dagen = dagen;
            _opgehaald = DateTimeOffset.Now;
        }
        Bewaar();
    }

    private sealed class OpSchijf
    {
        public DateTimeOffset Opgehaald { get; set; }

        public Dictionary<string, Weer.Verwachting> Dagen { get; set; } = new();
    }

    private static void Laad()
    {
        if (_dagen is not null)
        {
            return;
        }
        _dagen = new Dictionary<DateOnly, Weer.Verwachting>();
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<OpSchijf>(File.ReadAllText(DataFile)) is { } bewaard)
            {
                foreach (var (dag, verwachting) in bewaard.Dagen)
                {
                    if (DateOnly.TryParse(dag, CultureInfo.InvariantCulture, out var d))
                    {
                        _dagen[d] = verwachting;
                    }
                }
                _opgehaald = bewaard.Opgehaald;
            }
        }
        catch
        {
            // Onleesbaar: gewoon opnieuw ophalen.
        }
    }

    private static void Bewaar()
    {
        try
        {
            OpSchijf inhoud;
            lock (Grendel)
            {
                inhoud = new OpSchijf
                {
                    Opgehaald = _opgehaald,
                    Dagen = _dagen!.ToDictionary(
                        p => p.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), p => p.Value),
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(inhoud,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Zonder schijfcache werkt alles, alleen niet meteen na een herstart.
        }
    }
}
