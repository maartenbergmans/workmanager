using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Eén regel uit de portefeuille: zoveel stuks van één fonds, in één potje. De ticker is
/// die van Yahoo Finance (beurssuffix inbegrepen: .AS = Euronext Amsterdam, .DE = Xetra),
/// want daar komen de koersen vandaan.
/// </summary>
public sealed class Positie
{
    public string Ticker { get; set; } = "";

    /// <summary>Eigen naam; blijft leeg staan tot de koersbron de officiële naam meegeeft.</summary>
    public string Naam { get; set; } = "";

    public double Aantal { get; set; }

    /// <summary>Vrij label waarmee de posities gegroepeerd worden (privé, vennootschap, …).</summary>
    public string Potje { get; set; } = Portefeuille.PrivePotje;

    /// <summary>Gemiddelde aankoopkoers per stuk; leeg = geen rendement tonen.</summary>
    public double? AankoopKoers { get; set; }

    [JsonIgnore]
    public string Sleutel => $"{Potje}|{Ticker}";
}

/// <summary>
/// De portefeuille zoals ze op schijf staat (<c>portefeuille.json</c> in %APPDATA%\WorkManager):
/// de posities plus de weergavekeuzes die je over sessies heen wilt behouden. Bij de eerste
/// start worden Maartens eigen posities gezet; daarna is alles via "Posities…" te wijzigen.
/// </summary>
public sealed class Portefeuille
{
    public const string PrivePotje = "Privé";
    public const string BedrijfPotje = "BerMaCon";

    public List<Positie> Posities { get; set; } = new();

    /// <summary>Privacystand: bedragen worden als ••• getoond, percentages blijven zichtbaar.</summary>
    public bool BedragenVerborgen { get; set; }

    /// <summary>Laatst gekozen periode in de grafiek ("1M", "3M", "1J", "5J").</summary>
    public string Periode { get; set; } = "1J";

    /// <summary>Laatst gekozen potje in de filterbalk; leeg = alles samen.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Hoogste totaalwaarde die ooit gemeten is, en wanneer.</summary>
    public double HoogsteStand { get; set; }

    public DateTimeOffset? HoogsteStandOp { get; set; }

    /// <summary>De laatste mijlpaal (veelvoud van € 25.000) die al gevierd is.</summary>
    public double GevierdeMijlpaal { get; set; }

    /// <summary>Afgeleid uit de posities; hoort niet in het bestand thuis.</summary>
    [JsonIgnore]
    public IEnumerable<string> Potjes =>
        Posities.Select(p => p.Potje).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Leest en bewaart de portefeuille. Bewust zonder versleuteling: er staan geen
/// inloggegevens in, alleen aantallen — de discretie zit in de weergave, niet in het bestand.
/// </summary>
public static class PortefeuilleStore
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "portefeuille.json");

    private static readonly JsonSerializerOptions Opties = new() { WriteIndented = true };

    /// <summary>Wordt gemeld zodra posities of weergavekeuzes wijzigen (open vensters volgen).</summary>
    public static event Action? Gewijzigd;

    public static Portefeuille Laad()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<Portefeuille>(File.ReadAllText(DataFile)) is { } bewaard)
            {
                return bewaard;
            }
        }
        catch
        {
            // Onleesbaar bestand: met de standaardportefeuille verder, niets overschrijven
            // tot er echt iets bewaard wordt.
        }
        return Standaard();
    }

    public static void Bewaar(Portefeuille portefeuille)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(portefeuille, Opties));
        }
        catch
        {
            // Best effort: niet kunnen bewaren mag het venster niet breken.
        }
        Gewijzigd?.Invoke();
    }

    /// <summary>De posities zoals Maarten ze aangaf toen deze functie gebouwd werd.</summary>
    private static Portefeuille Standaard() => new()
    {
        Posities =
        {
            new Positie
            {
                Ticker = "IWDA.AS", Naam = "iShares Core MSCI World UCITS ETF",
                Aantal = 273, Potje = Portefeuille.PrivePotje,
            },
            new Positie
            {
                Ticker = "IS3Q.DE", Naam = "iShares Edge MSCI World Quality Factor UCITS ETF",
                Aantal = 500, Potje = Portefeuille.PrivePotje,
            },
            new Positie
            {
                Ticker = "IWDA.AS", Naam = "iShares Core MSCI World UCITS ETF",
                Aantal = 335, Potje = Portefeuille.BedrijfPotje,
            },
            new Positie
            {
                Ticker = "IS3Q.DE", Naam = "iShares Edge MSCI World Quality Factor UCITS ETF",
                Aantal = 481, Potje = Portefeuille.BedrijfPotje,
            },
        },
    };
}
