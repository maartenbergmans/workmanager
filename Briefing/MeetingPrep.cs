using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>Voorbereiding van één afspraak: context uit mail, taken en vorige keren.</summary>
public sealed class MeetingPrepItem
{
    /// <summary>Unieke sleutel van de afspraak (starttijd + titel), om dubbel werk te vermijden.</summary>
    public string Sleutel { get; set; } = "";

    public DateTimeOffset Start { get; set; }
    public DateTimeOffset Einde { get; set; }
    public string Titel { get; set; } = "";
    public string Locatie { get; set; } = "";
    public List<string> Deelnemers { get; set; } = new();

    /// <summary>Korte schets van waar deze afspraak over gaat en wat er speelt.</summary>
    public string Samenvatting { get; set; } = "";

    /// <summary>Punten die je zelf ter sprake wil brengen.</summary>
    public List<string> Punten { get; set; } = new();

    /// <summary>Vragen die je best stelt of laat bevestigen.</summary>
    public List<string> Vragen { get; set; } = new();

    /// <summary>Reisregel (rijtijd + vertrekmoment); leeg bij een online afspraak.</summary>
    public string Reis { get; set; } = "";

    public DateTimeOffset GemaaktOp { get; set; } = DateTimeOffset.Now;
}

/// <summary>Bewaarde voorbereidingen en welke reiswaarschuwingen al gegeven zijn.</summary>
public sealed class MeetingPrepData
{
    public List<MeetingPrepItem> Items { get; set; } = new();

    /// <summary>Sleutels van afspraken waarvoor de vertrekmelding al getoond is.</summary>
    public List<string> Gewaarschuwd { get; set; } = new();
}

/// <summary>
/// Kijkt vooruit in de agenda en doet twee dingen. Voor elke afspraak die binnen het uur
/// begint stelt hij een voorbereiding samen: waar ging het vorige keer over, wat liep er
/// recent per mail met de deelnemers, welke van je eigen taken hangen ermee samen — Claude
/// maakt daar een briefing met gespreks- en vraagpunten van. Voor afspraken met een echt
/// adres berekent hij daarnaast de rijtijd met het verkeer van nu (zie <see cref="Reistijd"/>)
/// en waarschuwt hij op tijd dat je moet vertrekken.
/// De tray-timer roept <see cref="ZorgVoorAsync"/> elke tien minuten aan.
/// </summary>
public static class MeetingPrep
{
    /// <summary>Zoveel minuten vóór de start wordt de voorbereiding gemaakt.</summary>
    private const int PrepMinuten = 60;

