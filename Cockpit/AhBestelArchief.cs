using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén product zoals het in het archief staat: wat gevraagd werd en wat ervan terechtkwam.</summary>
public sealed class AhArchiefProduct
{
    public string Naam { get; set; } = "";
    public string? Url { get; set; }
    public int Aantal { get; set; } = 1;

    /// <summary>Uit welk gerecht of welke rubriek dit product kwam (leeg = handmatig toegevoegd).</summary>
    public string Herkomst { get; set; } = "";

    /// <summary>Lag het na het vullen écht in het mandje? Null zolang dat niet gecontroleerd is.</summary>
    public bool? InMandje { get; set; }
}

/// <summary>Eén regel in het archiefbestand: een bestelling, of de mandjecontrole erna.</summary>
public sealed class AhArchiefRegel
{
    /// <summary>"bestelling" of "mandje".</summary>
    public string Soort { get; set; } = "bestelling";

    public DateTimeOffset Datum { get; set; }

    /// <summary>
    /// Sleutel die een mandjeregel aan zijn bestelling knoopt: de datum van de bestelling in
    /// ISO-formaat tot op de seconde.
    /// </summary>
    public string Sleutel { get; set; } = "";

    /// <summary>"cockpit" (AH-bestelvenster), "web" (de pagina voor Hilke) of "historiek" (migratie).</summary>
    public string Bron { get; set; } = "";

    public List<string> Gerechten { get; set; } = new();
    public List<AhArchiefProduct> Producten { get; set; } = new();

    /// <summary>Wat zelf gezocht moest worden (geen productlink).</summary>
    public List<string> Handmatig { get; set; } = new();

    public string Opmerking { get; set; } = "";
}

/// <summary>Een bestelling uit het archief, met de mandjecontrole er al in verwerkt.</summary>
public sealed record AhBesteldeRonde(
    DateTimeOffset Datum,
    string Bron,
    List<string> Gerechten,
    List<AhArchiefProduct> Producten,
    List<string> Handmatig)
{
    /// <summary>Alles wat besteld werd: producten met link én handmatige regels.</summary>
    public IEnumerable<string> AlleNamen =>
        Producten.Select(p => p.Naam).Concat(Handmatig);

    public int AantalStuks => Producten.Sum(p => Math.Max(1, p.Aantal)) + Handmatig.Count;

    /// <summary>Hoeveel producten na het vullen effectief in het mandje lagen (null = niet gecontroleerd).</summary>
    public int? AantalInMandje =>
        Producten.Any(p => p.InMandje is not null)
            ? Producten.Count(p => p.InMandje == true)
            : null;

    public string GerechtenKort => Gerechten.Count == 0
        ? ""
        : string.Join(", ", Gerechten.Take(3)) + (Gerechten.Count > 3 ? $" +{Gerechten.Count - 3}" : "");
}

/// <summary>Wat het archief over één product weet.</summary>
public sealed record AhProductTelling(
    string Naam, int Keren, int Stuks, DateTimeOffset Laatste, DateTimeOffset Eerste)
{
    public int DagenGeleden => (int)(DateTimeOffset.Now - Laatste).TotalDays;
}

