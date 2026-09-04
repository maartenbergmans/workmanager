using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace WorkManager;

/// <summary>Eén binnengekomen mail uit de inbox, met (na generatie) het conceptantwoord.</summary>
public sealed class MailBericht
{
    public uint Uid;
    public string Van = "";
    public string VanAdres = "";
    public string AntwoordAan = "";
    public List<string> OverigeOntvangers = new(); // Aan/cc-adressen behalve de afzender en mezelf
    public List<string> Cc = new(); // volledige cc-lijst (namen/adressen) voor de weergave
    public List<string> Aan = new(); // volledige Aan-lijst voor de weergave (nu alleen CED Outlook)
    public bool AlleBeantwoorden; // reply-all: overige ontvangers in cc meenemen
    public string Onderwerp = "";
    public DateTimeOffset Datum;
    public string Tekst = "";
    public string Html = "";
    public string MessageId = "";
    public List<string> Referenties = new();
    public List<string> Bijlagen = new();
    public List<LinkBijlage> LinkBijlagen = new();
    public bool ConceptKlaar;
    public string Concept = "";
    public string Reden = "";
    public bool Genegeerd; // chat zonder antwoord/actie: uit de lijst laten (oordeel zit in de cache)
    public bool Urgent; // vandaag best beantwoorden (oordeel van Claude; rood in de lijsten)
    public string UitschrijfUrl = ""; // afmeldlink (List-Unsubscribe of link in de tekst); leeg = geen
    public string ChatSpace = ""; // gevuld ("spaces/…") als dit een Google Chat-gesprek is i.p.v. een mail
    public string WhatsAppChat = ""; // gevuld (chatnaam) als dit een WhatsApp-gesprek is
    public string TeamsChat = ""; // gevuld (chatnaam) als dit een Teams-chat is (alleen uitlezen)
    public string OutlookMail = ""; // gevuld (omschrijving) als dit een CED-Outlookmail is (alleen uitlezen)
    public string OutlookUrl = ""; // directe OWA-link naar de mail (voor "Openen in browser" en mention-taken)
    public string SmartschoolBericht = ""; // gevuld ("kind|msgid") als dit een Smartschool-bericht is
    public List<MailBericht> CcDetails = new(); // onderliggende mails van een CC-overzichtsrij (klikbaar detail)
    public string Vertaling = ""; // Nederlandse vertaling (gevuld als de mail Frans/Engels is)
    public bool VertaalVerborgen; // gebruiker zette de vertaling uit via de 🌐-knop

    public bool IsChat =>
        ChatSpace.Length > 0 || WhatsAppChat.Length > 0 || TeamsChat.Length > 0 ||
        OutlookMail.Length > 0 || SmartschoolBericht.Length > 0;

    /// <summary>
    /// Bron-icoontje voor lijstweergaven, met dezelfde symbolen als het gezondheids-
    /// overzicht (🟢 WhatsApp, 💬 Google Chat, 🟪 Teams, 🔷 CED-Outlook, 🎒 Smartschool).
    /// Gewone Gmail-mail blijft kaal — dat is verreweg de grootste groep.
    /// </summary>
    public string BronIcoon =>
        WhatsAppChat.Length > 0 ? "🟢"
        : ChatSpace.Length > 0 ? "💬"
        : TeamsChat.Length > 0 ? "🟪"
        : SmartschoolBericht.Length > 0 ? "🎒"
        : OutlookMail.Length > 0 ? "🔷"
        : "";
}

/// <summary>Downloadbare PDF-link in de mailtekst (bv. een Stripe-factuur) — gedraagt zich als bijlage.</summary>
public sealed class LinkBijlage
{
    public string Naam = "";
    public string Url = "";
}

