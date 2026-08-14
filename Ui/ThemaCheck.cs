using System.Text;

namespace WorkManager;

/// <summary>
/// Leesbaarheidscontrole voor de kleurenschema's: rekent per palet het WCAG-contrast uit
/// van elke tekstkleur op elke ondergrond waarop hij echt voorkomt. Draaien met
/// <c>WorkManager.exe --themacheck</c>; de exitcode is het aantal combinaties dat zakt onder
/// de norm, en het rapport komt op stdout én in %APPDATA%\WorkManager\thema-check.txt.
///
/// <para>Norm: 4,5:1 voor gewone tekst, 3,0:1 voor grote tekst en voor lijnen/randen. Kleuren
/// die alleen als vlak dienen (accentbalkjes) worden op 3,0 getoetst.</para>
/// </summary>
public static class ThemaCheck
{
    private const double NormTekst = 4.5;
    private const double NormGroot = 3.0;

    public static int Draai()
    {
        var rapport = new StringBuilder();
        var fouten = 0;

        foreach (var p in Themas.Alle)
        {
            rapport.AppendLine($"=== {p.Naam} ({(p.Donker ? "donker" : "licht")}) ===");
            // Elke ondergrond waarop tekst echt terechtkomt — inclusief de menu-achtergrond
            // (paneel), de hover-toestand van een menu-item of knop (kaart-hover), de
            // selectiekleur in lijsten en de zebrastreep van oneven rijen.
            var selectie = Theme.Selectie(p);
            var zebra = p.Donker
                ? Color.FromArgb(Math.Min(255, p.Bg.R + 6), Math.Min(255, p.Bg.G + 6),
                    Math.Min(255, p.Bg.B + 9))
                : Color.FromArgb(Math.Max(0, p.Bg.R - 5), Math.Max(0, p.Bg.G - 5),
                    Math.Max(0, p.Bg.B - 4));
            var vlakken = new (string Naam, Color Kleur)[]
            {
                ("achtergrond", p.Bg), ("paneel/menu", p.Surface), ("kaart", p.Card),
                ("kaart-hover/menu-hover", p.CardHover), ("veld", p.Field),
                ("selectie", selectie), ("zebrarij", zebra),
            };
            var teksten = new (string Naam, Color Kleur, double Norm)[]
            {
                ("tekst", p.Text, NormTekst),
                ("gedempt", p.Muted, NormTekst),
                ("accent", p.Accent, NormGroot),
                ("accent-hover", p.AccentHover, NormGroot),
                ("waarschuwing", p.Warn, NormTekst),
                ("succes", p.Success, NormTekst),
                ("gevaar", p.Danger, NormTekst),
                ("klant CED", p.KlantCed, NormTekst),
                ("klant Aqurat", p.KlantAqurat, NormTekst),
                ("klant Radiology", p.KlantRadiology, NormTekst),
                ("klant UrbanIT", p.KlantUrbanIt, NormTekst),
                ("klant Privé", p.KlantPrive, NormTekst),
                ("klant Lauryssens", p.KlantLauryssens, NormTekst),
            };

            foreach (var (tekstNaam, tekstKleur, norm) in teksten)
            {
                foreach (var (vlakNaam, vlakKleur) in vlakken)
                {
                    var ratio = Contrast(tekstKleur, vlakKleur);
                    if (ratio < norm)
                    {
                        fouten++;
                        rapport.AppendLine(
                            $"  ✗ {tekstNaam} op {vlakNaam}: {ratio:0.00}:1 (norm {norm:0.0})");
                    }
                }
            }

            // Knoptekst op het accentvlak: de app kiest daar wit óf bijna-zwart (Theme.OpAccent),
            // afhankelijk van welk van de twee het beste leest. Toets dus dezelfde keuze.
            var opAccent = Math.Max(Contrast(Color.White, p.Accent),
                Contrast(Color.FromArgb(16, 16, 20), p.Accent));
            if (opAccent < NormTekst)
            {
                fouten++;
                rapport.AppendLine($"  ✗ knoptekst op accent: {opAccent:0.00}:1 (norm {NormTekst:0.0})");
            }
            // Randen moeten zichtbaar zijn tegen hun ondergrond.
            var rand = Contrast(p.Border, p.Bg);
            if (rand < 1.25)
            {
                fouten++;
                rapport.AppendLine($"  ✗ rand op achtergrond: {rand:0.00}:1 (minstens 1,25)");
            }
            rapport.AppendLine($"  … tekst/achtergrond {Contrast(p.Text, p.Bg):0.00}:1, " +
                               $"gedempt/achtergrond {Contrast(p.Muted, p.Bg):0.00}:1, " +
                               $"accent/achtergrond {Contrast(p.Accent, p.Bg):0.00}:1");
        }

        rapport.AppendLine(fouten == 0
            ? "Alle paletten halen de norm."
            : $"{fouten} combinatie(s) onder de norm.");
        var tekst = rapport.ToString();
        Console.Write(tekst);
        try
        {
            var pad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "thema-check.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(pad)!);
            File.WriteAllText(pad, tekst);
        }
        catch
        {
            // Rapport op stdout volstaat.
        }
        return fouten;
    }

    /// <summary>WCAG-contrastverhouding tussen twee kleuren (1:1 = gelijk, 21:1 = zwart-wit).</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminantie(a);
        var lb = Luminantie(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminantie(Color c)
    {
        static double Kanaal(int waarde)
        {
            var v = waarde / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Kanaal(c.R) + 0.7152 * Kanaal(c.G) + 0.0722 * Kanaal(c.B);
    }
}
