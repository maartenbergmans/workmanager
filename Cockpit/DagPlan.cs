using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Eén item in de dagplanning: een taak, een mail die beantwoord moet worden, of een vaste
/// afspraak. Vaste afspraken hebben een begin- en eindtijd en schuiven niet; de rest wordt
/// eromheen ingepland.
/// </summary>
public sealed class PlanItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>"taak", "mail" of "afspraak".</summary>
    public string Soort { get; set; } = "taak";
    public string Tekst { get; set; } = "";
    /// <summary>Geschatte duur in minuten (voor afspraken: de echte lengte).</summary>
    public int Minuten { get; set; } = 30;
    /// <summary>Korte motivatie van de plek in de volgorde ("deadline vandaag", "kort werk tussendoor").</summary>
    public string Waarom { get; set; } = "";
    /// <summary>Vast tijdstip (afspraken); null = vrij in te plannen.</summary>
    public DateTimeOffset? VastStart { get; set; }
    public DateTimeOffset? VastEinde { get; set; }
    /// <summary>Vroegste moment waarop dit item kan (startuur van de taak); null = geen beperking.</summary>
    public DateTimeOffset? NietVoor { get; set; }
    /// <summary>Koppeling terug naar de bron, zodat afvinken ook de echte taak afvinkt.</summary>
    public Guid? TaakId { get; set; }
    public string MailId { get; set; } = "";
    public bool Klaar { get; set; }
    public bool Overgeslagen { get; set; }

    /// <summary>Het item telt niet meer mee voor de resterende werktijd.</summary>
    public bool Afgehandeld => Klaar || Overgeslagen;

    /// <summary>
    /// Vast blok in de tijdlijn: een echte afspraak, of een "info"-item (afspraak die de
    /// agenda niet blokkeert — wél tonen, geen tijd voor reserveren). Niet af te vinken,
    /// niet te verslepen.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool VastBlok => Soort is "afspraak" or "info";
}

/// <summary>De planning van één dag, zoals bewaard in %APPDATA%\WorkManager\dagplan.json.</summary>
public sealed class DagPlanData
{
    public string Dag { get; set; } = "";
    public DateTimeOffset Gemaakt { get; set; }
    /// <summary>Tot hoe laat je vandaag wil werken (standaard 17:30).</summary>
    public string EindeWerkdag { get; set; } = "17:30";
    public List<PlanItem> Items { get; set; } = new();
}

