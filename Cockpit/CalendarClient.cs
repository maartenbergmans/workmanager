using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Schrijft afspraken naar Google Calendar via CalDAV, met exact hetzelfde app-wachtwoord als
/// de Gmail-koppeling (<see cref="MailReplySettings"/>). Google's legacy CalDAV-endpoint
/// (www.google.com/calendar/dav) aanvaardt dat app-wachtwoord via basic-auth, dus er is géén
/// aparte OAuth-koppeling nodig: wie mail kan versturen, kan ook een afspraak aanmaken.
/// </summary>
public static class CalendarClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Of er een mailkoppeling met app-wachtwoord bestaat om mee te schrijven.</summary>
    public static bool Beschikbaar
    {
        get
        {
            var s = MailReplySettings.Load();
            return s.Email.Length > 0 && s.AppWachtwoord.Length > 0;
        }
    }

    /// <summary>
    /// Maakt één afspraak aan in de hoofdagenda. <paramref name="start"/> is lokale tijd;
    /// hij wordt naar UTC omgezet. Geeft true als Google 201/204 teruggaf.
    /// </summary>
    public static async Task<bool> MaakAfspraakAsync(
        string titel, DateTime start, TimeSpan duur, string omschrijving, CancellationToken ct,
        string locatie = "")
    {
        var s = MailReplySettings.Load();
        if (s.Email.Length == 0 || s.AppWachtwoord.Length == 0)
        {
            return false;
        }

        var uid = $"wm-{Guid.NewGuid():N}@urbanit.be";
        var ics = BouwIcs(uid, titel, start, duur, omschrijving, locatie);
        var url = $"https://www.google.com/calendar/dav/{Uri.EscapeDataString(s.Email)}/events/{uid}.ics";

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(ics, Encoding.UTF8),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        // If-None-Match:* → alleen aanmaken, nooit per ongeluk een bestaande afspraak overschrijven.
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{s.Email}:{s.AppWachtwoord}")));

        using var res = await Http.SendAsync(req, ct);
        return res.IsSuccessStatusCode; // 201 Created of 204 No Content
    }

    private static AuthenticationHeaderValue Auth(MailReplySettings s) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Email}:{s.AppWachtwoord}")));

    /// <summary>Een via CalDAV teruggevonden afspraak uit de hoofdagenda (lokale tijden).</summary>
    public sealed record GevondenAfspraak(string Uid, DateTime Start, DateTime Einde, string Titel);

    /// <summary>
    /// Zoekt in de hoofdagenda alle afspraken op één dag waarvan de titel
    /// <paramref name="titelDeel"/> bevat. Gebruikt door de AH-leveringsverwerking (geen
    /// dubbele afspraken aanmaken) en de bezorgradar (de afspraak bijwerken op UID).
    /// </summary>
    public static async Task<List<GevondenAfspraak>> ZoekOpDagAsync(
        DateOnly dag, string titelDeel, CancellationToken ct) =>
        (await ZoekInPeriodeAsync(dag, dag, ct))
            .Where(a => a.Titel.Contains(titelDeel, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Alle losse (niet-herhalende) afspraken met een starttijd in de periode, in één
    /// CalDAV-REPORT. Gebruikt door de AH-agenda-planner om bezette avonden te markeren.
    /// </summary>
    public static async Task<List<GevondenAfspraak>> ZoekInPeriodeAsync(
        DateOnly van, DateOnly totEnMet, CancellationToken ct)
    {
        var s = MailReplySettings.Load();
        var resultaat = new List<GevondenAfspraak>();
        if (s.Email.Length == 0 || s.AppWachtwoord.Length == 0)
        {
            return resultaat;
        }
        // Eén dag marge: een event dat in UTC net vóór middernacht start valt anders buiten
        // het venster.
        var vanUtc = van.AddDays(-1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var totUtc = totEnMet.AddDays(2).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var body =
            "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
            "<d:prop><c:calendar-data/></d:prop>" +
            "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
            $"<c:time-range start=\"{vanUtc:yyyyMMdd'T'HHmmss'Z'}\" end=\"{totUtc:yyyyMMdd'T'HHmmss'Z'}\"/>" +
            "</c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";

        using var req = new HttpRequestMessage(new HttpMethod("REPORT"),
            $"https://www.google.com/calendar/dav/{Uri.EscapeDataString(s.Email)}/events/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        req.Headers.TryAddWithoutValidation("Depth", "1");
        req.Headers.Authorization = Auth(s);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            return resultaat;
        }
        var xml = await res.Content.ReadAsStringAsync(ct);
        var ics = string.Join("\n", Regex
            .Matches(xml, @"<[^>]*calendar-data[^>]*>([\s\S]*?)</[^>]*calendar-data>")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
        // Gevouwen ICS-regels (vervolgregel begint met spatie/tab) eerst ontvouwen.
        ics = Regex.Replace(ics, "\r?\n[ \t]", "");

        foreach (Match ev in Regex.Matches(ics, @"BEGIN:VEVENT([\s\S]*?)END:VEVENT"))
        {
            var blok = ev.Groups[1].Value;
            if (Regex.IsMatch(blok, @"^RRULE", RegexOptions.Multiline))
            {
                continue;
            }
            var titel = IcsWaarde(blok, "SUMMARY");
            var start = IcsTijd(blok, "DTSTART");
            var einde = IcsTijd(blok, "DTEND");
            var uid = IcsWaarde(blok, "UID");
            if (start is null || einde is null || uid.Length == 0 ||
                DateOnly.FromDateTime(start.Value) < van ||
                DateOnly.FromDateTime(start.Value) > totEnMet)
            {
                continue;
            }
            resultaat.Add(new GevondenAfspraak(uid, start.Value, einde.Value, titel));
        }
        return resultaat;
    }

    private static string IcsWaarde(string blok, string veld)
    {
        var m = Regex.Match(blok, $@"^{veld}[^:\r\n]*:(.*)$", RegexOptions.Multiline);
        return m.Success
            ? m.Groups[1].Value.Trim().Replace("\\,", ",").Replace("\\;", ";").Replace("\\n", "\n")
            : "";
    }

    /// <summary>DTSTART/DTEND naar lokale tijd; kent "…Z" (UTC) en TZID-vormen (≈ lokale tijd).</summary>
    private static DateTime? IcsTijd(string blok, string veld)
    {
        var m = Regex.Match(blok, $@"^{veld}[^:\r\n]*:(\d{{8}}T\d{{6}})(Z?)", RegexOptions.Multiline);
        if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd'T'HHmmss",
                null, System.Globalization.DateTimeStyles.None, out var t))
        {
            return null;
        }
        return m.Groups[2].Value == "Z"
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime()
            : t;
    }

    /// <summary>
    /// Wijzigt een bestaande afspraak (op UID): zoekt via een CalDAV-REPORT de resource-URL van
    /// het event en schrijft de nieuwe gegevens er overheen. Werkt voor losse (niet-herhalende)
    /// Google-events op de hoofdagenda. Geeft true bij succes; false als het event niet gevonden
    /// of niet te schrijven is (dan is bewerken in Google Agenda zelf nog een optie).
    /// </summary>
    public static async Task<bool> WijzigViaUidAsync(
        string uid, string titel, DateTime start, TimeSpan duur, string omschrijving, CancellationToken ct,
        string locatie = "")
    {
        var s = MailReplySettings.Load();
        if (s.Email.Length == 0 || s.AppWachtwoord.Length == 0 || uid.Length == 0)
        {
            return false;
        }
        var (putUrl, etag) = await ZoekResourceAsync(s, uid, ct);
        if (putUrl.Length == 0)
        {
            return false;
        }

        var ics = BouwIcs(uid, titel, start, duur, omschrijving, locatie);
        using var put = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new StringContent(ics, Encoding.UTF8),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        if (etag.Length > 0)
        {
            put.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        put.Headers.Authorization = Auth(s);
        using var putRes = await Http.SendAsync(put, ct);
        return putRes.IsSuccessStatusCode;
    }

    /// <summary>
    /// Verwijdert een bestaande afspraak (op UID) uit de hoofdagenda. Zelfde bereik als
    /// <see cref="WijzigViaUidAsync"/>: losse Google-events; uitnodigingen en herhalende
    /// afspraken weigert Google — dan blijft Google Agenda zelf de weg.
    /// </summary>
    public static async Task<bool> VerwijderViaUidAsync(string uid, CancellationToken ct)
    {
        var s = MailReplySettings.Load();
        if (s.Email.Length == 0 || s.AppWachtwoord.Length == 0 || uid.Length == 0)
        {
            return false;
        }
        var (url, etag) = await ZoekResourceAsync(s, uid, ct);
        if (url.Length == 0)
        {
            return false;
        }
        using var del = new HttpRequestMessage(HttpMethod.Delete, url);
        if (etag.Length > 0)
        {
            del.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        del.Headers.Authorization = Auth(s);
        using var res = await Http.SendAsync(del, ct);
        return res.IsSuccessStatusCode;
    }

    /// <summary>
    /// Zoekt de resource-URL (en etag) van een event op UID. Niet via een REPORT met
    /// UID-prop-filter: Google's legacy endpoint negeert dat filter en geeft álle events
    /// terug, waardoor "de eerste href" een willekeurige andere afspraak zou raken. De
    /// resourcenaam is gelukkig deterministisch — "&lt;uid&gt;.ics" (bij Google-UID's ook zonder
    /// het @-deel) — dus een GET op die naam mét controle van de UID in de inhoud volstaat.
    /// Niets gevonden = ("", ""): de app valt dan terug op Google Agenda zelf.
    /// </summary>
    private static async Task<(string Url, string Etag)> ZoekResourceAsync(
        MailReplySettings s, string uid, CancellationToken ct)
    {
        var collectie = $"https://www.google.com/calendar/dav/{Uri.EscapeDataString(s.Email)}/events/";
        var kandidaten = new List<string> { uid };
        var apenstaart = uid.IndexOf('@');
        if (apenstaart > 0)
        {
            kandidaten.Add(uid[..apenstaart]);
        }
        foreach (var naam in kandidaten)
        {
            var url = collectie + Uri.EscapeDataString(naam) + ".ics";
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Authorization = Auth(s);
            using var res = await Http.SendAsync(get, ct);
            if (!res.IsSuccessStatusCode)
            {
                continue;
            }
            var ics = await res.Content.ReadAsStringAsync(ct);
            if (!ics.Contains(uid, StringComparison.Ordinal))
            {
                continue; // andere afspraak achter die naam: niet aankomen
            }
            return (url, res.Headers.ETag?.Tag ?? "");
        }
        return ("", "");
    }

    private static string BouwIcs(
        string uid, string titel, DateTime start, TimeSpan duur, string omschrijving,
        string locatie = "")
    {
        var startUtc = start.ToUniversalTime();
        var eindUtc = (start + duur).ToUniversalTime();
        var nu = DateTime.UtcNow;
        var regels = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//WorkManager//AH-gerechten//NL",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{Utc(nu)}",
            $"DTSTART:{Utc(startUtc)}",
            $"DTEND:{Utc(eindUtc)}",
            $"SUMMARY:{Escape(titel)}",
            $"DESCRIPTION:{Escape(omschrijving)}",
        };
        if (locatie.Trim().Length > 0)
        {
            regels.Add($"LOCATION:{Escape(locatie.Trim())}");
        }
        regels.Add("END:VEVENT");
        regels.Add("END:VCALENDAR");
        return string.Join("\r\n", regels);
    }

    private static string Utc(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>iCalendar-escaping: backslash, puntkomma, komma en nieuwe regels.</summary>
    private static string Escape(string tekst) => tekst
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n");
}
