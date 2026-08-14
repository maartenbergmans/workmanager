using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WorkManager;

/// <summary>
/// Haalt bij een gerechtnaam een aantrekkelijke foto op en cachet die lokaal, zodat de
/// bestelflow naast elk gerecht een beeld toont. Eerst wordt Allerhande geprobeerd (echte
/// gerechtfoto's van de receptensite — www.ah.nl is, anders dan www.ah.be, gewoon
/// server-side bereikbaar en de zoekpagina bevat de static.ah.nl-receptfoto's letterlijk in
/// de HTML); lukt dat niet, dan valt hij terug op het beste AH-productzoekresultaat.
/// Downloaden gebeurt op de achtergrond; zodra er een nieuwe foto klaar is vuurt
/// <see cref="BeeldKlaar"/> zodat de lijst hertekent.
/// </summary>
public static class GerechtFoto
{
    /// <summary>Zijde (px) van de bewaarde gerechtfoto — ruim voor een aantrekkelijke rij.</summary>
    private const int Formaat = 120;

    private static readonly string CacheMap = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-gerechtfotos");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Image> _cache = new();
    private static readonly ConcurrentDictionary<string, byte> _bezig = new();
    private static readonly SemaphoreSlim _poort = new(3);

    /// <summary>Vuurt (op een achtergrondthread) zodra er een nieuwe gerechtfoto in de cache staat.</summary>
    public static event Action? BeeldKlaar;

    private static readonly string OverridesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-gerechtfoto-overrides.json");

    private static Dictionary<string, string>? _overrides;

    /// <summary>
    /// Handmatig vastgelegde foto voor een gerecht (ah-gerechtfoto-overrides.json:
    /// gerechtnaam → beeld-url). Wint altijd van de zoektocht — voor gerechten waar de
    /// automatiek een verkeerd beeld bij vindt.
    /// </summary>
    private static string? Override(string naam)
    {
        if (_overrides is null)
        {
            try
            {
                _overrides = File.Exists(OverridesFile)
                    ? new Dictionary<string, string>(
                        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(OverridesFile)) ?? new(),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        return _overrides.GetValueOrDefault(naam.Trim());
    }

    /// <summary>Bestandssleutel (veilige bestandsnaam) voor een gerechtnaam.</summary>
    private static string Sleutel(string naam)
    {
        var schoon = new string(naam.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return schoon.Trim('-');
    }

    /// <summary>
    /// De gecachete foto voor een gerechtnaam, of null als hij er (nog) niet is. Bij null wordt
    /// eenmaal een achtergrond-download gestart; slaagt die, dan volgt <see cref="BeeldKlaar"/>.
    /// </summary>
    public static Image? Voor(string? naam)
    {
        if (string.IsNullOrWhiteSpace(naam))
        {
            return null;
        }
        var id = Sleutel(naam);
        if (id.Length == 0)
        {
            return null;
        }
        if (_cache.TryGetValue(id, out var beeld))
        {
            return beeld;
        }
        if (LaadVanSchijf(id) is { } vanSchijf)
        {
            return _cache[id] = vanSchijf;
        }
        if (_bezig.TryAdd(id, 0))
        {
            _ = Task.Run(() => Ophalen(naam, id));
        }
        return null;
    }

    private static readonly ConcurrentDictionary<string, string?> _urlCache = new();

    private static readonly string UrlsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-gerechtfoto-urls.json");

    private static readonly object _urlSlot = new();
    private static Dictionary<string, string>? _bekendeUrls;

    /// <summary>
    /// De beeld-url voor een gerechtnaam (override → Allerhande → beeldzoek → productfoto),
    /// zonder te downloaden — voor de gsm-bestelpagina, die de foto zelf ophaalt. Null als er
    /// niets te vinden is; het resultaat (ook null) wordt per sessie onthouden.
    /// </summary>
    public static async Task<string?> UrlAsync(string naam)
    {
        var id = Sleutel(naam);
        if (id.Length == 0)
        {
            return null;
        }
        if (_urlCache.TryGetValue(id, out var bekend))
        {
            return bekend;
        }
        return _urlCache[id] = await ZoekOfHerinnerUrlAsync(naam, id);
    }

    /// <summary>
    /// Zoekt (of herinnert) de beeld-url voor een gerecht. De gekozen url wordt op schijf
    /// bewaard (ah-gerechtfoto-urls.json), zodat de pc-kaarten en de gsm-pagina gegarandeerd
    /// dezelfde foto tonen — de zoekketen (beeldzoek!) is namelijk niet deterministisch.
    /// Een override wint altijd, ook van een eerder bewaarde keuze.
    /// </summary>
    private static async Task<string?> ZoekOfHerinnerUrlAsync(string naam, string id)
    {
        if (Override(naam) is { } vast)
        {
            return vast;
        }
        lock (_urlSlot)
        {
            if (BekendeUrls().TryGetValue(id, out var bewaard))
            {
                return bewaard;
            }
        }
        var url = await ZoekReceptFotoAsync(naam) ??
            await ZoekOpDuckDuckGoAsync(naam) ??
            (await AhApi.ZoekTopAsync(ZoekTermen(naam).Last()))?.BeeldUrl;
        if (url is not null)
        {
            lock (_urlSlot)
            {
                BekendeUrls()[id] = url;
                BewaarBekendeUrls();
            }
        }
        return url;
    }

    /// <summary>Vergeet de bewaarde url (bv. omdat downloaden mislukte): volgende keer opnieuw zoeken.</summary>
    private static void VergeetUrl(string id)
    {
        lock (_urlSlot)
        {
            if (BekendeUrls().Remove(id))
            {
                BewaarBekendeUrls();
            }
        }
        _urlCache.TryRemove(id, out _);
    }

    private static Dictionary<string, string> BekendeUrls()
    {
        if (_bekendeUrls is null)
        {
            try
            {
                _bekendeUrls = File.Exists(UrlsFile)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(UrlsFile)) ?? new()
                    : new Dictionary<string, string>();
            }
            catch
            {
                _bekendeUrls = new Dictionary<string, string>();
            }
        }
        return _bekendeUrls;
    }

    private static void BewaarBekendeUrls()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UrlsFile)!);
            File.WriteAllText(UrlsFile, System.Text.Json.JsonSerializer.Serialize(
                _bekendeUrls, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Cache is gemak, geen voorwaarde.
        }
    }

