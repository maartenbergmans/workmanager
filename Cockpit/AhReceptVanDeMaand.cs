using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkManager;

/// <summary>
/// Het HelloFresh-gevoel van een menu dat blijft verrassen: één keer per maand laat de app
/// Claude ('claude -p' op het abonnement) een nieuw seizoensrecept verzinnen, opgebouwd uit
/// producten die Maarten al eens bestelde (de producttabel). Het resultaat komt als extra
/// suggestie (mét recept) in ah-gerechten.json en draait vanzelf mee in de weekrotatie.
/// Mislukt de poging (Claude niet beschikbaar, onbruikbare JSON), dan volgt morgen een
/// nieuwe; per dag hooguit één.
/// </summary>
public static class AhReceptVanDeMaand
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "ah-recept-maand.json");
    private static readonly string GerechtenFile = Path.Combine(DataDir, "ah-gerechten.json");

    private static bool _bezig;

    /// <summary>Draait de maandelijkse receptgeneratie als hij aan de beurt is; stil bij tegenslag.</summary>
    public static async Task ZorgVoorAsync(Form eigenaar, CancellationToken ct)
    {
        var maand = DateTime.Now.ToString("yyyy-MM");
        var vandaag = DateTime.Today.ToString("O");
        var state = LaadState();
        if (_bezig || state.LaatsteSuccesMaand == maand || state.LaatstePoging == vandaag ||
            DateTime.Now.Hour < 9 || !File.Exists(GerechtenFile))
        {
            return;
        }
        _bezig = true;
        try
        {
            state.LaatstePoging = vandaag;
            BewaarState(state);

            var bestaande = BestaandeNamen();
            var producten = AhProducten.Alles.Select(p => p.Naam).ToList();
            if (producten.Count == 0)
            {
                return;
            }
            var maandNaam = System.Globalization.CultureInfo.GetCultureInfo("nl-BE")
                .DateTimeFormat.GetMonthName(DateTime.Now.Month);
            var prompt = $$"""
                Je bent een ervaren kok. Verzin één nieuw, eenvoudig avondmaalgerecht voor een
                gezin (seizoen: {{maandNaam}}), uitsluitend met producten uit deze lijst
                (gebruik de namen EXACT zoals ze er staan, 5 tot 9 stuks):
                {{string.Join("\n", producten.Select(p => "- " + p))}}

                Deze gerechten bestaan al — verzin iets anders:
                {{string.Join("\n", bestaande.Select(n => "- " + n))}}

                Maarten eet glutenvrij: kies waar relevant de glutenvrije aanpak.

                Antwoord uitsluitend met JSON, exact in dit formaat (geen extra tekst):
                {"naam": "<korte gerechtnaam>", "ingredienten": ["<exacte productnaam>", …], "recept": "<3 tot 6 korte stappen, gescheiden door \n>", "minuten": <geheel getal>, "personen": <geheel getal>}
                """;

            var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
            using var doc = ClaudeDrafter.ParseJson(output);
            var wortel = doc.RootElement;
            var naam = wortel.TryGetProperty("naam", out var n) ? n.GetString()?.Trim() ?? "" : "";
            var recept = wortel.TryGetProperty("recept", out var r) ? r.GetString() ?? "" : "";
            var minuten = wortel.TryGetProperty("minuten", out var m) && m.TryGetInt32(out var mv) ? mv : 30;
            var personen = wortel.TryGetProperty("personen", out var p) && p.TryGetInt32(out var pv) ? pv : 4;
            var ingredienten = wortel.TryGetProperty("ingredienten", out var lijst) &&
                lijst.ValueKind == JsonValueKind.Array
                    ? lijst.EnumerateArray().Select(el => el.GetString()).OfType<string>().ToList()
                    : new List<string>();
            // Alleen producten die echt in de tabel staan; Claude verzint er soms eentje bij.
            ingredienten = ingredienten
                .Select(i => producten.FirstOrDefault(pr => pr.Equals(i, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>()
                .Distinct()
                .ToList();
            if (naam.Length == 0 || recept.Length == 0 || ingredienten.Count < 3 ||
                bestaande.Contains(naam))
            {
                return; // onbruikbaar antwoord: morgen een nieuwe poging
            }

            if (!VoegSuggestieToe(naam, ingredienten, recept, minuten, personen))
            {
                return;
            }
            state.LaatsteSuccesMaand = maand;
            BewaarState(state);
            if (!eigenaar.IsDisposed)
            {
                eigenaar.BeginInvoke(() => Toast.Toon(eigenaar,
                    $"Nieuw recept van de maand: {naam} — staat bij de AH-suggesties",
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

    /// <summary>Alle bestaande gerecht- en suggestienamen (om herhaling te vermijden).</summary>
    private static HashSet<string> BestaandeNamen()
    {
        var namen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(GerechtenFile));
            foreach (var sectie in new[] { "gerechten", "suggesties" })
            {
                if (doc.RootElement.TryGetProperty(sectie, out var el) &&
                    el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        namen.Add(prop.Name);
                    }
                }
            }
        }
        catch
        {
            // Leeg: dan is elke naam nieuw.
        }
        return namen;
    }

    /// <summary>Schrijft de nieuwe suggestie + het recept in ah-gerechten.json (via JsonNode,
    /// zodat de rest van het bestand onaangeroerd blijft).</summary>
    private static bool VoegSuggestieToe(
        string naam, List<string> ingredienten, string recept, int minuten, int personen)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(GerechtenFile)) is not { } wortel ||
                wortel["suggesties"] is not JsonObject suggesties)
            {
                return false;
            }
            var lijst = new JsonArray();
            foreach (var ing in ingredienten)
            {
                lijst.Add(ing);
            }
            suggesties[naam] = lijst;
            if (wortel["recepten"] is not JsonObject recepten)
            {
                wortel["recepten"] = recepten = new JsonObject();
            }
            recepten[naam] = new JsonObject
            {
                ["tekst"] = recept.Replace("\\n", "\n").Trim(),
                ["minuten"] = Math.Clamp(minuten, 0, 480),
                ["personen"] = Math.Clamp(personen, 1, 20),
            };
            File.WriteAllText(GerechtenFile,
                wortel.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- state ----------

    private sealed class State
    {
        public string LaatsteSuccesMaand { get; set; } = "";
        public string LaatstePoging { get; set; } = "";
    }

    private static State LaadState()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Als "nog nooit" behandelen.
        }
        return new State();
    }

    private static void BewaarState(State state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
