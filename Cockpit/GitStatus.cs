using System.Diagnostics;
using System.Text;

namespace WorkManager;

/// <summary>
/// Leest de git-status van een projectmap: hoeveel bestanden er nog ongecommit zijn en welke.
/// WSL-projecten (\\wsl.localhost\Ubuntu\home\…) worden ín WSL bevraagd — git op een UNC-pad
/// vanuit Windows is traag en ziet de repo vaak niet. Best effort: geen git, geen repo of geen
/// WSL levert een rapport met een foutmelding, nooit een crash.
/// </summary>
public static class GitStatus
{
    /// <summary>Eén gewijzigd bestand. <paramref name="Code"/> is de ruwe porcelain-code ("??", " M", "A ", …).</summary>
    public sealed record Wijziging(string Code, string Pad)
    {
        /// <summary>Leesbare omschrijving van de status, voor de kolom in het venster.</summary>
        public string Omschrijving => Code.Trim() switch
        {
            "??" => "nieuw (untracked)",
            "M" or "MM" => "gewijzigd",
            "A" or "AM" => "toegevoegd",
            "D" => "verwijderd",
            "R" => "hernoemd",
            "C" => "gekopieerd",
            "U" or "UU" or "AA" or "DD" => "conflict",
            "!!" => "genegeerd",
            _ => Code.Trim(),
        };

        /// <summary>Staat de wijziging al in de index (klaar om te committen)?</summary>
        public bool Gestaged => Code.Length > 0 && Code[0] is not (' ' or '?');
    }

    /// <summary>Het volledige beeld van één repo op één moment.</summary>
    public sealed record Rapport(
        string Map, string Branch, int Voor, int Achter,
        IReadOnlyList<Wijziging> Wijzigingen, string? Fout = null)
    {
        public int Aantal => Wijzigingen.Count;

        /// <summary>
        /// Korte samenvatting voor een menu-item of knoptekst: wat er lokaal openstaat én
        /// hoe ver je voor- of achterloopt op de remote branch.
        /// </summary>
        public string Kort => Fout is not null
            ? "git?"
            : (Aantal == 0 ? "alles gecommit" : $"{Aantal} ongecommit") +
              (Sync.Length > 0 ? $" · {Sync}" : "");

        /// <summary>Achterstand/voorsprong op de remote, of een lege tekst als die gelijk loopt.</summary>
        public string Sync =>
            (Voor > 0 ? $"{Voor} vóór" : "") +
            (Voor > 0 && Achter > 0 ? ", " : "") +
            (Achter > 0 ? $"{Achter} achter" : "");
    }

    /// <summary>
    /// Haalt de status op van <paramref name="werkmap"/>. Geeft altijd een rapport terug; bij
    /// problemen staat de reden in <see cref="Rapport.Fout"/>.
    /// </summary>
    public static async Task<Rapport> OphalenAsync(
        string werkmap, CancellationToken ct, bool fetchen = true)
    {
        // Zonder fetch weet git niet dat er op de remote iets bij kwam: "behind" blijft dan
        // eeuwig 0. Daarom eerst (hooguit één keer per tien minuten per repo) ophalen.
        if (fetchen)
        {
            await ProbeerFetchAsync(werkmap, ct);
        }
        var (exe, args) = ClientLauncher.TryWslPad(werkmap, out var distro, out var linux)
            ? ("wsl.exe", $"-d {distro} --cd \"{linux}\" -- git status --porcelain=v1 -b")
            : ("git.exe", $"-C \"{werkmap}\" status --porcelain=v1 -b");

        var (uit, fout, code) = await DraaiAsync(exe, args, ct);
        if (code != 0)
        {
            var reden = fout.Trim().Length > 0 ? fout.Trim() : uit.Trim();
            return new Rapport(werkmap, "", 0, 0, Array.Empty<Wijziging>(),
                reden.Length > 0 ? Kort(reden) : "git-status mislukt");
        }

        var branch = "";
        var voor = 0;
        var achter = 0;
        var wijzigingen = new List<Wijziging>();
        foreach (var regel in uit.Split('\n'))
        {
            var r = regel.TrimEnd('\r');
            if (r.Length == 0)
            {
                continue;
            }
            if (r.StartsWith("## ", StringComparison.Ordinal))
            {
                // "## main...origin/main [ahead 1, behind 2]" — of alleen "## main".
                var kop = r[3..];
                branch = kop.Split(new[] { "...", " [" }, StringSplitOptions.None)[0].Trim();
                var m = System.Text.RegularExpressions.Regex.Match(kop, @"ahead (\d+)");
                if (m.Success)
                {
                    voor = int.Parse(m.Groups[1].Value);
                }
                m = System.Text.RegularExpressions.Regex.Match(kop, @"behind (\d+)");
                if (m.Success)
                {
                    achter = int.Parse(m.Groups[1].Value);
                }
                continue;
            }
            if (r.Length < 4)
            {
                continue;
            }
            // Porcelain v1: twee statustekens, een spatie, dan het pad (bij rename "oud -> nieuw").
            var pad = r[3..].Trim();
            var pijl = pad.IndexOf(" -> ", StringComparison.Ordinal);
            if (pijl > 0)
            {
                pad = pad[(pijl + 4)..];
            }
            wijzigingen.Add(new Wijziging(r[..2], pad.Trim('"')));
        }
        return new Rapport(werkmap, branch, voor, achter, wijzigingen);
    }

