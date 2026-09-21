using System.Globalization;

namespace WorkManager;

/// <summary>
/// Opmaak van bedragen en percentages in de portefeuille, inclusief de privacystand: staat
/// die aan, dan worden álle bedragen vervangen door bolletjes en blijven alleen percentages
/// over. Zo kun je het venster openhouden terwijl er iemand meekijkt of je scherm deelt.
/// </summary>
public static class Bedrag
{
    private static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-BE");

    public const string Verborgen = "€ •••";

    /// <summary>"€ 152.842" — zonder centen, want daar kijk je bij een portefeuille niet naar.</summary>
    public static string Euro(double waarde, bool verbergen = false) =>
        verbergen ? Verborgen : "€ " + waarde.ToString("N0", Nl);

    /// <summary>"€ 152.842,65" — voor de plekken waar de cent wél telt (koers per stuk).</summary>
    public static string EuroPrecies(double waarde, bool verbergen = false) =>
        verbergen ? Verborgen : "€ " + waarde.ToString("N2", Nl);

    /// <summary>"+€ 412" / "−€ 96", met echt minteken.</summary>
    public static string EuroDelta(double waarde, bool verbergen = false)
    {
        if (verbergen)
        {
            return "•••";
        }
        var teken = waarde < 0 ? "−" : "+";
        return $"{teken}€ {Math.Abs(waarde).ToString("N0", Nl)}";
    }

    /// <summary>"+0,31 %" / "−0,28 %" — blijft ook in privacystand zichtbaar.</summary>
    public static string Procent(double procent, int cijfers = 2)
    {
        var teken = procent < 0 ? "−" : "+";
        return $"{teken}{Math.Abs(procent).ToString("N" + cijfers, Nl)} %";
    }

    /// <summary>Groen bij winst, rood bij verlies, gedempt bij vlak.</summary>
    public static Color Kleur(double waarde) => Math.Abs(waarde) < 0.005
        ? Theme.Muted
        : waarde > 0
            ? Theme.Success
            : Theme.Danger;

    /// <summary>Het driehoekje dat bij die richting hoort.</summary>
    public static string Pijl(double waarde) => Math.Abs(waarde) < 0.005 ? "•" : waarde > 0 ? "▴" : "▾";
}
