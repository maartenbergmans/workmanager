namespace WorkManager;

/// <summary>
/// De kerntemperatuur van je werk, als knipoog naar de vrieshuizen van Vriesveem: −18 °C of
/// kouder = alles diepgevroren en onder controle; hoe meer er open ligt (taken, taken over
/// tijd, wachtende berichten, werkdagen zonder geboekte uren), hoe verder het ijs smelt.
/// Zichtbaar als thermometer in de cockpitwerkbalk; klikken toont de opbouw.
/// </summary>
public static class KernTemperatuur
{
    private const double Basis = -24;

    public sealed record Meting(double Graden, List<(string Oorzaak, double Bijdrage)> Opbouw)
    {
        public string Icoon => Graden switch
        {
            <= -18 => "🧊",
            <= -8 => "❄️",
            <= 0 => "💧",
            _ => "🔥",
        };

        public string Label => $"{Icoon} {Graden:+0;-0} °C";

        public string Toestand => Graden switch
        {
            <= -18 => "diepgevroren: alles onder controle",
            <= -8 => "koelcel: het houdt stand",
            <= 0 => "aan het ontdooien: tijd om iets weg te werken",
            _ => "het ijs smelt: eerst de achterstand",
        };
    }

    /// <summary>Meet nu, met het aantal berichten dat in de cockpit wacht.</summary>
    public static Meting Meet(int wachtendeBerichten)
    {
        var opbouw = new List<(string, double)>();
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var open = MijnTaakStore.Load().Taken
                .Where(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt).ToList();
            var overTijd = open.Count(t => t.Deadline is { } d && d < vandaag);
            if (open.Count > 0)
            {
                opbouw.Add(($"{open.Count} open taken", open.Count * 0.35));
            }
            if (overTijd > 0)
            {
                opbouw.Add(($"{overTijd} over tijd", overTijd * 1.5));
            }
        }
        catch
        {
            // Zonder taken verder.
        }
        if (wachtendeBerichten > 0)
        {
            opbouw.Add(($"{wachtendeBerichten} wachtende berichten", Math.Min(8, wachtendeBerichten * 0.4)));
        }
        try
        {
            var gemist = UrenInhaler.Kandidaten().Count;
            if (gemist > 0)
            {
                opbouw.Add(($"{gemist} werkdag{(gemist == 1 ? "" : "en")} zonder geboekte uren", gemist * 2.5));
            }
        }
        catch
        {
            // Zonder journaal verder.
        }
        var graden = Math.Clamp(Basis + opbouw.Sum(o => o.Item2), -26, 9);
        return new Meting(Math.Round(graden), opbouw);
    }
}
