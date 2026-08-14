namespace WorkManager;

/// <summary>
/// Gezondheid per berichtenbron (Gmail, Google Chat, WhatsApp, Teams, Outlook): laatste
/// geslaagde sync, laatste fout en een foutbudget — na drie opeenvolgende fouten pauzeert
/// de bron een half uur, zodat een kapotte koppeling (bv. verlopen MFA) niet elke poll
/// opnieuw vertraging en meldingen veroorzaakt. Voedt ook het "Gezondheid…"-overzicht.
/// </summary>
public static class BronGezondheid
{
    public sealed class Stand
    {
        public DateTimeOffset? LaatsteSucces;
        public DateTimeOffset? LaatsteFout;
        public string LaatsteFoutTekst = "";
        public int OpRij; // opeenvolgende fouten
        public DateTimeOffset PauzeTot = DateTimeOffset.MinValue;
        public int FoutenVandaag;
        public DateOnly FoutenDag;
        /// <summary>Hoe ver in de ophaalbeurt deze bron klaar was (de bronnen lopen parallel).</summary>
        public TimeSpan LaatsteDuur;
        /// <summary>Hoe vaak deze bron vandaag al gepauzeerd is (bepaalt de duur van de volgende).</summary>
        public int Pauzes;
    }

    private static readonly object Slot = new();
    private static readonly Dictionary<string, Stand> Standen = new(StringComparer.OrdinalIgnoreCase);

