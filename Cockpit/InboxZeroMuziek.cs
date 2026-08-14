using System.Diagnostics;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Inbox zero verdient een deuntje. WorkManager stelt een nummer voor; klikken opent het in
/// Spotify. Bewust via de zoekfunctie en niet via een track-id: id's verouderen en leveren dan
/// "Kan dat nummer niet vinden" op, terwijl een zoekopdracht op titel en artiest altijd raak is.
/// Hooguit één suggestie per dag, en nooit twee keer na elkaar hetzelfde nummer.
/// </summary>
public static class InboxZeroMuziek
{
    private sealed record Nummer(string Titel, string Artiest)
    {
        /// <summary>Zoekterm voor Spotify (titel + artiest volstaat om bovenaan uit te komen).</summary>
        public string Zoek => $"{Titel} {Artiest}";
    }

    /// <summary>
    /// De speellijst hoort bij het kleurenschema: in 007 klinkt er een Bond-titelsong, in
    /// Zomer iets voor aan het water, in Neon synthwave en in Espresso iets warms. Zonder
    /// gekozen thema (Middernacht) de klassiekers die bij "klaar met werken" passen.
    /// </summary>
    private static Nummer[] Nummers => Theme.Palet.Naam switch
    {
        "007" => new Nummer[]
        {
            new("James Bond Theme", "Monty Norman"),
            new("Goldfinger", "Shirley Bassey"),
            new("Live and Let Die", "Paul McCartney & Wings"),
            new("Nobody Does It Better", "Carly Simon"),
            new("A View to a Kill", "Duran Duran"),
            new("You Know My Name", "Chris Cornell"),
            new("Skyfall", "Adele"),
            new("Writing's On The Wall", "Sam Smith"),
            new("Diamonds Are Forever", "Shirley Bassey"),
            new("The Man with the Golden Gun", "Lulu"),
        },
        "Zomer" => new Nummer[]
        {
            new("Island in the Sun", "Weezer"),
            new("Kokomo", "The Beach Boys"),
            new("Escape (The Piña Colada Song)", "Rupert Holmes"),
            new("Riptide", "Vance Joy"),
            new("Sunny Afternoon", "The Kinks"),
            new("Three Little Birds", "Bob Marley"),
            new("Good Vibrations", "The Beach Boys"),
            new("Summer Breeze", "Seals and Crofts"),
        },
        "Neon" => new Nummer[]
        {
            new("Nightcall", "Kavinsky"),
            new("Sunset", "The Midnight"),
            new("Midnight City", "M83"),
            new("Digital Love", "Daft Punk"),
            new("Tech Noir", "Gunship"),
            new("Blinding Lights", "The Weeknd"),
            new("Turbo Killer", "Carpenter Brut"),
        },
        "Espresso" => new Nummer[]
        {
            new("Espresso", "Sabrina Carpenter"),
            new("Lovely Day", "Bill Withers"),
            new("Let's Get Lost", "Chet Baker"),
            new("Come Away With Me", "Norah Jones"),
            new("The Girl from Ipanema", "Stan Getz Joao Gilberto"),
            new("Feeling Good", "Nina Simone"),
            new("Sunday Morning", "Maroon 5"),
        },
        "Daglicht" => new Nummer[]
        {
            new("Walking on Sunshine", "Katrina and the Waves"),
            new("Mr. Blue Sky", "Electric Light Orchestra"),
            new("Here Comes the Sun", "The Beatles"),
            new("Good as Hell", "Lizzo"),
            new("Best Day of My Life", "American Authors"),
            new("Happy", "Pharrell Williams"),
        },
        _ => new Nummer[]
        {
            new("Celebration", "Kool & The Gang"),
            new("Good as Hell", "Lizzo"),
            new("Walking on Sunshine", "Katrina and the Waves"),
            new("Don't Stop Me Now", "Queen"),
            new("Lovely Day", "Bill Withers"),
            new("September", "Earth Wind & Fire"),
            new("Happy", "Pharrell Williams"),
            new("I Gotta Feeling", "Black Eyed Peas"),
            new("Mr. Blue Sky", "Electric Light Orchestra"),
            new("Waterloo", "ABBA"),
            new("Signed Sealed Delivered", "Stevie Wonder"),
            new("Dancing Queen", "ABBA"),
            new("Ain't No Mountain High Enough", "Marvin Gaye Tammi Terrell"),
            new("Best Day of My Life", "American Authors"),
        },
    };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "inbox-zero-muziek.json");

    private sealed class State
    {
        public string LaatsteDag { get; set; } = "";
        public string LaatsteNummer { get; set; } = "";
    }

    /// <summary>Een suggestie: de tekst voor de melding en de actie die Spotify opent.</summary>
    public sealed record Suggestie(string Melding, string KnopTekst, Action Speel);

    /// <summary>
    /// Kiest een viernummer voor vandaag, of null als er vandaag al één voorgesteld is.
    /// Er wordt niets geopend tot je op de melding klikt.
    /// </summary>
    public static Suggestie? Voorstel()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var state = Laad();
        if (state.LaatsteDag == vandaag)
        {
            return null; // één suggestie per dag volstaat
        }

        var keuze = Nummers.Where(n => n.Zoek != state.LaatsteNummer).ToArray();
        var nummer = keuze[Random.Shared.Next(keuze.Length)];
        state.LaatsteDag = vandaag;
        state.LaatsteNummer = nummer.Zoek;
        Bewaar(state);

        // De aankondiging in de toon van het kleurenschema; 007 bestelt er een martini bij.
        var aanhef = Theme.Palet.Naam switch
        {
            "007" => "🍸 Mission accomplished, 007.",
            "Zomer" => "🍹 Inbox leeg! Glaasje erbij:",
            "Neon" => "⚡ Inbox: 0. Volume omhoog:",
            "Espresso" => "☕ Inbox leeg. Bij de koffie:",
            "Daglicht" => "☀️ Inbox zero! Zin in",
            _ => "🎵 Inbox zero! Zin in",
        };
        return new Suggestie(
            $"{aanhef} {nummer.Titel} — {nummer.Artiest}?",
            Theme.Palet.Naam == "007" ? "▶ Bond opzetten" : "▶ Spotify",
            () => Open(nummer));
    }

    private static void Open(Nummer nummer)
    {
        var term = Uri.EscapeDataString(nummer.Zoek);
        // Eerst de desktop-app (spotify:search:…); lukt dat niet, dan de webplayer.
        if (!Start($"spotify:search:{term}"))
        {
            Start($"https://open.spotify.com/search/{term}");
        }
    }

    private static bool Start(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false; // bv. geen handler voor spotify: — dan de webplayer proberen
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
            // Onleesbaar: als "nog niets voorgesteld" behandelen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
