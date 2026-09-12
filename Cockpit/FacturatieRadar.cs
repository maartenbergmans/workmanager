using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Facturatieradar: haalt via urbanadmin (/workmanager/facturatie) per factureerbaar project de
/// werkuren op die nog aan geen factuurregel hangen, met een geschat bedrag. Rond de
/// maandwissel (laatste drie en eerste twee werkdagen, vanaf 10 u) volgt één melding per dag
/// met het totaal en de grootste posten; via ⋯ "Nog te factureren…" altijd op te vragen.
/// Het overzicht staat ook in journaal\facturatie.md.
/// <para>Zolang het endpoint niet op productie staat (404), blijft de radar stil.</para>
/// </summary>
public static class FacturatieRadar
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager", "facturatie-radar.json");

    public static readonly string Overzicht = Path.Combine(Werkjournaal.Map, "facturatie.md");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly CultureInfo Nl = new("nl-BE");

    public sealed class Post
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("klant")] public string? Klant { get; set; }
        [JsonPropertyName("naam")] public string Naam { get; set; } = "";
        [JsonPropertyName("tarief")] public double Tarief { get; set; }
        [JsonPropertyName("isdagtarief")] public bool IsDagtarief { get; set; }
        [JsonPropertyName("minuten")] public int Minuten { get; set; }
        [JsonPropertyName("dagen")] public int Dagen { get; set; }
        [JsonPropertyName("oudste")] public string? Oudste { get; set; }
        [JsonPropertyName("bedrag")] public double Bedrag { get; set; }

        public string Label => $"{(Klant is { Length: > 0 } k ? ProjectCatalogus.ZonderRechtsvorm(k) : "Intern")} · {Naam}";
    }

    private sealed class Antwoord
    {
        [JsonPropertyName("projecten")] public List<Post> Projecten { get; set; } = new();
        [JsonPropertyName("totaal")] public double Totaal { get; set; }
    }

    private sealed class State
    {
        public string LaatsteMelding { get; set; } = "";
    }

    /// <summary>Haalt de open posten op (null bij geen verbinding of een ontbrekend endpoint).</summary>
    public static async Task<List<Post>?> HaalOpAsync(CancellationToken ct, string? baseUrl = null, string? token = null)
    {
        var settings = LaunchConfig.LoadOrCreate().Timesheets;
        baseUrl ??= settings?.BaseUrl;
        token ??= settings?.Token;
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token))
        {
            return null;
        }
        try
        {
            using var response = await Http.GetAsync(
                $"{baseUrl.TrimEnd('/')}/workmanager/facturatie/{token}?gebruiker_id={settings?.GebruikerId ?? 1}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var antwoord = JsonSerializer.Deserialize<Antwoord>(await response.Content.ReadAsStringAsync(ct));
            var posten = antwoord?.Projecten ?? new List<Post>();
            SchrijfOverzicht(posten);
            return posten;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Vanuit de 10-minutentik: rond de maandwissel één melding per dag.</summary>
    public static async Task ZorgVoorAsync(Action<string, string> toon)
    {
        var nu = DateTime.Now;
        if (nu.Hour < 10 || nu.Hour >= 18 || !RondMaandwissel(DateOnly.FromDateTime(nu)) || NietStoren.Actief)
        {
            return;
        }
        var state = Laad();
        var vandaag = nu.ToString("yyyy-MM-dd");
        if (state.LaatsteMelding == vandaag)
        {
            return;
        }
        state.LaatsteMelding = vandaag; // ook bij een fout: niet elke tien minuten opnieuw proberen
        Bewaar(state);
        var posten = await HaalOpAsync(CancellationToken.None);
        if (posten is not { Count: > 0 })
        {
            return;
        }
        var totaal = posten.Sum(p => p.Bedrag);
        var top = string.Join(", ", posten.Take(3).Select(p =>
            $"{p.Label} {Euro(p.Bedrag)} ({p.Minuten / 60.0:0.#} u sinds {Datum(p.Oudste)})"));
        toon($"🧾 Nog te factureren: ± {Euro(totaal)}", top + ". Klik voor het volledige overzicht.");
    }

    /// <summary>Laatste drie en eerste twee werkdagen van een maand.</summary>
    public static bool RondMaandwissel(DateOnly dag)
    {
        if (dag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }
        static bool Werkdag(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var laatste = new DateOnly(dag.Year, dag.Month, DateTime.DaysInMonth(dag.Year, dag.Month));
        var achteraan = Enumerable.Range(0, 10).Select(i => laatste.AddDays(-i)).Where(Werkdag).Take(3);
        var eerste = new DateOnly(dag.Year, dag.Month, 1);
        var vooraan = Enumerable.Range(0, 10).Select(i => eerste.AddDays(i)).Where(Werkdag).Take(2);
        return achteraan.Contains(dag) || vooraan.Contains(dag);
    }

    private static void SchrijfOverzicht(List<Post> posten)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Nog te factureren — stand {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"Totaal (schatting): {Euro(posten.Sum(p => p.Bedrag))}");
            sb.AppendLine();
            foreach (var p in posten)
            {
                sb.AppendLine($"- {p.Label}: {Euro(p.Bedrag)} — {p.Minuten / 60.0:0.#} u op {p.Dagen} dag(en) sinds {Datum(p.Oudste)} " +
                              $"({(p.IsDagtarief ? $"dagtarief {Euro(p.Tarief)}, gerekend als uren/8" : $"uurtarief {Euro(p.Tarief)}")})");
            }
            if (posten.Count == 0)
            {
                sb.AppendLine("Alles is gefactureerd. 🎉");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Overzicht)!);
            File.WriteAllText(Overzicht, sb.ToString());
        }
        catch
        {
            // Best effort.
        }
    }

    private static string Euro(double bedrag) => bedrag.ToString("C0", Nl);

    private static string Datum(string? iso) =>
        DateOnly.TryParse(iso, out var d) ? d.ToString("d/M", Nl) : "?";

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
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
