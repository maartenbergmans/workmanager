using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Weersverwachting van vandaag via Open-Meteo (gratis, geen API-sleutel). Alleen wat in een
/// dagstartbriefing thuishoort: minimum, maximum, neerslag en of je een jas of paraplu nodig
/// hebt. Zonder ingesteld thuisadres levert dit niets op en blijft het weer uit de briefing.
/// </summary>
public static class Weer
{
    public sealed record Verwachting(double Min, double Max, double Neerslag, int Code)
    {
        public string Omschrijving => Code switch
        {
            0 => "onbewolkt",
            1 or 2 => "half bewolkt",
            3 => "bewolkt",
            45 or 48 => "mistig",
            51 or 53 or 55 or 56 or 57 => "motregen",
            61 or 63 or 80 or 81 => "regen",
            65 or 82 => "zware regen",
            66 or 67 => "ijzel",
            71 or 73 or 75 or 77 or 85 or 86 => "sneeuw",
            95 or 96 or 99 => "onweer",
            _ => "wisselvallig",
        };

        public bool ParapluNodig => Neerslag >= 1.0 || Code is 65 or 82 or 95 or 96 or 99;

        public string Regel =>
            $"{Omschrijving}, {Min:0}° tot {Max:0}°" +
            (Neerslag >= 0.2 ? $", {Neerslag:0.#} mm neerslag" : "") +
            (ParapluNodig ? " — paraplu mee" : "");

        /// <summary>Weericoon (emoji) voor zon/wolk/regen/sneeuw, passend bij de weather-code.</summary>
        public string Emoji => Code switch
        {
            0 => "☀️",
            1 or 2 => "⛅",
            3 => "☁️",
            45 or 48 => "🌫️",
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 80 or 81 => "🌧️",
            65 or 82 or 66 or 67 => "🌧️",
            71 or 73 or 75 or 77 or 85 or 86 => "❄️",
            95 or 96 or 99 => "⛈️",
            _ => "🌥️",
        };

        /// <summary>Korte weergave voor onderaan de kalender: icoon + min–max in graden.</summary>
        public string Kort => $"{Emoji} {Min:0}–{Max:0}°";
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static Task<Verwachting?> VandaagAsync(double lat, double lon, CancellationToken ct) =>
        VoorDagAsync(lat, lon, DateOnly.FromDateTime(DateTime.Now), ct);

    /// <summary>
    /// Weersverwachting voor één specifieke dag. Open-Meteo dekt met de gratis forecast-API zo'n
    /// twee weken vooruit en een paar dagen terug; valt de dag daarbuiten, dan komt er niets terug
    /// en blijft het weer weg. De datum wordt als start_date=end_date meegegeven.
    /// </summary>
    public static async Task<Verwachting?> VoorDagAsync(double lat, double lon, DateOnly dag, CancellationToken ct)
    {
        if (lat == 0 && lon == 0)
        {
            return null;
        }
        try
        {
            var datum = dag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Inv(lat)}&longitude={Inv(lon)}" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum" +
                      $"&timezone=Europe%2FBrussels&start_date={datum}&end_date={datum}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var daily = doc.RootElement.GetProperty("daily");
            if (!daily.TryGetProperty("time", out var tijd) || tijd.ValueKind != JsonValueKind.Array
                || tijd.GetArrayLength() == 0)
            {
                return null; // buiten het beschikbare venster
            }
            return new Verwachting(
                Eerste(daily, "temperature_2m_min"),
                Eerste(daily, "temperature_2m_max"),
                Eerste(daily, "precipitation_sum"),
                (int)Eerste(daily, "weather_code"));
        }
        catch
        {
            // Weer is bijzaak: zonder verwachting gaat de briefing/kalender gewoon door.
            return null;
        }
    }

    private static double Eerste(JsonElement daily, string naam) =>
        daily.TryGetProperty(naam, out var reeks) && reeks.ValueKind == JsonValueKind.Array &&
        reeks.GetArrayLength() > 0 && reeks[0].ValueKind == JsonValueKind.Number
            ? reeks[0].GetDouble()
            : 0;

    private static string Inv(double waarde) => waarde.ToString("0.####", CultureInfo.InvariantCulture);
}
