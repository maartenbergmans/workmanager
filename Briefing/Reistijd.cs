using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Rijtijd en afstand tussen twee punten, met verkeer van nú. Primair via de routeerder
/// achter Waze Live Map (dezelfde die waze.com/live-map gebruikt): die geeft zowel de rijtijd
/// mét realtime verkeer als de rijtijd zonder, zodat je ziet of het vandaag tegenzit. Dat is
/// een ongedocumenteerde endpoint, dus als hij wegvalt of anders antwoordt schakelt de code
/// door naar OSRM (gratis, maar zonder verkeer). Geocoderen gebeurt via Waze' eigen
/// zoekserver, met Nominatim als terugval. Geen van beide vraagt een API-sleutel.
/// </summary>
public static class Reistijd
{
    /// <summary>Rijtijd met en zonder verkeer, plus de afstand en welke bron het antwoord gaf.</summary>
    public sealed record Route(TimeSpan Duur, TimeSpan DuurZonderVerkeer, double Kilometer, string Bron)
    {
        /// <summary>Vertraging door het verkeer op dit moment (nooit negatief).</summary>
        public TimeSpan Vertraging => Duur > DuurZonderVerkeer ? Duur - DuurZonderVerkeer : TimeSpan.Zero;

        public bool FileOpDeWeg => Vertraging >= TimeSpan.FromMinutes(5);
    }

    public sealed record Punt(double Lat, double Lon);

    private static readonly HttpClient Http = MaakClient();

