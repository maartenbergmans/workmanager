using System.Text.Json;

namespace WorkManager;

/// <summary>Eén conversatie waarin ik het laatste woord had en nog op antwoord wacht.</summary>
public sealed class FollowUpItem
{
    /// <summary>Gmail-conversatie-id; stabiel over meerdere scans heen.</summary>
    public string ThreadId { get; set; } = "";

    public string MessageId { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public DateTimeOffset Verstuurd { get; set; }

    /// <summary>Ontvangers als "Naam &lt;adres&gt;".</summary>
    public List<string> Ontvangers { get; set; } = new();

    /// <summary>Mijn laatste bericht in de conversatie (ingekorte platte tekst).</summary>
    public string Tekst { get; set; } = "";

    public int BerichtenInThread { get; set; }

    /// <summary>Door Claude opgestelde herinnering; leeg tot je erom vraagt.</summary>
    public string Concept { get; set; } = "";

    /// <summary>Bewust niet opvolgen (verdwijnt uit de lijst, komt niet terug).</summary>
    public bool Genegeerd { get; set; }

    /// <summary>Even niet tonen tot deze datum.</summary>
    public DateTimeOffset? UitgesteldTot { get; set; }

    /// <summary>Wanneer ik een herinnering verstuurd heb; daarna telt de klok opnieuw.</summary>
    public DateTimeOffset? Opgevolgd { get; set; }

    /// <summary>Hoeveel dagen er al zonder antwoord voorbij zijn.</summary>
    public int DagenStil => Math.Max(0, (int)(DateTimeOffset.Now - (Opgevolgd ?? Verstuurd)).TotalDays);

    /// <summary>Korte omschrijving van de tegenpartij, voor in de lijst.</summary>
    public string Wie => Ontvangers.Count switch
    {
        0 => "(onbekend)",
        1 => Naam(Ontvangers[0]),
        _ => $"{Naam(Ontvangers[0])} +{Ontvangers.Count - 1}",
    };

    internal static string Naam(string ontvanger)
    {
        var haakje = ontvanger.IndexOf('<');
        return haakje > 0 ? ontvanger[..haakje].Trim() : ontvanger.Trim();
    }
}

/// <summary>Bewaarde stand van de radar, inclusief de instellingen.</summary>
public sealed class FollowUpData
{
    public List<FollowUpItem> Items { get; set; } = new();

    public DateTimeOffset? LaatstGescand { get; set; }

    /// <summary>Dag (yyyy-MM-dd) waarop de tray-melding het laatst getoond is.</summary>
    public string LaatsteMelding { get; set; } = "";

    /// <summary>Pas na zoveel stille dagen komt een mail in de lijst.</summary>
    public int MinimumDagen { get; set; } = 4;

    /// <summary>Hoe ver terug er gekeken wordt; oudere conversaties zijn meestal dood.</summary>
    public int MaxDagen { get; set; } = 45;
}

/// <summary>
/// Houdt bij op welke van je eigen mails nog niet geantwoord is. De scan groepeert alles uit
/// "Alle berichten" per conversatie: waar jij als laatste schreef en er sindsdien X dagen niets
/// terugkwam, blijft er iemand in gebreke. Zodra een antwoord binnenkomt verdwijnt de
/// conversatie vanzelf uit de lijst — er is geen handmatig afvinken nodig. Voor wat overblijft
/// schrijft Claude op vraag een korte herinnering in dezelfde stijl als de mailassistent
/// (mail-reply-instructions.txt), die als reply in de bestaande thread vertrekt.
/// </summary>
public static class FollowUpRadar
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "followup-state.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Melding voor de tray: (titel, tekst).</summary>
    public static event Action<string, string>? Melding;

    private static bool _bezig;

    public static bool Bezig => _bezig;

