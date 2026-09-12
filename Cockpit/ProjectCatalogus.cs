using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>Eén urbanadmin-project waarop een timesheetregel geboekt kan worden.</summary>
public sealed class TimesheetProject
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("klant_id")] public int? KlantId { get; set; }
    [JsonPropertyName("klant")] public string? Klant { get; set; } // null = intern project
    [JsonPropertyName("naam")] public string Naam { get; set; } = "";
    [JsonPropertyName("actief")] public bool Actief { get; set; } = true;
    [JsonPropertyName("aantal")] public int Aantal { get; set; }   // boekingen in de periode
    [JsonPropertyName("minuten")] public int Minuten { get; set; } // geboekte minuten in de periode
    [JsonPropertyName("laatst")] public string? Laatst { get; set; }
    [JsonPropertyName("omschrijvingen")] public List<string> Omschrijvingen { get; set; } = new();

    /// <summary>Recente WorkManager-boekingen die nog niet in de serverstand zitten.</summary>
    [JsonIgnore] public int LokaleMinuten { get; set; }

    [JsonIgnore] public int Score => Minuten + LokaleMinuten;

    /// <summary>Klantnaam zonder rechtsvorm ("Vriesveem B.V." → "Vriesveem").</summary>
    [JsonIgnore]
    public string KlantKort
    {
        get
        {
            if (Klant is { Length: > 0 } klant)
            {
                return ProjectCatalogus.ZonderRechtsvorm(klant);
            }
            // Interne projecten heten "UrbanIT: administratie", "BerMaCon: administratie", …
            var dubbelepunt = Naam.IndexOf(':');
            return dubbelepunt > 0 ? Naam[..dubbelepunt].Trim() : "Intern";
        }
    }

    /// <summary>Het project zonder klantprefix ("UrbanIT: vergadering" → "vergadering").</summary>
    [JsonIgnore]
    public string NaamKort
    {
        get
        {
            var dubbelepunt = Naam.IndexOf(':');
            return Klant is null && dubbelepunt > 0 ? Naam[(dubbelepunt + 1)..].Trim() : Naam.Trim();
        }
    }

    /// <summary>Wat in de keuzelijsten en in <see cref="TimesheetRegel.Klant"/> staat.</summary>
    [JsonIgnore] public string Label => $"{KlantKort} · {NaamKort}";
}

/// <summary>
/// De projectenlijst van urbanadmin (endpoint /workmanager/projecten/{token}) met per project
/// het gebruik van de laatste 60 dagen en de recentste omschrijvingen. Voedt de keuzelijsten
/// van de timesheetvensters en de Claude-run achter het dagvoorstel, zodat die het juiste
/// project kiest (Vriesveem · Doorontwikkeling Cellaware in plaats van "UrbanIT") in plaats
/// van één vast project per klant.
/// <para>Cache: %APPDATA%\WorkManager\timesheet-projecten.json (hoogstens 6 uur oud; zonder
/// verbinding blijft de laatste stand gelden, en zonder cache een ingebouwde lijst).</para>
/// <para>De vroegere vaste klantnamen ("CED", "Aqurat", "Lauryssens laurapp", …) blijven als
/// alias werken: oude wachtrijregels, taakcategorieën en de webversie gebruiken ze nog.</para>
/// </summary>
public static class ProjectCatalogus
{
    private const int Dagen = 60;
    private static readonly TimeSpan MaxLeeftijd = TimeSpan.FromHours(6);

    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "timesheet-projecten.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim VernieuwSlot = new(1, 1);

