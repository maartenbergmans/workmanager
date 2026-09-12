using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Houdt bij wat Maarten zelf beloofd heeft. De <see cref="OnbeantwoordRadar"/> bewaakt vragen
/// die bij hem blijven liggen; dit is de andere helft: toezeggingen in zijn eigen verzonden
/// mails ("ik kijk het na", "morgen stuur ik de offerte", "ik pas dat aan zodra …"). Elke
/// ochtend leest Claude de verzonden mails van de voorbije dag(en) — Gmail met de volledige
/// eigen tekst, CED-Outlook met het begin van de mail — en maakt van elke concrete toezegging
/// een taak "🤝 Beloofd aan …" met een deadline.
/// <para>State: %APPDATA%\WorkManager\belofte-radar.json (gescande dagen, gemelde beloftes).</para>
/// </summary>
public static class BelofteRadar
{
    public const string TaakPrefix = "🤝 Beloofd";

    /// <summary>Zoveel dagen terug wordt een gemiste scan nog ingehaald.</summary>
    private const int InhaalDagen = 3;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "belofte-radar.json");

    private sealed class State
    {
        public List<string> GescandeDagen { get; set; } = new();
        public List<string> Gemeld { get; set; } = new();
    }

    private sealed record Bron(string Id, DateTimeOffset Moment, string Kanaal, string Aan, string AanAdres,
        string Onderwerp, string Tekst);

    private static bool _bezig;

    /// <summary>
    /// Vanaf 7 u: scant elke nog niet gescande dag van de voorbije <see cref="InhaalDagen"/>
    /// dagen (gisteren dus ook de avondmails). Retourneert het aantal nieuwe beloftetaken.
    /// Aanroepen op de UI-thread (de Outlook-scrape draait in een WebView2).
    /// </summary>
    public static async Task<int> ZorgVoorAsync(CancellationToken ct, Action<int>? gemeld = null)
    {
        if (_bezig || DateTime.Now.Hour < 7)
        {
            return 0;
        }
        var state = Laad();
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var dagen = Enumerable.Range(1, InhaalDagen).Select(i => vandaag.AddDays(-i))
            .Where(d => !state.GescandeDagen.Contains(d.ToString("yyyy-MM-dd")))
            .OrderBy(d => d)
            .ToList();
        if (dagen.Count == 0)
        {
            return 0;
        }
        _bezig = true;
        var nieuw = 0;
        try
        {
            foreach (var dag in dagen)
            {
                nieuw += await ScanAsync(dag, state, ct);
                state.GescandeDagen.Add(dag.ToString("yyyy-MM-dd"));
                if (state.GescandeDagen.Count > 60)
                {
                    state.GescandeDagen.RemoveRange(0, state.GescandeDagen.Count - 60);
                }
                if (state.Gemeld.Count > 500)
                {
                    state.Gemeld.RemoveRange(0, state.Gemeld.Count - 500);
                }
                Bewaar(state);
            }
            if (nieuw > 0)
            {
                gemeld?.Invoke(nieuw);
            }
        }
        catch
        {
            // Geen mail of Claude hapert: die dag is dan niet als gescand gemarkeerd en komt
            // bij de volgende tik opnieuw aan de beurt.
        }
        finally
        {
            _bezig = false;
        }
        return nieuw;
    }

    /// <summary>Scant één dag en maakt de taken; retourneert het aantal nieuwe taken.</summary>
    public static async Task<int> ScanAsync(DateOnly dag, CancellationToken ct) =>
        await ScanAsync(dag, Laad(), ct, bewaar: true);

    private static async Task<int> ScanAsync(DateOnly dag, State state, CancellationToken ct, bool bewaar = false)
    {
        var bronnen = new List<Bron>();
        var settings = MailReplySettings.Load();
        if (settings.AppWachtwoord.Length > 0 && settings.Email.Length > 0)
        {
            foreach (var m in await GmailClient.VerzondenMetTekstAsync(settings, dag, ct))
            {
                bronnen.Add(new Bron($"g{bronnen.Count + 1}", m.Moment, "Gmail", m.Aan, m.AanAdres,
                    m.Onderwerp, Kort(m.EigenTekst, 1500)));
            }
        }
        try
        {
            if (OutlookClient.OoitGekoppeld)
            {
                foreach (var regel in await OutlookClient.Instance.VerzondenVanDagAsync(dag, ct))
                {
                    // "HH:mm aan X — "onderwerp" · preview"
                    bronnen.Add(new Bron($"o{bronnen.Count + 1}", dag.ToDateTime(TimeOnly.MinValue),
                        "CED-Outlook", "", "", "", regel));
                }
            }
        }
        catch
        {
            // Outlook niet aangemeld (MFA): dan alleen Gmail.
        }
        if (bronnen.Count == 0)
        {
            return 0;
        }

        var categorieen = MijnTaakStore.Load().Categorieen;
        var sb = new StringBuilder();
        foreach (var b in bronnen)
        {
            sb.AppendLine($"### [{b.Id}] {b.Kanaal} {b.Moment.LocalDateTime:HH:mm}" +
                          (b.Aan.Length > 0 ? $" aan {b.Aan}" : "") +
                          (b.Onderwerp.Length > 0 ? $" — \"{b.Onderwerp}\"" : ""));
            sb.AppendLine(b.Tekst);
            sb.AppendLine();
        }
        var prompt = $$"""
            Hieronder de mails die Maarten (freelance IT'er, UrbanIT; teamleider IT bij CED) op
            {{dag:dddd d MMMM yyyy}} verstuurde. Van Gmail staat zijn eigen tekst erbij (citaten
            weggeknipt); van CED-Outlook alleen ontvanger, onderwerp en het begin van de mail.

            OPDRACHT: haal er de concrete toezeggingen uit — dingen die Maarten belooft zelf nog
            te doen of te bezorgen ("ik kijk het na", "ik stuur je morgen …", "ik pas dat aan",
            "ik laat iets weten tegen vrijdag", "ik bel je volgende week").
            NIET meenemen:
            - beleefdheidsformules en vage zinnen ("ik hou je op de hoogte", "laat maar weten");
            - wat in dezelfde mail al gebeurd is ("in bijlage vind je", "ik heb het aangepast");
            - taken die de ontvanger moet doen;
            - een toezegging die in een latere mail van deze lijst al ingelost werd.
            Bij twijfel weglaten: een valse taak is erger dan een gemiste.

            Per toezegging:
            - "bron": het id tussen [ ] van de mail;
            - "taak": korte actie in de gebiedende wijs, Nederlands, max. 90 tekens, met genoeg
              context om zonder de mail te begrijpen wat er moet gebeuren;
            - "aan": aan wie het beloofd is (voornaam of organisatie);
            - "deadline": yyyy-MM-dd als die uit de tekst volgt ("morgen", "vrijdag", "volgende
              week" = maandag daarop), anders null;
            - "categorie": exact één van: {{string.Join(", ", categorieen)}}
              (CED-Outlook-mails zijn altijd CED).

            Antwoord uitsluitend met JSON: {"beloftes": [{"bron": "g1", "taak": "…", "aan": "…", "deadline": null, "categorie": "CED"}]}
            Geen toezeggingen gevonden: {"beloftes": []}

            MAILS
            {{sb}}
            """;

        using var doc = ClaudeDrafter.ParseJson(await ClaudeDrafter.RunClaudeAsync(prompt, ct));
        if (!doc.RootElement.TryGetProperty("beloftes", out var lijst) || lijst.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        var data = MijnTaakStore.Load();
        var nieuw = 0;
        foreach (var el in lijst.EnumerateArray())
        {
            var taak = Tekst(el, "taak");
            if (taak.Length == 0)
            {
                continue;
            }
            var aan = Tekst(el, "aan");
            var bron = bronnen.FirstOrDefault(b => b.Id == Tekst(el, "bron"));
            var sleutel = $"{dag:yyyy-MM-dd}|{aan}|{taak}".ToLowerInvariant();
            if (state.Gemeld.Contains(sleutel))
            {
                continue;
            }
            var categorie = Tekst(el, "categorie");
            if (!data.Categorieen.Contains(categorie))
            {
                categorie = bron?.Kanaal == "CED-Outlook" ? "CED" : data.Categorieen.FirstOrDefault() ?? "";
            }
            var deadline = DateOnly.TryParse(Tekst(el, "deadline"), out var d) && d >= dag
                ? d
                : DateOnly.FromDateTime(DateTime.Now).AddDays(1);
            data.Taken.Add(new MijnTaak
            {
                Tekst = $"{TaakPrefix} aan {(aan.Length > 0 ? aan : "?")}: {taak}",
                Categorie = categorie,
                Prioriteit = 1,
                Deadline = deadline,
                // De mail erbij: zo zie je in de taak wat je precies schreef, en "Beantwoorden"
                // gaat naar wie je het beloofde.
                Mail = bron is null ? null : new TaakMail
                {
                    Van = bron.Aan,
                    VanAdres = bron.AanAdres,
                    Onderwerp = bron.Onderwerp,
                    Tekst = bron.Tekst,
                    Datum = bron.Moment,
                },
            });
            state.Gemeld.Add(sleutel);
            nieuw++;
        }
        if (nieuw > 0)
        {
            MijnTaakStore.Save(data);
        }
        if (bewaar)
        {
            Bewaar(state);
        }
        return nieuw;
    }

    private static string Tekst(JsonElement el, string veld) =>
        el.TryGetProperty(veld, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    private static string Kort(string tekst, int max) =>
        tekst.Length <= max ? tekst : tekst[..max] + " …";

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
