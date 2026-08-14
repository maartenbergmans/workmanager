using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de persoonlijke WorkManager-webpagina op de hosting: URL van wm.php en
/// het gedeelde token (DPAPI-versleuteld). Persistent in
/// %APPDATA%\WorkManager\wm-web-settings.json. Eigen token, los van dat van de
/// AH-bestelpagina: die link ligt bij Hilke op de gsm en mag niet bij taken of mail kunnen.
/// </summary>
public class WmWebSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "wm-web-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Url { get; set; } = "";
    public string TokenVersleuteld { get; set; } = "";
    public string PushTopicVersleuteld { get; set; } = "";

    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenVersleuteld))
            {
                return "";
            }
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(TokenVersleuteld), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }
        set => TokenVersleuteld = string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Het ntfy.sh-topic voor pushmeldingen. Ook dit is versleuteld: wie het topic kent, kan
    /// meelezen én meesturen — het is dus een wachtwoord, geen naam.
    /// </summary>
    [JsonIgnore]
    public string PushTopic
    {
        get => Ontsleutel(PushTopicVersleuteld);
        set => PushTopicVersleuteld = Versleutel(value);
    }

    private static string Ontsleutel(string versleuteld)
    {
        if (string.IsNullOrEmpty(versleuteld))
        {
            return "";
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(versleuteld), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    private static string Versleutel(string waarde) => string.IsNullOrEmpty(waarde)
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(waarde), null, DataProtectionScope.CurrentUser));

    [JsonIgnore]
    public bool Compleet => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Token.Length > 0;

    /// <summary>De link om op de gsm te openen (token erin, zodat er niet ingelogd hoeft te worden).</summary>
    [JsonIgnore]
    public string Link => Compleet ? $"{Url}?t={Uri.EscapeDataString(Token)}" : "";

    public static WmWebSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<WmWebSettings>(File.ReadAllText(SettingsFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar bestand: koppeling staat dan gewoon uit.
        }
        return new WmWebSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Brug tussen de persoonlijke webpagina (wm.php) en deze pc. Twee richtingen:
/// (1) een snapshot van taken, agenda, wachtende berichten en de urenstand omhoog zetten,
/// zodat de pagina onderweg leesbaar is zonder iets van de pc te weten; (2) de wachtrij met
/// acties van de gsm ophalen en uitvoeren (taak afvinken, snoozen, nieuwe taak).
///
/// <para>Staat de pc uit, dan blijft een actie gewoon in de wachtrij staan tot hij weer
/// aangaat — de pagina meldt dat ook zo.</para>
/// </summary>
public class WmWebSync
{
    /// <summary>Hoe vaak het snapshot ververst wordt als er niets verandert.</summary>
    private static readonly TimeSpan SnapshotHoudbaar = TimeSpan.FromMinutes(4);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private bool _bezig;
    private DateTimeOffset _laatsteSnapshot = DateTimeOffset.MinValue;
    private string _laatsteInhoud = "";

    /// <summary>Vuurt met een korte melding zodra er een actie van de gsm verwerkt is.</summary>
    public event Action<string>? ActieVerwerkt;

    /// <summary>Eén pollronde: snapshot bijwerken en de actiewachtrij leeghalen. Stil bij fouten.</summary>
    public async Task PollAsync()
    {
        var settings = WmWebSettings.Load();
        if (_bezig || !settings.Compleet)
        {
            return;
        }
        _bezig = true;
        try
        {
            await VerwerkLocatiesAsync(settings);
            await VerwerkActiesAsync(settings);
            await ZetSnapshotAsync(settings);
            await PushMelding.RondeAsync();
        }
        catch
        {
            // Netwerk-/serverfout: volgende ronde opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    // ---------- Snapshot (omhoog) ----------

    private async Task ZetSnapshotAsync(WmWebSettings settings)
    {
        var snapshot = BouwSnapshot();
        var inhoud = JsonSerializer.Serialize(snapshot, JsonOpts);
        // Alleen sturen als er iets veranderd is, of als het snapshot oud wordt (de
        // "geleden"-tekst op de pagina moet blijven kloppen).
        if (inhoud == _laatsteInhoud && DateTimeOffset.Now - _laatsteSnapshot < SnapshotHoudbaar)
        {
            return;
        }
        await PostAsync(settings, "snapshot", new { snapshot });
        _laatsteInhoud = inhoud;
        _laatsteSnapshot = DateTimeOffset.Now;
    }

    private static object BouwSnapshot() => new
    {
        taken = Taken(),
        later = LaterTaken(),
        agenda = Agenda(),
        berichten = Berichten(),
        uren = Uren(),
        klanten = TimesheetStore.Klanten,
        plan = Plan(),
        timer = Timer(),
        dossiers = Dossiers(),
        hier = LocatieLog.Hier(),
        vanHuis = LocatieLog.VertrokkenVanHuis() is { } vh
            ? vh.LocalDateTime.ToString("HH:mm")
            : "",
        plekken = LocatieLog.Plekken(),
        bezoeken = LocatieLog.Vandaag().Select(b => new
        {
            plek = b.Plek,
            van = b.Aankomst.LocalDateTime.ToString("HH:mm"),
            tot = b.Vertrek?.LocalDateTime.ToString("HH:mm") ?? "",
            minuten = b.Minuten,
        }).ToList(),
        team = Team(),
        projecten = Projecten(),
        voorstel = Voorstel(),
    };

    /// <summary>
    /// De klantdossiers, zodat je vlak voor een bezoek nog kunt nalezen wat er speelt en wat
    /// er openstaat. De openstaande punten komen uit dezelfde parser als de maandagscan.
    /// </summary>
    private static List<object> Dossiers()
    {
        try
        {
            var map = KlantDossier.Map();
            if (!Directory.Exists(map))
            {
                return new List<object>();
            }
            return Directory.EnumerateFiles(map, "*.md")
                .Select(pad =>
                {
                    var tekst = File.ReadAllText(pad);
                    return (object)new
                    {
                        klant = Hoofdletter(Path.GetFileNameWithoutExtension(pad)),
                        punten = DossierPunten.PuntenUit(tekst).Select(p => Kort(p, 220)).ToList(),
                        // De volledige tekst mag mee: het zijn er drie van een paar duizend
                        // tekens, en onderweg wil je juist de details kunnen nalezen.
                        tekst = tekst.Length > 24_000
                            ? tekst[..24_000] + Environment.NewLine + Environment.NewLine + "(ingekort)"
                            : tekst,
                        telefoon = Telefoonnummers(tekst),
                        bijgewerkt = File.GetLastWriteTime(pad).ToString("d MMM"),
                    };
                })
                .ToList();
        }
        catch
        {
            return new List<object>();
        }
    }

    /// <summary>
    /// Telefoonnummers uit een dossier, voor een beltoets op de gsm. Bewust strikt: een
    /// dossier staat vol getallen (klantnummers, ondernemingsnummers, bedragen), en een
    /// verkeerd nummer waar je dan naartoe belt is erger dan geen nummer. Daarom alleen de
    /// herkenbare Belgische vormen — met landcode, of met echte scheidingstekens.
    /// </summary>
    private static List<string> Telefoonnummers(string tekst) =>
        System.Text.RegularExpressions.Regex.Matches(tekst,
                @"\+32[\s.]?\d(?:[\s.]?\d){7,8}" +          // +32 3 660 13 91 / +32 475 12 34 56
                @"|\b0\d[\s.]\d{3}[\s.]\d{2}[\s.]\d{2}\b" + // 03 660 13 91
                @"|\b04\d{2}[\s.]\d{2}[\s.]\d{2}[\s.]\d{2}\b") // 0475 12 34 56
            .Select(m => System.Text.RegularExpressions.Regex.Replace(m.Value, @"[\s.]", ""))
            .Distinct()
            .Take(4)
            .ToList();

    /// <summary>Openstaande teamtaken per lid.</summary>
    private static object Team()
    {
        var data = TeamTaskStore.Load();
        return new
        {
            leden = data.Leden,
            taken = data.Taken
                .Where(t => !t.Klaar)
                .OrderBy(t => t.Prioriteit)
                .Take(60)
                .Select(t => new
                {
                    id = t.Id.ToString(),
                    lid = t.Lid,
                    tekst = t.Tekst,
                    prioriteit = t.Prioriteit,
                    subtaken = t.Subtaken.Where(st => !st.Klaar).Select(st => Kort(st.Tekst, 90)).ToList(),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Wat je op afstand op de pc kunt starten. Bewust een korte, vaste lijst: dit zijn de
    /// dingen die je onderweg al wilt laten opstarten zodat ze klaarstaan als je thuiskomt.
    /// De sleutel gaat over de lijn, niet het pad — een webpagina hoort geen commando's te
    /// kunnen samenstellen.
    /// </summary>
    private const string Wsl = @"\wsl.localhost\Ubuntu\home\maarten\projecten\";

    private static readonly (string Sleutel, string Label, string Klant, Action Doe)[] Startbaar =
    {
        ("aqurat-claude", "Claude — aqurat", "Aqurat",
            () => ClientLauncher.StartClaude(Wsl + "aqurat")),
        ("aqurat-phpstorm", "PhpStorm — aqurat", "Aqurat",
            () => ClientLauncher.StartPhpStorm(Wsl + "aqurat")),
        ("bloom-claude", "Claude — bloom-datawarehouse", "RadiologyPartners",
            () => ClientLauncher.StartClaude(Wsl + "bloom-datawarehouse")),
        ("movaware-claude", "Claude — movaware-backend", "Vriesveemlogistics",
            () => ClientLauncher.StartClaude(Wsl + "movaware-backend")),
        ("cellaware-claude", "Claude — cellaware-backend", "Vriesveem",
            () => ClientLauncher.StartClaude(Wsl + "cellaware-backend")),
        ("laurapp-claude", "Claude — laurapp-backend", "Lauryssens",
            () => ClientLauncher.StartClaude(Wsl + "laurapp-backend")),
        ("glascalculator-claude", "Claude — glascalculator", "Lauryssens",
            () => ClientLauncher.StartClaude(@"G:\Mijn Drive\UrbanIT\Lauryssens\glascalculator")),
        ("wm-claude", "Claude — WorkManager zelf", "UrbanIT",
            () => ClientLauncher.StartClaude(@"C:\Data\Projecten\Workmanager")),
    };

    /// <summary>Wat je op afstand op de pc kunt starten (zelfde lijst als het Projecten-menu).</summary>
    private static List<object> Projecten() =>
        Startbaar.Select(p => (object)new { sleutel = p.Sleutel, label = p.Label, klant = p.Klant })
            .ToList();

    /// <summary>Een klaarstaand dagvoorstel (via "Uren voorstellen" op de webversie).</summary>
    private static object? Voorstel()
    {
        var regels = VoorstelStore.Laad();
        return regels.Count == 0 ? null : new
        {
            regels = regels.Select(r => new
            {
                id = r.Id.ToString(),
                klant = r.Klant,
                minuten = r.Minuten,
                tekst = r.Omschrijving,
                van = r.Van?.ToString("HH:mm") ?? "",
            }).ToList(),
        };
    }

    /// <summary>De dagplanning van vandaag: wat er nog te doen staat, in volgorde.</summary>
    private static List<object> Plan()
    {
        if (DagPlan.LaadVandaag() is not { } plan)
        {
            return new List<object>();
        }
        return plan.Items
            .Where(i => !i.Afgehandeld)
            .Take(15)
            .Select(i => (object)new
            {
                id = i.Id,
                taakId = i.TaakId?.ToString() ?? "",
                tekst = i.Tekst,
                minuten = i.Minuten,
                soort = i.Soort,
                waarom = Kort(i.Waarom, 90),
                vast = i.VastBlok,
                start = i.VastStart?.ToLocalTime().ToString("HH:mm") ?? "",
            })
            .ToList();
    }

    /// <summary>De lopende taaktimer, zodat je onderweg ziet dat hij nog loopt.</summary>
    private static object? Timer() =>
        TaakTimer.Huidig() is not { } lopend ? null : new
        {
            taakId = lopend.TaakId?.ToString() ?? "",
            tekst = lopend.Tekst,
            klant = lopend.Klant,
            minuten = lopend.Ruw,
            sinds = lopend.Start.ToLocalTime().ToString("HH:mm"),
        };

    private static List<object> Taken()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return MijnTaakStore.Load().Taken
            .Where(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart)
            .OrderBy(t => t.Deadline ?? DateOnly.MaxValue)
            .ThenBy(t => t.Prioriteit)
            .Take(60)
            .Select(t => (object)new
            {
                id = t.Id.ToString(),
                tekst = t.Tekst,
                categorie = t.Categorie,
                prioriteit = t.Prioriteit,
                deadline = t.Deadline?.ToString("yyyy-MM-dd") ?? "",
                deadlineTekst = DeadlineTekst(t.Deadline, vandaag),
                laat = t.Deadline is { } d && d < vandaag,
                vandaag = t.Deadline == vandaag,
            })
            .ToList();
    }

    /// <summary>
    /// Wat er nog aankomt: taken met een startdatum in de toekomst of die nog even snoozen.
    /// Zelfde idee als het anticipeervenster op de pc — je wilt onderweg kunnen zien wat er
    /// deze week nog opduikt, zonder dat het tussen het werk van vandaag staat.
    /// </summary>
    private static List<object> LaterTaken()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return MijnTaakStore.Load().Taken
            .Where(t => !t.Klaar && (t.NogNietGestart || t.Gesnoozed))
            .Select(t => new
            {
                Taak = t,
                Moment = t.NogNietGestart && t.Startdatum is { } s
                    ? s.ToDateTime(t.StartUur ?? TimeOnly.MinValue)
                    : t.SnoozeTot?.LocalDateTime ?? DateTime.MaxValue,
            })
            .OrderBy(x => x.Moment)
            .Take(25)
            .Select(x => (object)new
            {
                id = x.Taak.Id.ToString(),
                tekst = x.Taak.Tekst,
                categorie = x.Taak.Categorie,
                prioriteit = x.Taak.Prioriteit,
                deadlineTekst = x.Taak.Deadline is { } d ? DeadlineTekst(d, vandaag) : "",
                wanneer = AankomstTekst(x.Moment, x.Taak.NogNietGestart),
            })
            .ToList();
    }

    private static string AankomstTekst(DateTime moment, bool startdatum)
    {
        var dagen = DateOnly.FromDateTime(moment).DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        var wanneer = dagen switch
        {
            <= 0 => $"straks om {moment:HH:mm}",
            1 => "morgen",
            < 7 => moment.ToString("dddd"),
            _ => KorteDatum(moment),
        };
        return startdatum ? $"start {wanneer}" : $"terug {wanneer}";
    }

    private static string DeadlineTekst(DateOnly? deadline, DateOnly vandaag)
    {
        if (deadline is not { } d)
        {
            return "";
        }
        var dagen = d.DayNumber - vandaag.DayNumber;
        return dagen switch
        {
            < -1 => $"{-dagen} dagen te laat",
            -1 => "gisteren",
            0 => "vandaag",
            1 => "morgen",
            < 7 => d.ToString("dddd"),
            _ => KorteDatum(d.ToDateTime(TimeOnly.MinValue)),
        };
    }

    /// <summary>"12 sep" binnen dit jaar, met jaartal erbij zodra het een ander jaar is —
    /// anders lijkt een mail van vorig september alsof hij van volgende maand is.</summary>
    private static string KorteDatum(DateTime moment) =>
        moment.Year == DateTime.Now.Year ? moment.ToString("d MMM") : moment.ToString("d MMM yyyy");

    private static List<object> Agenda()
    {
        if (MeetingsCache.Load() is not { } cache)
        {
            return new List<object>();
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var nu = DateTimeOffset.Now;

        // Eigen agenda plus de CED-afspraken van vandaag en morgen; Hilkes agenda blijft
        // hier bewust buiten — dit is het werkbeeld.
        var items = cache.Eigen.Select(i => (Item: i, Bron: ""))
            .Concat(cache.Ced
                .Where(p => DateOnly.TryParse(p.Key, out var dag) &&
                            (dag == vandaag || dag == vandaag.AddDays(1)))
                .SelectMany(p => p.Value.Select(i => (Item: i, Bron: "CED"))))
            .Where(x => DateOnly.FromDateTime(x.Item.Start.LocalDateTime) is var dag &&
                        (dag == vandaag || dag == vandaag.AddDays(1)))
            .OrderBy(x => x.Item.Start)
            .Take(30);

        return items.Select(x => (object)new
        {
            dag = DateOnly.FromDateTime(x.Item.Start.LocalDateTime) == vandaag ? "Vandaag" : "Morgen",
            van = x.Item.HeleDag ? "hele" : x.Item.Start.ToLocalTime().ToString("HH:mm"),
            tot = x.Item.HeleDag ? "dag" : x.Item.Einde.ToLocalTime().ToString("HH:mm"),
            titel = x.Item.Titel,
            locatie = Kort(x.Item.Locatie, 40),
            bron = x.Bron,
            nu = !x.Item.HeleDag && x.Item.Start <= nu && x.Item.Einde > nu,
            // Voor de knop "Uren boeken" op de kaart: duur afgerond op een kwartier, en de
            // klant zoals de timesheets hem kennen (leeg = laat de keuze aan de pagina).
            minuten = x.Item.HeleDag ? 0
                : Math.Clamp((int)Math.Round(
                    (x.Item.Einde - x.Item.Start).TotalMinutes / 15) * 15, 15, 720),
            klant = KlantVoorAfspraak(x.Item.Titel, x.Bron),
            // Alleen wat vandaag al voorbij of bezig is: morgen boeken slaat nergens op.
            // Geplande avondmaaltijden (🍴) zijn geen werktijd — die nooit aanbieden.
            boekbaar = !x.Item.HeleDag && x.Item.Start <= nu &&
                !x.Item.Titel.StartsWith("🍴", StringComparison.Ordinal),
        }).ToList();
    }

    /// <summary>
    /// De timesheetklant die bij een afspraak past: eerst de bron (CED-agenda), anders een
    /// klantnaam in de titel. Niets gevonden = leeg, dan kiest de pagina zelf.
    /// </summary>
    private static string KlantVoorAfspraak(string titel, string bron)
    {
        if (bron == "CED")
        {
            return "CED";
        }
        return TimesheetStore.Klanten.FirstOrDefault(k =>
            titel.Contains(k.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static List<object> Berichten() =>
        CockpitCache.Load()
            .Where(b => !b.Genegeerd && b.MessageId.Length > 0)
            .OrderByDescending(b => b.Urgent)
            .ThenByDescending(b => b.Datum)
            .Take(25)
            .Select(b => (object)new
            {
                id = b.MessageId,
                van = b.Van,
                onderwerp = Kort(b.Onderwerp, 90),
                soort = Soort(b),
                urgent = b.Urgent,
                wanneer = Wanneer(b.Datum),
                // Een duim kan alleen op Google Chat; de CC-map heeft zijn eigen afhandeling
                // in de cockpit en blijft hier bewust buiten.
                duim = b.ChatSpace.Length > 0,
                archiveren = b.VanAdres != "CC-map",
                fragment = Kort(b.Tekst, 260),
                // Antwoorden kan op Gmail-mail (via SMTP) en op Google Chat; Outlook, Teams
                // en WhatsApp niet — die hebben de ingelogde sessie op de pc nodig.
                antwoorden = b.ChatSpace.Length > 0 ||
                    (!b.IsChat && b.AntwoordAan.Length > 0),
                // Snoozen is Gmail-only: dat gebeurt met het label "Gesnoozed".
                snoozen = !b.IsChat && b.Uid > 0,
                concept = b.ConceptKlaar && b.Concept.Length > 0 ? b.Concept : "",
                // Bijlagen komen niet op de webserver terecht: te veel klantdocumenten op een
                // plek waar ze niet horen. Wel één druk op de knop naar Drive, en die app
                // staat op de gsm.
                bijlagen = b.Bijlagen.Concat(b.LinkBijlagen.Select(l => l.Naam))
                    .Select(n => Kort(n, 40)).Take(6).ToList(),
            })
            .ToList();

    private static string Soort(MailBericht b) =>
        b.WhatsAppChat.Length > 0 ? "WhatsApp"
        : b.TeamsChat.Length > 0 ? "Teams"
        : b.ChatSpace.Length > 0 ? "Chat"
        : b.OutlookMail.Length > 0 || b.VanAdres is "CED Outlook" or "CC-map" ? "CED"
        : "Mail";

    private static string Wanneer(DateTimeOffset moment)
    {
        var verschil = DateTimeOffset.Now - moment;
        return verschil switch
        {
            { TotalMinutes: < 60 } => $"{Math.Max(1, (int)verschil.TotalMinutes)} min geleden",
            { TotalHours: < 24 } => $"{(int)verschil.TotalHours} u geleden",
            { TotalDays: < 7 } => $"{(int)verschil.TotalDays} d geleden",
            _ => KorteDatum(moment.LocalDateTime),
        };
    }

    private static object Uren()
    {
        var regels = TimesheetStore.Load();
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        // Maandag als weekstart (ISO), zoals overal in de app.
        var weekStart = vandaag.AddDays(-((int)vandaag.DayOfWeek + 6) % 7);

        var vanVandaag = regels.Where(r => r.Datum == vandaag).ToList();
        var openMinuten = regels.Where(r => !r.Doorgeboekt).Sum(r => r.Minuten);
        var vanWeek = regels.Where(r => r.Datum >= weekStart && r.Datum <= vandaag).ToList();
        var weekMinuten = vanWeek.Sum(r => r.Minuten);
        var perKlant = vanWeek.GroupBy(r => r.Klant)
            .Select(g => new
            {
                klant = g.Key.Length > 0 ? g.Key : "zonder klant",
                minuten = g.Sum(r => r.Minuten),
                doorgeboekt = g.All(r => r.Doorgeboekt),
            })
            .OrderByDescending(k => k.minuten)
            .ToList();

        return new
        {
            vandaagTekst = UurTekst(vanVandaag.Sum(r => r.Minuten)),
            regels = vanVandaag.Count,
            openTekst = openMinuten == 0
                ? "alles doorgeboekt"
                : $"{UurTekst(openMinuten)} nog door te boeken",
            weekTekst = UurTekst(weekMinuten),
            weekOmschrijving = $"sinds maandag {weekStart:d MMM}",
            perKlant = perKlant.Select(k => new
            {
                k.klant,
                tekst = UurTekst(k.minuten),
                k.doorgeboekt,
                deel = weekMinuten > 0 ? (int)Math.Round(100.0 * k.minuten / weekMinuten) : 0,
            }).ToList(),
        };
    }

    private static string UurTekst(int minuten) => minuten == 0
        ? "niets geboekt"
        : minuten < 60 ? $"{minuten} min" : $"{minuten / 60}u{(minuten % 60 == 0 ? "" : $"{minuten % 60:00}")}";

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }

    private static string Hoofdletter(string tekst) =>
        tekst.Length == 0 ? tekst : char.ToUpperInvariant(tekst[0]) + tekst[1..];

    // ---------- Locaties (omlaag) ----------

    /// <summary>
    /// Haalt de posities en aankomst/vertrek-meldingen op, verwerkt ze tot bezoeken en zet
    /// afgeronde bezoeken in het urenvoorstel. Een afgerond klantbezoek is een pushmelding
    /// waard: dan weet je dat de tijd geregistreerd is zonder dat je iets moest doen.
    /// </summary>
    private async Task VerwerkLocatiesAsync(WmWebSettings settings)
    {
        using var doc = await GetAsync(settings, "locwerk");
        if (doc is null || !doc.RootElement.TryGetProperty("punten", out var lijst) ||
            lijst.GetArrayLength() == 0)
        {
            return;
        }
        var ids = lijst.EnumerateArray()
            .Select(p => p.TryGetProperty("id", out var i)
                ? (i.ValueKind == JsonValueKind.Number ? i.GetInt32() : int.Parse(i.GetString()!))
                : 0)
            .Where(i => i > 0)
            .ToList();

        // Thuiskomst 's avonds: één rustige dagafsluiter met de blik op morgen.
        if (lijst.EnumerateArray().Any(r =>
                r.TryGetProperty("soort", out var soortEl) && soortEl.GetString() == "aankomst" &&
                r.TryGetProperty("plek", out var plekEl) &&
                LocatieLog.IsGeenWerk(plekEl.GetString() ?? "")))
        {
            await ThuiskomstMeldingAsync();
        }

        var afgesloten = LocatieLog.Verwerk(lijst.EnumerateArray());
        // Altijd afmelden, ook als er niets bruikbaars in zat: anders blijven ze terugkomen.
        await PostAsync(settings, "locklaar", new { ids });

        var nieuw = LocatieLog.ZetBezoekenInVoorstel();
        foreach (var bezoek in afgesloten)
        {
            var periode = $"({bezoek.Aankomst.LocalDateTime:HH:mm}–" +
                $"{bezoek.Vertrek!.Value.LocalDateTime:HH:mm})";
            string titel, melding;
            // Een lang werkbezoek is een afgeronde werkdag: op het moment dat je in de auto
            // stapt wil je weten of je uren compleet zijn.
            if (bezoek.Minuten >= 4 * 60 && !LocatieLog.IsGeenWerk(bezoek.Plek))
            {
                var open = VoorstelStore.Laad().Count;
                titel = "Werkdag afgerond";
                melding = $"{bezoek.Plek}: {UurTekst(bezoek.Minuten)} {periode}." +
                    (open > 0 ? $" {open} voorstelregel(s) staan klaar bij je uren." : "");
            }
            else
            {
                titel = "Bezoek geregistreerd";
                melding = $"{bezoek.Plek}: {UurTekst(bezoek.Minuten)} ter plaatse {periode}";
            }
            await PushMelding.StuurAsync(titel, melding, $"bezoek|{melding}");
            ActieVerwerkt?.Invoke(melding);
        }
        if (nieuw > 0)
        {
            ActieVerwerkt?.Invoke($"{nieuw} bezoek(en) staan als voorstel bij je uren.");
        }
    }

    /// <summary>
    /// De dagafsluiter bij thuiskomst ('s avonds, max. één per dag): wat morgen als eerste
    /// op je afkomt en of er nog uren klaarstaan — het spiegelbeeld van de ochtendbriefing.
    /// </summary>
    private async Task ThuiskomstMeldingAsync()
    {
        if (DateTime.Now.Hour < 16)
        {
            return; // overdag even thuis langsgaan is geen dagafsluiting
        }
        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorkManager", "thuiskomst-melding.txt");
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == vandaag)
            {
                return;
            }
            File.WriteAllText(marker, vandaag);
        }
        catch
        {
            // Marker niet schrijfbaar: dan hooguit een dubbele melding.
        }

        var morgen = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var delen = new List<string>();
        var taken = MijnTaakStore.Load().Taken
            .Count(t => !t.Klaar && t.Deadline is { } d && d <= morgen);
        delen.Add(taken > 0
            ? $"morgen {taken} {(taken == 1 ? "taak" : "taken")} met deadline"
            : "morgen geen deadline-taken");
        if (MeetingsCache.Load() is { } cache)
        {
            var eerste = cache.Eigen
                .Concat(cache.Ced
                    .Where(p => DateOnly.TryParse(p.Key, out var dag) && dag == morgen)
                    .SelectMany(p => p.Value))
                .Where(i => !i.HeleDag && !i.Titel.StartsWith("🍴", StringComparison.Ordinal) &&
                            DateOnly.FromDateTime(i.Start.LocalDateTime) == morgen)
                .OrderBy(i => i.Start)
                .FirstOrDefault();
            if (eerste is not null)
            {
                delen.Add($"eerste meeting {eerste.Start.ToLocalTime():HH:mm} " +
                    $"({Kort(eerste.Titel, 30)})");
            }
        }
        var voorstellen = VoorstelStore.Laad().Count;
        if (voorstellen > 0)
        {
            delen.Add($"{voorstellen} voorstelregel(s) staan nog bij je uren");
        }
        var melding = "Welkom thuis 👋  " + Hoofdletter(string.Join(" · ", delen)) + ".";
        await PushMelding.StuurAsync("Dag afgerond", melding, $"thuis|{vandaag}");
        ActieVerwerkt?.Invoke(melding);
    }

    // ---------- Acties (omlaag) ----------

    private async Task VerwerkActiesAsync(WmWebSettings settings)
    {
        using var doc = await GetAsync(settings, "wmwerk");
        if (doc is null || !doc.RootElement.TryGetProperty("acties", out var lijst))
        {
            return;
        }
        foreach (var actie in lijst.EnumerateArray())
        {
            var id = actie.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (id.Length == 0 || !actie.TryGetProperty("inhoud", out var inhoud))
            {
                continue;
            }
            string melding;
            try
            {
                melding = await VoerUitAsync(inhoud);
            }
            catch (Exception ex)
            {
                melding = $"Niet gelukt: {ex.Message}";
            }
            // Altijd afmelden: anders blijft dezelfde actie elke ronde terugkomen.
            await PostAsync(settings, "wmklaar", new { id, melding });
            ActieVerwerkt?.Invoke(melding);
        }
    }

    /// <summary>Voert één actie van de webpagina uit en geeft de melding voor op de gsm.</summary>
    private static async Task<string> VoerUitAsync(JsonElement inhoud)
    {
        var soort = inhoud.TryGetProperty("soort", out var s) ? s.GetString() ?? "" : "";
        var data = MijnTaakStore.Load();

        switch (soort)
        {
            case "taak_nieuw":
            {
                var tekst = (inhoud.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "").Trim();
                return tekst.Length == 0 ? "Lege taak — niets toegevoegd." : await NieuweTaakAsync(tekst, data);
            }

            case "bericht_archiveer":
            case "bericht_duim":
                return await BerichtActieAsync(
                    inhoud.TryGetProperty("id", out var bid) ? bid.GetString() ?? "" : "",
                    duim: soort == "bericht_duim");

            case "bericht_antwoord":
                return await AntwoordAsync(
                    inhoud.TryGetProperty("id", out var aid) ? aid.GetString() ?? "" : "",
                    (inhoud.TryGetProperty("tekst", out var at) ? at.GetString() ?? "" : "").Trim());

            case "bericht_snooze":
                return await SnoozeBerichtAsync(
                    inhoud.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "",
                    inhoud.TryGetProperty("wanneer", out var sw) ? sw.GetString() ?? "" : "");

            case "uren_boek":
                return BoekUren(inhoud);

            case "bericht_drive":
                return await NaarDriveAsync(
                    inhoud.TryGetProperty("id", out var did) ? did.GetString() ?? "" : "");

            case "timer_start":
            case "timer_stop":
                return TimerActie(data, inhoud, starten: soort == "timer_start");

            case "uren_doorboek":
            {
                var aantal = await TimesheetStore.BoekDoorAsync(CancellationToken.None);
                return aantal == 0
                    ? "Niets om door te boeken (of de urbanadmin-koppeling ontbreekt)."
                    : $"{aantal} regel(s) doorgeboekt naar urbanadmin.";
            }

            case "uren_voorstel":
                return await VoorstelMakenAsync();

            case "uren_voorstel_boek":
                return VoorstelBoeken(inhoud);

            case "uren_voorstel_weg":
                VoorstelStore.Wis();
                return "Voorstel weggegooid.";

            case "plek_bewaren":
                return LocatieLog.BewaarPlek(
                    inhoud.TryGetProperty("naam", out var pn) ? pn.GetString() ?? "" : "");

            case "teamtaak_nieuw":
                return TeamTaakNieuw(inhoud);

            case "teamtaak_klaar":
                return TeamTaakKlaar(inhoud);

            case "start_project":
            {
                var sleutel = inhoud.TryGetProperty("sleutel", out var sl) ? sl.GetString() ?? "" : "";
                if (Startbaar.FirstOrDefault(p => p.Sleutel == sleutel) is not { Label.Length: > 0 } gekozen)
                {
                    return "Onbekend project.";
                }
                gekozen.Doe();
                return $"Gestart op de pc: {gekozen.Label}";
            }

            case "taak_klaar":
            case "taak_snooze":
            {
                if (Zoek(data, inhoud) is not { } taak)
                {
                    return "Die taak staat er niet meer.";
                }
                if (soort == "taak_klaar")
                {
                    taak.Klaar = true;
                    taak.KlaarOp = DateTimeOffset.Now;
                    MijnTaakStore.Save(data);
                    return $"Afgevinkt: {Kort(taak.Tekst, 45)}";
                }
                var uren = inhoud.TryGetProperty("uren", out var u) && u.TryGetInt32(out var uu)
                    ? Math.Clamp(uu, 1, 168)
                    : 3;
                taak.SnoozeTot = DateTimeOffset.Now.AddHours(uren);
                taak.UitstelTeller++;
                MijnTaakStore.Save(data);
                return uren >= 24
                    ? $"Verzet naar morgen: {Kort(taak.Tekst, 40)}"
                    : $"Verzet met {uren} u: {Kort(taak.Tekst, 40)}";
            }

            default:
                return $"Onbekende actie ({soort}).";
        }
    }

    /// <summary>
    /// Een taak bijmaken vanaf de gsm. De zin gaat eerst langs Claude, zodat "Nicolas bellen
    /// over de plaatsingsprijzen, moet maandag af" meteen de juiste categorie en deadline
    /// krijgt. Lukt dat niet (geen CLI, geen internet), dan komt de zin er letterlijk in —
    /// een taak kwijtraken is erger dan een taak zonder deadline.
    /// </summary>
    private static async Task<string> NieuweTaakAsync(string tekst, MijnTakenData data)
    {
        try
        {
            using var afbreken = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var voorstellen = await ClaudeTaken.GenereerAsync(tekst, data.Categorieen, afbreken.Token);
            if (voorstellen.Count > 0)
            {
                foreach (var v in voorstellen)
                {
                    data.Taken.Add(new MijnTaak
                    {
                        Tekst = v.Tekst,
                        Categorie = v.Categorie,
                        Prioriteit = v.Prioriteit,
                        Deadline = v.Deadline,
                    });
                }
                MijnTaakStore.Save(data);
                var eerste = voorstellen[0];
                var extra = eerste.Deadline is { } d ? $", deadline {d:d MMM}" : "";
                return voorstellen.Count == 1
                    ? $"\"{Kort(eerste.Tekst, 45)}\" ({eerste.Categorie}{extra})"
                    : $"{voorstellen.Count} taken toegevoegd, eerste: {Kort(eerste.Tekst, 40)}";
            }
        }
        catch
        {
            // Claude niet bereikbaar of onbruikbaar antwoord: gewoon de ruwe zin opslaan.
        }

        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = data.Categorieen.FirstOrDefault() ?? "",
        });
        MijnTaakStore.Save(data);
        return $"\"{Kort(tekst, 50)}\" staat in Mijn taken.";
    }

    /// <summary>
    /// Een bericht afhandelen vanaf de gsm: archiveren, of een duim op een Google Chat.
    /// Het bericht komt uit de cockpitcache (daar staat de Uid die IMAP nodig heeft); voor
    /// Outlook, Teams en WhatsApp gaat het via de duurzame actiewachtrij, want die sessies
    /// draaien in verborgen vensters die nu misschien net herstarten.
    /// </summary>
    private static async Task<string> BerichtActieAsync(string messageId, bool duim)
    {
        var berichten = CockpitCache.Load();
        if (berichten.FirstOrDefault(b => b.MessageId == messageId) is not { } bericht)
        {
            return "Dat bericht staat niet meer in de lijst.";
        }

        var melding = "Uit de lijst gehaald";
        if (duim)
        {
            if (bericht.ChatSpace.Length == 0 ||
                !bericht.MessageId.StartsWith("chat:", StringComparison.Ordinal))
            {
                return "Een duim kan alleen op een Google Chat.";
            }
            await GoogleChatClient.ReageerAsync(
                GoogleChatSettings.Load(), bericht.MessageId[5..], CancellationToken.None);
            melding = $"👍 gestuurd naar {bericht.Van}";
        }
        else if (!bericht.IsChat)
        {
            await GmailClient.ArchiveerAsync(
                MailReplySettings.Load(), new[] { bericht }, CancellationToken.None);
            melding = $"Gearchiveerd: {Kort(bericht.Onderwerp, 40)}";
        }
        else if (bericht.OutlookMail.Length > 0)
        {
            ActieWachtrij.Voeg(new ActieWachtrij.Actie
            {
                Soort = "outlook-archief", Van = bericht.Van,
                Onderwerp = bericht.Onderwerp, Url = bericht.OutlookUrl,
            });
            melding = "Archiveren in Outlook staat klaar";
        }
        else if (bericht.TeamsChat.Length > 0 || bericht.WhatsAppChat.Length > 0)
        {
            var teams = bericht.TeamsChat.Length > 0;
            ActieWachtrij.Voeg(new ActieWachtrij.Actie
            {
                Soort = teams ? "teams-gelezen" : "wa-gelezen",
                Chat = teams ? bericht.TeamsChat : bericht.WhatsAppChat,
            });
            melding = $"Wordt in {(teams ? "Teams" : "WhatsApp")} als gelezen gezet";
        }

        MarkeerAfgehandeld(berichten, bericht);
        return melding;
    }

    /// <summary>
    /// Het antwoord versturen dat je op de gsm hebt nagelezen (meestal het concept dat hier
    /// al klaarstond, eventueel bijgewerkt). Mail gaat via SMTP als reply in de thread en
    /// wordt daarna gearchiveerd, net als op de pc; een chat gaat naar de space.
    /// </summary>
    private static async Task<string> AntwoordAsync(string messageId, string tekst)
    {
        if (tekst.Length < 2)
        {
            return "Leeg antwoord — niets verstuurd.";
        }
        var berichten = CockpitCache.Load();
        if (berichten.FirstOrDefault(b => b.MessageId == messageId) is not { } bericht)
        {
            return "Dat bericht staat niet meer in de lijst.";
        }

        if (bericht.ChatSpace.Length > 0)
        {
            await GoogleChatClient.VerstuurAsync(
                GoogleChatSettings.Load(), bericht.ChatSpace, tekst, CancellationToken.None);
        }
        else if (!bericht.IsChat && bericht.AntwoordAan.Length > 0)
        {
            var settings = MailReplySettings.Load();
            // VerstuurAsync neemt de tekst uit Concept; die vullen we met wat er op de gsm
            // stond. Zonder cc: reply-all vanaf de telefoon is te makkelijk misgeklikt.
            bericht.Concept = tekst;
            bericht.AlleBeantwoorden = false;
            var verstuurd = await GmailClient.VerstuurAsync(
                settings, new[] { bericht }, _ => { }, CancellationToken.None);
            if (verstuurd.Count == 0)
            {
                return "Versturen mislukt — het bericht staat nog in de lijst.";
            }
            await GmailClient.ArchiveerAsync(settings, verstuurd, CancellationToken.None);
        }
        else
        {
            return "Antwoorden kan hier alleen voor Gmail en Google Chat.";
        }

        MarkeerAfgehandeld(berichten, bericht);
        return $"Antwoord verstuurd aan {bericht.Van}.";
    }

    /// <summary>
    /// Een Gmail-mail wegleggen tot later: label "Gesnoozed" en uit de inbox. De tray-app
    /// zet hem op het gekozen moment vanzelf terug (zelfde wachtlijst als de cockpit).
    /// </summary>
    private static async Task<string> SnoozeBerichtAsync(string messageId, string wanneer)
    {
        var berichten = CockpitCache.Load();
        if (berichten.FirstOrDefault(b => b.MessageId == messageId) is not { } bericht)
        {
            return "Dat bericht staat niet meer in de lijst.";
        }
        if (bericht.IsChat || bericht.Uid == 0)
        {
            return "Snoozen kan hier alleen voor Gmail-mail.";
        }

        var nu = DateTimeOffset.Now;
        var tot = wanneer switch
        {
            "vanavond" => Op(nu, nu.Hour < 18 ? 0 : 1, 18),
            "maandag" => Op(nu, ((int)DayOfWeek.Monday - (int)nu.DayOfWeek + 7) % 7 is var d && d == 0
                ? 7 : d, 8),
            _ => Op(nu, 1, 8), // "morgen"
        };

        await GmailClient.SnoozeArchiveerAsync(
            MailReplySettings.Load(), new[] { bericht }, CancellationToken.None);
        var snoozes = SnoozeStore.LoadSnoozes();
        snoozes.Add(new SnoozeStore.SnoozeItem
        {
            MessageId = bericht.MessageId, Van = bericht.Van,
            Onderwerp = bericht.Onderwerp, Tot = tot,
        });
        SnoozeStore.SaveSnoozes(snoozes);
        MarkeerAfgehandeld(berichten, bericht);
        return $"Terug in de inbox op {tot:ddd d MMM 'om' HH:mm}.";
    }

    private static DateTimeOffset Op(DateTimeOffset vanaf, int dagenLater, int uur) =>
        new DateTimeOffset(vanaf.Date.AddDays(dagenLater).AddHours(uur), vanaf.Offset);

    /// <summary>
    /// Onthoudt dat een bericht afgehandeld is (zodat de volgende ophaalbeurt het niet
    /// terugzet) en haalt het meteen uit de cache, zodat de webpagina het ook niet meer toont.
    /// </summary>
    private static void MarkeerAfgehandeld(List<MailBericht> berichten, MailBericht bericht)
    {
        var cache = ConceptCache.Load();
        if (!cache.TryGetValue(bericht.MessageId, out var entry))
        {
            cache[bericht.MessageId] = entry = new ConceptCache.Entry { Datum = bericht.Datum };
        }
        entry.Genegeerd = true;
        ConceptCache.Save(cache);
        berichten.RemoveAll(b => b.MessageId == bericht.MessageId);
        CockpitCache.Save(berichten);
    }

    /// <summary>De bijlagen van een mail naar Drive zetten, zodat je ze op de gsm kunt openen.</summary>
    private static async Task<string> NaarDriveAsync(string messageId)
    {
        if (CockpitCache.Load().FirstOrDefault(b => b.MessageId == messageId) is not { } bericht)
        {
            return "Dat bericht staat niet meer in de lijst.";
        }
        if (!BijlagenNaarDrive.HeeftBijlagen(bericht))
        {
            return "Deze mail heeft geen bijlagen.";
        }
        return await BijlagenNaarDrive.StilNaarDriveAsync(
            MailReplySettings.Load(), bericht, "", CancellationToken.None);
    }

    /// <summary>
    /// De taaktimer op afstand starten of stoppen. Stoppen boekt de tijd meteen als
    /// timesheetregel, net als op de pc — anders klopt je dag niet als je bij de klant zit.
    /// </summary>
    private static string TimerActie(MijnTakenData data, JsonElement inhoud, bool starten)
    {
        if (!starten)
        {
            if (TaakTimer.Stop() is not { } gestopt)
            {
                return "Er liep geen timer.";
            }
            // Op Minuten kan niet getoetst worden: die rondt naar boven af met een minimum van
            // 5, dus een timer van tien seconden zou een kwartier lijken. Ruw is de echte tijd.
            if (gestopt.Ruw >= 3)
            {
                TimesheetStore.Voeg(new TimesheetRegel
                {
                    Datum = DateOnly.FromDateTime(gestopt.Start.LocalDateTime),
                    Van = TimeOnly.FromDateTime(gestopt.Start.LocalDateTime),
                    Klant = TimesheetStore.Klanten.Contains(gestopt.Klant)
                        ? gestopt.Klant
                        : "Niet factureerbaar",
                    Minuten = gestopt.Minuten,
                    Omschrijving = gestopt.Tekst,
                    Bron = "timer",
                });
                return $"Timer gestopt: {UurTekst(gestopt.Minuten)} geboekt op {gestopt.Tekst}.";
            }
            return "Timer gestopt (te kort om te boeken).";
        }

        if (Zoek(data, inhoud) is not { } taak)
        {
            return "Die taak staat er niet meer.";
        }
        TaakTimer.Start(taak.Id, "", taak.Tekst, KlantVoorCategorie(taak.Categorie));
        return $"Timer loopt op: {Kort(taak.Tekst, 45)}";
    }

    /// <summary>De timesheetklant die bij een taakcategorie hoort (leeg als er geen match is).</summary>
    private static string KlantVoorCategorie(string categorie) =>
        TimesheetStore.Klanten.FirstOrDefault(k =>
            k.StartsWith(categorie, StringComparison.OrdinalIgnoreCase) ||
            categorie.StartsWith(k.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ?? "";

    /// <summary>
    /// Laat Claude van de activiteitenlog een dagvoorstel maken en zet dat klaar op de
    /// webversie. Het wordt níét meteen geboekt: je keurt het op de gsm regel voor regel goed.
    /// </summary>
    private static async Task<string> VoorstelMakenAsync()
    {
        var dag = DateOnly.FromDateTime(DateTime.Now);
        List<AgendaClient.AgendaItem> meetings;
        try
        {
            meetings = MeetingsCache.Load() is { } cache
                ? cache.Eigen.Where(m => DateOnly.FromDateTime(m.Start.LocalDateTime) == dag).ToList()
                : new List<AgendaClient.AgendaItem>();
        }
        catch
        {
            meetings = new List<AgendaClient.AgendaItem>();
        }
        using var afbreken = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var voorstel = await ActiviteitenLog.VoorstelAsync(dag, meetings, afbreken.Token);
        VoorstelStore.Bewaar(voorstel);
        if (voorstel.Count == 0)
        {
            return "Geen voorstel — te weinig activiteit vandaag om iets van te maken.";
        }
        var totaal = voorstel.Sum(r => r.Minuten);
        return $"{voorstel.Count} regel(s) klaar ({UurTekst(totaal)}) — nakijken op het urentabblad.";
    }

    /// <summary>De aangevinkte voorstelregels echt boeken; de rest blijft staan.</summary>
    private static string VoorstelBoeken(JsonElement inhoud)
    {
        var gekozen = inhoud.TryGetProperty("ids", out var lijst) && lijst.ValueKind == JsonValueKind.Array
            ? lijst.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet()
            : new HashSet<string>();
        var voorstel = VoorstelStore.Laad();
        var boeken = voorstel.Where(r => gekozen.Count == 0 || gekozen.Contains(r.Id.ToString())).ToList();
        if (boeken.Count == 0)
        {
            return "Niets aangevinkt.";
        }
        foreach (var regel in boeken)
        {
            TimesheetStore.Voeg(regel);
        }
        VoorstelStore.Bewaar(voorstel.Except(boeken).ToList());
        return $"{boeken.Count} regel(s) geboekt ({UurTekst(boeken.Sum(r => r.Minuten))}).";
    }

    private static string TeamTaakNieuw(JsonElement inhoud)
    {
        var lid = (inhoud.TryGetProperty("lid", out var l) ? l.GetString() ?? "" : "").Trim();
        var tekst = (inhoud.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "").Trim();
        if (tekst.Length == 0)
        {
            return "Lege teamtaak — niets toegevoegd.";
        }
        var data = TeamTaskStore.Load();
        if (!data.Leden.Contains(lid))
        {
            return $"Onbekend teamlid ({lid}).";
        }
        data.Taken.Add(new TeamTaak { Lid = lid, Tekst = tekst });
        TeamTaskStore.Save(data);
        return $"Teamtaak voor {lid}: {Kort(tekst, 45)}";
    }

    private static string TeamTaakKlaar(JsonElement inhoud)
    {
        var data = TeamTaskStore.Load();
        if (!inhoud.TryGetProperty("id", out var idEl) ||
            !Guid.TryParse(idEl.GetString(), out var id) ||
            data.Taken.FirstOrDefault(t => t.Id == id && !t.Klaar) is not { } taak)
        {
            return "Die teamtaak staat er niet meer.";
        }
        taak.Klaar = true;
        taak.KlaarOp = DateTimeOffset.Now;
        TeamTaskStore.Save(data);
        return $"Afgevinkt bij {taak.Lid}: {Kort(taak.Tekst, 40)}";
    }

    /// <summary>Een timesheetregel bijboeken vanaf de gsm (na een klantbezoek, in de auto).</summary>
    private static string BoekUren(JsonElement inhoud)
    {
        var klant = (inhoud.TryGetProperty("klant", out var k) ? k.GetString() ?? "" : "").Trim();
        var minuten = inhoud.TryGetProperty("minuten", out var m) && m.TryGetInt32(out var mm) ? mm : 0;
        var omschrijving = (inhoud.TryGetProperty("omschrijving", out var o)
            ? o.GetString() ?? "" : "").Trim();
        if (!TimesheetStore.Klanten.Contains(klant))
        {
            return $"Onbekende klant ({klant}).";
        }
        if (minuten is < 5 or > 720)
        {
            return "Aantal minuten moet tussen 5 en 720 liggen.";
        }
        if (omschrijving.Length == 0)
        {
            return "Zonder omschrijving kan de regel niet doorgeboekt worden.";
        }
        TimesheetStore.Voeg(new TimesheetRegel
        {
            Datum = DateOnly.FromDateTime(DateTime.Now),
            Klant = klant,
            Minuten = minuten,
            Omschrijving = omschrijving,
            Bron = "webversie",
        });
        return $"{UurTekst(minuten)} op {klant} geboekt.";
    }

    private static MijnTaak? Zoek(MijnTakenData data, JsonElement inhoud) =>
        inhoud.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var id)
            ? data.Taken.FirstOrDefault(t => t.Id == id && !t.Klaar)
            : null;

    // ---------- HTTP ----------

    private static async Task<JsonDocument?> GetAsync(WmWebSettings settings, string actie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Url}?actie={actie}");
        request.Headers.Add("X-Wm-Token", settings.Token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task PostAsync(WmWebSettings settings, string actie, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}?actie={actie}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Wm-Token", settings.Token);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
