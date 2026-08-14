namespace WorkManager;

/// <summary>
/// De "stem" van een kleurenschema: elk thema heeft naast kleuren ook een eigen toon in de
/// kleine teksten van de app — de lege inbox, een gevierde inbox zero, de pauzetip en de
/// begroeting bij het wisselen. Zomer praat over mocktails aan het water, 007 bestelt een
/// wodka-martini (shaken, not stirred), Espresso schenkt koffie, Neon draait door de nacht.
///
/// <para>Zo blijft de app functioneel identiek, maar voelt een ander thema ook echt anders.
/// Nieuwe teksten toevoegen: één regel bijzetten in de juiste lijst hieronder.</para>
/// </summary>
public static class ThemaStem
{
    private static string Kies(IReadOnlyList<string> regels)
    {
        if (regels.Count == 0)
        {
            return "";
        }
        // Eén vaste keuze per uur in plaats van elke ververs een andere: de teksten blijven
        // rustig staan, maar wisselen in de loop van de dag toch nog. De lengte van de
        // eerste regel zit in de mix zodat niet alle lijsten synchroon dezelfde index kiezen.
        var uur = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 3600);
        return regels[Math.Abs(uur + regels[0].Length * 17) % regels.Count];
    }

    /// <summary>Tekst in een lege berichtenlijst (inbox zero).</summary>
    public static string LegeInbox() => Kies(Theme.Palet.Naam switch
    {
        "Zomer" => new[]
        {
            "Inbox leeg — voeten in het zand, mocktail in de hand 🍹",
            "Nul berichten. Virgin mojito verdiend 🌴",
            "Alles verwerkt. De ligstoel roept 🌞",
            "Leeg! Tijd voor een limonade met munt en ijs 🧊",
            "Zonnig en leeg. Geniet ervan 🏖️",
        },
        "007" => new[]
        {
            "Inbox neutralised. Vodka martini — shaken, not stirred 🍸",
            "Mission accomplished. De aktetas mag dicht 🕴️",
            "Nul berichten. Zelfs Q heeft niets meer voor je 🔧",
            "Leeg. Tijd om de smoking uit te hangen 🎩",
            "Geen doelwitten meer op de radar 🎯",
            "Bond. James Bond. En een lege inbox 🍸",
            "For your eyes only: nul berichten 🕶️",
            "We have all the time in the world 💍",
            "Moneypenny heeft niets voor je. Geniet ervan 💌",
            "The world is not enough — maar deze lege inbox wel 🌍",
            "Diamonds are forever. Inbox zero helaas niet — geniet nu 💎",
        },
        "Godfather" => new[]
        {
            "Inbox leeg. Leave the gun, take the cannoli 🍰",
            "Alles geregeld. It's not personal — strictly business 🥃",
            "Geen berichten meer. De familie slaapt rustig 🌙",
            "Afgehandeld — een aanbod dat niemand kon weigeren ✉️",
            "Leeg. Tijd voor een espresso op het terras van Corleone ☕",
            "Elke mail sleeps with the fishes 🐟",
            "I believe in America. En in een lege inbox 🇺🇸",
            "De consigliere heeft niets te melden. Rust 🎩",
            "Niemand vraagt vandaag een gunst aan de Don 💍",
        },
        "Neon" => new[]
        {
            "Inbox: 0. De stad slaapt, jij niet 🌃",
            "Alles verwerkt — synthwave mag luider 🎹",
            "Nul berichten in de buffer. Systeem stabiel 💾",
            "Leeg. Neonlichten aan, deadlines uit ⚡",
        },
        "Espresso" => new[]
        {
            "Inbox leeg — tijd voor een dubbele espresso ☕",
            "Alles verwerkt. Koffie met een koekje erbij 🍪",
            "Nul berichten. Zet er nog eentje op ☕",
            "Leeg en warm. Even niets, en dat is prima 🛋️",
        },
        "Daglicht" => new[]
        {
            "Inbox leeg. Even naar buiten? ☀️",
            "Alles verwerkt — heldere kop, heldere lijst ✨",
            "Nul berichten. Fris begin 🌤️",
            "Leeg! Ga iets leuks doen 🎉",
        },
        _ => new[]
        {
            "Inbox zero! Tijd voor een koffie ☕",
            "Helemaal leeg — jij wint van je inbox vandaag 🏆",
            "Niets te doen hier. Geniet ervan! 🌞",
            "Alles verwerkt. Ga iets leuks doen 🎉",
            "Verdacht rustig hier… profiteer ervan 🤫",
            "Leeg! Zelfs Claude is onder de indruk 🤖✨",
            "Nul berichten. Champagnemoment 🥂",
        },
    });

    /// <summary>Tekst in een lege takenlijst.</summary>
    public static string GeenTaken() => Kies(Theme.Palet.Naam switch
    {
        "Zomer" => new[] { "Geen taken meer — parasol uit 🌴", "Alles klaar. Zon op, laptop dicht 🌞" },
        "007" => new[]
        {
            "Geen opdrachten meer, 007 🕴️",
            "Alle dossiers gesloten 🗄️",
            "Missie volbracht. You only live twice — verspil het niet aan taken 🌅",
            "Niets meer te doen. Die another day 🎯",
            "M heeft geen nieuwe missies. De Aston wacht 🚗",
        },
        "Godfather" => new[]
        {
            "Geen zaken meer op tafel 🥃",
            "Alles is geregeld. Rustig aan 🍰",
            "Vandaag geen gunsten meer te verlenen 💍",
            "A man who doesn't spend time with his family can never be a real man 👨‍👩‍👧",
            "De tafel is leeg. Ga naar de familie 🍝",
        },
        "Neon" => new[] { "Takenwachtrij leeg ⚡", "Niets in de queue 🌃" },
        "Espresso" => new[] { "Niets meer te doen — koffiepauze ☕", "Alles afgewerkt, rustig aan 🛋️" },
        "Daglicht" => new[] { "Geen open taken meer ✨", "Alles afgewerkt 🎉" },
        _ => new[] { "Geen open taken 🎉" },
    });

    /// <summary>Feestregel bij een gehaalde inbox zero of een volledig afgevinkte lijst.</summary>
    public static string Gevierd() => Kies(Theme.Palet.Naam switch
    {
        "Zomer" => new[]
        {
            "Proost — mocktail met een schijfje limoen 🍹",
            "Verdiend! Voeten omhoog aan het water 🏝️",
        },
        "007" => new[]
        {
            "Een wodka-martini. Shaken, not stirred 🍸",
            "Netjes afgehandeld, 007. M zou tevreden zijn 🎩",
            "Nobody does it better 🎶",
            "GoldenEye-status: alles geneutraliseerd 🛰️",
            "Casino Royale gehaald: alle fiches binnen 🎰",
            "Licence to chill — verdiend 🕶️",
        },
        "Godfather" => new[]
        {
            "Netjes geregeld. Don Corleone knikt 🎩",
            "Zo doe je zaken. Een glas grappa erop 🥃",
            "Vandaag regelde jij de zaken als een echte Don 💍",
            "De familie is trots op je 🎻",
            "Keep your friends close — en je inbox closer 🤝",
        },
        "Neon" => new[] { "Level gehaald. Neon aan 🌆", "Combo compleet ⚡" },
        "Espresso" => new[] { "Verdiend — dubbele espresso ☕", "Goed werk. Koffie erop ☕" },
        "Daglicht" => new[] { "Mooi opgeruimd ✨", "Helemaal bij 🎉" },
        _ => new[] { "Mooi werk 🎉", "Alles bij 🏆" },
    });

    /// <summary>Korte pauzetip, in de toon van het thema.</summary>
    public static string Pauze() => Kies(Theme.Palet.Naam switch
    {
        "Zomer" => new[]
        {
            "Tip: even buiten, met iets fris met munt en limoen 🍹",
            "Pauze? Een virgin mojito en vijf minuten zon 🌞",
        },
        "007" => new[]
        {
            "Pauze: martini, shaken not stirred. Of toch maar water 🍸",
            "Even ontwapenen: vijf minuten, geen schermen 🕶️",
            "Tomorrow never dies — die taak ook niet. Eerst vijf minuten lucht 🌅",
            "Zelfs 007 verlaat soms het hoofdkwartier. Rechtstaan 🚁",
            "Quantum of solace: vijf minuten stilte 🤫",
        },
        "Godfather" => new[]
        {
            "Pauze. Leave the gun, take the cannoli 🍰",
            "Even de familie zien: vijf minuten, geen schermen 🍇",
            "Ga even naar buiten — in Sicilië werkt niemand tussen twaalf en drie 🍋",
            "Een Don beslist nooit met een leeg glas. Water halen 🥃",
            "Never let anyone know what you are thinking — dus even weg van dat scherm 🚶",
        },
        "Neon" => new[] { "Reboot even: water, rechtstaan, doorgaan ⚡" },
        "Espresso" => new[] { "Koffie? Deze keer met een koekje ☕" },
        "Daglicht" => new[] { "Even opstaan en naar buiten kijken ☀️" },
        _ => new[] { "Even pauze — recht staan en water drinken 💧" },
    });

    /// <summary>Begroeting zodra een thema gekozen wordt.</summary>
    public static string Welkom() => Theme.Palet.Naam switch
    {
        "Zomer" => "Zomer aan — mocktails staan koud 🍹",
        "007" => Kies(new[]
        {
            "007 aan. Vodka martini, shaken not stirred 🍸",
            "Bond. James Bond. Welkom op het hoofdkwartier 🕴️",
            "Welcome back, 007. From CED with love 💌",
        }),
        "Godfather" => Kies(new[]
        {
            "Godfather aan. Ik ga je een aanbod doen dat je niet kunt weigeren 🎩",
            "Just when I thought I was out… they pull me back in 🥃",
            "Welkom terug in de familie 💍",
        }),
        "Neon" => "Neon aan — de nacht is van jou 🌃",
        "Espresso" => "Espresso aan — warm en rustig ☕",
        "Daglicht" => "Daglicht aan — fris en helder ☀️",
        _ => "Middernacht aan — het vertrouwde donker 🌙",
    };

    /// <summary>
    /// Begroeting bij het openen van de cockpit, afhankelijk van het dagdeel én het thema.
    /// In 007 word je aangesproken als agent, in Zomer als iemand met vakantie in zicht.
    /// </summary>
    public static string Dagdeel()
    {
        var uur = DateTime.Now.Hour;
        var deel = uur < 6 ? "nacht" : uur < 12 ? "ochtend" : uur < 18 ? "middag" : "avond";
        return Theme.Palet.Naam switch
        {
            "007" => deel switch
            {
                "nacht" => "De nacht is jong, 007 🌙",
                "ochtend" => "Goedemorgen, 007. M verwacht je rapport 🕴️",
                "middag" => "Middagbriefing, 007 🎩",
                _ => "Goedenavond, 007. De bar is open 🍸",
            },
            "Godfather" => deel switch
            {
                "nacht" => "Laat. Alleen de familie is nog wakker 🌙",
                "ochtend" => "Goedemorgen, Don. De zaken wachten ☕",
                "middag" => "Middag. Tijd om afspraken na te komen 🤝",
                _ => "Goedenavond. De deur van het kantoor staat open 🥃",
            },
            "Zomer" => deel switch
            {
                "nacht" => "Nog wakker? De hangmat wacht 🌙",
                "ochtend" => "Goeiemorgen! De dag is nog fris 🌅",
                "middag" => "Middagzon — even iets fris drinken? 🍹",
                _ => "Avondzon. Tijd voor het terras 🌇",
            },
            "Neon" => deel switch
            {
                "ochtend" => "Systeem online. Goedemorgen ⚡",
                "middag" => "Middagpiek — alles draait 🌆",
                _ => "Nachtmodus. De stad licht op 🌃",
            },
            "Espresso" => deel switch
            {
                "ochtend" => "Goedemorgen. Eerste koffie? ☕",
                "middag" => "Middagdip — dubbele espresso ☕",
                _ => "Avondrust. Deze keer decaf ☕",
            },
            "Daglicht" => deel switch
            {
                "ochtend" => "Goedemorgen — frisse start ☀️",
                "middag" => "Goedemiddag 🌤️",
                _ => "Goedenavond ✨",
            },
            _ => deel switch
            {
                "ochtend" => "Goedemorgen 🌙",
                "middag" => "Goedemiddag",
                _ => "Goedenavond",
            },
        };
    }

    /// <summary>
    /// Het symbool van de inbox-zero-reeks. Standaard groeit er een plantje, maar elk thema
    /// heeft zijn eigen reeks: 007 klimt van dossier naar diamant, Zomer van zaadje naar
    /// palmboom, Espresso van boon naar volle kop.
    /// </summary>
    public static string Streak(int dagen)
    {
        if (dagen <= 0)
        {
            return "";
        }
        var trap = dagen switch
        {
            1 => 0, 2 => 1, 3 => 2, 4 => 3, <= 6 => 4, <= 9 => 5, <= 14 => 6, <= 19 => 7,
            <= 29 => 8, _ => 9,
        };
        var reeks = Theme.Palet.Naam switch
        {
            "007" => new[] { "🗂️", "🕶️", "🎩", "🍸", "🥂", "🎯", "🏅", "💼", "💎", "👑" },
            "Godfather" => new[] { "🍇", "🍊", "🥃", "☕", "🍰", "🎻", "🎩", "🌹", "💍", "👑" },
            "Zomer" => new[] { "🌱", "🌿", "☘️", "🍀", "🌻", "🌺", "🍹", "🏝️", "🌴", "🥥" },
            "Neon" => new[] { "▪️", "🔹", "🔷", "💠", "⚡", "🌐", "🚀", "🛸", "🌌", "🌠" },
            "Espresso" => new[] { "🫘", "☕", "☕", "🍫", "🥐", "🍰", "🧁", "🏆", "🥇", "👑" },
            _ => new[] { "🌱", "🌿", "☘️", "🍀", "🪴", "🌾", "🌳", "🌸", "🌺", "🌻" },
        };
        return reeks[trap];
    }

    /// <summary>
    /// Naam van de cockpit in de venstertitel. Alleen de titel verandert: knoppen en menu's
    /// blijven overal hetzelfde heten, zodat je nooit hoeft te zoeken.
    /// </summary>
    public static string CockpitTitel() => Theme.Palet.Naam switch
    {
        "007" => "HQ – WorkManager",
        "Godfather" => "Il Padrino – WorkManager",
        "Zomer" => "Cockpit – WorkManager 🌴",
        "Neon" => "Cockpit – WorkManager ⚡",
        "Espresso" => "Cockpit – WorkManager ☕",
        _ => "Cockpit – WorkManager",
    };

    /// <summary>
    /// Groot, heel gedempt silhouet achter een lege lijst — puur sfeer. Verschilt per soort
    /// lijst ("berichten", "taken", "meetings", "deadline"), zodat niet elk leeg paneel er
    /// hetzelfde uitziet. Leeg = niets tekenen (Middernacht en Daglicht blijven rustig).
    /// </summary>
    public static string LeegSilhouet(string soort = "") => Theme.Palet.Naam switch
    {
        "007" => soort switch
        {
            "taken" => "💼",     // aktetas: alle opdrachten afgewerkt
            "meetings" => "🕶️",  // niets op de radar
            "deadline" => "🎯",  // geen doelwitten binnen bereik
            _ => "🍸",           // berichten: de martini
        },
        "Godfather" => soort switch
        {
            "taken" => "🎩",     // hoed op de kapstok: zaken afgehandeld
            "meetings" => "🤝",  // geen afspraken
            "deadline" => "🌹",  // niets dringends
            _ => "🍰",           // berichten: de cannoli
        },
        "Zomer" => soort switch
        {
            "taken" => "🌴",
            "meetings" => "⛱️",
            "deadline" => "🏖️",
            _ => "🍹",
        },
        "Neon" => soort switch
        {
            "taken" => "🌃",
            "meetings" => "🛸",
            "deadline" => "🎯",
            _ => "⚡",
        },
        "Espresso" => soort switch
        {
            "taken" => "🫘",
            "meetings" => "🥐",
            "deadline" => "⏳",
            _ => "☕",
        },
        _ => "",
    };

    /// <summary>Tekst in een lege meetinglijst (vandaag of een andere dag).</summary>
    public static string GeenMeetings(bool vandaag) => Kies(Theme.Palet.Naam switch
    {
        "007" => vandaag
            ? new[]
            {
                "Niets op de radar vandaag 🕶️",
                "Geen afspraken, 007 — vrij spel 🎩",
                "Geen briefings. Q test intussen de gadgets 🔧",
                "Stil op het hoofdkwartier. Verdacht stil 🤫",
            }
            : new[] { "Geen afspraken deze dag 🕶️" },
        "Godfather" => vandaag
            ? new[]
            {
                "Geen afspraken vandaag 🤝",
                "Niemand aan de deur — zeldzaam 🎩",
                "Geen bezoekers in het kantoor. De deur mag dicht 🚪",
                "Vandaag geen zittingen. Don't ever take sides against de agenda 🍊",
            }
            : new[] { "Geen afspraken deze dag 🤝" },
        "Zomer" => vandaag
            ? new[] { "Geen meetings — het strand roept ⛱️", "Agenda leeg, zon op 🌞" }
            : new[] { "Geen meetings deze dag ⛱️" },
        "Neon" => vandaag
            ? new[] { "Agenda leeg. Focus aan ⚡" }
            : new[] { "Geen meetings deze dag ⚡" },
        "Espresso" => vandaag
            ? new[] { "Geen meetings — tijd om rustig door te werken ☕" }
            : new[] { "Geen meetings deze dag ☕" },
        _ => vandaag
            ? new[] { "Geen meetings vandaag 🎉" }
            : new[] { "Geen meetings deze dag" },
    });

    /// <summary>Tekst als er wel taken zijn, maar geen binnen het gekozen deadlinefilter.</summary>
    public static string NietsBinnenDeadline() => Kies(Theme.Palet.Naam switch
    {
        "007" => new[]
        {
            "Geen dringende doelwitten 🎯",
            "Niets urgents op de missielijst 🎯",
            "Alles kan wachten. Die another day 🕶️",
        },
        "Godfather" => new[]
        {
            "Niets dringends. Wraak is een gerecht dat koud smaakt 🌹",
            "Geen haast. Een Don laat zich nooit opjagen 🎩",
            "Time erodes gratitude quicker than beauty — maar vandaag niet 🥃",
        },
        "Zomer" => new[] { "Niets dringends — rustig aan 🏖️" },
        "Neon" => new[] { "Geen deadlines in de buffer 🎯" },
        "Espresso" => new[] { "Niets dringends. Koffie kan ⏳" },
        _ => new[] { "Niets binnen deze deadline" },
    });

    /// <summary>Melding nadat een project of tool gestart is.</summary>
    public static string Gestart(string wat) => Theme.Palet.Naam switch
    {
        "007" => $"Q heeft je uitrusting klaar: {wat}",
        "Godfather" => $"Geregeld: {wat}",
        "Zomer" => $"Klaar voor gebruik: {wat} 🌞",
        "Neon" => $"Online: {wat} ⚡",
        "Espresso" => $"Staat te dampen: {wat} ☕",
        _ => $"Gestart: {wat}",
    };

    /// <summary>Naam van de taaktimer in menu's en meldingen.</summary>
    public static string TimerNaam() => Theme.Palet.Naam switch
    {
        "007" => "missieklok",
        "Godfather" => "familieklok",
        "Neon" => "session timer",
        "Espresso" => "koffieklok",
        _ => "timer",
    };

    /// <summary>Titel van het venster dat de productiedatabank naar localhost haalt.</summary>
    public static string ProdDbTitel(string klant) => Theme.Palet.Naam switch
    {
        "007" => $"Data-extractie — {klant}",
        "Godfather" => $"De boeken van {klant}",
        "Neon" => $"Datastroom — {klant}",
        _ => $"Productie-DB → localhost — {klant}",
    };

    /// <summary>Kop van de vrijdagse weekafsluiter.</summary>
    public static string DebriefingKop() => Theme.Palet.Naam switch
    {
        "007" => "Debriefing, 007",
        "Godfather" => "De week in de boeken 🎩",
        "Zomer" => "Weekend in zicht 🌴",
        "Neon" => "Week afgesloten ⚡",
        "Espresso" => "Weekafsluiter ☕",
        _ => "Weekoverzicht",
    };

    /// <summary>Statusregel terwijl er iets loopt (Claude die nadenkt, een lange kopie).</summary>
    public static string Bezig() => Kies(Theme.Palet.Naam switch
    {
        "007" => new[]
        {
            "Decoderen…", "Q werkt eraan…", "Dossier wordt gelicht…",
            "MI6 draait op volle toeren…", "Satelliet wordt uitgelezen…",
        },
        "Godfather" => new[]
        {
            "We regelen het…", "Even overleggen met de familie…", "Consigliere denkt na…",
            "We gaan hem een aanbod doen…", "De boeken worden nagekeken…",
        },
        "Zomer" => new[] { "Even shaken…", "Ijsblokjes erbij…", "Momentje in de zon…" },
        "Neon" => new[] { "Processing…", "Datastroom binnen…", "Rendering…" },
        "Espresso" => new[] { "Even laten doorlopen…", "Bonen malen…", "Water opwarmen…" },
        _ => new[] { "Bezig…", "Even geduld…" },
    });
}
