using System.Text.Json;

namespace WorkManager;

/// <summary>Eén afgeronde AH-bestelling, zoals bewaard in ah-historiek.json.</summary>
public sealed class AhBestelling
{
    public DateTimeOffset Datum { get; set; }

    /// <summary>De productnamen die effectief in het mandje gingen.</summary>
    public List<string> Producten { get; set; } = new();
}

/// <summary>Wat er van een product geleerd is uit de bestelgeschiedenis.</summary>
public sealed record AhRitme(
    string Naam, int GemiddeldeDagen, DateTimeOffset Laatste, int DagenGeleden, int Keren)
{
    /// <summary>Hoeveel dagen over tijd (negatief = nog niet aan de beurt).</summary>
    public int OverTijd => DagenGeleden - GemiddeldeDagen;

    /// <summary>Regel voor in de lijst: "melk — om de 7 d, 9 d geleden".</summary>
    public string Regel =>
        $"{Naam} — om de {GemiddeldeDagen} d, {DagenGeleden} d geleden" +
        (OverTijd > 0 ? $" ({OverTijd} d over tijd)" : "");
}

/// <summary>
/// Het voorraadgeheugen van de boodschappen. Elke afgeronde bestelling wordt weggeschreven;
/// uit die reeks leidt de app per product af om de hoeveel dagen het normaal terugkomt. Wat
/// aan zijn gemiddelde toe is, staat de volgende keer bovenaan de lijst als suggestie — geen
/// vaste boodschappenlijst dus, maar een lijst die zich naar je eigen ritme voegt.
/// Producten die je pas één keer besteld hebt geven nog geen ritme en blijven buiten beeld.
/// </summary>
public static class AhHistoriek
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string HistoriekFile = Path.Combine(DataDir, "ah-historiek.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Minstens zoveel bestellingen met een product nodig voor een betrouwbaar ritme.</summary>
    private const int MinimumKeren = 3;

    /// <summary>Producten die je hooguit een paar keer per jaar koopt, laten we met rust.</summary>
    private const int MaximumInterval = 100;

    /// <summary>
    /// Zoveel van het gemiddelde interval moet verstreken zijn voor een product opduikt.
    /// Iets onder 1 zodat je het al in huis hebt vóór het echt op is.
    /// </summary>
    private const double Drempel = 0.85;

    public static List<AhBestelling> Laad()
    {
        try
        {
            if (File.Exists(HistoriekFile) &&
                JsonSerializer.Deserialize<List<AhBestelling>>(File.ReadAllText(HistoriekFile), JsonOpts)
                    is { } lijst)
            {
                return lijst;
            }
        }
        catch
        {
            // Onleesbaar: begin opnieuw met verzamelen.
        }
        return new List<AhBestelling>();
    }

    /// <summary>
    /// Schrijft een afgeronde bestelling weg. Meerdere bestellingen op dezelfde dag worden
    /// samengevoegd: dat is één boodschappenronde, geen twee, en anders zou het ritme
    /// kunstmatig versnellen.
    /// </summary>
    public static void Registreer(IEnumerable<string> producten)
    {
        var namen = producten
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (namen.Count == 0)
        {
            return;
        }

        var historiek = Laad();
        var vandaag = DateTimeOffset.Now;
        if (historiek.LastOrDefault() is { } laatste &&
            laatste.Datum.Date == vandaag.Date)
        {
            foreach (var naam in namen.Where(n =>
                         !laatste.Producten.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                laatste.Producten.Add(naam);
            }
        }
        else
        {
            historiek.Add(new AhBestelling { Datum = vandaag, Producten = namen });
        }

        // Twee jaar geschiedenis volstaat ruimschoots om een ritme te zien.
        historiek.RemoveAll(b => b.Datum < vandaag.AddYears(-2));
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(HistoriekFile, JsonSerializer.Serialize(historiek, JsonOpts));
        }
        catch
        {
            // Best effort: het geheugen is een extraatje, geen voorwaarde om te bestellen.
        }
    }

    /// <summary>
    /// De producten die volgens hun eigen ritme aan een nabestelling toe zijn, de meest
    /// achterstallige eerst. Leeg zolang er te weinig geschiedenis is.
    /// </summary>
    public static List<AhRitme> Nabestellen(int max = 12) =>
        AlleRitmes()
            .Where(r => r.DagenGeleden >= r.GemiddeldeDagen * Drempel)
            .OrderByDescending(r => r.OverTijd)
            .Take(max)
            .ToList();

    /// <summary>Het ritme van elk product waarvan er genoeg bestellingen bekend zijn.</summary>
    public static List<AhRitme> AlleRitmes()
    {
        var historiek = Laad().OrderBy(b => b.Datum).ToList();
        if (historiek.Count < MinimumKeren)
        {
            return new List<AhRitme>();
        }

        var perProduct = new Dictionary<string, List<DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bestelling in historiek)
        {
            foreach (var naam in bestelling.Producten)
            {
                if (!perProduct.TryGetValue(naam, out var datums))
                {
                    perProduct[naam] = datums = new List<DateTimeOffset>();
                }
                datums.Add(bestelling.Datum);
            }
        }

        var nu = DateTimeOffset.Now;
        var ritmes = new List<AhRitme>();
        foreach (var (naam, datums) in perProduct)
        {
            if (datums.Count < MinimumKeren)
            {
                continue;
            }
            var eerste = datums[0];
            var laatste = datums[^1];
            var gemiddelde = (int)Math.Round((laatste - eerste).TotalDays / (datums.Count - 1));
            if (gemiddelde <= 0 || gemiddelde > MaximumInterval)
            {
                continue;
            }
            ritmes.Add(new AhRitme(
                naam, gemiddelde, laatste, (int)Math.Round((nu - laatste).TotalDays), datums.Count));
        }
        return ritmes.OrderBy(r => r.Naam).ToList();
    }
}
