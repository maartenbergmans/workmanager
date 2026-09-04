using System.Text.Json;

namespace WorkManager;

/// <summary>Eén jarige met zijn cadeaugeschiedenis en ideeën.</summary>
public sealed class Jarige
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Naam { get; set; } = "";
    public int Dag { get; set; }
    public int Maand { get; set; }
    /// <summary>Geboortejaar; 0 = onbekend (dan toont de radar geen leeftijd).</summary>
    public int Jaar { get; set; }
    /// <summary>Relatie ("echtgenote", "dochter", "schoonmoeder", …) — context voor de ideeën.</summary>
    public string Relatie { get; set; } = "";
    /// <summary>Vrije notitie: interesses, maten, wat hij/zij zeker niet wil.</summary>
    public string Notities { get; set; } = "";
    /// <summary>Richtbedrag in euro; 0 = geen afspraak.</summary>
    public int Budget { get; set; }
    /// <summary>Hoeveel dagen vooraf de "cadeau bedenken"-taak moet verschijnen.</summary>
    public int DagenVooraf { get; set; } = 21;
    /// <summary>Link naar het online verlanglijstje (mijnverlanglijst.eu) — inspiratie voor de ideeën.</summary>
    public string Verlanglijst { get; set; } = "";
    /// <summary>Bewaarde ideeën (uit Claude of zelf getypt) voor het volgende cadeau.</summary>
    public List<string> Ideeen { get; set; } = new();
    /// <summary>Wat er eerder gegeven is — voorkomt dat je jezelf herhaalt.</summary>
    public List<GegevenCadeau> Gegeven { get; set; } = new();

    /// <summary>De eerstvolgende verjaardag vanaf vandaag (vandaag telt mee).</summary>
    public DateOnly Volgende(DateOnly vanaf)
    {
        var dag = Math.Clamp(Dag, 1, 31);
        var maand = Math.Clamp(Maand, 1, 12);
        // 29 februari in een gewoon jaar: op 28 februari vieren.
        var dezeKeer = new DateOnly(vanaf.Year, maand,
            Math.Min(dag, DateTime.DaysInMonth(vanaf.Year, maand)));
        if (dezeKeer >= vanaf)
        {
            return dezeKeer;
        }
        var jaar = vanaf.Year + 1;
        return new DateOnly(jaar, maand, Math.Min(dag, DateTime.DaysInMonth(jaar, maand)));
    }

    public int DagenTot(DateOnly vanaf) => Volgende(vanaf).DayNumber - vanaf.DayNumber;

    /// <summary>De leeftijd die hij/zij op de eerstvolgende verjaardag wordt; null = jaar onbekend.</summary>
    public int? WordtOp(DateOnly vanaf) => Jaar > 0 ? Volgende(vanaf).Year - Jaar : null;
}

/// <summary>Een cadeau dat ooit gegeven is (jaar + wat het was).</summary>
public sealed class GegevenCadeau
{
    public int Jaar { get; set; }
    public string Wat { get; set; } = "";
}

public sealed class VerjaardagData
{
    public List<Jarige> Jarigen { get; set; } = new();
    /// <summary>Verwerkte taken, sleutel "id|jaar|soort" — zo komt elke taak maar één keer.</summary>
    public List<string> Verwerkt { get; set; } = new();
}

/// <summary>
/// De verjaardag- en cadeauradar: houdt de belangrijke verjaardagen bij
/// (%APPDATA%\WorkManager\verjaardagen.json) en zet op tijd taken klaar — eerst om een
/// cadeau te bedenken (standaard drie weken vooraf), dan om het te kopen (vijf dagen
/// vooraf) en op de dag zelf om te feliciteren. Cadeau-ideeën vraagt hij aan Claude, met
/// de eerder gegeven cadeaus erbij zodat je jezelf niet herhaalt.
/// </summary>
public static class Verjaardagen
{
    public const string BedenkPrefix = "🎁 Cadeau bedenken";
    public const string KoopPrefix = "🎁 Cadeau kopen";
    public const string VieringPrefix = "🎂";

    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "verjaardagen.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>De verjaardagen die Maarten doorgaf; de basis bij een leeg bestand.</summary>
    private static List<Jarige> Standaard() => new()
    {
        new Jarige { Naam = "Hilke", Dag = 12, Maand = 6, Relatie = "echtgenote", DagenVooraf = 28 },
        new Jarige { Naam = "Emilia", Dag = 13, Maand = 9, Relatie = "dochter" },
        new Jarige { Naam = "Lisa", Dag = 23, Maand = 5, Relatie = "dochter" },
        new Jarige { Naam = "Oma", Dag = 25, Maand = 11, Relatie = "oma" },
        new Jarige { Naam = "Vava", Dag = 5, Maand = 4, Relatie = "vava (opa)" },
        new Jarige { Naam = "Eline", Dag = 30, Maand = 12, Relatie = "familie" },
        new Jarige { Naam = "Robin", Dag = 14, Maand = 1, Relatie = "familie" },
    };

