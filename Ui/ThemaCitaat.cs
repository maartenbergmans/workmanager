namespace WorkManager;

/// <summary>
/// Het citaat van de dag, in de stem van het gekozen thema. Eén korte regel — een knipoog
/// over werk, focus of beslissen — die de hele dag hetzelfde blijft: de keuze hangt aan de
/// datum, niet aan het toeval. Zo verrast hij 's ochtends één keer en wordt hij daarna
/// gewoon behang, in plaats van bij elke verversing te veranderen.
///
/// <para>Bewust kort en zonder emoji: de rest van de app heeft die al (zie
/// <see cref="ThemaStem"/>). Hier ligt de nadruk op de toon, niet op de versiering.</para>
/// </summary>
public static class ThemaCitaat
{
    private static readonly string[] Zeven007 =
    {
        "Een missie zonder briefing is een wandeling met risico's.",
        "Q levert het gereedschap. Het doelwit kies je zelf.",
        "Elegantie is precisie die je niet ziet werken.",
        "Wie alles tegelijk bewaakt, bewaakt niets.",
        "De beste dekmantel is een agenda die klopt.",
        "Rustig blijven is sneller dan snel zijn.",
        "M vraagt geen uren. M vraagt resultaat.",
    };

    private static readonly string[] Godfather =
    {
        "Zaken zijn zaken; de rest is timing.",
        "Een belofte is een schuld. Noteer ze dus.",
        "Hou je vrienden dichtbij en je deadlines dichterbij.",
        "Wie te snel ja zegt, betaalt later de rente.",
        "Respect verdien je met wat je aflevert, niet met wat je aankondigt.",
        "Een familie draait op afspraken die nagekomen worden.",
        "Nooit boos beslissen. Nooit hongerig onderhandelen.",
    };

    private static readonly string[] Zomer =
    {
        "Werk als een golf: gestaag, niet gehaast.",
        "Schaduw zoeken is ook een strategie.",
        "Een korte pauze kost minder dan een lange fout.",
        "Niet alles hoeft vandaag. Wel iets.",
        "Eb en vloed: je agenda mag ademen.",
        "Wie op tijd stopt, begint morgen sneller.",
        "Zand in je schoenen, orde in je hoofd.",
    };

    private static readonly string[] Neon =
    {
        "Focus is het enige licht dat je nodig hebt.",
        "Ruis genoeg. Kies één signaal.",
        "De stad slaapt niet, jij wel — plan ernaar.",
        "Snel bewegen is makkelijk; de juiste richting niet.",
        "Elke taak is een schakeling: aan of uit, niet halfweg.",
        "Wat niet in het systeem staat, bestaat morgen niet.",
        "Deadlines zijn de enige zwaartekracht hier.",
    };

    private static readonly string[] Espresso =
    {
        "Eén ding tegelijk, maar dan goed gezet.",
        "Goede koffie en goed werk delen hetzelfde geheim: druk en tijd.",
        "Kort en sterk verslaat lang en slap.",
        "Begin met de moeilijkste slok.",
        "Rust in je hoofd smaakt naar een pauze die je écht nam.",
        "Wie niet maalt, krijgt geen espresso.",
        "Kleine kop, volle dag.",
    };

    private static readonly string[] Zakelijk =
    {
        "Wat je vandaag afwerkt, hoef je morgen niet te bewaken.",
        "Een taak zonder deadline is een wens.",
        "Beslissen is ook werk — vaak het duurste.",
        "Overzicht is goedkoper dan inhalen.",
        "Half af is nul waarde voor de klant.",
        "Wie zijn uren kent, kent zijn marge.",
        "Nee zeggen is een planning maken.",
        "Twee uur voorbereiden scheelt een dag herstellen.",
    };

    /// <summary>Het citaat voor vandaag, of voor een gekozen dag (voor de dagafsluiter).</summary>
    public static string VanDeDag(DateTime? dag = null)
    {
        var regels = Theme.Palet.Naam switch
        {
            "007" => Zeven007,
            "Godfather" => Godfather,
            "Zomer" => Zomer,
            "Neon" => Neon,
            "Espresso" => Espresso,
            _ => Zakelijk,
        };
        // Datum + themanaam als index: op dezelfde dag geeft elk thema een ánder citaat,
        // zodat wisselen van thema ook echt iets oplevert.
        var datum = (dag ?? DateTime.Today).DayOfYear;
        var index = Math.Abs(datum + Theme.Palet.Naam.GetHashCode(StringComparison.Ordinal) / 64) % regels.Length;
        return regels[index];
    }

    /// <summary>Het citaat met aanhalingstekens, klaar om als één regel te tonen.</summary>
    public static string Aangehaald(DateTime? dag = null) => "„" + VanDeDag(dag) + "”";
}
