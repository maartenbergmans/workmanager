using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkManager;

/// <summary>
/// Vult automatisch de recepten aan: elk gerecht of elke suggestie zonder recepttekst krijgt
/// er (maximaal vijf per dag, 's ochtends op de achtergrond) één van Claude — dezelfde
/// prompt als de knop "Recept voorstellen" in de ingrediëntbewerker, maar dan zonder
/// handwerk. Zo heeft elke kaart in de bestelflow vanzelf een recept, bereidingstijd en
/// portie-aantal.
/// </summary>
public static class AhReceptAanvuller
{
    /// <summary>Hooguit zoveel Claude-runs per dag; binnen een week is alles gevuld.</summary>
    private const int MaxPerDag = 5;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "ah-recept-aanvuller.json");
    private static readonly string GerechtenFile = Path.Combine(DataDir, "ah-gerechten.json");

    private static bool _bezig;

    /// <summary>Draait de dagelijkse aanvulronde als hij aan de beurt is; stil bij tegenslag.</summary>
    public static async Task ZorgVoorAsync(Form eigenaar, CancellationToken ct)
    {
        var vandaag = DateTime.Today.ToString("O");
        if (_bezig || DateTime.Now.Hour < 9 || !File.Exists(GerechtenFile) ||
            LaadState() == vandaag)
        {
            return;
        }
        _bezig = true;
        try
        {
            BewaarState(vandaag);
            var ontbrekend = ZonderRecept();
            var gemaakt = 0;
            foreach (var (naam, ingredienten) in ontbrekend.Take(MaxPerDag))
            {
                ct.ThrowIfCancellationRequested();
                if (await MaakReceptAsync(naam, ingredienten, ct))
                {
                    gemaakt++;
                }
            }
            if (gemaakt > 0 && !eigenaar.IsDisposed)
            {
                eigenaar.BeginInvoke(() => Toast.Toon(eigenaar,
                    $"{gemaakt} recept(en) automatisch aangevuld" +
                    (ontbrekend.Count > gemaakt ? $" — morgen volgen er meer" : ""),
                    Fluent.EtenDrinken));
            }
        }
        catch
        {
            // Best effort; morgen opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Alle gerechten en suggesties die nog geen recept(tekst) hebben.</summary>
    private static List<(string Naam, List<string> Ingredienten)> ZonderRecept()
    {
        var resultaat = new List<(string, List<string>)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(GerechtenFile));
            var metRecept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("recepten", out var recepten) &&
                recepten.ValueKind == JsonValueKind.Object)
            {
                foreach (var r in recepten.EnumerateObject())
                {
                    if (r.Value.TryGetProperty("tekst", out var t) &&
                        (t.GetString()?.Length ?? 0) > 0)
                    {
                        metRecept.Add(r.Name);
                    }
                }
            }
            foreach (var sectie in new[] { "gerechten", "suggesties" })
            {
                if (!doc.RootElement.TryGetProperty(sectie, out var el) ||
                    el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var gerecht in el.EnumerateObject())
                {
                    if (metRecept.Contains(gerecht.Name))
                    {
                        continue;
                    }
                    var namen = new List<string>();
                    foreach (var ing in gerecht.Value.EnumerateArray())
                    {
                        var naam = ing.ValueKind switch
                        {
                            JsonValueKind.String => ing.GetString(),
                            JsonValueKind.Object when ing.TryGetProperty("naam", out var n) => n.GetString(),
                            _ => null,
                        };
                        if (!string.IsNullOrWhiteSpace(naam))
                        {
                            namen.Add(naam);
                        }
                    }
                    if (namen.Count > 0)
                    {
                        resultaat.Add((gerecht.Name, namen));
                    }
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: dan niets aanvullen.
        }
        return resultaat;
    }

    /// <summary>Laat Claude één recept schrijven en bewaart het; true bij succes.</summary>
    private static async Task<bool> MaakReceptAsync(
        string naam, List<string> ingredienten, CancellationToken ct)
    {
        try
        {
            var prompt = $$"""
                Je bent een ervaren kok. Stel een eenvoudig recept voor het gerecht "{{naam}}" op,
                op basis van deze ingrediënten:
                {{string.Join("\n", ingredienten.Select(i => "- " + i))}}

                Maarten eet glutenvrij: kies waar relevant de glutenvrije aanpak.

                Antwoord uitsluitend met JSON, exact in dit formaat (geen extra tekst):
                {"recept": "<bereidingswijze in het Nederlands, 3 tot 6 korte stappen, gescheiden door \n>", "minuten": <bereidingstijd in hele minuten>, "personen": <aantal personen als geheel getal>}
                """;
            var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
            using var doc = ClaudeDrafter.ParseJson(output);
            var wortel = doc.RootElement;
            var tekst = wortel.TryGetProperty("recept", out var r) ? r.GetString() ?? "" : "";
            if (tekst.Length == 0)
            {
                return false;
            }
            var minuten = wortel.TryGetProperty("minuten", out var m) && m.TryGetInt32(out var mv) ? mv : 30;
            var personen = wortel.TryGetProperty("personen", out var p) && p.TryGetInt32(out var pv) ? pv : 4;

            if (JsonNode.Parse(File.ReadAllText(GerechtenFile)) is not { } bestand)
            {
                return false;
            }
            if (bestand["recepten"] is not JsonObject recepten)
            {
                bestand["recepten"] = recepten = new JsonObject();
            }
            recepten[naam] = new JsonObject
            {
                ["tekst"] = tekst.Replace("\\n", "\n").Trim(),
                ["minuten"] = Math.Clamp(minuten, 0, 480),
                ["personen"] = Math.Clamp(personen, 1, 20),
            };
            File.WriteAllText(GerechtenFile,
                bestand.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string LaadState()
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

    private static void BewaarState(string datum)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(datum));
        }
        catch
        {
            // Best effort.
        }
    }
}