    public static VerjaardagData Load()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<VerjaardagData>(File.ReadAllText(DataFile)) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: met de standaardlijst verder.
        }
        return new VerjaardagData { Jarigen = Standaard() };
    }

    public static void Save(VerjaardagData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(data, JsonOpts));
    }

    /// <summary>De jarigen op volgorde van "wie is het eerst aan de beurt".</summary>
    public static List<Jarige> OpVolgorde(DateOnly vanaf) =>
        Load().Jarigen.OrderBy(j => j.DagenTot(vanaf)).ToList();

    /// <summary>
    /// Zet de taken klaar die vandaag aan de beurt zijn. Per persoon en per jaar hooguit één
    /// taak van elke soort; taken die de gebruiker weggooit komen dus niet terug.
    /// </summary>
    public static void ZorgVoorTaken()
    {
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var data = Load();
            var taken = MijnTaakStore.Load();
            var gewijzigd = false;

            foreach (var jarige in data.Jarigen)
            {
                var dag = jarige.Volgende(vandaag);
                var dagen = jarige.DagenTot(vandaag);
                var leeftijd = jarige.WordtOp(vandaag) is { } l ? $" wordt {l}" : "";

                // 1) Bedenken: vanaf X dagen vooraf (standaard drie weken).
                if (dagen <= Math.Max(3, jarige.DagenVooraf) && dagen > 5)
                {
                    gewijzigd |= VoegToe(data, taken, jarige, dag, "bedenk",
                        $"{BedenkPrefix} voor {jarige.Naam} ({dag:d MMM}{leeftijd})",
                        dag.AddDays(-7),
                        $"Over {dagen} dagen jarig. Dubbelklik op deze taak in de takenlijst " +
                        "(of klik hieronder op de knop) voor cadeau-ideeën.");
                }
                // 2) Kopen: de laatste vijf dagen, met de verjaardag als deadline.
                if (dagen is <= 5 and >= 0)
                {
                    gewijzigd |= VoegToe(data, taken, jarige, dag, "koop",
                        $"{KoopPrefix} voor {jarige.Naam} ({dag:ddd d MMM}{leeftijd})",
                        dag.AddDays(-1),
                        "Cadeau in huis halen en inpakken.");
                }
                // 3) De dag zelf: feliciteren.
                if (dagen == 0)
                {
                    gewijzigd |= VoegToe(data, taken, jarige, dag, "vier",
                        $"{VieringPrefix} {jarige.Naam} is jarig{leeftijd.Replace(" wordt", " — wordt")}",
                        dag,
                        "Feliciteren!");
                }
            }

            if (gewijzigd)
            {
                MijnTaakStore.Save(taken);
                Save(data);
            }
        }
        catch
        {
            // Best effort: de volgende ronde probeert het opnieuw.
        }
    }

    private static bool VoegToe(VerjaardagData data, MijnTakenData taken, Jarige jarige,
        DateOnly verjaardag, string soort, string tekst, DateOnly deadline, string toelichting)
    {
        var sleutel = $"{jarige.Id}|{verjaardag.Year}|{soort}";
        if (data.Verwerkt.Contains(sleutel))
        {
            return false;
        }
        data.Verwerkt.Add(sleutel);
        // Oude sleutels (ouder dan twee jaar) opruimen zodat het bestand niet blijft groeien.
        data.Verwerkt.RemoveAll(s => s.Split('|') is [_, var jaar, _] &&
            int.TryParse(jaar, out var j) && j < DateTime.Now.Year - 1);
        taken.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "Privé",
            Prioriteit = soort == "koop" ? 0 : 1,
            Deadline = deadline,
            Startdatum = soort == "bedenk" ? null : deadline.AddDays(-3),
            Mail = new TaakMail { Onderwerp = tekst, Tekst = toelichting },
        });
        return true;
    }

    /// <summary>
    /// Laat Claude cadeau-ideeën bedenken voor deze persoon: relatie, leeftijd, interesses,
    /// budget en — belangrijk — wat er de vorige jaren al gegeven is.
    /// </summary>
    public static async Task<List<string>> IdeeenAsync(Jarige jarige, CancellationToken ct)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var leeftijd = jarige.WordtOp(vandaag) is { } l ? $"wordt {l} jaar" : "leeftijd onbekend";
        var eerder = jarige.Gegeven.Count > 0
            ? string.Join("\n", jarige.Gegeven.OrderByDescending(g => g.Jaar)
                .Select(g => $"- {g.Jaar}: {g.Wat}"))
            : "- (nog niets genoteerd)";
        // Het online verlanglijstje is de actuele smaak van de jarige zelf — goud als
        // inspiratie. Best effort: zonder lijstje (of bij een haperende site) gewoon verder.
        var lijstje = new List<string>();
        try
        {
            lijstje = await VerlanglijstItemsAsync(jarige.Verlanglijst, ct);
        }
        catch
        {
            // Site onbereikbaar of lay-out gewijzigd: dan zonder.
        }
        var prompt = $$"""
            Je helpt Maarten (freelance IT'er in Vlaanderen) een verjaardagscadeau kiezen.

            Voor wie: {{jarige.Naam}} ({{jarige.Relatie}}), {{leeftijd}},
            jarig op {{jarige.Volgende(vandaag):d MMMM}}.
            Budget: {{(jarige.Budget > 0 ? $"ongeveer € {jarige.Budget}" : "geen vast bedrag, houd het redelijk")}}.
            Interesses en aandachtspunten: {{(jarige.Notities.Length > 0 ? jarige.Notities : "niet opgegeven")}}.

            Wat er nu op het eigen online verlanglijstje staat (dit is de actuele smaak —
            gebruik het als inspiratie: kies er gerust iets van, of stel iets voor dat er
            logisch bij aansluit):
            {{(lijstje.Count > 0 ? string.Join("\n", lijstje.Select(i => $"- {i}"))
                : "- (geen verlanglijstje bekend of niet op te halen)")}}

            Eerder gegeven cadeaus (NIET herhalen, en niet te dicht in de buurt komen):
            {{eerder}}

            Geef 6 concrete, verschillende ideeën die in België vlot te vinden zijn: een mix van
            spullen, iets beleefs (uitstap, workshop) en iets persoonlijks/zelfgemaakts. Elk idee
            één zin: wát het is, waarom het past, en een indicatie van de prijs. Geen inleiding,
            geen slotzin, geen nummering met punten — alleen de JSON hieronder.

            Antwoord uitsluitend met JSON, exact dit formaat:
            {"ideeen": ["…", "…"]}
            """;
        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        using var doc = ClaudeDrafter.ParseJson(output);
        var ideeen = new List<string>();
        if (doc.RootElement.TryGetProperty("ideeen", out var lijst) &&
            lijst.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in lijst.EnumerateArray())
            {
                if (el.GetString() is { Length: > 0 } idee)
                {
                    ideeen.Add(idee.Trim());
                }
            }
        }
        return ideeen;
    }

    /// <summary>
    /// De artikeltitels van een online verlanglijstje (mijnverlanglijst.eu): de gedeelde
    /// pagina bevat de items gewoon in de HTML (class="title"). Leeg bij een lege link.
    /// </summary>
    public static async Task<List<string>> VerlanglijstItemsAsync(string url, CancellationToken ct)
    {
        var items = new List<string>();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return items;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0) WorkManager");
        var html = await http.GetStringAsync(url, ct);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
                     .Matches(html, "class=\"title\"><span>([^<]+)</span>"))
        {
            var titel = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (titel.Length > 0 && !items.Contains(titel))
            {
                items.Add(titel);
            }
        }
        return items.Take(40).ToList();
    }

    /// <summary>De naam uit een radartaak halen ("🎁 Cadeau kopen voor Hilke (…)" → "Hilke").</summary>
    public static Jarige? UitTaak(string taakTekst)
    {
        var jarigen = Load().Jarigen;
        // De viertaak begint met de naam, de andere twee met "… voor <naam> (".
        var naam = taakTekst.Contains(" voor ", StringComparison.OrdinalIgnoreCase)
            ? taakTekst[(taakTekst.IndexOf(" voor ", StringComparison.OrdinalIgnoreCase) + 6)..]
            : taakTekst.Replace(VieringPrefix, "").TrimStart();
        naam = naam.Split('(')[0].Split(" is jarig")[0].Trim();
        return jarigen.FirstOrDefault(j => j.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Hoort deze taaktekst bij de radar?</summary>
    public static bool IsRadarTaak(string tekst) =>
        tekst.StartsWith(BedenkPrefix, StringComparison.Ordinal) ||
        tekst.StartsWith(KoopPrefix, StringComparison.Ordinal) ||
        tekst.StartsWith(VieringPrefix, StringComparison.Ordinal);
}
