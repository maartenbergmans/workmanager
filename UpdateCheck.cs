using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Controleert (max. één keer per dag) of er een nieuwe versie is van de Claude Code CLI en van
/// PhpStorm. Is dat zo, dan komt er een taak in "Mijn taken" met een directe updatelink, zodat
/// je het niet hoeft te onthouden. Voor Claude geldt dat alleen bij een échte versiesprong
/// (2.1 → 2.2): patch-releases komen bijna dagelijks en haal je op eigen tempo binnen via de
/// vaste "Claude bijwerken"-knop in de cockpit. Best effort: geen netwerk of niet
/// geïnstalleerd = niets.
/// </summary>
public static class UpdateCheck
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "update-check.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task ZorgVoorAsync(CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (LaatsteDag() == vandaag)
        {
            return;
        }
        BewaarDag(vandaag); // meteen: hooguit één check per dag, ook bij fouten

        try
        {
            await CheckClaudeAsync(ct);
        }
        catch
        {
            // Best effort.
        }
        try
        {
            await CheckPhpStormAsync(ct);
        }
        catch
        {
            // Best effort.
        }
    }

    // ---------- Claude Code CLI ----------

    private static async Task CheckClaudeAsync(CancellationToken ct)
    {
        var huidig = Versie(await DraaiAsync("cmd.exe", "/c claude --version", 15000));
        if (huidig is null)
        {
            return; // niet geïnstalleerd of niet te lezen
        }
        var json = await Http.GetStringAsync(
            "https://registry.npmjs.org/@anthropic-ai/claude-code/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var laatste = Versie(doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null);
        if (laatste is null || laatste <= huidig)
        {
            return;
        }
        // Alleen een taak bij een versiesprong (major of minor, bv. 2.1 → 2.2). Patch-updates
        // geven géén taak; die doe je wanneer het uitkomt via de knop in de cockpit.
        if (laatste.Major == huidig.Major && laatste.Minor == huidig.Minor)
        {
            // Nog openstaande patch-taken (van vóór deze regel) meteen opruimen.
            var data = MijnTaakStore.Load();
            if (data.Taken.RemoveAll(t => !t.Klaar &&
                    t.Tekst.StartsWith("Claude bijwerken", StringComparison.OrdinalIgnoreCase)) > 0)
            {
                MijnTaakStore.Save(data);
            }
            return;
        }
        VoegTaakToe(
            $"Claude bijwerken: v{huidig} → v{laatste}",
            "Dubbelklik op deze taak: 'claude update' draait automatisch.",
            ""); // bewust geen link — de update gebeurt in de app zelf
    }

    // ---------- PhpStorm ----------

    private static async Task CheckPhpStormAsync(CancellationToken ct)
    {
        var huidig = GeinstalleerdePhpStormVersie();
        if (huidig is null)
        {
            return;
        }
        var (laatste, installer) = await NieuwstePhpStormAsync(ct);
        if (laatste is null || laatste <= huidig)
        {
            return;
        }
        // De directe Windows-installerlink meebewaren: dubbelklik op de taak downloadt hem en
        // draait de update stil (de cockpit herkent de taak op zijn "PhpStorm bijwerken"-prefix).
        VoegTaakToe(
            $"PhpStorm bijwerken: {huidig} → {laatste}",
            "Dubbelklik op deze taak: de installer wordt gedownload en de update draait automatisch.",
            installer ?? "");
    }

    /// <summary>
    /// De tekst van de openstaande update-taak met dit prefix (bv. "Claude bijwerken: v1 → v2"),
    /// of null als er geen update klaarstaat. De dagelijkse check maakt die taak alleen aan als er
    /// écht een nieuwere versie is; menu's kunnen er dus op afgaan zonder zelf het net op te moeten.
    /// </summary>
    public static string? OpenUpdateTaak(string prefix)
    {
        try
        {
            return MijnTaakStore.Load().Taken
                .FirstOrDefault(t => !t.Klaar &&
                    t.Tekst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Tekst;
        }
        catch
        {
            return null; // onleesbare takenlijst: doe alsof er niets klaarstaat
        }
    }

    /// <summary>Vinkt open update-taken met dit prefix af (na een geslaagde update).</summary>
    public static void VinkTaakAf(string prefix)
    {
        var data = MijnTaakStore.Load();
        var open = data.Taken.Where(t => !t.Klaar &&
            t.Tekst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (open.Count == 0)
        {
            return;
        }
        foreach (var t in open)
        {
            t.Klaar = true;
            t.KlaarOp = DateTimeOffset.Now;
        }
        MijnTaakStore.Save(data);
    }

    /// <summary>De nieuwste PhpStorm-versie + directe Windows-installerlink uit de JetBrains-API.</summary>
    public static async Task<(Version? Versie, string? InstallerUrl)> NieuwstePhpStormAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(
            "https://data.services.jetbrains.com/products/releases?code=PS&latest=true&type=release", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("PS", out var lijst) ||
            lijst.ValueKind != JsonValueKind.Array || lijst.GetArrayLength() == 0)
        {
            return (null, null);
        }
        var versie = Versie(lijst[0].TryGetProperty("version", out var v) ? v.GetString() : null);
        string? url = null;
        if (lijst[0].TryGetProperty("downloads", out var dl) &&
            dl.TryGetProperty("windows", out var win) &&
            win.TryGetProperty("link", out var link))
        {
            url = link.GetString();
        }
        return (versie, url);
    }

    /// <summary>De geïnstalleerde PhpStorm-versie uit product-info.json, of null.</summary>
    public static Version? GeinstalleerdePhpStormVersie()
    {
        var root = @"C:\Program Files\JetBrains";
        if (!Directory.Exists(root))
        {
            return null;
        }
        foreach (var dir in Directory.GetDirectories(root, "PhpStorm*")
                     .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var info = Path.Combine(dir, "product-info.json");
            if (!File.Exists(info))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                if (Versie(doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null) is { } ver)
                {
                    return ver;
                }
            }
            catch
            {
                // Volgende map proberen.
            }
        }
        return null;
    }

    // ---------- Hulpjes ----------

    /// <summary>Haalt een versienummer (x.y.z) uit een tekst en parseert het naar <see cref="Version"/>.</summary>
    private static Version? Versie(string? tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst))
        {
            return null;
        }
        var m = Regex.Match(tekst, @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    private static async Task<string> DraaiAsync(string exe, string args, int timeoutMs)
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
            });
            if (proc is null)
            {
                return "";
            }
            var uit = await proc.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            return uit;
        }
        catch
        {
            return "";
        }
    }

    private static void VoegTaakToe(string tekst, string uitleg, string link)
    {
        var data = MijnTaakStore.Load();
        // Geen dubbele of verouderde updatetaken: bestaande open update-taken van hetzelfde
        // programma opruimen voor we de nieuwe toevoegen.
        var prefix = tekst.Split(':')[0]; // "Claude bijwerken" / "PhpStorm bijwerken"
        data.Taken.RemoveAll(t => !t.Klaar && t.Tekst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "Urban IT",
            Prioriteit = 2,
            Deadline = DateOnly.FromDateTime(DateTime.Now),
            Mail = new TaakMail { Onderwerp = tekst, Tekst = uitleg, Link = link },
        });
        MijnTaakStore.Save(data);
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
