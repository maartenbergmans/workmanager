using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Kennis bewaren uit de Claude-sessies: één keer per week (vrijdag vanaf 15 u, of later in
/// het weekend als dat gemist werd) leest dit de getypte prompts van de voorbije 7 dagen uit
/// de Claude Code-transcripten (Windows én WSL), groepeert ze per git-repo en laat Claude per
/// repo voorstellen wat er in CLAUDE.md bij moet: instructies die Maarten telkens opnieuw
/// moest geven, conventies, valkuilen, vaste commando's. Niets wordt automatisch geschreven:
/// in het venster "Kennisvoorstellen" vink je aan wat erin mag, dat komt dan onderaan
/// CLAUDE.md onder "## Aanvullingen uit de praktijk" (ongecommit; zelf nakijken en committen).
/// <para>State: %APPDATA%\WorkManager\kennis-voorstellen.json.</para>
/// </summary>
public static class KennisVoorstellen
{
    private const int MinPrompts = 5;
    private const string Kop = "## Aanvullingen uit de praktijk";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "kennis-voorstellen.json");

    private static readonly string[] TranscriptMappen =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"),
        @"\\wsl.localhost\Ubuntu\home\maarten\.claude\projects",
    };

    public sealed class Voorstel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Week { get; set; } = "";
        public string Repo { get; set; } = "";      // pad naar de repo-root
        public string RepoNaam { get; set; } = "";
        public string Titel { get; set; } = "";
        public string Tekst { get; set; } = "";     // markdown, zo in CLAUDE.md
        public string Waarom { get; set; } = "";
        public string Status { get; set; } = "open"; // open | toegevoegd | genegeerd
    }

    private sealed class State
    {
        public string LaatsteWeek { get; set; } = "";
        public List<Voorstel> Voorstellen { get; set; } = new();
    }

    private static int _bezig;

    public static List<Voorstel> Open() => Laad().Voorstellen.Where(v => v.Status == "open").ToList();

    /// <summary>Vrijdag vanaf 15 u (of za/zo als het gemist werd): 1× per ISO-week genereren.</summary>
    public static async Task ZorgVoorAsync(CancellationToken ct, Action<int>? klaar = null)
    {
        var nu = DateTime.Now;
        var inVenster = nu.DayOfWeek switch
        {
            DayOfWeek.Friday => nu.Hour >= 15,
            DayOfWeek.Saturday or DayOfWeek.Sunday => true,
            _ => false,
        };
        if (!inVenster || Laad().LaatsteWeek == WeekVan(nu))
        {
            return;
        }
        var aantal = await GenereerAsync(ct);
        if (aantal > 0)
        {
            klaar?.Invoke(aantal);
        }
    }

    /// <summary>Genereert de voorstellen voor de voorbije 7 dagen (ook handmatig). Retourneert het aantal nieuwe.</summary>
    public static async Task<int> GenereerAsync(CancellationToken ct, IProgress<string>? voortgang = null)
    {
        if (Interlocked.Exchange(ref _bezig, 1) == 1)
        {
            return 0;
        }
        try
        {
            var state = Laad();
            state.LaatsteWeek = WeekVan(DateTime.Now);
            Bewaar(state);

            var perRepo = await Task.Run(() => PromptsPerRepo(DateTimeOffset.Now.AddDays(-7)), ct);
            var nieuw = 0;
            foreach (var (repo, prompts) in perRepo.Where(kv => kv.Value.Count >= MinPrompts)
                         .OrderByDescending(kv => kv.Value.Count))
            {
                var naam = Path.GetFileName(repo.TrimEnd('\\'));
                voortgang?.Report($"{naam}: {prompts.Count} prompts analyseren…");
                try
                {
                    var voorstellen = await VoorstellenVoorAsync(repo, naam, prompts, ct);
                    state = Laad();
                    state.Voorstellen.AddRange(voorstellen);
                    Bewaar(state);
                    nieuw += voorstellen.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Eén repo die mislukt (Claude-timeout) mag de rest niet tegenhouden.
                }
            }
            return nieuw;
        }
        finally
        {
            Interlocked.Exchange(ref _bezig, 0);
        }
    }

    private static async Task<List<Voorstel>> VoorstellenVoorAsync(
        string repo, string naam, List<string> prompts, CancellationToken ct)
    {
        var claudeMd = Path.Combine(repo, "CLAUDE.md");
        var huidig = File.Exists(claudeMd) ? await File.ReadAllTextAsync(claudeMd, ct) : "";
        if (huidig.Length > 15000)
        {
            huidig = huidig[..15000] + "\n…(afgekapt)";
        }
        var eerder = Laad().Voorstellen.Where(v => v.Repo == repo)
            .Select(v => $"- {v.Titel} ({v.Status})").TakeLast(30);
        var prompt = $$"""
            Je helpt Maarten (freelance ontwikkelaar) om de projectinstructies van zijn repo
            "{{naam}}" scherp te houden. Claude Code leest CLAUDE.md bij elke sessie.

            Hieronder zijn getypte opdrachten aan Claude Code in deze repo van de voorbije
            week, en de huidige CLAUDE.md.

            OPDRACHT: stel hoogstens 6 aanvullingen voor CLAUDE.md voor die toekomstige
            sessies echt tijd besparen: instructies die Maarten meermaals moest herhalen,
            correcties ("nee, niet zo…"), conventies, valkuilen, vaste commando's (deploy,
            testen, starten), domeinbegrippen. Alleen wat NOG NIET in CLAUDE.md staat en
            wat blijvend geldt — geen losse taken van deze week. Liever 0 voorstellen dan
            vulsel. Schrijf in het Nederlands, bondig, als markdown-bullets.

            Eerder al voorgesteld (niet herhalen):
            {{string.Join("\n", eerder)}}

            Antwoord uitsluitend met JSON:
            {"voorstellen": [{"titel": "kort", "tekst": "- markdown-bullet(s) zoals ze in CLAUDE.md komen", "waarom": "welke prompts dit tonen"}]}

            HUIDIGE CLAUDE.md
            {{(huidig.Length > 0 ? huidig : "(bestaat nog niet)")}}

            PROMPTS VAN DEZE WEEK
            {{string.Join("\n", prompts.TakeLast(120).Select(p => "- " + p))}}
            """;
        using var doc = ClaudeDrafter.ParseJson(await ClaudeDrafter.RunClaudeAsync(prompt, ct));
        var lijst = new List<Voorstel>();
        if (doc.RootElement.TryGetProperty("voorstellen", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var tekst = Veld(el, "tekst");
                if (tekst.Length == 0)
                {
                    continue;
                }
                lijst.Add(new Voorstel
                {
                    Week = WeekVan(DateTime.Now),
                    Repo = repo,
                    RepoNaam = naam,
                    Titel = Veld(el, "titel"),
                    Tekst = tekst,
                    Waarom = Veld(el, "waarom"),
                });
            }
        }
        return lijst;
    }

    /// <summary>Voegt de voorstellen onderaan CLAUDE.md van hun repo toe en markeert ze.</summary>
    public static int VoegToe(IEnumerable<Guid> ids)
    {
        var state = Laad();
        var gekozen = state.Voorstellen.Where(v => ids.Contains(v.Id) && v.Status == "open").ToList();
        foreach (var groep in gekozen.GroupBy(v => v.Repo))
        {
            var pad = Path.Combine(groep.Key, "CLAUDE.md");
            var tekst = File.Exists(pad) ? File.ReadAllText(pad) : $"# {groep.First().RepoNaam}\n";
            var nl = tekst.Contains("\r\n") ? "\r\n" : "\n";
            var sb = new StringBuilder(tekst.TrimEnd());
            if (!tekst.Contains(Kop))
            {
                sb.Append(nl).Append(nl).Append(Kop).Append(nl);
            }
            sb.Append(nl);
            foreach (var v in groep)
            {
                sb.Append(v.Tekst.Trim().Replace("\r\n", "\n").Replace("\n", nl)).Append(nl);
                v.Status = "toegevoegd";
            }
            File.WriteAllText(pad, sb.ToString());
        }
        Bewaar(state);
        return gekozen.Count;
    }

    /// <summary>Neemt een in het venster aangepaste tekst over vóór het toevoegen.</summary>
    public static void ZetTekst(Guid id, string tekst)
    {
        var state = Laad();
        if (state.Voorstellen.FirstOrDefault(v => v.Id == id) is { } v && tekst.Trim().Length > 0 && v.Tekst != tekst)
        {
            v.Tekst = tekst.Trim();
            Bewaar(state);
        }
    }

    public static void Negeer(IEnumerable<Guid> ids)
    {
        var state = Laad();
        foreach (var v in state.Voorstellen.Where(v => ids.Contains(v.Id)))
        {
            v.Status = "genegeerd";
        }
        Bewaar(state);
    }

    // ---------------------------------------------------------------- transcripten

    /// <summary>Getypte prompts sinds <paramref name="vanaf"/>, per git-repo-root.</summary>
    private static Dictionary<string, List<string>> PromptsPerRepo(DateTimeOffset vanaf)
    {
        var perRepo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rootCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var basis in TranscriptMappen)
        {
            IEnumerable<string> bestanden;
            try
            {
                bestanden = Directory.Exists(basis)
                    ? Directory.EnumerateFiles(basis, "*.jsonl", SearchOption.AllDirectories)
                        .Where(f => File.GetLastWriteTimeUtc(f) >= vanaf.UtcDateTime)
                        .ToList()
                    : Array.Empty<string>();
            }
            catch
            {
                continue; // WSL niet bereikbaar
            }
            foreach (var bestand in bestanden)
            {
                // Subagent-transcripten bevatten geen eigen prompts van Maarten.
                if (bestand.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                foreach (var (cwd, prompt) in LeesPrompts(bestand, vanaf))
                {
                    if (!rootCache.TryGetValue(cwd, out var root))
                    {
                        root = GitRoot(cwd);
                        rootCache[cwd] = root;
                    }
                    if (root is null)
                    {
                        continue;
                    }
                    if (!perRepo.TryGetValue(root, out var lijst))
                    {
                        perRepo[root] = lijst = new List<string>();
                    }
                    lijst.Add(prompt);
                }
            }
        }
        return perRepo;
    }

    private static IEnumerable<(string Cwd, string Prompt)> LeesPrompts(string bestand, DateTimeOffset vanaf)
    {
        var uit = new List<(string, string)>();
        try
        {
            using var stream = new FileStream(bestand, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } regel)
            {
                if (!regel.Contains("\"type\":\"user\"") || regel.Contains("\"isSidechain\":true"))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(regel);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }
                    if (r.TryGetProperty("origin", out var origin) &&
                        origin.TryGetProperty("kind", out var kind) && kind.GetString() != "human")
                    {
                        continue;
                    }
                    if (!r.TryGetProperty("timestamp", out var ts) || ts.GetDateTimeOffset() < vanaf)
                    {
                        continue;
                    }
                    if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.String)
                    {
                        continue; // tool_result-arrays zijn geen getypte prompts
                    }
                    var tekst = Regex.Replace(content.GetString() ?? "", @"\s+", " ").Trim();
                    if (tekst.Length < 8 || tekst.StartsWith('<') || tekst.StartsWith("/color") ||
                        tekst.StartsWith("Caveat:"))
                    {
                        continue;
                    }
                    var cwd = r.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
                    if (cwd.Length > 0)
                    {
                        uit.Add((cwd, tekst.Length > 300 ? tekst[..300] + "…" : tekst));
                    }
                }
                catch
                {
                    // Kapotte regel overslaan.
                }
            }
        }
        catch
        {
            // Bestand in gebruik of weg.
        }
        return uit;
    }

    /// <summary>Loopt vanaf de werkmap omhoog tot een map met .git; WSL-paden via \\wsl.localhost.</summary>
    private static string? GitRoot(string cwd)
    {
        var pad = cwd.StartsWith('/') ? @"\\wsl.localhost\Ubuntu" + cwd.Replace('/', '\\') : cwd;
        // Headless runs van WorkManager zelf (mailconcepten, dagvoorstel) draaien in de datamap.
        if (pad.Contains(@"AppData\Roaming\WorkManager", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var map = new DirectoryInfo(pad);
            for (var i = 0; i < 6 && map is not null; i++, map = map.Parent)
            {
                if (Directory.Exists(Path.Combine(map.FullName, ".git")) ||
                    File.Exists(Path.Combine(map.FullName, ".git")))
                {
                    return map.FullName.TrimEnd('\\');
                }
            }
        }
        catch
        {
            // Onbereikbaar pad.
        }
        return null;
    }

    // ---------------------------------------------------------------- opslag

    private static string WeekVan(DateTime moment) =>
        $"{ISOWeek.GetYear(moment)}-W{ISOWeek.GetWeekOfYear(moment):00}";

    private static string Veld(JsonElement el, string naam) =>
        el.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
