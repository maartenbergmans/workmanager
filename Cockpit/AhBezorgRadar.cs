using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Verfijnt op de leverdag zelf de AH-agenda-afspraak: ah.be toont in de loop van de dag een
/// nauwkeuriger bezorgvenster ("Onze bezorger verwacht tussen 16:20 en 17:20 uur") dan het
/// bestelde slot van twee uur. De radar kijkt maximaal om de 2 uur op /mijnbestellingen (met
/// de bewaarde AH-login uit het winkelmandje-profiel) en zet de afspraak meteen op die
/// exactere tijden. Moet — zoals alles met WebView2 — vanaf de UI-thread draaien.
/// </summary>
public static class AhBezorgRadar
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "ah-bezorg-radar.json");

    private static readonly string[] Maanden =
    {
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december",
    };

    private static bool _bezig;

    /// <summary>
    /// Zet de intervalklok zó dat de eerstvolgende takenverversing meteen opnieuw checkt —
    /// aangeroepen als het inlogvenster sluit, zodat een verse login direct benut wordt.
    /// </summary>
    public static void PlanSnelleHerkansing()
    {
        var state = LaadState();
        state.LaatsteCheck = DateTimeOffset.Now - Interval;
        BewaarState(state);
    }

    /// <summary>Draait de check als hij aan de beurt is; stil bij elke vorm van tegenslag.</summary>
    public static async Task ZorgVoorAsync(Form eigenaar, CancellationToken ct)
    {
        if (_bezig || !CalendarClient.Beschikbaar || DateTime.Now.Hour < 7)
        {
            return;
        }
        if (AhSessie.Instance.VensterZichtbaar)
        {
            return; // Maarten is aan het inloggen: de 2-uursbeurt niet verbruiken
        }
        var state = LaadState();
        if (state.LaatsteCheck is { } vorige && DateTimeOffset.Now - vorige < Interval)
        {
            return;
        }
        _bezig = true;
        try
        {
            state.LaatsteCheck = DateTimeOffset.Now; // ook bij fouten: volgende poging over 2 uur
            BewaarState(state);

            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var afspraak = (await CalendarClient.ZoekOpDagAsync(vandaag, "AH-levering", ct))
                .FirstOrDefault();
            if (afspraak is null || DateTime.Now > afspraak.Einde)
            {
                return; // vandaag geen levering (meer)
            }

            // Het oorspronkelijk bestelde slot éénmalig vastleggen: na een eerdere verfijning
            // staan de afspraaktijden immers al op het bezorgvenster.
            if (state.Datum != vandaag.ToString("O"))
            {
                state.Datum = vandaag.ToString("O");
                state.SlotStart = afspraak.Start.ToString("HH\\:mm");
                state.SlotEinde = afspraak.Einde.ToString("HH\\:mm");
                state.LaatsteVenster = "";
                BewaarState(state);
            }

            var tekst = await AhSessie.Instance.BestellingenTekstAsync(ct);
            var geparsed = ParseVenster(tekst, vandaag);
            SchrijfDebug(tekst, geparsed);
            if (geparsed is not (TimeSpan van, TimeSpan tot) || tot <= van)
            {
                // Vraagt ah.be om opnieuw in te loggen ("Even controleren"), dan kan de radar
                // niets doen: dat maximaal één keer per dag melden — inloggen moet Maarten
                // zelf (hCaptcha) — en daarna al na een half uur opnieuw proberen in plaats
                // van pas over 2 uur.
                if (Regex.IsMatch(tekst, "opnieuw te laten weten wie je bent|Log in met een Passkey",
                    RegexOptions.IgnoreCase))
                {
                    state.LaatsteCheck = DateTimeOffset.Now - Interval + TimeSpan.FromMinutes(30);
                    // Een toast is hier te vluchtig (weg voor je hem ziet): het loginvenster
                    // gaat gewoon meteen open — hooguit één keer per dag, en alleen op een
                    // leverdag wanneer de login echt ontbreekt. Inloggen + venster sluiten
                    // volstaat; de radar pakt daarna vanzelf door.
                    if (state.LoginMelding != vandaag.ToString("O") && !eigenaar.IsDisposed)
                    {
                        state.LoginMelding = vandaag.ToString("O");
                        BewaarState(state);
                        await AhSessie.Instance.ToonLoginAsync(ct);
                        Toast.Toon(eigenaar,
                            "AH-levering vandaag: log even in, dan volg ik het exacte bezorgmoment",
                            Fluent.Winkelwagen);
                    }
                    BewaarState(state);
                }
                return; // anders: pagina anders opgebouwd of nog geen exacter venster bekend
            }
            var venster = $"{van:hh\\:mm}–{tot:hh\\:mm}";
            if (venster == state.LaatsteVenster ||
                (afspraak.Start.TimeOfDay == van && afspraak.Einde.TimeOfDay == tot))
            {
                state.LaatsteVenster = venster;
                BewaarState(state);
                return; // agenda klopt al
            }

            var start = vandaag.ToDateTime(TimeOnly.FromTimeSpan(van));
            var omschrijving = "Je Albert Heijn-bestelling wordt geleverd.\n" +
                $"Bezorger verwacht tussen {venster} (ah.be, stand {DateTime.Now:HH\\:mm}); " +
                $"besteld slot {state.SlotStart}–{state.SlotEinde}.";
            if (await CalendarClient.WijzigViaUidAsync(
                afspraak.Uid, afspraak.Titel, start, tot - van, omschrijving, ct))
            {
                state.LaatsteVenster = venster;
                BewaarState(state);
                if (!eigenaar.IsDisposed)
                {
                    Toast.Toon(eigenaar,
                        $"AH-bezorger verwacht {venster} — agenda-afspraak bijgewerkt",
                        Fluent.Winkelwagen);
                }
            }
        }
        catch
        {
            // Best effort; volgende ronde opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Het exacte bezorgvenster uit de paginatekst, bij voorkeur verankerd aan de datum van
    /// vandaag ("Maandag 3 augustus 16:00 - 18:00 … verwacht tussen 16:20 en 17:20"). Staat
    /// er maar één "verwacht tussen"-regel op de pagina, dan volstaat die ook zonder datum.
    /// </summary>
    private static (TimeSpan Van, TimeSpan Tot)? ParseVenster(string tekst, DateOnly dag)
    {
        if (tekst.Length == 0)
        {
            return null;
        }
        const string vensterRx = @"verwacht\s+tussen\s+(\d{1,2})[:.](\d{2})\s+en\s+(\d{1,2})[:.](\d{2})";
        var maand = Maanden[dag.Month - 1];
        var m = Regex.Match(tekst,
            $@"\b{dag.Day}\s+{maand}\b[\s\S]{{0,400}}?{vensterRx}", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            var alle = Regex.Matches(tekst, vensterRx, RegexOptions.IgnoreCase);
            if (alle.Count != 1)
            {
                return null; // geen of meerdere leveringen: zonder datumanker niet te kiezen
            }
            m = alle[0];
        }
        try
        {
            var van = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
            var tot = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
            return van < TimeSpan.FromHours(24) && tot < TimeSpan.FromHours(24) ? (van, tot) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Laatste scrape + parse-uitkomst naar schijf — onmisbaar als ah.be de
    /// pagina-opbouw wijzigt en de radar stil niets meer vindt.</summary>
    private static void SchrijfDebug(string tekst, (TimeSpan Van, TimeSpan Tot)? geparsed)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "ah-bezorg-debug.json"),
                JsonSerializer.Serialize(new
                {
                    datum = DateTimeOffset.Now,
                    tekstLengte = tekst.Length,
                    venster = geparsed is { } v ? $"{v.Van:hh\\:mm}–{v.Tot:hh\\:mm}" : null,
                    loginDiagnose = AhSessie.LaatsteLoginDiagnose,
                    tekst = tekst.Length > 4000 ? tekst[..4000] + "…" : tekst,
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    // ---------- state ----------

    private sealed class State
    {
        public DateTimeOffset? LaatsteCheck { get; set; }
        public string Datum { get; set; } = "";
        public string SlotStart { get; set; } = "";
        public string SlotEinde { get; set; } = "";
        public string LaatsteVenster { get; set; } = "";
        public string LoginMelding { get; set; } = "";
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
            // Onleesbaar: als "nog nooit" behandelen.
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
