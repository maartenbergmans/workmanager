using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Instellingen voor de voice-koppeling: URL van de api.php op de hosting en het gedeelde
/// token (DPAPI-versleuteld). Persistent in %APPDATA%\WorkManager\voice-settings.json.
/// </summary>
public class VoiceSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "voice-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Url { get; set; } = "";
    public string TokenVersleuteld { get; set; } = "";
    public int PollSeconden { get; set; } = 15;

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

    [JsonIgnore]
    public bool Compleet => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Token.Length > 0;

    public static VoiceSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(SettingsFile), JsonOpts);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: koppeling staat dan gewoon uit.
        }
        return new VoiceSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Verwerkt spraakcommando's uit de auto: pollt de wachtrij op de hosting, laat Claude
/// ('claude -p', zoals de mailassistent) het gesprek parsen, en zet het gesproken antwoord
/// terug zodat de telefoon het kan voorlezen. Kan taken aanmaken/afvinken/snoozen (eigen én
/// team), mails voorlezen en archiveren, en de agenda opvragen; mails en agenda worden
/// alleen opgehaald als het gesprek erom vraagt.
/// </summary>
public class VoiceSync
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly CancellationToken _ct;
    private bool _bezig;

    /// <summary>Wordt aangeroepen (op de achtergrondthread) met een melding per doorgevoerde sessie.</summary>
    public event Action<string>? TakenToegevoegd;

    public VoiceSync(CancellationToken ct)
    {
        _ct = ct;
    }

    /// <summary>Eén pollronde: openstaande sessies ophalen en verwerken. Stil bij fouten.</summary>
    public async Task PollAsync()
    {
        if (_bezig)
        {
            return;
        }
        var settings = VoiceSettings.Load();
        if (!settings.Compleet)
        {
            return;
        }

        _bezig = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Url}?actie=werk");
            request.Headers.Add("X-Wm-Token", settings.Token);
            using var response = await Http.SendAsync(request, _ct);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct));
            if (!doc.RootElement.TryGetProperty("sessies", out var sessies))
            {
                return;
            }
            foreach (var sessie in sessies.EnumerateArray())
            {
                await VerwerkSessieAsync(settings,
                    sessie.GetProperty("id").GetString() ?? "",
                    sessie.GetProperty("historie"));
            }
        }
        catch
        {
            // Netwerk-/serverfout: volgende pollronde opnieuw proberen.
        }
        finally
        {
            _bezig = false;
        }
    }

    private async Task VerwerkSessieAsync(VoiceSettings settings, string sessieId, JsonElement historie)
    {
        if (sessieId.Length == 0)
        {
            return;
        }

        var gesprek = new StringBuilder();
        foreach (var beurt in historie.EnumerateArray())
        {
            var rol = beurt.TryGetProperty("rol", out var r) ? r.GetString() ?? "gebruiker" : "gebruiker";
            var tekst = beurt.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "";
            gesprek.AppendLine($"[{rol}] {tekst}");
        }

        string antwoordTekst;
        var klaar = true;
        var meldingen = new List<string>();
        try
        {
            var context = await BouwContextAsync(gesprek.ToString());
            var uitvoer = await ClaudeDrafter.RunClaudeAsync(BouwPrompt(gesprek.ToString(), context), _ct);
            using var doc = ClaudeDrafter.ParseJson(uitvoer);
            var root = doc.RootElement;
            antwoordTekst = root.TryGetProperty("antwoord", out var a) ? a.GetString() ?? "" : "";
            klaar = root.TryGetProperty("klaar", out var k) && k.ValueKind == JsonValueKind.True;
            if (antwoordTekst.Length == 0)
            {
                antwoordTekst = klaar ? "In orde." : "Kan je dat herhalen?";
            }
            if (klaar && root.TryGetProperty("acties", out var acties) &&
                acties.ValueKind == JsonValueKind.Array)
            {
                meldingen = await VoerActiesDoorAsync(acties, context);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            antwoordTekst = "Er ging iets mis bij het verwerken; probeer het straks opnieuw.";
        }

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                sessie = sessieId,
                antwoord = antwoordTekst,
                klaar,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}?actie=resultaat")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Wm-Token", settings.Token);
            using var response = await Http.SendAsync(request, _ct);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Terugmelden mislukt: sessie blijft op wacht_pc staan en wordt opnieuw verwerkt.
            return;
        }

        if (meldingen.Count > 0)
        {
            TakenToegevoegd?.Invoke(string.Join(Environment.NewLine, meldingen));
        }
    }

    /// <summary>
    /// Alles wat Claude als context krijgt én wat nodig is om acties uit te voeren: de
    /// genummerde lijsten in de prompt en in de acties verwijzen naar dezelfde objecten.
    /// </summary>
    private sealed class VoiceContext
    {
        public MijnTakenData MijnData = new();
        public List<MijnTaak> MijnOpen = new();
        public TeamTasksData TeamData = new();
        public List<TeamTaak> TeamOpen = new();
        public List<MailBericht> Mails = new();
        public bool MailsOpgehaald;
        public string MailFout = "";
        public List<AgendaClient.AgendaItem> Agenda = new();
        public bool AgendaOpgehaald;
    }

    /// <summary>
    /// Laadt de taken en haalt — alleen als het gesprek erom vraagt — ook de inbox en de
    /// agenda op, zodat een gewone takenronde geen IMAP-/ICS-vertraging oploopt.
    /// </summary>
    private async Task<VoiceContext> BouwContextAsync(string gesprek)
    {
        var context = new VoiceContext
        {
            MijnData = MijnTaakStore.Load(),
            TeamData = TeamTaskStore.Load(),
        };
        context.MijnOpen = context.MijnData.Taken.Where(t => !t.Klaar).ToList();
        context.TeamOpen = context.TeamData.Taken.Where(t => !t.Klaar).ToList();

        if (Regex.IsMatch(gesprek, @"\b(mail|mails|mailtje|inbox|bericht|berichten)\b", RegexOptions.IgnoreCase))
        {
            var mailSettings = MailReplySettings.Load();
            if (mailSettings.AppWachtwoord.Length == 0)
            {
                context.MailFout = "mailkoppeling niet ingesteld";
            }
            else
            {
                try
                {
                    context.Mails = await GmailClient.FetchAsync(mailSettings, _ct);
                    context.MailsOpgehaald = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    context.MailFout = ex.Message;
                }
            }
        }

        if (Regex.IsMatch(gesprek, "agenda|afspra|vergader|kalender|meeting", RegexOptions.IgnoreCase))
        {
            var agendaSettings = AgendaSettings.Load();
            if (agendaSettings.Compleet)
            {
                try
                {
                    var vandaag = DateOnly.FromDateTime(DateTime.Now);
                    context.Agenda = await AgendaClient.OphalenAsync(
                        agendaSettings.Urls, vandaag, vandaag.AddDays(7), _ct);
                    context.AgendaOpgehaald = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Agenda niet bereikbaar: sectie blijft gewoon weg.
                }
            }
        }
        return context;
    }

    /// <summary>Voert de bevestigde acties door en geeft een meldingsregel per actie terug.</summary>
    private async Task<List<string>> VoerActiesDoorAsync(JsonElement acties, VoiceContext context)
    {
        var meldingen = new List<string>();
        var mijnGewijzigd = false;
        var teamGewijzigd = false;
        var teArchiveren = new List<MailBericht>();
        // De context is een snapshot van vóór de Claude-rondreis; bijhouden wát er wijzigt,
        // zodat we straks alleen die wijzigingen op de verse stand van schijf toepassen.
        var mijnNieuw = new List<MijnTaak>();
        var mijnAangepast = new List<MijnTaak>();
        var teamNieuw = new List<TeamTaak>();
        var teamAangepast = new List<TeamTaak>();

        foreach (var actie in acties.EnumerateArray())
        {
            string Tekst(string naam) =>
                actie.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "").Trim() : "";
            int Nummer() =>
                actie.TryGetProperty("nummer", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32() : 0;
            int Prioriteit() =>
                actie.TryGetProperty("prioriteit", out var v) && v.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(v.GetInt32(), 0, 2) : 1;

            switch (Tekst("type"))
            {
                case "mijn_toevoegen":
                {
                    var omschrijving = Tekst("tekst");
                    if (omschrijving.Length == 0)
                    {
                        break;
                    }
                    var categorie = Tekst("categorie");
                    categorie = context.MijnData.Categorieen.FirstOrDefault(c =>
                            string.Equals(c, categorie, StringComparison.OrdinalIgnoreCase))
                        ?? context.MijnData.Categorieen.FirstOrDefault() ?? categorie;
                    DateOnly? deadline = null;
                    if (DateOnly.TryParse(Tekst("deadline"), out var d))
                    {
                        deadline = d;
                    }
                    var nieuwe = new MijnTaak
                    {
                        Tekst = omschrijving, Categorie = categorie,
                        Prioriteit = Prioriteit(), Deadline = deadline,
                    };
                    context.MijnData.Taken.Add(nieuwe);
                    mijnNieuw.Add(nieuwe);
                    mijnGewijzigd = true;
                    meldingen.Add($"Taak ({categorie}): {omschrijving}");
                    break;
                }
                case "team_toevoegen":
                {
                    var omschrijving = Tekst("tekst");
                    if (omschrijving.Length == 0)
                    {
                        break;
                    }
                    var lid = Tekst("lid");
                    lid = context.TeamData.Leden.FirstOrDefault(l =>
                            string.Equals(l, lid, StringComparison.OrdinalIgnoreCase))
                        ?? context.TeamData.Leden.FirstOrDefault() ?? lid;
                    var nieuweTeam = new TeamTaak
                    {
                        Lid = lid, Tekst = omschrijving, Prioriteit = Prioriteit(),
                    };
                    context.TeamData.Taken.Add(nieuweTeam);
                    teamNieuw.Add(nieuweTeam);
                    teamGewijzigd = true;
                    meldingen.Add($"Teamtaak voor {lid}: {omschrijving}");
                    break;
                }
                case "mijn_afvinken":
                {
                    var taak = Kies(context.MijnOpen, Nummer());
                    if (taak is null)
                    {
                        break;
                    }
                    taak.Klaar = true;
                    taak.KlaarOp = DateTimeOffset.Now;
                    mijnAangepast.Add(taak);
                    mijnGewijzigd = true;
                    meldingen.Add($"Afgevinkt: {taak.Tekst}");
                    break;
                }
                case "team_afvinken":
                {
                    var taak = Kies(context.TeamOpen, Nummer());
                    if (taak is null)
                    {
                        break;
                    }
                    taak.Klaar = true;
                    taak.KlaarOp = DateTimeOffset.Now;
                    teamAangepast.Add(taak);
                    teamGewijzigd = true;
                    meldingen.Add($"Teamtaak afgevinkt ({taak.Lid}): {taak.Tekst}");
                    break;
                }
                case "mijn_snoozen":
                {
                    var taak = Kies(context.MijnOpen, Nummer());
                    if (taak is null || !DateOnly.TryParse(Tekst("tot"), out var tot))
                    {
                        break;
                    }
                    taak.SnoozeTot = new DateTimeOffset(tot.ToDateTime(TimeOnly.MinValue));
                    mijnAangepast.Add(taak);
                    mijnGewijzigd = true;
                    meldingen.Add($"Gesnoozed tot {tot:dd/MM}: {taak.Tekst}");
                    break;
                }
                case "mail_archiveren":
                {
                    var mail = Kies(context.Mails, Nummer());
                    if (mail is not null)
                    {
                        teArchiveren.Add(mail);
                    }
                    break;
                }
            }
        }

        // Niet de snapshot terugschrijven maar de wijzigingen op een verse stand toepassen:
        // tussen het laden van de context en hier zit een Claude-rondreis, en in die tijd
        // kan er op de pc van alles gebeurd zijn (bv. een weektaak afgevinkt) dat de oude
        // snapshot stil zou terugdraaien.
        if (mijnGewijzigd)
        {
            var vers = MijnTaakStore.Load();
            vers.Taken.AddRange(mijnNieuw);
            foreach (var wijziging in mijnAangepast)
            {
                if (vers.Taken.FirstOrDefault(t => t.Id == wijziging.Id) is { } doel)
                {
                    doel.Klaar = wijziging.Klaar;
                    doel.KlaarOp = wijziging.KlaarOp;
                    doel.SnoozeTot = wijziging.SnoozeTot;
                }
            }
            MijnTaakStore.Save(vers);
        }
        if (teamGewijzigd)
        {
            var vers = TeamTaskStore.Load();
            vers.Taken.AddRange(teamNieuw);
            foreach (var wijziging in teamAangepast)
            {
                if (vers.Taken.FirstOrDefault(t => t.Id == wijziging.Id) is { } doel)
                {
                    doel.Klaar = wijziging.Klaar;
                    doel.KlaarOp = wijziging.KlaarOp;
                }
            }
            TeamTaskStore.Save(vers);
        }
        if (teArchiveren.Count > 0)
        {
            try
            {
                await GmailClient.ArchiveerAsync(MailReplySettings.Load(), teArchiveren, _ct);
                meldingen.AddRange(teArchiveren.Select(m => $"Mail gearchiveerd: {m.Onderwerp}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                meldingen.Add("Mail archiveren mislukt.");
            }
        }
        return meldingen;
    }

    private static T? Kies<T>(List<T> lijst, int nummer) where T : class =>
        nummer >= 1 && nummer <= lijst.Count ? lijst[nummer - 1] : null;

    private static string PrioNaam(int prioriteit) =>
        prioriteit switch { 0 => "hoog", 2 => "laag", _ => "normaal" };

    /// <summary>Klapt witruimte samen en kapt af, voor mailfragmenten in de prompt.</summary>
    private static string Kort(string tekst, int max)
    {
        var plat = Regex.Replace(tekst, @"\s+", " ").Trim();
        return plat.Length <= max ? plat : plat[..max] + "…";
    }

    private static string BouwPrompt(string gesprek, VoiceContext context)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var categorieen = string.Join(", ", context.MijnData.Categorieen);
        var leden = string.Join(", ", context.TeamData.Leden);

        var mijnLijst = context.MijnOpen.Count == 0
            ? "(geen)"
            : string.Join("\n", context.MijnOpen.Select((t, i) =>
            {
                var info = new StringBuilder($"{t.Categorie}, {PrioNaam(t.Prioriteit)}");
                if (t.Deadline is { } deadline)
                {
                    info.Append($", deadline {deadline:yyyy-MM-dd}");
                }
                if (t.Gesnoozed)
                {
                    info.Append($", gesnoozed tot {t.SnoozeTot:yyyy-MM-dd}");
                }
                return $"{i + 1}. [{info}] {t.Tekst}";
            }));

        var teamLijst = context.TeamOpen.Count == 0
            ? "(geen)"
            : string.Join("\n", context.TeamOpen.Select((t, i) =>
                $"{i + 1}. [{t.Lid}, {PrioNaam(t.Prioriteit)}] {t.Tekst}"));

        string mailSectie;
        if (context.MailsOpgehaald)
        {
            mailSectie = context.Mails.Count == 0
                ? "Mails in de inbox: geen."
                : "Mails in de inbox (nieuwste eerst, genummerd voor mail_archiveren):\n" +
                  string.Join("\n", context.Mails.Select((m, i) =>
                      $"{i + 1}. Van {m.Van} — \"{m.Onderwerp}\" ({m.Datum.ToLocalTime():ddd dd/MM HH:mm}): {Kort(m.Tekst, 350)}"));
        }
        else
        {
            mailSectie = context.MailFout.Length > 0
                ? $"Mails konden niet opgehaald worden ({context.MailFout}); zeg dat kort als hij ernaar vraagt."
                : "";
        }

        var agendaSectie = !context.AgendaOpgehaald
            ? ""
            : context.Agenda.Count == 0
                ? "Agenda komende 7 dagen: leeg."
                : "Agenda komende 7 dagen:\n" + string.Join("\n", context.Agenda.Select(a =>
                    a.HeleDag
                        ? $"- {a.Start.ToLocalTime():ddd dd/MM} (hele dag) {a.Titel}"
                        : $"- {a.Start.ToLocalTime():ddd dd/MM HH:mm}–{a.Einde.ToLocalTime():HH:mm} {a.Titel}"));

        return
            $$"""
            Je bent de spraakassistent van Maartens WorkManager. Hieronder staat een gesprek dat
            in de auto gedicteerd wordt. Maarten kan taken aanmaken (voor zichzelf of zijn team),
            zijn taken laten voorlezen, taken afvinken of snoozen, mails laten voorlezen of
            archiveren, en zijn agenda vragen. Je antwoord wordt VOORGELEZEN — houd het kort,
            natuurlijk Nederlands, zonder opsommingstekens of technische termen.

            Context:
            - Vandaag is {{vandaag:yyyy-MM-dd}} ({{vandaag.DayOfWeek}}).
            - Categorieën voor eigen taken: {{categorieen}}.
            - Teamleden: {{leden}}.
            - prioriteit: 0 = hoog (alleen bij echte urgentie), 1 = normaal, 2 = laag.
            - deadline: alleen invullen als die genoemd of duidelijk geïmpliceerd is, anders null.

            Eigen open taken (genummerd voor mijn_afvinken/mijn_snoozen):
            {{mijnLijst}}

            Open teamtaken (genummerd voor team_afvinken):
            {{teamLijst}}

            {{mailSectie}}

            {{agendaSectie}}

            Werkwijze:
            1. Nieuwe taken: vat je voorstel kort samen en vraag om bevestiging (klaar=false,
               acties=[]). Bevestigt Maarten ("ja", "oké", "doe maar"): klaar=true met de
               acties en een korte bevestiging ("Staat genoteerd."). Meerdere taken in één
               commando mag.
            2. Voorlezen (taken, mails, agenda): geef meteen de gevraagde info, compact en
               vlot voorleesbaar — bij mails per mail de afzender en waar het over gaat,
               details alleen als hij doorvraagt. Sluit af met een korte vervolgvraag
               ("Wil je er iets mee doen?") en klaar=false; zegt hij "nee" of "dat was het",
               dan klaar=true zonder acties.
            3. Afvinken, snoozen of archiveren: gebruik het nummer uit de lijsten hierboven.
               Is de opdracht eenduidig, voer dan meteen uit (klaar=true met de acties); bij
               twijfel eerst kort bevestigen (klaar=false).
            4. Corrigeert hij ("nee, voor Kris", "zonder deadline"): pas aan en vraag opnieuw
               (klaar=false). Annuleert hij ("laat maar"): klaar=true, acties=[].
            5. BELANGRIJK: acties worden alléén uitgevoerd bij klaar=true, precies één keer,
               aan het einde van het gesprek. Zet dus nooit acties bij klaar=false, en neem
               bij klaar=true álle in het gesprek afgesproken acties op.
            6. Kan iets echt niet (mail beantwoorden, taken bewerken, iets buiten deze lijst):
               zeg dat in één zin.

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"antwoord": "korte gesproken reactie", "klaar": true of false, "acties": [
              {"type": "mijn_toevoegen", "tekst": "…", "categorie": "…", "prioriteit": 1, "deadline": "yyyy-MM-dd" of null},
              {"type": "team_toevoegen", "tekst": "…", "lid": "…", "prioriteit": 1},
              {"type": "mijn_afvinken", "nummer": 2},
              {"type": "team_afvinken", "nummer": 5},
              {"type": "mijn_snoozen", "nummer": 3, "tot": "yyyy-MM-dd"},
              {"type": "mail_archiveren", "nummer": 1}
            ]}
            (Dit zijn de zes mogelijke actietypes; "acties" bevat alleen wat echt moet gebeuren.)

            Gesprek tot nu toe:
            ---
            {{gesprek}}
            ---
            """;
    }
}
