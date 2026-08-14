using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de SD Worx-koppeling (myworkandme.com): inloggegevens
/// (DPAPI-versleuteld) en welke teamleden niet in de weekmail-opmerking horen.
/// Persistent in %APPDATA%\WorkManager\sdworx-settings.json.
/// </summary>
public class SdWorxSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "sdworx-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string GebruikerVersleuteld { get; set; } = "";
    public string WachtwoordVersleuteld { get; set; } = "";
    public List<string> UitgeslotenNamen { get; set; } = new() { "Yvo", "Jeroen" };

    [JsonIgnore]
    public string Gebruiker
    {
        get => Decrypt(GebruikerVersleuteld);
        set => GebruikerVersleuteld = Encrypt(value);
    }

    [JsonIgnore]
    public string Wachtwoord
    {
        get => Decrypt(WachtwoordVersleuteld);
        set => WachtwoordVersleuteld = Encrypt(value);
    }

    private static string Encrypt(string value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static SdWorxSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<SdWorxSettings>(File.ReadAllText(SettingsFile), JsonOpts);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults.
        }
        return new SdWorxSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Verwerkt de uitgelezen SD Worx-teamkalender (rij per medewerker, celcode per dag) tot
/// een opmerking voor de weekmail. Codes: leeg = aanwezig, "X" = inactiviteitsdag,
/// "BF"/"VBF" = (vervangings)feestdag voor iedereen; al de rest (VBI, VAKEP, OK+, WVVW…)
/// telt als individuele afwezigheid.
/// </summary>
public static class SdWorxVakanties
{
    public sealed record MaandData(DateOnly MaandStart, List<MaandRij> Rijen);
    public sealed record MaandRij(string Naam, List<string> Dagen);

    private static readonly string[] NietAfwezig = { "", "X", "BF", "VBF" };

    /// <summary>Het overzicht kijkt drie werkweken vooruit (ma t.e.m. vr van week 3).</summary>
    public const int VensterDagen = 18;

    /// <summary>Eerstvolgende maandag (de week waarover de weekmail gaat).</summary>
    public static DateOnly VolgendeMaandag(DateOnly vandaag)
    {
        var dagen = ((int)DayOfWeek.Monday - (int)vandaag.DayOfWeek + 6) % 7 + 1;
        return vandaag.AddDays(dagen);
    }

    /// <summary>
    /// Feestdagen binnen het venster: dagen waarop minstens de helft van het team de code
    /// BF (feestdag) of VBF (vervangingsfeestdag) heeft.
    /// </summary>
    public static List<(DateOnly Datum, string Code)> VerzamelFeestdagen(
        MaandData maand, DateOnly van, DateOnly tot)
    {
        var resultaat = new List<(DateOnly, string)>();
        if (maand.Rijen.Count == 0)
        {
            return resultaat;
        }
        for (var d = van; d <= tot; d = d.AddDays(1))
        {
            if (d.Year != maand.MaandStart.Year || d.Month != maand.MaandStart.Month)
            {
                continue;
            }
            var index = d.Day - 1;
            var codes = maand.Rijen
                .Where(r => index < r.Dagen.Count)
                .Select(r => r.Dagen[index].Trim().ToUpperInvariant())
                .Where(c => c is "BF" or "VBF")
                .ToList();
            if (codes.Count >= Math.Max(1, maand.Rijen.Count / 2))
            {
                var dominant = codes.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
                resultaat.Add((d, dominant));
            }
        }
        return resultaat;
    }

