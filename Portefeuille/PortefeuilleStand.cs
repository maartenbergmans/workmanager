using System.Globalization;
using System.Text.Json;

namespace WorkManager;

/// <summary>Eén positie, doorgerekend tegen de laatst bekende koers.</summary>
public sealed record PositieWaarde(Positie Positie, Koers? Koers, double NaarEuro)
{
    public double Prijs => (Koers?.Prijs ?? 0) * NaarEuro;

    public double Waarde => Prijs * Positie.Aantal;

    public double DagVerschil => (Koers?.DagVerschil ?? 0) * NaarEuro * Positie.Aantal;

    public double DagProcent => Koers?.DagProcent ?? 0;

    /// <summary>Inleg tegen de ingevulde gemiddelde aankoopkoers; leeg als die ontbreekt.</summary>
    public double? Inleg => Positie.AankoopKoers is { } k and > 0 ? k * Positie.Aantal : null;

    public double? Rendement => Inleg is { } inleg ? Waarde - inleg : null;

    public double? RendementProcent => Inleg is { } inleg and > 0 ? (Waarde - inleg) / inleg * 100 : null;
}

/// <summary>
/// De hele portefeuille op één moment: alle posities doorgerekend, met de totalen per potje.
/// </summary>
public sealed record PortefeuilleStand(
    IReadOnlyList<PositieWaarde> Regels,
    DateTimeOffset Moment,
    bool UitCache,
    bool BeursOpen)
{
    public double Totaal => Regels.Sum(r => r.Waarde);

    public double DagVerschil => Regels.Sum(r => r.DagVerschil);

    public double VorigeSluiting => Totaal - DagVerschil;

    public double DagProcent => VorigeSluiting > 0 ? DagVerschil / VorigeSluiting * 100 : 0;

    /// <summary>Alleen ingevuld als élke positie een aankoopkoers heeft; anders misleidend.</summary>
    public double? Inleg => Regels.All(r => r.Inleg is not null) && Regels.Count > 0
        ? Regels.Sum(r => r.Inleg!.Value)
        : null;

    public double? Rendement => Inleg is { } inleg ? Totaal - inleg : null;

    public double? RendementProcent => Inleg is { } inleg and > 0 ? (Totaal - inleg) / inleg * 100 : null;

    /// <summary>Er ontbreekt een koers: het totaal is dan onvolledig.</summary>
    public bool Onvolledig => Regels.Any(r => r.Koers is null);

    /// <summary>Dezelfde stand, beperkt tot één potje (leeg = alles).</summary>
    public PortefeuilleStand Filter(string? potje) =>
        string.IsNullOrEmpty(potje)
            ? this
            : this with
            {
                Regels = Regels
                    .Where(r => r.Positie.Potje.Equals(potje, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            };

    /// <summary>Totaal en dagverschil per potje, grootste eerst.</summary>
    public IReadOnlyList<(string Potje, double Waarde, double DagVerschil)> PerPotje() =>
        Regels.GroupBy(r => r.Positie.Potje, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Potje: g.Key, Waarde: g.Sum(r => r.Waarde), DagVerschil: g.Sum(r => r.DagVerschil)))
            .OrderByDescending(g => g.Waarde)
            .ToList();
}

/// <summary>Waardeverloop van de portefeuille over een periode.</summary>
public sealed record Verloop(IReadOnlyList<(DateOnly Dag, double Waarde)> Punten)
{
    public bool Bruikbaar => Punten.Count >= 2;

    public double Eerste => Punten.Count > 0 ? Punten[0].Waarde : 0;

    public double Laatste => Punten.Count > 0 ? Punten[^1].Waarde : 0;

    public double Verschil => Laatste - Eerste;

    public double Procent => Eerste > 0 ? Verschil / Eerste * 100 : 0;
}

/// <summary>
/// Rekent de portefeuille door tegen actuele koersen en reconstrueert het waardeverloop.
/// Het verloop is bewust "de posities van vandaag tegen de koersen van toen": zonder
/// transactiehistoriek is dat de enige eerlijke lijn — bijkopen verschijnt er niet als winst.
/// </summary>
public static class PortefeuilleMeting
{
    /// <summary>Periodes zoals ze in de knoppenbalk staan, met hun Yahoo-bereik.</summary>
    public static readonly (string Label, string Bereik, string Uitleg)[] Periodes =
    {
        ("1M", "1mo", "de laatste maand"),
        ("3M", "3mo", "de laatste drie maanden"),
        ("1J", "1y", "het laatste jaar"),
        ("5J", "5y", "de laatste vijf jaar"),
    };

    /// <summary>Haalt verse koersen op en rekent alles door.</summary>
    public static async Task<PortefeuilleStand> MeetAsync(Portefeuille portefeuille, CancellationToken ct)
    {
        var koersen = await Koersen.HaalAllemaalAsync(
            portefeuille.Posities.Select(p => p.Ticker), ct);
        var wissel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var valuta in koersen.Values.Select(k => k.Valuta).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            wissel[valuta] = await Koersen.NaarEuroAsync(valuta, ct);
        }
        return Bouw(portefeuille, t => koersen.GetValueOrDefault(t), wissel);
    }

    /// <summary>
    /// Dezelfde stand uit de schijfcache, zonder netwerk: zo staat er meteen iets op het
    /// scherm (en toont de cockpitknop na een herstart al de juiste stand).
    /// </summary>
    public static PortefeuilleStand UitCache(Portefeuille portefeuille) =>
        Bouw(portefeuille, Koersen.UitCache, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    private static PortefeuilleStand Bouw(
        Portefeuille portefeuille,
        Func<string, Koers?> koersVan,
        IReadOnlyDictionary<string, double> wissel)
    {
        var regels = new List<PositieWaarde>();
        foreach (var positie in portefeuille.Posities)
        {
            var koers = koersVan(positie.Ticker);
            var factor = koers is null ? 1 : wissel.GetValueOrDefault(koers.Valuta, 1);
            regels.Add(new PositieWaarde(positie, koers, factor));
        }
        var gekend = regels.Where(r => r.Koers is not null).Select(r => r.Koers!).ToList();
        return new PortefeuilleStand(
            regels,
            gekend.Count > 0 ? gekend.Max(k => k.Moment) : DateTimeOffset.Now,
            UitCache: gekend.Count > 0 && gekend.All(k => k.UitCache),
            BeursOpen: gekend.Any(k => k.BeursOpen));
    }

    /// <summary>
    /// Het waardeverloop over een periode, met de huidige aantallen. De laatste dag krijgt de
    /// actuele stand mee, zodat de lijn tot op de minuut doorloopt.
    /// </summary>
    public static async Task<Verloop> VerloopAsync(
        Portefeuille portefeuille, string bereik, string? potje, PortefeuilleStand? nu, CancellationToken ct)
    {
        var posities = portefeuille.Posities
            .Where(p => string.IsNullOrEmpty(potje) ||
                        p.Potje.Equals(potje, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Aantal != 0)
            .ToList();
        var tickers = posities.Select(p => p.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tickers.Count == 0)
        {
            return new Verloop(Array.Empty<(DateOnly, double)>());
        }

        // Naast elkaar ophalen: bij een trage koersbron scheelt dat de helft van de wachttijd.
        var opgehaald = await Task.WhenAll(tickers.Select(t => Koersen.HistoriekAsync(t, bereik, ct)));
        var reeksen = new Dictionary<string, IReadOnlyList<(DateOnly Dag, double Slot)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reeks in opgehaald)
        {
            if (reeks is { Punten.Count: > 0 })
            {
                reeksen[reeks.Ticker] = reeks.Punten;
            }
        }
        if (reeksen.Count < tickers.Count)
        {
            // Eén ontbrekende reeks zou een sprong in de lijn geven; dan liever geen grafiek.
            return new Verloop(Array.Empty<(DateOnly, double)>());
        }

        var dagen = reeksen.Values.SelectMany(r => r.Select(p => p.Dag)).Distinct().OrderBy(d => d).ToList();
        var lopend = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var index = tickers.ToDictionary(t => t, _ => 0, StringComparer.OrdinalIgnoreCase);
        var punten = new List<(DateOnly, double)>();
        foreach (var dag in dagen)
        {
            foreach (var ticker in tickers)
            {
                var reeks = reeksen[ticker];
                // Vooruitschuiven tot en met deze dag; ontbreekt ze (beursfeestdag), dan blijft
                // de vorige slotkoers staan.
                while (index[ticker] < reeks.Count && reeks[index[ticker]].Dag <= dag)
                {
                    lopend[ticker] = reeks[index[ticker]].Slot;
                    index[ticker]++;
                }
            }
            if (lopend.Count < tickers.Count)
            {
                continue; // nog niet elke reeks begonnen
            }
            punten.Add((dag, posities.Sum(p => lopend[p.Ticker] * p.Aantal)));
        }

        if (nu is not null && punten.Count > 0)
        {
            var actueel = nu.Filter(potje).Totaal;
            if (actueel > 0)
            {
                var vandaag = DateOnly.FromDateTime(DateTime.Now);
                punten[^1] = punten[^1].Item1 == vandaag
                    ? (vandaag, actueel)
                    : punten[^1];
                if (punten[^1].Item1 != vandaag)
                {
                    punten.Add((vandaag, actueel));
                }
            }
        }
        return new Verloop(punten);
    }
}

/// <summary>
/// Houdt per dag de slotstand van de portefeuille bij (<c>portefeuille-historiek.json</c>).
/// Anders dan het gereconstrueerde verloop bevat dit de échte stand van die dag, inclusief
/// bijkopen — de basis voor "hoogste stand ooit" en voor het verloop op langere termijn.
/// </summary>
public static class PortefeuilleHistoriek
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "portefeuille-historiek.json");

    /// <summary>Legt de stand van vandaag vast (de laatste meting van de dag wint).</summary>
    public static void Noteer(double totaal)
    {
        if (totaal <= 0)
        {
            return;
        }
        try
        {
            var punten = Laad();
            punten[DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd")] = Math.Round(totaal, 2);
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(punten,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: de historiek is een extraatje, geen voorwaarde.
        }
    }

    public static IReadOnlyList<(DateOnly Dag, double Waarde)> Punten()
    {
        var lijst = new List<(DateOnly, double)>();
        foreach (var (dag, waarde) in Laad())
        {
            if (DateOnly.TryParse(dag, CultureInfo.InvariantCulture, out var d))
            {
                lijst.Add((d, waarde));
            }
        }
        return lijst.OrderBy(p => p.Item1).ToList();
    }

    private static SortedDictionary<string, double> Laad()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<SortedDictionary<string, double>>(
                    File.ReadAllText(DataFile)) is { } bewaard)
            {
                return bewaard;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new SortedDictionary<string, double>(StringComparer.Ordinal);
    }
}
