using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Dagelijks overzicht van de CC-map in Outlook (waar een regel alle mails heen verplaatst
/// waarin Maarten in de cc staat): één keer per dag worden de nieuwe CC-mails opgehaald en
/// door Claude geanalyseerd. Het resultaat is één overzichtsrij in de cockpit-inbox met
/// per mail een korte samenvatting; mails waarin Maarten zelf genoemd of aangesproken
/// wordt komen daarnaast als volledige (urgente) rij in de lijst.
/// </summary>
public static class CcOverzicht
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "cc-overzicht.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        IncludeFields = true, // MailBericht gebruikt velden
    };

    private sealed class State
    {
        public string LaatsteDag { get; set; } = "";
        public List<string> Verwerkt { get; set; } = new();
        public List<MailBericht> Rijen { get; set; } = new();
    }

    /// <summary>Dinsdag en donderdag zijn de CC-overzichtdagen.</summary>
    private static bool IsCcDag(DateTime dag) =>
        dag.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday;

    /// <summary>
    /// Geeft de actuele CC-rijen voor de berichtenlijst. Het overzicht wordt alleen op
    /// dinsdag en donderdag getoond en dan ook alleen op die dag gegenereerd (na 07:00);
    /// het bevat álle CC-mails sinds het vorige overzicht. Buiten die dagen blijft de
    /// lijst leeg.
    /// </summary>
    public static async Task<List<MailBericht>> RijenAsync(CancellationToken ct)
    {
        // Buiten dinsdag/donderdag niets doen én niets tonen.
        if (!IsCcDag(DateTime.Now))
        {
            return new List<MailBericht>();
        }
        var state = Laad();
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (state.LaatsteDag != vandaag && DateTime.Now.Hour >= 7 && OutlookClient.OoitGekoppeld)
        {
            try
            {
                await GenereerAsync(state, ct);
                state.LaatsteDag = vandaag;
                Bewaar(state);
                Log($"KLAAR: dag gemarkeerd, {state.Rijen.Count} rijen bewaard");
            }
            catch (Exception ex)
            {
                // Outlook of Claude even niet beschikbaar: volgende poll opnieuw proberen.
                Log($"MISLUKT: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Alleen het overzicht van vandaag tonen (dinsdag of donderdag).
        var grens = DateTimeOffset.Now.Date;
        return state.Rijen.Where(r => r.Datum >= grens).ToList();
    }

    /// <summary>
    /// Haalt een gearchiveerde CC-rij (het overzicht of een "genoemd"-mail) definitief uit de
    /// opgeslagen rijen, zodat hij na archiveren niet bij de volgende poll terugkomt.
    /// </summary>
    public static void Verwijder(string messageId)
    {
        if (messageId.Length == 0)
        {
            return;
        }
        var state = Laad();
        if (state.Rijen.RemoveAll(r => r.MessageId == messageId) > 0)
        {
            Bewaar(state);
        }
    }

    private static void Log(string melding)
    {
        try
        {
            var pad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "cc-debug.txt");
            File.AppendAllText(pad, $"{DateTime.Now:HH:mm:ss} [gen] {melding}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    private static async Task GenereerAsync(State state, CancellationToken ct)
    {
        // Sinds het overzicht nog maar twee keer per week draait (di/do), stapelen er meer
        // CC-mails op tussen twee beurten: ruimere cap zodat de volledige lijst sinds het
        // vorige overzicht wordt meegenomen. Reeds verwerkte mails vallen via state.Verwerkt af.
        var mails = await OutlookClient.Instance.CcMailsAsync(state.Verwerkt, max: 40, ct);
        Log($"CcMailsAsync gaf {mails.Count} mails terug");
        if (mails.Count == 0)
        {
            return; // niets nieuws in de CC-map
        }

        // Eén Claude-beurt voor alle mails: per mail een korte samenvatting en de vraag
        // of Maarten erin genoemd/aangesproken wordt.
        var blokken = string.Join("\n\n", mails.Select((m, i) =>
            $"=== MAIL {i} ===\nVan: {m.Van}\nOnderwerp: {m.Onderwerp}\n" +
            $"Datum: {m.Datum:yyyy-MM-dd HH:mm}\n" +
            m.Tekst[..Math.Min(2500, m.Tekst.Length)]));
        var prompt =
            $$"""
            Je bent de mailassistent van Maarten Bergmans (maarten.bergmans@ced.be). Hieronder
            staan mails waarin Maarten alleen in de CC stond. Vat elke mail kort samen (één à
            twee zinnen, Nederlands, zakelijk) en beoordeel of Maarten er persoonlijk in
            genoemd of aangesproken wordt ("Maarten", "@Maarten" — zijn e-mailadres in
            headers/citaten telt niet).

            Antwoord UITSLUITEND met één JSON-array, zonder verdere tekst of markdown:
            [{"i": 0, "samenvatting": "…", "maarten": true of false}, …]

            {{blokken}}
            """;
        Log($"Claude aanroepen (prompt {prompt.Length} tekens)");
        var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
        Log($"Claude gaf {output.Length} tekens terug: {output[..Math.Min(120, output.Length)].Replace("\r", " ").Replace("\n", " ")}");
        var start = output.IndexOf('[');
        var einde = output.LastIndexOf(']');
        if (start < 0 || einde <= start)
        {
            throw new InvalidOperationException("Geen JSON-array in het antwoord van Claude.");
        }
        var samenvattingen = new Dictionary<int, (string Tekst, bool Maarten)>();
        using (var doc = JsonDocument.Parse(output[start..(einde + 1)]))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                samenvattingen[e.GetProperty("i").GetInt32()] = (
                    e.TryGetProperty("samenvatting", out var s) ? s.GetString() ?? "" : "",
                    e.TryGetProperty("maarten", out var m) && m.ValueKind == JsonValueKind.True);
            }
        }

        // Het overzichtsbericht: één rij met alle samenvattingen.
        var datum = DateTimeOffset.Now;
        // Het overzicht beslaat meerdere dagen (di→do): per mail dag + tijdstip tonen.
        var nl = System.Globalization.CultureInfo.GetCultureInfo("nl-BE");
        string Wanneer(DateTimeOffset d) => d.ToString("ddd d MMM HH:mm", nl);
        var regels = mails.Select((m, i) =>
        {
            var (samenvatting, genoemd) = samenvattingen.GetValueOrDefault(i, ("", false));
            return (Mail: m, Samenvatting: samenvatting, Genoemd: genoemd);
        }).ToList();
        var overzicht = new MailBericht
        {
            MessageId = $"cc-overzicht:{datum:yyyy-MM-dd}",
            Van = "CC-overzicht",
            VanAdres = "CC-map",
            Onderwerp = $"📋 CC-mails: {mails.Count} nieuwe" +
                (regels.Count(r => r.Genoemd) is var n && n > 0 ? $", {n}× Maarten genoemd" : ""),
            Tekst = "Mails waarin je in de CC stond (samenvattingen door Claude):\n\n" +
                string.Join("\n\n", regels.Select(r =>
                    $"• {r.Mail.Van} — {r.Mail.Onderwerp} ({Wanneer(r.Mail.Datum)})" +
                    (r.Genoemd ? " ⚠️ (jij wordt genoemd)" : "") +
                    $"\n  {r.Samenvatting}")) +
                "\n\n(Klik op een mail om hem helemaal te openen; archiveren zet alle CC-mails in Outlook op gelezen en haalt dit overzicht uit de lijst.)",
            Html = "<div style=\"font-size:13.5px\"><p><b>Mails waarin je in de CC stond</b> " +
                "(samenvattingen door Claude — klik een mail aan om hem te openen):</p>" +
                string.Join("", regels.Select((r, i) =>
                    $"<a href=\"wm-ccmail:{i}\" style=\"text-decoration:none;color:inherit;display:block;" +
                    "margin:0 0 12px;padding:10px 12px;background:#f6f8fc;border-radius:8px;cursor:pointer" +
                    (r.Genoemd ? ";border-left:4px solid #d93025" : "") + "\">" +
                    $"<b>{System.Net.WebUtility.HtmlEncode(r.Mail.Van)}</b> — " +
                    System.Net.WebUtility.HtmlEncode(r.Mail.Onderwerp) +
                    $" <span style=\"color:#80868b;white-space:nowrap\">{Wanneer(r.Mail.Datum)}</span>" +
                    (r.Genoemd ? " <span style=\"color:#d93025\">● jij wordt genoemd</span>" : "") +
                    " <span style=\"color:#1a73e8\">→ openen</span>" +
                    $"<div style=\"color:#444;margin-top:4px\">{System.Net.WebUtility.HtmlEncode(r.Samenvatting)}</div>" +
                    "</a>")) + "</div>",
            Datum = datum,
            // De volledige mails onder de overzichtsrij, zodat elke samenvatting doorklikbaar
            // is naar het echte bericht (zelfde volgorde als de links hierboven).
            CcDetails = regels.Select(r => new MailBericht
            {
                Van = r.Mail.Van,
                VanAdres = "CC-map",
                Onderwerp = r.Mail.Onderwerp,
                Tekst = r.Mail.Tekst,
                Html = r.Mail.Html,
                Datum = r.Mail.Datum,
            }).ToList(),
        };
        var nieuweRijen = new List<MailBericht> { overzicht };

        // Mails waarin Maarten genoemd wordt: als volledige, urgente rij in de lijst.
        foreach (var r in regels.Where(r => r.Genoemd))
        {
            nieuweRijen.Add(new MailBericht
            {
                MessageId = OutlookClient.CcSleutel(r.Mail.Van, r.Mail.Onderwerp, r.Mail.Datum),
                Van = r.Mail.Van,
                VanAdres = "CC-map",
                Onderwerp = $"📣 {r.Mail.Onderwerp}",
                Tekst = r.Mail.Tekst +
                    "\n\n(Uit de CC-map — jij wordt genoemd; beantwoorden in Outlook zelf.)",
                Html = r.Mail.Html,
                Datum = r.Mail.Datum,
                Urgent = true,
            });
        }

        // De nieuwe mails als verwerkt markeren en de rijen bewaren (oude opruimen).
        state.Verwerkt.AddRange(mails.Select(m =>
            OutlookClient.CcSleutel(m.Van, m.Onderwerp, m.Datum)));
        if (state.Verwerkt.Count > 400)
        {
            state.Verwerkt.RemoveRange(0, state.Verwerkt.Count - 400);
        }
        state.Rijen.RemoveAll(r => r.Datum < DateTimeOffset.Now.AddDays(-2));
        state.Rijen.AddRange(nieuweRijen);

        // De overzichtsrij en CC-mailrijen zijn geen chats: de Claude-screening en de
        // conceptgenerator moeten er vanaf blijven (concept staat "klaar").
        var conceptCache = ConceptCache.Load();
        foreach (var rij in nieuweRijen)
        {
            conceptCache[rij.MessageId] = new ConceptCache.Entry
            {
                ConceptKlaar = true,
                Reden = "CC-overzicht (alleen ter info)",
                Urgent = rij.Urgent,
                Datum = rij.Datum,
            };
        }
        ConceptCache.Save(conceptCache);
    }

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), JsonOpts) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen (hooguit één dubbele analyse).
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }
}
