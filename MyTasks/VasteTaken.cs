using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Vaste terugkerende taken in "Mijn taken": woensdag "Facturen goedkeuren (ISPnext)",
/// vrijdag "Weekmail team klaarzetten" en op de laatste dag van de maand "Bermacon factuur opmaken"
/// (dubbelklik in de cockpit opent Billit). De tray-timer roept
/// <see cref="ZorgVoorWeektaken"/> periodiek aan; per dag wordt maar één exemplaar
/// aangemaakt (ook als de taak intussen afgevinkt en opgeruimd is). De weektaken worden
/// automatisch afgevinkt zodra de bijbehorende flow gelopen is.
/// </summary>
public static class VasteTaken
{
    public const string FacturenTaak = "Facturen goedkeuren (ISPnext)";
    public const string WeekmailTaak = "Weekmail team klaarzetten";
    public const string BermaconTaak = "Bermacon factuur opmaken";
    public const string BureaubladTaak = "Bureaublad opruimen";

    /// <summary>Vanaf zoveel losse bestanden op het bureaublad komt er een opruimtaak.</summary>
    private const int BureaubladDrempel = 20;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "vaste-taken.json");

    private sealed class State
    {
        public string FacturenAangemaakt { get; set; } = ""; // datum (yyyy-MM-dd) van de laatste aanmaak
        public string WeekmailAangemaakt { get; set; } = "";
        public string BermaconAangemaakt { get; set; } = "";
        public string BureaubladAangemaakt { get; set; } = "";
    }

    /// <summary>Maakt de vaste taak van vandaag aan als dat nog niet gebeurd is.</summary>
    public static void ZorgVoorWeektaken()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        switch (vandaag.DayOfWeek)
        {
            case DayOfWeek.Wednesday:
                MaakEensPerDag(vandaag, FacturenTaak, s => s.FacturenAangemaakt,
                    (s, d) => s.FacturenAangemaakt = d);
                break;
            case DayOfWeek.Friday:
                MaakEensPerDag(vandaag, WeekmailTaak, s => s.WeekmailAangemaakt,
                    (s, d) => s.WeekmailAangemaakt = d);
                break;
        }
        if (vandaag.Day == DateTime.DaysInMonth(vandaag.Year, vandaag.Month))
        {
            // Maandtaak: de Bermacon-factuur, op de laatste dag zodat hij nog binnen de
            // maand de deur uitgaat (dubbelklik opent Billit).
            MaakEensPerDag(vandaag, BermaconTaak, s => s.BermaconAangemaakt,
                (s, d) => s.BermaconAangemaakt = d);
        }
        ZorgVoorBureaubladTaak(vandaag);
    }

    /// <summary>Hoeveel losse bestanden staan er nu op het bureaublad? (-1 = niet leesbaar.)</summary>
    private static int BureaubladBestanden()
    {
        try
        {
            return Directory.EnumerateFiles(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
                .Count(p => !Path.GetFileName(p).StartsWith('.') &&
                            !p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return -1; // bureaublad niet leesbaar
        }
    }

    /// <summary>
    /// Houdt een openstaande opruimtaak gelijk met de werkelijkheid: het aantal in de tekst
    /// telt af terwijl je bestanden weggooit, en zodra het bureaublad onder de drempel zakt
    /// vinkt de taak zichzelf af. Anders bleef er "(24 bestanden)" staan op een bureaublad
    /// waar er nog vier op stonden, en moest je hem met de hand wegklikken.
    /// Wordt aangeroepen vanuit de periodieke ronde én zodra de opruimcleaner sluit.
    /// </summary>
    public static void WerkBureaubladTaakBij()
    {
        var aantal = BureaubladBestanden();
        if (aantal < 0)
        {
            return;
        }
        var data = MijnTaakStore.Load();
        var open = data.Taken.Where(t =>
            !t.Klaar && t.Tekst.StartsWith(BureaubladTaak, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (open.Count == 0)
        {
            return;
        }

        var gewijzigd = false;
        foreach (var taak in open)
        {
            if (aantal <= BureaubladDrempel)
            {
                taak.Klaar = true;
                taak.KlaarOp = DateTimeOffset.Now;
                gewijzigd = true;
                continue;
            }
            var nieuweTekst = $"{BureaubladTaak} ({aantal} bestanden)";
            if (taak.Tekst != nieuweTekst)
            {
                taak.Tekst = nieuweTekst;
                gewijzigd = true;
            }
        }
        if (gewijzigd)
        {
            MijnTaakStore.Save(data);
        }
    }

    /// <summary>
    /// Loopt het bureaublad vol, dan komt er een opruimtaak in de lijst (dubbelklik opent de
    /// bureaubladcleaner). Hooguit één per dag, en alleen zolang de drempel overschreden is.
    /// </summary>
    private static void ZorgVoorBureaubladTaak(DateOnly vandaag)
    {
        // Eerst een bestaande taak bijwerken of afvinken; pas daarna eventueel een nieuwe.
        WerkBureaubladTaakBij();
        var aantal = BureaubladBestanden();
        if (aantal <= BureaubladDrempel)
        {
            return;
        }
        MaakEensPerDag(vandaag, $"{BureaubladTaak} ({aantal} bestanden)",
            s => s.BureaubladAangemaakt, (s, d) => s.BureaubladAangemaakt = d,
            prioriteit: 2, bestaandePrefix: BureaubladTaak);
    }

    /// <summary>
    /// Vinkt de openstaande vaste taak af waarvan de tekst de zoekterm bevat (aangeroepen
    /// door de flow zelf, bv. na het versturen van de goedkeuring of de weekmail).
    /// </summary>
    public static void VinkAf(string zoekterm)
    {
        var data = MijnTaakStore.Load();
        var geraakt = false;
        foreach (var taak in data.Taken.Where(t =>
            !t.Klaar && t.Tekst.Contains(zoekterm, StringComparison.OrdinalIgnoreCase)))
        {
            taak.Klaar = true;
            taak.KlaarOp = DateTimeOffset.Now;
            geraakt = true;
        }
        if (geraakt)
        {
            MijnTaakStore.Save(data);
        }
    }

    /// <param name="prioriteit">0 = hoog (standaard: hoort dezelfde dag te gebeuren).</param>
    /// <param name="bestaandePrefix">
    /// Waarop gecontroleerd wordt of de taak al openstaat; standaard de volledige tekst. Handig
    /// als de tekst een wisselend deel bevat (bv. het aantal bestanden).
    /// </param>
    private static void MaakEensPerDag(
        DateOnly vandaag, string tekst, Func<State, string> lees, Action<State, string> schrijf,
        int prioriteit = 0, string? bestaandePrefix = null)
    {
        var datum = vandaag.ToString("yyyy-MM-dd");
        var state = LoadState();
        if (lees(state) == datum)
        {
            return;
        }

        var data = MijnTaakStore.Load();
        // Niet dubbel aanmaken als de taak (bv. handmatig) al open staat.
        var bestaat = bestaandePrefix is { } prefix
            ? data.Taken.Any(t => !t.Klaar && t.Tekst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            : data.Taken.Any(t => !t.Klaar && t.Tekst.Equals(tekst, StringComparison.OrdinalIgnoreCase));
        if (!bestaat)
        {
            data.Taken.Add(new MijnTaak
            {
                Tekst = tekst,
                Categorie = "Urban IT",
                Prioriteit = prioriteit,
                Deadline = vandaag,
            });
            MijnTaakStore.Save(data);
        }

        schrijf(state, datum);
        SaveState(state);
    }

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } state)
            {
                return state;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen; hooguit wordt een taak één keer extra aangemaakt.
        }
        return new State();
    }

    private static void SaveState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(
            state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
