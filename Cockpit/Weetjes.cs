using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// "Wist je dat…": verrassende feitjes uit je eigen werkdata (werkjournaal, minuutlog,
/// Claude-opdrachten, timesheets, taken), twee keer per werkdag op een willekeurig moment
/// tussen 9:30 en 17:30. Alleen als je achter de pc zit en "niet storen" uit staat — een
/// weetje wordt dan uitgesteld, nooit ingehaald in de samenvatting. Hetzelfde weetje komt
/// niet terug zolang de cijfers erachter niet veranderd zijn.
/// <para>State: %APPDATA%\WorkManager\weetjes.json.</para>
/// </summary>
public static class Weetjes
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "weetjes.json");
    private static readonly CultureInfo Nl = new("nl-BE");

    private sealed class State
    {
        public string Dag { get; set; } = "";
        public List<DateTimeOffset> Momenten { get; set; } = new();
        public int Getoond { get; set; }
        public List<string> Gezien { get; set; } = new(); // weetje-id's (met periode erin)
    }

    /// <summary>Vanuit de 10-minutentik van de tray-app: toont een weetje als het moment daar is.</summary>
    public static void ZorgVoor()
    {
        try
        {
            var nu = DateTimeOffset.Now;
            if (nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return;
            }
            var state = Laad();
            var vandaag = nu.ToString("yyyy-MM-dd");
            if (state.Dag != vandaag)
            {
                // Twee willekeurige momenten, minstens drie uur uit elkaar.
                var rnd = new Random();
                var eerste = nu.Date.AddHours(9.5).AddMinutes(rnd.Next(0, 180));
                var tweede = eerste.AddHours(3).AddMinutes(rnd.Next(0, 120));
                state = new State
                {
                    Dag = vandaag,
                    Momenten = new() { new DateTimeOffset(eerste), new DateTimeOffset(tweede) },
                    Gezien = state.Gezien,
                };
                Bewaar(state);
            }
            if (state.Getoond >= state.Momenten.Count || nu < state.Momenten[state.Getoond] ||
                nu.Hour >= 18 || NietStoren.Actief || IdleSeconden() > 120)
            {
                return;
            }
            // De hint naar de geheime missie gaat voor, zolang die nooit ontdekt werd.
            var (id, tekst) = GeheimeMissie.Hint() is { } hint ? ("hint", hint) : Kies(state);
            if (tekst.Length == 0)
            {
                return;
            }
            state.Getoond++;
            if (id != "hint")
            {
                state.Gezien.Add(id);
                if (state.Gezien.Count > 300)
                {
                    state.Gezien.RemoveRange(0, state.Gezien.Count - 300);
                }
            }
            Bewaar(state);
            TrayMelding.Toon(id == "hint" ? "🕵️ Psst…" : "💡 Wist je dat…", tekst, null, 14000);
        }
        catch
        {
            // Een weetje is nooit belangrijk genoeg om iets te breken.
        }
    }

    /// <summary>Alle weetjes die nu te maken zijn (voor tests en voor de keuze).</summary>
    public static List<(string Id, string Tekst)> Alle()
    {
        var lijst = new List<(string, string)>();
        void Probeer(Func<(string, string)?> maak)
        {
            try
            {
                if (maak() is { } w && w.Item2.Length > 0)
                {
                    lijst.Add(w);
                }
            }
            catch
            {
                // Dit weetje kan vandaag niet.
            }
        }
        var week = Werkjournaal.Dagen(7);
        var maand = Werkjournaal.Dagen(31);
        var weekId = $"{ISOWeek.GetYear(DateTime.Now)}W{ISOWeek.GetWeekOfYear(DateTime.Now)}";

        Probeer(LangsteBlok);
        Probeer(() =>
        {
            var n = week.Sum(d => d.Claude.Opdrachten);
            if (n < 10)
            {
                return null;
            }
            var top = week.SelectMany(d => d.Claude.PerMap).GroupBy(kv => kv.Key)
                .Select(g => (Map: g.Key, N: g.Sum(kv => kv.Value))).OrderByDescending(x => x.N).First();
            return ($"claude-{weekId}-{n / 25}",
                $"Je gaf Claude de voorbije zeven dagen {n} opdrachten. {100 * top.N / n}% daarvan ging naar {top.Map}.");
        });
        Probeer(() =>
        {
            var uren = LeesClaudeUren();
            if (uren.Count < 30)
            {
                return null;
            }
            var top = uren.GroupBy(h => h).OrderByDescending(g => g.Count()).First();
            return ($"claudeuur-{weekId}",
                $"Je drukste Claude-uur is {top.Key}u–{top.Key + 1}u: daar valt {100 * top.Count() / uren.Count}% van al je opdrachten.");
        });
        Probeer(() =>
        {
            var minuten = week.Sum(d => d.ActiefMinuten);
            if (minuten < 600)
            {
                return null;
            }
            var (film, lengte) = Theme.Palet.Naam switch
            {
                "Godfather" => ("The Godfather", 175),
                "007" => ("Casino Royale", 144),
                "Espresso" => ("keer Cinema Paradiso", 155),
                _ => ("Titanic", 194),
            };
            var keer = minuten / (double)lengte;
            return ($"film-{weekId}-{minuten / 120}",
                $"Je zat de voorbije zeven dagen {minuten / 60} uur actief achter je pc. Dat is {keer:0.#} keer {film.Replace("keer ", "")} na elkaar.");
        });
        Probeer(() =>
        {
            // Vóór 5 u is het nog "gisteren" (doorgewerkt over middernacht), geen vroege start.
            var vroegste = maand.Where(d => d.Eerste is not null && string.CompareOrdinal(d.Eerste, "05:00") >= 0)
                .OrderBy(d => d.Eerste).FirstOrDefault();
            return vroegste is null ? null : ($"vroeg-{vroegste.Datum}",
                $"Je vroegste start van de voorbije maand: {vroegste.Eerste} op {vroegste.Weekdag} {DateOnly.Parse(vroegste.Datum):d/M}.");
        });
        Probeer(() =>
        {
            var laatste = maand.Where(d => d.Laatste is not null).OrderByDescending(d => d.Laatste).FirstOrDefault();
            return laatste is null ? null : ($"laat-{laatste.Datum}",
                $"Je laatste activiteit van de voorbije maand: {laatste.Laatste} op {laatste.Weekdag} {DateOnly.Parse(laatste.Datum):d/M}." +
                (Theme.Palet.Naam == "Godfather" ? " Zelfs de Don slaapt ooit." : Theme.Palet.Naam == "007" ? " Ook 007 moet af en toe slapen." : ""));
        });
        Probeer(() =>
        {
            var na7 = week.Sum(d => d.NaZevenMinuten);
            return na7 < 120 ? null : ($"avond-{weekId}-{na7 / 60}",
                $"De voorbije zeven dagen werkte je {na7 / 60} uur na 19 u. Met die tijd had je {na7 / 45} afleveringen van een serie gekeken.");
        });
        Probeer(() =>
        {
            var top = week.SelectMany(d => d.WorkManagerActies).GroupBy(kv => kv.Key)
                .Select(g => (Knop: g.Key, N: g.Sum(kv => kv.Value))).OrderByDescending(x => x.N).FirstOrDefault();
            return top.N < 5 ? null : ($"knop-{weekId}-{top.Knop}",
                $"Je favoriete WorkManager-knop deze week: \"{top.Knop}\", {top.N} keer aangeklikt.");
        });
        Probeer(() =>
        {
            var af = week.Sum(d => d.TakenAfgevinkt.Count);
            var beste = week.OrderByDescending(d => d.TakenAfgevinkt.Count).FirstOrDefault();
            return af < 5 || beste is null ? null : ($"taken-{weekId}-{af / 5}",
                $"Je vinkte de voorbije zeven dagen {af} taken af. Je beste dag was {beste.Weekdag} met {beste.TakenAfgevinkt.Count}.");
        });
        Probeer(() =>
        {
            var totaal = week.Sum(d => d.ActiefMinuten);
            var contexten = week.SelectMany(d => d.PerContext).GroupBy(kv => kv.Key)
                .Select(g => (Naam: g.Key, Min: g.Sum(kv => kv.Value)))
                .Where(x => !x.Naam.StartsWith("Overig") && !x.Naam.Contains("onbekend") && !x.Naam.StartsWith("Browser"))
                .OrderByDescending(x => x.Min).Take(2).ToList();
            return totaal < 300 || contexten.Count < 2 ? null : ($"context-{weekId}-{contexten[0].Naam}",
                $"Volgens je venstertitels ging deze week het meeste tijd naar {contexten[0].Naam} ({contexten[0].Min / 60} u), " +
                $"gevolgd door {contexten[1].Naam} ({contexten[1].Min / 60} u).");
        });
        Probeer(() =>
        {
            var maandStart = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
            var regels = TimesheetStore.Load().Where(r => r.Datum >= maandStart).ToList();
            var minuten = regels.Sum(r => r.Minuten);
            if (minuten < 600)
            {
                return null;
            }
            var top = regels.GroupBy(r => ProjectCatalogus.LabelVoor(r.Klant))
                .Select(g => (Label: g.Key, Min: g.Sum(r => r.Minuten))).OrderByDescending(x => x.Min).First();
            return ($"ts-{maandStart:yyyyMM}-{minuten / 600}",
                $"Deze maand staat er al {minuten / 60} uur geboekt. De koploper is {top.Label} met {top.Min / 60} uur.");
        });
        Probeer(() =>
        {
            var woord = PopulairsteWoord(week);
            return woord is null ? null : ($"woord-{weekId}-{woord.Value.Woord}",
                $"Het woord dat je deze week het vaakst tegen Claude typte: \"{woord.Value.Woord}\" ({woord.Value.N} keer).");
        });
        return lijst;
    }

    private static (string, string) Kies(State state)
    {
        var nieuw = Alle().Where(w => !state.Gezien.Contains(w.Id)).ToList();
        if (nieuw.Count == 0)
        {
            return ("", "");
        }
        return nieuw[new Random().Next(nieuw.Count)];
    }

    /// <summary>Langste aaneengesloten werkblok (gat ≤ 3 min) uit de minuutlog van de laatste 21 dagen.</summary>
    private static (string, string)? LangsteBlok()
    {
        var pad = Path.Combine(DataDir, "activiteiten-log.jsonl");
        if (!File.Exists(pad))
        {
            return null;
        }
        var tijden = new List<DateTimeOffset>();
        using (var stream = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } regel)
            {
                var m = Regex.Match(regel, "\"t\":\"([^\"]+)\"");
                if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, out var t))
                {
                    tijden.Add(t);
                }
            }
        }
        if (tijden.Count < 60)
        {
            return null;
        }
        tijden.Sort();
        var besteStart = tijden[0];
        var besteDuur = TimeSpan.Zero;
        var start = tijden[0];
        for (var i = 1; i <= tijden.Count; i++)
        {
            if (i == tijden.Count || tijden[i] - tijden[i - 1] > TimeSpan.FromMinutes(3))
            {
                var duur = tijden[i - 1] - start;
                if (duur > besteDuur)
                {
                    besteDuur = duur;
                    besteStart = start;
                }
                if (i < tijden.Count)
                {
                    start = tijden[i];
                }
            }
        }
        if (besteDuur < TimeSpan.FromMinutes(60))
        {
            return null;
        }
        var d = besteStart.LocalDateTime;
        return ($"blok-{d:yyyyMMddHHmm}",
            $"Je langste aaneengesloten werkblok van de voorbije drie weken duurde {(int)besteDuur.TotalHours}u{besteDuur.Minutes:00}: " +
            $"{d.ToString("dddd d/M", Nl)} van {d:HH:mm} tot {d + besteDuur:HH:mm}, zonder één pauze van meer dan drie minuten.");
    }

    private static List<int> LeesClaudeUren()
    {
        var pad = Path.Combine(DataDir, "claude-requests.jsonl");
        var uren = new List<int>();
        if (!File.Exists(pad))
        {
            return uren;
        }
        using var stream = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } regel)
        {
            var m = Regex.Match(regel, "\"t\":\"([^\"]+)\"");
            if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, out var t))
            {
                uren.Add(t.LocalDateTime.Hour);
            }
        }
        return uren;
    }

    private static readonly HashSet<string> Stopwoorden = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "het", "een", "en", "van", "in", "op", "te", "dat", "die", "is", "je", "ik", "niet", "met", "voor",
        "er", "om", "ook", "als", "maar", "dan", "aan", "bij", "nog", "wat", "kan", "zijn", "dit", "wel", "moet",
        "of", "naar", "hoe", "zo", "heb", "hier", "we", "mij", "me", "mijn", "the", "to", "and", "graag", "even",
        "nu", "al", "wordt", "worden", "deze", "daar", "geen", "kun", "doe", "maak", "zou", "was", "waar", "welke",
        "hebben", "heeft", "gaan", "gaat", "meer", "alle", "alles", "eens", "toch", "dus", "want", "via", "uit",
        "task", "with", "this", "that", "from", "have", "your", "what", "when", "will", "into", "then",
    };

    private static (string Woord, int N)? PopulairsteWoord(List<Werkjournaal.DagSamenvatting> week)
    {
        var woorden = week.SelectMany(d => d.Claude.Prompts)
            .Select(p => Regex.Replace(p, @"^\d\d:\d\d \[[^\]]*\] ", ""))
            .Where(p => !p.StartsWith('<'))
            .SelectMany(p => Regex.Matches(p.ToLowerInvariant(), @"\p{L}{4,}").Select(m => m.Value))
            .Where(w => !Stopwoorden.Contains(w))
            .GroupBy(w => w).Select(g => (Woord: g.Key, N: g.Count()))
            .OrderByDescending(x => x.N).FirstOrDefault();
        return woorden.N >= 5 ? woorden : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private static double IdleSeconden()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? (Environment.TickCount - (int)info.dwTime) / 1000.0 : 0;
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
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
