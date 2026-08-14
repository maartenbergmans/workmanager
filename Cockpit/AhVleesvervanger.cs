using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Eén huisgenoot is pescotariër: vis en schaaldieren zijn prima, vlees niet. Vleesgerechten
/// blijven gewoon op het menu, maar bij het bestellen komt er automatisch een passende
/// plantaardige vervanger voor het vlees­bestanddeel bij de boodschappen — zelfde gerecht,
/// één bord anders. Ingrediënten die al plantaardig zijn (of vis) krijgen geen vervanger.
/// </summary>
public static class AhVleesvervanger
{
    private sealed record Vervanger(Regex Patroon, string Naam, string Url);

    /// <summary>Van specifiek naar algemeen; de eerste treffer per gerecht wint.</summary>
    private static readonly Vervanger[] Vervangers =
    {
        new(new Regex(@"soepballetjes", RegexOptions.IgnoreCase),
            "AH Terra Plantaardige soepballetjes",
            "https://www.ah.be/producten/product/wi567251/ah-terra-plantaardige-soepballetjes"),
        new(new Regex(@"gehakt", RegexOptions.IgnoreCase),
            "AH Terra Plantaardige gehakt gebraden",
            "https://www.ah.be/producten/product/wi489282/ah-terra-plantaardige-gehakt-gebraden"),
        new(new Regex(@"spek", RegexOptions.IgnoreCase),
            "Vivera Plantaardige spekjes",
            "https://www.ah.be/producten/product/wi467423/vivera-plantaardige-spekjes"),
        new(new Regex(@"worst|chipolata", RegexOptions.IgnoreCase),
            "AH Terra Plantaardige kipbraadworst",
            "https://www.ah.be/producten/product/wi564866/ah-terra-plantaardige-kipbraadworst"),
        new(new Regex(@"kip", RegexOptions.IgnoreCase),
            "AH Terra Plantaardige kipstukjes",
            "https://www.ah.be/producten/product/wi580544/ah-terra-plantaardige-kipstukjes"),
        new(new Regex(@"\bham\b|salami|bbq", RegexOptions.IgnoreCase),
            "AH Terra Plantaardige kipstukjes",
            "https://www.ah.be/producten/product/wi580544/ah-terra-plantaardige-kipstukjes"),
    };

    /// <summary>
    /// De plantaardige vervanger voor het vlees in dit gerecht, of null als er niets te
    /// vervangen valt (geen vlees, of het gerecht is al plantaardig).
    /// </summary>
    public static (string Naam, string Url)? Voor(IEnumerable<AhIngredient> ingredienten)
    {
        foreach (var ing in ingredienten)
        {
            if (ing.Naam.Contains("plantaardig", StringComparison.OrdinalIgnoreCase) ||
                ing.Naam.Contains("terra", StringComparison.OrdinalIgnoreCase))
            {
                continue; // dit bestanddeel is al goed voor de pescotariër
            }
            foreach (var vervanger in Vervangers)
            {
                if (vervanger.Patroon.IsMatch(ing.Naam))
                {
                    return (vervanger.Naam, vervanger.Url);
                }
            }
        }
        return null;
    }
}
