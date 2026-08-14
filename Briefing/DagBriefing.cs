using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>De briefing van één dag, zoals bewaard in %APPDATA%\WorkManager\dag-briefing.json.</summary>
public sealed class DagBriefingData
{
    /// <summary>Dag waarop deze briefing slaat (yyyy-MM-dd).</summary>
    public string Datum { get; set; } = "";

    public DateTimeOffset GemaaktOp { get; set; }

    /// <summary>Twee of drie zinnen over hoe de dag eruitziet.</summary>
    public string Samenvatting { get; set; } = "";

    /// <summary>Waar je vandaag je tijd het best aan besteedt (hooguit vier punten).</summary>
    public List<string> Focus { get; set; } = new();

    /// <summary>Wat dreigt mis te lopen: deadlines, wachtende mensen, conflicten in de agenda.</summary>
    public List<string> Attentie { get; set; } = new();

    /// <summary>De agenda van vandaag als kale regels (feiten, niet van Claude).</summary>
    public List<string> Agenda { get; set; } = new();

    /// <summary>Weersverwachting in één regel; leeg als er geen thuisadres ingesteld is.</summary>
    public string Weer { get; set; } = "";

    /// <summary>Eerste verplaatsing van vandaag, met rijtijd en vertrekmoment.</summary>
    public string Reis { get; set; } = "";

    /// <summary>Aantallen die de briefing samenvatten (voor de tray-melding).</summary>
    public int OpenTaken { get; set; }

    public int Afspraken { get; set; }

    public int WachtendeBerichten { get; set; }
}

