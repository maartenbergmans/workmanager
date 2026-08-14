using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén product uit de AH-producttabel (opgebouwd uit de eerdere bestellingen).</summary>
public sealed class AhProduct
{
    public string Naam { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>Namen waaronder een ingrediënt dit product kan bedoelen ("wortel", "peen", …).</summary>
    public List<string> Trefwoorden { get; set; } = new();
}

/// <summary>Hoe zeker een ingrediënt aan een product gekoppeld kon worden.</summary>
public enum AhMatch
{
    Geen,

    /// <summary>Gevonden via een deelwoord — plausibel, maar de mens kijkt best na.</summary>
    Gok,

    /// <summary>Trefwoord komt (op enkelvoud/meervoud na) letterlijk overeen.</summary>
    Zeker,
}

/// <summary>
/// De lokale producttabel: naam + ah.be-url van alles wat Maarten eerder bestelde
/// (%APPDATA%\WorkManager\ah-producten.json — ontbreekt die, dan wordt de meegeleverde
/// versie daar neergezet zodat je hem kunt bijwerken). Wordt gebruikt om ingrediënten van
/// een gerecht automatisch aan een echt product te koppelen.
/// </summary>
public static class AhProducten
{
    private static readonly string CatalogusFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-producten.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Bestand
    {
        public List<AhProduct> Producten { get; set; } = new();
    }

    private static List<AhProduct>? _cache;

    public static IReadOnlyList<AhProduct> Alles => _cache ??= Laad();

    /// <summary>Vergeet de gecachete tabel (na bewerken van het bestand).</summary>
    public static void Herlaad() => _cache = null;

    private static List<AhProduct> Laad()
    {
        try
        {
            if (File.Exists(CatalogusFile))
            {
                return Lees(File.ReadAllText(CatalogusFile));
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op de meegeleverde tabel.
        }
        try
        {
            using var stroom = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("WorkManager.Assets.ah-producten.json");
            if (stroom is null)
            {
                return new List<AhProduct>();
            }
            var json = new StreamReader(stroom).ReadToEnd();
            try
            {
                // Meteen naast de andere gegevens zetten: daar kan Maarten hem aanvullen.
                Directory.CreateDirectory(Path.GetDirectoryName(CatalogusFile)!);
                File.WriteAllText(CatalogusFile, json);
            }
            catch
            {
                // Alleen een gemak; de tabel werkt ook zonder.
            }
            return Lees(json);
        }
        catch
        {
            return new List<AhProduct>();
        }
    }

    private static List<AhProduct> Lees(string json) =>
        JsonSerializer.Deserialize<Bestand>(json, JsonOpts)?.Producten
            .Where(p => !string.IsNullOrWhiteSpace(p.Url))
            .ToList()
        ?? new List<AhProduct>();

    /// <summary>
    /// Zoekt het product dat bij een ingrediëntnaam hoort. Een ingrediënt mag alternatieven
    /// bevatten ("passata of tomatenblokjes") en een toelichting tussen haakjes
    /// ("aardappelen (kruimig)"); beide worden meegenomen bij het zoeken.
    /// </summary>
    public static (AhProduct? Product, AhMatch Zekerheid) Zoek(string ingredient)
    {
        AhProduct? gok = null;
        foreach (var variant in Varianten(ingredient))
        {
            foreach (var product in Alles)
            {
                foreach (var trefwoord in product.Trefwoorden.Append(product.Naam))
                {
                    var t = Normaliseer(trefwoord);
                    if (t.Length == 0)
                    {
                        continue;
                    }
                    if (Stam(t) == Stam(variant))
                    {
                        return (product, AhMatch.Zeker);
                    }
                    // Deelwoord-treffer: "tomatenblokjes" bij trefwoord "tomaten". Kort
                    // afkappen zou "ui" in "uitjes" laten passen, vandaar de ondergrens.
                    if (gok is null && variant.Length >= 5 && t.Length >= 5 &&
                        (t.Contains(variant, StringComparison.Ordinal) ||
                         variant.Contains(t, StringComparison.Ordinal)))
                    {
                        gok = product;
                    }
                }
            }
        }
        return gok is null ? (null, AhMatch.Geen) : (gok, AhMatch.Gok);
    }

    /// <summary>Splitst "penne of spaghetti (vers)" in de zoekbare delen.</summary>
    private static IEnumerable<string> Varianten(string ingredient)
    {
        var kaal = Normaliseer(ingredient);
        // Toelichting tussen haakjes weglaten, maar wel als extra variant proberen.
        var haakje = kaal.IndexOf('(');
        if (haakje > 0)
        {
            kaal = kaal[..haakje].Trim();
        }
        yield return kaal;
        foreach (var deel in kaal.Split(new[] { " of ", " en ", "/", ",", " met " },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (deel != kaal && deel.Length > 2)
            {
                yield return deel;
            }
        }
    }

    /// <summary>Kleine letters zonder accenten, dubbele spaties weg.</summary>
    private static string Normaliseer(string tekst)
    {
        var ontleed = tekst.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(ontleed.Length);
        foreach (var teken in ontleed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(teken) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(teken);
            }
        }
        return string.Join(' ', sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Grove enkelvoudsvorm zodat "wortel" en "wortelen" hetzelfde opleveren. Geen echte
    /// stemmer — genoeg om meervouden van boodschappen te vangen zonder rare treffers.
    /// </summary>
    private static string Stam(string woord)
    {
        foreach (var uitgang in new[] { "'s", "en", "s" })
        {
            if (woord.Length > uitgang.Length + 3 && woord.EndsWith(uitgang, StringComparison.Ordinal))
            {
                return woord[..^uitgang.Length];
            }
        }
        return woord;
    }
}
