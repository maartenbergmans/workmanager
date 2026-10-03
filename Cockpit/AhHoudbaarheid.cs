namespace WorkManager;

/// <summary>
/// Hoe lang een gerecht kan wachten na de levering. Verse vis moet de dag zelf op, gehakt en
/// gevogelte binnen twee dagen, een pasta met blokjes tomaat kan een week later nog. Daarom
/// stelt de agendastap de gerechten in die volgorde voor: het bederfelijkste vooraan, zodat
/// er niets in de koelkast blijft liggen tot het niet meer kan.
///
/// <para>De schatting komt uit de productnamen van de ingrediënten — dat is alles wat de app
/// over een gerecht weet. Eén onbekend ingrediënt maakt niets kapot: dat telt gewoon als lang
/// houdbaar en het laagste getal van het gerecht beslist.</para>
/// </summary>
public static class AhHoudbaarheid
{
    /// <summary>Lang houdbaar: droog, blik, diepvries of gewoon onbekend.</summary>
    public const int LangHoudbaar = 9;

    /// <summary>
    /// Trefwoord → dagen dat het na levering nog goed is. De volgorde telt: het eerste
    /// trefwoord dat in de productnaam voorkomt beslist, dus specifieke termen staan boven
    /// algemene ("diepvriesvis" vóór "vis", "kipfilet" vóór "kip").
    /// </summary>
    private static readonly (string Woord, int Dagen)[] Tabel =
    {
        // Diepvries en conserven eerst: die moeten niet struikelen over "vis" of "erwt".
        ("diepvries", LangHoudbaar),
        ("blik", LangHoudbaar),
        ("bokaal", LangHoudbaar),
        ("gedroogd", LangHoudbaar),
        ("gedroogde", LangHoudbaar),

        // Dag 1 — verse vis en zeevruchten, en wat rauw gegeten wordt.
        ("verse vis", 1),
        ("zalm", 1),
        ("kabeljauw", 1),
        ("pangasius", 1),
        ("tilapia", 1),
        ("tonijnsteak", 1),
        ("victoriabaars", 1),
        ("zeewolf", 1),
        ("forel", 1),
        ("garnaal", 1),
        ("garnalen", 1),
        ("scampi", 1),
        ("mossel", 1),
        ("gamba", 1),
        ("vispannetje", 1),
        ("sushi", 1),
        ("steak tartaar", 1),
        ("filet americain", 1),

        // Dag 2 — gehakt en gevogelte: de klassieke "binnen twee dagen".
        ("gehakt", 2),
        ("kipfilet", 2),
        ("kippenbout", 2),
        ("kippenvleugel", 2),
        ("kipworst", 2),
        ("kip", 2),
        ("kalkoen", 2),
        ("lever", 2),
        ("worst", 2),
        ("chipolata", 2),
        ("merguez", 2),
        ("vol-au-vent", 2),

        // Dag 3 — ander vers vlees en verse zuivelbereidingen.
        ("varken", 3),
        ("spiering", 3),
        ("kotelet", 3),
        ("rund", 3),
        ("biefstuk", 3),
        ("steak", 3),
        ("entrecote", 3),
        ("lam", 3),
        ("spek", 3),
        ("verse pasta", 3),
        ("verse soep", 3),
        ("room", 3),
        ("mascarpone", 3),
        ("ricotta", 3),

        // Dag 4-5 — bladgroenten en vruchten die snel slap worden.
        ("spinazie", 4),
        ("rucola", 4),
        ("veldsalade", 4),
        ("sla", 4),
        ("champignon", 4),
        ("asperge", 4),
        ("broccoli", 5),
        ("bloemkool", 5),
        ("courgette", 5),
        ("paprika", 5),
        ("tomaat", 5),
        ("komkommer", 5),
        ("aubergine", 5),
        ("prei", 5),
        ("witloof", 5),
        ("boontjes", 5),
        ("tofu", 5),
        ("quorn", 5),
        ("vegetarische", 5),
        ("vleesvervanger", 5),

        // Dag 7 — stevige groenten en kaas: die halen de week wel.
        ("wortel", 7),
        ("kool", 7),
        ("pompoen", 7),
        ("aardappel", 7),
        ("ui", 7),
        ("kaas", 7),
        ("mozzarella", 7),
        ("feta", 7),
        ("ei", 7),
    };

    /// <summary>Hoeveel dagen dit product na levering nog goed is (schatting).</summary>
    public static int VoorProduct(string naam)
    {
        if (string.IsNullOrWhiteSpace(naam))
        {
            return LangHoudbaar;
        }
        var klein = naam.ToLowerInvariant();
        foreach (var (woord, dagen) in Tabel)
        {
            if (klein.Contains(woord, StringComparison.Ordinal))
            {
                return dagen;
            }
        }
        return LangHoudbaar;
    }

    /// <summary>
    /// Hoe lang het hele gerecht kan wachten: het bederfelijkste ingrediënt beslist. Een
    /// gerecht zonder bekende ingrediënten geldt als lang houdbaar.
    /// </summary>
    public static int VoorGerecht(IEnumerable<string> ingredienten)
    {
        var minste = LangHoudbaar;
        foreach (var naam in ingredienten)
        {
            minste = Math.Min(minste, VoorProduct(naam));
        }
        return minste;
    }

    /// <summary>
    /// Korte uitleg bij de voorgestelde dag, voor achter de gerechtnaam in de agendastap.
    /// Leeg voor gerechten die gewoon kunnen wachten — dan hoeft er niets bij te staan.
    /// </summary>
    public static string Hint(int dagen) => dagen switch
    {
        <= 1 => "🐟 vers — eerste dag",
        2 => "🍗 binnen 2 dagen",
        3 => "🥩 binnen 3 dagen",
        <= 5 => "🥬 binnen 5 dagen",
        < LangHoudbaar => "binnen een week",
        _ => "",
    };
}