    /// <summary>
    /// Draait één kaal git-commando in de repo (WSL of Windows) en geeft de stdout getrimd
    /// terug; een lege string bij elke vorm van mislukking. Voor kleine vragen zoals
    /// "waar staat de upstream-ref" (deploy-vreugde).
    /// </summary>
    public static async Task<string> KaleUitvoerAsync(
        string werkmap, string gitArgs, CancellationToken ct)
    {
        var (exe, args) = ClientLauncher.TryWslPad(werkmap, out var distro, out var linux)
            ? ("wsl.exe", $"-d {distro} --cd \"{linux}\" -- git {gitArgs}")
            : ("git.exe", $"-C \"{werkmap}\" {gitArgs}");
        var (uit, _, code) = await DraaiAsync(exe, args, ct);
        return code == 0 ? uit.Trim() : "";
    }

    /// <summary>Wanneer er voor deze repo voor het laatst opgehaald is (throttling).</summary>
    private static readonly Dictionary<string, DateTimeOffset> LaatsteFetch =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Haalt stilletjes de remote-stand op (git fetch), zodat "x achter" klopt. Hooguit één
    /// keer per tien minuten per repo, en elke fout wordt genegeerd: geen netwerk of geen
    /// remote mag de statusweergave nooit tegenhouden.
    /// </summary>
    private static async Task ProbeerFetchAsync(string werkmap, CancellationToken ct)
    {
        lock (LaatsteFetch)
        {
            if (LaatsteFetch.TryGetValue(werkmap, out var vorige) &&
                DateTimeOffset.Now - vorige < TimeSpan.FromMinutes(10))
            {
                return;
            }
            LaatsteFetch[werkmap] = DateTimeOffset.Now;
        }
        try
        {
            var (exe, args) = ClientLauncher.TryWslPad(werkmap, out var distro, out var linux)
                ? ("wsl.exe", $"-d {distro} --cd \"{linux}\" -- git fetch --quiet")
                : ("git.exe", $"-C \"{werkmap}\" fetch --quiet");
            await DraaiAsync(exe, args, ct);
        }
        catch
        {
            // Offline of geen remote: dan tonen we gewoon de laatst bekende stand.
        }
    }

    /// <summary>
    /// Draait een git-commando in de repo en geeft alles terug: stdout, stderr en de exitcode.
    /// Voor bewerkingen waarbij de foutmelding van git zelf het antwoord is — een afgeketste
    /// pre-commit hook of een push zonder upstream wil je letterlijk kunnen lezen.
    /// </summary>
    public static Task<(string Uit, string Fout, int Code)> GitAsync(
        string werkmap, string gitArgs, CancellationToken ct)
    {
        var (exe, args) = ClientLauncher.TryWslPad(werkmap, out var distro, out var linux)
            ? ("wsl.exe", $"-d {distro} --cd \"{linux}\" -- git {gitArgs}")
            : ("git.exe", $"-C \"{werkmap}\" {gitArgs}");
        return DraaiAsync(exe, args, ct);
    }

