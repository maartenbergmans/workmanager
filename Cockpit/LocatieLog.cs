using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Waar je was, en hoe lang. Twee bronnen, met een heel verschillend karakter:
///
/// <list type="bullet">
/// <item>de webpagina stuurt een grove positie zodra je haar opent — dat kost geen batterij
/// (wifi/zendmast, geen GPS), maar het gebeurt alleen als je toevallig kijkt;</item>
/// <item>een iOS-automatisering ("wanneer ik aankom bij Lauryssens") stuurt aankomst en
/// vertrek vanzelf door. Dat doet het besturingssysteem met de locatiedienst die toch al
/// draait, dus ook dat merk je niet aan je batterij — en je hoeft niets te openen.</item>
/// </list>
///
/// <para>Van een aankomst plus vertrek maakt dit een bezoek met een duur, en dat wordt een
/// voorstelregel voor je timesheets. Nooit meteen een boeking: je keurt het zelf goed.</para>
///
/// <para>Opslag: %APPDATA%\WorkManager\locatie-log.json.</para>
/// </summary>
public static class LocatieLog
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "locatie-log.json");

    /// <summary>Binnen zoveel meter van een bekende plek geldt "je bent daar".</summary>
    private const int PlekStraalMeter = 250;

    /// <summary>Een positie ouder dan dit zegt niets meer over waar je nú bent.</summary>
    private static readonly TimeSpan HierGeldig = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Plekken die nooit een timesheetregel opleveren. Thuis is er zo één: dat "bezoek"
    /// loopt van 's avonds tot 's ochtends en zou anders een voorstel van twaalf uur worden.
    /// Wel nuttig om te registreren — vertrek thuis markeert het begin van je werkdag.
    /// </summary>
    private static readonly string[] GeenWerkPlekken = { "thuis", "home", "huis" };

    /// <summary>Langer dan dit is geen klantbezoek maar een vergeten vertrekmelding.</summary>
    private const int MaxBezoekMinuten = 10 * 60;

    /// <summary>Een gat tussen vertrek en aankomst binnen deze grenzen telt als reistijd.</summary>
    private const int MinReisMinuten = 5;
    private const int MaxReisMinuten = 3 * 60;

    public sealed class Plek
    {
        public string Naam { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    public sealed class Punt
    {
        public DateTimeOffset Moment { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Plek { get; set; } = "";
    }

    public sealed class Bezoek
    {
        public string Plek { get; set; } = "";
        public DateTimeOffset Aankomst { get; set; }
        public DateTimeOffset? Vertrek { get; set; }
        /// <summary>Er is al een voorstelregel van gemaakt.</summary>
        public bool Verwerkt { get; set; }
        /// <summary>De reistijd naar dit bezoek toe is al beoordeeld.</summary>
        public bool ReisVerwerkt { get; set; }

        public int Minuten => Vertrek is { } v ? Math.Max(0, (int)(v - Aankomst).TotalMinutes) : 0;
    }

    public sealed class Data
    {
        public List<Plek> Plekken { get; set; } = new();
        public List<Punt> Punten { get; set; } = new();
        public List<Bezoek> Bezoeken { get; set; } = new();
        /// <summary>Coördinaten waarvoor Maarten géén naam wil (Naam blijft leeg).</summary>
        public List<Plek> Genegeerd { get; set; } = new();
    }

    public static Data Laad()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<Data>(File.ReadAllText(Bestand)) is { } d)
            {
                return d;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen; het zijn hulpgegevens, geen administratie.
        }
        return new Data();
    }

    public static void Bewaar(Data data)
    {
        try
        {
            // Niet eeuwig laten groeien: een week is genoeg voor de timesheets.
            var grens = DateTimeOffset.Now.AddDays(-7);
            data.Punten = data.Punten.Where(p => p.Moment > grens).TakeLast(500).ToList();
            data.Bezoeken = data.Bezoeken.Where(b => b.Aankomst > grens).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Verwerkt wat er van de gsm binnenkwam. Punten worden aan een bekende plek gekoppeld;
    /// aankomst/vertrek worden bezoeken. Geeft de bezoeken terug die deze beurt afgesloten
    /// werden (minstens 10 minuten) — de beller maakt er meldingen van.
    /// </summary>
    public static List<Bezoek> Verwerk(IEnumerable<JsonElement> rijen)
    {
        var data = Laad();
        var afgesloten = new List<Bezoek>();

        foreach (var rij in rijen)
        {
            var soort = rij.TryGetProperty("soort", out var s) ? s.GetString() ?? "punt" : "punt";
            var moment = Moment(rij);
            switch (soort)
            {
                case "punt":
                {
                    if (!Getal(rij, "lat", out var lat) || !Getal(rij, "lon", out var lon))
                    {
                        continue;
                    }
                    data.Punten.Add(new Punt
                    {
                        Moment = moment, Lat = lat, Lon = lon,
                        Plek = PlekBij(data, lat, lon),
                    });
                    break;
                }

                case "aankomst":
                {
                    var plek = Tekst(rij, "plek");
                    if (plek.Length == 0)
                    {
                        continue;
                    }
                    // Een tweede aankomst zonder vertrek: de eerste telt (de automatisering
                    // kan bij het rondrijden op een terrein meerdere keren afgaan). Maar een
                    // bezoek dat al langer openstaat dan een bezoek kán duren is een vergeten
                    // vertrekmelding — die mag een nieuwe aankomst niet blijven blokkeren.
                    if (data.Bezoeken.LastOrDefault(b => b.Plek == plek && b.Vertrek is null)
                        is { } open)
                    {
                        if ((moment - open.Aankomst).TotalMinutes <= MaxBezoekMinuten)
                        {
                            break; // zelfde bezoek, niets te doen
                        }
                        open.Vertrek = open.Aankomst; // geen bruikbare duur, wel afgesloten
                        open.Verwerkt = true;
                    }
                    data.Bezoeken.Add(new Bezoek { Plek = plek, Aankomst = moment });
                    break;
                }

                case "vertrek":
                {
                    var plek = Tekst(rij, "plek");
                    if (data.Bezoeken.LastOrDefault(b => b.Plek == plek && b.Vertrek is null)
                        is not { } lopend)
                    {
                        continue;
                    }
                    lopend.Vertrek = moment;
                    if (lopend.Minuten >= 10)
                    {
                        afgesloten.Add(lopend);
                    }
                    break;
                }
            }
        }

        Bewaar(data);
        return afgesloten;
    }

    /// <summary>
    /// Afgeronde bezoeken van vandaag die nog geen voorstelregel opleverden, omgezet naar
    /// timesheetregels. Ze komen in het voorstel, niet in de boekingen.
    /// </summary>
    public static int ZetBezoekenInVoorstel()
    {
        var data = Laad();
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var klaar = data.Bezoeken
            .Where(b => !b.Verwerkt && b.Vertrek is not null && b.Minuten >= 15 &&
                        b.Minuten <= MaxBezoekMinuten && !IsGeenWerk(b.Plek) &&
                        DateOnly.FromDateTime(b.Aankomst.LocalDateTime) == vandaag)
            .ToList();
        // Thuis en te lange bezoeken tellen wel als bezoek (je ziet ze onder "Waar je was"),
        // maar leveren geen voorstelregel op. Wel afvinken, anders blijven ze aankloppen.
        foreach (var overgeslagen in data.Bezoeken.Where(b =>
                     !b.Verwerkt && b.Vertrek is not null &&
                     (IsGeenWerk(b.Plek) || b.Minuten < 15 || b.Minuten > MaxBezoekMinuten)))
        {
            overgeslagen.Verwerkt = true;
        }

        var voorstel = VoorstelStore.Laad();
        var voorAantal = voorstel.Count;
        // Reistijd: het gat tussen een vertrek en de eerstvolgende aankomst wordt een eigen
        // voorstelregel, zolang minstens één van de twee plekken een werkplek is (de klant
        // van de rit). Thuis–thuis of een gat van uren is geen rit.
        foreach (var bezoek in data.Bezoeken.Where(b => !b.ReisVerwerkt &&
                     DateOnly.FromDateTime(b.Aankomst.LocalDateTime) == vandaag))
        {
            bezoek.ReisVerwerkt = true;
            var vorige = data.Bezoeken
                .Where(v => v != bezoek && v.Vertrek is { } vv && vv <= bezoek.Aankomst)
                .OrderByDescending(v => v.Vertrek)
                .FirstOrDefault();
            if (vorige?.Vertrek is not { } vertrek)
            {
                continue;
            }
            var reisMinuten = (int)(bezoek.Aankomst - vertrek).TotalMinutes;
            var werkPlek = !IsGeenWerk(bezoek.Plek) ? bezoek.Plek
                : !IsGeenWerk(vorige.Plek) ? vorige.Plek : "";
            if (reisMinuten < MinReisMinuten || reisMinuten > MaxReisMinuten ||
                werkPlek.Length == 0)
            {
                continue;
            }
            voorstel.Add(new TimesheetRegel
            {
                Datum = vandaag,
                Van = TimeOnly.FromDateTime(vertrek.LocalDateTime),
                Klant = KlantVoor(werkPlek),
                Minuten = Math.Clamp((int)Math.Ceiling(reisMinuten / 15.0) * 15, 15, MaxReisMinuten),
                Omschrijving = $"Verplaatsing {vorige.Plek} → {bezoek.Plek}",
                Bron = "locatie",
            });
        }
        foreach (var bezoek in klaar)
        {
            voorstel.Add(new TimesheetRegel
            {
                Datum = vandaag,
                Van = TimeOnly.FromDateTime(bezoek.Aankomst.LocalDateTime),
                Klant = KlantVoor(bezoek.Plek),
                // Naar boven op een kwartier: zo werk je een timesheet ook echt bij.
                Minuten = Math.Clamp((int)Math.Ceiling(bezoek.Minuten / 15.0) * 15, 15, 720),
                Omschrijving = $"Ter plaatse bij {bezoek.Plek}",
                Bron = "locatie",
            });
            bezoek.Verwerkt = true;
        }
        if (voorstel.Count > voorAantal)
        {
            VoorstelStore.Bewaar(voorstel);
        }
        Bewaar(data);
        return klaar.Count;
    }

    /// <summary>Waar je nu bent, voor zover we dat weten (leeg = geen recente positie).</summary>
    public static string Hier()
    {
        var data = Laad();
        // Een lopend bezoek uit de iOS-automatisering weet het zeker; dat gaat voor.
        if (data.Bezoeken.LastOrDefault(b => b.Vertrek is null) is { } lopend &&
            DateTimeOffset.Now - lopend.Aankomst < TimeSpan.FromHours(12))
        {
            return $"{lopend.Plek} (sinds {lopend.Aankomst.LocalDateTime:HH:mm})";
        }
        if (data.Punten.LastOrDefault() is { } laatste &&
            DateTimeOffset.Now - laatste.Moment < HierGeldig)
        {
            return laatste.Plek.Length > 0
                ? laatste.Plek
                : $"onbekende plek ({laatste.Moment.LocalDateTime:HH:mm})";
        }
        return "";
    }

    /// <summary>De laatst gemeten positie, om er een plek van te kunnen maken.</summary>
    public static Punt? LaatstePunt() =>
        Laad().Punten.LastOrDefault(p => DateTimeOffset.Now - p.Moment < HierGeldig);

    /// <summary>Onthoudt de laatst gemeten positie onder een naam.</summary>
    public static string BewaarPlek(string naam)
    {
        naam = naam.Trim();
        if (naam.Length == 0)
        {
            return "Geef de plek een naam.";
        }
        var data = Laad();
        if (data.Punten.LastOrDefault(p => DateTimeOffset.Now - p.Moment < HierGeldig)
            is not { } punt)
        {
            return "Geen recente positie — open de pagina op de plek zelf.";
        }
        data.Plekken.RemoveAll(p => p.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase));
        data.Plekken.Add(new Plek { Naam = naam, Lat = punt.Lat, Lon = punt.Lon });
        punt.Plek = naam;
        Bewaar(data);
        return $"\"{naam}\" onthouden — voortaan herkend als je hier bent.";
    }

    public static List<string> Plekken() => Laad().Plekken.Select(p => p.Naam).ToList();

    /// <summary>
    /// Coördinaten die de laatste week minstens drie keer terugkwamen zonder naam en niet
    /// bij een bekende (of bewust genegeerde) plek horen: een kandidaat om te benoemen.
    /// </summary>
    public static (double Lat, double Lon, int Aantal)? NaamloosCluster()
    {
        var data = Laad();
        var naamloos = data.Punten.Where(p => p.Plek.Length == 0).ToList();
        foreach (var punt in naamloos)
        {
            var buurt = naamloos
                .Where(q => MeterTussen(punt.Lat, punt.Lon, q.Lat, q.Lon) <= PlekStraalMeter)
                .ToList();
            if (buurt.Count >= 3 &&
                !data.Plekken.Concat(data.Genegeerd).Any(p =>
                    MeterTussen(punt.Lat, punt.Lon, p.Lat, p.Lon) <= PlekStraalMeter))
            {
                return (buurt.Average(b => b.Lat), buurt.Average(b => b.Lon), buurt.Count);
            }
        }
        return null;
    }

    /// <summary>Geeft een cluster een naam; bestaande naamloze punten erbinnen ook meteen.</summary>
    public static void BenoemCluster(double lat, double lon, string naam)
    {
        naam = naam.Trim();
        if (naam.Length == 0)
        {
            return;
        }
        var data = Laad();
        data.Plekken.RemoveAll(p => p.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase));
        data.Plekken.Add(new Plek { Naam = naam, Lat = lat, Lon = lon });
        foreach (var punt in data.Punten.Where(p => p.Plek.Length == 0 &&
                     MeterTussen(p.Lat, p.Lon, lat, lon) <= PlekStraalMeter))
        {
            punt.Plek = naam;
        }
        Bewaar(data);
    }

    /// <summary>Deze plek nooit meer voorstellen.</summary>
    public static void NegeerCluster(double lat, double lon)
    {
        var data = Laad();
        data.Genegeerd.Add(new Plek { Lat = lat, Lon = lon });
        Bewaar(data);
    }

    /// <summary>Bezoeken van vandaag, voor de webversie.</summary>
    public static List<Bezoek> Vandaag()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return Laad().Bezoeken
            .Where(b => DateOnly.FromDateTime(b.Aankomst.LocalDateTime) == vandaag)
            .OrderBy(b => b.Aankomst)
            .ToList();
    }

    /// <summary>De naam van de bekende plek bij deze coördinaten, of leeg.</summary>
    private static string PlekBij(Data data, double lat, double lon) =>
        data.Plekken
            .Select(p => (p.Naam, Afstand: MeterTussen(lat, lon, p.Lat, p.Lon)))
            .Where(x => x.Afstand <= PlekStraalMeter)
            .OrderBy(x => x.Afstand)
            .Select(x => x.Naam)
            .FirstOrDefault() ?? "";

    /// <summary>
    /// Afstand in meter (haversine). Op deze schaal — een paar honderd meter — hoeft dat niet
    /// exacter dan dit.
    /// </summary>
    private static double MeterTussen(double lat1, double lon1, double lat2, double lon2)
    {
        const double straal = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return straal * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Is dit een plek waar je niet voor werkt (thuis)?</summary>
    public static bool IsGeenWerk(string plek) =>
        GeenWerkPlekken.Any(p => plek.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Wanneer je vandaag van huis vertrok — het begin van je werkdag, handig als ijkpunt
    /// bij het aanvullen van je uren. Null als er vandaag geen vertrek thuis geregistreerd is.
    /// </summary>
    public static DateTimeOffset? VertrokkenVanHuis()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return Laad().Bezoeken
            .Where(b => IsGeenWerk(b.Plek) && b.Vertrek is { } v &&
                        DateOnly.FromDateTime(v.LocalDateTime) == vandaag)
            .Select(b => b.Vertrek)
            .OrderBy(v => v)
            .FirstOrDefault();
    }

    /// <summary>De timesheetklant die bij een plek hoort (anders: niet factureerbaar).</summary>
    private static string KlantVoor(string plek) =>
        TimesheetStore.Klanten.FirstOrDefault(k =>
            plek.Contains(k.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ??
        "Niet factureerbaar";

    /// <summary>
    /// De server levert ISO-8601 mét tijdzone (zie wm.php). Zelf gokken of een kale
    /// "2026-08-10 04:53:11" nu UTC of lokale tijd is, zette een bezoek twee uur verkeerd —
    /// en daarmee ook de timesheetregel.
    /// </summary>
    private static DateTimeOffset Moment(JsonElement rij) =>
        rij.TryGetProperty("moment", out var m) && m.GetString() is { } tekst &&
        DateTimeOffset.TryParse(tekst, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var moment)
            ? moment.ToLocalTime()
            : DateTimeOffset.Now;

    private static string Tekst(JsonElement rij, string naam) =>
        rij.TryGetProperty(naam, out var el) ? el.GetString() ?? "" : "";

    private static bool Getal(JsonElement rij, string naam, out double waarde)
    {
        waarde = 0;
        if (!rij.TryGetProperty(naam, out var el))
        {
            return false;
        }
        // MySQL levert DOUBLE via PDO als string terug.
        return el.ValueKind == JsonValueKind.Number
            ? el.TryGetDouble(out waarde)
            : double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out waarde);
    }
}
