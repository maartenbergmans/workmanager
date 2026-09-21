using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>Momentopname van één fonds, zoals de beurs hem nu geeft.</summary>
public sealed record Koers(
    string Ticker,
    string Naam,
    string Beurs,
    string Valuta,
    double Prijs,
    double VorigSlot,
    DateTimeOffset Moment,
    double DagHoog,
    double DagLaag,
    double JaarHoog,
    double JaarLaag,
    bool BeursOpen,
    bool UitCache)
{
    public double DagVerschil => Prijs - VorigSlot;

    public double DagProcent => VorigSlot > 0 ? (Prijs - VorigSlot) / VorigSlot * 100 : 0;

    /// <summary>Waar de koers staat tussen jaarlaag en jaarhoog (0..1); NaN zonder bereik.</summary>
    public double JaarPositie =>
        JaarHoog > JaarLaag ? Math.Clamp((Prijs - JaarLaag) / (JaarHoog - JaarLaag), 0, 1) : double.NaN;
}

/// <summary>Slotkoersen per dag, oplopend in de tijd.</summary>
public sealed record KoersReeks(string Ticker, IReadOnlyList<(DateOnly Dag, double Slot)> Punten);

/// <summary>
/// Haalt koersen op bij Yahoo Finance (de publieke chart-endpoint, zonder sleutel) en houdt
/// ze kort in het geheugen én blijvend op schijf vast. Die schijfcache maakt het venster
/// bruikbaar zodra het opent — en blijft werken zonder internet of wanneer Yahoo hapert:
/// dan toont het venster de laatst bekende koers met de bijhorende tijdstempel.
/// </summary>
public static class Koersen
{
    private const int VersheidSeconden = 45;
    private const int ReeksVersheidMinuten = 180;

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "koersen-cache.json");

    // Yahoo antwoordt met 404 op verzoeken zonder geloofwaardige browser-User-Agent; met
    // deze header werkt dezelfde URL wél. Niet weghalen.
    private static readonly HttpClient Http = MaakClient();

    private static HttpClient MaakClient()
    {
        // Yahoo doet er geregeld tien seconden of meer over; met een krappe timeout viel het
        // venster daardoor onnodig terug op de cache.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/130.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    private static readonly object Grendel = new();
    private static Cache? _cache;

    // ---------- Koersen ----------

    /// <summary>
    /// De koers van één fonds. Binnen <see cref="VersheidSeconden"/> komt hetzelfde antwoord
    /// terug (zo mag elk venster vrij vaak vragen), daarbuiten gaat er één verzoek uit.
    /// Mislukt dat, dan komt de laatst bekende koers terug met <c>UitCache = true</c>.
    /// </summary>
    public static async Task<Koers?> HaalAsync(string ticker, CancellationToken ct)
    {
        ticker = ticker.Trim();
        if (ticker.Length == 0)
        {
            return null;
        }
        if (Bewaard(ticker) is { } vers &&
            DateTimeOffset.Now - vers.Opgehaald < TimeSpan.FromSeconds(VersheidSeconden))
        {
            return vers.AlsKoers(uitCache: false);
        }

        try
        {
            using var doc = await HaalJsonAsync(ticker, "1d", "1d", ct);
            var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
            var koers = LeesMeta(ticker, meta);
            if (koers is not null)
            {
                Onthoud(ticker, koers);
            }
            return koers ?? Bewaard(ticker)?.AlsKoers(uitCache: true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Bewaard(ticker)?.AlsKoers(uitCache: true);
        }
    }

    /// <summary>Alle koersen tegelijk; de verzoeken lopen naast elkaar.</summary>
    public static async Task<Dictionary<string, Koers>> HaalAllemaalAsync(
        IEnumerable<string> tickers, CancellationToken ct)
    {
        var uniek = tickers.Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resultaten = await Task.WhenAll(uniek.Select(t => HaalAsync(t, ct)));
        var kaart = new Dictionary<string, Koers>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < uniek.Count; i++)
        {
            if (resultaten[i] is { } koers)
            {
                kaart[uniek[i]] = koers;
            }
        }
        return kaart;
    }

    /// <summary>De laatst bewaarde koers, zonder netwerk — voor een gevuld venster bij het openen.</summary>
    public static Koers? UitCache(string ticker) => Bewaard(ticker.Trim())?.AlsKoers(uitCache: true);

    /// <summary>
    /// Omrekenfactor van een vreemde munt naar euro (1 voor EUR). De portefeuille rekent in
    /// euro, maar een fonds op een andere beurs hoeft daar niet in te noteren.
    /// </summary>
    public static async Task<double> NaarEuroAsync(string valuta, CancellationToken ct)
    {
        if (valuta.Length == 0 || valuta.Equals("EUR", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        var koers = await HaalAsync($"{valuta.ToUpperInvariant()}EUR=X", ct);
        return koers is { Prijs: > 0 } ? koers.Prijs : 1;
    }

    // ---------- Historiek ----------

    /// <summary>
    /// Dagelijkse slotkoersen over een periode ("1mo", "3mo", "1y", "5y"). Ook die blijven op
    /// schijf staan, zodat de grafiek er meteen is en een storing hem niet leeg maakt.
    /// </summary>
    public static async Task<KoersReeks?> HistoriekAsync(string ticker, string periode, CancellationToken ct)
    {
        ticker = ticker.Trim();
        var sleutel = $"{ticker}|{periode}";
        var bewaard = BewaardeReeks(sleutel);
        if (bewaard is not null &&
            DateTimeOffset.Now - bewaard.Opgehaald < TimeSpan.FromMinutes(ReeksVersheidMinuten))
        {
            return new KoersReeks(ticker, bewaard.AlsPunten());
        }

        try
        {
            using var doc = await HaalJsonAsync(ticker, periode, "1d", ct);
            var resultaat = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var tijden = resultaat.GetProperty("timestamp");
            var sloten = resultaat.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close");
            var punten = new List<(DateOnly, double)>();
            for (var i = 0; i < tijden.GetArrayLength() && i < sloten.GetArrayLength(); i++)
            {
                // Dagen zonder handel (een feestdag op één van beide beurzen) komen als null terug.
                if (sloten[i].ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                var moment = DateTimeOffset.FromUnixTimeSeconds(tijden[i].GetInt64()).ToLocalTime();
                punten.Add((DateOnly.FromDateTime(moment.DateTime), sloten[i].GetDouble()));
            }
            if (punten.Count == 0)
            {
                return bewaard is null ? null : new KoersReeks(ticker, bewaard.AlsPunten());
            }
            OnthoudReeks(sleutel, punten);
            return new KoersReeks(ticker, punten);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return bewaard is null ? null : new KoersReeks(ticker, bewaard.AlsPunten());
        }
    }

    // ---------- Yahoo ----------

    private static async Task<JsonDocument> HaalJsonAsync(
        string ticker, string range, string interval, CancellationToken ct)
    {
        var pad = $"/v8/finance/chart/{Uri.EscapeDataString(ticker)}" +
                  $"?range={range}&interval={interval}&includePrePost=false";
        Exception? laatste = null;
        // query1 valt wel eens uit; query2 serveert exact dezelfde API.
        foreach (var host in new[] { "https://query1.finance.yahoo.com", "https://query2.finance.yahoo.com" })
        {
            try
            {
                var json = await Http.GetStringAsync(host + pad, ct);
                return JsonDocument.Parse(json);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                laatste = ex;
            }
        }
        throw laatste ?? new HttpRequestException("Geen koersbron bereikbaar");
    }

    private static Koers? LeesMeta(string ticker, JsonElement meta)
    {
        if (Getal(meta, "regularMarketPrice") is not { } prijs || prijs <= 0)
        {
            return null;
        }
        var vorig = Getal(meta, "chartPreviousClose") ?? Getal(meta, "previousClose") ?? prijs;
        var moment = meta.TryGetProperty("regularMarketTime", out var t) && t.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).ToLocalTime()
            : DateTimeOffset.Now;
        return new Koers(
            ticker,
            Tekst(meta, "longName") ?? Tekst(meta, "shortName") ?? ticker,
            Tekst(meta, "fullExchangeName") ?? Tekst(meta, "exchangeName") ?? "",
            Tekst(meta, "currency")?.ToUpperInvariant() ?? "EUR",
            prijs,
            vorig,
            moment,
            Getal(meta, "regularMarketDayHigh") ?? prijs,
            Getal(meta, "regularMarketDayLow") ?? prijs,
            Getal(meta, "fiftyTwoWeekHigh") ?? 0,
            Getal(meta, "fiftyTwoWeekLow") ?? 0,
            HandelsuurLoopt(meta),
            UitCache: false);
    }

    /// <summary>Loopt de gewone handelssessie nu? Yahoo geeft dat venster in epochseconden mee.</summary>
    private static bool HandelsuurLoopt(JsonElement meta)
    {
        try
        {
            var regulier = meta.GetProperty("currentTradingPeriod").GetProperty("regular");
            var nu = DateTimeOffset.Now.ToUnixTimeSeconds();
            return nu >= regulier.GetProperty("start").GetInt64() &&
                   nu <= regulier.GetProperty("end").GetInt64();
        }
        catch
        {
            return false;
        }
    }

    private static double? Getal(JsonElement el, string naam) =>
        el.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Tekst(JsonElement el, string naam) =>
        el.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- Schijfcache ----------

    private sealed class Cache
    {
        public Dictionary<string, BewaardeKoers> Koersen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, BewaardeReeksData> Reeksen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BewaardeKoers
    {
        public DateTimeOffset Opgehaald { get; set; }
        public string Naam { get; set; } = "";
        public string Beurs { get; set; } = "";
        public string Valuta { get; set; } = "EUR";
        public double Prijs { get; set; }
        public double VorigSlot { get; set; }
        public DateTimeOffset Moment { get; set; }
        public double DagHoog { get; set; }
        public double DagLaag { get; set; }
        public double JaarHoog { get; set; }
        public double JaarLaag { get; set; }
        public bool BeursOpen { get; set; }

        /// <summary>Komt uit de sleutel van het woordenboek, staat dus niet apart in het bestand.</summary>
        [JsonIgnore]
        public string Ticker { get; set; } = "";

        public Koers AlsKoers(bool uitCache) => new(
            Ticker, Naam, Beurs, Valuta, Prijs, VorigSlot, Moment,
            DagHoog, DagLaag, JaarHoog, JaarLaag, BeursOpen && !uitCache, uitCache);
    }

    private sealed class BewaardeReeksData
    {
        public DateTimeOffset Opgehaald { get; set; }

        /// <summary>Punten als "2026-09-18=127.05" — compact genoeg voor vijf jaar dagkoersen.</summary>
        public List<string> Punten { get; set; } = new();

        public IReadOnlyList<(DateOnly Dag, double Slot)> AlsPunten()
        {
            var lijst = new List<(DateOnly, double)>(Punten.Count);
            foreach (var regel in Punten)
            {
                var scheiding = regel.IndexOf('=');
                if (scheiding > 0 &&
                    DateOnly.TryParse(regel[..scheiding], CultureInfo.InvariantCulture, out var dag) &&
                    double.TryParse(regel[(scheiding + 1)..], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var slot))
                {
                    lijst.Add((dag, slot));
                }
            }
            return lijst;
        }
    }

    private static Cache LaadCache()
    {
        lock (Grendel)
        {
            if (_cache is not null)
            {
                return _cache;
            }
            try
            {
                if (File.Exists(CacheFile) &&
                    JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) is { } bewaard)
                {
                    foreach (var (ticker, koers) in bewaard.Koersen)
                    {
                        koers.Ticker = ticker;
                    }
                    return _cache = bewaard;
                }
            }
            catch
            {
                // Beschadigde cache: opnieuw beginnen.
            }
            return _cache = new Cache();
        }
    }

    private static BewaardeKoers? Bewaard(string ticker) =>
        LaadCache().Koersen.TryGetValue(ticker, out var k) ? k : null;

    private static BewaardeReeksData? BewaardeReeks(string sleutel) =>
        LaadCache().Reeksen.TryGetValue(sleutel, out var r) ? r : null;

    private static void Onthoud(string ticker, Koers koers)
    {
        var cache = LaadCache();
        lock (Grendel)
        {
            cache.Koersen[ticker] = new BewaardeKoers
            {
                Ticker = ticker,
                Opgehaald = DateTimeOffset.Now,
                Naam = koers.Naam,
                Beurs = koers.Beurs,
                Valuta = koers.Valuta,
                Prijs = koers.Prijs,
                VorigSlot = koers.VorigSlot,
                Moment = koers.Moment,
                DagHoog = koers.DagHoog,
                DagLaag = koers.DagLaag,
                JaarHoog = koers.JaarHoog,
                JaarLaag = koers.JaarLaag,
                BeursOpen = koers.BeursOpen,
            };
        }
        BewaarCache();
    }

    private static void OnthoudReeks(string sleutel, List<(DateOnly Dag, double Slot)> punten)
    {
        var cache = LaadCache();
        lock (Grendel)
        {
            cache.Reeksen[sleutel] = new BewaardeReeksData
            {
                Opgehaald = DateTimeOffset.Now,
                Punten = punten
                    .Select(p => $"{p.Dag:yyyy-MM-dd}={p.Slot.ToString(CultureInfo.InvariantCulture)}")
                    .ToList(),
            };
        }
        BewaarCache();
    }

    private static void BewaarCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            string json;
            lock (Grendel)
            {
                json = JsonSerializer.Serialize(_cache);
            }
            File.WriteAllText(CacheFile, json);
        }
        catch
        {
            // Zonder schijfcache werkt alles, alleen minder snel na een herstart.
        }
    }
}
