using System.Net.Http;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Het seizoensdrankje bij de ochtendbegroeting: kijkt via Open-Meteo (gratis, geen sleutel)
/// wat voor dag het wordt in Brasschaat en stelt het passende drankje voor — ijskoffie boven
/// de 25°, chocolademelk bij de eerste sneeuw. Elke dag hooguit één keer opgehaald; zonder
/// internet valt hij terug op het seizoen alleen.
/// </summary>
public static class WeerDrankje
{
    // Henrilei, Brasschaat — nauwkeurig genoeg voor een drankje.
    private const string WeerUrl =
        "https://api.open-meteo.com/v1/forecast?latitude=51.29&longitude=4.49" +
        "&daily=temperature_2m_max,weathercode&timezone=Europe%2FBrussels&forecast_days=1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "weer-vandaag.json");

    private sealed class Cache
    {
        public string Dag { get; set; } = "";
        public double MaxTemp { get; set; }
        public int Code { get; set; }
        public bool Gelukt { get; set; }
    }

    /// <summary>Het drankje voor bij de ochtendbegroeting; null buiten de ochtend.</summary>
    public static async Task<string?> VoorstelAsync(CancellationToken ct)
    {
        if (DateTime.Now.Hour is < 5 or >= 12)
        {
            return null; // een ochtenddrankje hoort bij de ochtend
        }
        var weer = await WeerVandaagAsync(ct);
        return Drankje(weer);
    }

    /// <summary>Kiest het drankje bij weer en seizoen; zonder weer beslist het seizoen.</summary>
    private static string Drankje((double MaxTemp, int Code)? weer)
    {
        var maand = DateTime.Now.Month;
        // Het kleurenschema mag meepraten: 007 bestelt iets uit de bar (alcoholvrij, het is
        // ochtend), Zomer een mocktail. Bij extreem weer wint het weer alsnog, hieronder.
        if (weer is null or { Code: < 51, MaxTemp: > 3 and < 25 })
        {
            var themaDrank = Theme.Palet.Naam switch
            {
                "007" => "🍸 Ochtendbriefing — een virgin martini, geschud",
                "Zomer" => "🍹 Zomerse start — mocktail met munt en limoen",
                "Neon" => "⚡ Neon-ochtend — ijskoude energie in een glas",
                "Espresso" => "☕ Espresso-ochtend — dubbel, zonder suiker",
                _ => "",
            };
            if (themaDrank.Length > 0)
            {
                return themaDrank;
            }
        }
        if (weer is { } w)
        {
            // WMO-weathercodes: 71-77/85-86 = sneeuw, 51-67/80-82 = (mot)regen/buien,
            // 95+ = onweer.
            if (w.Code is >= 71 and <= 77 or 85 or 86)
            {
                return "☃️ Sneeuw vandaag — warme chocolademelk is verplicht";
            }
            if (w.MaxTemp >= 25)
            {
                return $"🧊 {Math.Round(w.MaxTemp)}° vandaag — ijskoffie-weer";
            }
            if (w.Code is >= 95)
            {
                return "⛈️ Onweer op komst — sterke koffie en binnenblijven";
            }
            if (w.Code is >= 51 and <= 67 or >= 80 and <= 82)
            {
                return "☔ Regen vandaag — thermoskan koffie erbij, perfecte focusdag";
            }
            if (w.MaxTemp <= 3)
            {
                return $"🍫 Amper {Math.Round(w.MaxTemp)}° — chocolademelk-weer";
            }
            if (w.MaxTemp >= 20 && maand is >= 6 and <= 8)
            {
                return $"🍹 Zomerse {Math.Round(w.MaxTemp)}° — koude thee in de buurt houden";
            }
        }
        return maand switch
        {
            >= 9 and <= 11 => "🎃 Herfst — pompoen-latte-seizoen, officieel",
            12 or 1 or 2 => "☕ Winterochtend — dubbele espresso tegen de kou",
            >= 3 and <= 5 => "🌷 Lente — verse muntthee past erbij",
            _ => "☕ Gewoon een goeie koffie dan",
        };
    }

    /// <summary>Max-temperatuur en weathercode van vandaag; per dag gecachet. Null = niet te halen.</summary>
    private static async Task<(double MaxTemp, int Code)?> WeerVandaagAsync(CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var cache = LaadCache();
        if (cache.Dag == vandaag)
        {
            return cache.Gelukt ? (cache.MaxTemp, cache.Code) : null;
        }
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(WeerUrl, ct));
            var daily = doc.RootElement.GetProperty("daily");
            var temp = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            var code = daily.GetProperty("weathercode")[0].GetInt32();
            BewaarCache(new Cache { Dag = vandaag, MaxTemp = temp, Code = code, Gelukt = true });
            return (temp, code);
        }
        catch
        {
            // Ook mislukkingen cachen: niet elke begroeting opnieuw proberen.
            BewaarCache(new Cache { Dag = vandaag, Gelukt = false });
            return null;
        }
    }

    private static Cache LaadCache()
    {
        try
        {
            if (File.Exists(CacheFile) &&
                JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) is { } c)
            {
                return c;
            }
        }
        catch
        {
            // Onleesbaar: gewoon opnieuw ophalen.
        }
        return new Cache();
    }

    private static void BewaarCache(Cache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Best effort.
        }
    }
}
