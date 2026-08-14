using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Wekelijkse bonuscheck: loopt (1×/week, op de achtergrond) alle gelinkte AH-producten uit de
/// vaste gerechten/suggesties/rubrieken na en meldt hoeveel er in de Bonus staan — zodat je dat
/// weet vóór je bestelt. Het resultaat gaat als tray-balloon naar Maarten; de details staan
/// toch al in de bestelflow (🏷-teller en oranje prijzen).
/// </summary>
public static class AhBonusRadar
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-bonus-radar.json");

    private static readonly string GerechtenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ah-gerechten.json");

    private static bool _bezig;

    /// <summary>
    /// Checkt (max. 1×/week) de bonusstatus en geeft een meldtekst terug, of null als er niets
    /// te melden valt (al gecheckt deze week, geen bonusproducten, of geen gerechtenbestand).
    /// </summary>
    public static async Task<string?> CheckWekelijksAsync(CancellationToken ct)
    {
        if (_bezig)
        {
            return null;
        }
        var week = $"{System.Globalization.ISOWeek.GetYear(DateTime.Now)}-" +
            $"{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now)}";
        if (LaatsteWeek() == week)
        {
            return null;
        }
        _bezig = true;
        try
        {
            BewaarWeek(week); // één poging per week, ook bij fouten

            var urls = LeesProductUrls();
            if (urls.Count == 0)
            {
                return null;
            }
            var namen = new List<string>();
            foreach (var url in urls.Take(80)) // ruime cap; de API blijft er licht onder
            {
                ct.ThrowIfCancellationRequested();
                if (AhApi.WebshopId(url) is not { } id)
                {
                    continue;
                }
                if (await AhApi.DetailAsync(id, ct) is { Bonus: true } info)
                {
                    namen.Add(info.Titel);
                }
            }
            if (namen.Count == 0)
            {
                return null;
            }
            var top = string.Join(", ", namen.Take(4));
            return $"{namen.Count} vaste product(en) in de AH-Bonus: {top}" +
                (namen.Count > 4 ? ", …" : "");
        }
        catch
        {
            return null; // volgende week opnieuw
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Alle unieke product-urls uit ah-gerechten.json (gerechten, suggesties, rubrieken).</summary>
    private static List<string> LeesProductUrls()
    {
        try
        {
            if (!File.Exists(GerechtenFile))
            {
                return new List<string>();
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(GerechtenFile));
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Verzamel(doc.RootElement, urls);
            return urls.ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Recursief alle "url"-waarden verzamelen, wat de structuur ook precies is.</summary>
    private static void Verzamel(JsonElement el, HashSet<string> urls)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name.Equals("url", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.String &&
                        prop.Value.GetString() is { Length: > 0 } url)
                    {
                        urls.Add(url);
                    }
                    else
                    {
                        Verzamel(prop.Value, urls);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    Verzamel(item, urls);
                }
                break;
        }
    }

    private static string LaatsteWeek()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                return JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) ?? "";
            }
        }
        catch
        {
            // Als "nog nooit" behandelen.
        }
        return "";
    }

    private static void BewaarWeek(string week)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(week));
        }
        catch
        {
            // Best effort.
        }
    }
}
