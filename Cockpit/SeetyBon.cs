using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// De Seety-bon na elke parkeersessie ("Je Seety-bon" van laura@seety.co) automatisch
/// archiveren als er niets betaald is ("Totaal betaald 0,00 €") — gratis sessies zijn
/// pure routine. Bonnen mét een echt bedrag blijven in de cockpit staan.
/// </summary>
public static class SeetyBon
{
    public static bool IsGratisBon(MailBericht m) =>
        m.VanAdres.Contains("seety.co", StringComparison.OrdinalIgnoreCase) &&
        m.Onderwerp.Contains("Seety", StringComparison.OrdinalIgnoreCase) &&
        // In de HTML zit opmaak tussen het label en het bedrag; de lookbehind voorkomt
        // dat "10,00 €" op zijn laatste "0,00" matcht.
        Regex.IsMatch(m.Tekst.Length > 0 ? m.Tekst : m.Html,
            @"Totaal\s+betaald[\s\S]{0,300}?(?<!\d)0[.,]00\s*€", RegexOptions.IgnoreCase);
}
