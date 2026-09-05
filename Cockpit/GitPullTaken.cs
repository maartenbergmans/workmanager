using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Controleert één keer per dag of de lokale hoofdbranch (main/master) van de projecten
/// achterloopt op de remote. Is dat zo, dan komt er één taak in "Mijn taken" die voorstelt om
/// te pullen; dubbelklikken werkt de achterlopende projecten meteen bij. Dat gebeurt altijd
/// fast-forward: staat er een andere branch uitgecheckt of lopen lokaal en remote uiteen, dan
/// blijft het werk ongemoeid en meldt de taak dat handwerk nodig is. Best effort: geen
/// netwerk, geen WSL of geen repo = niets.
/// </summary>
public static class GitPullTaken
{
    public const string TaakPrefix = "Git pullen";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "git-pull-check.json");

    /// <summary>Repo's bovenop de vaste projectenlijst van <see cref="GitTaken"/>.</summary>
    private static readonly string[] ExtraRepos =
    {
        @"C:\Data\Projecten\Workmanager",
        @"\\wsl.localhost\Ubuntu\home\maarten\projecten\urbanadmin",
    };

    public static IEnumerable<string> Projecten => GitTaken.Projecten.Concat(ExtraRepos);

    private static bool _bezig;

    /// <summary>
    /// Zet de pulltaak klaar als een project achterloopt. Draait op de achtergrond (fetch per
    /// repo, WSL is traag) en doet niets als alles bij is.
    /// </summary>
    public static async Task ZorgVoorAsync(CancellationToken ct)
    {
        if (_bezig)
        {
            return;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (LaatsteDag() == vandaag)
        {
            return;
        }
        _bezig = true;
        try
        {
            BewaarDag(vandaag); // meteen: hooguit één check per dag, ook bij fouten

            var achterstand = new List<(string Naam, int Achter)>();
            foreach (var map in Projecten)
            {
                ct.ThrowIfCancellationRequested();
                var achter = await AchterstandAsync(map, ct);
                if (achter > 0)
                {
                    achterstand.Add((Naam(map), achter));
                }
            }

            var data = MijnTaakStore.Load();
            // Een ouder voorstel klopt niet meer: altijd eerst opruimen — ook als er vandaag
            // niets achterloopt (dan was de pull blijkbaar al gedaan).
            var opgeruimd = data.Taken.RemoveAll(t => !t.Klaar &&
                t.Tekst.StartsWith(TaakPrefix, StringComparison.OrdinalIgnoreCase));
            if (achterstand.Count == 0)
            {
                if (opgeruimd > 0)
                {
                    MijnTaakStore.Save(data);
                }
                return;
            }

            var samenvatting = string.Join(", ", achterstand
                .OrderByDescending(a => a.Achter)
                .Select(a => $"{a.Naam} {a.Achter} achter"));
            data.Taken.Add(new MijnTaak
            {
                Tekst = $"{TaakPrefix}: {samenvatting}",
                Categorie = "Urban IT",
                Prioriteit = 2,
                Deadline = DateOnly.FromDateTime(DateTime.Now),
                Mail = new TaakMail
                {
                    Onderwerp = "Lokale hoofdbranch loopt achter op de remote",
                    Tekst = "Dubbelklik op deze taak: de projecten worden fast-forward bijgewerkt.",
                    Link = "",
                },
            });
            MijnTaakStore.Save(data);
        }
        catch (OperationCanceledException)
        {
            // App sluit af tijdens het scannen.
        }
        catch
        {
            // Git of WSL niet beschikbaar: morgen opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Werkt de hoofdbranch bij van elk project dat in de taaktekst genoemd wordt. Staat de
    /// hoofdbranch uitgecheckt, dan is het een gewone fast-forward pull; zit je op een andere
    /// branch, dan wordt alleen de lokale hoofdbranch doorgeschoven (fetch origin main:main).
    /// </summary>
    /// <returns>Of alles gelukt is, plus een korte melding voor de toast.</returns>
    public static async Task<(bool AllesKlaar, string Melding)> PullAsync(
        string taakTekst, CancellationToken ct)
    {
        var klaar = new List<string>();
        var mislukt = new List<string>();
        foreach (var map in Projecten)
        {
            var naam = Naam(map);
            if (!taakTekst.Contains(naam + " ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ct.ThrowIfCancellationRequested();
            var branch = await HoofdbranchAsync(map, ct);
            if (branch.Length == 0)
            {
                mislukt.Add(naam);
                continue;
            }
            var huidige = await GitStatus.KaleUitvoerAsync(map, "rev-parse --abbrev-ref HEAD", ct);
            if (huidige == branch)
            {
                await GitStatus.KaleUitvoerAsync(map, "pull --ff-only --quiet", ct);
            }
            else
            {
                await GitStatus.KaleUitvoerAsync(map, $"fetch --quiet origin {branch}:{branch}", ct);
            }
            // Succes aflezen aan het resultaat, niet aan de (stille) uitvoer: staat de lokale
            // branch nu gelijk met de remote, dan is de pull gelukt.
            var restant = await GitStatus.KaleUitvoerAsync(map,
                $"rev-list --count {branch}..origin/{branch}", ct);
            if (restant == "0")
            {
                klaar.Add(naam);
            }
            else
            {
                mislukt.Add(naam);
            }
        }
        if (klaar.Count == 0 && mislukt.Count == 0)
        {
            return (false, "Geen project herkend in de taak");
        }
        var melding =
            (klaar.Count > 0 ? $"Bijgewerkt: {string.Join(", ", klaar)}" : "") +
            (klaar.Count > 0 && mislukt.Count > 0 ? " · " : "") +
            (mislukt.Count > 0
                ? $"Zelf nakijken (geen fast-forward): {string.Join(", ", mislukt)}"
                : "");
        return (mislukt.Count == 0, melding);
    }

    /// <summary>Hoeveel commits de lokale hoofdbranch achterloopt op de remote (0 bij twijfel).</summary>
    private static async Task<int> AchterstandAsync(string map, CancellationToken ct)
    {
        var branch = await HoofdbranchAsync(map, ct);
        if (branch.Length == 0)
        {
            return 0; // geen repo of geen remote
        }
        await GitStatus.KaleUitvoerAsync(map, "fetch --quiet", ct);
        var telling = await GitStatus.KaleUitvoerAsync(map,
            $"rev-list --count {branch}..origin/{branch}", ct);
        return int.TryParse(telling, out var n) ? n : 0;
    }

    /// <summary>De hoofdbranch van de remote ("main" of "master"), of leeg bij twijfel.</summary>
    private static async Task<string> HoofdbranchAsync(string map, CancellationToken ct)
    {
        // origin/HEAD wijst naar de hoofdbranch, maar oudere clones missen die ref weleens;
        // dan volstaat kijken welke van de twee gangbare namen op de remote bestaat.
        var head = await GitStatus.KaleUitvoerAsync(map, "rev-parse --abbrev-ref origin/HEAD", ct);
        if (head.StartsWith("origin/", StringComparison.Ordinal))
        {
            return head["origin/".Length..];
        }
        foreach (var naam in new[] { "main", "master" })
        {
            var ok = await GitStatus.KaleUitvoerAsync(
                map, $"rev-parse --verify --quiet origin/{naam}", ct);
            if (ok.Length > 0)
            {
                return naam;
            }
        }
        return "";
    }

    /// <summary>
    /// Naam voor in de taaktekst. Meestal de mapnaam; heet de map zelf generiek (backend,
    /// frontend), dan komt de projectnaam erbij zodat namen elkaar niet overlappen.
    /// </summary>
    private static string Naam(string map)
    {
        var delen = map.TrimEnd('\\', '/').Split('\\', '/');
        var laatste = delen[^1];
        return laatste.ToLowerInvariant() is "backend" or "frontend" or "webapp"
            ? $"{delen[^2]}/{laatste}"
            : laatste;
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
