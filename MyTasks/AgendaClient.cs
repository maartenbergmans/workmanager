using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Instellingen voor de agenda-koppeling: de geheime iCal-adressen van Google Calendar
/// (één per regel, DPAPI-versleuteld — het zijn geheime URL's). Persistent in
/// %APPDATA%\WorkManager\agenda-settings.json.
/// </summary>
public class AgendaSettings
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string SettingsFile = Path.Combine(DataDir, "agenda-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string UrlsVersleuteld { get; set; } = "";
    public string HilkeUrlsVersleuteld { get; set; } = "";

    /// <summary>Hilkes afspraken in de cockpit tonen (de knop in het meetingpaneel togglet dit).</summary>
    public bool HilkeTonen { get; set; } = true;

    [JsonIgnore]
    public List<string> Urls
    {
        get => Decrypt(UrlsVersleuteld)
            .Split('\n')
            .Select(r => r.Trim())
            .Where(r => r.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                        r.StartsWith("caldav:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        set => UrlsVersleuteld = Encrypt(string.Join("\n", value));
    }

    /// <summary>Agenda van Hilke: apart (lichter grijs, onderaan) getoond in de cockpit.</summary>
    [JsonIgnore]
    public List<string> HilkeUrls
    {
        get => Decrypt(HilkeUrlsVersleuteld)
            .Split('\n')
            .Select(r => r.Trim())
            .Where(r => r.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                        r.StartsWith("caldav:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        set => HilkeUrlsVersleuteld = Encrypt(string.Join("\n", value));
    }

    [JsonIgnore]
    public bool Compleet => Urls.Count > 0;

    private static string Encrypt(string value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static AgendaSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AgendaSettings>(File.ReadAllText(SettingsFile), JsonOpts);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults.
        }
        return new AgendaSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>
/// Leest Google Calendar via de geheime iCal-feeds en levert de afspraken binnen een
/// datumvenster op. De ICS-parser dekt de gangbare gevallen: gewone en hele-dag-afspraken,
/// tijdzones (TZID/UTC), herhalingen (RRULE daily/weekly/monthly/yearly met INTERVAL,
/// UNTIL, COUNT en BYDAY), uitzonderingen (EXDATE) en verplaatste instanties (RECURRENCE-ID).
/// </summary>
public static class AgendaClient
{
    /// <summary>
    /// Eén afspraak. <paramref name="Locatie"/>, <paramref name="Omschrijving"/> en
    /// <paramref name="Deelnemers"/> zijn optioneel (niet elke agenda vult ze in) en worden
    /// gebruikt door de meetingvoorbereiding en de reisassistent.
    /// </summary>
    public sealed record AgendaItem(
        DateTimeOffset Start, DateTimeOffset Einde, bool HeleDag, string Titel,
        string Locatie = "", string Omschrijving = "", IReadOnlyList<string>? Deelnemers = null,
        string Uid = "", bool Herhalend = false, string MeetLink = "")
    {
        public IReadOnlyList<string> Genodigden => Deelnemers ?? Array.Empty<string>();

        /// <summary>Een los (niet-herhalend) event met UID kan in de app bewerkt worden.</summary>
        public bool Bewerkbaar => Uid.Length > 0 && !Herhalend;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<List<AgendaItem>> OphalenAsync(
        List<string> urls, DateOnly van, DateOnly tot, CancellationToken ct)
    {
        var items = new List<AgendaItem>();
        foreach (var url in urls)
        {
            // Twee soorten bronnen: een gewone ICS-feed (https://…/basic.ics) of een
            // "caldav:<agenda-id>" — voor agenda's die met Maarten gedeeld zijn maar waarvan
            // Google geen geheim iCal-adres toont (bv. Hilkes gmail-agenda). Die worden via
            // CalDAV opgehaald met het bestaande Gmail-app-wachtwoord.
            var ics = url.StartsWith("caldav:", StringComparison.OrdinalIgnoreCase)
                ? await CalDavIcsAsync(url[7..].Trim(), van, tot, ct)
                : await Http.GetStringAsync(url, ct);
            items.AddRange(ParseIcs(ics, van, tot));
        }
        return items.OrderBy(i => i.Start).ThenBy(i => !i.HeleDag).ToList();
    }

    /// <summary>
    /// Haalt de events van een (gedeelde) Google-agenda op via CalDAV en geeft ze terug als
    /// één ICS-tekst voor de bestaande parser. Auth: het Gmail-adres + app-wachtwoord uit de
    /// mailinstellingen — dezelfde als waarmee afspraken aangemaakt worden.
    /// </summary>
    private static async Task<string> CalDavIcsAsync(
        string agendaId, DateOnly van, DateOnly tot, CancellationToken ct)
    {
        var s = MailReplySettings.Load();
        if (s.Email.Length == 0 || s.AppWachtwoord.Length == 0)
        {
            return ""; // geen koppeling: stil niets teruggeven
        }
        // Eén dag marge aan beide kanten: tijdzones en hele-dag-events vallen anders net
        // buiten het venster.
        var start = van.AddDays(-1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var einde = tot.AddDays(2).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var body =
            "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
            "<d:prop><c:calendar-data/></d:prop>" +
            "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
            $"<c:time-range start=\"{start:yyyyMMdd'T'HHmmss'Z'}\" end=\"{einde:yyyyMMdd'T'HHmmss'Z'}\"/>" +
            "</c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";

        using var req = new HttpRequestMessage(
            new HttpMethod("REPORT"),
            $"https://www.google.com/calendar/dav/{Uri.EscapeDataString(agendaId)}/events/")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml"),
        };
        req.Headers.TryAddWithoutValidation("Depth", "1");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{s.Email}:{s.AppWachtwoord}")));
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode(); // 207 Multi-Status telt als succes
        var xml = await res.Content.ReadAsStringAsync(ct);

        // De VCALENDAR-blokken zitten (XML-geëscapet) in de calendar-data-elementen; alles
        // achter elkaar plakken volstaat voor de parser, die leest per BEGIN:VEVENT.
        var blokken = System.Text.RegularExpressions.Regex
            .Matches(xml, @"<[^>]*calendar-data[^>]*>([\s\S]*?)</[^>]*calendar-data>")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        return string.Join("\n", blokken);
    }

    // ---------- ICS-parsing ----------

    private sealed class RuwEvent
    {
        public string Titel = "";
        public string Uid = "";
        public string Status = "";
        public string DtStart = "";
        public string DtStartTz = "";
        public bool StartIsDatum;
        public string DtEnd = "";
        public string DtEndTz = "";
        public string RRule = "";
        public string RecurrenceId = "";
        public string Locatie = "";
        public string Omschrijving = "";
        public string MeetLink = "";
        public readonly List<string> ExDates = new();
        public readonly List<string> Deelnemers = new();
    }

    internal static List<AgendaItem> ParseIcs(string ics, DateOnly van, DateOnly tot)
    {
        var events = LeesEvents(ics);

        // Verplaatste instanties (RECURRENCE-ID) verdringen de gegenereerde instantie
        // van de reeks op dat oorspronkelijke moment.
        var overrides = events
            .Where(e => e.RecurrenceId.Length > 0)
            .ToLookup(e => e.Uid, e => ParseMoment(e.RecurrenceId, e.DtStartTz, e.StartIsDatum));

        var vanMoment = van.ToDateTime(TimeOnly.MinValue);
        var totMoment = tot.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var resultaat = new List<AgendaItem>();

        foreach (var ev in events)
        {
            if (ev.DtStart.Length == 0 ||
                ev.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var start = ParseMoment(ev.DtStart, ev.DtStartTz, ev.StartIsDatum);
            var einde = ev.DtEnd.Length > 0
                ? ParseMoment(ev.DtEnd, ev.DtEndTz.Length > 0 ? ev.DtEndTz : ev.DtStartTz, ev.StartIsDatum)
                : start + (ev.StartIsDatum ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            var duur = einde - start;

            if (ev.RRule.Length == 0)
            {
                // Los event (of override-instantie): meenemen als het het venster raakt.
                if (start < totMoment && einde > vanMoment)
                {
                    resultaat.Add(new AgendaItem(
                        Lokaal(start), Lokaal(einde), ev.StartIsDatum, ev.Titel,
                        ev.Locatie, ev.Omschrijving, ev.Deelnemers, ev.Uid,
                        MeetLink: ev.MeetLink));
                }
                continue;
            }

            var exDates = ev.ExDates
                .SelectMany(x => x.Split(','))
                .Select(x => ParseMoment(x.Trim(), ev.DtStartTz, ev.StartIsDatum))
                .ToHashSet();
            var verdrongen = overrides[ev.Uid].ToHashSet();

            foreach (var occurrence in Herhalingen(start, ev.RRule, totMoment))
            {
                if (occurrence + duur <= vanMoment ||
                    exDates.Contains(occurrence) || verdrongen.Contains(occurrence))
                {
                    continue;
                }
                if (occurrence < totMoment)
                {
                    resultaat.Add(new AgendaItem(
                        Lokaal(occurrence), Lokaal(occurrence + duur), ev.StartIsDatum, ev.Titel,
                        ev.Locatie, ev.Omschrijving, ev.Deelnemers, ev.Uid, Herhalend: true,
                        MeetLink: ev.MeetLink));
                }
            }
        }
        return resultaat.OrderBy(i => i.Start).ToList();
    }

    private static List<RuwEvent> LeesEvents(string ics)
    {
        // Regels "unfolden": vervolgregels beginnen met een spatie of tab.
        var regels = new List<string>();
        foreach (var ruw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((ruw.StartsWith(' ') || ruw.StartsWith('\t')) && regels.Count > 0)
            {
                regels[^1] += ruw[1..];
            }
            else
            {
                regels.Add(ruw);
            }
        }

        var events = new List<RuwEvent>();
        RuwEvent? huidig = null;
        foreach (var regel in regels)
        {
            if (regel == "BEGIN:VEVENT")
            {
                huidig = new RuwEvent();
                continue;
            }
            if (regel == "END:VEVENT")
            {
                if (huidig is not null)
                {
                    events.Add(huidig);
                }
                huidig = null;
                continue;
            }
            if (huidig is null)
            {
                continue;
            }

            var scheiding = regel.IndexOf(':');
            if (scheiding < 0)
            {
                continue;
            }
            var kop = regel[..scheiding];
            var waarde = regel[(scheiding + 1)..];
            var naam = kop.Split(';')[0].ToUpperInvariant();
            var tzid = "";
            var cn = "";
            var isDatum = kop.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) &&
                          !kop.Contains("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase);
            foreach (var param in kop.Split(';').Skip(1))
            {
                if (param.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
                {
                    tzid = param[5..];
                }
                else if (param.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    cn = param[3..].Trim('"');
                }
            }

            switch (naam)
            {
                case "SUMMARY":
                    huidig.Titel = Unescape(waarde);
                    break;
                case "UID":
                    huidig.Uid = waarde;
                    break;
                case "STATUS":
                    huidig.Status = waarde;
                    break;
                case "DTSTART":
                    huidig.DtStart = waarde;
                    huidig.DtStartTz = tzid;
                    huidig.StartIsDatum = isDatum || (waarde.Length == 8 && !waarde.Contains('T'));
                    break;
                case "DTEND":
                    huidig.DtEnd = waarde;
                    huidig.DtEndTz = tzid;
                    break;
                case "RRULE":
                    huidig.RRule = waarde;
                    break;
                case "EXDATE":
                    huidig.ExDates.Add(waarde);
                    break;
                case "RECURRENCE-ID":
                    huidig.RecurrenceId = waarde;
                    break;
                case "LOCATION":
                    huidig.Locatie = Unescape(waarde);
                    break;
                case "DESCRIPTION":
                    huidig.Omschrijving = UnescapeMeerregelig(waarde);
                    break;
                case "X-GOOGLE-CONFERENCE":
                case "CONFERENCE":
                    // Google zet de Meet-link van een afspraak niet in de omschrijving maar
                    // in dit veld; zonder dit veld heeft een Meet-afspraak dus geen joinlink.
                    if (waarde.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        huidig.MeetLink = waarde.Trim();
                    }
                    break;
                case "ATTENDEE":
                case "ORGANIZER":
                    // Naam als die er is, anders het adres achter "mailto:"; resources
                    // (vergaderzalen) laten we buiten de deelnemerslijst.
                    var adres = waarde.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? waarde[7..]
                        : waarde;
                    var deelnemer = cn.Length > 0 ? $"{cn} <{adres}>" : adres;
                    if (adres.Length > 0 &&
                        !kop.Contains("CUTYPE=RESOURCE", StringComparison.OrdinalIgnoreCase) &&
                        !kop.Contains("CUTYPE=ROOM", StringComparison.OrdinalIgnoreCase) &&
                        !huidig.Deelnemers.Contains(deelnemer, StringComparer.OrdinalIgnoreCase))
                    {
                        huidig.Deelnemers.Add(deelnemer);
                    }
                    break;
            }
        }
        return events;
    }

    /// <summary>Parseert een ICS-moment naar lokale tijd (DateTime, Kind=Local of Unspecified-lokaal).</summary>
    private static DateTime ParseMoment(string waarde, string tzid, bool isDatum)
    {
        if (isDatum || (waarde.Length == 8 && !waarde.Contains('T')))
        {
            return DateTime.ParseExact(waarde[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
        }

        var utc = waarde.EndsWith('Z');
        var dt = DateTime.ParseExact(waarde.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc)
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }
        if (tzid.Length > 0)
        {
            try
            {
                // .NET herkent IANA-namen ("Europe/Brussels") ook op Windows.
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified),
                    zone, TimeZoneInfo.Local);
            }
            catch
            {
                // Onbekende zone: als lokale tijd behandelen.
            }
        }
        return dt;
    }

    private static DateTimeOffset Lokaal(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Local));

    /// <summary>Genereert de startmomenten van een RRULE-reeks tot (exclusief) de venstergrens.</summary>
    private static IEnumerable<DateTime> Herhalingen(DateTime start, string rrule, DateTime totMoment)
    {
        var delen = rrule.Split(';')
            .Select(d => d.Split('=', 2))
            .Where(d => d.Length == 2)
            .ToDictionary(d => d[0].ToUpperInvariant(), d => d[1], StringComparer.OrdinalIgnoreCase);

        var freq = delen.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(delen.GetValueOrDefault("INTERVAL"), out var i) ? Math.Max(1, i) : 1;
        var count = int.TryParse(delen.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        DateTime? until = null;
        if (delen.TryGetValue("UNTIL", out var untilTekst))
        {
            until = ParseMoment(untilTekst, "", untilTekst.Length == 8);
        }

        // BYDAY voor weekly: MO,TU,… → dagen van de week.
        var byDay = new HashSet<DayOfWeek>();
        if (delen.TryGetValue("BYDAY", out var byDayTekst))
        {
            foreach (var dag in byDayTekst.Split(','))
            {
                // Posities zoals "2FR" (tweede vrijdag) worden hier bewust genegeerd voor weekly.
                var code = dag.Trim().Length >= 2 ? dag.Trim()[^2..] : dag.Trim();
                var gevonden = code switch
                {
                    "MO" => DayOfWeek.Monday,
                    "TU" => DayOfWeek.Tuesday,
                    "WE" => DayOfWeek.Wednesday,
                    "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday,
                    "SA" => DayOfWeek.Saturday,
                    "SU" => DayOfWeek.Sunday,
                    _ => (DayOfWeek?)null,
                };
                if (gevonden is { } d)
                {
                    byDay.Add(d);
                }
            }
        }

        var aantal = 0;
        var stappen = 0;
        if (freq == "WEEKLY" && byDay.Count > 0)
        {
            // Per dag itereren; een "week" telt per INTERVAL weken vanaf de startweek.
            var weekStart = start.Date.AddDays(-WeekdagIndex(start.DayOfWeek));
            for (var dag = start.Date; dag < totMoment && aantal < count && stappen < 20000; dag = dag.AddDays(1), stappen++)
            {
                var wekenSindsStart = (int)((dag.Date.AddDays(-WeekdagIndex(dag.DayOfWeek)) - weekStart).TotalDays / 7);
                if (wekenSindsStart % interval != 0 || !byDay.Contains(dag.DayOfWeek))
                {
                    continue;
                }
                var moment = dag + start.TimeOfDay;
                if (moment < start)
                {
                    continue;
                }
                if (until is { } u && moment > u)
                {
                    yield break;
                }
                aantal++;
                yield return moment;
            }
            yield break;
        }

        for (var moment = start; moment < totMoment && aantal < count && stappen < 20000; stappen++)
        {
            if (until is { } u && moment > u)
            {
                yield break;
            }
            aantal++;
            yield return moment;
            moment = freq switch
            {
                "DAILY" => moment.AddDays(interval),
                "WEEKLY" => moment.AddDays(7 * interval),
                "MONTHLY" => moment.AddMonths(interval),
                "YEARLY" => moment.AddYears(interval),
                _ => totMoment, // onbekende frequentie: alleen de eerste keer tonen
            };
        }
    }

    private static int WeekdagIndex(DayOfWeek dag) => ((int)dag + 6) % 7; // maandag = 0

    private static string Unescape(string tekst) => tekst
        .Replace("\\n", " · ")
        .Replace("\\,", ",")
        .Replace("\\;", ";")
        .Replace("\\\\", "\\");

    /// <summary>Zoals <see cref="Unescape"/>, maar met echte regeleindes — voor de omschrijving.</summary>
    private static string UnescapeMeerregelig(string tekst) => tekst
        .Replace("\\n", "\n")
        .Replace("\\N", "\n")
        .Replace("\\,", ",")
        .Replace("\\;", ";")
        .Replace("\\\\", "\\");
}