/// <summary>
/// Gmail-toegang via IMAP (inbox uitlezen) en SMTP (antwoorden versturen),
/// met het e-mailadres + app-wachtwoord uit de instellingen.
/// </summary>
public static class GmailClient
{
    /// <summary>
    /// Diagnose: wat staat er écht in de inbox, wat geeft de zoekopdracht van de cockpit
    /// terug, en wat blijft er na de eigen filters over? Zonder dit is "ik zie er maar twee"
    /// niet na te trekken — de mail kan wegvallen bij Gmail, bij de zoekopdracht of bij de
    /// genegeerd-cache. Zie de CLI-schakelaar --mailcheck.
    /// </summary>
    public static async Task<string> DiagnoseAsync(MailReplySettings s, CancellationToken ct)
    {
        var uit = new System.Text.StringBuilder();
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var alles = await inbox.SearchAsync(SearchQuery.All, ct);
        var zoek = s.AlleenOngelezen ? "-in:snoozed is:unread" : "-in:snoozed";
        var gefilterd = await inbox.SearchAsync(SearchQuery.GMailRawSearch(zoek), ct);
        uit.AppendLine($"INBOX totaal: {alles.Count}");
        uit.AppendLine($"Zoekopdracht cockpit (\"{zoek}\"): {gefilterd.Count}");
        uit.AppendLine();

        var cache = ConceptCache.Load();
        var geselecteerd = new HashSet<uint>(gefilterd.Select(u => u.Id));
        foreach (var uid in alles.Reverse())
        {
            var msg = await inbox.GetMessageAsync(uid, ct);
            var van = msg.From.Mailboxes.FirstOrDefault();
            var inZoek = geselecteerd.Contains(uid.Id);
            var genegeerd = msg.MessageId is { Length: > 0 } id &&
                cache.TryGetValue(id, out var e) && e.Genegeerd;
            var status = !inZoek ? "WEG: niet in zoekopdracht (Gmail zegt: gesnoozed)"
                : genegeerd ? "WEG: eerder afgehandeld (genegeerd in de cache)"
                : "zichtbaar";
            uit.AppendLine($"[{status}]");
            uit.AppendLine($"   {van?.Name ?? van?.Address}  —  {msg.Subject}");
            uit.AppendLine($"   {msg.Date.LocalDateTime:dd-MM-yyyy HH:mm}   message-id: {msg.MessageId}");
        }
        // Staan er mails in de webinbox die IMAP niet in INBOX toont? Vraag Gmail welke
        // labels die berichten dan wél hebben — dat wijst meteen de oorzaak aan.
        uit.AppendLine();
        uit.AppendLine("Labels van de recentste berichten (uit Alle e-mail):");
        try
        {
            var alleMail = await imap.GetFolderAsync("[Gmail]/Alle e-mail", ct);
            await alleMail.OpenAsync(FolderAccess.ReadOnly, ct);
            var recentste = await alleMail.SearchAsync(
                SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-7)), ct);
            var samenvatting = await alleMail.FetchAsync(
                recentste.Reverse().Take(12).ToList(),
                MessageSummaryItems.GMailLabels | MessageSummaryItems.Envelope, ct);
            foreach (var item in samenvatting.OrderByDescending(i => i.Envelope?.Date))
            {
                var labels = item.GMailLabels is { Count: > 0 }
                    ? string.Join(", ", item.GMailLabels)
                    : "(geen)";
                uit.AppendLine($"  • {item.Envelope?.Subject}");
                uit.AppendLine($"      labels: {labels}");
            }
        }
        catch (Exception ex)
        {
            uit.AppendLine("  (labels niet op te vragen: " + ex.Message + ")");
        }

        uit.AppendLine();
        uit.AppendLine("Waar staan de recente mails volgens IMAP:");
        var top = imap.GetFolder(imap.PersonalNamespaces[0]);
        foreach (var map in await top.GetSubfoldersAsync(false, ct))
        {
            await ToonMapAsync(map, uit, ct);
            foreach (var sub in await map.GetSubfoldersAsync(false, ct))
            {
                await ToonMapAsync(sub, uit, ct);
            }
        }
        await imap.DisconnectAsync(true, ct);
        return uit.ToString();
    }

    private static async Task ToonMapAsync(
        IMailFolder map, System.Text.StringBuilder uit, CancellationToken ct)
    {
        try
        {
            if ((map.Attributes & FolderAttributes.NonExistent) != 0)
            {
                return;
            }
            await map.OpenAsync(FolderAccess.ReadOnly, ct);
            var recent = await map.SearchAsync(
                SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-14)), ct);
            if (recent.Count > 0)
            {
                uit.AppendLine($"  {map.FullName}: {recent.Count} van de laatste 14 dagen");
                foreach (var uid in recent.Reverse().Take(6))
                {
                    var m = await map.GetMessageAsync(uid, ct);
                    uit.AppendLine($"      • {m.Subject}");
                }
            }
        }
        catch
        {
            // Niet elke map is te openen (Gmail-virtuele mappen); overslaan.
        }
    }

    public static async Task<List<MailBericht>> FetchAsync(MailReplySettings s, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);

        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        // Via Gmails eigen zoektaal (X-GM-RAW): in Gmail gesnoozde mails blijven via IMAP
        // gewoon in INBOX staan, dus die expliciet uitsluiten zodat de lijst de webinbox volgt.
        var uids = await inbox.SearchAsync(
            SearchQuery.GMailRawSearch(s.AlleenOngelezen ? "-in:snoozed is:unread" : "-in:snoozed"), ct);
        var recent = uids.Skip(Math.Max(0, uids.Count - s.MaxMails)).Reverse().ToList(); // nieuwste eerst

        var berichten = new List<MailBericht>();
        foreach (var uid in recent)
        {
            var msg = await inbox.GetMessageAsync(uid, ct);
            var van = msg.From.Mailboxes.FirstOrDefault();
            var antwoordAan = msg.ReplyTo.Mailboxes.FirstOrDefault() ?? van;
            var overigeOntvangers = msg.To.Mailboxes.Concat(msg.Cc.Mailboxes)
                .Select(m => m.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a) &&
                    !a.Equals(s.Email, StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals(van?.Address, StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals(antwoordAan?.Address, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Volgorde 1-op-1 met msg.Attachments houden (index wordt gebruikt bij het downloaden).
            var bijlagen = msg.Attachments
                .Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "")
                .Select(n => string.IsNullOrWhiteSpace(n) ? "bijlage" : n)
                .ToList();
            berichten.Add(new MailBericht
            {
                Bijlagen = bijlagen,
                LinkBijlagen = ExtractLinkBijlagen(msg, van?.Address ?? ""),
                Uid = uid.Id,
                Van = string.IsNullOrWhiteSpace(van?.Name) ? van?.Address ?? "" : van!.Name,
                VanAdres = van?.Address ?? "",
                AntwoordAan = antwoordAan?.Address ?? "",
                OverigeOntvangers = overigeOntvangers,
                Cc = msg.Cc.Mailboxes
                    .Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList(),
                Onderwerp = msg.Subject ?? "(geen onderwerp)",
                Datum = msg.Date,
                Tekst = ExtractTekst(msg),
                Html = EmbedInlineAfbeeldingen(msg, msg.HtmlBody ?? ""),
                MessageId = msg.MessageId ?? "",
                Referenties = msg.References.ToList(),
                UitschrijfUrl = VindUitschrijfLink(msg),
            });
        }

        await imap.DisconnectAsync(true, ct);
        return berichten;
    }

    /// <summary>
    /// De eerdere berichten uit dezelfde Gmail-conversatie (thread), ook al zijn ze gelezen:
    /// zoekt in "Alle berichten" op de thread van de gegeven Message-ID en levert korte
    /// regels op (afzender · datum + eerste stuk tekst), oudste eerst. De mail zelf wordt
    /// overgeslagen (die staat al in beeld).
    /// </summary>
    public static async Task<List<string>> ThreadAsync(
        MailReplySettings s, string messageId, int max, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);

        var alle = imap.GetFolder(SpecialFolder.All);
        await alle.OpenAsync(FolderAccess.ReadOnly, ct);

        var regels = new List<string>();
        var eigen = await alle.SearchAsync(
            SearchQuery.GMailRawSearch($"rfc822msgid:{messageId}"), ct);
        if (eigen.Count > 0)
        {
            var samenvatting = await alle.FetchAsync(
                eigen, MessageSummaryItems.GMailThreadId, ct);
            if (samenvatting.FirstOrDefault()?.GMailThreadId is { } threadId)
            {
                var threadUids = await alle.SearchAsync(SearchQuery.GMailThreadId(threadId), ct);
                foreach (var uid in threadUids.Skip(Math.Max(0, threadUids.Count - max - 1)))
                {
                    var msg = await alle.GetMessageAsync(uid, ct);
                    if (msg.MessageId == messageId)
                    {
                        continue;
                    }
                    var van = msg.From.Mailboxes.FirstOrDefault();
                    var wie = van?.Address?.Equals(s.Email, StringComparison.OrdinalIgnoreCase) == true
                        ? "Ik"
                        : string.IsNullOrWhiteSpace(van?.Name) ? van?.Address ?? "?" : van!.Name;
                    var tekst = System.Text.RegularExpressions.Regex
                        .Replace(ExtractTekst(msg), @"\s+", " ").Trim();
                    if (tekst.Length > 500)
                    {
                        tekst = tekst[..500] + "…";
                    }
                    regels.Add($"{wie} · {msg.Date.ToLocalTime():d MMM HH:mm}\n{tekst}");
                }
            }
        }
        await imap.DisconnectAsync(true, ct);
        return regels.TakeLast(max).ToList();
    }

    /// <summary>
    /// Vervangt cid:-verwijzingen in de HTML door ingebedde data-URL's van de bijbehorende
    /// inline afbeeldingen, zodat mails met ingesloten beelden volledig renderen.
    /// </summary>
    private static string EmbedInlineAfbeeldingen(MimeMessage msg, string html)
    {
        if (html.Length == 0 || !html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }
        try
        {
            foreach (var deel in msg.BodyParts.OfType<MimePart>())
            {
                var cid = deel.ContentId?.Trim('<', '>') ?? "";
                if (cid.Length == 0 ||
                    !deel.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                    !html.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using var buffer = new MemoryStream();
                deel.Content.DecodeTo(buffer);
                if (buffer.Length > 3_000_000)
                {
                    continue; // extreem grote afbeelding: laten staan (cache klein houden)
                }
                html = html.Replace("cid:" + cid,
                    $"data:{deel.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Best effort: dan zonder ingesloten afbeeldingen.
        }
        return html;
    }

    /// <summary>
    /// Recente correspondentie met één adres (beide richtingen) uit "Alle berichten", als
    /// korte regels (oudste eerst) — context voor betere Claude-concepten.
    /// </summary>
    public static async Task<List<string>> CorrespondentieAsync(
        MailReplySettings s, string adres, int maanden, int max, CancellationToken ct)
    {
        if (adres.Length == 0)
        {
            return new List<string>();
        }
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var alle = imap.GetFolder(SpecialFolder.All);
        await alle.OpenAsync(FolderAccess.ReadOnly, ct);

        var sinds = DateTime.Now.AddMonths(-maanden);
        var uids = await alle.SearchAsync(SearchQuery.GMailRawSearch(
            $"(from:{adres} OR to:{adres}) after:{sinds:yyyy/MM/dd}"), ct);

        var regels = new List<string>();
        foreach (var uid in uids.Skip(Math.Max(0, uids.Count - max)))
        {
            var msg = await alle.GetMessageAsync(uid, ct);
            var van = msg.From.Mailboxes.FirstOrDefault();
            var wie = van?.Address?.Equals(s.Email, StringComparison.OrdinalIgnoreCase) == true
                ? "Ik"
                : string.IsNullOrWhiteSpace(van?.Name) ? van?.Address ?? "?" : van!.Name;
            var tekst = System.Text.RegularExpressions.Regex
                .Replace(ExtractTekst(msg), @"\s+", " ").Trim();
            if (tekst.Length > 400)
            {
                tekst = tekst[..400] + "…";
            }
            regels.Add($"{wie} · {msg.Date.ToLocalTime():d MMM} · {msg.Subject}\n{tekst}");
        }
        await imap.DisconnectAsync(true, ct);
        return regels;
    }

    /// <summary>
    /// Recente correspondentie met meerdere adressen tegelijk, in één IMAP-sessie: handig om
    /// vóór een vergadering de context met alle deelnemers op te halen. Levert korte regels
    /// (oudste eerst), net als <see cref="CorrespondentieAsync"/>.
    /// </summary>
    public static async Task<List<string>> CorrespondentieMetAsync(
        MailReplySettings s, IReadOnlyList<string> adressen, int maanden, int max, CancellationToken ct)
    {
        var doelen = adressen
            .Where(a => a.Contains('@'))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => !a.Equals(s.Email, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (doelen.Count == 0)
        {
            return new List<string>();
        }

        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var alle = imap.GetFolder(SpecialFolder.All);
        await alle.OpenAsync(FolderAccess.ReadOnly, ct);

        var sinds = DateTime.Now.AddMonths(-maanden);
        var wie = string.Join(" OR ", doelen.Select(a => $"from:{a} OR to:{a}"));
        var uids = await alle.SearchAsync(
            SearchQuery.GMailRawSearch($"({wie}) after:{sinds:yyyy/MM/dd}"), ct);

        var regels = new List<string>();
        foreach (var uid in uids.Skip(Math.Max(0, uids.Count - max)))
        {
            var msg = await alle.GetMessageAsync(uid, ct);
            var van = msg.From.Mailboxes.FirstOrDefault();
            var afzender = van?.Address?.Equals(s.Email, StringComparison.OrdinalIgnoreCase) == true
                ? "Ik"
                : string.IsNullOrWhiteSpace(van?.Name) ? van?.Address ?? "?" : van!.Name;
            var tekst = Regex.Replace(ExtractTekst(msg), @"\s+", " ").Trim();
            if (tekst.Length > 400)
            {
                tekst = tekst[..400] + "…";
            }
            regels.Add($"{afzender} · {msg.Date.ToLocalTime():d MMM} · {msg.Subject}\n{tekst}");
        }
        await imap.DisconnectAsync(true, ct);
        return regels;
    }

    /// <summary>Een verstuurde mail waarop de tegenpartij (nog) niet geantwoord heeft.</summary>
    public sealed class OnbeantwoordeMail
    {
        public string ThreadId = "";
        public string MessageId = "";
        public string Onderwerp = "";
        public DateTimeOffset Verstuurd;

        /// <summary>Ontvangers als "Naam &lt;adres&gt;" (aan + cc, zonder mezelf).</summary>
        public List<string> Ontvangers = new();

        /// <summary>Mijn laatste bericht in de thread, als platte tekst (ingekort).</summary>
        public string Tekst = "";

        /// <summary>Hoeveel berichten er in totaal in de conversatie zitten.</summary>
        public int BerichtenInThread;
    }

    /// <summary>
    /// Zoekt conversaties waarin ik het laatste woord had en waarop al minstens
    /// <paramref name="minimumDagen"/> dagen niet geantwoord is. Werkt in één IMAP-sessie:
    /// eerst de envelopes van alles uit de laatste <paramref name="maxDagen"/> dagen ophalen
    /// en lokaal per conversatie groeperen, daarna alleen van de kandidaten de tekst lezen.
    /// Automatische afzenders (no-reply, mailinglijsten) worden overgeslagen — daar heeft
    /// nabellen geen zin.
    /// </summary>
    public static async Task<List<OnbeantwoordeMail>> WachtOpAntwoordAsync(
        MailReplySettings s, int minimumDagen, int maxDagen, int max, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var alle = imap.GetFolder(SpecialFolder.All);
        await alle.OpenAsync(FolderAccess.ReadOnly, ct);

        var sinds = DateTime.Now.Date.AddDays(-maxDagen);
        var uids = await alle.SearchAsync(SearchQuery.GMailRawSearch(
            $"after:{sinds:yyyy/MM/dd} -in:chats -in:spam -in:trash"), ct);
        var samenvattingen = uids.Count == 0
            ? new List<IMessageSummary>()
            : (await alle.FetchAsync(uids,
                MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId |
                MessageSummaryItems.GMailThreadId, ct)).ToList();

        var kandidaten = new List<(IMessageSummary Laatste, int Aantal)>();
        foreach (var groep in samenvattingen
                     .Where(m => m.Envelope is not null && m.GMailThreadId is not null)
                     .GroupBy(m => m.GMailThreadId))
        {
            var geordend = groep.OrderBy(m => m.Envelope.Date ?? DateTimeOffset.MinValue).ToList();
            var laatste = geordend[^1];
            var verstuurd = laatste.Envelope.Date ?? DateTimeOffset.MinValue;

            if (!VanMij(laatste.Envelope, s.Email) ||
                verstuurd > DateTimeOffset.Now.AddDays(-minimumDagen))
            {
                continue; // zij waren als laatste aan zet, of het is nog te vroeg om te porren
            }
            // Alleen threads waarin echt iemand anders zit, en geen automatische afzenders.
            var anderen = Anderen(laatste.Envelope, s.Email);
            if (anderen.Count == 0 || anderen.Any(a => IsAutomatisch(Adres(a))))
            {
                continue;
            }
            kandidaten.Add((laatste, geordend.Count));
        }

        var resultaat = new List<OnbeantwoordeMail>();
        foreach (var (laatste, aantal) in kandidaten.OrderBy(k => k.Laatste.Envelope.Date).Take(max))
        {
            var tekst = "";
            try
            {
                var msg = await alle.GetMessageAsync(laatste.UniqueId, ct);
                tekst = Regex.Replace(ExtractTekst(msg), @"\s+", " ").Trim();
                if (tekst.Length > 1200)
                {
                    tekst = tekst[..1200] + "…";
                }
            }
            catch
            {
                // Bericht niet meer op te halen: de rest van de gegevens volstaat.
            }
            resultaat.Add(new OnbeantwoordeMail
            {
                ThreadId = laatste.GMailThreadId?.ToString() ?? "",
                MessageId = laatste.Envelope.MessageId ?? "",
                Onderwerp = laatste.Envelope.Subject ?? "(geen onderwerp)",
                Verstuurd = (laatste.Envelope.Date ?? DateTimeOffset.MinValue).ToLocalTime(),
                Ontvangers = Anderen(laatste.Envelope, s.Email),
                BerichtenInThread = aantal,
                Tekst = tekst,
            });
        }

        await imap.DisconnectAsync(true, ct);
        return resultaat;
    }

    /// <summary>Een binnengekomen mail waarop ík nog niet geantwoord heb.</summary>
    public sealed class WachtOpMij
    {
        public string ThreadId = "";
        public string Onderwerp = "";
        public string Van = "";        // "Naam <adres>"
        public string VanAdres = "";
        public DateTimeOffset Ontvangen;
        public string Tekst = "";
        public bool BevatVraag;

        public int DagenStil => Math.Max(0, (int)(DateTimeOffset.Now - Ontvangen).TotalDays);
    }

    /// <summary>
    /// De spiegel van <see cref="WachtOpAntwoordAsync"/>: conversaties waarin de ánder het
    /// laatste woord had en waar sindsdien minstens <paramref name="minimumDagen"/> dagen
    /// niets van mij terugkwam. Dat zijn de vragen die blijven liggen. Automatische afzenders
    /// en mails van mezelf vallen af; per conversatie wordt gekeken of er een echte vraag in
    /// staat (vraagteken of een vraagwoord), zodat beleefde bevestigingen niet meetellen.
    /// </summary>
    public static async Task<List<WachtOpMij>> WachtOpMijAsync(
        MailReplySettings s, int minimumDagen, int maxDagen, int max, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var alle = imap.GetFolder(SpecialFolder.All);
        await alle.OpenAsync(FolderAccess.ReadOnly, ct);

        var sinds = DateTime.Now.Date.AddDays(-maxDagen);
        var uids = await alle.SearchAsync(SearchQuery.GMailRawSearch(
            $"after:{sinds:yyyy/MM/dd} -in:chats -in:spam -in:trash"), ct);
        var samenvattingen = uids.Count == 0
            ? new List<IMessageSummary>()
            : (await alle.FetchAsync(uids,
                MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId |
                MessageSummaryItems.GMailThreadId, ct)).ToList();

        var kandidaten = new List<IMessageSummary>();
        foreach (var groep in samenvattingen
                     .Where(m => m.Envelope is not null && m.GMailThreadId is not null)
                     .GroupBy(m => m.GMailThreadId))
        {
            var laatste = groep.OrderBy(m => m.Envelope.Date ?? DateTimeOffset.MinValue).Last();
            var ontvangen = laatste.Envelope.Date ?? DateTimeOffset.MinValue;
            if (VanMij(laatste.Envelope, s.Email) ||
                ontvangen > DateTimeOffset.Now.AddDays(-minimumDagen))
            {
                continue; // ik was als laatste aan zet, of het is nog vers
            }
            var afzender = laatste.Envelope.From.Mailboxes.FirstOrDefault();
            if (afzender is null || IsAutomatisch(afzender.Address ?? ""))
            {
                continue;
            }
            kandidaten.Add(laatste);
        }

        var resultaat = new List<WachtOpMij>();
        foreach (var laatste in kandidaten.OrderBy(k => k.Envelope.Date).Take(max))
        {
            var tekst = "";
            try
            {
                var msg = await alle.GetMessageAsync(laatste.UniqueId, ct);
                tekst = Regex.Replace(ExtractTekst(msg), @"\s+", " ").Trim();
                if (tekst.Length > 1200)
                {
                    tekst = tekst[..1200] + "…";
                }
            }
            catch
            {
                // Zonder tekst blijft de rest bruikbaar.
            }
            var afzender = laatste.Envelope.From.Mailboxes.First();
            var onderwerp = laatste.Envelope.Subject ?? "(geen onderwerp)";
            resultaat.Add(new WachtOpMij
            {
                ThreadId = laatste.GMailThreadId?.ToString() ?? "",
                Onderwerp = onderwerp,
                Van = string.IsNullOrWhiteSpace(afzender.Name)
                    ? afzender.Address : $"{afzender.Name} <{afzender.Address}>",
                VanAdres = afzender.Address ?? "",
                Ontvangen = (laatste.Envelope.Date ?? DateTimeOffset.MinValue).ToLocalTime(),
                Tekst = tekst,
                BevatVraag = tekst.Contains('?') || onderwerp.Contains('?') ||
                    Regex.IsMatch(tekst,
                        @"\b(kan (je|jij|u)|kun je|zou (je|u)|graag|aub|a\.u\.b\.|wanneer|hoe|waarom|" +
                        @"kunnen jullie|laat (je|u) (het )?weten|hoor ik|verneem)\b",
                        RegexOptions.IgnoreCase),
            });
        }

        await imap.DisconnectAsync(true, ct);
        return resultaat;
    }

    private static bool VanMij(Envelope envelope, string eigenAdres) =>
        envelope.From.Mailboxes.Any(m =>
            m.Address.Equals(eigenAdres, StringComparison.OrdinalIgnoreCase));

    /// <summary>De ontvangers van een bericht behalve ikzelf, als "Naam &lt;adres&gt;".</summary>
    private static List<string> Anderen(Envelope envelope, string eigenAdres) =>
        envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes)
            .Where(m => !string.IsNullOrWhiteSpace(m.Address) &&
                        !m.Address.Equals(eigenAdres, StringComparison.OrdinalIgnoreCase))
            .Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Adres(string ontvanger)
    {
        var start = ontvanger.IndexOf('<');
        var einde = ontvanger.IndexOf('>');
        return start >= 0 && einde > start ? ontvanger[(start + 1)..einde] : ontvanger;
    }

    /// <summary>Adressen waar een herinnering zinloos is (robots, lijsten, ticketsystemen).</summary>
    private static bool IsAutomatisch(string adres)
    {
        string[] stukken =
        {
            "no-reply", "noreply", "no_reply", "donotreply", "do-not-reply", "notifications@",
            "notification@", "mailer-daemon", "postmaster@", "bounce", "newsletter", "@calendar.",
            "automated", "support@atlassian", "jira@", "github.com",
        };
        return stukken.Any(t => adres.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verstuurt de antwoorden in één SMTP-sessie (als reply in de juiste thread) en markeert de
    /// originele mails daarna via IMAP als beantwoord. Fouten per mail komen in het log; de rest
    /// gaat gewoon door. Retourneert de succesvol verstuurde berichten.
    /// </summary>
    public static async Task<List<MailBericht>> VerstuurAsync(
        MailReplySettings s, IReadOnlyList<MailBericht> mails, Action<string> log, CancellationToken ct)
    {
        var verstuurd = new List<MailBericht>();

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort, SecureSocketOptions.SslOnConnect, ct);
            await smtp.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);

            foreach (var mail in mails)
            {
                try
                {
                    await smtp.SendAsync(BouwAntwoord(s, mail), ct);
                    verstuurd.Add(mail);
                    var cc = mail.AlleBeantwoorden && mail.OverigeOntvangers.Count > 0
                        ? $" (+{mail.OverigeOntvangers.Count} in cc)"
                        : "";
                    log($"Verstuurd: antwoord aan {mail.Van}{cc} – \"{mail.Onderwerp}\"");
                }
                catch (Exception ex)
                {
                    log($"Versturen aan {mail.Van} mislukt: {ex.Message}");
                }
            }

            await smtp.DisconnectAsync(true, ct);
        }

        // Alleen mails met een bekende IMAP-uid markeren (antwoorden vanuit een taak hebben
        // die niet meer; de mail zelf is dan mogelijk al gearchiveerd).
        var teMarkeren = verstuurd.Where(m => m.Uid > 0).ToList();
        if (teMarkeren.Count > 0)
        {
            try
            {
                using var imap = new ImapClient();
                await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
                await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
                await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
                await imap.Inbox.AddFlagsAsync(
                    teMarkeren.Select(m => new UniqueId(m.Uid)).ToList(),
                    MessageFlags.Answered | MessageFlags.Seen, silent: true, ct);
                await imap.DisconnectAsync(true, ct);
            }
            catch (Exception ex)
            {
                log($"Markeren als beantwoord in Gmail mislukt (antwoorden zijn wél verstuurd): {ex.Message}");
            }
        }

        return verstuurd;
    }

    /// <summary>
    /// De mails die Maarten op één dag zelf verstuurde (map Verzonden), als signaalregels
    /// voor het dagvoorstel: tijdstip, ontvanger, onderwerp en de omvang van de eigen tekst
    /// (het geciteerde deel onder "Op … schreef" telt niet mee). Het dagvoorstel rekent
    /// per verzonden mail minstens een kwartier werktijd.
    /// </summary>
    public static async Task<List<string>> VerzondenVanDagAsync(
        MailReplySettings s, DateOnly dag, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        var map = imap.GetFolder(SpecialFolder.Sent);
        await map.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await map.SearchAsync(SearchQuery.GMailRawSearch(
            $"after:{dag:yyyy/MM/dd} before:{dag.AddDays(1):yyyy/MM/dd}"), ct);
        var regels = new List<string>();
        foreach (var uid in uids.Take(40))
        {
            try
            {
                var msg = await map.GetMessageAsync(uid, ct);
                var aan = string.Join(", ", msg.To.Mailboxes
                    .Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name)
                    .Take(3));
                regels.Add($"{msg.Date.ToLocalTime():HH:mm} aan {aan} — " +
                    $"\"{msg.Subject}\" (±{EigenWoorden(ExtractTekst(msg))} woorden)");
            }
            catch
            {
                // Eén onleesbare mail mag het signaal niet breken.
            }
        }
        await imap.DisconnectAsync(true, ct);
        return regels.OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>Woorden in de eigen tekst: citaten (">"-regels) en alles onder de citatiekop vallen af.</summary>
    private static int EigenWoorden(string tekst)
    {
        var eigen = new List<string>();
        foreach (var lijn in tekst.Split('\n'))
        {
            if (lijn.TrimStart().StartsWith('>'))
            {
                continue;
            }
            if (Regex.IsMatch(lijn,
                @"^\s*(Op .+ schreef|On .+ wrote:|-{2,}\s*(Original|Oorspronkelijk|Forwarded|Doorgestuurd))"))
            {
                break;
            }
            eigen.Add(lijn);
        }
        return string.Join(" ", eigen)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    /// <summary>
    /// Downloadt de gekozen bijlagen van een mail naar de opgegeven map, onder de door de
    /// gebruiker gekozen bestandsnamen (bestaande namen krijgen een volgnummer). De index
    /// verwijst naar de volgorde in <see cref="MailBericht.Bijlagen"/>. Retourneert de
    /// opgeslagen bestandspaden.
    /// </summary>
    public static async Task<List<string>> DownloadBijlagenAsync(
        MailReplySettings s, MailBericht mail, string doelmap,
        IReadOnlyList<(int Index, string Naam)> selectie, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var msg = await imap.Inbox.GetMessageAsync(new UniqueId(mail.Uid), ct);
        var bijlagen = msg.Attachments.ToList();
        var paden = new List<string>();
        foreach (var (index, gekozenNaam) in selectie)
        {
            if (index < 0 || index >= bijlagen.Count)
            {
                continue;
            }
            var naam = string.Concat(gekozenNaam.Split(Path.GetInvalidFileNameChars())).Trim();
            var pad = UniekPad(doelmap, naam.Length > 0 ? naam : "bijlage");

            await using var stream = File.Create(pad);
            if (bijlagen[index] is MimePart part)
            {
                await part.Content.DecodeToAsync(stream, ct);
            }
            else if (bijlagen[index] is MessagePart bijgevoegdeMail)
            {
                await bijgevoegdeMail.Message.WriteToAsync(stream, ct);
            }
            paden.Add(pad);
        }

        await imap.DisconnectAsync(true, ct);
        return paden;
    }

    private static readonly HttpClient Http = new();

    /// <summary>
    /// Stuurt de gekozen bijlagen van een mail door (bv. naar Billit): de aangevinkte echte
    /// bijlagen plus de aangevinkte linkbijlagen (Stripe-facturen e.d.), onder de gekozen
    /// namen. Retourneert het aantal doorgestuurde bijlagen.
    /// </summary>
    public static async Task<int> DoorsturenAsync(
        MailReplySettings s, MailBericht mail, string naarAdres,
        IReadOnlyList<(int Index, string Naam)> selectie,
        IReadOnlyList<(string Url, string Naam)> linkSelectie, CancellationToken ct)
    {
        MimeMessage origineel;
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            origineel = await imap.Inbox.GetMessageAsync(new UniqueId(mail.Uid), ct);
            await imap.DisconnectAsync(true, ct);
        }

        var builder = new BodyBuilder
        {
            TextBody =
                $"""
                ---------- Doorgestuurde mail ----------
                Van: {mail.Van} <{mail.VanAdres}>
                Datum: {mail.Datum:yyyy-MM-dd HH:mm}
                Onderwerp: {mail.Onderwerp}

                {mail.Tekst}
                """,
        };
        var bijlagen = origineel.Attachments.ToList();
        var aantal = 0;
        foreach (var (index, naam) in selectie)
        {
            if (index < 0 || index >= bijlagen.Count)
            {
                continue;
            }
            using var buffer = new MemoryStream();
            if (bijlagen[index] is MimePart part)
            {
                await part.Content.DecodeToAsync(buffer, ct);
            }
            else if (bijlagen[index] is MessagePart bijgevoegdeMail)
            {
                await bijgevoegdeMail.Message.WriteToAsync(buffer, ct);
            }
            builder.Attachments.Add(naam, buffer.ToArray());
            aantal++;
        }
        foreach (var (url, naam) in linkSelectie)
        {
            try
            {
                builder.Attachments.Add(naam, await Http.GetByteArrayAsync(url, ct));
                aantal++;
            }
            catch
            {
                // Linkdownload mislukt (bv. verlopen link): de rest gewoon doorsturen.
            }
        }
        if (aantal == 0)
        {
            throw new InvalidOperationException("Geen van de gekozen bijlagen kon worden toegevoegd.");
        }

        var bericht = new MimeMessage();
        bericht.From.Add(MailboxAddress.Parse(s.Email));
        bericht.To.Add(MailboxAddress.Parse(naarAdres));
        bericht.Subject = "Fwd: " + mail.Onderwerp;
        bericht.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort, SecureSocketOptions.SslOnConnect, ct);
        await smtp.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        await smtp.SendAsync(bericht, ct);
        await smtp.DisconnectAsync(true, ct);
        return aantal;
    }

    /// <summary>Downloadt een linkbijlage (bv. Stripe-factuur) naar de opgegeven map.</summary>
    public static async Task<string> DownloadLinkAsync(
        string url, string doelmap, string naam, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct);
        naam = string.Concat(naam.Split(Path.GetInvalidFileNameChars())).Trim();
        var pad = UniekPad(doelmap, naam.Length > 0 ? naam : "download.pdf");
        await File.WriteAllBytesAsync(pad, bytes, ct);
        return pad;
    }

    /// <summary>
    /// Zoekt in de mailtekst naar links die naar een downloadbare PDF wijzen
    /// (zoals Stripe-facturen: ".../pdf?s=em") en biedt die aan als bijlage.
    /// </summary>
    private static List<LinkBijlage> ExtractLinkBijlagen(MimeMessage msg, string vanAdres)
    {
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(msg.HtmlBody))
        {
            urls.AddRange(Regex.Matches(msg.HtmlBody, "href=\"([^\"]+)\"", RegexOptions.IgnoreCase)
                .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)));
        }
        if (!string.IsNullOrEmpty(msg.TextBody))
        {
            urls.AddRange(Regex.Matches(msg.TextBody, @"https?://[^\s<>()\[\]""]+")
                .Select(m => m.Value.TrimEnd('.', ',', ')')));
        }

        var domein = vanAdres.Split('@').ElementAtOrDefault(1)?.Split('.') is { Length: >= 2 } delen
            ? delen[^2]
            : "";
        var resultaat = new List<LinkBijlage>();
        foreach (var url in urls.Distinct())
        {
            var lower = url.ToLowerInvariant();
            if (!lower.StartsWith("http") ||
                (!lower.EndsWith(".pdf") && !lower.Contains(".pdf?") && !lower.Contains("/pdf")))
            {
                continue;
            }
            var soort = lower.Contains("invoice") || lower.Contains("factuur") ? "invoice"
                : lower.Contains("receipt") ? "receipt"
                : "download";
            resultaat.Add(new LinkBijlage
            {
                Naam = (domein.Length > 0 ? $"{soort} {domein}.pdf" : $"{soort}.pdf"),
                Url = url,
            });
        }
        return resultaat.Take(5).ToList(); // niet elke mail vol marketinglinks laten uitdijen
    }

    /// <summary>
    /// Zoekt een afmeldlink: eerst de List-Unsubscribe-header (de nette weg), anders een
    /// link in de HTML met "unsubscribe/uitschrijven/afmelden" in de tekst of het adres.
    /// </summary>
    private static string VindUitschrijfLink(MimeMessage msg)
    {
        var header = msg.Headers["List-Unsubscribe"];
        if (!string.IsNullOrEmpty(header))
        {
            var match = Regex.Match(header, @"<(https?://[^>]+)>");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        if (!string.IsNullOrEmpty(msg.HtmlBody))
        {
            var opTekst = Regex.Match(msg.HtmlBody,
                "<a[^>]+href=\"([^\"]+)\"[^>]*>(?:(?!</a>).){0,120}?(unsubscribe|uitschrijv|afmeld|désabonn|désinscri)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (opTekst.Success)
            {
                return WebUtility.HtmlDecode(opTekst.Groups[1].Value);
            }
            var opAdres = Regex.Match(msg.HtmlBody,
                "href=\"([^\"]*(?:unsubscribe|optout|opt-out|afmeld)[^\"]*)\"", RegexOptions.IgnoreCase);
            if (opAdres.Success)
            {
                return WebUtility.HtmlDecode(opAdres.Groups[1].Value);
            }
        }
        return "";
    }

    private static string UniekPad(string map, string naam)
    {
        var pad = Path.Combine(map, naam);
        var basis = Path.GetFileNameWithoutExtension(naam);
        var extensie = Path.GetExtension(naam);
        for (var i = 2; File.Exists(pad); i++)
        {
            pad = Path.Combine(map, $"{basis} ({i}){extensie}");
        }
        return pad;
    }

    private const string SnoozeLabel = "Gesnoozed";

    /// <summary>
    /// Snoozet mails Gmail-zichtbaar: label "Gesnoozed" erop (zichtbaar in de Gmail-zijbalk)
    /// en uit de inbox halen. Het terugzetten gebeurt door <see cref="TerugNaarInboxAsync"/>.
    /// </summary>
    public static async Task SnoozeArchiveerAsync(
        MailReplySettings s, IReadOnlyList<MailBericht> mails, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);

        var map = await SnoozeMapAsync(imap, aanmaken: true, ct)
            ?? throw new InvalidOperationException($"Kon het Gmail-label \"{SnoozeLabel}\" niet aanmaken.");
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        var uids = mails.Select(m => new UniqueId(m.Uid)).ToList();
        await imap.Inbox.CopyToAsync(uids, map, ct); // label toevoegen
        await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Deleted, silent: true, ct);
        await imap.Inbox.ExpungeAsync(uids, ct); // uit de inbox (mail blijft onder het label)
        await imap.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Zet een gesnoozde mail terug in de inbox: eerst zoeken onder het label "Gesnoozed"
    /// (label eraf + naar INBOX), anders terugvallen op "Alle berichten" (oude snoozes).
    /// Retourneert false als de mail niet meer teruggevonden wordt.
    /// </summary>
    public static async Task<bool> TerugNaarInboxAsync(
        MailReplySettings s, string messageId, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);

        var zoek = SearchQuery.HeaderContains("Message-Id", messageId);
        var gevonden = false;

        if (await SnoozeMapAsync(imap, aanmaken: false, ct) is { } map)
        {
            await map.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await map.SearchAsync(zoek, ct);
            if (uids.Count > 0)
            {
                await map.CopyToAsync(uids, imap.Inbox, ct);
                await map.AddFlagsAsync(uids, MessageFlags.Deleted, silent: true, ct);
                await map.ExpungeAsync(uids, ct); // snoozelabel weer weghalen
                gevonden = true;
            }
        }

        if (!gevonden && imap.GetFolder(SpecialFolder.All) is { } alleBerichten)
        {
            await alleBerichten.OpenAsync(FolderAccess.ReadOnly, ct);
            var uids = await alleBerichten.SearchAsync(zoek, ct);
            if (uids.Count > 0)
            {
                await alleBerichten.CopyToAsync(uids, imap.Inbox, ct);
                gevonden = true;
            }
        }

        await imap.DisconnectAsync(true, ct);
        return gevonden;
    }

    /// <summary>De IMAP-map van het snoozelabel; optioneel aanmaken als die nog niet bestaat.</summary>
    private static async Task<IMailFolder?> SnoozeMapAsync(
        ImapClient imap, bool aanmaken, CancellationToken ct)
    {
        var top = imap.GetFolder(imap.PersonalNamespaces[0]);
        try
        {
            return await top.GetSubfolderAsync(SnoozeLabel, ct);
        }
        catch (FolderNotFoundException)
        {
            return aanmaken ? await top.CreateAsync(SnoozeLabel, isMessageFolder: true, ct) : null;
        }
    }

    /// <summary>
    /// Archiveert mails zoals in Gmail: ze verdwijnen uit de inbox maar blijven bewaard
    /// onder "Alle berichten" (bij Gmail-IMAP is verwijderen uit INBOX = label weghalen).
    /// Archiveren betekent altijd ook gelezen: anders blijft de ongelezen-teller op de
    /// telefoon oplopen voor mail die hier al afgehandeld is.
    /// </summary>
    public static async Task ArchiveerAsync(
        MailReplySettings s, IReadOnlyList<MailBericht> mails, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(s.Email, s.AppWachtwoord, ct);
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        var uids = mails.Select(m => new UniqueId(m.Uid)).ToList();
        await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Seen, silent: true, ct);
        await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Deleted, silent: true, ct);
        await imap.Inbox.ExpungeAsync(uids, ct);
        await imap.DisconnectAsync(true, ct);
    }

    private static MimeMessage BouwAntwoord(MailReplySettings s, MailBericht mail)
    {
        var reply = new MimeMessage();
        reply.From.Add(MailboxAddress.Parse(s.Email));
        reply.To.Add(new MailboxAddress(mail.Van, mail.AntwoordAan));
        if (mail.AlleBeantwoorden)
        {
            foreach (var adres in mail.OverigeOntvangers)
            {
                if (MailboxAddress.TryParse(adres, out var cc))
                {
                    reply.Cc.Add(cc);
                }
            }
        }
        reply.Subject = mail.Onderwerp.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? mail.Onderwerp
            : "Re: " + mail.Onderwerp;

        if (!string.IsNullOrEmpty(mail.MessageId))
        {
            reply.InReplyTo = mail.MessageId;
            foreach (var referentie in mail.Referenties)
            {
                reply.References.Add(referentie);
            }
            reply.References.Add(mail.MessageId);
        }

        reply.Body = new TextPart("plain") { Text = mail.Concept };
        return reply;
    }

    private static string ExtractTekst(MimeMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.TextBody))
        {
            return msg.TextBody.Trim();
        }
        return string.IsNullOrWhiteSpace(msg.HtmlBody) ? "" : StripHtml(msg.HtmlBody);
    }

    private static string StripHtml(string html)
    {
        var tekst = Regex.Replace(html, @"<(script|style)\b[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        tekst = Regex.Replace(tekst, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        tekst = Regex.Replace(tekst, "<[^>]+>", "");
        tekst = WebUtility.HtmlDecode(tekst);
        tekst = Regex.Replace(tekst, @"[ \t]+", " ");
        tekst = Regex.Replace(tekst, @"\n{3,}", "\n\n");
        return tekst.Trim();
    }
}
