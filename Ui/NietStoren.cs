using System.Text.Json;

namespace WorkManager;

/// <summary>
/// "Niet storen" voor WorkManager: zolang het aan staat, verschijnt er niets ongevraagd —
/// geen tray-meldingen (Claude, Teams/WhatsApp/Outlook-berichten via de cockpit, radars,
/// herinneringen), geen toasts in een venster waar je niet mee bezig bent, geen confetti of
/// themaintro. Wat onderdrukt werd, komt in het 🔔-log (met 🔕) en na afloop volgt één
/// samenvatting. Aan te zetten voor een duur, tot het einde van de lopende meeting, tot je
/// het zelf uitzet, of automatisch tijdens elke meeting uit de agenda.
/// <para>Windows' eigen "Niet storen" (voor de Teams- en WhatsApp-app) laat zich op Windows
/// 11 25H2 niet door een app omzetten — getest op 2026-09-12 (WNF, registerschakelaar en de
/// FocusSessionManager-API, die een Limited Access Feature is).</para>
/// <para>State: %APPDATA%\WorkManager\niet-storen.json.</para>
/// </summary>
public static class NietStoren
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "niet-storen.json");

    private sealed class State
    {
        public DateTimeOffset? Tot { get; set; }          // null + Onbeperkt=false = uit
        public bool Onbeperkt { get; set; }
        public bool AutoTijdensMeetings { get; set; }
        public string AutoMeeting { get; set; } = "";     // sleutel van de meeting die het aanzette
        public string UitgezetVoor { get; set; } = "";    // die meeting niet opnieuw automatisch
        public string Reden { get; set; } = "";
    }

    private static readonly object Slot = new();
    private static State _state = Laad();
    private static readonly List<(DateTimeOffset Moment, string Titel, string Tekst)> _gemist = new();

    /// <summary>Gaat af bij elke wijziging (aan, uit, verlengd, auto-optie); niet per se op de UI-thread.</summary>
    public static event Action? Gewijzigd;

    /// <summary>Toont de samenvatting na afloop (gezet door de tray-app, die de UI-thread heeft).</summary>
    public static Action<string, string>? ToonSamenvatting { get; set; }

    public static bool Actief
    {
        get
        {
            lock (Slot)
            {
                return _state.Onbeperkt || (_state.Tot is { } tot && tot > DateTimeOffset.Now);
            }
        }
    }

    public static DateTimeOffset? Tot
    {
        get
        {
            lock (Slot)
            {
                return _state.Onbeperkt ? null : _state.Tot;
            }
        }
    }

    public static string Reden
    {
        get
        {
            lock (Slot)
            {
                return _state.Reden;
            }
        }
    }

    public static bool AutoTijdensMeetings
    {
        get
        {
            lock (Slot)
            {
                return _state.AutoTijdensMeetings;
            }
        }
        set
        {
            lock (Slot)
            {
                _state.AutoTijdensMeetings = value;
                Bewaar();
            }
            Gewijzigd?.Invoke();
        }
    }

    /// <summary>Korte status voor knoppen en menu's: "🔕 tot 14:30", "🔕 aan" of leeg.</summary>
    public static string StatusTekst =>
        !Actief ? "" : Tot is { } tot ? $"🔕 tot {tot.LocalDateTime:HH:mm}" : "🔕 aan";

    public static void AanVoor(TimeSpan duur, string reden = "") =>
        Zet(DateTimeOffset.Now + duur, onbeperkt: false, reden, autoMeeting: "");

    public static void AanTot(DateTimeOffset tot, string reden = "") =>
        Zet(tot, onbeperkt: false, reden, autoMeeting: "");

    public static void AanOnbeperkt() => Zet(null, onbeperkt: true, "", autoMeeting: "");

    public static void Uit()
    {
        bool wasActief;
        lock (Slot)
        {
            wasActief = Actief;
            // Handmatig uitgezet tijdens een automatische meeting: die meeting met rust laten.
            if (_state.AutoMeeting.Length > 0)
            {
                _state.UitgezetVoor = _state.AutoMeeting;
            }
            _state.Tot = null;
            _state.Onbeperkt = false;
            _state.AutoMeeting = "";
            _state.Reden = "";
            Bewaar();
        }
        if (wasActief)
        {
            Afgelopen();
        }
        Gewijzigd?.Invoke();
    }

    private static void Zet(DateTimeOffset? tot, bool onbeperkt, string reden, string autoMeeting)
    {
        lock (Slot)
        {
            _state.Tot = tot;
            _state.Onbeperkt = onbeperkt;
            _state.Reden = reden;
            _state.AutoMeeting = autoMeeting;
            Bewaar();
        }
        Gewijzigd?.Invoke();
    }

    /// <summary>Noteert een melding die niet getoond werd (komt in het 🔔-log en de samenvatting).</summary>
    public static void Onderdruk(string titel, string tekst)
    {
        lock (Slot)
        {
            _gemist.Add((DateTimeOffset.Now, titel, tekst));
            if (_gemist.Count > 200)
            {
                _gemist.RemoveAt(0);
            }
        }
        Toast.RegistreerOnderdrukt($"🔕 {titel}: {tekst}");
    }

    /// <summary>
    /// Elke halve minuut vanuit de tray-app: laat een verlopen periode aflopen en zet het
    /// automatisch aan of uit rond meetings uit de agenda.
    /// </summary>
    public static void Controleer(IEnumerable<AgendaClient.AgendaItem> meetingsVandaag)
    {
        var nu = DateTimeOffset.Now;
        bool afgelopen = false, gewijzigd = false;
        lock (Slot)
        {
            if (!_state.Onbeperkt && _state.Tot is { } tot && tot <= nu)
            {
                _state.Tot = null;
                _state.AutoMeeting = "";
                _state.Reden = "";
                Bewaar();
                afgelopen = gewijzigd = true;
            }
        }
        if (afgelopen)
        {
            Afgelopen();
        }

        if (AutoTijdensMeetings && !Actief)
        {
            var lopend = meetingsVandaag
                .Where(m => !m.HeleDag && !GeenWerktijd.Is(m.Titel) && m.Start <= nu.AddMinutes(1) && m.Einde > nu)
                .OrderBy(m => m.Einde)
                .FirstOrDefault();
            if (lopend is not null)
            {
                var sleutel = $"{lopend.Start:O}|{lopend.Titel}";
                string uitgezetVoor;
                lock (Slot)
                {
                    uitgezetVoor = _state.UitgezetVoor;
                }
                if (sleutel != uitgezetVoor)
                {
                    Zet(lopend.Einde, onbeperkt: false, lopend.Titel, autoMeeting: sleutel);
                    gewijzigd = false; // Zet meldde het al
                }
            }
        }
        if (gewijzigd)
        {
            Gewijzigd?.Invoke();
        }
    }

    /// <summary>De meeting die nu loopt (voor "tot het einde van deze meeting"), of null.</summary>
    public static AgendaClient.AgendaItem? LopendeMeeting(IEnumerable<AgendaClient.AgendaItem> meetingsVandaag)
    {
        var nu = DateTimeOffset.Now;
        return meetingsVandaag
            .Where(m => !m.HeleDag && !GeenWerktijd.Is(m.Titel) && m.Start <= nu.AddMinutes(5) && m.Einde > nu)
            .OrderBy(m => m.Einde)
            .FirstOrDefault();
    }

    /// <summary>De meetings van vandaag uit de agendacache (eigen agenda + CED).</summary>
    public static List<AgendaClient.AgendaItem> MeetingsVandaag()
    {
        try
        {
            if (MeetingsCache.Load() is not { } cache)
            {
                return new();
            }
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var ced = cache.Ced.TryGetValue(vandaag.ToString("yyyy-MM-dd"), out var c) ? c : new();
            return cache.Eigen.Concat(ced)
                .Where(m => DateOnly.FromDateTime(m.Start.LocalDateTime) == vandaag)
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Vult een (op te bouwen) menu met de keuzes: status en uitzetten, tot het einde van de
    /// lopende meeting, vaste duren, onbeperkt, de auto-optie en Windows' eigen instelling.
    /// Aanroepen bij het openen, zodat de lopende meeting en de status actueel zijn.
    /// </summary>
    public static void VulMenu(ToolStripDropDown menu)
    {
        menu.Items.Clear();
        if (Actief)
        {
            menu.Items.Add(new ToolStripMenuItem(
                StatusTekst + (Reden.Length > 0 ? $" · {Kort(Reden, 40)}" : "")) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("🔔 Uitzetten", null, (_, _) => Uit()));
            menu.Items.Add(new ToolStripSeparator());
        }
        if (LopendeMeeting(MeetingsVandaag()) is { } meeting)
        {
            menu.Items.Add(new ToolStripMenuItem(
                $"Tot het einde van \"{Kort(meeting.Titel, 30)}\" ({meeting.Einde.LocalDateTime:HH:mm})", null,
                (_, _) => AanTot(meeting.Einde, meeting.Titel)));
        }
        foreach (var (label, duur) in new[]
                 {
                     ("30 minuten", TimeSpan.FromMinutes(30)),
                     ("1 uur", TimeSpan.FromHours(1)),
                     ("2 uur", TimeSpan.FromHours(2)),
                 })
        {
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => AanVoor(duur)));
        }
        menu.Items.Add(new ToolStripMenuItem("Tot ik het uitzet", null, (_, _) => AanOnbeperkt()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Automatisch tijdens meetings uit de agenda", null,
            (_, _) => AutoTijdensMeetings = !AutoTijdensMeetings) { Checked = AutoTijdensMeetings });
        menu.Items.Add(new ToolStripSeparator());
        // De Teams- en WhatsApp-app zijn aparte programma's: hun meldingen stilt alleen
        // Windows' eigen "Niet storen" (bovenaan deze instellingenpagina, één klik).
        menu.Items.Add(new ToolStripMenuItem("Windows 'Niet storen' (Teams- en WhatsApp-app)…", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            }
            catch
            {
                // Instellingen niet te openen.
            }
        }));
    }

    private static void Afgelopen()
    {
        List<(DateTimeOffset Moment, string Titel, string Tekst)> gemist;
        lock (Slot)
        {
            gemist = _gemist.ToList();
            _gemist.Clear();
        }
        if (gemist.Count == 0)
        {
            ToonSamenvatting?.Invoke("Niet storen voorbij", "Je hebt niets gemist.");
            return;
        }
        var regels = gemist
            .GroupBy(g => g.Titel)
            .Select(g => g.Count() == 1 ? $"• {g.Key}: {Kort(g.First().Tekst, 60)}" : $"• {g.Key} ({g.Count()}×)")
            .Take(6);
        ToonSamenvatting?.Invoke(
            $"Niet storen voorbij — {gemist.Count} melding{(gemist.Count == 1 ? "" : "en")} gemist",
            string.Join("\n", regels) + "\nAlles staat ook in het 🔔-log van de cockpit.");
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.Replace("\n", " ").Trim();
        return tekst.Length <= max ? tekst : tekst[..(max - 1)] + "…";
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
            // Onleesbaar: uit.
        }
        return new State();
    }

    private static void Bewaar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_state));
        }
        catch
        {
            // Best effort.
        }
    }
}