    /// <summary>Afwezige (naam, datum)-paren binnen het venster, zonder de uitgesloten namen.</summary>
    public static List<(string Naam, DateOnly Datum)> VerzamelAfwezig(
        MaandData maand, DateOnly van, DateOnly tot, List<string> uitgesloten)
    {
        var resultaat = new List<(string, DateOnly)>();
        foreach (var rij in maand.Rijen)
        {
            if (uitgesloten.Any(u => rij.Naam.Contains(u, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            for (var d = van; d <= tot; d = d.AddDays(1))
            {
                if (d.Year != maand.MaandStart.Year || d.Month != maand.MaandStart.Month)
                {
                    continue;
                }
                var index = d.Day - 1;
                if (index < rij.Dagen.Count &&
                    !NietAfwezig.Contains(rij.Dagen[index].Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    resultaat.Add((rij.Naam, d));
                }
            }
        }
        return resultaat;
    }

    /// <summary>
    /// Persoonlijke niet-werkdagen binnen het venster: inactiviteitsdagen ("X", bv. een
    /// vaste vrije vrijdag bij deeltijds werk) en (vervangings)feestdagen. Die tellen niet
    /// als terugkeerdag en breken een afwezigheidsreeks niet.
    /// </summary>
    public static List<(string Naam, DateOnly Datum)> VerzamelNietWerk(
        MaandData maand, DateOnly van, DateOnly tot)
    {
        var resultaat = new List<(string, DateOnly)>();
        foreach (var rij in maand.Rijen)
        {
            for (var d = van; d <= tot; d = d.AddDays(1))
            {
                if (d.Year != maand.MaandStart.Year || d.Month != maand.MaandStart.Month)
                {
                    continue;
                }
                var index = d.Day - 1;
                if (index < rij.Dagen.Count &&
                    rij.Dagen[index].Trim().ToUpperInvariant() is "X" or "BF" or "VBF")
                {
                    resultaat.Add((rij.Naam, d));
                }
            }
        }
        return resultaat;
    }

    /// <summary>
    /// Groepeert afwezige dagen per persoon tot aaneengesloten reeksen. Weekends en
    /// persoonlijke niet-werkdagen (vaste vrije dagen, feestdagen) breken een reeks niet.
    /// </summary>
    public static List<(string Naam, DateOnly Van, DateOnly Tot)> BouwReeksen(
        List<(string Naam, DateOnly Datum)> afwezig,
        IReadOnlyDictionary<string, HashSet<DateOnly>> nietWerk)
    {
        bool Vrij(string naam, DateOnly d) =>
            d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            (nietWerk.TryGetValue(naam, out var dagen) && dagen.Contains(d));

        var resultaat = new List<(string, DateOnly, DateOnly)>();
        foreach (var groep in afwezig.GroupBy(a => a.Naam))
        {
            var reeksen = new List<(DateOnly Van, DateOnly Tot)>();
            foreach (var dag in groep.Select(a => a.Datum).Distinct().OrderBy(d => d))
            {
                var aansluitend = false;
                if (reeksen.Count > 0 && (dag.DayNumber - reeksen[^1].Tot.DayNumber) <= 14)
                {
                    // Aansluitend als alle tussenliggende dagen vrije dagen zijn.
                    aansluitend = true;
                    for (var d = reeksen[^1].Tot.AddDays(1); d < dag; d = d.AddDays(1))
                    {
                        if (!Vrij(groep.Key, d))
                        {
                            aansluitend = false;
                            break;
                        }
                    }
                }
                if (aansluitend)
                {
                    reeksen[^1] = (reeksen[^1].Van, dag);
                }
                else
                {
                    reeksen.Add((dag, dag));
                }
            }
            resultaat.AddRange(reeksen.Select(r => (groep.Key, r.Van, r.Tot)));
        }
        return resultaat;
    }

    /// <summary>
    /// Bouwt de opmerking: eerst de feestdagen, dan per persoon de afwezigheden die binnen
    /// de komende drie werkweken beginnen, mét de dag waarop iemand terug is. De data mag
    /// verder reiken dan het meldvenster (er wordt vooruitgebladerd om de terugkeer te
    /// vinden); alleen als een reeks tot het einde van de geladen data doorloopt, valt er
    /// niets beters te zeggen dan "zeker t.e.m. …".
    /// </summary>
    public static string BouwSamenvatting(
        List<(string Naam, DateOnly Datum)> afwezig,
        List<(DateOnly Datum, string Code)> feestdagen,
        DateOnly maandag,
        DateOnly dataEinde,
        IReadOnlyDictionary<string, HashSet<DateOnly>> nietWerk)
    {
        var meldEinde = maandag.AddDays(VensterDagen);
        var cultuur = CultureInfo.GetCultureInfo("nl-BE");
        string Kort(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToString("ddd d/M", cultuur);
        // Namen staan als "Achternaam Voornaam" in de kalender; de voornaam is het laatste woord.
        string Voornaam(string naam) => naam.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        // Eerstvolgende échte werkdag voor deze persoon: geen weekend, geen vaste vrije
        // dag ("X", bv. wie op vrijdag nooit werkt) en geen feestdag.
        DateOnly Werkdag(string naam, DateOnly d)
        {
            for (var i = 0; i < 40; i++)
            {
                var vrij = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                           (nietWerk.TryGetValue(naam, out var dagen) && dagen.Contains(d));
                if (!vrij)
                {
                    return d;
                }
                d = d.AddDays(1);
            }
            return d;
        }

        var regels = new List<string>();
        foreach (var (datum, code) in feestdagen.DistinctBy(f => f.Datum).OrderBy(f => f.Datum))
        {
            regels.Add(code == "VBF"
                ? $"Vervangingsfeestdag op {Kort(datum)}."
                : $"Feestdag op {Kort(datum)}.");
        }

        foreach (var (naamVolledig, van, tot) in BouwReeksen(afwezig, nietWerk))
        {
            if (van > meldEinde)
            {
                continue; // begint pas na het meldvenster
            }
            var naam = Voornaam(naamVolledig);
            if (tot >= dataEinde.AddDays(-3))
            {
                // Loopt door tot het einde van alles wat we geladen hebben (langdurig).
                regels.Add(van == maandag
                    ? $"{naam} is langdurig afwezig, zeker t.e.m. {Kort(tot)}."
                    : $"{naam} is vanaf {Kort(van)} langdurig afwezig, zeker t.e.m. {Kort(tot)}.");
            }
            else if (van == tot)
            {
                regels.Add($"{naam} afwezig op {Kort(van)}.");
            }
            else
            {
                var terug = Werkdag(naamVolledig, tot.AddDays(1));
                regels.Add(van == maandag
                    ? $"{naam} is afwezig t.e.m. {Kort(tot)}, terug op {Kort(terug)}."
                    : $"{naam} afwezig van {Kort(van)} t.e.m. {Kort(tot)}, terug op {Kort(terug)}.");
            }
        }
        return string.Join(Environment.NewLine, regels.Distinct());
    }

    /// <summary>
    /// Leest de afwezigheidsregels uit de weekmail-opmerking terug als periodes (voornaam,
    /// van, tot). De opmerking is de enige plek waar de opgehaalde SD Worx-afwezigheden
    /// bewaard blijven; het teamtakenvenster gebruikt dit om wie vandaag afwezig is
    /// dichtgeklapt te starten. Regels zonder begindatum ("is afwezig t.e.m. …") lopen al:
    /// die beginnen op <paramref name="vandaag"/>. Datums hebben geen jaartal; een datum
    /// die maanden voorbij is, hoort bij volgend jaar.
    /// </summary>
    public static List<(string Naam, DateOnly Van, DateOnly Tot)> ParseAfwezigheden(
        string opmerking, DateOnly vandaag)
    {
        DateOnly? Datum(string tekst)
        {
            var m = System.Text.RegularExpressions.Regex.Match(tekst, @"(\d{1,2})/(\d{1,2})");
            if (!m.Success)
            {
                return null;
            }
            var maand = int.Parse(m.Groups[2].Value);
            if (maand is < 1 or > 12)
            {
                return null;
            }
            var dag = Math.Min(int.Parse(m.Groups[1].Value),
                DateTime.DaysInMonth(vandaag.Year, maand));
            var d = new DateOnly(vandaag.Year, maand, dag);
            return d < vandaag.AddDays(-120) ? d.AddYears(1) : d;
        }

        var resultaat = new List<(string, DateOnly, DateOnly)>();
        foreach (var ruw in opmerking.Split('\n'))
        {
            var regel = ruw.Trim().TrimEnd('.');
            var m = System.Text.RegularExpressions.Regex.Match(
                regel, @"^(?<naam>\S+)( is)? afwezig van (?<van>.+?) t\.e\.m\. (?<tot>[^,]+)");
            if (m.Success && Datum(m.Groups["van"].Value) is { } v1 &&
                Datum(m.Groups["tot"].Value) is { } t1)
            {
                resultaat.Add((m.Groups["naam"].Value, v1, t1));
                continue;
            }
            m = System.Text.RegularExpressions.Regex.Match(
                regel, @"^(?<naam>\S+) is afwezig t\.e\.m\. (?<tot>[^,]+)");
            if (m.Success && Datum(m.Groups["tot"].Value) is { } t2)
            {
                resultaat.Add((m.Groups["naam"].Value, vandaag, t2));
                continue;
            }
            m = System.Text.RegularExpressions.Regex.Match(
                regel, @"^(?<naam>\S+)( is)? afwezig op (?<dag>.+)$");
            if (m.Success && Datum(m.Groups["dag"].Value) is { } d3)
            {
                resultaat.Add((m.Groups["naam"].Value, d3, d3));
                continue;
            }
            m = System.Text.RegularExpressions.Regex.Match(
                regel, @"^(?<naam>\S+) is( vanaf (?<van>.+?))? langdurig afwezig, zeker t\.e\.m\. (?<tot>.+)$");
            if (m.Success && Datum(m.Groups["tot"].Value) is { } t4)
            {
                var van = m.Groups["van"].Success ? Datum(m.Groups["van"].Value) : null;
                resultaat.Add((m.Groups["naam"].Value, van ?? vandaag, t4));
            }
        }
        return resultaat;
    }

    /// <summary>
    /// Herkent een regel die door <see cref="BouwSamenvatting"/> (of een oudere variant
    /// ervan, of als handmatige afwezigheidsnotitie) in de opmerking is gezet. Bij opnieuw
    /// ophalen worden die regels vervangen in plaats van opgestapeld.
    /// </summary>
    public static bool IsAfwezigheidsRegel(string regel)
    {
        regel = regel.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(
                regel, @"^(Vervangings)?[Ff]eestdag op ") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                regel, @"^\S+( is)? (langdurig |vanaf [^,]+ )?afwezig( |,|\.)");
    }
}