/// <summary>
/// Bouwt elke ochtend één overzicht van de dag: agenda, eigen taken, teamtaken, berichten die
/// nog een reactie vragen, het weer en de eerste verplaatsing. De feiten worden lokaal
/// verzameld; Claude maakt er een samenvatting, een focuslijst en een aandachtslijst van.
/// Het resultaat gaat naar dag-briefing.json zodat het venster het meteen kan tonen en de
/// briefing maar één keer per dag hoeft te draaien.
/// </summary>
public static class DagBriefing
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string BriefingFile = Path.Combine(DataDir, "dag-briefing.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static bool _bezig;

    /// <summary>Draait er op dit moment een briefing (voorkomt dubbele Claude-aanroepen).</summary>
    public static bool Bezig => _bezig;

    /// <summary>De bewaarde briefing, of null als er nog geen van vandaag is.</summary>
    public static DagBriefingData? VanVandaag()
    {
        var data = Laad();
        return data is not null && data.Datum == DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd")
            ? data
            : null;
    }

    /// <summary>De laatst bewaarde briefing, ook als die van een vorige dag is.</summary>
    public static DagBriefingData? Laatste() => Laad();

    /// <summary>
    /// Stelt de briefing van vandaag samen en bewaart hem. Met <paramref name="forceer"/>
    /// = false wordt een bestaande briefing van vandaag gewoon teruggegeven.
    /// </summary>
    public static async Task<DagBriefingData> MaakAsync(CancellationToken ct, bool forceer = false)
    {
        if (!forceer && VanVandaag() is { } bestaand)
        {
            return bestaand;
        }
        if (_bezig)
        {
            throw new InvalidOperationException("De briefing wordt al samengesteld.");
        }

        _bezig = true;
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Today);
            var briefing = new DagBriefingData
            {
                Datum = vandaag.ToString("yyyy-MM-dd"),
                GemaaktOp = DateTimeOffset.Now,
            };

            var agenda = await AgendaVanVandaagAsync(ct);
            briefing.Agenda = agenda.Select(BeschrijfAfspraak).ToList();
            briefing.Afspraken = agenda.Count(a => !a.HeleDag);

            var reis = ReisSettings.Load();
            if (reis.Aan && reis.HeeftThuis)
            {
                briefing.Weer = await Weer.VandaagAsync(reis.ThuisLat, reis.ThuisLon, ct) is { } weer
                    ? weer.Regel
                    : "";
                briefing.Reis = await EersteVerplaatsingAsync(agenda, reis, ct);
            }

            var taken = MijnTaakStore.Load().Taken
                .Where(t => !t.Klaar && !t.Gesnoozed)
                .OrderBy(t => t.Deadline ?? DateOnly.MaxValue)
                .ThenBy(t => t.Prioriteit)
                .ToList();
            briefing.OpenTaken = taken.Count;

            var berichten = WachtendeBerichten();
            briefing.WachtendeBerichten = berichten.Count;

            var context = BouwContext(vandaag, agenda, taken, berichten, briefing);
            try
            {
                var antwoord = await ClaudeDrafter.RunClaudeAsync(Prompt(context), ct);
                using var doc = ClaudeDrafter.ParseJson(antwoord);
                briefing.Samenvatting = Tekst(doc.RootElement, "samenvatting");
                briefing.Focus = Lijst(doc.RootElement, "focus");
                briefing.Attentie = Lijst(doc.RootElement, "attentie");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Zonder Claude blijft de briefing bruikbaar: de feiten staan er al in.
                briefing.Samenvatting = $"(samenvatting niet gelukt: {ex.Message})";
            }

            Bewaar(briefing);
            return briefing;
        }
        finally
        {
            _bezig = false;
        }
    }

    // ---------- Feiten verzamelen ----------

    internal static async Task<List<AgendaClient.AgendaItem>> AgendaVanVandaagAsync(CancellationToken ct)
    {
        var settings = AgendaSettings.Load();
        if (!settings.Compleet)
        {
            return new List<AgendaClient.AgendaItem>();
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            return await AgendaClient.OphalenAsync(settings.Urls, vandaag, vandaag, ct);
        }
        catch
        {
            // Geen netwerk of een kapotte feed: de briefing gaat door zonder agenda.
            return new List<AgendaClient.AgendaItem>();
        }
    }

    private static string BeschrijfAfspraak(AgendaClient.AgendaItem item) =>
        (item.HeleDag ? "Hele dag" : $"{item.Start:HH:mm}–{item.Einde:HH:mm}") +
        $" · {item.Titel}" +
        (item.Locatie.Length > 0 ? $" ({item.Locatie})" : "");

    /// <summary>
    /// Berichten uit de cockpit-cache die nog iets van je vragen: Claude markeerde ze als
    /// urgent of er staat een conceptantwoord klaar dat nog niet vertrokken is.
    /// </summary>
    private static List<MailBericht> WachtendeBerichten() =>
        CockpitCache.Load()
            .Where(b => !b.Genegeerd && (b.Urgent || (b.ConceptKlaar && b.Concept.Length > 0)))
            .OrderByDescending(b => b.Urgent)
            .ThenByDescending(b => b.Datum)
            .Take(15)
            .ToList();

    /// <summary>
    /// Zoekt de eerste afspraak van vandaag met een adres en berekent de rijtijd ernaartoe,
    /// zodat de briefing meteen zegt hoe laat je moet vertrekken.
    /// </summary>
    private static async Task<string> EersteVerplaatsingAsync(
        List<AgendaClient.AgendaItem> agenda, ReisSettings reis, CancellationToken ct)
    {
        var doel = agenda.FirstOrDefault(a =>
            !a.HeleDag && a.Start > DateTimeOffset.Now && MeetingPrep.IsEchtAdres(a.Locatie));
        if (doel is null)
        {
            return "";
        }
        try
        {
            var naar = await Reistijd.GeocodeAsync(doel.Locatie, ct);
            if (naar is null)
            {
                return "";
            }
            var route = await Reistijd.BerekenAsync(
                new Reistijd.Punt(reis.ThuisLat, reis.ThuisLon), naar, ct);
            if (route is null)
            {
                return "";
            }
            var vertrek = doel.Start - route.Duur - TimeSpan.FromMinutes(reis.BufferMinuten);
            return $"{doel.Titel} ({doel.Locatie}): {route.Duur.TotalMinutes:0} min rijden" +
                   (route.FileOpDeWeg ? $" — {route.Vertraging.TotalMinutes:0} min vertraging" : "") +
                   $", vertrek rond {vertrek:HH:mm}";
        }
        catch
        {
            // Routeerder onbereikbaar: de briefing meldt gewoon geen reistijd.
            return "";
        }
    }

    // ---------- Claude ----------

    private static string BouwContext(
        DateOnly vandaag,
        List<AgendaClient.AgendaItem> agenda,
        List<MijnTaak> taken,
        List<MailBericht> berichten,
        DagBriefingData briefing)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vandaag is {vandaag:dddd d MMMM yyyy}.");
        if (briefing.Weer.Length > 0)
        {
            sb.AppendLine($"Weer: {briefing.Weer}");
        }
        if (briefing.Reis.Length > 0)
        {
            sb.AppendLine($"Verplaatsing: {briefing.Reis}");
        }

        sb.AppendLine();
        sb.AppendLine("## Agenda vandaag");
        sb.AppendLine(agenda.Count == 0
            ? "(geen afspraken)"
            : string.Join("\n", agenda.Select(a =>
                "- " + BeschrijfAfspraak(a) +
                (a.Genodigden.Count > 0 ? $" — met {string.Join(", ", a.Genodigden.Take(6))}" : ""))));

        sb.AppendLine();
        sb.AppendLine("## Mijn open taken");
        sb.AppendLine(taken.Count == 0
            ? "(geen open taken)"
            : string.Join("\n", taken.Take(30).Select(t =>
                $"- [{Prio(t.Prioriteit)}] {t.Tekst}" +
                (t.Categorie.Length > 0 ? $" · {t.Categorie}" : "") +
                (t.Deadline is { } d
                    ? $" · deadline {d:d MMM}{(d < vandaag ? " (VERLOPEN)" : d == vandaag ? " (VANDAAG)" : "")}"
                    : ""))));

        var teamTaken = TeamTaskStore.Load().Taken
            .Where(t => !t.Klaar && t.Prioriteit == 0)
            .Take(15)
            .ToList();
        if (teamTaken.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Urgente teamtaken (bij collega's)");
            sb.AppendLine(string.Join("\n", teamTaken.Select(t => $"- {t.Lid}: {t.Tekst}")));
        }

        if (berichten.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Berichten die nog iets vragen");
            sb.AppendLine(string.Join("\n", berichten.Select(b =>
                $"- {(b.Urgent ? "URGENT · " : "")}{b.Van}: {b.Onderwerp}" +
                (b.Reden.Length > 0 ? $" — {b.Reden}" : ""))));
        }

        return sb.ToString();
    }

    private static string Prio(int prioriteit) => prioriteit switch
    {
        0 => "hoog",
        2 => "laag",
        _ => "normaal",
    };

    private static string Prompt(string context) =>
        $$"""
        Je bent de persoonlijke assistent van Maarten (zaakvoerder van een IT-bedrijf, werkt voor
        de klanten CED, Aqurat en RadiologyPartners). Hieronder staat alles wat vandaag op zijn
        bord ligt. Maak daar één korte dagstartbriefing van.

        Regels:
        - Nederlands, zakelijk maar vlot, geen aanhef of afsluiting.
        - Wees concreet en verwijs naar de echte afspraken, taken en namen uit de context.
        - Kijk naar samenhang: een taak die past bij een afspraak van vandaag, een verlopen
          deadline, te weinig ruimte tussen twee afspraken, iemand die al even op antwoord wacht.
        - Verzin niets bij wat niet in de context staat.
        - "focus": maximaal 4 punten, elk één zin, gerangschikt op belang.
        - "attentie": alleen echte risico's; laat de lijst leeg als er niets speelt.

        Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
        {"samenvatting": "2 à 3 zinnen over hoe de dag eruitziet", "focus": ["…", "…"], "attentie": ["…"]}

        Context:
        ---
        {{context}}
        ---
        """;

    private static string Tekst(JsonElement root, string naam) =>
        root.TryGetProperty(naam, out var waarde) && waarde.ValueKind == JsonValueKind.String
            ? waarde.GetString() ?? ""
            : "";

    private static List<string> Lijst(JsonElement root, string naam)
    {
        var resultaat = new List<string>();
        if (root.TryGetProperty(naam, out var waarde) && waarde.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in waarde.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } tekst)
                {
                    resultaat.Add(tekst);
                }
            }
        }
        return resultaat;
    }

    // ---------- Opslag ----------

    private static DagBriefingData? Laad()
    {
        try
        {
            if (File.Exists(BriefingFile))
            {
                return JsonSerializer.Deserialize<DagBriefingData>(File.ReadAllText(BriefingFile), JsonOpts);
            }
        }
        catch
        {
            // Onleesbaar: de briefing wordt gewoon opnieuw gemaakt.
        }
        return null;
    }

    private static void Bewaar(DagBriefingData data)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(BriefingFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Niet kunnen bewaren mag het tonen van de briefing niet tegenhouden.
        }
    }
}
