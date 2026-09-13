using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Verjaardagsmodus voor het gezin (jarigen met relatie dochter/zoon/partner in
/// verjaardagen.json). De dag ervoor: een pannenkoekencheck — traditie rond de verjaardagen
/// van Emilia en Lisa — met een taak als de laatste AH-bestelling de ingrediënten niet had.
/// Op de dag zelf: een feestbanner bovenaan de cockpit en één keer confetti.
/// <para>State: %APPDATA%\WorkManager\feestdag.json (wat al gemeld/gevierd is).</para>
/// </summary>
public static class Feestdag
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "feestdag.json");

    /// <summary>Het pannenkoekenrecept uit ah-gerechten.json (glutenvrije bloem, dus ook voor Hilke).</summary>
    private static readonly (string Wat, string Trefwoord)[] Pannenkoekbasis =
    {
        ("glutenvrije bloem (Freee Plain white flour)", "flour"),
        ("eieren", "eieren"),
        ("melk", "melk"),
        ("boter", "boter"),
    };

    private sealed class State
    {
        public List<string> Gemeld { get; set; } = new();   // "id|jaar|voor" of "id|jaar|feest"
    }

    /// <summary>De gezinsleden die vandaag jarig zijn.</summary>
    public static List<Jarige> VandaagJarig() => Gezin().Where(j => j.DagenTot(Vandaag) == 0).ToList();

    /// <summary>Feesttekst voor de banner, of leeg als er niemand jarig is.</summary>
    public static string BannerTekst()
    {
        var jarigen = VandaagJarig();
        if (jarigen.Count == 0)
        {
            return "";
        }
        var namen = string.Join(" en ", jarigen.Select(j =>
            j.WordtOp(Vandaag) is { } leeftijd ? $"{j.Naam} ({leeftijd})" : j.Naam));
        return $"🎂  Vandaag is {namen} jarig — pannenkoekendag! 🥞  Hiep hiep hoera!  🎈";
    }

    /// <summary>Is de banner van vandaag al weggeklikt?</summary>
    public static bool BannerWeggeklikt() => Laad().Gemeld.Contains(BannerSleutel());

    /// <summary>De banner voor de rest van vandaag verbergen (✕ in de cockpit).</summary>
    public static void KlikBannerWeg()
    {
        var state = Laad();
        var sleutel = BannerSleutel();
        if (!state.Gemeld.Contains(sleutel))
        {
            state.Gemeld.Add(sleutel);
            Bewaar(state);
        }
    }

    private static string BannerSleutel() => $"{Vandaag:yyyy-MM-dd}|banner-weg";

    /// <summary>True de eerste keer vandaag (voor de confetti bij het openen van de cockpit).</summary>
    public static bool EersteKeerVieren()
    {
        var jarigen = VandaagJarig();
        if (jarigen.Count == 0)
        {
            return false;
        }
        var state = Laad();
        var sleutel = $"{string.Join("+", jarigen.Select(j => j.Id))}|{Vandaag.Year}|feest";
        if (state.Gemeld.Contains(sleutel))
        {
            return false;
        }
        state.Gemeld.Add(sleutel);
        Bewaar(state);
        return true;
    }

    /// <summary>
    /// De dag vóór een gezinsverjaardag (vanaf 8 u, één keer): melding + pannenkoekentaak als
    /// de laatste AH-bestelling van de voorbije week de ingrediënten niet bevatte.
    /// </summary>
    public static void ZorgVoorDagErvoor(Action? openTaken)
    {
        if (DateTime.Now.Hour < 8)
        {
            return;
        }
        var state = Laad();
        foreach (var jarige in Gezin().Where(j => j.DagenTot(Vandaag) == 1))
        {
            var sleutel = $"{jarige.Id}|{Vandaag.AddDays(1).Year}|voor";
            if (state.Gemeld.Contains(sleutel))
            {
                continue;
            }
            state.Gemeld.Add(sleutel);
            Bewaar(state);

            var ontbreekt = OntbrekendInLaatsteBestelling(out var besteldOp);
            var wordt = jarige.WordtOp(Vandaag) is { } l ? $" ({l} jaar)" : "";
            if (ontbreekt.Count == 0)
            {
                TrayMelding.Toon($"🎂 Morgen is {jarige.Naam} jarig{wordt}",
                    "Pannenkoekentraditie: alles zat in de AH-bestelling. Morgen feestmodus in de cockpit! 🥞",
                    null, 12000);
                continue;
            }
            var data = MijnTaakStore.Load();
            data.Taken.Add(new MijnTaak
            {
                Tekst = $"🥞 Pannenkoeken voor {Bezittelijk(jarige.Naam)} verjaardag morgen: {string.Join(", ", ontbreekt)} in huis?",
                Categorie = data.Categorieen.FirstOrDefault(c => c.StartsWith("Priv", StringComparison.OrdinalIgnoreCase))
                            ?? data.Categorieen.FirstOrDefault() ?? "",
                Prioriteit = 1,
                Deadline = Vandaag,
            });
            MijnTaakStore.Save(data);
            TrayMelding.Toon($"🎂 Morgen is {jarige.Naam} jarig{wordt}",
                $"Pannenkoekentraditie! De laatste AH-bestelling{(besteldOp is { } b ? $" ({b:d/M})" : "")} " +
                $"had geen {string.Join(", ", ontbreekt)}. Staat als taak klaar.",
                openTaken, 15000);
        }
    }

    private static List<string> OntbrekendInLaatsteBestelling(out DateTimeOffset? besteldOp)
    {
        besteldOp = null;
        var ingredienten = new List<string>();
        try
        {
            var pad = Path.Combine(DataDir, "ah-bestelling.json");
            if (File.Exists(pad))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pad));
                if (doc.RootElement.TryGetProperty("datum", out var d) && d.TryGetDateTimeOffset(out var dt))
                {
                    besteldOp = dt;
                }
                if (besteldOp is { } b && DateTimeOffset.Now - b < TimeSpan.FromDays(7) &&
                    doc.RootElement.TryGetProperty("ingredienten", out var lijst) && lijst.ValueKind == JsonValueKind.Array)
                {
                    ingredienten = lijst.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                }
            }
        }
        catch
        {
            // Onleesbaar: dan geldt alles als ontbrekend.
        }
        return Pannenkoekbasis
            .Where(p => !ingredienten.Any(i => i.Contains(p.Trefwoord, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Wat)
            .ToList();
    }

    private static DateOnly Vandaag => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>"Emilia" → "Emilia's", "Lisa" → "Lisa's", "Jan" → "Jans" (Nederlandse spelling).</summary>
    private static string Bezittelijk(string naam) =>
        naam.Length > 0 && "aeiouyé".Contains(char.ToLowerInvariant(naam[^1])) ? naam + "'s" : naam + "s";

    private static IEnumerable<Jarige> Gezin()
    {
        try
        {
            return Verjaardagen.Load().Jarigen.Where(j =>
                System.Text.RegularExpressions.Regex.IsMatch(j.Relatie,
                    @"dochter|zoon|partner|echtgeno|vrouw|\bman\b|kind", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToList();
        }
        catch
        {
            return Array.Empty<Jarige>();
        }
    }

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            if (state.Gemeld.Count > 100)
            {
                state.Gemeld.RemoveRange(0, state.Gemeld.Count - 100);
            }
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