/// <summary>
/// Stelt met Claude een werkvolgorde voor de dag samen: wat doe je nu, wat daarna, en geraak je
/// rond voor het einde van je werkdag. Vaste afspraken zijn ankers; taken en te beantwoorden
/// mails worden ertussen gelegd, met een geschatte duur per item.
/// </summary>
public static class DagPlan
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "dagplan.json");

    /// <summary>
    /// Waar de planbare dag begint: 08:30, behalve op dinsdag en donderdag (CED-dagen) — dan
    /// moet Maarten om 09:00 in Vilvoorde zijn en start het eigen werk daar.
    /// </summary>
    public static DateTimeOffset StartWerkdag(DateOnly dag)
    {
        var tijd = IsCedDag(dag) ? new TimeOnly(9, 0) : new TimeOnly(8, 30);
        return new DateTimeOffset(dag.ToDateTime(tijd));
    }

    private static bool IsCedDag(DateOnly dag) =>
        dag.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday;

    /// <summary>
    /// Blokkeert deze afspraak de agenda niet? Twee bronnen: de marker uit de afspraakdialoog
    /// (eigen Google-afspraken) en de lokale lijst uit <see cref="WerkbaarStore"/> (rechtsklik
    /// op eender welke afspraak). Dan is hij geen anker in de planning.
    /// </summary>
    public static bool KanDoorwerken(AgendaClient.AgendaItem m) =>
        m.Omschrijving.Contains(AgendaAfspraakForm.WerkbaarMarker, StringComparison.OrdinalIgnoreCase) ||
        m.Titel.Contains(AgendaAfspraakForm.WerkbaarMarker, StringComparison.OrdinalIgnoreCase) ||
        WerkbaarStore.Is(m);

    /// <summary>Vaste ankers die niet uit de agenda komen: op CED-dagen om 09:00 in Vilvoorde.</summary>
    private static IEnumerable<PlanItem> StandaardAnkers(DateOnly dag)
    {
        if (IsCedDag(dag))
        {
            var negen = new DateTimeOffset(dag.ToDateTime(new TimeOnly(9, 0)));
            yield return new PlanItem
            {
                Soort = "afspraak",
                Tekst = "Bij CED in Vilvoorde",
                Minuten = 0,
                VastStart = negen,
                VastEinde = negen,
                Waarom = "vaste CED-dag",
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Het plan van vandaag, of null als er nog geen gemaakt is (of het is van gisteren).</summary>
    public static DagPlanData? LaadVandaag()
    {
        try
        {
            if (!File.Exists(DataFile))
            {
                return null;
            }
            var data = JsonSerializer.Deserialize<DagPlanData>(File.ReadAllText(DataFile));
            return data?.Dag == DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") ? data : null;
        }
        catch
        {
            return null; // onleesbaar: doe alsof er nog geen plan is
        }
    }

    public static void Bewaar(DagPlanData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort: dan is het plan niet blijvend, maar het venster werkt gewoon.
        }
    }

    /// <summary>
    /// Laat Claude een nieuwe volgorde en duurschatting maken op basis van de open taken, de
    /// meetings van vandaag en de mails die nog een antwoord vragen.
    /// </summary>
    public static async Task<DagPlanData> MaakAsync(
        List<AgendaClient.AgendaItem> meetings, string eindeWerkdag, CancellationToken ct)
    {
        var nu = DateTimeOffset.Now;
        var vandaag = DateOnly.FromDateTime(nu.LocalDateTime);

        // Alleen wat vandaag speelt: verlopen deadlines, deadlines van vandaag en taken zonder
        // deadline. Een taak die pas volgende week moet, hoort niet in de planning van vandaag.
        var taken = MijnTaakStore.Load().Taken
            .Where(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart)
            .Where(t => t.Deadline is not { } d || d <= vandaag)
            .OrderBy(t => t.Deadline ?? DateOnly.MaxValue)
            .ThenBy(t => t.Prioriteit)
            .Take(25)
            .ToList();

        // Mails die volgens de screening nog een antwoord vragen (rood = vandaag).
        var mails = new List<MailBericht>();
        try
        {
            mails = CockpitCache.Load()
                .Where(m => m.Urgent || m.ConceptKlaar)
                .OrderByDescending(m => m.Urgent)
                .Take(12)
                .ToList();
        }
        catch
        {
            // Geen cache: dan zonder mails plannen.
        }

        // Alleen afspraken die nog moeten komen én echt blokkeren zijn ankers voor "wat nu".
        var komende = meetings
            .Where(m => !m.HeleDag && m.Einde > nu && !KanDoorwerken(m))
            .OrderBy(m => m.Start)
            .ToList();
        var werkbare = meetings
            .Where(m => !m.HeleDag && m.Einde > nu && KanDoorwerken(m))
            .OrderBy(m => m.Start)
            .ToList();

        var invoer = new StringBuilder();
        invoer.AppendLine($"Nu: {nu:dddd d MMMM yyyy HH:mm}. Einde werkdag: {eindeWerkdag}.");
        invoer.AppendLine($"De werkdag begint om {StartWerkdag(vandaag).LocalDateTime:HH:mm}.");
        if (IsCedDag(vandaag))
        {
            invoer.AppendLine("Vandaag is een CED-dag: vanaf 09:00 is Maarten bij CED in " +
                              "Vilvoorde; plan vóór 09:00 niets in.");
        }
        if (werkbare.Count > 0)
        {
            invoer.AppendLine("Ter info (blokkeert de agenda NIET, gewoon doorwerken): " +
                string.Join("; ", werkbare.Select(m =>
                    $"{m.Start.ToLocalTime():HH:mm} {m.Titel.Replace(AgendaAfspraakForm.WerkbaarMarker, "").Trim()}")));
        }
        invoer.AppendLine();
        invoer.AppendLine("AFSPRAKEN (vast, verschuiven niet):");
        foreach (var m in komende)
        {
            invoer.AppendLine(
                $"- [afspraak] {m.Start.ToLocalTime():HH:mm}-{m.Einde.ToLocalTime():HH:mm} {m.Titel}");
        }
        if (komende.Count == 0)
        {
            invoer.AppendLine("- (geen)");
        }
        invoer.AppendLine();
        invoer.AppendLine("TAKEN (vrij in te plannen, alleen wat vandaag speelt):");
        foreach (var t in taken)
        {
            var deadline = (t.Deadline is { } d
                ? (d < vandaag ? $"deadline VERLOPEN {d:dd/MM}" : "deadline vandaag")
                : "geen deadline") +
                (t.StartUur is { } su ? $", niet vóór {su:HH\\:mm}" : "");
            var prio = t.Prioriteit switch { 0 => "hoog", 2 => "laag", _ => "normaal" };
            invoer.AppendLine($"- [taak:{t.Id}] {t.Tekst} ({t.Categorie}, prioriteit {prio}, {deadline})");
        }
        if (taken.Count == 0)
        {
            invoer.AppendLine("- (geen)");
        }
        invoer.AppendLine();
        invoer.AppendLine("MAILS die nog een antwoord vragen:");
        foreach (var m in mails)
        {
            var id = m.MessageId.Length > 0 ? m.MessageId : m.Onderwerp;
            invoer.AppendLine($"- [mail:{id}] {m.Van}: {Kort(m.Onderwerp, 80)}" +
                              (m.Urgent ? " (dringend)" : "") +
                              (m.ConceptKlaar ? " (concept staat al klaar)" : ""));
        }
        if (mails.Count == 0)
        {
            invoer.AppendLine("- (geen)");
        }

        var prompt =
            $$"""
            Je bent de dagplanner van Maarten (zelfstandig IT-consultant). Zet zijn werk van
            vandaag in de beste volgorde en schat per item in hoe lang het duurt.

            Regels:
            - Afspraken zijn vaste ankers: neem ze over met hun eigen tijd en duur, en plan er
              geen werk overheen.
            - Schat de duur realistisch in minuten (5, 10, 15, 20, 30, 45, 60, 90, 120). Een mail
              beantwoorden is meestal 5-15 minuten; een concept dat al klaarstaat 5.
            - Volgorde: verlopen en vandaag vervallende deadlines eerst, dan dringende mails,
              dan hoge prioriteit. Zet zwaar denkwerk liefst vroeg, korte klusjes in de gaatjes
              vlak vóór een afspraak.
            - Vóór 09:00 kun je niemand bellen: taken die telefoneren inhouden ("bellen",
              "telefoon", iemand opbellen) plan je nooit vóór 09:00.
            - Sommige taken hebben een "niet vóór"-tijd: plan die nooit eerder dan dat uur.
            - "waarom": maximaal 6 woorden Nederlands, waarom dit item op die plek staat.
            - Neem elk taak- en mail-item uit de invoer precies één keer over, met exact dezelfde
              id tussen de blokhaken.

            Antwoord UITSLUITEND met één JSON-array, zonder tekst of markdown eromheen:
            [{"id": "taak:<guid>" of "mail:<id>" of "afspraak", "soort": "taak|mail|afspraak",
              "tekst": "…", "minuten": 30, "waarom": "…"}]

            Invoer:
            ---
            {{invoer}}
            ---
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        var start = output.IndexOf('[');
        var einde = output.LastIndexOf(']');
        if (start < 0 || einde <= start)
        {
            throw new InvalidOperationException("Geen JSON-lijst in het antwoord van Claude.");
        }

        var plan = new DagPlanData
        {
            Dag = vandaag.ToString("yyyy-MM-dd"),
            Gemaakt = nu,
            EindeWerkdag = eindeWerkdag,
        };
        using var doc = JsonDocument.Parse(output[start..(einde + 1)]);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var tekst = (el.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "").Trim();
            if (tekst.Length == 0)
            {
                continue;
            }
            var id = el.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var soort = el.TryGetProperty("soort", out var s) ? s.GetString() ?? "taak" : "taak";
            var minuten = el.TryGetProperty("minuten", out var m) && m.ValueKind == JsonValueKind.Number
                ? Math.Clamp(m.GetInt32(), 5, 240) : 30;
            var item = new PlanItem
            {
                Soort = soort,
                Tekst = tekst,
                Minuten = minuten,
                Waarom = el.TryGetProperty("waarom", out var w) ? w.GetString() ?? "" : "",
            };
            if (id.StartsWith("taak:", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(id[5..], out var taakId))
            {
                item.TaakId = taakId;
                item.Soort = "taak";
                // Het startuur is hard: ook als het model zich vergist, plant de tijdlijn
                // dit item nooit vóór dat uur.
                if (taken.FirstOrDefault(t => t.Id == taakId)?.StartUur is { } uur)
                {
                    item.NietVoor = new DateTimeOffset(vandaag.ToDateTime(uur));
                }
            }
            else if (id.StartsWith("mail:", StringComparison.OrdinalIgnoreCase))
            {
                item.MailId = id[5..];
                item.Soort = "mail";
            }
            plan.Items.Add(item);
        }

        // Afspraken komen uit de agenda, niet uit het model: die zetten we er zelf bij met hun
        // echte tijden (zo kan het model er niet naast zitten).
        plan.Items.RemoveAll(i => i.VastBlok);
        plan.Items.AddRange(StandaardAnkers(vandaag));
        foreach (var m in komende)
        {
            plan.Items.Add(new PlanItem
            {
                Soort = "afspraak",
                Tekst = m.Titel,
                Minuten = Math.Max(5, (int)(m.Einde - m.Start).TotalMinutes),
                VastStart = m.Start,
                VastEinde = m.Einde,
                Waarom = "vaste afspraak",
            });
        }
        plan.Items.AddRange(InfoItems(werkbare));
        Bewaar(plan);
        return plan;
    }

    /// <summary>
    /// Werkbare afspraken ("blokkeert mijn agenda niet") als ter-info-regels: je wil ze zien
    /// in de tijdlijn (de levering komt eraan), maar er wordt geen werktijd voor gereserveerd.
    /// </summary>
    private static IEnumerable<PlanItem> InfoItems(IEnumerable<AgendaClient.AgendaItem> werkbare) =>
        werkbare.Select(m => new PlanItem
        {
            Soort = "info",
            Tekst = m.Titel.Replace(AgendaAfspraakForm.WerkbaarMarker, "",
                StringComparison.OrdinalIgnoreCase).Trim(),
            Minuten = Math.Max(5, (int)(m.Einde - m.Start).TotalMinutes),
            VastStart = m.Start,
            VastEinde = m.Einde,
            Waarom = "ter info — blokkeert niet",
        });

    /// <summary>
    /// Houdt een bestaand plan bij de tijd zonder Claude opnieuw te bevragen: nieuw binnengekomen
    /// dringende mails en nieuwe taken van vandaag komen erbij, afgevinkte taken en verdwenen
    /// mails vallen eruit, en nieuwe afspraken worden overgenomen. Geeft terug wat er bijkwam,
    /// zodat de cockpit dat kan melden. Voor een échte herschikking klik je "Plan mijn dag".
    /// </summary>
    public static (int Bij, int Weg) VulAan(
        DagPlanData plan, List<MailBericht> mails, List<AgendaClient.AgendaItem> meetings)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var nu = DateTimeOffset.Now;
        var bij = 0;
        var weg = 0;

        // ---- Taken die intussen elders afgevinkt (of gesnoozed) zijn, hier ook afsluiten ----
        // Idem voor taken die niet meer bij vandaag horen: een deadline die naar de toekomst
        // verzet is hoort niet in het plan van vandaag te blijven staan.
        var taken = MijnTaakStore.Load().Taken;
        foreach (var item in plan.Items.Where(i => i.TaakId is not null && !i.Afgehandeld).ToList())
        {
            var bron = taken.FirstOrDefault(t => t.Id == item.TaakId);
            if (bron is null || bron.Klaar || bron.Gesnoozed)
            {
                item.Klaar = bron?.Klaar ?? false;
                item.Overgeslagen = !item.Klaar;
                weg++;
            }
            else if (bron.Deadline is { } dl && dl > vandaag)
            {
                plan.Items.Remove(item); // pas later aan de beurt: helemaal uit het plan
                weg++;
            }
        }

        // ---- Nieuwe taken van vandaag erbij ----
        foreach (var t in taken.Where(t =>
            !t.Klaar && !t.Gesnoozed && !t.NogNietGestart &&
            (t.Deadline is not { } d || d <= vandaag)))
        {
            if (plan.Items.Any(i => i.TaakId == t.Id))
            {
                continue;
            }
            var nieuw = new PlanItem
            {
                Soort = "taak",
                Tekst = t.Tekst,
                TaakId = t.Id,
                Minuten = 30,
                Waarom = "nieuw op de lijst",
                NietVoor = t.StartUur is { } uur
                    ? new DateTimeOffset(vandaag.ToDateTime(uur)) : null,
            };
            // Een storing schuift alles opzij: helemaal vooraan in het plan.
            if (t.Tekst.StartsWith(AlarmMails.TaakPrefix, StringComparison.Ordinal))
            {
                nieuw.Waarom = "storing — meteen oppakken";
                plan.Items.Insert(0, nieuw);
            }
            else
            {
                plan.Items.Add(nieuw);
            }
            bij++;
        }

        // ---- Mails die om een antwoord vragen ----
        var actueel = mails.Where(m => m.Urgent || m.ConceptKlaar).ToList();
        foreach (var m in actueel)
        {
            var id = m.MessageId.Length > 0 ? m.MessageId : m.Onderwerp;
            if (plan.Items.Any(i => i.MailId == id))
            {
                continue;
            }
            var item = new PlanItem
            {
                Soort = "mail",
                Tekst = $"{m.Van}: {(m.Onderwerp.Length > 0 ? m.Onderwerp : "(geen onderwerp)")}",
                MailId = id,
                Minuten = m.ConceptKlaar ? 5 : 15,
                Waarom = m.Urgent ? "net binnen, dringend" : "net binnen",
            };
            // Dringend werk hoort vooraan, maar niet vóór waar je nu mee bezig bent.
            var positie = m.Urgent
                ? Math.Min(1, plan.Items.Count(i => !i.Afgehandeld))
                : plan.Items.Count;
            plan.Items.Insert(Math.Min(positie, plan.Items.Count), item);
            bij++;
        }
        // Mails die intussen beantwoord of gearchiveerd zijn, hoeven niet meer.
        foreach (var item in plan.Items.Where(i => i.MailId.Length > 0 && !i.Afgehandeld))
        {
            if (!actueel.Any(m => (m.MessageId.Length > 0 ? m.MessageId : m.Onderwerp) == item.MailId))
            {
                item.Klaar = true;
                weg++;
            }
        }

        // ---- Afspraken gelijkzetten met de agenda (werkbare blokkeren niet, maar staan er
        // wel als ter-info-regel bij) ----
        plan.Items.RemoveAll(i => i.VastBlok);
        plan.Items.AddRange(StandaardAnkers(vandaag));
        foreach (var m in meetings
                     .Where(m => !m.HeleDag && m.Einde > nu && !KanDoorwerken(m))
                     .OrderBy(m => m.Start))
        {
            plan.Items.Add(new PlanItem
            {
                Soort = "afspraak",
                Tekst = m.Titel,
                Minuten = Math.Max(5, (int)(m.Einde - m.Start).TotalMinutes),
                VastStart = m.Start,
                VastEinde = m.Einde,
                Waarom = "vaste afspraak",
            });
        }
        plan.Items.AddRange(InfoItems(meetings
            .Where(m => !m.HeleDag && m.Einde > nu && KanDoorwerken(m))
            .OrderBy(m => m.Start)));

        if (bij > 0 || weg > 0)
        {
            Bewaar(plan);
        }
        return (bij, weg);
    }

    /// <summary>
    /// De lunch-luider: staat er op een werkdag tussen 12:00 en 13:30 nog geen pauze in het
    /// plan terwijl er wél open werk ligt, dan schuift er een blokje van 20 minuten lunch
    /// vooraan de wachtrij in. Ook machines hebben stroom nodig. True = toegevoegd.
    /// </summary>
    public static bool VoegLunchToe()
    {
        var nu = DateTime.Now;
        if (nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            nu.TimeOfDay < new TimeSpan(12, 0, 0) || nu.TimeOfDay > new TimeSpan(13, 30, 0))
        {
            return false;
        }
        var plan = LaadVandaag();
        if (plan is null || plan.Items.Count == 0 ||
            plan.Items.Any(i =>
                i.Tekst.Contains("lunch", StringComparison.OrdinalIgnoreCase) ||
                i.Tekst.Contains("pauze", StringComparison.OrdinalIgnoreCase)) ||
            !plan.Items.Any(i => !i.VastBlok && !i.Afgehandeld))
        {
            return false; // weekend, geen plan, pauze staat er al, of alles is toch al klaar
        }
        var lunch = new PlanItem
        {
            Soort = "taak",
            Tekst = "🍽️ Lunchpauze",
            Minuten = 20,
            Waarom = "ook machines hebben stroom nodig",
        };
        var idx = plan.Items.FindIndex(i => !i.VastBlok && !i.Afgehandeld);
        plan.Items.Insert(idx < 0 ? plan.Items.Count : idx, lunch);
        Bewaar(plan);
        return true;
    }

    /// <summary>
    /// Tegenhanger van de lunch-luider: is de middag voorbij (na 14:00) en staat de
    /// lunchpauze nog open, dan wordt ze stilletjes afgevinkt — anders blijft "Lunchpauze"
    /// tot 's avonds als NU-item in de cockpit staan. True = opgeruimd.
    /// </summary>
    public static bool RuimLunchOp()
    {
        if (DateTime.Now.TimeOfDay < new TimeSpan(14, 0, 0))
        {
            return false;
        }
        var plan = LaadVandaag();
        var lunch = plan?.Items.FirstOrDefault(i =>
            !i.Afgehandeld && i.Tekst.Contains("Lunchpauze", StringComparison.OrdinalIgnoreCase));
        if (plan is null || lunch is null)
        {
            return false;
        }
        lunch.Klaar = true;
        Bewaar(plan);
        return true;
    }

    /// <summary>
    /// Zet het plan om in een tijdlijn: elk item krijgt een begintijd, rekening houdend met de
    /// vaste afspraken. Afgehandelde items vallen weg.
    /// </summary>
    public static List<(PlanItem Item, DateTimeOffset Start)> Tijdlijn(DagPlanData plan)
    {
        var resultaat = new List<(PlanItem, DateTimeOffset)>();
        // Slots beginnen op de werkdagstart (08:30, CED-dagen 09:00); later op de dag op "nu".
        var startWerk = StartWerkdag(DateOnly.FromDateTime(DateTime.Now));
        var cursor = DateTimeOffset.Now > startWerk ? DateTimeOffset.Now : startWerk;
        var afspraken = plan.Items
            .Where(i => i is { Soort: "afspraak", Afgehandeld: false } && i.VastStart is not null)
            .OrderBy(i => i.VastStart!.Value)
            .ToList();
        var wachtend = plan.Items.Where(i => !i.VastBlok && !i.Afgehandeld).ToList();

        while (wachtend.Count > 0)
        {
            // Het eerste item (planvolgorde) dat nu al mág — een startuur ("niet vóór") laat
            // andere items voorgaan tot het zover is.
            var item = wachtend.FirstOrDefault(i => i.NietVoor is not { } nv || nv <= cursor);
            if (item is null)
            {
                // Alles wat overblijft moet nog wachten: spring naar het vroegste startuur.
                cursor = wachtend.Min(i => i.NietVoor!.Value);
                continue;
            }
            // Botst het item met de eerstvolgende afspraak? Dan eerst de afspraak plaatsen.
            while (afspraken.FirstOrDefault() is { } afspraak &&
                   cursor.AddMinutes(item.Minuten) > afspraak.VastStart!.Value)
            {
                resultaat.Add((afspraak, afspraak.VastStart.Value));
                cursor = afspraak.VastEinde ?? afspraak.VastStart.Value.AddMinutes(afspraak.Minuten);
                if (cursor < DateTimeOffset.Now)
                {
                    cursor = DateTimeOffset.Now;
                }
                afspraken.RemoveAt(0);
            }
            resultaat.Add((item, cursor));
            cursor = cursor.AddMinutes(item.Minuten);
            wachtend.Remove(item);
        }
        // Afspraken die na al het werk komen.
        foreach (var afspraak in afspraken)
        {
            resultaat.Add((afspraak, afspraak.VastStart!.Value));
        }
        // Ter-info-items (werkbare afspraken) op hun eigen tijd tussenvoegen: ze zijn
        // zichtbaar in de tijdlijn maar schuiven niets op — de cursor bleef er los van.
        foreach (var info in plan.Items.Where(i =>
                     i is { Soort: "info", Afgehandeld: false, VastStart: not null }))
        {
            resultaat.Add((info, info.VastStart!.Value));
        }
        return resultaat.OrderBy(r => r.Item2).ToList();
    }

    /// <summary>
    /// Geraak je rond? Geeft het moment waarop je klaar bent en hoeveel tijd je te kort komt
    /// (of overhoudt) ten opzichte van het einde van je werkdag.
    /// </summary>
    public static (DateTimeOffset Klaar, TimeSpan Verschil, int MinutenWerk) Haalbaarheid(DagPlanData plan)
    {
        // Info-items tellen niet mee: een levering die tot 17:20 kan komen maakt je dag
        // niet langer — je werkt er gewoon doorheen.
        var tijdlijn = Tijdlijn(plan).Where(r => r.Item.Soort != "info").ToList();
        var klaar = tijdlijn.Count > 0
            ? tijdlijn.Max(r => r.Start.AddMinutes(r.Item.Minuten))
            : DateTimeOffset.Now;
        var werk = plan.Items.Where(i => !i.VastBlok && !i.Afgehandeld).Sum(i => i.Minuten);
        var einde = EindeMoment(plan);
        return (klaar, einde - klaar, werk);
    }

    public static DateTimeOffset EindeMoment(DagPlanData plan)
    {
        var tijd = TimeOnly.TryParse(plan.EindeWerkdag, out var t) ? t : new TimeOnly(17, 30);
        return new DateTimeOffset(DateOnly.FromDateTime(DateTime.Now).ToDateTime(tijd));
    }

    private static string Kort(string tekst, int max) =>
        tekst.Length <= max ? tekst : tekst[..max] + "…";
}
