namespace WorkManager;

/// <summary>
/// Blijvende achtergrondkennis per klant, die bij het opstellen van een conceptantwoord
/// meegegeven wordt. Waar de correspondentie-historiek alleen de laatste weken toont, staat
/// hier wat je over de héle samenwerking moet weten: wie is wie, welke software draait er,
/// welke afspraken gelden, welk jargon de klant gebruikt en wat er nog openstaat.
///
/// <para>Eén markdownbestand per klant in %APPDATA%\WorkManager\klantdossiers\. De eerste
/// regel bepaalt voor wie het dossier geldt:
/// <c>domeinen: vriesveemlogistics.nl, vriesveem.nl</c> (of <c>adressen:</c> met volledige
/// e-mailadressen). Zonder die regel wordt het bestand genegeerd — zo kun je er ook losse
/// notities naast leggen.</para>
/// </summary>
public static class KlantDossier
{
    private static readonly string DossierDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "klantdossiers");

    /// <summary>Boven dit aantal tekens wordt het dossier afgekapt (de prompt blijft werkbaar).</summary>
    private const int MaxTekens = 12000;

    /// <summary>
    /// Het dossier dat bij dit e-mailadres hoort, of een lege string als er geen is.
    /// Matcht op volledig adres (adressen:) of op domein (domeinen:).
    /// </summary>
    public static string Voor(string emailAdres)
    {
        var adres = emailAdres.Trim().ToLowerInvariant();
        if (adres.Length == 0 || !Directory.Exists(DossierDir))
        {
            return "";
        }
        var domein = adres.Contains('@') ? adres[(adres.IndexOf('@') + 1)..] : adres;

        foreach (var pad in Directory.EnumerateFiles(DossierDir, "*.md"))
        {
            string tekst;
            try
            {
                tekst = File.ReadAllText(pad);
            }
            catch
            {
                continue; // onleesbaar bestand overslaan
            }
            var eersteRegels = tekst.Split('\n').Take(5).ToList();
            var geldt = eersteRegels.Any(regel =>
            {
                var r = regel.Trim().ToLowerInvariant();
                if (r.StartsWith("domeinen:", StringComparison.Ordinal))
                {
                    return Waarden(r).Any(d => domein == d || domein.EndsWith("." + d, StringComparison.Ordinal));
                }
                if (r.StartsWith("adressen:", StringComparison.Ordinal))
                {
                    return Waarden(r).Contains(adres);
                }
                return false;
            });
            if (geldt)
            {
                return tekst.Length > MaxTekens ? tekst[..MaxTekens] + "\n…(ingekort)" : tekst;
            }
        }
        return "";
    }

    private static List<string> Waarden(string regel) =>
        regel[(regel.IndexOf(':') + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>De map met dossiers (wordt aangemaakt als hij nog niet bestaat).</summary>
    public static string Map()
    {
        Directory.CreateDirectory(DossierDir);
        return DossierDir;
    }
}
