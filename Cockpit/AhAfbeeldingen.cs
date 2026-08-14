using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Haalt een kleine productfoto op bij een ah.be-productlink en cachet die lokaal, zodat de
/// boodschappenlijst naast elk ingrediënt het echte AH-product laat zien. Werkt volledig via de
/// publieke AH-mobiele-API (anonieme token → productdetail → afbeelding op static.ah.nl); geen
/// browser en geen login nodig — elk product is te vinden op zijn webshop-id (de "wi4076" uit de
/// productlink). Downloaden gebeurt op de achtergrond met beperkte gelijktijdigheid; zodra er een
/// nieuwe foto in de cache staat vuurt <see cref="BeeldKlaar"/>, waarop de lijst zich hertekent.
/// </summary>
public static class AhAfbeeldingen
{
    /// <summary>Zijde (px) van de bewaarde thumbnail — ruim genoeg voor de lijst, ook op HiDPI.</summary>
    private const int Formaat = 80;

    private static readonly string CacheMap = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-afbeeldingen");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // id → thumbnail (blijft in geheugen; niet disposen zolang de cache leeft).
    private static readonly ConcurrentDictionary<string, Image> _cache = new();
    // id waarvoor al een download loopt of definitief mislukte, zodat we niet blijven proberen.
    private static readonly ConcurrentDictionary<string, byte> _bezig = new();
    private static readonly SemaphoreSlim _poort = new(4);

    private static readonly Regex IdRegex =
        new(@"/product/wi(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Vuurt (op een achtergrondthread) zodra er een nieuwe foto in de cache staat.</summary>
    public static event Action? BeeldKlaar;

    static AhAfbeeldingen()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Appie/8.22.3");
    }

    /// <summary>Webshop-id uit een productlink ("…/product/wi4076/…" → "4076"), of null.</summary>
    private static string? IdVan(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        var m = IdRegex.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// De gecachete foto voor een productlink, of null als hij er (nog) niet is. Bij null wordt
    /// eenmaal een achtergrond-download gestart; slaagt die, dan volgt <see cref="BeeldKlaar"/>.
    /// </summary>
    public static Image? Voor(string? productUrl)
    {
        var id = IdVan(productUrl);
        if (id is null)
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
            _ = Task.Run(() => Ophalen(id));
        }
        return null;
    }

    /// <summary>Warmt de cache alvast op voor een reeks productlinks (bij het openen van de lijst).</summary>
    public static void Voorladen(IEnumerable<string?> productUrls)
    {
        foreach (var url in productUrls)
        {
            Voor(url);
        }
    }

    /// <summary>Cachebestand; het formaat zit in de naam zodat een grotere gewenste maat niet
    /// tegen oude, kleinere thumbnails aanloopt.</summary>
    private static string CachePad(string id) => Path.Combine(CacheMap, $"{id}@{Formaat}.png");

    private static Image? LaadVanSchijf(string id)
    {
        var pad = CachePad(id);
        try
        {
            if (!File.Exists(pad))
            {
                return null;
            }
            // Via bytes inlezen: Image.FromFile houdt het bestand vergrendeld.
            using var ms = new MemoryStream(File.ReadAllBytes(pad));
            using var tijdelijk = new Bitmap(ms);
            return new Bitmap(tijdelijk);
        }
        catch
        {
            return null; // stuk of onleesbaar: opnieuw ophalen
        }
    }

    private static async Task Ophalen(string id)
    {
        try
        {
            await _poort.WaitAsync();
            try
            {
                var info = await AhApi.DetailAsync(id);
                if (info?.BeeldUrl is not { } url)
                {
                    return;
                }
                var bytes = await Http.GetByteArrayAsync(url);
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
            // Mislukt: id blijft in _bezig staan zodat we deze sessie niet blijven hameren.
        }
    }

    /// <summary>Schaalt een bron passend in een vierkant van <paramref name="maat"/> px (verhouding behouden).</summary>
    private static Bitmap Verklein(Image bron, int maat)
    {
        var doel = new Bitmap(maat, maat, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(doel);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var schaal = Math.Min((float)maat / bron.Width, (float)maat / bron.Height);
        var w = bron.Width * schaal;
        var h = bron.Height * schaal;
        g.DrawImage(bron, (maat - w) / 2f, (maat - h) / 2f, w, h);
        return doel;
    }
}
