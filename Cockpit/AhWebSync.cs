using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de AH-webbestelpagina op de hosting: URL van ah.php en het gedeelde
/// token (DPAPI-versleuteld). Persistent in %APPDATA%\WorkManager\ah-web-settings.json.
/// Zelfde patroon als <see cref="VoiceSettings"/>, maar met een eigen token zodat de
/// gsm-link niets anders kan dan boodschappen doorgeven.
/// </summary>
public class AhWebSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "ah-web-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Url { get; set; } = "";
    public string TokenVersleuteld { get; set; } = "";

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

    public static AhWebSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AhWebSettings>(File.ReadAllText(SettingsFile), JsonOpts);
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
        return new AhWebSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Brug tussen de AH-bestelpagina op workmanager.urbanit.be (voor op de gsm) en deze pc.
/// Twee richtingen: (1) periodiek een snapshot van de gerechten, suggesties en rubrieken —
/// met foto's, prijzen en producttabelmatches al opgelost — naar de hosting zetten, zodat de
/// pagina niets van ah.be zelf hoeft te weten; (2) de bestelwachtrij pollen en een
/// binnengekomen bestelling in het echte mandje leggen via <see cref="AhWinkelForm"/>
/// (die heeft de ingelogde AH-sessie, dus dat kan alleen hier).
/// </summary>
public class AhWebSync
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "ah-web-sync.json");
    private static readonly string GerechtenFile = Path.Combine(DataDir, "ah-gerechten.json");

    /// <summary>Hoe oud het snapshot op de server mag worden voor we een verse maken (prijzen!).</summary>
    private static readonly TimeSpan SnapshotHoudbaar = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class SyncState
    {
        public DateTimeOffset LaatsteSnapshot { get; set; } = DateTimeOffset.MinValue;
        public DateTime GerechtenBestandVan { get; set; } = DateTime.MinValue;
        public List<string> Verwerkt { get; set; } = new();
        public List<string> VerwerkteWensen { get; set; } = new();
    }

    private bool _pollBezig;
    private bool _snapshotBezig;

    /// <summary>Vuurt (op de UI-thread) met een melding zodra er een bestelling binnenkwam.</summary>
    public event Action<string>? BestellingOntvangen;

    /// <summary>Vuurt (op de UI-thread) met de gerechtnaam zodra een gsm-voorstel een gerecht werd.</summary>
    public event Action<string>? GerechtToegevoegd;

    /// <summary>
    /// Eén pollronde (vanaf een UI-timer, want een bestelling opent een venster met WebView2):
    /// zo nodig het snapshot verversen, en de bestelwachtrij leeghalen. Stil bij fouten.
    /// </summary>
    public async Task PollAsync()
    {
        if (_pollBezig)
        {
            return;
        }
        var settings = AhWebSettings.Load();
        if (!settings.Compleet)
        {
            return;
        }

        _pollBezig = true;
        try
        {
            VerversSnapshotAlsNodig(settings);
            await VerwerkBestellingenAsync(settings);
            await VerwerkGerechtWensenAsync(settings);
        }
        catch
        {
            // Netwerk-/serverfout: volgende pollronde opnieuw.
        }
        finally
        {
            _pollBezig = false;
        }
    }

    // ---------- Bestellingen (omlaag) ----------

    private async Task VerwerkBestellingenAsync(AhWebSettings settings)
    {
        using var doc = await GetAsync(settings, "ahwerk");
        if (doc is null || !doc.RootElement.TryGetProperty("bestellingen", out var lijst))
        {
            return;
        }

        var state = LaadState();
        foreach (var bestelling in lijst.EnumerateArray())
        {
            var id = bestelling.GetProperty("id").GetString() ?? "";
            if (id.Length == 0 || state.Verwerkt.Contains(id))
            {
                continue; // al gedaan (klaar-melding was toen blijkbaar niet aangekomen)
            }

            var producten = new List<AhIngredient>();
            var handmatig = new List<string>();
            var inhoud = bestelling.GetProperty("inhoud");
            if (inhoud.TryGetProperty("producten", out var pLijst))
            {
                foreach (var p in pLijst.EnumerateArray())
                {
                    var url = p.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var naam = p.TryGetProperty("naam", out var n) ? n.GetString() ?? "" : "";
                    var aantal = p.TryGetProperty("aantal", out var a) && a.TryGetInt32(out var aa) ? aa : 1;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        handmatig.Add(naam);
                    }
                    else
                    {
                        producten.Add(new AhIngredient
                        {
                            Naam = naam, Url = url, Aantal = Math.Max(1, aantal),
                        });
                    }
                }
            }
            if (inhoud.TryGetProperty("handmatig", out var hLijst))
            {
                foreach (var h in hLijst.EnumerateArray())
                {
                    if (h.GetString() is { Length: > 0 } naam)
                    {
                        handmatig.Add(naam);
                    }
                }
            }

            // Geplande gerechten meteen in Google Calendar zetten (CalDAV is snel en de
            // uitkomst hoort in de klaar-melding op de gsm thuis).
            var gepland = await PlanInAgendaAsync(inhoud);

            // Eerst afmelden (zodat een crash tijdens het vullen geen dubbele run geeft; het
            // vullen zelf is bovendien idempotent), dan het mandje vullen.
            var melding = $"{producten.Count} product(en) in het mandje gezet" +
                (handmatig.Count > 0 ? $", {handmatig.Count} zelf te zoeken" : "") +
                (gepland > 0 ? $", {gepland} gerecht(en) in de agenda" : "");
            await PostAsync(settings, "ahklaar", new { id, melding });
            state.Verwerkt.Add(id);
            if (state.Verwerkt.Count > 100)
            {
                state.Verwerkt.RemoveRange(0, state.Verwerkt.Count - 100);
            }
            BewaarState(state);

            AhHistoriek.Registreer(producten.Select(p => p.Naam).Concat(handmatig));
            var wie = inhoud.TryGetProperty("wie", out var w) ? w.GetString() : null;
            BestellingOntvangen?.Invoke(
                $"Van {(string.IsNullOrWhiteSpace(wie) ? "de gsm" : wie)}: {producten.Count} product(en)" +
                (handmatig.Count > 0 ? $" + {handmatig.Count} zelf zoeken" : "") +
                (gepland > 0 ? $", {gepland} in de agenda" : ""));
            if (producten.Count > 0)
            {
                // Niet-modaal: de tray-lus moet gewoon doordraaien; vullen start bij Shown.
                new AhWinkelForm(producten, handmatig).Show();
            }
        }
    }

    /// <summary>
    /// Zet de op de gsm geplande gerechten als 🍴-afspraken in Google Calendar (CalDAV,
    /// zelfde route als de agenda-stap op de pc): 18:00, of 12:00 met het middag-vinkje;
    /// duur = bereidingstijd of 45 min. Geeft het aantal gelukte afspraken terug.
    /// </summary>
    private static async Task<int> PlanInAgendaAsync(JsonElement inhoud)
    {
        if (!inhoud.TryGetProperty("agenda", out var lijst) ||
            lijst.ValueKind != JsonValueKind.Array || lijst.GetArrayLength() == 0 ||
            !CalendarClient.Beschikbaar)
        {
            return 0;
        }

        var personen = inhoud.TryGetProperty("personen", out var p) &&
            p.TryGetInt32(out var pp) ? pp : 4;
        var data = AhBestelForm.LaadGerechten();
        var gelukt = 0;
        foreach (var item in lijst.EnumerateArray())
        {
            var gerecht = item.TryGetProperty("gerecht", out var g) ? g.GetString() ?? "" : "";
            var datumTekst = item.TryGetProperty("datum", out var d) ? d.GetString() ?? "" : "";
            var middag = item.TryGetProperty("middag", out var m) && m.ValueKind == JsonValueKind.True;
            if (gerecht.Length == 0 || !DateOnly.TryParse(datumTekst, out var datum) ||
                datum < DateOnly.FromDateTime(DateTime.Now))
            {
                continue;
            }
            try
            {
                var recept = data.Recepten.GetValueOrDefault(gerecht);
                var start = datum.ToDateTime(new TimeOnly(middag ? 12 : 18, 0));
                // Zelfde afspraak als in het plannervenster: altijd een uur blokkeren.
                var duur = AhAgendaForm.ReceptDuur;
                if (await CalendarClient.MaakAfspraakAsync("🍴 " + gerecht, start, duur,
                        AgendaOmschrijving(gerecht, recept, personen, data), CancellationToken.None))
                {
                    gelukt++;
                }
            }
            catch
            {
                // Eén mislukte afspraak mag de rest niet tegenhouden.
            }
        }
        return gelukt;
    }

    /// <summary>Afspraaktekst: recept + bereidingstijd + porties + boodschappen van dat gerecht.</summary>
    private static string AgendaOmschrijving(
        string gerecht, Recept? recept, int personen, AhBestelForm.GerechtenData data)
    {
        var regels = new List<string>();
        if (recept?.Tekst is { Length: > 0 } tekst)
        {
            regels.Add(tekst);
        }
        if (recept?.Minuten > 0)
        {
            regels.Add($"Bereidingstijd: {recept.Minuten} min");
        }
        regels.Add($"Voor {personen} personen");
        var ingredienten = data.Gerechten.GetValueOrDefault(gerecht)
            ?? data.Suggesties.GetValueOrDefault(gerecht);
        if (ingredienten is { Count: > 0 })
        {
            regels.Add("Boodschappen: " + string.Join(", ", ingredienten.Select(i => i.Naam)));
        }
        regels.Add("Gepland via de gsm-bestelpagina.");
        return string.Join("\n\n", regels);
    }

    // ---------- Gerecht-voorstellen van de gsm ----------

    /// <summary>
    /// Vrije gerechtwensen van de gsm-pagina ("lasagne met spinazie en zalm"): Claude maakt
    /// er een volwaardig gerecht van — ingrediënten bij voorkeur uit de producttabel, plus
    /// recept — dat in ah-gerechten.json bij de gerechten komt. Maarten krijgt een
    /// nakijktaak in "Mijn taken"; de gsm ziet de uitkomst via de wensstatus.
    /// </summary>
    private async Task VerwerkGerechtWensenAsync(AhWebSettings settings)
    {
        using var doc = await GetAsync(settings, "wenswerk");
        if (doc is null || !doc.RootElement.TryGetProperty("wensen", out var lijst))
        {
            return;
        }
        foreach (var wens in lijst.EnumerateArray())
        {
            var id = wens.GetProperty("id").GetString() ?? "";
            var tekst = wens.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "";
            var state = LaadState();
            if (id.Length == 0 || tekst.Length == 0 || state.VerwerkteWensen.Contains(id))
            {
                continue;
            }

            string melding;
            try
            {
                melding = await MaakGerechtVanWensAsync(tekst);
            }
            catch (Exception ex)
            {
                melding = $"Niet gelukt ({ex.Message}) — probeer het straks anders te omschrijven.";
            }

            await PostAsync(settings, "wensklaar", new { id, melding });
            state = LaadState();
            state.VerwerkteWensen.Add(id);
            if (state.VerwerkteWensen.Count > 100)
            {
                state.VerwerkteWensen.RemoveRange(0, state.VerwerkteWensen.Count - 100);
            }
            BewaarState(state);
        }
    }

    /// <summary>Laat Claude van de wens een gerecht maken en schrijft het weg; geeft de melding voor de gsm.</summary>
    private async Task<string> MaakGerechtVanWensAsync(string wens)
    {
        var producten = AhProducten.Alles.Select(p => p.Naam).ToList();
        var prompt = $$"""
            Je bent een ervaren kok. Iemand van het gezin wenst dit gerecht (vrije beschrijving,
            mogelijk met spelfouten):
            "{{wens}}"

            Maak er één eenvoudig avondmaalgerecht van. Kies de ingrediënten bij voorkeur
            EXACT uit deze lijst (namen letterlijk overnemen); ontbreekt iets essentieels,
            zet het er dan als korte gewone naam bij (bv. "kokosmelk"):
            {{string.Join("\n", producten.Select(p => "- " + p))}}

            Maarten eet glutenvrij: kies waar relevant de glutenvrije aanpak of vermeld een
            glutenvrij alternatief in het recept.

            Antwoord uitsluitend met JSON, exact in dit formaat (geen extra tekst):
            {"naam": "<korte gerechtnaam>", "ingredienten": ["<naam>", …], "recept": "<3 tot 6 korte stappen, gescheiden door \n>", "minuten": <geheel getal>, "personen": <geheel getal>}
            """;

        var output = await ClaudeDrafter.RunClaudeAsync(prompt, CancellationToken.None);
        using var doc = ClaudeDrafter.ParseJson(output);
        var wortel = doc.RootElement;
        var naam = wortel.TryGetProperty("naam", out var n) ? n.GetString()?.Trim() ?? "" : "";
        var recept = wortel.TryGetProperty("recept", out var r) ? r.GetString() ?? "" : "";
        var minuten = wortel.TryGetProperty("minuten", out var m) && m.TryGetInt32(out var mv) ? mv : 30;
        var personen = wortel.TryGetProperty("personen", out var p) && p.TryGetInt32(out var pv) ? pv : 4;
        var ingredienten = wortel.TryGetProperty("ingredienten", out var lijst) &&
            lijst.ValueKind == JsonValueKind.Array
                ? lijst.EnumerateArray().Select(el => el.GetString()).OfType<string>()
                    .Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().Take(12).ToList()
                : new List<string>();
        if (naam.Length == 0 || recept.Length == 0 || ingredienten.Count < 3)
        {
            return "Kon er geen bruikbaar gerecht van maken — probeer het anders te omschrijven.";
        }

        if (!VoegGerechtToe(ref naam, ingredienten, recept, minuten, personen))
        {
            return "Wegschrijven mislukte — probeer het straks opnieuw.";
        }

        // Nakijktaak voor Maarten: het voorstel is van Claude, de mens keurt.
        var taken = MijnTaakStore.Load();
        taken.Taken.Add(new MijnTaak
        {
            Tekst = $"Nieuw AH-gerecht nakijken: {naam} (gsm-voorstel: \"{Kort(wens)}\")",
            Categorie = "AH",
            Deadline = DateOnly.FromDateTime(DateTime.Now).AddDays(2),
        });
        MijnTaakStore.Save(taken);

        GerechtToegevoegd?.Invoke(naam);
        return $"\"{naam}\" staat bij de gerechten (Maarten kijkt het na) — herlaad de pagina straks om het te zien.";
    }

    private static string Kort(string tekst) =>
        tekst.Length <= 60 ? tekst : tekst[..57] + "…";

    /// <summary>Schrijft het nieuwe gerecht + recept in ah-gerechten.json via JsonNode (zelfde
    /// patroon als AhReceptVanDeMaand, zodat de rest van het bestand onaangeroerd blijft).
    /// Bestaat de naam al, dan krijgt hij een "(gsm)"-achtervoegsel.</summary>
    private static bool VoegGerechtToe(
        ref string naam, List<string> ingredienten, string recept, int minuten, int personen)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(GerechtenFile)) is not { } wortel ||
                wortel["gerechten"] is not JsonObject gerechten)
            {
                return false;
            }
            if (gerechten.ContainsKey(naam) ||
                wortel["suggesties"] is JsonObject s && s.ContainsKey(naam))
            {
                naam += " (gsm)";
            }
            var lijst = new JsonArray();
            foreach (var ing in ingredienten)
            {
                lijst.Add(ing);
            }
            gerechten[naam] = lijst;
            if (wortel["recepten"] is not JsonObject recepten)
            {
                wortel["recepten"] = recepten = new JsonObject();
            }
            recepten[naam] = new JsonObject
            {
                ["tekst"] = recept.Replace("\\n", "\n").Trim(),
                ["minuten"] = Math.Clamp(minuten, 0, 480),
                ["personen"] = Math.Clamp(personen, 1, 20),
            };
            File.WriteAllText(GerechtenFile,
                wortel.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Snapshot (omhoog) ----------

    /// <summary>
    /// Start op de achtergrond een snapshot-upload als het huidige te oud is of de gerechten
    /// intussen bewerkt zijn. De bouw doet honderden AH-API-calls en mag de UI niet raken.
    /// </summary>
    private void VerversSnapshotAlsNodig(AhWebSettings settings)
    {
        if (_snapshotBezig)
        {
            return;
        }
        var state = LaadState();
        var bestandVan = BestandVan();
        if (DateTimeOffset.Now - state.LaatsteSnapshot < SnapshotHoudbaar &&
            bestandVan == state.GerechtenBestandVan)
        {
            return;
        }

        _snapshotBezig = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await BouwSnapshotAsync();
                await PostAsync(settings, "snapshot", new { snapshot });
                var vers = LaadState();
                vers.LaatsteSnapshot = DateTimeOffset.Now;
                vers.GerechtenBestandVan = bestandVan;
                BewaarState(vers);
            }
            catch
            {
                // Volgende pollronde opnieuw proberen.
            }
            finally
            {
                _snapshotBezig = false;
            }
        });
    }

    private static DateTime BestandVan()
    {
        try
        {
            return File.Exists(GerechtenFile) ? File.GetLastWriteTimeUtc(GerechtenFile) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Bouwt het complete pakket voor de webpagina: alle secties met per ingrediënt de
    /// productlink (eigen link of producttabelmatch), foto, prijs, bonus en glutenstatus,
    /// en per gerecht de receptfoto en portie-info. Alles voorgekauwd: de pagina hoeft
    /// alleen nog te tonen en op te tellen.
    /// </summary>
    private static async Task<object> BouwSnapshotAsync()
    {
        var data = AhBestelForm.LaadGerechten();
        var week = AhBestelForm.SuggestiesVanDeWeek(data.Suggesties);

        // Eerst per ingrediënt de productlink oplossen (eigen url wint van de tabelmatch).
        object MaakIngredient(AhIngredient ing, AhApi.ProductInfo? info, string? url, bool gok) => new
        {
            naam = ing.Naam,
            url,
            aantal = Math.Max(1, ing.Aantal),
            standaard = ing.Standaard,
            gok,
            titel = info?.Titel,
            beeld = info?.BeeldUrl,
            prijs = info?.Prijs,
            prijsVoorBonus = info?.PrijsVoorBonus,
            bonus = info?.Bonus == true,
            gluten = info?.Gluten == AhApi.GlutenStatus.Bevat,
            nutri = info?.Nutri,
        };

        var opgelost = new List<(AhIngredient Ing, string? Url, bool Gok)>();
        var perSectie = new Dictionary<string, List<(string Naam, List<AhIngredient> Ingredienten)>>
        {
            ["gerechten"] = data.Gerechten.Select(kv => (kv.Key, kv.Value)).ToList(),
            ["suggesties"] = week.Select(kv => (kv.Key, kv.Value)).ToList(),
            ["rubrieken"] = data.Rubrieken.Select(kv => (kv.Key, kv.Value)).ToList(),
        };
        foreach (var groepen in perSectie.Values)
        {
            foreach (var (_, ingredienten) in groepen)
            {
                foreach (var ing in ingredienten)
                {
                    if (!string.IsNullOrWhiteSpace(ing.Url))
                    {
                        opgelost.Add((ing, ing.Url, false));
                    }
                    else
                    {
                        var (product, zekerheid) = AhProducten.Zoek(ing.Naam);
                        opgelost.Add((ing, product?.Url, zekerheid == AhMatch.Gok));
                    }
                }
            }
        }

        // Details (titel, prijs, foto, gluten) per uniek product ophalen, met wat parallellisme.
        var ids = opgelost
            .Select(o => AhApi.WebshopId(o.Url))
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct()
            .ToList();
        var details = new Dictionary<string, AhApi.ProductInfo>();
        var poort = new SemaphoreSlim(6);
        await Task.WhenAll(ids.Select(async id =>
        {
            await poort.WaitAsync();
            try
            {
                if (await AhApi.DetailAsync(id) is { } info)
                {
                    lock (details)
                    {
                        details[id] = info;
                    }
                }
            }
            finally
            {
                poort.Release();
            }
        }));

        var perIngredient = opgelost.ToDictionary(
            o => o.Ing,
            o => MaakIngredient(o.Ing,
                AhApi.WebshopId(o.Url) is { } id ? details.GetValueOrDefault(id) : null,
                o.Url, o.Gok));

        async Task<List<object>> MaakGerechtenAsync(
            List<(string Naam, List<AhIngredient> Ingredienten)> groepen, bool metFoto)
        {
            var lijst = new List<object>();
            foreach (var (naam, ingredienten) in groepen)
            {
                var recept = data.Recepten.GetValueOrDefault(naam);
                lijst.Add(new
                {
                    naam,
                    foto = metFoto ? await GerechtFoto.UrlAsync(naam) : null,
                    minuten = recept?.Minuten ?? 0,
                    personen = Math.Max(1, recept?.Personen ?? 4),
                    recept = recept?.Tekst,
                    ingredienten = ingredienten.Select(i => perIngredient[i]).ToList(),
                });
            }
            return lijst;
        }

        return new
        {
            gemaakt = DateTimeOffset.Now,
            gerechten = await MaakGerechtenAsync(perSectie["gerechten"], metFoto: true),
            suggesties = await MaakGerechtenAsync(perSectie["suggesties"], metFoto: true),
            rubrieken = await MaakGerechtenAsync(perSectie["rubrieken"], metFoto: false),
            agendaBezet = await BezetteAvondenAsync(),
        };
    }

    /// <summary>
    /// De dagen (komende twee weken) waarop 's avonds (17–21 u) al iets in de agenda staat;
    /// de agenda-stap op de gsm markeert die als bezet. Zelfde regel als AhAgendaForm.
    /// </summary>
    private static async Task<List<string>> BezetteAvondenAsync()
    {
        if (!CalendarClient.Beschikbaar)
        {
            return new List<string>();
        }
        try
        {
            var morgen = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
            var afspraken = await CalendarClient.ZoekInPeriodeAsync(
                morgen, morgen.AddDays(13), CancellationToken.None);
            return afspraken
                .Where(a => a.Start.Hour is >= 17 and < 21)
                .Select(a => a.Start.ToString("yyyy-MM-dd"))
                .Distinct()
                .OrderBy(d => d)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---------- HTTP + state ----------

    private static async Task<JsonDocument?> GetAsync(AhWebSettings settings, string actie)
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

    private static async Task PostAsync(AhWebSettings settings, string actie, object body)
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

    private static SyncState LaadState()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StateFile), JsonOpts) is { } state)
            {
                return state;
            }
        }
        catch
        {
            // Kapotte state: gewoon opnieuw beginnen (ergste geval: één extra snapshot).
        }
        return new SyncState();
    }

    private static void BewaarState(SyncState state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }
}
