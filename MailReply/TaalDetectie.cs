namespace WorkManager;

/// <summary>
/// Eenvoudige taalherkenning (Nederlands/Frans/Engels) op basis van veelvoorkomende
/// stopwoorden. Bewust licht en zonder externe afhankelijkheden: genoeg om een taalvlag bij
/// een mail te tonen en Nederlandse mails (geen vlag) van Franse/Engelse te onderscheiden.
/// </summary>
public static class TaalDetectie
{
    public enum Taal { Onbekend, Nederlands, Frans, Engels }

    private static readonly string[] Nl =
        { "de", "het", "een", "en", "van", "ik", "je", "niet", "dat", "met", "voor", "op", "gaan", "wij", "graag", "beste", "groeten" };
    private static readonly string[] Fr =
        { "le", "la", "les", "un", "une", "et", "de", "je", "vous", "nous", "pour", "avec", "pas", "bonjour", "merci", "cordialement", "être" };
    private static readonly string[] En =
        { "the", "a", "an", "and", "of", "you", "we", "for", "with", "not", "this", "please", "thanks", "regards", "hello", "best" };

    /// <summary>De vermoedelijke taal van een stuk tekst.</summary>
    public static Taal Detecteer(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst))
        {
            return Taal.Onbekend;
        }
        var woorden = tekst.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries);
        if (woorden.Length < 3)
        {
            return Taal.Onbekend;
        }
        var set = new HashSet<string>(woorden);
        int nl = Nl.Count(set.Contains), fr = Fr.Count(set.Contains), en = En.Count(set.Contains);
        var max = Math.Max(nl, Math.Max(fr, en));
        if (max < 2)
        {
            return Taal.Onbekend;
        }
        if (max == fr && fr > nl)
        {
            return Taal.Frans;
        }
        if (max == en && en > nl)
        {
            return Taal.Engels;
        }
        return max == nl ? Taal.Nederlands : Taal.Onbekend;
    }

    /// <summary>Een vlagje voor de taal (leeg voor Nederlands/onbekend, zodat alleen FR/EN opvallen).</summary>
    public static string Vlag(string tekst) => Detecteer(tekst) switch
    {
        Taal.Frans => "🇫🇷",
        Taal.Engels => "🇬🇧",
        _ => "",
    };
}