    /// <summary>Warmt de cache alvast op voor een reeks gerechtnamen (bij het openen van de lijst).</summary>
    public static void Voorladen(IEnumerable<string> namen)
    {
        foreach (var naam in namen)
        {
            Voor(naam);
        }
    }

    /// <summary>Cachebestand voor een gerecht; formaat én bron ("r" = receptfoto-tijdperk)
    /// zitten in de naam zodat oude thumbnails (kleiner, of productfoto's) niet blijven hangen.</summary>
    private static string CachePad(string id) => Path.Combine(CacheMap, $"{id}@{Formaat}r.png");

    private static Image? LaadVanSchijf(string id)
    {
        var pad = CachePad(id);
        try
        {
            if (!File.Exists(pad))
            {
                return null;
            }
            using var ms = new MemoryStream(File.ReadAllBytes(pad));
            using var tijdelijk = new Bitmap(ms);
            return new Bitmap(tijdelijk);
        }
        catch
        {
            return null;
        }
    }

    private static async Task Ophalen(string naam, string id)
    {
        try
        {
            await _poort.WaitAsync();
            try
            {
                // Vastgelegde foto eerst; dan de gedeelde (bewaarde) zoekkeuze, zodat lijst,
                // receptkaart én gsm-pagina dezelfde foto tonen.
                var url = await ZoekOfHerinnerUrlAsync(naam, id);
                if (url is null)
                {
                    return;
                }
                byte[] bytes;
                try
                {
                    bytes = await Http.GetByteArrayAsync(url);
                }
                catch
                {
                    VergeetUrl(id); // dode link: volgende keer opnieuw zoeken
                    throw;
                }
                using var bron = new Bitmap(new MemoryStream(bytes));
                var thumb = Verklein(bron, Formaat);

                Directory.CreateDirectory(CacheMap);
                thumb.Save(CachePad(id), ImageFormat.Png);
                _cache[id] = thumb;
                BeeldKlaar?.Invoke();
            }
            finally
            {
                _poort.Release();
            }
        }
        catch
        {
            // Mislukt: id blijft in _bezig staan zodat we deze sessie niet blijven proberen.
        }
    }

    private static readonly ConcurrentDictionary<string, Image> _kaartCache = new();
    private static readonly ConcurrentDictionary<string, byte> _kaartBezig = new();

    /// <summary>
    /// Grote kaartfoto, niet-blokkerend: meteen als hij in het geheugen zit, anders start er
    /// één achtergrond-download en vuurt <see cref="BeeldKlaar"/> zodra hij er is. Voor het
    /// gerechten-grid, dat per paint om de foto vraagt.
    /// </summary>
    public static Image? Kaart(string naam)
    {
        var id = Sleutel(naam);
        if (id.Length == 0)
        {
            return null;
        }
        if (_kaartCache.TryGetValue(id, out var beeld))
        {
            return beeld;
        }
        if (_kaartBezig.TryAdd(id, 0))
        {
            _ = Task.Run(async () =>
            {
                if (await GrootAsync(naam) is { } groot)
                {
                    _kaartCache[id] = groot;
                    BeeldKlaar?.Invoke();
                }
            });
        }
        return null;
    }

