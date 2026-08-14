using System.Text.Json;

namespace WorkManager;

/// <summary>Eén persoonlijke taak (todo) van de gebruiker.</summary>
public class MijnTaak
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Tekst { get; set; } = "";
    public string Categorie { get; set; } = "";
    /// <summary>0 = hoog, 1 = normaal, 2 = laag.</summary>
    public int Prioriteit { get; set; } = 1;
    public DateOnly? Deadline { get; set; }
    /// <summary>Dag waarop de taak pas relevant wordt; ervóór is er niks aan te doen en blijft hij verborgen.</summary>
    public DateOnly? Startdatum { get; set; }
    /// <summary>
    /// Vroegste uur waarop de taak kan (bv. bellen kan pas als de winkel open is). De taak
    /// blijft gewoon zichtbaar in de lijst; alleen de dagplanning plant hem niet eerder.
    /// </summary>
    public TimeOnly? StartUur { get; set; }
    public DateTimeOffset? SnoozeTot { get; set; }

    /// <summary>Hoe vaak deze taak vooruitgeschoven is (snooze of deadline naar later).</summary>
    public int UitstelTeller { get; set; }

    /// <summary>De uitstel-por ("in blokjes hakken?") is al één keer getoond.</summary>
    public bool UitstelPorGehad { get; set; }
    public bool Klaar { get; set; }
    public DateTimeOffset AangemaaktOp { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? KlaarOp { get; set; }

    /// <summary>Gesnoozed = tijdelijk verborgen tot het gekozen moment.</summary>
    public bool Gesnoozed => !Klaar && SnoozeTot is { } tot && tot > DateTimeOffset.Now;

    /// <summary>Startdatum ligt nog in de toekomst: taak nog niet relevant, standaard verborgen.</summary>
    public bool NogNietGestart =>
        !Klaar && Startdatum is { } start && start > DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Startuur vandaag nog niet bereikt ("bellen kan pas vanaf 14:00"): de taak is nu nog
    /// niet uitvoerbaar en blijft — net als bij een toekomstige startdatum — verborgen tot
    /// dat uur. Vanaf het startuur verschijnt hij vanzelf.
    /// </summary>
    public bool NogNietAanDeBeurt =>
        !Klaar && !NogNietGestart && StartUur is { } uur &&
        TimeOnly.FromDateTime(DateTime.Now) < uur;

    /// <summary>Het bericht waar deze taak uit voortkwam (indien via "Taak van bericht maken").</summary>
    public TaakMail? Mail { get; set; }
}

/// <summary>
/// Kopie van de mail/chat die aan een taak hangt, plus een link naar de bron en de
/// reply-gegevens zodat er rechtstreeks vanuit de taak geantwoord kan worden.
/// </summary>
public class TaakMail
{
    public string Van { get; set; } = "";
    public string VanAdres { get; set; } = "";
    public string AntwoordAan { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public string Tekst { get; set; } = "";
    public string Link { get; set; } = "";
    public DateTimeOffset Datum { get; set; }
    public string MessageId { get; set; } = "";
    public List<string> Referenties { get; set; } = new();
    public string ChatSpace { get; set; } = "";
    public string WhatsAppChat { get; set; } = "";
}

public class MijnTakenData
{
    public List<string> Categorieen { get; set; } = new();
    public List<MijnTaak> Taken { get; set; } = new();
}

/// <summary>
/// Opslag van de persoonlijke takenlijst in %APPDATA%\WorkManager\my-tasks.json.
/// Elke wijziging wordt meteen bewaard (zelfde aanpak als de teamtaken).
/// </summary>
public static class MijnTaakStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string TakenFile = Path.Combine(DataDir, "my-tasks.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static MijnTakenData Load()
    {
        try
        {
            if (File.Exists(TakenFile))
            {
                var data = JsonSerializer.Deserialize<MijnTakenData>(File.ReadAllText(TakenFile));
                if (data is not null)
                {
                    if (data.Categorieen.Count == 0)
                    {
                        data.Categorieen = StandaardCategorieen();
                    }
                    return data;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: met een lege lijst beginnen; bij de eerste wijziging
            // wordt het bestand opnieuw geschreven.
        }
        return new MijnTakenData { Categorieen = StandaardCategorieen() };
    }

    public static void Save(MijnTakenData data)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(TakenFile, JsonSerializer.Serialize(data, JsonOpts));
    }

    /// <summary>Open taken die vandaag of eerder gepland staan (voor de tray-herinnering).</summary>
    public static List<MijnTaak> AandachtVandaag()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return Load().Taken
            .Where(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt &&
                        t.Deadline is { } d && d <= vandaag)
            .ToList();
    }

    public static int OpenAantal() => Load().Taken
        .Count(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt);

    /// <summary>Laatste schrijftijd van het takenbestand (MinValue als het nog niet bestaat).</summary>
    public static DateTime BestandTijd() =>
        File.Exists(TakenFile) ? File.GetLastWriteTimeUtc(TakenFile) : DateTime.MinValue;

    private static List<string> StandaardCategorieen() =>
        new() { "CED", "Aqurat", "RadiologyPartners", "Lauryssens", "Urban IT", "Privé" };
}
