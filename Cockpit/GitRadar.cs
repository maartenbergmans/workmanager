namespace WorkManager;

/// <summary>
/// De git-radar: één plek die weet welke repo's er op deze pc staan en hoe ze ervoor staan.
/// Hij vindt de repo's zélf (één niveau diep onder de projectmappen, plus een vaste lijst als
/// achtervang), peilt ze één keer per uur en bewaart het volledige beeld — branch, aantal
/// ongecommit, staged, voor/achter op de remote en per bestand hoe lang het al openstaat — in
/// <see cref="GitStatusCache"/>.
///
/// <para>Alles wat met git te maken heeft leest uit diezelfde cache: de labels in het
/// Projecten-menu, het git-overzichtsvenster, de teller in de cockpitwerkbalk en het uurlijkse
/// snapshot dat naar de online tabel gaat. Zo wordt er nooit twee keer gescand en zien al die
/// plekken altijd hetzelfde.</para>
/// </summary>
public static class GitRadar
{
    /// <summary>Hoe vaak de volledige ronde draait (en dus ook hoe vaak de online tabel bijkomt).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Hoe lang de ontdekte repo-lijst meegaat voor er opnieuw gezocht wordt.</summary>
    private static readonly TimeSpan OntdekkingHoudbaar = TimeSpan.FromHours(24);

    private const string WslProjecten = @"\\wsl.localhost\Ubuntu\home\maarten\projecten";

    /// <summary>
    /// Mappen waarin naar repo's gezocht wordt. Eén niveau diep (een map die zelf een repo is,
    /// wordt niet verder afgedaald) — zo komen ook projecten mee die pas vandaag gekloond zijn,
    /// zonder dat er hier een lijst bijgehouden moet worden.
    /// </summary>
    private static readonly string[] Zoekmappen =
    {
        WslProjecten,
        @"C:\Data\Projecten",
    };

    /// <summary>
    /// Achtervang: deze repo's horen er altijd bij, ook als het zoeken mislukt (WSL niet
    /// gestart, netwerkmap even weg). Het zijn de projecten waar dagelijks in gewerkt wordt.
    /// </summary>
    private static readonly string[] Vast =
    {
        WslProjecten + @"\aqurat",
        WslProjecten + @"\bloom-datawarehouse",
        WslProjecten + @"\movaware-backend",
        WslProjecten + @"\movaware-frontend",
        WslProjecten + @"\cellaware-backend",
        WslProjecten + @"\cellaware-frontend",
        WslProjecten + @"\totalloss-cednl-backend",
        WslProjecten + @"\totalloss-cednl-frontend",
        WslProjecten + @"\urbanadmin",
        // WorkManager zelf: daar bleven wijzigingen van meerdere sessies het langst liggen.
        @"C:\Data\Projecten\Workmanager",
        @"C:\Data\Projecten\BloomDataUploader",
    };

    private static GitStatusCache.Data? _cache;

    /// <summary>
    /// De gedeelde cache. Eén exemplaar voor de hele app: wie hem bijwerkt, werkt hem voor
    /// iedereen bij (cockpitmenu, overzichtsvenster, online snapshot).
    /// </summary>
    public static GitStatusCache.Data Cache => _cache ??= GitStatusCache.Load();