/// <summary>
/// Het volledige archief van de AH-bestellingen: élke bestelling die via de cockpit of via de
/// webpagina wordt samengesteld, met de gekozen gerechten, de producten (met aantal en uit
/// welk gerecht ze kwamen) en — zodra het winkelmandje gevuld is — of ze er echt in lagen.
///
/// <para>Dit is bewust een ander bestand dan <see cref="AhHistoriek"/>: dat is het
/// voorraadgeheugen dat na twee jaar vergeet om het ritme vers te houden. Het archief vergeet
/// niets en wordt alleen maar aangevuld, zodat je er later nog iets mee kunt doen (wat kochten
/// we vorig jaar rond de feestdagen, hoe vaak gaat er pasta mee, welke gerechten komen echt
/// terug). Eén JSON-object per regel in <c>ah-archief.jsonl</c>: aanvullen kost één regel
/// schrijven, en een half weggeschreven regel maakt de rest niet onleesbaar.</para>
/// </summary>
public static class AhBestelArchief
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    /// <summary>Het archiefbestand — append-only, één JSON-object per regel.</summary>
    public static string Bestand => Path.Combine(DataDir, "ah-archief.jsonl");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Grendel = new();

    /// <summary>De sleutel waarmee een mandjecontrole bij zijn bestelling hoort.</summary>
    public static string Sleutel(DateTimeOffset datum) =>
        datum.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Legt een samengestelde bestelling vast en geeft de sleutel terug, zodat de mandjecontrole
    /// er later bij kan. Loopt alles best effort: een bestelling mag nooit stranden op het archief.
    /// </summary>
    public static string Leg(
        string bron, IEnumerable<string> gerechten, IEnumerable<AhArchiefProduct> producten,
        IEnumerable<string> handmatig, string opmerking = "")
    {
        var datum = DateTimeOffset.Now;
        var regel = new AhArchiefRegel
        {
            Soort = "bestelling",
            Datum = datum,
            Sleutel = Sleutel(datum),
            Bron = bron,
            Gerechten = gerechten.Where(g => g.Trim().Length > 0).Select(g => g.Trim()).ToList(),
            Producten = producten.ToList(),
            Handmatig = handmatig.Where(h => h.Trim().Length > 0).Select(h => h.Trim()).ToList(),
            Opmerking = opmerking,
        };
        Schrijf(regel);
        return regel.Sleutel;
    }

    /// <summary>
    /// Vult het archief aan met wat er na het vullen écht in het mandje lag. Komt als aparte
    /// regel in het bestand: het archief wordt alleen aangevuld, nooit herschreven.
    /// </summary>
    public static void LegMandje(string sleutel, IEnumerable<AhArchiefProduct> producten)
    {
        var lijst = producten.ToList();
        if (sleutel.Length == 0 || lijst.Count == 0)
        {
            return;
        }
        Schrijf(new AhArchiefRegel
        {
            Soort = "mandje",
            Datum = DateTimeOffset.Now,
            Sleutel = sleutel,
            Producten = lijst,
        });
    }

    /// <summary>Alle bestelrondes, nieuwste eerst, met de mandjecontrole er al in verwerkt.</summary>
    public static List<AhBesteldeRonde> Lees()
    {
        var regels = LeesRegels();
        var mandjes = regels
            .Where(r => r.Soort == "mandje" && r.Sleutel.Length > 0)
            .GroupBy(r => r.Sleutel)
            .ToDictionary(g => g.Key, g => g.Last().Producten);

        var rondes = new List<AhBesteldeRonde>();
        foreach (var r in regels.Where(r => r.Soort == "bestelling"))
        {
            var producten = r.Producten.Select(p => new AhArchiefProduct
            {
                Naam = p.Naam, Url = p.Url, Aantal = p.Aantal, Herkomst = p.Herkomst,
                InMandje = p.InMandje,
            }).ToList();
            if (r.Sleutel.Length > 0 && mandjes.TryGetValue(r.Sleutel, out var controle))
            {
                foreach (var p in producten)
                {
                    var gevonden = controle.FirstOrDefault(c =>
                        string.Equals(c.Naam, p.Naam, StringComparison.OrdinalIgnoreCase) ||
                        (c.Url is { Length: > 0 } && c.Url == p.Url));
                    if (gevonden is not null)
                    {
                        p.InMandje = gevonden.InMandje;
                    }
                }
            }
            rondes.Add(new AhBesteldeRonde(
                r.Datum, r.Bron, r.Gerechten, producten, r.Handmatig));
        }
        return rondes.OrderByDescending(b => b.Datum).ToList();
    }

    /// <summary>Per product wat het archief ervan weet, de vaakst bestelde eerst.</summary>
    public static List<AhProductTelling> PerProduct()
    {
        var per = new Dictionary<string, (int Keren, int Stuks, DateTimeOffset Laatste, DateTimeOffset Eerste)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var ronde in Lees())
        {
            // Per ronde één keer per product tellen (twee keer pasta in dezelfde bestelling is
            // één boodschappenronde), maar de stuks wél optellen.
            var gezien = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ronde.Producten)
            {
                Tel(p.Naam, Math.Max(1, p.Aantal));
            }
            foreach (var naam in ronde.Handmatig)
            {
                Tel(naam, 1);
            }

            void Tel(string naam, int stuks)
            {
                naam = naam.Trim();
                if (naam.Length == 0)
                {
                    return;
                }
                var nieuw = gezien.Add(naam);
                if (per.TryGetValue(naam, out var huidig))
                {
                    per[naam] = (
                        huidig.Keren + (nieuw ? 1 : 0),
                        huidig.Stuks + stuks,
                        ronde.Datum > huidig.Laatste ? ronde.Datum : huidig.Laatste,
                        ronde.Datum < huidig.Eerste ? ronde.Datum : huidig.Eerste);
                }
                else
                {
                    per[naam] = (1, stuks, ronde.Datum, ronde.Datum);
                }
            }
        }
        return per
            .Select(kv => new AhProductTelling(
                kv.Key, kv.Value.Keren, kv.Value.Stuks, kv.Value.Laatste, kv.Value.Eerste))
            .OrderByDescending(t => t.Keren)
            .ThenBy(t => t.Naam, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Eén regel samenvatting voor onderaan het venster.</summary>
    public static string Samenvatting()
    {
        var rondes = Lees();
        if (rondes.Count == 0)
        {
            return "Nog geen bestellingen in het archief.";
        }
        var stuks = rondes.Sum(r => r.AantalStuks);
        var eerste = rondes.Min(r => r.Datum);
        return $"{rondes.Count} bestellingen sinds {eerste:MMMM yyyy} · {stuks} producten · " +
               $"gemiddeld {(double)stuks / rondes.Count:0.#} per keer";
    }

    /// <summary>
    /// Zet het archief in een CSV-bestand (één regel per product per bestelling), zodat je er
    /// in Excel of DataGrip verder mee kunt. Geeft het pad terug.
    /// </summary>
    public static string ExporteerCsv()
    {
        var pad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"ah-bestellingen-{DateTime.Now:yyyy-MM-dd}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("datum;bron;gerechten;product;aantal;herkomst;in mandje");
        foreach (var ronde in Lees().OrderBy(r => r.Datum))
        {
            var gerechten = Csv(string.Join(" | ", ronde.Gerechten));
            foreach (var p in ronde.Producten)
            {
                sb.AppendLine($"{ronde.Datum:yyyy-MM-dd HH:mm};{Csv(ronde.Bron)};{gerechten};" +
                              $"{Csv(p.Naam)};{Math.Max(1, p.Aantal)};{Csv(p.Herkomst)};" +
                              $"{(p.InMandje is null ? "" : p.InMandje == true ? "ja" : "nee")}");
            }
            foreach (var naam in ronde.Handmatig)
            {
                sb.AppendLine($"{ronde.Datum:yyyy-MM-dd HH:mm};{Csv(ronde.Bron)};{gerechten};" +
                              $"{Csv(naam)};1;zelf zoeken;");
            }
        }
        File.WriteAllText(pad, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel leest dan juist
        return pad;

        static string Csv(string waarde) => waarde.Replace(';', ',').Replace("\r", "").Replace("\n", " ");
    }

    /// <summary>
    /// Haalt de bestaande bestelgeschiedenis (het voorraadgeheugen) eenmalig het archief in,
    /// zodat het archief niet bij nul begint. Alleen de productnamen zijn daar bekend; die
    /// regels krijgen bron "historiek". Doet niets als er al iets in het archief staat.
    /// </summary>
    public static void MigreerUitHistoriek()
    {
        try
        {
            if (File.Exists(Bestand))
            {
                return;
            }
            var oud = AhHistoriek.Laad();
            if (oud.Count == 0)
            {
                return;
            }
            foreach (var bestelling in oud.OrderBy(b => b.Datum))
            {
                Schrijf(new AhArchiefRegel
                {
                    Soort = "bestelling",
                    Datum = bestelling.Datum,
                    Sleutel = Sleutel(bestelling.Datum),
                    Bron = "historiek",
                    Producten = bestelling.Producten
                        .Select(n => new AhArchiefProduct { Naam = n })
                        .ToList(),
                    Opmerking = "overgenomen uit het voorraadgeheugen (alleen productnamen bekend)",
                });
            }
        }
        catch
        {
            // Zonder migratie begint het archief vandaag; dat is geen reden om te klagen.
        }
    }

    private static void Schrijf(AhArchiefRegel regel)
    {
        try
        {
            lock (Grendel)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Bestand,
                    JsonSerializer.Serialize(regel, JsonOpts) + Environment.NewLine);
            }
        }
        catch
        {
            // Best effort: het archief is een geheugensteun, geen voorwaarde om te bestellen.
        }
    }

    private static List<AhArchiefRegel> LeesRegels()
    {
        var regels = new List<AhArchiefRegel>();
        try
        {
            lock (Grendel)
            {
                if (!File.Exists(Bestand))
                {
                    return regels;
                }
                foreach (var lijn in File.ReadLines(Bestand))
                {
                    if (lijn.Trim().Length == 0)
                    {
                        continue;
                    }
                    try
                    {
                        if (JsonSerializer.Deserialize<AhArchiefRegel>(lijn, JsonOpts) is { } r)
                        {
                            regels.Add(r);
                        }
                    }
                    catch
                    {
                        // Één kapotte regel (bv. na een crash tijdens het schrijven) mag de
                        // rest van het archief niet meesleuren.
                    }
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: dan is het archief voor nu leeg.
        }
        return regels;
    }
}
