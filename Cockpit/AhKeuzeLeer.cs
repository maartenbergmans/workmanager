using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Leert de Standaard-vlag van de AH-ingrediënten uit het afvinkgedrag in de keuzestap.
/// Vink je een product dat standaard aan staat twee bestellingen op rij af, dan stelt de app
/// voor het voortaan standaard uit te zetten (en omgekeerd: twee keer op rij aangevinkt
/// terwijl het standaard uit staat → voorstel om het aan te zetten). De tellers staan in
/// %APPDATA%\WorkManager\ah-keuze-leer.json; een gedane suggestie zet de teller terug op nul,
/// óók als Maarten ze afwijst — anders zou dezelfde vraag elke bestelling terugkomen.
/// </summary>
public static class AhKeuzeLeer
{
    /// <summary>Zoveel bestellingen op rij hetzelfde afwijkende gedrag → voorstel.</summary>
    private const int Drempel = 2;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-keuze-leer.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Teller per ingrediënt: hoe vaak op rij het van zijn Standaard-vlag afweek.</summary>
    private sealed class Teller
    {
        public int AfgevinktOpRij { get; set; }
        public int AangevinktOpRij { get; set; }
    }

    /// <summary>Voorstel om de Standaard-vlag van een ingrediënt om te zetten.</summary>
    public sealed record Voorstel(string Naam, bool NieuwStandaard);

    /// <summary>
    /// Verwerkt het afvinkgedrag van één bestelling (per regel uit de keuzestap: naam, de
    /// Standaard-vlag waarmee hij getoond werd en of hij uiteindelijk aangevinkt bleef) en
    /// geeft de omzet-voorstellen terug die daardoor de drempel halen.
    /// </summary>
    public static List<Voorstel> Verwerk(IEnumerable<(string Naam, bool Standaard, bool Aangevinkt)> keuzes)
    {
        var tellers = Laad();
        var voorstellen = new List<Voorstel>();
        foreach (var (naam, standaard, aangevinkt) in keuzes)
        {
            var sleutel = naam.Trim().ToLowerInvariant();
            if (sleutel.Length == 0)
            {
                continue;
            }
            if (aangevinkt == standaard)
            {
                // Gedrag volgt de vlag: geen afwijking meer, teller weg.
                tellers.Remove(sleutel);
                continue;
            }
            if (!tellers.TryGetValue(sleutel, out var teller))
            {
                tellers[sleutel] = teller = new Teller();
            }
            if (standaard)
            {
                teller.AfgevinktOpRij++;
                teller.AangevinktOpRij = 0;
                if (teller.AfgevinktOpRij >= Drempel)
                {
                    voorstellen.Add(new Voorstel(naam, NieuwStandaard: false));
                    tellers.Remove(sleutel); // één keer vragen, daarna opnieuw tellen
                }
            }
            else
            {
                teller.AangevinktOpRij++;
                teller.AfgevinktOpRij = 0;
                if (teller.AangevinktOpRij >= Drempel)
                {
                    voorstellen.Add(new Voorstel(naam, NieuwStandaard: true));
                    tellers.Remove(sleutel);
                }
            }
        }
        Bewaar(tellers);
        return voorstellen;
    }

    private static Dictionary<string, Teller> Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Dictionary<string, Teller>>(
                    File.ReadAllText(StateFile), JsonOpts) is { } tellers)
            {
                return tellers;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen met tellen.
        }
        return new Dictionary<string, Teller>();
    }

    private static void Bewaar(Dictionary<string, Teller> tellers)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(tellers, JsonOpts));
        }
        catch
        {
            // Best effort: leren is een extraatje, geen voorwaarde om te bestellen.
        }
    }
}