    /// <summary>
    /// Draait een kaal commando ín een WSL-distro (niet per se git) en geeft de stdout terug;
    /// leeg bij elke vorm van mislukking. De radar zoekt er in één keer alle repo's mee op.
    /// </summary>
    public static async Task<string> WslUitvoerAsync(
        string distro, string cd, string commando, CancellationToken ct)
    {
        var (uit, _, code) = await DraaiAsync(
            "wsl.exe", $"-d {distro} --cd \"{cd}\" -- {commando}", ct);
        return code == 0 ? uit : "";
    }

    /// <summary>
    /// De diff van één bestand. Staat er iets in de index én in de werkkopie, dan komen beide
    /// delen onder een kopje. Een nieuw (untracked) bestand heeft geen diff — daarvan komt de
    /// inhoud zelf terug, want dat is wat je wil nalezen voor je commit.
    /// </summary>
    public static async Task<string> DiffAsync(string werkmap, string pad, CancellationToken ct)
    {
        var werk = await KaleUitvoerAsync(werkmap, $"diff --no-color -- \"{pad}\"", ct);
        var index = await KaleUitvoerAsync(werkmap, $"diff --no-color --cached -- \"{pad}\"", ct);
        if (werk.Length > 0 && index.Length > 0)
        {
            return $"# In de index (klaar om te committen)\n{index}\n\n# Nog niet gestaged\n{werk}";
        }
        if (index.Length > 0)
        {
            return index;
        }
        if (werk.Length > 0)
        {
            return werk;
        }
        return NieuweInhoud(werkmap, pad);
    }

    /// <summary>
    /// De inhoud van een nieuw bestand (of de bestandenlijst van een nieuwe map), ingekort,
    /// als vervanger van de diff die git voor untracked bestanden niet heeft.
    /// </summary>
    private static string NieuweInhoud(string werkmap, string pad)
    {
        try
        {
            var vol = Path.Combine(werkmap, pad.Replace('/', '\\'));
            if (Directory.Exists(vol))
            {
                var inhoud = Directory.EnumerateFiles(vol, "*", SearchOption.AllDirectories)
                    .Take(200)
                    .Select(f => "  " + Path.GetRelativePath(vol, f).Replace('\\', '/'))
                    .ToList();
                return $"# Nieuwe map met {inhoud.Count} bestand(en)\n" + string.Join("\n", inhoud);
            }
            if (!File.Exists(vol))
            {
                return "(bestand is verwijderd of niet bereikbaar)";
            }
            var info = new FileInfo(vol);
            if (info.Length > 400_000)
            {
                return $"# Nieuw bestand van {info.Length / 1024} kB — te groot om hier te tonen.";
            }
            var tekst = File.ReadAllText(vol);
            // Een binair bestand als tekst tonen levert alleen ruis op; dat zeggen we liever.
            if (tekst.Contains('\0'))
            {
                return $"# Nieuw binair bestand ({info.Length / 1024} kB).";
            }
            var regels = tekst.ReplaceLineEndings("\n").Split('\n');
            var kop = $"# Nieuw bestand — {regels.Length} regel(s)\n";
            return regels.Length <= 500
                ? kop + tekst
                : kop + string.Join("\n", regels.Take(500)) + "\n\n# (ingekort)";
        }
        catch (Exception ex)
        {
            return $"(inhoud niet leesbaar: {ex.Message})";
        }
    }