    private static Stand Van(string bron)
    {
        if (!Standen.TryGetValue(bron, out var s))
        {
            Standen[bron] = s = new Stand();
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (s.FoutenDag != vandaag)
        {
            s.FoutenDag = vandaag;
            s.FoutenVandaag = 0;
            s.Pauzes = 0;
        }
        return s;
    }

    public static void Succes(string bron)
    {
        lock (Slot)
        {
            var s = Van(bron);
            s.LaatsteSucces = DateTimeOffset.Now;
            s.OpRij = 0;
            s.PauzeTot = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Hoe ver in de ophaalbeurt deze bron zijn gegevens klaar had. Apart van
    /// <see cref="Succes"/>, want de bronnen lopen parallel: de tijd moet gemeten worden
    /// wanneer de ophaaltaak áf is, niet wanneer de verwerking eraan toekomt.
    /// </summary>
    public static void Klaar(string bron, TimeSpan duur)
    {
        lock (Slot)
        {
            Van(bron).LaatsteDuur = duur;
        }
    }

    /// <summary>Registreert een fout; true als de bron hiermee nét gepauzeerd wordt.</summary>
    public static bool Fout(string bron, string melding)
    {
        lock (Slot)
        {
            var s = Van(bron);
            s.LaatsteFout = DateTimeOffset.Now;
            s.LaatsteFoutTekst = melding.Length > 160 ? melding[..160] + "…" : melding;
            s.OpRij++;
            s.FoutenVandaag++;
            // Pas na vijf mislukkingen op rij pauzeren, en dan oplopend: 5, 15, 30 minuten.
            // Drie keer meteen een half uur bleek te streng — één hapering (een trage
            // aanmeldcontrole, een browser die net herstart) haalde een bron dan een half uur
            // uit de lucht, terwijl hij de volgende ronde gewoon weer werkte.
            if (s.OpRij >= 5 && s.PauzeTot <= DateTimeOffset.Now)
            {
                s.Pauzes++;
                var minuten = s.Pauzes switch { 1 => 5, 2 => 15, _ => 30 };
                s.PauzeTot = DateTimeOffset.Now.AddMinutes(minuten);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Na een geslaagde (her)aanmelding: pauze en fouttellers wissen, zodat de bron meteen
    /// weer meedoet in plaats van "gepauzeerd tot …" te blijven melden terwijl het
    /// onderliggende probleem al opgelost is.
    /// </summary>
    public static void Hervat(string bron)
    {
        lock (Slot)
        {
            var s = Van(bron);
            s.OpRij = 0;
            s.Pauzes = 0;
            s.PauzeTot = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Herkent een aanmeldprobleem aan de fouttekst (Teams/Outlook-meldingen).</summary>
    public static bool IsAanmeldFout(string melding) =>
        melding.Contains("niet aangemeld", StringComparison.OrdinalIgnoreCase) ||
        melding.Contains("niet ingelogd", StringComparison.OrdinalIgnoreCase);

    /// <summary>True als de laatste fout van deze bron een aanmeldprobleem was.</summary>
    public static bool LaatsteFoutIsAanmelding(string bron)
    {
        lock (Slot)
        {
            return IsAanmeldFout(Van(bron).LaatsteFoutTekst);
        }
    }

    public static bool Gepauzeerd(string bron, out DateTimeOffset tot)
    {
        lock (Slot)
        {
            tot = Van(bron).PauzeTot;
            return tot > DateTimeOffset.Now;
        }
    }

    /// <summary>Meerregelig overzicht voor het gezondheidsvenster, incl. crashtellers.</summary>
    /// <summary>Eén regel met "bron=seconden" per bron, voor het verversingslogboek.</summary>
    public static string DurenKort()
    {
        lock (Slot)
        {
            return string.Join("  ", new[] { "Gmail", "Google Chat", "WhatsApp", "Teams", "Outlook" }
                .Select(b => (Bron: b, Duur: Van(b).LaatsteDuur))
                .Where(x => x.Duur > TimeSpan.Zero)
                .Select(x => $"{x.Bron}={x.Duur.TotalSeconds:0.0}s"));
        }
    }

    public static string Overzicht()
    {
        var regels = new List<string>();
        lock (Slot)
        {
            foreach (var (icoon, bron) in new[]
            {
                ("✉️", "Gmail"), ("💬", "Google Chat"), ("🟢", "WhatsApp"),
                ("🟪", "Teams"), ("🔷", "Outlook"),
            })
            {
                var s = Van(bron);
                var regel = $"{icoon} {bron,-12}  " +
                    (s.LaatsteSucces is { } ok
                        ? $"laatste sync {ok.ToLocalTime():HH:mm}"
                        : "nog niet gesynct") +
                    (s.LaatsteDuur > TimeSpan.Zero
                        ? $"  ·  klaar na {s.LaatsteDuur.TotalSeconds:0.0} s"
                        : "");
                if (s.FoutenVandaag > 0 && s.LaatsteFout is { } fout)
                {
                    regel += $"  ·  {s.FoutenVandaag} fout(en) vandaag, " +
                        $"laatste {fout.ToLocalTime():HH:mm}: {s.LaatsteFoutTekst}";
                }
                if (s.PauzeTot > DateTimeOffset.Now)
                {
                    regel += IsAanmeldFout(s.LaatsteFoutTekst)
                        ? "  ·  🔑 wacht op aanmelding"
                        : $"  ·  ⏸ gepauzeerd tot {s.PauzeTot.ToLocalTime():HH:mm}";
                }
                regels.Add(regel);
            }
        }

        // Browsercrashes van vandaag uit de crash-logs van de verborgen sessies.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");
        var crashRegels = new List<string>();
        foreach (var (naam, bestand) in new[]
        {
            ("Teams", "teams-crash-log.txt"), ("Outlook", "outlook-crash-log.txt"),
            ("WhatsApp", "wa-crash-log.txt"), ("App zelf", "crash-log.txt"),
        })
        {
            try
            {
                var pad = Path.Combine(dataDir, bestand);
                var vandaag = DateTime.Now.ToString("yyyy-MM-dd");
                var n = File.Exists(pad)
                    ? File.ReadLines(pad).Count(r => r.StartsWith(vandaag, StringComparison.Ordinal))
                    : 0;
                if (n > 0)
                {
                    crashRegels.Add($"{naam}: {n}× (zie {bestand})");
                }
            }
            catch
            {
                // Log even niet leesbaar: overslaan.
            }
        }
        regels.Add("");
        regels.Add(crashRegels.Count > 0
            ? "Crashes vandaag (automatisch hersteld):\r\n  " + string.Join("\r\n  ", crashRegels)
            : "Geen crashes vandaag.");
        return string.Join("\r\n", regels);
    }
}
