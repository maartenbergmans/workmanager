using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Google Chat via de officiële Chat API met gebruikers-OAuth: spaces en recente berichten
/// uitlezen en antwoorden versturen. De koppeling loopt via de standaard loopback-flow
/// (browser opent, code komt terug op 127.0.0.1); tokens staan versleuteld in de instellingen.
/// </summary>
public static class GoogleChatClient
{
    private static readonly HttpClient Http = new();

    private static readonly string[] Scopes =
    {
        "openid",
        "https://www.googleapis.com/auth/chat.spaces.readonly",
        "https://www.googleapis.com/auth/chat.messages",
        "https://www.googleapis.com/auth/chat.memberships.readonly", // DM-leden opzoeken (Chat Jan-knop)
        // Gelezen-status per gesprek: een chat die in Google Chat zelf al gelezen is, hoeft
        // niet telkens terug in de cockpit (zelfde model als Teams/WhatsApp).
        "https://www.googleapis.com/auth/chat.users.readstate.readonly",
        "https://www.googleapis.com/auth/contacts.readonly",
        "https://www.googleapis.com/auth/directory.readonly",
        "https://www.googleapis.com/auth/calendar.events", // o.a. AH-levermoment in de agenda
        // Volledige Drive-scope, en niet drive.file: bijlagen gaan naar mappen die al bestaan
        // (de boekhoudmappen), en drive.file geeft alleen toegang tot wat de app zelf aanmaakte.
        "https://www.googleapis.com/auth/drive",
    };

    // Access-token in-memory hergebruiken tot vlak voor het verloopt.
    private static string _accessToken = "";
    private static DateTimeOffset _accessTokenGeldigTot = DateTimeOffset.MinValue;

    // ---------- Koppelen (OAuth loopback-flow) ----------

    /// <summary>
    /// Voert de volledige OAuth-koppeling uit: opent de browser voor toestemming, vangt de
    /// code op via een tijdelijke luisteraar op 127.0.0.1 en bewaart het refresh-token
    /// (en het eigen account-id) in de instellingen.
    /// </summary>
    public static async Task KoppelAsync(GoogleChatSettings s, CancellationToken ct)
    {
        using var listener = new HttpListener();
        var port = VrijePoort();
        var redirect = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(redirect);
        listener.Start();

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(s.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString(string.Join(" ", Scopes))}" +
            "&access_type=offline&prompt=consent";
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var contextTask = listener.GetContextAsync();
        var context = await contextTask.WaitAsync(timeout.Token);

        var code = context.Request.QueryString["code"] ?? "";
        var fout = context.Request.QueryString["error"] ?? "";
        var antwoord = Encoding.UTF8.GetBytes(code.Length > 0
            ? "<html><body style=\"font-family:Segoe UI\">Google Chat is gekoppeld — je kunt dit venster sluiten.</body></html>"
            : $"<html><body style=\"font-family:Segoe UI\">Koppelen mislukt: {WebUtility.HtmlEncode(fout)}</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(antwoord, ct);
        context.Response.Close();
        listener.Stop();

        if (code.Length == 0)
        {
            throw new InvalidOperationException($"Google gaf geen code terug ({fout}).");
        }

        using var doc = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = s.ClientId,
            ["client_secret"] = s.ClientSecret,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }, ct);
        var root = doc.RootElement;
        s.RefreshToken = root.GetProperty("refresh_token").GetString() ?? "";
        _accessToken = root.GetProperty("access_token").GetString() ?? "";
        _accessTokenGeldigTot = DateTimeOffset.Now.AddSeconds(
            root.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 60 : 3000);