    /// <summary>Adres → coördinaten, voor de duur van de sessie onthouden (null = niet gevonden).</summary>
    private static readonly Dictionary<string, Punt?> GeocodeCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient MaakClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Beide diensten weigeren verzoeken zonder herkenbare User-Agent.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "WorkManager/1.0 (persoonlijke agenda-assistent; maarten@urbanit.be)");
        return http;
    }

    /// <summary>
    /// Zet een adres (of plaatsnaam) om in coördinaten. Eerst via Waze' zoekserver — die kent
    /// Belgische adressen goed — en anders via Nominatim. Null als geen van beide iets vindt.
    /// </summary>
    public static async Task<Punt?> GeocodeAsync(string adres, CancellationToken ct)
    {
        adres = adres.Trim();
        if (adres.Length < 3)
        {
            return null;
        }
        // Dezelfde afspraak wordt elke ronde opnieuw bekeken; het adres verandert niet.
        // Ook een mislukte zoekopdracht onthouden we, anders bevragen we de zoekserver
        // elke tien minuten voor een locatie die toch niet te vinden is.
        if (GeocodeCache.TryGetValue(adres, out var bekend))
        {
            return bekend;
        }
        var punt = await ViaWazeZoekAsync(adres, ct) ?? await ViaNominatimAsync(adres, ct);
        GeocodeCache[adres] = punt;
        return punt;
    }

    private static async Task<Punt?> ViaWazeZoekAsync(string adres, CancellationToken ct)
    {
        try
        {
            var url = "https://www.waze.com/SearchServer/mozi" +
                      $"?q={Uri.EscapeDataString(adres)}&lang=nl&origin=livemap";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            foreach (var treffer in doc.RootElement.EnumerateArray())
            {
                if (treffer.TryGetProperty("location", out var loc) &&
                    loc.TryGetProperty("lat", out var lat) && loc.TryGetProperty("lon", out var lon))
                {
                    return new Punt(lat.GetDouble(), lon.GetDouble());
                }
            }
        }
        catch
        {
            // Geen net, ander antwoordformaat of niets gevonden: Nominatim probeert het nog.
        }
        return null;
    }

    private static async Task<Punt?> ViaNominatimAsync(string adres, CancellationToken ct)
    {
        try
        {
            var url = "https://nominatim.openstreetmap.org/search?format=json&limit=1" +
                      $"&q={Uri.EscapeDataString(adres)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            foreach (var treffer in doc.RootElement.EnumerateArray())
            {
                if (double.TryParse(treffer.GetProperty("lat").GetString(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                    double.TryParse(treffer.GetProperty("lon").GetString(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    return new Punt(lat, lon);
                }
            }
        }
        catch
        {
            // Adres niet gevonden: de aanroeper slaat de reisassistentie voor deze afspraak over.
        }
        return null;
    }

    /// <summary>
    /// Rijtijd van <paramref name="van"/> naar <paramref name="naar"/> met het verkeer van nu.
    /// Null als geen enkele routeerder antwoordt.
    /// </summary>
    public static async Task<Route?> BerekenAsync(Punt van, Punt naar, CancellationToken ct) =>
        await ViaWazeRouteAsync(van, naar, ct) ?? await ViaOsrmAsync(van, naar, ct);

    private static async Task<Route?> ViaWazeRouteAsync(Punt van, Punt naar, CancellationToken ct)
    {
        try
        {
            // "row" = rest of world (Europa); de VS/Israël draaien op een aparte server.
            var url = "https://www.waze.com/row-RoutingManager/routingRequest" +
                      $"?from={Coord(van)}&to={Coord(naar)}" +
                      "&at=0&returnJSON=true&returnGeometries=false&returnInstructions=false" +
                      "&timeout=60000&nPaths=1&options=AVOID_TRAILS%3At%2CALLOW_UTURNS%3At";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.waze.com/nl/live-map/");
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var resultaten = VindResultaten(doc.RootElement);
            if (resultaten is not { } lijst)
            {
                return null;
            }

            double seconden = 0, secondenVrij = 0, meter = 0;
            foreach (var segment in lijst.EnumerateArray())
            {
                seconden += Getal(segment, "crossTime");
                // Zonder verkeer heet het veld anders; valt terug op de gewone tijd.
                var vrij = Getal(segment, "crossTimeWithoutRealTime");
                secondenVrij += vrij > 0 ? vrij : Getal(segment, "crossTime");
                meter += Getal(segment, "length");
            }
            if (seconden <= 0)
            {
                return null;
            }
            return new Route(
                TimeSpan.FromSeconds(seconden), TimeSpan.FromSeconds(secondenVrij),
                Math.Round(meter / 1000, 1), "Waze");
        }
        catch
        {
            // Endpoint gewijzigd of onbereikbaar: OSRM neemt over.
            return null;
        }
    }

    /// <summary>
    /// De segmentlijst uit het Waze-antwoord. Afhankelijk van de parameters zit die onder
    /// "response" of onder het eerste alternatief.
    /// </summary>
    private static JsonElement? VindResultaten(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("results", out var direct) &&
            direct.ValueKind == JsonValueKind.Array)
        {
            return direct;
        }
        if (root.TryGetProperty("alternatives", out var alternatieven) &&
            alternatieven.ValueKind == JsonValueKind.Array)
        {
            foreach (var alternatief in alternatieven.EnumerateArray())
            {
                if (alternatief.TryGetProperty("response", out var alt) &&
                    alt.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    return results;
                }
            }
        }
        return null;
    }

    private static double Getal(JsonElement element, string naam) =>
        element.TryGetProperty(naam, out var waarde) && waarde.ValueKind == JsonValueKind.Number
            ? waarde.GetDouble()
            : 0;

    private static async Task<Route?> ViaOsrmAsync(Punt van, Punt naar, CancellationToken ct)
    {
        try
        {
            var url = "https://router.project-osrm.org/route/v1/driving/" +
                      $"{Inv(van.Lon)},{Inv(van.Lat)};{Inv(naar.Lon)},{Inv(naar.Lat)}?overview=false";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("routes", out var routes) ||
                routes.GetArrayLength() == 0)
            {
                return null;
            }
            var eerste = routes[0];
            var duur = TimeSpan.FromSeconds(eerste.GetProperty("duration").GetDouble());
            var km = Math.Round(eerste.GetProperty("distance").GetDouble() / 1000, 1);
            // OSRM kent geen verkeer: rijtijd met en zonder zijn dezelfde.
            return new Route(duur, duur, km, "OSRM");
        }
        catch
        {
            return null;
        }
    }

    private static string Coord(Punt p) =>
        Uri.EscapeDataString($"x:{Inv(p.Lon)} y:{Inv(p.Lat)}");

    private static string Inv(double waarde) => waarde.ToString("0.######", CultureInfo.InvariantCulture);
}
