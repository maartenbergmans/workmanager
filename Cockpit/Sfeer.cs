using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De persoonlijkheid van de cockpit: een begroeting die weet hoe laat en welke dag het is,
/// en badges voor kleine prestaties. Puur sfeer — als hier iets misgaat, mag het gewoon
/// stilzwijgend niets doen.
/// </summary>
public static class Sfeer
{
    private static readonly string BadgeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "badges.json");

    private static readonly Random Willekeur = new();

    // ---------- Begroeting ----------

    /// <summary>Eén regel voor bij het openen van de cockpit: dagdeel, dag en wat er op stapel staat.</summary>
    public static string Begroeting(int openTaken, int meetings)
    {
        var nu = DateTime.Now;
        var basis = SpecialeDag(nu) ?? Dagdeel(nu);
        var vooruitblik = Vooruitblik(openTaken, meetings, nu);
        return vooruitblik.Length > 0 ? $"{basis}  ·  {vooruitblik}" : basis;
    }

    /// <summary>Is vandaag een dag die extra confetti verdient (verjaardag, nieuwjaar)?</summary>
    public static bool FeestDag()
    {
        var nu = DateTime.Now;
        return (nu.Month, nu.Day) is (11, 18) or (1, 1);
    }

    private static string Dagdeel(DateTime nu) => nu.Hour switch
    {
        < 5 => Kies("Nog wakker? De taken lopen niet weg 🌙",
                    "Nachtwerk. Respect — of ga slapen 🌙"),
        < 9 => Kies("Goeiemorgen ☀️", "Vroeg op vandaag ☀️", "Morgen! Koffie staat klaar ☕"),
        < 12 => Kies("Goeiemorgen ☀️", "Prima moment om iets af te werken 💪"),
        < 14 => Kies("Middag 🍽️", "Lunchpauze verdiend?"),
        < 17 => Kies("Namiddag 🕒", "Nog even doorbijten 💪", "De namiddag is van jou 🕒"),
        < 20 => Kies("Avond 🌆", "Feierabend in zicht 🌆"),
        _ => Kies("Late uurtjes 🌙", "Nog bezig? Morgen is er ook nog 🌙"),
    };

    private static string? SpecialeDag(DateTime nu) => (nu.Month, nu.Day) switch
    {
        (11, 18) => "🎂 Gelukkige verjaardag! Vandaag mag de lijst blijven staan",
        (12, 24) or (12, 25) => "🎄 Vrolijk kerstfeest",
        (12, 31) => "🥂 Laatste werkdag van het jaar — afsluiten maar",
        (1, 1) => "🎆 Gelukkig nieuwjaar! Nieuwe lijst, nieuwe kansen",
        (12, 6) => "🎁 Sinterklaas — braaf geweest?",
        (10, 31) => "🎃 Halloween — de enige dag dat een volle inbox eng mag zijn",
        (4, 1) => "🃏 1 april — vertrouw vandaag niets, ook deze melding niet",
        _ => nu.DayOfWeek == DayOfWeek.Friday && nu.Hour >= 15
            ? Kies("🍻 Vrijdagnamiddag — bijna weekend", "🍻 Vrijdag! Afronden en wegwezen")
            : null,
    };

    private static string Vooruitblik(int openTaken, int meetings, DateTime nu)
    {
        if (openTaken == 0 && meetings == 0)
        {
            return nu.Hour < 12
                ? "lege agenda en geen taken — dat wordt genieten"
                : "niets meer op de lijst 🎉";
        }
        var delen = new List<string>();
        if (openTaken > 0)
        {
            delen.Add(openTaken == 1 ? "1 open taak" : $"{openTaken} open taken");
        }
        if (meetings > 0)
        {
            delen.Add(meetings == 1 ? "1 meeting" : $"{meetings} meetings");
        }
        var lijst = string.Join(" en ", delen);
        return meetings >= 4 ? $"{lijst} — succes ermee 😅" : lijst;
    }

    private static string Kies(params string[] opties) => opties[Willekeur.Next(opties.Length)];

    // ---------- Badges ----------

    /// <summary>
    /// Kijkt of het afvinken van een taak een badge oplevert. Elke badge komt hooguit één keer
    /// per dag; is er niets te vieren, dan geeft dit null terug.
    /// </summary>
    public static string? BadgeVoorAfvinken(int vandaagAfgevinkt)
    {
        var nu = DateTime.Now;
        var (sleutel, tekst) = Bepaal(vandaagAfgevinkt, nu);
        if (sleutel is null || tekst is null || AlGehad(sleutel))
        {
            return null;
        }
        Onthoud(sleutel);
        return tekst;
    }

    private static (string?, string?) Bepaal(int aantal, DateTime nu) => aantal switch
    {
        // De grotere mijlpalen eerst: die zijn zeldzamer en dus leuker om te melden.
        >= 15 => ("vijftien", "🚀 Vijftien taken op één dag. Gaat het wel?"),
        >= 10 => ("tien", "🔟 Tien afgevinkt vandaag — indrukwekkend"),
        >= 5 => ("vijf", "🏅 Vijf taken vandaag. Lekker bezig"),
        1 when nu.Hour < 9 => ("vroege-vogel", "🌅 Eerste taak vóór negenen — vroege vogel"),
        1 when nu.Hour >= 22 => ("nachtuil", "🦉 Nog aan het afvinken om dit uur? Nachtuil 🌙"),
        _ => (null, null),
    };

    private sealed class BadgeDag
    {
        public string Dag { get; set; } = "";
        public List<string> Gehad { get; set; } = new();
    }

    private static bool AlGehad(string sleutel)
    {
        var dag = Laad();
        return dag.Dag == Vandaag() && dag.Gehad.Contains(sleutel);
    }

    private static void Onthoud(string sleutel)
    {
        var dag = Laad();
        if (dag.Dag != Vandaag())
        {
            dag = new BadgeDag { Dag = Vandaag() };
        }
        dag.Gehad.Add(sleutel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BadgeFile)!);
            File.WriteAllText(BadgeFile, JsonSerializer.Serialize(dag));
        }
        catch
        {
            // Best effort: hooguit zie je een badge twee keer.
        }
    }

    private static BadgeDag Laad()
    {
        try
        {
            if (File.Exists(BadgeFile) &&
                JsonSerializer.Deserialize<BadgeDag>(File.ReadAllText(BadgeFile)) is { } d)
            {
                return d;
            }
        }
        catch
        {
            // Onleesbaar: als "nog niets gehad" behandelen.
        }
        return new BadgeDag();
    }

    private static string Vandaag() => DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
}