    public static FollowUpData Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<FollowUpData>(File.ReadAllText(StateFile), JsonOpts) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen (de volgende scan vult alles weer).
        }
        return new FollowUpData();
    }

    public static void Bewaar(FollowUpData data)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // Best effort: de lijst wordt bij de volgende scan gewoon opnieuw opgebouwd.
        }
    }

    /// <summary>Wat er nu écht om opvolging vraagt: niet genegeerd en niet uitgesteld.</summary>
    public static List<FollowUpItem> Actief(FollowUpData? data = null)
    {
        var nu = DateTimeOffset.Now;
        return (data ?? Laad()).Items
            .Where(i => !i.Genegeerd && (i.UitgesteldTot is null || i.UitgesteldTot <= nu))
            .OrderByDescending(i => i.DagenStil)
            .ToList();
    }

    /// <summary>
    /// Haalt de wachtende conversaties op bij Gmail en voegt ze samen met de bewaarde stand:
    /// bestaande items houden hun concept, uitstel en negeerstatus; conversaties waarop
    /// intussen geantwoord is verdwijnen. Retourneert de actieve lijst.
    /// </summary>
    public static async Task<List<FollowUpItem>> ScanAsync(CancellationToken ct)
    {
        if (_bezig)
        {
            return Actief();
        }
        var settings = MailReplySettings.Load();
        if (settings.AppWachtwoord.Length == 0)
        {
            return Actief();
        }

        _bezig = true;
        try
        {
            var data = Laad();
            var vers = await GmailClient.WachtOpAntwoordAsync(
                settings, data.MinimumDagen, data.MaxDagen, 40, ct);

            var oud = data.Items.ToDictionary(i => i.ThreadId, StringComparer.Ordinal);
            var nieuw = new List<FollowUpItem>();
            foreach (var mail in vers)
            {
                if (oud.TryGetValue(mail.ThreadId, out var bestaand))
                {
                    // Heb ik zelf al een herinnering gestuurd? Dan is dat nu het laatste bericht
                    // en begint de stilte opnieuw; het oude concept is niet meer bruikbaar.
                    if (bestaand.MessageId != mail.MessageId)
                    {
                        bestaand.Concept = "";
                        bestaand.UitgesteldTot = null;
                    }
                    bestaand.MessageId = mail.MessageId;
                    bestaand.Onderwerp = mail.Onderwerp;
                    bestaand.Verstuurd = mail.Verstuurd;
                    bestaand.Ontvangers = mail.Ontvangers;
                    bestaand.Tekst = mail.Tekst;
                    bestaand.BerichtenInThread = mail.BerichtenInThread;
                    nieuw.Add(bestaand);
                    continue;
                }
                nieuw.Add(new FollowUpItem
                {
                    ThreadId = mail.ThreadId,
                    MessageId = mail.MessageId,
                    Onderwerp = mail.Onderwerp,
                    Verstuurd = mail.Verstuurd,
                    Ontvangers = mail.Ontvangers,
                    Tekst = mail.Tekst,
                    BerichtenInThread = mail.BerichtenInThread,
                });
            }

            data.Items = nieuw;
            data.LaatstGescand = DateTimeOffset.Now;
            Bewaar(data);
            return Actief(data);
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Scant hoogstens één keer per dag (op een werkdag, vanaf 9u) en meldt in de tray
    /// hoeveel mensen er nog op antwoord wachten. Slikt fouten in: dit draait op een timer.
    /// </summary>
    public static async Task ZorgVoorMeldingAsync(CancellationToken ct)
    {
        var nu = DateTime.Now;
        if (nu.Hour < 9 || nu.Hour >= 19 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }
        var vandaag = nu.ToString("yyyy-MM-dd");
        var data = Laad();
        if (data.LaatsteMelding == vandaag || _bezig)
        {
            return;
        }
        data.LaatsteMelding = vandaag;
        Bewaar(data);

        try
        {
            var wachtend = await ScanAsync(ct);
            if (wachtend.Count == 0)
            {
                return;
            }
            var voorbeeld = string.Join("\n", wachtend.Take(3).Select(i =>
                $"• {i.Wie} — {i.Onderwerp} ({i.DagenStil} d)"));
            Melding?.Invoke(
                wachtend.Count == 1
                    ? "1 mail wacht op antwoord"
                    : $"{wachtend.Count} mails wachten op antwoord",
                voorbeeld + (wachtend.Count > 3 ? $"\n… en nog {wachtend.Count - 3}" : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Geen net of Gmail-fout: morgen opnieuw.
        }
    }

    /// <summary>Laat Claude een korte herinnering schrijven en bewaart die bij het item.</summary>
    public static async Task<string> ConceptAsync(FollowUpItem item, CancellationToken ct)
    {
        var antwoord = await ClaudeDrafter.RunClaudeAsync(Prompt(item), ct);
        using var doc = ClaudeDrafter.ParseJson(antwoord);
        var tekst = doc.RootElement.TryGetProperty("herinnering", out var waarde) &&
                    waarde.ValueKind == JsonValueKind.String
            ? waarde.GetString() ?? ""
            : "";
        if (tekst.Length == 0)
        {
            throw new InvalidOperationException("Claude gaf geen herinnering terug.");
        }

        var data = Laad();
        if (data.Items.FirstOrDefault(i => i.ThreadId == item.ThreadId) is { } bewaard)
        {
            bewaard.Concept = tekst;
            Bewaar(data);
        }
        item.Concept = tekst;
        return tekst;
    }

    private static string Prompt(FollowUpItem item) =>
        $$"""
        {{MailReplySettings.LoadInstructies()}}

        ---

        Hierboven staan de algemene schrijfinstructies. Nu een andere taak: Maarten stuurde
        onderstaande mail en kreeg er na {{item.DagenStil}} dagen nog geen antwoord op. Schrijf
        een korte, vriendelijke herinnering die als antwoord in dezelfde conversatie vertrekt.

        Regels:
        - Schrijf in dezelfde taal als de oorspronkelijke mail.
        - Kort: hooguit vier zinnen plus de ondertekening. Geen onderwerpregel.
        - Verwijs concreet naar waar het over ging en naar wat je van hen nodig hebt.
        - Niet verwijtend; ga ervan uit dat het gewoon ondergesneeuwd is.
        - Verzin geen nieuwe feiten, bedragen of afspraken.
        - Vermeld geen datums die niet in de oorspronkelijke mail staan.

        Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
        {"herinnering": "de volledige tekst van de mail, inclusief ondertekening"}

        Oorspronkelijke mail:
        ---
        Aan: {{string.Join(", ", item.Ontvangers)}}
        Onderwerp: {{item.Onderwerp}}
        Verstuurd: {{item.Verstuurd:dddd d MMMM yyyy}} ({{item.DagenStil}} dagen geleden)
        Berichten in de conversatie: {{item.BerichtenInThread}}

        {{item.Tekst}}
        ---
        """;

    /// <summary>
    /// Verstuurt de herinnering als antwoord in de bestaande conversatie. Daarna wordt het
    /// item op "opgevolgd" gezet: de teller begint opnieuw en het staat pas weer in de lijst
    /// als het na de volgende scan nóg stil blijft.
    /// </summary>
    public static async Task VerstuurAsync(FollowUpItem item, CancellationToken ct)
    {
        if (item.Concept.Trim().Length == 0)
        {
            throw new InvalidOperationException("Er staat nog geen herinnering klaar.");
        }
        if (item.Ontvangers.Count == 0)
        {
            throw new InvalidOperationException("Deze conversatie heeft geen ontvanger.");
        }

        var settings = MailReplySettings.Load();
        var eerste = item.Ontvangers[0];
        var bericht = new MailBericht
        {
            Uid = 0, // niet uit de inbox: niets te markeren
            Van = FollowUpItem.Naam(eerste),
            AntwoordAan = AdresUit(eerste),
            OverigeOntvangers = item.Ontvangers.Skip(1).Select(AdresUit).Where(a => a.Length > 0).ToList(),
            AlleBeantwoorden = item.Ontvangers.Count > 1,
            Onderwerp = item.Onderwerp,
            MessageId = item.MessageId,
            Concept = item.Concept.Trim(),
        };

        var fouten = new List<string>();
        var verstuurd = await GmailClient.VerstuurAsync(
            settings, new[] { bericht }, regel =>
            {
                if (regel.Contains("mislukt", StringComparison.OrdinalIgnoreCase))
                {
                    fouten.Add(regel);
                }
            }, ct);
        if (verstuurd.Count == 0)
        {
            throw new InvalidOperationException(
                fouten.Count > 0 ? fouten[0] : "Versturen mislukt.");
        }

        var data = Laad();
        if (data.Items.FirstOrDefault(i => i.ThreadId == item.ThreadId) is { } bewaard)
        {
            bewaard.Opgevolgd = DateTimeOffset.Now;
            bewaard.Concept = "";
            bewaard.UitgesteldTot = DateTimeOffset.Now.AddDays(data.MinimumDagen);
            Bewaar(data);
        }
        item.Opgevolgd = DateTimeOffset.Now;
    }

    /// <summary>Zet een conversatie op genegeerd of stelt hem uit; null = niet wijzigen.</summary>
    public static void Markeer(string threadId, bool? genegeerd = null, DateTimeOffset? uitstelTot = null)
    {
        var data = Laad();
        if (data.Items.FirstOrDefault(i => i.ThreadId == threadId) is not { } item)
        {
            return;
        }
        if (genegeerd is { } negeer)
        {
            item.Genegeerd = negeer;
        }
        if (uitstelTot is { } tot)
        {
            item.UitgesteldTot = tot;
        }
        Bewaar(data);
    }

    private static string AdresUit(string ontvanger)
    {
        var start = ontvanger.IndexOf('<');
        var einde = ontvanger.IndexOf('>');
        if (start >= 0 && einde > start)
        {
            return ontvanger[(start + 1)..einde].Trim();
        }
        return ontvanger.Contains('@') ? ontvanger.Trim() : "";
    }
}
