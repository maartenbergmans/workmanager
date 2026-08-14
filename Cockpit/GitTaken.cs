using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Het doel is een schone werkkopie in elk project. Is de takenlijst op een werkdag rustig, dan
/// is dat het moment om achterstallig werk op te ruimen: deze radar zet er één taak voor klaar
/// met de projecten die openstaan. Bewust terughoudend — hooguit één keer per week, nooit in
/// het weekend, en alleen voor wijzigingen die al langer dan een week blijven liggen. Wat je
/// gisteren aanpaste is geen achterstand.
/// </summary>
public static class GitTaken
{
    public const string TaakPrefix = "Ongecommitte wijzigingen nakijken";

    /// <summary>Bij meer open taken dan dit is er genoeg te doen en zwijgt de radar.</summary>
    private const int RustigVanaf = 3;

    /// <summary>Zo lang moet een wijziging blijven liggen voor ze meetelt als achterstand.</summary>
    private const int OudNaDagen = 7;

    /// <summary>Minstens zoveel dagen tussen twee opruimtaken.</summary>
    private const int MinstensOmDeDagen = 7;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "git-taken.json");

    private const string WslBasis = @"\\wsl.localhost\Ubuntu\home\maarten\projecten\";

    /// <summary>De projectmappen die meegenomen worden (zelfde set als de dev-menu's).</summary>
    public static readonly string[] Projecten =
    {
        WslBasis + "aqurat",
        WslBasis + "bloom-datawarehouse",
        WslBasis + "movaware-backend",
        WslBasis + "movaware-frontend",
        WslBasis + "cellaware-backend",
        WslBasis + "cellaware-frontend",
        @"C:\Data\Projecten\BloomDataUploader",
    };

    private static bool _bezig;

    /// <summary>
    /// Zet de opruimtaak klaar als de takenlijst rustig is. Draait de git-status op de
    /// achtergrond (elk project kost ongeveer een seconde) en doet niets als er niets te
    /// melden valt.
    /// </summary>
    /// <returns>True als er een taak bijgekomen is (dan mag de lijst ververst worden).</returns>
    public static async Task<bool> ZorgVoorAsync(int openTaken, CancellationToken ct)
    {
        if (_bezig || openTaken > RustigVanaf)
        {
            return false;
        }
        var nu = DateTime.Now;
        // Opruimwerk is werkwerk: in het weekend zwijgt de radar.
        if (nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }
        var vandaag = DateOnly.FromDateTime(nu);
        if (DateOnly.TryParse(LaatsteDag(), out var vorige) &&
            vandaag < vorige.AddDays(MinstensOmDeDagen))
        {
            return false; // deze week al gehad
        }
        _bezig = true;
        try
        {
            BewaarDag(vandaag.ToString("yyyy-MM-dd")); // ook bij een mislukking: pas volgende week opnieuw

            var grens = nu.AddDays(-OudNaDagen);
            var vuil = new List<(string Naam, int Aantal)>();
            foreach (var map in Projecten)
            {
                ct.ThrowIfCancellationRequested();
                var rapport = await GitStatus.OphalenAsync(map, ct);
                if (rapport.Fout is not null || rapport.Aantal == 0)
                {
                    continue;
                }
                var oud = OudeWijzigingen(rapport, map, grens);
                if (oud > 0)
                {
                    vuil.Add((map.TrimEnd('\\', '/').Split('\\', '/').Last(), oud));
                }
            }
            if (vuil.Count == 0)
            {
                return false; // niets dat al een week blijft liggen
            }

            var samenvatting = string.Join(", ", vuil
                .OrderByDescending(v => v.Aantal)
                .Select(v => $"{v.Naam} {v.Aantal}"));
            var tekst = $"{TaakPrefix} ({samenvatting} — ouder dan een week)";

            var data = MijnTaakStore.Load();
            // Een oudere versie van deze taak vervangen: de aantallen kloppen dan niet meer.
            data.Taken.RemoveAll(t => !t.Klaar &&
                t.Tekst.StartsWith(TaakPrefix, StringComparison.OrdinalIgnoreCase));
            data.Taken.Add(new MijnTaak
            {
                Tekst = tekst,
                Categorie = "Urban IT",
                Prioriteit = 2, // laag: dit is opruimwerk voor een rustig moment
                Deadline = DateOnly.FromDateTime(DateTime.Now),
            });
            MijnTaakStore.Save(data);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het scannen.
        }
        catch
        {
            // Git of WSL niet beschikbaar: morgen opnieuw.
        }
        finally
        {
            _bezig = false;
        }
        return false;
    }

    /// <summary>
    /// Hoeveel van de ongecommitte wijzigingen al vóór <paramref name="grens"/> voor het laatst
    /// aangeraakt zijn. Verwijderde bestanden tellen niet mee (die hebben geen tijdstempel meer);
    /// bij een hele map (untracked directory) telt het nieuwste bestand erin.
    /// </summary>
    private static int OudeWijzigingen(GitStatus.Rapport rapport, string map, DateTime grens)
    {
        var oud = 0;
        foreach (var w in rapport.Wijzigingen)
        {
            try
            {
                var pad = Path.Combine(map, w.Pad.Replace('/', '\\'));
                DateTime tijd;
                if (File.Exists(pad))
                {
                    tijd = File.GetLastWriteTime(pad);
                }
                else if (Directory.Exists(pad))
                {
                    tijd = NieuwsteIn(pad);
                }
                else
                {
                    continue; // verwijderd of onbereikbaar
                }
                if (tijd <= grens)
                {
                    oud++;
                }
            }
            catch
            {
                // Onleesbaar pad: dan telt het gewoon niet mee.
            }
        }
        return oud;
    }

    private static DateTime NieuwsteIn(string map)
    {
        try
        {
            var bestanden = Directory.EnumerateFiles(map, "*", SearchOption.AllDirectories)
                .Take(200) // grote mappen niet volledig aflopen
                .Select(File.GetLastWriteTime)
                .ToList();
            return bestanden.Count > 0 ? bestanden.Max() : Directory.GetLastWriteTime(map);
        }
        catch
        {
            return Directory.GetLastWriteTime(map);
        }
    }

    /// <summary>Het project met de meeste wijzigingen uit een taaktekst, om er meteen op te klikken.</summary>
    public static string? EersteProjectUit(string taakTekst)
    {
        foreach (var map in Projecten)
        {
            var naam = map.TrimEnd('\\', '/').Split('\\', '/').Last();
            if (taakTekst.Contains(naam, StringComparison.OrdinalIgnoreCase))
            {
                return map;
            }
        }
        return null;
    }

    private static string LaatsteDag()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                return JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) ?? "";
            }
        }
        catch
        {
            // Onleesbaar: als "nog niet" behandelen.
        }
        return "";
    }

    private static void BewaarDag(string dag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(dag));
        }
        catch
        {
            // Best effort.
        }
    }
}
