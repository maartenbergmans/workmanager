using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Logo's van klanten voor in menu's en knoppen. De afbeelding komt uit
/// %APPDATA%\WorkManager\klantlogos\&lt;naam&gt;.png; ontbreekt die, dan haalt hij één keer op de
/// achtergrond het favicon van de klantwebsite op en bewaart dat. Zolang er niets is (of de
/// site niets bruikbaars geeft) toont hij een getekende initiaal in de klantkleur — er staat
/// dus altijd iets, en het menu wacht nooit op het internet.
/// </summary>
public static class KlantLogo
{
    // Bewust breder dan hoog: de meeste bedrijfslogo's zijn woordmerken. In een
    // vierkant vakje geperst worden die onleesbaar; met 28×18 en behoud van de
    // verhouding blijven ze herkenbaar.
    private const int Breedte = 28;
    private const int Hoogte = 18;

    private static readonly string LogoDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "klantlogos");

    // Met een gewone browser-user-agent: sommige sites weigeren kale requests.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkManager/1.0" },
        },
    };

    private static readonly Dictionary<string, Image> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Geprobeerd = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>De websites waar we het logo van mogen halen, per klantnaam uit de launcher.</summary>
    public static readonly Dictionary<string, string> Websites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aqurat"] = "aqurat.be",
        ["RadiologyP."] = "bloom-caregroup.com",
        ["RadiologyPartners"] = "bloom-caregroup.com",
        ["Vriesveemlog."] = "vriesveemlogistics.nl",
        ["Vriesveem"] = "vriesveem.nl",
        ["Nemijtek"] = "nemijtek.nl",
        ["Lauryssens"] = "lauryssens.be",
        ["CED"] = "ced.be",
        ["UrbanIT"] = "urbanit.be",
        ["WorkManager"] = "urbanit.be",
    };

    /// <summary>
    /// Het logo van deze klant, klaar om als menu-icoon te gebruiken. Geeft altijd een
    /// afbeelding terug: het echte logo als dat er is, anders een initiaal in de klantkleur.
    /// </summary>
    public static Image Voor(string klant)
    {
        var sleutel = Sleutel(klant);
        lock (Cache)
        {
            if (Cache.TryGetValue(sleutel, out var bestaand))
            {
                return bestaand;
            }
        }

        var pad = Path.Combine(LogoDir, sleutel + ".png");
        Image? logo = null;
        try
        {
            if (File.Exists(pad))
            {
                // Via een kopie inlezen: anders houdt GDI+ het bestand open.
                using var bron = Image.FromFile(pad);
                logo = Schaal(bron);
            }
        }
        catch
        {
            // Onleesbaar bestand: dan de initiaal.
        }

        logo ??= Initiaal(klant);
        lock (Cache)
        {
            Cache[sleutel] = logo;
        }
        if (!File.Exists(pad))
        {
            _ = HaalOpAsync(klant, sleutel, pad); // eenmalig, op de achtergrond
        }
        return logo;
    }

    /// <summary>Vergeet de cache, zodat pas opgehaalde logo's en een nieuw thema doorwerken.</summary>
    public static void Vergeet()
    {
        lock (Cache)
        {
            foreach (var afbeelding in Cache.Values)
            {
                afbeelding.Dispose();
            }
            Cache.Clear();
        }
    }

    private static string Sleutel(string klant) =>
        new string(klant.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Haalt eenmalig het favicon van de klantwebsite op (eerst /favicon.ico, dan de
    /// apple-touch-icon) en bewaart het. Mislukt dat, dan blijft de initiaal staan.
    /// </summary>
    private static async Task HaalOpAsync(string klant, string sleutel, string pad)
    {
        lock (Geprobeerd)
        {
            if (!Geprobeerd.Add(sleutel) || !Websites.TryGetValue(klant, out _))
            {
                return;
            }
        }
        var domein = Websites[klant];
        // Eerst de homepage lezen: de meeste sites zetten hun icoon níét op /favicon.ico maar
        // verwijzen ernaar met <link rel="icon"> of een og:image. Die kandidaten eerst
        // proberen, daarna pas de standaardplekken.
        var kandidaten = new List<string>();
        foreach (var basis in new[] { $"https://{domein}/", $"https://www.{domein}/" })
        {
            try
            {
                var html = await Http.GetStringAsync(basis);
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(html,
                             """<link[^>]+rel=["'][^"']*icon[^"']*["'][^>]*>""",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var href = System.Text.RegularExpressions.Regex.Match(m.Value,
                        """href=["']([^"']+)["']""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                    if (href.Length > 0 && !href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(new Uri(basis), href, out var absoluut))
                    {
                        kandidaten.Add(absoluut.ToString());
                    }
                }
                var og = System.Text.RegularExpressions.Regex.Match(html,
                    """<meta[^>]+property=["']og:image["'][^>]+content=["']([^"']+)["']""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                if (og.Length > 0 && Uri.TryCreate(new Uri(basis), og, out var ogUrl))
                {
                    kandidaten.Add(ogUrl.ToString());
                }
                // Veel (oudere) sites hebben helemaal geen icon-link, maar wel een
                // <img src="…logo….png"> in de kop. Dat is meestal hét bedrijfslogo.
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(html,
                             """<img[^>]+src=["']([^"']*logo[^"']*)["']""",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var src = m.Groups[1].Value;
                    // "../images/x.png" op de homepage betekent gewoon "/images/x.png".
                    var opgeschoond = src.Replace("../", "/", StringComparison.Ordinal);
                    if (!opgeschoond.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(new Uri(basis), opgeschoond, out var imgUrl))
                    {
                        kandidaten.Add(imgUrl.ToString());
                    }
                }
                break; // homepage gelezen; de andere variant hoeft niet meer
            }
            catch
            {
                // Volgende variant (met of zonder www) proberen.
            }
        }
        kandidaten.AddRange(new[]
        {
            $"https://{domein}/apple-touch-icon.png",
            $"https://www.{domein}/apple-touch-icon.png",
            $"https://{domein}/favicon.ico",
            $"https://www.{domein}/favicon.ico",
        });

        foreach (var url in kandidaten)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length < 100)
                {
                    continue;
                }
                using var stroom = new MemoryStream(bytes);
                using var bron = Image.FromStream(stroom);
                using var klein = Schaal(bron);
                Directory.CreateDirectory(LogoDir);
                klein.Save(pad, System.Drawing.Imaging.ImageFormat.Png);
                lock (Cache)
                {
                    Cache.Remove(sleutel); // volgende keer het echte logo
                }
                return;
            }
            catch
            {
                // Volgende url proberen; lukt niets, dan blijft de initiaal staan.
            }
        }
    }

    /// <summary>Schaalt met behoud van de verhouding en centreert in een 28×18-vlak.</summary>
    private static Bitmap Schaal(Image bron)
    {
        var doel = new Bitmap(Breedte, Hoogte);
        using var g = Graphics.FromImage(doel);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var schaal = Math.Min(Breedte / (float)bron.Width, Hoogte / (float)bron.Height);
        var breed = Math.Max(1, (int)(bron.Width * schaal));
        var hoog = Math.Max(1, (int)(bron.Height * schaal));
        g.DrawImage(bron, new Rectangle((Breedte - breed) / 2, (Hoogte - hoog) / 2, breed, hoog));
        return doel;
    }

    /// <summary>Rondje met de eerste letter, in de kleur die bij deze klant hoort.</summary>
    private static Bitmap Initiaal(string klant)
    {
        var kleur = Theme.VoorKlant(klant.Replace(" ▾", "").Replace(".", "").Trim());
        var letter = klant.TrimStart().FirstOrDefault(char.IsLetter);
        var doel = new Bitmap(Breedte, Hoogte);
        using var g = Graphics.FromImage(doel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rond = new Rectangle((Breedte - Hoogte) / 2, 0, Hoogte - 1, Hoogte - 1);
        using (var vlak = new SolidBrush(Color.FromArgb(60, kleur)))
        {
            g.FillEllipse(vlak, rond);
        }
        using (var pen = new Pen(kleur, 1.4f))
        {
            g.DrawEllipse(pen, rond.X + 0.7f, 0.7f, rond.Width - 1.4f, rond.Height - 1.4f);
        }
        TextRenderer.DrawText(g, char.ToUpperInvariant(letter).ToString(), Theme.CaptionFont,
            new Rectangle(0, 0, Breedte, Hoogte), kleur,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return doel;
    }
}