    /// <summary>
    /// De vroegere vaste klantnamen → project-id. "Lauryssens laurapp" wees vroeger naar 9
    /// (dat is "Nemijtek — dringende interventie"); LaurApp is 96.
    /// </summary>
    private static readonly Dictionary<string, int> Aliassen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CED"] = 1,
        ["Aqurat"] = 114,
        ["RadiologyPartners"] = 99,
        ["Lauryssens advies"] = 2,
        ["Lauryssens laurapp"] = 96,
        ["UrbanIT"] = 31,
        ["Urban IT"] = 31,
        ["Niet factureerbaar"] = 30,
    };

    /// <summary>Het project voor privézaken en ander niet-factureerbaar werk.</summary>
    public const int NietFactureerbaarId = 30;

    /// <summary>
    /// Ingebouwde reservelijst (stand urbanadmin juli 2026) voor als er nog nooit een
    /// catalogus opgehaald is — bv. zolang het endpoint niet op productie staat.
    /// </summary>
    private static readonly (int Id, string? Klant, string Naam)[] Reserve =
    {
        (1, "CED Belgium NV", "consultancy (dagbasis)"),
        (104, "CED Nederland", "Repalink Support"),
        (114, "Aqurat BV", "Ontwikkeling Fase 1"),
        (120, "Aqurat BV", "Ontwikkeling Fase 2"),
        (109, "Aqurat BV", "Analyse"),
        (110, "Aqurat BV", "Vergaderingen nieuwe app"),
        (115, "Aqurat BV", "Datamigratie"),
        (111, "Aqurat BV", "Architectuur"),
        (121, "Aqurat BV", "Automated testing"),
        (99, "Radiology Partners Europe BV", "IT advies"),
        (2, "Danny Lauryssens nv", "advies en consultancy"),
        (96, "Danny Lauryssens nv", "ontwikkeling LaurApp"),
        (108, "Danny Lauryssens nv", "herstel calculator"),
        (105, "Danny Lauryssens nv", "Peppol: advies en integratie"),
        (4, "Danny Lauryssens nv", "installatie, helpdesk en onderhoud ICT"),
        (11, "Vriesveem B.V.", "ICT management"),
        (91, "Vriesveem B.V.", "Doorontwikkeling Cellaware"),
        (61, "Vriesveem B.V.", "Support Cellaware Vriesveem"),
        (117, "Vriesveem B.V.", "Support Cellaware Nemijtek"),
        (93, "Vriesveem B.V.", "Doorontwikkeling Movaware"),
        (75, "Vriesveem B.V.", "Support Movaware"),
        (14, "Vriesveem B.V.", "website aanpassingen"),
        (13, "Vriesveem B.V.", "dringende interventie Vriesveem"),
        (118, "Vriesveem B.V.", "dringende interventie Nemijtek"),
        (82, "Vriesveem B.V.", "dringende interventie Logistics"),
        (84, "Vriesveem B.V.", "Douane"),
        (12, "Vriesveem B.V.", "installatie & configuratie hardware/software"),
        (119, "Vriesveem B.V.", "installatie & conf. hardware/software Nemijtek"),
        (116, "Garage Loos bv", "Integratie planning"),
        (107, "Ellu-Invest bvba", "Vakantiehuis Bourgogne"),
        (31, null, "UrbanIT: administratie"),
        (33, null, "UrbanIT: ontwikkeling UrbanAdmin"),
        (34, null, "UrbanIT: vergadering"),
        (32, null, "UrbanIT: helpdesk/technisch webhosting"),
        (35, null, "UrbanIT: website"),
        (28, null, "eigen computer/infrastructuur"),
        (27, null, "bijleren"),
        (29, null, "mails checken en allerlei"),
        (30, null, "NF contact met klant"),
        (36, null, "BerMaCon: administratie"),
        (58, null, "repalink"),
    };

    private sealed class CacheInhoud
    {
        [JsonPropertyName("opgehaald")] public DateTimeOffset Opgehaald { get; set; }
        [JsonPropertyName("vanaf")] public string Vanaf { get; set; } = "";
        [JsonPropertyName("projecten")] public List<TimesheetProject> Projecten { get; set; } = new();
    }

    private static readonly object Slot = new();
    private static List<TimesheetProject>? _projecten; // op relevantie gesorteerd
    private static DateTimeOffset _opgehaald;
    private static string _vanaf = ""; // begin van de periode achter de gebruikscijfers

    /// <summary>Alle boekbare projecten: eerst wat de laatste 60 dagen gebruikt werd, dan de rest.</summary>
    public static IReadOnlyList<TimesheetProject> Alle
    {
        get
        {
            lock (Slot)
            {
                if (_projecten is null)
                {
                    LaadCache();
                }
                return _projecten!;
            }
        }
    }

    /// <summary>De labels van <see cref="Alle"/>, in dezelfde volgorde (voor keuzelijsten).</summary>
    public static IReadOnlyList<string> Labels => Alle.Select(p => p.Label).ToList();

    /// <summary>
    /// Zoekt het project achter een label, een oude klantnaam ("CED", "Lauryssens laurapp")
    /// of een losse klantnaam ("Vriesveem" → het meest gebruikte Vriesveem-project).
    /// </summary>
    public static TimesheetProject? Zoek(string? sleutel)
    {
        if (string.IsNullOrWhiteSpace(sleutel))
        {
            return null;
        }
        sleutel = sleutel.Trim();
        var alle = Alle;
        if (alle.FirstOrDefault(p => p.Label.Equals(sleutel, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }
        if (Aliassen.TryGetValue(sleutel, out var aliasId) && Zoek(aliasId) is { } alias)
        {
            return alias;
        }
        // Losse klantnaam (taakcategorie, locatie, webversie): het meest gebruikte project
        // van die klant. Alle staat al op relevantie, dus de eerste treffer is de beste.
        var eerste = sleutel.Split(' ', '·', '-')[0].Trim();
        return eerste.Length < 3
            ? null
            : alle.FirstOrDefault(p => p.KlantKort.StartsWith(eerste, StringComparison.OrdinalIgnoreCase) ||
                                       (p.Klant?.Contains(eerste, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public static TimesheetProject? Zoek(int id) => Alle.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Het meest gebruikte project van een klant die in een vrije tekst (plek, agendatitel)
    /// genoemd wordt: op het eerste woord van de klantnaam ("Vriesveem", "CED") of een
    /// onderscheidend laatste woord ("Danny Lauryssens" → "Lauryssens"). Null = geen treffer.
    /// </summary>
    public static TimesheetProject? ZoekInTekst(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst))
        {
            return null;
        }
        return Alle.Where(p => p.Klant is not null).FirstOrDefault(p =>
        {
            var woorden = p.KlantKort.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kandidaten = new List<string>();
            if (woorden.Length > 0 && woorden[0].Length >= 3)
            {
                kandidaten.Add(woorden[0]);
            }
            if (woorden.Length > 1 && woorden[^1].Length >= 5 && !AlgemeneWoorden.Contains(woorden[^1]))
            {
                kandidaten.Add(woorden[^1]);
            }
            return kandidaten.Any(w => Regex.IsMatch(tekst, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
        });
    }

    private static readonly HashSet<string> AlgemeneWoorden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Belgium", "Belgie", "België", "Nederland", "Europe", "Invest", "Group", "Partners", "OUD",
    };

    /// <summary>Het label voor een sleutel (zie <see cref="Zoek(string)"/>); onbekend = ongewijzigd.</summary>
    public static string LabelVoor(string? sleutel) =>
        Zoek(sleutel)?.Label ?? sleutel?.Trim() ?? "";

    /// <summary>Het label van het niet-factureerbare project (privé, contact zonder facturatie).</summary>
    public static string NietFactureerbaar => Zoek(NietFactureerbaarId)?.Label ?? "Niet factureerbaar";

    public static string ZonderRechtsvorm(string klant) =>
        Regex.Replace(klant, @"\s+(b\.?v\.?b\.?a\.?|b\.?v\.?|n\.?v\.?)(?=\s|$)", "",
            RegexOptions.IgnoreCase).Trim();

    /// <summary>
    /// Haalt de catalogus opnieuw op als de cache ouder is dan 6 uur (of bij <paramref name="forceer"/>).
    /// Fouten worden ingeslikt: de laatste stand blijft dan gewoon gelden.
    /// </summary>
    public static async Task VernieuwAlsNodigAsync(CancellationToken ct, bool forceer = false)
    {
        if (!forceer && DateTimeOffset.Now - Opgehaald() < MaxLeeftijd)
        {
            return;
        }
        if (LaunchConfig.LoadOrCreate().Timesheets is not { Token.Length: > 0 } settings)
        {
            return;
        }
        if (!await VernieuwSlot.WaitAsync(0, ct))
        {
            return; // er loopt al een verversing
        }
        try
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/workmanager/projecten/{settings.Token}" +
                      $"?dagen={Dagen}&gebruiker_id={settings.GebruikerId}";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return; // bv. 404 zolang het endpoint niet op productie staat
            }
            var inhoud = JsonSerializer.Deserialize<CacheInhoud>(
                await response.Content.ReadAsStringAsync(ct));
            if (inhoud?.Projecten is not { Count: > 0 })
            {
                return;
            }
            inhoud.Opgehaald = DateTimeOffset.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            await File.WriteAllTextAsync(Bestand, JsonSerializer.Serialize(inhoud,
                new JsonSerializerOptions { WriteIndented = true }), ct);
            lock (Slot)
            {
                Zet(inhoud.Projecten, inhoud.Opgehaald, inhoud.Vanaf);
            }
        }
        catch
        {
            // Offline of urbanadmin hapert: de vorige stand blijft.
        }
        finally
        {
            VernieuwSlot.Release();
        }
    }

    private static DateTimeOffset Opgehaald()
    {
        _ = Alle; // zorgt dat de cache geladen is
        lock (Slot)
        {
            return _opgehaald;
        }
    }

    private static void LaadCache()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<CacheInhoud>(File.ReadAllText(Bestand)) is { Projecten.Count: > 0 } inhoud)
            {
                Zet(inhoud.Projecten, inhoud.Opgehaald, inhoud.Vanaf);
                return;
            }
        }
        catch
        {
            // Onleesbaar: dan de ingebouwde lijst.
        }
        Zet(Reserve.Select(r => new TimesheetProject { Id = r.Id, Klant = r.Klant, Naam = r.Naam }).ToList(),
            DateTimeOffset.MinValue, "");
    }

    /// <summary>
    /// Zet de lijst klaar: inactief-en-ongebruikt eruit, de recente WorkManager-boekingen
    /// die nog niet in de serverstand zitten erbij geteld, en gesorteerd op relevantie.
    /// </summary>
    private static void Zet(List<TimesheetProject> projecten, DateTimeOffset opgehaald, string vanaf)
    {
        // Klanten die in urbanadmin als "… OUD" gemarkeerd zijn (Nemijtek zit sinds de fusie
        // bij Vriesveem) alleen tonen als er recent toch nog op geboekt werd.
        var lijst = projecten
            .Where(p => p.Actief || p.Aantal > 0)
            .Where(p => p.Aantal > 0 ||
                        !Regex.IsMatch(p.Klant ?? "", @"\bOUD\s*$", RegexOptions.IgnoreCase))
            .ToList();
        // Recente lokale boekingen tellen mee: de serverstand kan uren oud zijn (of bij de
        // reservelijst helemaal leeg), en net daar zit wat nu relevant is.
        try
        {
            var grens = DateOnly.FromDateTime(DateTime.Today.AddDays(-Dagen));
            foreach (var regel in TimesheetStore.Load().Where(r => r.Datum >= grens && r.Aangemaakt > opgehaald))
            {
                var id = regel.ProjectId ??
                    (Aliassen.TryGetValue(regel.Klant, out var a) ? a
                        : lijst.FirstOrDefault(p => p.Label.Equals(regel.Klant, StringComparison.OrdinalIgnoreCase))?.Id);
                if (lijst.FirstOrDefault(p => p.Id == id) is { } project)
                {
                    project.LokaleMinuten += regel.Minuten;
                }
            }
        }
        catch
        {
            // Zonder lokale telling verder.
        }
        _projecten = lijst
            .OrderByDescending(p => p.Score > 0)
            .ThenByDescending(p => p.Score)
            .ThenBy(p => p.Label, StringComparer.Create(new CultureInfo("nl-BE"), true))
            .ToList();
        _opgehaald = opgehaald;
        _vanaf = vanaf;
    }

    /// <summary>De catalogus als promptblok voor Claude: id, label, gebruik en recente omschrijvingen.</summary>
    public static string PromptBlok()
    {
        var sb = new StringBuilder();
        var alle = Alle;
        string periode;
        lock (Slot)
        {
            periode = _vanaf.Length > 0 ? $"sinds {_vanaf}" : "recent";
        }
        foreach (var p in alle)
        {
            sb.Append("- [").Append(p.Id).Append("] ").Append(p.Label);
            if (p.Score > 0)
            {
                sb.Append($" — {periode} {p.Score / 60.0:0.#} u geboekt");
                if (p.Laatst is { Length: > 0 } laatst)
                {
                    sb.Append(", laatst ").Append(laatst);
                }
            }
            else
            {
                sb.Append(" — recent niet gebruikt");
            }
            if (p.Omschrijvingen.Count > 0)
            {
                sb.Append("; recent: ").Append(string.Join(", ",
                    p.Omschrijvingen.Take(4).Select(o => $"\"{o.Trim()}\"")));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
