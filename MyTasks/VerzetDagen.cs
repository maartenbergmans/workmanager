using System.Globalization;

namespace WorkManager;

/// <summary>
/// De slimme verzet-voorstellen in het rechterklikmenu van een taak. "Morgen" en "overmorgen"
/// kloppen in twee gevallen zelden:
/// <list type="bullet">
///   <item>op donderdag en vrijdag vallen ze in het weekend — dan hoort de maandag van de week
///   nadien erbij;</item>
///   <item>een CED-taak pakt Maarten op zijn CED-dagen op — die krijgt altijd de eerstvolgende
///   dinsdag en donderdag als keuze.</item>
/// </list>
/// Eén bron voor zowel de cockpit als het takenvenster, zodat beide menu's dezelfde dagen tonen.
/// </summary>
public static class VerzetDagen
{
    /// <summary>De taakcategorie waarvoor de CED-dagen verschijnen (zie MijnTaakStore).</summary>
    public const string CedCategorie = "CED";

    /// <summary>
    /// De eerstvolgende <paramref name="dag"/> ná <paramref name="vanaf"/>. Valt <paramref name="vanaf"/>
    /// zelf op die weekdag, dan is het die dag een week later — "donderdag nadien" op een donderdag
    /// betekent immers volgende week, niet vandaag.
    /// </summary>
    public static DateOnly Volgende(DayOfWeek dag, DateOnly vanaf)
    {
        var stappen = ((int)dag - (int)vanaf.DayOfWeek + 7) % 7;
        return vanaf.AddDays(stappen == 0 ? 7 : stappen);
    }

    /// <summary>
    /// De extra verzet-keuzes voor een taak in <paramref name="categorie"/>, in de volgorde waarin
    /// ze onder "Verzet naar overmorgen" horen. Een lege lijst = niets extra tonen.
    /// </summary>
    public static IReadOnlyList<(string Label, DateOnly Doel)> Voorstellen(
        DateOnly vandaag, string? categorie)
    {
        var lijst = new List<(string Label, DateOnly Doel)>();

        // Donderdag/vrijdag: morgen en overmorgen zijn weekend, dus bieden we de maandag erna aan.
        if (vandaag.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday)
        {
            var maandag = Volgende(DayOfWeek.Monday, vandaag);
            lijst.Add(($"Verzet naar maandag · {Kort(maandag)}", maandag));
        }

        // CED-taken: altijd de twee vaste CED-dagen, ongeacht welke dag het vandaag is.
        if (string.Equals(categorie, CedCategorie, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dag in new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday })
            {
                var doel = Volgende(dag, vandaag);
                if (lijst.All(v => v.Doel != doel))
                {
                    lijst.Add(($"Verzet naar {Naam(dag)} · {Kort(doel)} (CED)", doel));
                }
            }
        }

        return lijst;
    }

    /// <summary>"ma 29 sep" — kort genoeg voor een menu-item, met de dag erbij ter controle.</summary>
    private static string Kort(DateOnly dag) =>
        dag.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    private static string Naam(DayOfWeek dag) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(dag).ToLower(CultureInfo.CurrentCulture);
}