    /// <summary>
    /// Commit de gekozen paden — ze worden eerst gestaged — en pusht desgevraagd. Het
    /// commitbericht gaat via een bestand in .git en niet via <c>-m</c>: zo blijven
    /// aanhalingstekens, accenten en meerdere regels staan zoals je ze typte.
    /// </summary>
    /// <returns>Of het gelukt is, plus de melding die in de toast hoort.</returns>
    public static async Task<(bool Ok, string Melding)> CommitAsync(
        string werkmap, IReadOnlyList<string> paden, string bericht, bool pushen,
        CancellationToken ct)
    {
        if (paden.Count == 0)
        {
            return (false, "Geen bestanden aangevinkt");
        }
        if (bericht.Trim().Length == 0)
        {
            return (false, "Geen commitbericht");
        }

        // Stagen in blokken: een commandoregel met honderden paden erin wordt te lang.
        foreach (var blok in paden.Chunk(40))
        {
            ct.ThrowIfCancellationRequested();
            var lijst = string.Join(" ", blok.Select(p => $"\"{p}\""));
            var (_, addFout, addCode) = await GitAsync(werkmap, $"add -- {lijst}", ct);
            if (addCode != 0)
            {
                return (false, $"Stagen mislukt: {Kort(addFout)}");
            }
        }

        var berichtBestand = Path.Combine(werkmap, ".git", "WM_COMMIT_MSG");
        var viaBestand = Directory.Exists(Path.Combine(werkmap, ".git"));
        try
        {
            string commitArgs;
            if (viaBestand)
            {
                // Relatief pad: met -C (en met --cd in WSL) staat git al in de repo.
                await File.WriteAllTextAsync(berichtBestand, bericht.Trim() + "\n", ct);
                commitArgs = "commit -F .git/WM_COMMIT_MSG";
            }
            else
            {
                // Zeldzaam (worktree of submodule: .git is dan een bestand). Dan toch -m, met
                // een bericht op één regel en zonder aanhalingstekens.
                var eenRegel = bericht.ReplaceLineEndings(" ").Replace("\"", "'").Trim();
                commitArgs = $"commit -m \"{eenRegel}\"";
            }
            var (uit, fout, code) = await GitAsync(werkmap, commitArgs, ct);
            if (code != 0)
            {
                return (false, $"Commit mislukt: {Kort(fout.Length > 0 ? fout : uit)}");
            }
        }
        finally
        {
            try
            {
                if (viaBestand)
                {
                    File.Delete(berichtBestand);
                }
            }
            catch
            {
                // Blijft het berichtbestand liggen: geen probleem, git kijkt er niet naar.
            }
        }

        var melding = $"{paden.Count} bestand(en) gecommit";
        if (!pushen)
        {
            return (true, melding);
        }
        var (pushOk, pushMelding) = await PushAsync(werkmap, ct);
        return (pushOk, $"{melding} · {pushMelding}");
    }

    /// <summary>
    /// Pusht de huidige branch. Heeft die nog geen upstream, dan wordt die in één keer gezet
    /// (<c>push -u origin branch</c>) — anders moest je daarvoor alsnog naar de terminal.
    /// </summary>
    public static async Task<(bool Ok, string Melding)> PushAsync(
        string werkmap, CancellationToken ct)
    {
        var (uit, fout, code) = await GitAsync(werkmap, "push", ct);
        if (code == 0)
        {
            return (true, "gepusht");
        }
        var samen = fout + uit;
        if (samen.Contains("no upstream branch", StringComparison.OrdinalIgnoreCase) ||
            samen.Contains("set-upstream", StringComparison.OrdinalIgnoreCase))
        {
            var branch = await KaleUitvoerAsync(werkmap, "rev-parse --abbrev-ref HEAD", ct);
            if (branch.Length > 0)
            {
                var (_, fout2, code2) = await GitAsync(
                    werkmap, $"push --set-upstream origin {branch}", ct);
                return code2 == 0
                    ? (true, $"gepusht (upstream origin/{branch} gezet)")
                    : (false, $"push mislukt: {Kort(fout2)}");
            }
        }
        return (false, $"push mislukt: {Kort(samen)}");
    }

    private static string Kort(string tekst)
    {
        var eenRegel = tekst.ReplaceLineEndings(" ").Trim();
        return eenRegel.Length > 160 ? eenRegel[..160] + "…" : eenRegel;
    }

    private static async Task<(string Uit, string Fout, int Code)> DraaiAsync(
        string exe, string args, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (proc is null)
            {
                return ("", $"{exe} kon niet gestart worden", -1);
            }
            var uitTaak = proc.StandardOutput.ReadToEndAsync(ct);
            var foutTaak = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* al weg */ }
                return ("", "git-status duurde te lang", -1);
            }
            return (await uitTaak, await foutTaak, proc.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }
    }
}