    /// <summary>Elke repo die gevolgd wordt, op naam gesorteerd. Genegeerde repo's zitten er niet in.</summary>
    public static IReadOnlyList<string> Repos
    {
        get
        {
            var genegeerd = new HashSet<string>(Cache.Genegeerd, StringComparer.OrdinalIgnoreCase);
            return Vast.Concat(Cache.Repos)
                .Where(m => !genegeerd.Contains(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Naam, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Vuurt zodra een ronde (of één repo) de cache bijgewerkt heeft.</summary>
    public static event Action? Bijgewerkt;

    private static bool _bezig;

    /// <summary>Loopt er een ronde? Dan hoeft een tweede aanroep niets te doen.</summary>
    public static bool Bezig => _bezig;

    /// <summary>Is de laatste ronde langer dan <see cref="Interval"/> geleden?</summary>
    public static bool Verouderd =>
        DateTimeOffset.Now - Cache.LaatsteControle >= Interval;

    /// <summary>
    /// Zorgt dat er een verse ronde gedraaid is: doet niets als de vorige nog jong is.
    /// Hiermee liften de cockpitpoll en de uurlijkse webupload op dezelfde scan mee.
    /// </summary>
    public static async Task<bool> ZorgVersAsync(CancellationToken ct, TimeSpan? maxOud = null)
    {
        if (DateTimeOffset.Now - Cache.LaatsteControle < (maxOud ?? Interval))
        {
            return false;
        }
        return await ScanAsync(ct);
    }

    /// <summary>
    /// Eén volledige ronde: repo's opzoeken (ten hoogste één keer per dag) en ze allemaal
    /// peilen. Sequentieel — een git-call in WSL kost bijna een seconde en tien parallelle
    /// fetches maken de pc alleen maar traag. Best effort: een repo die niet antwoordt komt
    /// met een foutmelding in de cache te staan.
    /// </summary>
    /// <param name="voortgang">Krijgt de naam van de repo die net gepeild wordt.</param>
    /// <returns>True als de ronde gedraaid heeft (false = er liep er al een).</returns>
    public static async Task<bool> ScanAsync(CancellationToken ct, Action<string>? voortgang = null)
    {
        if (_bezig)
        {
            return false;
        }
        _bezig = true;
        try
        {
            if (DateTimeOffset.Now - Cache.LaatsteOntdekking >= OntdekkingHoudbaar)
            {
                await OntdekAsync(ct);
            }
            foreach (var map in Repos)
            {
                ct.ThrowIfCancellationRequested();
                voortgang?.Invoke(Naam(map));
                var rapport = await GitStatus.OphalenAsync(map, ct);
                Cache.PerMap[map] = StandUit(map, rapport);
            }
            // Repo's die niet meer gevolgd worden (verwijderd of genegeerd) mogen niet als
            // spookregel in het overzicht en de online tabel blijven staan.
            var bekend = new HashSet<string>(Repos, StringComparer.OrdinalIgnoreCase);
            foreach (var weg in Cache.PerMap.Keys.Where(k => !bekend.Contains(k)).ToList())
            {
                Cache.PerMap.Remove(weg);
            }
            Cache.LaatsteControle = DateTimeOffset.Now;
            GitStatusCache.Save(Cache);
            Bijgewerkt?.Invoke();
            return true;
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Peilt één repo opnieuw en werkt de cache bij — na een commit of een pull, zodat het
    /// overzicht meteen klopt zonder de hele ronde over te doen.
    /// </summary>
    public static async Task VerversAsync(string map, CancellationToken ct, bool fetchen = false)
    {
        var rapport = await GitStatus.OphalenAsync(map, ct, fetchen);
        Cache.PerMap[map] = StandUit(map, rapport);
        GitStatusCache.Save(Cache);
        Bijgewerkt?.Invoke();
    }

    /// <summary>Haalt een repo uit het beeld (overzicht, teller, online tabel).</summary>
    public static void Negeer(string map)
    {
        if (!Cache.Genegeerd.Contains(map, StringComparer.OrdinalIgnoreCase))
        {
            Cache.Genegeerd.Add(map);
        }
        Cache.PerMap.Remove(map);
        GitStatusCache.Save(Cache);
        Bijgewerkt?.Invoke();
    }

    /// <summary>Volgt een eerder genegeerde repo weer.</summary>
    public static void VolgWeer(string map)
    {
        Cache.Genegeerd.RemoveAll(m => string.Equals(m, map, StringComparison.OrdinalIgnoreCase));
        GitStatusCache.Save(Cache);
        Bijgewerkt?.Invoke();
    }

    /// <summary>Alle standen in beeld, drukste repo eerst (en de schone onderaan).</summary>
    public static IEnumerable<KeyValuePair<string, GitStatusCache.Stand>> Standen() =>
        Repos
            .Where(Cache.PerMap.ContainsKey)
            .Select(m => new KeyValuePair<string, GitStatusCache.Stand>(m, Cache.PerMap[m]))
            .OrderByDescending(p => p.Value.Aantal)
            .ThenByDescending(p => p.Value.Achter)
            .ThenBy(p => Naam(p.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>Totaal aantal ongecommitte bestanden over alle gevolgde repo's.</summary>
    public static int TotaalOngecommit => Standen().Sum(p => p.Value.Aantal);

    /// <summary>Hoeveel repo's er openstaand werk hebben.</summary>
    public static int ProjectenMetWerk => Standen().Count(p => p.Value.Aantal > 0);

    /// <summary>Hoeveel repo's achterlopen op hun remote.</summary>
    public static int ProjectenAchter => Standen().Count(p => p.Value.Achter > 0);

    /// <summary>
    /// Naam voor in een menu, taaktekst of tabel. Meestal de mapnaam; heet de map zelf
    /// generiek (backend, frontend), dan komt de bovenliggende naam erbij zodat namen elkaar
    /// niet overlappen.
    /// </summary>
    public static string Naam(string map)
    {
        var delen = map.TrimEnd('\\', '/').Split('\\', '/');
        var laatste = delen[^1];
        return delen.Length > 1 && laatste.ToLowerInvariant() is "backend" or "frontend" or "webapp"
            ? $"{delen[^2]}/{laatste}"
            : laatste;
    }

    /// <summary>
    /// Hoeveel dagen een wijziging al ligt: de ouderdom van het bestand op schijf. −1 als dat
    /// niet te bepalen valt (verwijderd bestand, onbereikbaar pad). Bij een hele map (untracked
    /// directory) telt het nieuwste bestand erin.
    /// </summary>
    public static int DagenOud(string map, string pad)
    {
        try
        {
            var vol = Path.Combine(map, pad.Replace('/', '\\'));
            DateTime tijd;
            if (File.Exists(vol))
            {
                tijd = File.GetLastWriteTime(vol);
            }
            else if (Directory.Exists(vol))
            {
                tijd = NieuwsteIn(vol);
            }
            else
            {
                return -1;
            }
            return Math.Max(0, (int)(DateTime.Now - tijd).TotalDays);
        }
        catch
        {
            return -1;
        }
    }

    private static DateTime NieuwsteIn(string map)
    {
        try
        {
            var tijden = Directory.EnumerateFiles(map, "*", SearchOption.AllDirectories)
                .Take(200) // grote mappen niet volledig aflopen
                .Select(File.GetLastWriteTime)
                .ToList();
            return tijden.Count > 0 ? tijden.Max() : Directory.GetLastWriteTime(map);
        }
        catch
        {
            return Directory.GetLastWriteTime(map);
        }
    }

    /// <summary>Zet een rapport om in de cachestand, inclusief de ouderdom per bestand.</summary>
    private static GitStatusCache.Stand StandUit(string map, GitStatus.Rapport rapport)
    {
        var bestanden = rapport.Wijzigingen
            // Gestagede wijzigingen eerst, daarna op pad: zelfde ordening als in de vensters.
            .OrderByDescending(w => w.Gestaged)
            .ThenBy(w => w.Pad, StringComparer.OrdinalIgnoreCase)
            .Select(w => new GitStatusCache.Bestand
            {
                Code = w.Code,
                Omschrijving = w.Omschrijving,
                Pad = w.Pad,
                Gestaged = w.Gestaged,
                Dagen = GitRadar.DagenOud(map, w.Pad),
            })
            .ToList();
        return new GitStatusCache.Stand
        {
            Kort = rapport.Kort,
            Branch = rapport.Branch,
            Aantal = rapport.Aantal,
            Staged = rapport.Wijzigingen.Count(w => w.Gestaged),
            Voor = rapport.Voor,
            Achter = rapport.Achter,
            OudsteDagen = bestanden.Count > 0 ? bestanden.Max(b => b.Dagen) : 0,
            Fout = rapport.Fout ?? "",
            Bestanden = bestanden,
            Moment = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Zoekt de repo's onder de zoekmappen op en bewaart de lijst in de cache. Eén niveau
    /// diep, plus één niveau eronder voor projecten die hun repo in een submap hebben
    /// (backend/frontend). WSL wordt in één keer bevraagd — <c>find</c> ín Linux is veel
    /// sneller dan over het \\wsl.localhost-pad wandelen.
    /// </summary>
    public static async Task OntdekAsync(CancellationToken ct)
    {
        var gevonden = new List<string>();
        foreach (var zoekmap in Zoekmappen)
        {
            try
            {
                gevonden.AddRange(ClientLauncher.TryWslPad(zoekmap, out var distro, out var linux)
                    ? await WslReposAsync(distro, linux, zoekmap, ct)
                    : WindowsRepos(zoekmap));
            }
            catch
            {
                // Zoekmap niet bereikbaar (WSL uit, schijf weg): de vaste lijst blijft gelden.
            }
        }
        if (gevonden.Count > 0)
        {
            Cache.Repos = gevonden.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        Cache.LaatsteOntdekking = DateTimeOffset.Now;
    }

    /// <summary>De repo's onder een WSL-zoekmap, via één find-commando in de distro.</summary>
    private static async Task<List<string>> WslReposAsync(
        string distro, string linuxMap, string windowsMap, CancellationToken ct)
    {
        // -maxdepth 3 vangt projecten/<repo>/.git en projecten/<project>/<repo>/.git;
        // -prune voorkomt dat find in de .git-mappen zelf gaat wandelen.
        var uit = await GitStatus.WslUitvoerAsync(
            distro, linuxMap,
            $"find \"{linuxMap}\" -maxdepth 3 -type d -name .git -prune -print", ct);
        var repos = new List<string>();
        foreach (var regel in uit.Split('\n'))
        {
            var r = regel.Trim().TrimEnd('\r');
            if (!r.EndsWith("/.git", StringComparison.Ordinal))
            {
                continue;
            }
            var repo = r[..^"/.git".Length];
            if (!repo.StartsWith(linuxMap, StringComparison.Ordinal))
            {
                continue;
            }
            // Terug naar het Windows-pad, zodat de rest van de app één soort pad ziet.
            var rest = repo[linuxMap.Length..].TrimStart('/').Replace('/', '\\');
            repos.Add(windowsMap.TrimEnd('\\') + "\\" + rest);
        }
        return repos;
    }

    /// <summary>De repo's onder een Windows-zoekmap (twee niveaus diep).</summary>
    private static List<string> WindowsRepos(string zoekmap)
    {
        var repos = new List<string>();
        if (!Directory.Exists(zoekmap))
        {
            return repos;
        }
        foreach (var map in Directory.EnumerateDirectories(zoekmap))
        {
            if (Directory.Exists(Path.Combine(map, ".git")))
            {
                repos.Add(map);
                continue; // een repo niet verder afdalen (submodules/vendor hoeven niet mee)
            }
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(map))
                {
                    if (Directory.Exists(Path.Combine(sub, ".git")))
                    {
                        repos.Add(sub);
                    }
                }
            }
            catch
            {
                // Geen toegang tot een submap: overslaan.
            }
        }
        return repos;
    }
}