    /// <summary>
    /// Grote receptfoto (612x450) voor de receptkaart, met schijfcache. Valt terug op de
    /// productfoto als Allerhande niets kent; null als er helemaal geen beeld te vinden is.
    /// </summary>
    public static async Task<Image?> GrootAsync(string naam)
    {
        var id = Sleutel(naam);
        if (id.Length == 0)
        {
            return null;
        }
        var pad = Path.Combine(CacheMap, $"{id}@groot.jpg");
        try
        {
            if (File.Exists(pad))
            {
                using var ms = new MemoryStream(File.ReadAllBytes(pad));
                using var tijdelijk = new Bitmap(ms);
                return new Bitmap(tijdelijk);
            }
        }
        catch
        {
            // Kapotte cache: opnieuw downloaden.
        }
        try
        {
            var url = await ZoekOfHerinnerUrlAsync(naam, id);
            if (url is null)
            {
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = await Http.GetByteArrayAsync(url);
            }
            catch
            {
                VergeetUrl(id); // dode link: volgende keer opnieuw zoeken
                return null;
            }
            using var bron = new MemoryStream(bytes);
            using var beeld = new Bitmap(bron);
            var kopie = new Bitmap(beeld);
            try
            {
                Directory.CreateDirectory(CacheMap);
                File.WriteAllBytes(pad, bytes);
            }
            catch
            {
                // Cache is gemak, geen voorwaarde.
            }
            return kopie;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// De foto van het eerste Allerhande-zoekresultaat, met steeds eenvoudigere zoektermen
    /// tot er iets gevonden is. De zoekpagina van www.ah.nl is server-side bereikbaar (200,
    /// geen login) en bevat de receptfoto's letterlijk als static.ah.nl-urls in de HTML; de
    /// 612x450-rendition is ruim genoeg. Volgorde in de HTML = volgorde van de resultaten.
    /// </summary>
    private static async Task<string?> ZoekReceptFotoAsync(string naam)
    {
        foreach (var term in ZoekTermen(naam))
        {
            if (await ZoekOpAllerhandeAsync(term) is { } url)
            {
                return url;
            }
        }
        return null; // dan volstaat de productfoto (of DuckDuckGo)
    }

    /// <summary>
    /// Zoektermen van specifiek naar grof: de volledige naam, dan zonder de bijgerechten
    /// ("Fishsticks met wortelpuree" → "Fishsticks"), telkens met wat vernederlandsing
    /// ("fishsticks" staat op Allerhande als "vissticks").
    /// </summary>
    private static IEnumerable<string> ZoekTermen(string naam)
    {
        static string Vertaal(string t) => t
            .Replace("fishsticks", "vissticks", StringComparison.OrdinalIgnoreCase)
            .Replace("fish", "vis", StringComparison.OrdinalIgnoreCase);
        yield return naam;
        var vertaald = Vertaal(naam);
        if (!vertaald.Equals(naam, StringComparison.Ordinal))
        {
            yield return vertaald;
        }
        var met = naam.IndexOf(" met ", StringComparison.OrdinalIgnoreCase);
        if (met > 0)
        {
            yield return Vertaal(naam[..met]);
        }
    }

    private static async Task<string?> ZoekOpAllerhandeAsync(string term)
    {
        try
        {
            using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                "https://www.ah.nl/allerhande/recepten-zoeken?query=" + Uri.EscapeDataString(term));
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            var html = await res.Content.ReadAsStringAsync();
            var m = System.Text.RegularExpressions.Regex.Match(html,
                @"https://static\.ah\.nl/static/recepten/img_[A-Za-z0-9_]+_612x450_JPG\.jpg");
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Laatste redmiddel: DuckDuckGo-beeldzoek op "&lt;naam&gt; recept". Twee stappen — de
    /// html-pagina levert een vqd-token, i.js daarmee de resultaten. Niet officieel, dus
    /// ruimhartig in try/catch: mislukt het, dan blijft de kaart gewoon zonder foto.
    /// </summary>
    private static async Task<string?> ZoekOpDuckDuckGoAsync(string naam)
    {
        try
        {
            var query = Uri.EscapeDataString(naam + " recept");
            using var req1 = new HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                $"https://duckduckgo.com/?q={query}&iax=images&ia=images");
            req1.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var res1 = await Http.SendAsync(req1);
            var vqd = System.Text.RegularExpressions.Regex
                .Match(await res1.Content.ReadAsStringAsync(), "vqd=\"([^\"]+)\"").Groups[1].Value;
            if (vqd.Length == 0)
            {
                return null;
            }
            using var req2 = new HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                $"https://duckduckgo.com/i.js?l=nl-nl&o=json&q={query}&vqd={vqd}&p=1");
            req2.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            req2.Headers.Referrer = new Uri("https://duckduckgo.com/");
            using var res2 = await Http.SendAsync(req2);
            using var doc = System.Text.Json.JsonDocument.Parse(await res2.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("results", out var lijst) &&
                lijst.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var r in lijst.EnumerateArray())
                {
                    if (r.TryGetProperty("image", out var beeld) &&
                        beeld.GetString() is { Length: > 0 } url &&
                        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return url;
                    }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap Verklein(Image bron, int maat)
    {
        var doel = new Bitmap(maat, maat, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(doel);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Zachte hoeken: de foto's zijn groot genoeg om als "kaartjes" te ogen in de lijst.
        using (var hoeken = Theme.RoundedPath(new Rectangle(0, 0, maat, maat), maat / 8))
        {
            g.SetClip(hoeken);
        }
        var schaal = Math.Min((float)maat / bron.Width, (float)maat / bron.Height);
        var w = bron.Width * schaal;
        var h = bron.Height * schaal;
        g.DrawImage(bron, (maat - w) / 2f, (maat - h) / 2f, w, h);
        return doel;
    }
}
