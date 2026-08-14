using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Verborgen prestaties: badges waarvan je de voorwaarde pas ziet als je hem haalt — de lol
/// zit in het ontdekken. Gebeurtenissen uit de cockpit (afvinken, archiveren, deploys,
/// snoozes, …) druppelen hier binnen; levert een gebeurtenis een nieuwe prestatie op, dan
/// verschijnt een 🏆-toast met confetti. De prijzenkast toont behaalde prestaties met datum
/// en de rest als "???". State in %APPDATA%\WorkManager\prestaties.json.
/// </summary>
public static class Prestaties
{
    public sealed record Prestatie(string Id, string Naam, string Omschrijving);

    /// <summary>Alle prestaties — de omschrijving blijft geheim tot hij behaald is.</summary>
    public static readonly Prestatie[] Alle =
    {
        new("vroege-vogel", "🌅 Dauwtrapper", "Een taak afgevinkt vóór 07:30"),
        new("middernacht", "🌙 Middernachtwerker", "Nog iets afgehandeld na 23:30"),
        new("bliksem", "⚡ Bliksemantwoord", "Een bericht beantwoord binnen 60 seconden"),
        new("ijskoud", "🧊 IJskoude discipline", "Zeven dagen zonder één snooze"),
        new("dubbele-lancering", "🚀 Dubbele lancering", "Twee deploys binnen één uur"),
        new("grote-schoonmaak", "🧹 Grote schoonmaak", "Tien berichten gearchiveerd op één dag"),
        new("groene-week", "🌿 Groene week", "Inbox-zero-reeks van vijf werkdagen"),
        new("brandweer", "🚒 Brandweerman", "Een storingstaak zelf afgevinkt"),
        new("fijnhakker", "🧩 Fijnhakker", "Een uitgestelde taak in blokjes laten hakken"),
        new("cheatcode", "🎮 Old school", "De Konami-code ontdekt"),
        new("weekendwacht", "🏖️ Weekendwacht", "Een heel weekend niets in WorkManager gedaan"),
    };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "prestaties.json");

    private sealed class State
    {
        public Dictionary<string, string> Behaald { get; set; } = new(); // id → datum
        public string LaatsteSnooze { get; set; } = "";
        public string EersteGebeurtenis { get; set; } = "";
        public int ArchiefVandaag { get; set; }
        public string ArchiefDag { get; set; } = "";
        public List<DateTimeOffset> Deploys { get; set; } = new();
        public List<string> ActieveDagen { get; set; } = new(); // dagen met activiteit (max 21)
    }

    /// <summary>Behaalde prestaties (id → datum) voor de prijzenkast.</summary>
    public static Dictionary<string, string> Behaald() => Laad().Behaald;

    /// <summary>
    /// Meldt een gebeurtenis; soorten: "taak-af" (met taaktekst als detail), "archief",
    /// "antwoord" (detail = leeftijd in seconden), "snooze", "deploy", "blokjes",
    /// "inboxzero" (detail = reekslengte), "konami". Nieuwe prestaties worden meteen
    /// gevierd op <paramref name="eigenaar"/>.
    /// </summary>
    public static void Gebeurtenis(Form? eigenaar, string soort, string detail = "")
    {
        try
        {
            var state = Laad();
            var nu = DateTime.Now;
            var vandaag = DateOnly.FromDateTime(nu).ToString("yyyy-MM-dd");
            if (state.EersteGebeurtenis.Length == 0)
            {
                state.EersteGebeurtenis = vandaag;
            }
            if (!state.ActieveDagen.Contains(vandaag))
            {
                state.ActieveDagen.Add(vandaag);
                if (state.ActieveDagen.Count > 21)
                {
                    state.ActieveDagen.RemoveAt(0);
                }
            }
            var nieuw = new List<Prestatie>();
            void Ken(string id)
            {
                if (!state.Behaald.ContainsKey(id) &&
                    Alle.FirstOrDefault(p => p.Id == id) is { } p)
                {
                    state.Behaald[id] = vandaag;
                    nieuw.Add(p);
                }
            }

            switch (soort)
            {
                case "taak-af":
                    if (nu.TimeOfDay < new TimeSpan(7, 30, 0))
                    {
                        Ken("vroege-vogel");
                    }
                    if (detail.StartsWith(AlarmMails.TaakPrefix, StringComparison.Ordinal) ||
                        detail.StartsWith(DataCheckRadar.TaakPrefix, StringComparison.OrdinalIgnoreCase) ||
                        detail.StartsWith("🔴", StringComparison.Ordinal))
                    {
                        Ken("brandweer");
                    }
                    break;
                case "archief":
                    if (state.ArchiefDag != vandaag)
                    {
                        state.ArchiefDag = vandaag;
                        state.ArchiefVandaag = 0;
                    }
                    if (++state.ArchiefVandaag >= 10)
                    {
                        Ken("grote-schoonmaak");
                    }
                    break;
                case "antwoord":
                    if (double.TryParse(detail, out var sec) && sec is > 0 and <= 60)
                    {
                        Ken("bliksem");
                    }
                    break;
                case "snooze":
                    state.LaatsteSnooze = vandaag;
                    break;
                case "deploy":
                    state.Deploys.Add(DateTimeOffset.Now);
                    state.Deploys.RemoveAll(d => DateTimeOffset.Now - d > TimeSpan.FromHours(2));
                    if (state.Deploys.Count(d => DateTimeOffset.Now - d <= TimeSpan.FromHours(1)) >= 2)
                    {
                        Ken("dubbele-lancering");
                    }
                    break;
                case "blokjes":
                    Ken("fijnhakker");
                    break;
                case "inboxzero":
                    if (int.TryParse(detail, out var reeks) && reeks >= 5)
                    {
                        Ken("groene-week");
                    }
                    break;
                case "konami":
                    Ken("cheatcode");
                    break;
            }

            // Nachtwerk telt bij elke actieve gebeurtenis.
            if (soort is "taak-af" or "archief" or "antwoord" && nu.Hour >= 23 && nu.Minute >= 30)
            {
                Ken("middernacht");
            }
            // Zeven dagen zonder snooze — er moet wel ooit gesnoozed (of lang gewerkt) zijn,
            // anders is het geen discipline maar een verse installatie.
            if (DateOnly.TryParse(
                    state.LaatsteSnooze.Length > 0 ? state.LaatsteSnooze : state.EersteGebeurtenis,
                    out var sinds) &&
                DateOnly.FromDateTime(nu).DayNumber - sinds.DayNumber >= 7)
            {
                Ken("ijskoud");
            }
            // Weekendwacht: doordeweeks kijken of afgelopen zaterdag én zondag stil waren,
            // terwijl de week ervoor wél gewerkt werd.
            if (nu.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var d = DateOnly.FromDateTime(nu);
                var zondag = d.AddDays(-((int)d.DayOfWeek == 0 ? 7 : (int)d.DayOfWeek));
                var zaterdag = zondag.AddDays(-1);
                if (state.ActieveDagen.Any(a => DateOnly.TryParse(a, out var ad) && ad < zaterdag) &&
                    !state.ActieveDagen.Contains(zaterdag.ToString("yyyy-MM-dd")) &&
                    !state.ActieveDagen.Contains(zondag.ToString("yyyy-MM-dd")))
                {
                    Ken("weekendwacht");
                }
            }

            Bewaar(state);
            if (nieuw.Count > 0 && eigenaar is { IsDisposed: false })
            {
                Confetti.Vier(eigenaar);
                foreach (var p in nieuw)
                {
                    Toast.Toon(eigenaar,
                        $"🏆 Prestatie ontgrendeld: {p.Naam} — {p.Omschrijving}", Fluent.Ster);
                }
            }
        }
        catch
        {
            // Prestaties zijn sfeer: nooit een foutmelding waard.
        }
    }

    private static State Laad()
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
            // Onleesbaar: prijzenkast begint opnieuw — pijnlijk, maar geen drama.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