        // Eigen account-id uit het id-token (JWT-payload, veld "sub") — nodig om eigen
        // berichten in gesprekken te herkennen.
        if (root.TryGetProperty("id_token", out var idToken) &&
            idToken.GetString()?.Split('.') is { Length: 3 } delen)
        {
            var payload = delen[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload));
            s.MijnUserId = jwt.RootElement.TryGetProperty("sub", out var sub)
                ? sub.GetString() ?? ""
                : "";
        }
        s.Save();
    }

    private static int VrijePoort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    // ---------- Berichten ophalen ----------

    /// <summary>
    /// Haalt per space (gesprek of ruimte) de berichten van de laatste dagen op en geeft
    /// ze terug als lijstitems: één item per gesprek, met het hele recente verloop als
    /// tekst. Spaces zonder recente berichten worden overgeslagen.
    /// </summary>
    public static async Task<List<MailBericht>> FetchAsync(GoogleChatSettings s, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        var sinds = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, s.DagenTerug));
        var resultaat = new List<MailBericht>();

        // De spaces om de beurt ophalen was de traagste stap van de hele cockpit-ophaalbeurt
        // (elke space een eigen HTTP-rondje). Ze zijn onafhankelijk, dus tegelijk — met een
        // rem van acht, zodat we de Chat-API niet met tientallen verzoeken tegelijk bestoken.
        var spaces = await SpacesAsync(token, ct);
        using var rem = new SemaphoreSlim(8);
        var perSpace = await Task.WhenAll(spaces.Select(async space =>
        {
            await rem.WaitAsync(ct);
            try
            {
                return (Space: space, Berichten: await BerichtenAsync(token, space.Name, sinds, ct));
            }
            catch
            {
                // Eén onbereikbare space mag de rest niet meeslepen.
                return (Space: space, Berichten: new List<ChatMsg>());
            }
            finally
            {
                rem.Release();
            }
        }));

        foreach (var (space, berichten) in perSpace)
        {
            if (berichten.Count == 0)
            {
                continue;
            }

            foreach (var b in berichten)
            {
                b.SenderNaam = await NaamAsync(s, token, b.SenderId, ct);
            }
            var laatste = berichten[^1];
            // Heb jij zelf het laatste woord gehad, dan valt er niets te beantwoorden en
            // hoort de chat niet in de lijst. (Blijft het antwoord uit, dan pikt de
            // "wacht op antwoord"-radar dat op — dat is een andere vraag dan "actie nodig".)
            if (laatste.SenderId == s.MijnUserId)
            {
                continue;
            }
            // Al gelezen in Google Chat zelf? Dan hoeft de cockpit hem niet (opnieuw) te
            // tonen — anders komt een druk gesprek (Jan) na elk bericht terug, ook al is
            // het daar al bekeken. Oudere koppelingen zonder readstate-scope vallen terug
            // op de oude regel (alles tonen waar de ander het laatste woord had).
            if (await GelezenAsync(token, space.Name, laatste.Tijd, ct))
            {
                continue;
            }
            var partner = berichten.LastOrDefault(b => b.SenderId != s.MijnUserId) ?? laatste;
            var titel = space.DisplayName.Length > 0 ? space.DisplayName : partner.SenderNaam;

            var transcript = string.Join("\n", berichten.Select(b =>
                $"[{b.Tijd.ToLocalTime():dd-MM HH:mm}] " +
                $"{(b.SenderId == s.MijnUserId ? "Maarten (ikzelf)" : b.SenderNaam)}: " +
                TekstMetBijlagen(b.Tekst, b.Afbeeldingen, b.Bestanden)));

            resultaat.Add(new MailBericht
            {
                ChatSpace = space.Name,
                MessageId = "chat:" + laatste.Name, // cache-sleutel; verschuift mee met het gesprek
                Van = titel,
                VanAdres = space.DisplayName.Length > 0 ? "ruimte" : "chat",
                // Een bericht met alleen een foto heeft geen tekst: dan een korte aanduiding,
                // anders staat er een lege regel in de berichtenlijst.
                Onderwerp = laatste.Tekst.Length > 0
                    ? Kort(laatste.Tekst, 80)
                    : laatste.Afbeeldingen.Count > 0 ? "📷 foto" : "📎 bijlage",
                Datum = laatste.Tijd,
                Tekst = transcript,
                Html = BouwChatHtml(berichten.Select(b => new ChatRegel(
                    b.SenderNaam, b.Tijd, b.SenderId == s.MijnUserId, b.Tekst,
                    b.Afbeeldingen, b.Bestanden))),
            });
        }

        return resultaat.OrderByDescending(m => m.Datum).ToList();
    }

    /// <summary>Verstuurt een chatbericht naar de opgegeven space.</summary>
    public static async Task VerstuurAsync(
        GoogleChatSettings s, string spaceName, string tekst, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://chat.googleapis.com/v1/{spaceName}/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { text = tekst }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
    }

    /// <summary>
    /// Reageert met een emoji (standaard 👍) op een chatbericht — vaak alles wat een berichtje
    /// nodig heeft. <paramref name="messageName"/> is de volledige naam
    /// ("spaces/…/messages/…"), zoals opgeslagen in MailBericht.MessageId achter "chat:".
    /// </summary>
    public static async Task ReageerAsync(
        GoogleChatSettings s, string messageName, CancellationToken ct, string emoji = "👍")
    {
        var token = await AccessTokenAsync(s, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://chat.googleapis.com/v1/{messageName}/reactions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { emoji = new { unicode = emoji } }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        // Twee keer dezelfde reactie = 409; dat is geen fout maar "stond er al".
        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            await ControleerAsync(response, ct);
        }
    }

    /// <summary>
    /// Zet een afspraak in de primaire Google-agenda (vereist dat de koppeling de
    /// agenda-scope heeft; herkoppelen als dit met 403 "insufficient scopes" faalt).
    /// </summary>
    public static async Task MaakAgendaEventAsync(
        GoogleChatSettings s, string titel, DateTimeOffset start, DateTimeOffset einde,
        CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://www.googleapis.com/calendar/v3/calendars/primary/events")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                summary = titel,
                start = new { dateTime = start.ToString("yyyy-MM-dd'T'HH:mm:sszzz") },
                end = new { dateTime = einde.ToString("yyyy-MM-dd'T'HH:mm:sszzz") },
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
    }

    // ---------- Intern ----------

    /// <summary>
    /// True als de space in Google Chat zelf al gelezen is tot en met dit bericht. Bij een
    /// koppeling zonder readstate-scope (of een API-fout) komt er false terug: dan geldt
    /// gewoon de oude regel en wordt er niets extra weggefilterd.
    /// </summary>
    private static async Task<bool> GelezenAsync(
        string token, string spaceName, DateTimeOffset laatste, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://chat.googleapis.com/v1/users/me/{spaceName}/spaceReadState");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("lastReadTime", out var t) &&
                   DateTimeOffset.TryParse(t.GetString(), out var gelezen) &&
                   gelezen >= laatste;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Space(string Name, string DisplayName, DateTimeOffset LaatstActief);

    private sealed class ChatMsg
    {
        public string Name = "";
        public string SenderId = "";
        public string SenderNaam = "";
        public string Tekst = "";
        public DateTimeOffset Tijd;
        /// <summary>Meegestuurde afbeeldingen, al omgezet naar data-URL's.</summary>
        public List<string> Afbeeldingen = new();
        /// <summary>Namen van bijlagen die geen afbeelding zijn (pdf, zip …).</summary>
        public List<string> Bestanden = new();
    }

    /// <summary>
    /// Al opgehaalde afbeeldingen, op resourceName. De chatlijst wordt elke pollronde
    /// opnieuw opgebouwd; zonder deze cache zou dezelfde foto elke twee minuten opnieuw
    /// gedownload worden.
    /// </summary>
    private static readonly Dictionary<string, string> AfbeeldingCache = new();

    /// <summary>
    /// Grens voor de download per afbeelding; daarboven tonen we alleen de bestandsnaam.
    /// Ruim genomen: telefoonfoto's van 5–10 MB worden hieronder toch verkleind vóór ze
    /// in de HTML belanden.
    /// </summary>
    private const int MaxAfbeeldingBytes = 12 * 1024 * 1024;

    private static async Task<List<Space>> SpacesAsync(string token, CancellationToken ct)
    {
        var spaces = new List<Space>();
        var pageToken = "";
        do
        {
            using var doc = await GetAsync(token,
                $"https://chat.googleapis.com/v1/spaces?pageSize=100&pageToken={Uri.EscapeDataString(pageToken)}", ct);
            if (doc.RootElement.TryGetProperty("spaces", out var lijst))
            {
                spaces.AddRange(lijst.EnumerateArray().Select(s => new Space(
                    s.GetProperty("name").GetString() ?? "",
                    s.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                    s.TryGetProperty("lastActiveTime", out var la) &&
                        DateTimeOffset.TryParse(la.GetString(), out var moment)
                        ? moment : DateTimeOffset.MinValue)));
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next)
                ? next.GetString() ?? ""
                : "";
        } while (pageToken.Length > 0);
        return spaces;
    }

    private static async Task<List<ChatMsg>> BerichtenAsync(
        string token, string spaceName, DateTimeOffset sinds, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"createTime > \"{sinds:yyyy-MM-dd'T'HH:mm:ss'Z'}\"");
        // Nieuwste eerst opvragen: bij meer dan 50 berichten in het venster zouden we anders
        // juist de óudste 50 krijgen en de recentste missen (de lijst wordt hieronder weer
        // chronologisch gesorteerd).
        var orderBy = Uri.EscapeDataString("createTime desc");
        using var doc = await GetAsync(token,
            $"https://chat.googleapis.com/v1/{spaceName}/messages?pageSize=50&filter={filter}&orderBy={orderBy}", ct);
        var berichten = new List<ChatMsg>();
        if (doc.RootElement.TryGetProperty("messages", out var lijst))
        {
            foreach (var m in lijst.EnumerateArray())
            {
                var tekst = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                // Bijlagen: een bericht met alleen een foto heeft geen tekst en werd vroeger
                // helemaal overgeslagen — dan zag je in WorkManager niets van wat er gestuurd was.
                var afbeeldingen = new List<string>();
                var bestanden = new List<string>();
                if (m.TryGetProperty("attachment", out var bijlagen) &&
                    bijlagen.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bijlage in bijlagen.EnumerateArray())
                    {
                        var naam = bijlage.TryGetProperty("contentName", out var cn)
                            ? cn.GetString() ?? "bijlage" : "bijlage";
                        var soort = bijlage.TryGetProperty("contentType", out var ct2)
                            ? ct2.GetString() ?? "" : "";
                        var bron = bijlage.TryGetProperty("attachmentDataRef", out var adr) &&
                            adr.TryGetProperty("resourceName", out var rn) ? rn.GetString() ?? "" : "";
                        if (soort.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                            bron.Length > 0 &&
                            await AfbeeldingAsync(token, bron, soort, ct) is { Length: > 0 } dataUrl)
                        {
                            afbeeldingen.Add(dataUrl);
                        }
                        else
                        {
                            bestanden.Add(naam);
                        }
                    }
                }
                if (tekst.Length == 0 && afbeeldingen.Count == 0 && bestanden.Count == 0)
                {
                    continue; // systeemberichten zonder inhoud
                }
                var sender = m.TryGetProperty("sender", out var snd) ? snd : default;
                berichten.Add(new ChatMsg
                {
                    Afbeeldingen = afbeeldingen,
                    Bestanden = bestanden,
                    Name = m.GetProperty("name").GetString() ?? "",
                    SenderId = (sender.ValueKind == JsonValueKind.Object &&
                        sender.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "")
                        .Replace("users/", ""),
                    SenderNaam = sender.ValueKind == JsonValueKind.Object &&
                        sender.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    Tekst = tekst,
                    Tijd = m.TryGetProperty("createTime", out var tijd) &&
                        DateTimeOffset.TryParse(tijd.GetString(), out var d) ? d : DateTimeOffset.Now,
                });
            }
        }
        return berichten.OrderBy(b => b.Tijd).ToList();
    }

    /// <summary>
    /// Zoekt de directe chat (DM) met een persoon op naam en retourneert de space-id
    /// ("spaces/…"), of "" als die niet gevonden wordt. Probeert eerst de ledenlijst per
    /// DM-space (vereist de memberships-scope) en valt anders terug op de afzendernamen
    /// van recente berichten in die DM.
    /// </summary>
    public static async Task<string> ZoekDmAsync(GoogleChatSettings s, string naam, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        var delen = naam.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Er kunnen meerdere chats met dezelfde persoon bestaan: op recentste activiteit
        // sorteren, zodat de eerste match de hoofdchat is (waar het gesprek nu loopt).
        foreach (var space in (await SpacesAsync(token, ct))
            .OrderByDescending(sp => sp.LaatstActief))
        {
            if (space.DisplayName.Length > 0)
            {
                continue; // groepsruimtes hebben een naam; DM's niet
            }
            var ids = new List<string>();
            try
            {
                using var doc = await GetAsync(token,
                    $"https://chat.googleapis.com/v1/{space.Name}/members?pageSize=10", ct);
                if (doc.RootElement.TryGetProperty("memberships", out var leden))
                {
                    ids.AddRange(leden.EnumerateArray()
                        .Select(l => l.TryGetProperty("member", out var m) &&
                            m.TryGetProperty("name", out var n)
                            ? (n.GetString() ?? "").Replace("users/", "") : "")
                        .Where(id => id.Length > 0));
                }
            }
            catch
            {
                // Geen memberships-scope (oude koppeling): afzenders van berichten proberen.
                try
                {
                    ids.AddRange((await BerichtenAsync(token, space.Name,
                        DateTimeOffset.Now.AddDays(-90), ct))
                        .Select(b => b.SenderId).Distinct());
                }
                catch
                {
                    continue;
                }
            }
            foreach (var id in ids)
            {
                var lidNaam = (await NaamAsync(s, token, id, ct)).ToLowerInvariant();
                if (delen.All(d => lidNaam.Contains(d)))
                {
                    return space.Name;
                }
            }
        }
        return "";
    }

    public sealed record ChatRegel(
        string Naam, DateTimeOffset Tijd, bool VanMij, string Tekst,
        IReadOnlyList<string>? Afbeeldingen = null, IReadOnlyList<string>? Bestanden = null);

    /// <summary>
    /// Berichttekst voor transcripten (Claude-concepten, snelantwoord, webversie): een
    /// bericht met alleen een foto zou daar anders als lege regel verschijnen, dus foto's
    /// en bijlagen krijgen een tekstmarkering achter de tekst.
    /// </summary>
    public static string TekstMetBijlagen(
        string tekst, IReadOnlyList<string>? afbeeldingen, IReadOnlyList<string>? bestanden)
    {
        var markers = new List<string>();
        var fotos = afbeeldingen?.Count ?? 0;
        if (fotos > 0)
        {
            markers.Add(fotos == 1 ? "[📷 foto]" : $"[📷 {fotos} foto's]");
        }
        markers.AddRange((bestanden ?? Array.Empty<string>()).Select(naam => $"[📎 {naam}]"));
        if (markers.Count == 0)
        {
            return tekst;
        }
        var achtervoegsel = string.Join(" ", markers);
        return tekst.Length == 0 ? achtervoegsel : $"{tekst} {achtervoegsel}";
    }

    /// <summary>
    /// Haalt een meegestuurde afbeelding op en geeft haar terug als data-URL. De WebView kan
    /// de Google-URL zelf niet ophalen (die vraagt een token), dus de bytes gaan mee in de
    /// HTML. Leeg bij een fout of een te groot bestand — dan tonen we alleen de naam.
    /// </summary>
    private static async Task<string> AfbeeldingAsync(
        string token, string resourceName, string contentType, CancellationToken ct)
    {
        if (AfbeeldingCache.TryGetValue(resourceName, out var bekend))
        {
            return bekend;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://chat.googleapis.com/v1/media/{Uri.EscapeDataString(resourceName)}?alt=media");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaxAfbeeldingBytes)
            {
                return AfbeeldingCache[resourceName] = "";
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxAfbeeldingBytes)
            {
                return AfbeeldingCache[resourceName] = "";
            }
            // De cache niet eindeloos laten groeien.
            if (AfbeeldingCache.Count > 60)
            {
                AfbeeldingCache.Clear();
            }
            return AfbeeldingCache[resourceName] = WeergaveDataUrl(bytes, contentType);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Maakt van gedownloade afbeeldingsbytes een data-URL die klein genoeg is voor de
    /// weergave. Eén telefoonfoto van een paar MB zou de hele chat-HTML anders over de
    /// NavigateToString-limiet duwen, waarna de weergave terugvalt op platte tekst en er
    /// juist níets meer te zien is. Grote foto's worden daarom verkleind (max 900 px) en
    /// als JPEG opnieuw gecodeerd; kleine plaatjes gaan ongewijzigd mee.
    /// </summary>
    internal static string WeergaveDataUrl(byte[] bytes, string contentType)
    {
        const int maxZijde = 900;
        const int maxDirecteBytes = 250_000;
        try
        {
            using var invoer = new MemoryStream(bytes);
            using var origineel = System.Drawing.Image.FromStream(invoer);
            if (bytes.Length <= maxDirecteBytes &&
                origineel.Width <= maxZijde && origineel.Height <= maxZijde)
            {
                return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            }
            var schaal = Math.Min(1.0,
                (double)maxZijde / Math.Max(origineel.Width, origineel.Height));
            var breedte = Math.Max(1, (int)Math.Round(origineel.Width * schaal));
            var hoogte = Math.Max(1, (int)Math.Round(origineel.Height * schaal));
            using var klein = new Bitmap(breedte, hoogte);
            using (var g = Graphics.FromImage(klein))
            {
                // JPEG kent geen transparantie: doorschijnende screenshots op wit zetten.
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(origineel, 0, 0, breedte, hoogte);
            }
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.MimeType == "image/jpeg");
            using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, 80L);
            using var uitvoer = new MemoryStream();
            klein.Save(uitvoer, jpeg, parameters);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(uitvoer.ToArray())}";
        }
        catch
        {
            // Geen leesbaar beeld (of System.Drawing-strubbeling): dan maar de bestandsnaam.
            return "";
        }
    }

    /// <summary>
    /// Diagnose voor --chatimg: toont per recente space de berichten mét bijlage (rauwe
    /// attachment-JSON) en probeert elke afbeelding echt te downloaden, met het resultaat
    /// erbij. Zo is zichtbaar wáár het tonen van foto's strandt.
    /// </summary>
    public static async Task<string> DiagnoseAfbeeldingenAsync(
        GoogleChatSettings s, int dagen, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        var sinds = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, dagen));
        var sb = new StringBuilder();
        var totaal = 0;
        foreach (var space in (await SpacesAsync(token, ct))
            .Where(sp => sp.LaatstActief >= sinds))
        {
            var filter = Uri.EscapeDataString($"createTime > \"{sinds:yyyy-MM-dd'T'HH:mm:ss'Z'}\"");
            var orderBy = Uri.EscapeDataString("createTime desc");
            using var doc = await GetAsync(token,
                $"https://chat.googleapis.com/v1/{space.Name}/messages?pageSize=50&filter={filter}&orderBy={orderBy}", ct);
            if (!doc.RootElement.TryGetProperty("messages", out var lijst))
            {
                continue;
            }
            foreach (var m in lijst.EnumerateArray())
            {
                if (!m.TryGetProperty("attachment", out var bijlagen) ||
                    bijlagen.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                totaal++;
                sb.AppendLine($"=== {space.Name} ({(space.DisplayName.Length > 0 ? space.DisplayName : "DM")})");
                sb.AppendLine($"    bericht: {m.GetProperty("name").GetString()}");
                foreach (var bijlage in bijlagen.EnumerateArray())
                {
                    sb.AppendLine("    attachment: " + JsonSerializer.Serialize(bijlage,
                        new JsonSerializerOptions { WriteIndented = false }));
                    var bron = bijlage.TryGetProperty("attachmentDataRef", out var adr) &&
                        adr.TryGetProperty("resourceName", out var rn) ? rn.GetString() ?? "" : "";
                    if (bron.Length == 0)
                    {
                        sb.AppendLine("    → geen attachmentDataRef.resourceName (Drive-bestand?)");
                        continue;
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://chat.googleapis.com/v1/media/{Uri.EscapeDataString(bron)}?alt=media");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await Http.SendAsync(request, ct);
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    sb.AppendLine($"    → download: HTTP {(int)response.StatusCode}, " +
                        $"{bytes.Length} bytes, type {response.Content.Headers.ContentType}");
                    if (!response.IsSuccessStatusCode)
                    {
                        var foutTekst = Encoding.UTF8.GetString(bytes);
                        sb.AppendLine("      " + (foutTekst.Length > 300 ? foutTekst[..300] : foutTekst));
                    }
                }
            }
        }
        sb.AppendLine($"--- {totaal} bericht(en) met bijlage in de laatste {dagen} dag(en).");
        return sb.ToString();
    }

    /// <summary>Recente berichten uit één space, gestructureerd (oudste eerst).</summary>
    public static async Task<List<ChatRegel>> TranscriptRegelsAsync(
        GoogleChatSettings s, string spaceName, int dagen, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        var regels = new List<ChatRegel>();
        foreach (var b in await BerichtenAsync(token, spaceName,
            DateTimeOffset.Now.AddDays(-dagen), ct))
        {
            var naam = b.SenderId == s.MijnUserId
                ? "Ik"
                : b.SenderNaam.Length > 0 ? b.SenderNaam : await NaamAsync(s, token, b.SenderId, ct);
            regels.Add(new ChatRegel(naam, b.Tijd, b.SenderId == s.MijnUserId, b.Tekst,
                b.Afbeeldingen, b.Bestanden));
        }
        return regels;
    }

    /// <summary>
    /// Google Chat-stijl weergave: eigen berichten in Google-blauw rechts, de rest wit
    /// links met naam erboven; nieuwste onderaan en meteen in beeld.
    /// </summary>
    public static string BouwChatHtml(IEnumerable<ChatRegel> berichten)
    {
        // Totaalbudget voor ingebedde foto's: de weergave valt boven ±1,5 MB HTML terug op
        // platte tekst (NavigateToString-limiet), dus liever de óudste foto's als tekstchip
        // tonen dan de hele bubbelweergave verliezen. Nieuwste eerst, die winnen het budget.
        var fotoBudget = 1_000_000;
        var sb = new StringBuilder();
        sb.Append("<div class=\"wm-chat wm-chat-scroll\" style=\"background:#f6f8fc;margin:-16px;" +
            "padding:14px;display:flex;flex-direction:column-reverse;max-height:560px;" +
            "overflow-y:auto\">");
        foreach (var b in berichten.Reverse())
        {
            var kleur = b.VanMij ? "#d3e3fd" : "#ffffff";
            var kant = b.VanMij ? "flex-end" : "flex-start";
            sb.Append($"<div style=\"align-self:{kant};max-width:78%;margin:3px 0\">");
            sb.Append($"<div style=\"background:{kleur};border-radius:12px;padding:7px 11px 4px;" +
                "border:1px solid #e0e3e9;font-size:13.5px;color:#1f1f1f;" +
                "white-space:pre-wrap;word-break:break-word\">");
            if (!b.VanMij)
            {
                sb.Append("<div style=\"font-size:12px;font-weight:600;color:#1a73e8;" +
                    $"margin-bottom:2px\">{System.Net.WebUtility.HtmlEncode(b.Naam)}</div>");
            }
            sb.Append(System.Net.WebUtility.HtmlEncode(b.Tekst));
            // Meegestuurde foto's onder de tekst, schaalbaar binnen de bubbel.
            foreach (var afbeelding in b.Afbeeldingen ?? Array.Empty<string>())
            {
                if (afbeelding.Length <= fotoBudget)
                {
                    fotoBudget -= afbeelding.Length;
                    sb.Append($"<img src=\"{afbeelding}\" style=\"display:block;max-width:100%;" +
                        "border-radius:8px;margin:6px 0 2px\">");
                }
                else
                {
                    sb.Append("<div style=\"margin:5px 0 2px;font-size:12.5px;color:#5f6368\">" +
                        "📷 foto (te veel foto's om allemaal te tonen)</div>");
                }
            }
            foreach (var bestand in b.Bestanden ?? Array.Empty<string>())
            {
                sb.Append("<div style=\"margin:5px 0 2px;font-size:12.5px;color:#1a73e8\">📎 " +
                    System.Net.WebUtility.HtmlEncode(bestand) + "</div>");
            }
            sb.Append("<div style=\"font-size:10.5px;color:#5f6368;text-align:right;margin-top:3px\">" +
                $"{b.Tijd.ToLocalTime():d MMM HH:mm}</div>");
            sb.Append("</div></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Weergavenaam voor een account-id, via de cache of de People API.</summary>
    private static async Task<string> NaamAsync(
        GoogleChatSettings s, string token, string userId, CancellationToken ct)
    {
        if (userId.Length == 0)
        {
            return "onbekend";
        }
        if (s.NaamCache.TryGetValue(userId, out var bekend))
        {
            return bekend;
        }
        var naam = userId;
        try
        {
            using var doc = await GetAsync(token,
                $"https://people.googleapis.com/v1/people/{userId}?personFields=names", ct);
            if (doc.RootElement.TryGetProperty("names", out var namen) &&
                namen.GetArrayLength() > 0 &&
                namen[0].TryGetProperty("displayName", out var d))
            {
                naam = d.GetString() ?? userId;
            }
        }
        catch
        {
            // Naam niet opvraagbaar (geen contact/collega): toon het id.
        }
        s.NaamCache[userId] = naam;
        s.Save();
        return naam;
    }

    /// <summary>
    /// Een geldig access-token voor de gekoppelde Google-account. Publiek omdat ook de
    /// Drive-client (bijlagen opslaan) op dezelfde koppeling en hetzelfde token meelift.
    /// </summary>
    public static async Task<string> AccessTokenAsync(GoogleChatSettings s, CancellationToken ct)
    {
        if (_accessToken.Length > 0 && DateTimeOffset.Now < _accessTokenGeldigTot)
        {
            return _accessToken;
        }
        if (!s.Gekoppeld)
        {
            throw new InvalidOperationException("Google Chat is nog niet gekoppeld (zie Instellingen).");
        }
        using var doc = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["refresh_token"] = s.RefreshToken,
            ["client_id"] = s.ClientId,
            ["client_secret"] = s.ClientSecret,
            ["grant_type"] = "refresh_token",
        }, ct);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        _accessTokenGeldigTot = DateTimeOffset.Now.AddSeconds(
            doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 60 : 3000);
        return _accessToken;
    }

    private static async Task<JsonDocument> TokenRequestAsync(
        Dictionary<string, string> velden, CancellationToken ct)
    {
        using var response = await Http.PostAsync(
            "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(velden), ct);
        await ControleerAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonDocument> GetAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        await ControleerAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ControleerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Google API {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }
}
