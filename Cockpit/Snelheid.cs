using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De snelheidsduivel: klokt de tijd tussen het binnenkomen van een bericht en het moment
/// waarop Maarten hem afhandelt (beantwoorden of archiveren, alleen handmatige acties).
/// Nieuw record = meteen een feestje; op vrijdagmiddag volgt één keer het weekgemiddelde.
/// Alles best effort, persistent in %APPDATA%\WorkManager\snelheid.json.
/// </summary>
public static class Snelheid
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "snelheid.json");

    private sealed class Meting
    {
        public DateTimeOffset Moment { get; set; }
        public double Seconden { get; set; }
    }

    private sealed class State
    {
        public double RecordSeconden { get; set; }
        public string RecordDatum { get; set; } = "";
        public List<Meting> Metingen { get; set; } = new();
        public string WeekGemeld { get; set; } = "";
    }

    /// <summary>
    /// Registreert een afhandeling en geeft een feesttekst terug bij een nieuw record (anders
    /// null). Alleen berichten die minder dan 48 uur oud zijn tellen — wat al dagen in de
    /// lijst stond, is geen snelheidswedstrijd meer.
    /// </summary>
    public static string? Registreer(MailBericht bericht, string actie)
    {
        try
        {
            var leeftijd = DateTimeOffset.Now - bericht.Datum;
            if (leeftijd < TimeSpan.FromSeconds(5) || leeftijd > TimeSpan.FromHours(48))
            {
                return null; // klokfout of oud zeer: telt niet mee
            }
            var state = Laad();
            state.Metingen.Add(new Meting
            {
                Moment = DateTimeOffset.Now,
                Seconden = leeftijd.TotalSeconds,
            });
            // Alleen de laatste 300 metingen bewaren: ruim genoeg voor weekgemiddelden.
            if (state.Metingen.Count > 300)
            {
                state.Metingen.RemoveRange(0, state.Metingen.Count - 300);
            }
            string? melding = null;
            // Een record is pas leuk als er iets te verslaan valt: minimaal 5 metingen.
            if (state.Metingen.Count >= 5 &&
                (state.RecordSeconden <= 0 || leeftijd.TotalSeconds < state.RecordSeconden))
            {
                var oud = state.RecordSeconden;
                state.RecordSeconden = leeftijd.TotalSeconds;
                state.RecordDatum = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
                if (oud > 0)
                {
                    melding = $"⚡ Nieuw snelheidsrecord: {actie} in {Formaat(leeftijd)}!";
                }
            }
            else if (state.RecordSeconden <= 0 && state.Metingen.Count >= 5)
            {
                // Eerste record vastleggen zodra er genoeg metingen zijn (stil).
                var snelste = state.Metingen.Min(m => m.Seconden);
                state.RecordSeconden = snelste;
                state.RecordDatum = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            }
            Bewaar(state);
            return melding;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Op vrijdag vanaf 15:00 één keer per week: het gemiddelde van deze week plus het
    /// staande record. Null als het nog geen tijd is of er niets te melden valt.
    /// </summary>
    public static string? WeekOverzicht()
    {
        try
        {
            var nu = DateTime.Now;
            if (nu.DayOfWeek != DayOfWeek.Friday || nu.Hour < 15)
            {
                return null;
            }
            var week = $"{System.Globalization.ISOWeek.GetYear(nu)}-" +
                       $"{System.Globalization.ISOWeek.GetWeekOfYear(nu)}";
            var state = Laad();
            if (state.WeekGemeld == week)
            {
                return null;
            }
            var maandag = DateOnly.FromDateTime(nu).AddDays(-(int)nu.DayOfWeek + 1);
            var deesWeek = state.Metingen
                .Where(m => DateOnly.FromDateTime(m.Moment.LocalDateTime) >= maandag)
                .ToList();
            if (deesWeek.Count < 3)
            {
                return null; // te weinig om een gemiddelde iets te laten zeggen
            }
            state.WeekGemeld = week;
            Bewaar(state);
            var gemiddeld = TimeSpan.FromSeconds(deesWeek.Average(m => m.Seconden));
            var record = TimeSpan.FromSeconds(state.RecordSeconden);
            return $"⚡ Deze week {deesWeek.Count} berichten afgehandeld, gemiddeld binnen " +
                   $"{Formaat(gemiddeld)} — record blijft {Formaat(record)}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"38 s", "12 min" of "3u20" — leesbaar zonder komma's.</summary>
    private static string Formaat(TimeSpan t) => t.TotalSeconds switch
    {
        < 90 => $"{(int)t.TotalSeconds} s",
        < 5400 => $"{(int)Math.Round(t.TotalMinutes)} min",
        _ => $"{(int)t.TotalHours}u{t.Minutes:00}",
    };

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
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
    }
}