    /// <summary>Binnen dit venster wordt de rijtijd berekend (ruim genoeg voor verre afspraken).</summary>
    private const int ReisVensterMinuten = 120;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string PrepFile = Path.Combine(DataDir, "meeting-preps.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Melding voor de tray: (titel, tekst, opent-de-voorbereiding).</summary>
    public static event Action<string, string, bool>? Melding;

    private static bool _bezig;
    private static List<AgendaClient.AgendaItem>? _agendaCache;
    private static DateTimeOffset _agendaOpgehaald;

    /// <summary>De bewaarde voorbereidingen, nieuwste eerst.</summary>
    public static List<MeetingPrepItem> Recent() =>
        Laad().Items.OrderByDescending(i => i.Start).ToList();

    /// <summary>De voorbereiding van de eerstvolgende afspraak, als die er is.</summary>
    public static MeetingPrepItem? Volgende() =>
        Laad().Items
            .Where(i => i.Start > DateTimeOffset.Now.AddMinutes(-30))
            .OrderBy(i => i.Start)
            .FirstOrDefault();

    /// <summary>
    /// Eén ronde: voorbereidingen maken voor wat er aankomt en vertrektijden bewaken.
    /// Slikt fouten in — dit draait op een timer en mag nooit de app storen.
    /// </summary>
    public static async Task ZorgVoorAsync(CancellationToken ct)
    {
        if (_bezig)
        {
            return;
        }
        _bezig = true;
        try
        {
            var agenda = await AgendaAsync(ct);
            var nu = DateTimeOffset.Now;
            var aankomend = agenda
                .Where(a => !a.HeleDag && a.Start > nu &&
                            a.Start <= nu.AddMinutes(Math.Max(PrepMinuten, ReisVensterMinuten)))
                .OrderBy(a => a.Start)
                .ToList();

            foreach (var afspraak in aankomend)
            {
                await BewaakVertrekAsync(afspraak, ct);
                if (afspraak.Start <= nu.AddMinutes(PrepMinuten))
                {
                    await MaakPrepAsync(afspraak, agenda, ct);
                }
            }
            RuimOp();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Achtergrondtaak: stil falen, volgende ronde opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Stelt de voorbereiding van één afspraak samen (als dat nog niet gebeurd is) en meldt
    /// hem in de tray. Ook los aanroepbaar vanuit het venster ("Nu voorbereiden").
    /// </summary>
    public static async Task<MeetingPrepItem?> MaakPrepAsync(
        AgendaClient.AgendaItem afspraak, List<AgendaClient.AgendaItem>? agenda,
        CancellationToken ct, bool forceer = false)
    {
        var sleutel = Sleutel(afspraak);
        var data = Laad();
        if (!forceer && data.Items.Any(i => i.Sleutel == sleutel))
        {
            return data.Items.First(i => i.Sleutel == sleutel);
        }

        agenda ??= await AgendaAsync(ct);
        var item = new MeetingPrepItem
        {
            Sleutel = sleutel,
            Start = afspraak.Start,
            Einde = afspraak.Einde,
            Titel = afspraak.Titel,
            Locatie = afspraak.Locatie,
            Deelnemers = afspraak.Genodigden.ToList(),
        };

        var context = await BouwContextAsync(afspraak, agenda, ct);
        try
        {
            var antwoord = await ClaudeDrafter.RunClaudeAsync(Prompt(afspraak, context), ct);
            using var doc = ClaudeDrafter.ParseJson(antwoord);
            item.Samenvatting = Tekst(doc.RootElement, "samenvatting");
            item.Punten = Lijst(doc.RootElement, "punten");
            item.Vragen = Lijst(doc.RootElement, "vragen");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.Samenvatting = $"(voorbereiding niet gelukt: {ex.Message})";
        }

        data = Laad();
        data.Items.RemoveAll(i => i.Sleutel == sleutel);
        data.Items.Add(item);
        Bewaar(data);

        Melding?.Invoke(
            $"Zo meteen: {afspraak.Titel}",
            item.Samenvatting.Length > 0
                ? Kort(item.Samenvatting, 180)
                : $"Begint om {afspraak.Start:HH:mm}",
            true);
        return item;
    }

    // ---------- Reisassistent ----------

    /// <summary>
    /// Berekent de rijtijd naar een afspraak met een echt adres en waarschuwt zodra het
    /// vertrekmoment nadert. Werkt de reisregel van een bestaande voorbereiding bij.
    /// </summary>
    private static async Task BewaakVertrekAsync(AgendaClient.AgendaItem afspraak, CancellationToken ct)
    {
        var reis = ReisSettings.Load();
        if (!reis.Aan || !reis.HeeftThuis || !IsEchtAdres(afspraak.Locatie))
        {
            return;
        }

        var naar = await Reistijd.GeocodeAsync(afspraak.Locatie, ct);
        if (naar is null)
        {
            return;
        }
        var route = await Reistijd.BerekenAsync(new Reistijd.Punt(reis.ThuisLat, reis.ThuisLon), naar, ct);
        if (route is null || route.Duur.TotalMinutes < reis.MinimumRijMinuten)
        {
            return;
        }

        var vertrek = afspraak.Start - route.Duur - TimeSpan.FromMinutes(reis.BufferMinuten);
        var regel = $"{route.Duur.TotalMinutes:0} min rijden ({route.Kilometer:0.#} km, via {route.Bron})" +
                    (route.FileOpDeWeg ? $", {route.Vertraging.TotalMinutes:0} min vertraging" : "") +
                    $" — vertrek om {vertrek:HH:mm}";

        var data = Laad();
        var sleutel = Sleutel(afspraak);
        if (data.Items.FirstOrDefault(i => i.Sleutel == sleutel) is { } bestaand)
        {
            bestaand.Reis = regel;
        }

        if (!data.Gewaarschuwd.Contains(sleutel) &&
            DateTimeOffset.Now >= vertrek - TimeSpan.FromMinutes(reis.WaarschuwMinuten))
        {
            data.Gewaarschuwd.Add(sleutel);
            Bewaar(data);
            Melding?.Invoke(
                DateTimeOffset.Now >= vertrek
                    ? $"Vertrekken naar {afspraak.Titel}"
                    : $"Over {(vertrek - DateTimeOffset.Now).TotalMinutes:0} min vertrekken",
                $"{afspraak.Locatie}\n{regel}",
                false);
            return;
        }
        Bewaar(data);
    }

    /// <summary>
    /// Of een locatieveld een adres is waar je naartoe rijdt, en niet een videovergaderlink
    /// of een zaalnaam in het eigen gebouw.
    /// </summary>
    public static bool IsEchtAdres(string locatie)
    {
        locatie = locatie.Trim();
        if (locatie.Length < 6)
        {
            return false;
        }
        string[] online =
        {
            "meet.google", "zoom.us", "teams.microsoft", "teams.live", "webex", "whereby",
            "http://", "https://", "skype", "gotomeeting", "telefonisch", "online", "bellen",
        };
        if (online.Any(o => locatie.Contains(o, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        // Een adres heeft normaal een huisnummer of een postcode; een zaalnaam niet.
        return Regex.IsMatch(locatie, @"\d");
    }

    // ---------- Context voor Claude ----------

    private static async Task<string> BouwContextAsync(
        AgendaClient.AgendaItem afspraak, List<AgendaClient.AgendaItem> agenda, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Afspraak: {afspraak.Titel}");
        sb.AppendLine($"Wanneer: {afspraak.Start:dddd d MMMM HH:mm}–{afspraak.Einde:HH:mm}");
        if (afspraak.Locatie.Length > 0)
        {
            sb.AppendLine($"Waar: {afspraak.Locatie}");
        }
        if (afspraak.Genodigden.Count > 0)
        {
            sb.AppendLine($"Deelnemers: {string.Join(", ", afspraak.Genodigden)}");
        }
        if (afspraak.Omschrijving.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Omschrijving uit de agenda:");
            sb.AppendLine(Kort(afspraak.Omschrijving, 1500));
        }

        // Vorige keren dat deze afspraak plaatsvond (zelfde titel, laatste twee maanden).
        var vorige = agenda
            .Where(a => a.Start < DateTimeOffset.Now &&
                        a.Titel.Equals(afspraak.Titel, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Start)
            .Take(3)
            .ToList();
        if (vorige.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Deze afspraak was er eerder op: " +
                          string.Join(", ", vorige.Select(v => v.Start.ToString("d MMM"))));
        }

        // Recente mail met de deelnemers.
        var adressen = afspraak.Genodigden
            .Select(AdresUit)
            .Where(a => a.Length > 0)
            .Take(5)
            .ToList();
        var settings = MailReplySettings.Load();
        if (adressen.Count > 0 && settings.AppWachtwoord.Length > 0)
        {
            try
            {
                var regels = await GmailClient.CorrespondentieMetAsync(settings, adressen, 3, 8, ct);
                if (regels.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Recente mail met de deelnemers (oudste eerst):");
                    sb.AppendLine(string.Join("\n\n", regels));
                }
            }
            catch
            {
                // Geen mailcontext: de voorbereiding gaat door met wat er wel is.
            }
        }

        // Eigen taken die met deze afspraak of deelnemers te maken hebben.
        var trefwoorden = Trefwoorden(afspraak);
        var taken = MijnTaakStore.Load().Taken
            .Where(t => !t.Klaar && trefwoorden.Any(w =>
                t.Tekst.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                t.Categorie.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(10)
            .ToList();
        if (taken.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Mijn open taken die hierbij lijken te horen:");
            sb.AppendLine(string.Join("\n", taken.Select(t =>
                $"- {t.Tekst}" + (t.Deadline is { } d ? $" (deadline {d:d MMM})" : ""))));
        }

        // Teamtaken bij dezelfde klant of persoon.
        var teamTaken = TeamTaskStore.Load().Taken
            .Where(t => !t.Klaar && trefwoorden.Any(w =>
                t.Tekst.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                t.Lid.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(10)
            .ToList();
        if (teamTaken.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Lopende teamtaken die hierbij lijken te horen:");
            sb.AppendLine(string.Join("\n", teamTaken.Select(t => $"- {t.Lid}: {t.Tekst}")));
        }

        return sb.ToString();
    }

    /// <summary>Woorden waarop taken gekoppeld worden: klantnamen uit de titel en voornamen van deelnemers.</summary>
    private static List<string> Trefwoorden(AgendaClient.AgendaItem afspraak)
    {
        var woorden = afspraak.Titel
            .Split(new[] { ' ', '-', '–', ':', ',', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .ToList();
        woorden.AddRange(afspraak.Genodigden
            .Select(d => d.Split('<')[0].Trim())
            .SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(n => n.Length >= 4));
        return woorden.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    private static string AdresUit(string deelnemer)
    {
        var start = deelnemer.IndexOf('<');
        var einde = deelnemer.IndexOf('>');
        if (start >= 0 && einde > start)
        {
            return deelnemer[(start + 1)..einde].Trim();
        }
        return deelnemer.Contains('@') ? deelnemer.Trim() : "";
    }

    private static string Prompt(AgendaClient.AgendaItem afspraak, string context) =>
        $$"""
        Je bent de assistent van Maarten (zaakvoerder van een IT-bedrijf, klanten CED, Aqurat en
        RadiologyPartners). Hij heeft zo meteen de afspraak "{{afspraak.Titel}}". Hieronder staat
        alle context die de computer erover kon vinden. Bereid hem in één minuut voor.

        Regels:
        - Nederlands, kort en concreet; geen aanhef of afsluiting.
        - Baseer je alleen op de context; verzin geen feiten, namen of afspraken.
        - "punten": wat Maarten zelf ter sprake moet brengen (max. 5, elk één zin).
        - "vragen": wat hij best vraagt of laat bevestigen (max. 4). Leeg als er niets te vragen is.
        - Als de context mager is, zeg dat eerlijk in de samenvatting en houd de lijsten kort.

        Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
        {"samenvatting": "2 à 3 zinnen: waar gaat dit over en wat speelt er", "punten": ["…"], "vragen": ["…"]}

        Context:
        ---
        {{context}}
        ---
        """;

    // ---------- Agenda ----------

    /// <summary>
    /// De agenda van twee maanden terug tot morgen, hooguit elke tien minuten opnieuw
    /// opgehaald. Het venster loopt bewust ver terug: zo kent de voorbereiding ook de vorige
    /// keren dat dezelfde afspraak plaatsvond, zonder extra download.
    /// </summary>
    private static async Task<List<AgendaClient.AgendaItem>> AgendaAsync(CancellationToken ct)
    {
        if (_agendaCache is not null && DateTimeOffset.Now - _agendaOpgehaald < TimeSpan.FromMinutes(10))
        {
            return _agendaCache;
        }
        var settings = AgendaSettings.Load();
        if (!settings.Compleet)
        {
            return new List<AgendaClient.AgendaItem>();
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var items = await AgendaClient.OphalenAsync(settings.Urls, vandaag.AddDays(-60), vandaag.AddDays(1), ct);
        _agendaCache = items;
        _agendaOpgehaald = DateTimeOffset.Now;
        return items;
    }

    /// <summary>Vergeet de gecachte agenda, zodat de volgende ronde verse gegevens ophaalt.</summary>
    public static void VergeetAgenda() => _agendaCache = null;

    // ---------- Opslag ----------

    private static string Sleutel(AgendaClient.AgendaItem afspraak) =>
        $"{afspraak.Start:yyyy-MM-ddTHH:mm}|{afspraak.Titel}";

    private static string Kort(string tekst, int max) =>
        tekst.Length > max ? tekst[..max] + "…" : tekst;

    private static MeetingPrepData Laad()
    {
        try
        {
            if (File.Exists(PrepFile) &&
                JsonSerializer.Deserialize<MeetingPrepData>(File.ReadAllText(PrepFile), JsonOpts) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new MeetingPrepData();
    }

    private static void Bewaar(MeetingPrepData data)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(PrepFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Voorbereidingen ouder dan een week weggooien, zodat het bestand klein blijft.</summary>
    private static void RuimOp()
    {
        var data = Laad();
        var grens = DateTimeOffset.Now.AddDays(-7);
        var voor = data.Items.Count;
        data.Items.RemoveAll(i => i.Start < grens);
        var levend = data.Items.Select(i => i.Sleutel).ToHashSet();
        var gewaarschuwdVoor = data.Gewaarschuwd.Count;
        data.Gewaarschuwd.RemoveAll(s => !levend.Contains(s) && !s.StartsWith(
            DateTime.Today.ToString("yyyy-MM-dd"), StringComparison.Ordinal));
        if (voor != data.Items.Count || gewaarschuwdVoor != data.Gewaarschuwd.Count)
        {
            Bewaar(data);
        }
    }

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
}
