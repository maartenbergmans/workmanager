using System.Globalization;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Dagelijkse stille peiling van ISPnext (werkdagen, na 08:00): hoeveel facturen wachten
/// er, voor hoeveel, en hoeveel keuren de regels automatisch goed? De uitkomst komt in de
/// tekst van de weektaak "Facturen goedkeuren (ISPnext)" — op woensdag bestaat die al; op
/// andere dagen komt er alleen een taak als er vervallen facturen liggen te wachten.
/// Staat <see cref="AutoGoedkeuren"/> aan, dan legt de peiling bovendien klaar wat de regels
/// automatisch goedkeuren en vraagt ze erom (zie <see cref="AutoGoedkeurForm"/>): versturen
/// gebeurt pas na een klik, nooit op eigen initiatief.
/// Peilt nooit terwijl het goedkeurvenster open staat. Is de ISPnext-sessie verlopen, dan
/// meldt de peiler zich zelf stil aan (CED-login + MFA-code uit de seed). Lukt dat niet, dan
/// blijft het daarbij: op dit CED-account wordt dezelfde dag geen tweede aanmelding meer
/// geprobeerd (lockout-risico) — er komt een melding en morgen begint de radar opnieuw.
/// Alleen een onleesbare pagina (geen loginscherm, geen tabel) krijgt herkansingen, hoogstens
/// drie per dag met anderhalf uur ertussen.
/// </summary>
public static class IspRadar
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ispnext-radar.json");

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("nl-BE");

    /// <summary>Hoogstens drie pogingen per dag, met minstens anderhalf uur ertussen.</summary>
    private const int MaxPogingen = 3;

    private static readonly TimeSpan TussenPogingen = TimeSpan.FromMinutes(90);

    private sealed class State
    {
        /// <summary>De dag waarop het lezen écht gelukt is; dan is de radar voor vandaag klaar.</summary>
        public string LaatsteDag { get; set; } = "";

        /// <summary>Dag waarop <see cref="PogingenVandaag"/> hoort (anders begint de teller opnieuw).</summary>
        public string PogingenDag { get; set; } = "";

        public int PogingenVandaag { get; set; }

        public DateTimeOffset? LaatstePoging { get; set; }
    }

    private static bool _bezig;

    /// <param name="openVenster">
    /// Wat de meldingen openen als je erop klikt (het goedkeurvenster); null = niet klikbaar.
    /// </param>
    public static async Task ZorgVoorAsync(CancellationToken ct, Action? openVenster = null)
    {
        var nu = DateTime.Now;
        if (_bezig || nu.Hour < 8 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }
        var state = LoadState();
        var vandaag = nu.ToString("yyyy-MM-dd");
        if (state.LaatsteDag == vandaag || InvoiceApprovalForm.IsOpen)
        {
            return; // vandaag al gelezen, of het echte venster gebruikt het profiel
        }
        if (state.PogingenDag != vandaag)
        {
            state.PogingenDag = vandaag;
            state.PogingenVandaag = 0;
        }
        if (state.PogingenVandaag >= MaxPogingen ||
            (state.LaatstePoging is { } vorige && nu - vorige.LocalDateTime < TussenPogingen))
        {
            return; // het aantal pogingen is op, of de vorige was net
        }
        _bezig = true;
        try
        {
            // Loginstappen in het logboek: anders is een stille aanmelding die blijft steken
            // niet na te kijken (het venster logt in beeld, de radar heeft alleen dit bestand).
            var peiling = await IspNextClient.Instance.PeilAsync(ct, Logboek);
            // Elke echte poging telt mee (ook een mislukte), zodat een onleesbare pagina of een
            // aanmelding die blijft steken niet elke tien minuten een webview opstart.
            state.PogingenVandaag++;
            state.LaatstePoging = DateTimeOffset.Now;
            if (peiling?.Aangemeld == true)
            {
                state.LaatsteDag = vandaag; // gelukt: morgen weer
            }
            else if (peiling is not null)
            {
                // Aanmelden is niet gelukt. Dit is het CED-account, dus hier wordt vandaag
                // niets meer geprobeerd: nog een ronde met hetzelfde wachtwoord of een nieuwe
                // MFA-code riskeert een blokkering. Maarten meldt zich zelf aan in het venster
                // (en de radar begint morgen gewoon weer).
                state.PogingenVandaag = MaxPogingen;
                Logboek("stil aanmelden mislukt — vandaag geen nieuwe poging (CED-account)");
                TrayMelding.Toon("ISPnext", "De sessie is verlopen en stil aanmelden lukte niet. " +
                    "Open het goedkeurvenster om je zelf aan te melden.", openVenster, 15000);
            }
            SaveState(state);
            if (peiling is not null)
            {
                WerkTaakBij(peiling, []);
                VraagGoedkeuring(peiling);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logboek(ex.Message);
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Eén regel in ispnext-radar.log: loginstappen en fouten, puur voor diagnose.</summary>
    private static void Logboek(string regel)
    {
        try
        {
            File.AppendAllText(Path.ChangeExtension(StateFile, ".log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {regel}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>Eén regel voor in de taaktekst / het venster: "7 facturen, € 12.345,00 · 5 auto · ⚠ 2 vervallen".</summary>
    public static string Samenvatting(IspPeiling p)
    {
        if (!p.Aangemeld)
        {
            return "sessie verlopen — aanmelden bij openen";
        }
        if (p.Facturen.Count == 0)
        {
            return "geen facturen wachten";
        }
        var s = string.Create(Culture, $"{p.Facturen.Count} facturen, € {p.Totaal:N2}");
        s += $" · {p.AantalAuto} auto ≤ plafond";
        if (p.AantalVervallen > 0)
        {
            s += $" · ⚠ {p.AantalVervallen} vervallen";
        }
        return s;
    }

    /// <summary>
    /// Staat de schakelaar aan en liggen er facturen die de regels automatisch goedkeuren, dan
    /// legt de radar ze klaar en vraagt hij erom: één melding die het bevestigingsvenster
    /// opent. Goedkeuren gebeurt dus nooit buiten Maarten om — hij hoeft alleen nog "ja" te
    /// klikken in plaats van de hele routine zelf te doorlopen.
    /// </summary>
    private static void VraagGoedkeuring(IspPeiling peiling)
    {
        if (!AutoGoedkeuren.Aan || !peiling.Aangemeld || peiling.AantalAuto == 0)
        {
            return;
        }
        var auto = peiling.Facturen.Where(f => f.Auto).ToList();
        var totaal = auto.Sum(f => f.Bedrag ?? 0);
        var kop = string.Create(Culture, $"{auto.Count} facturen voldoen aan je regels (€ {totaal:N2}).");
        TrayMelding.Toon("ISPnext — klaar om goed te keuren",
            kop + " Klik om te bekijken en in één keer goed te keuren.",
            () => AutoGoedkeurForm.Toon(peiling), 60000);
    }

    /// <summary>
    /// Na een bevestigde goedkeuringsronde: de weektaak bijwerken (of afvinken als er niets
    /// meer ligt) en één melding met aantal en totaal, zodat het ook in de meldingsgeschiedenis
    /// staat. De volledige lijst zit in het goedkeuringslogboek.
    /// </summary>
    public static void NaGoedkeuring(IspPeiling? peiling, List<IspFactuur> goedgekeurd)
    {
        if (peiling is not null)
        {
            WerkTaakBij(peiling, goedgekeurd);
        }
        if (goedgekeurd.Count > 0)
        {
            MeldGoedkeuring(goedgekeurd, null);
        }
    }

    private static void MeldGoedkeuring(List<IspFactuur> goedgekeurd, Action? openVenster)
    {
        var totaal = goedgekeurd.Sum(f => f.Bedrag ?? 0);
        var namen = string.Join(", ", goedgekeurd.Take(3).Select(f => f.Leverancier)) +
            (goedgekeurd.Count > 3 ? "…" : "");
        TrayMelding.Toon("ISPnext — automatisch goedgekeurd",
            string.Create(Culture, $"{goedgekeurd.Count} facturen, € {totaal:N2}: {namen}"),
            openVenster, 30000);
    }

    private static void WerkTaakBij(IspPeiling p, List<IspFactuur> goedgekeurd)
    {
        // Wat net stil goedgekeurd is, wacht niet meer: de taaktekst gaat over wat er nog ligt.
        if (goedgekeurd.Count > 0)
        {
            p = p with { Facturen = p.Facturen.Where(f => !goedgekeurd.Contains(f)).ToList() };
            if (p.Facturen.Count == 0)
            {
                VasteTaken.VinkAf(VasteTaken.FacturenTaak); // niets meer te doen deze week
                return;
            }
        }
        var data = MijnTaakStore.Load();
        var taak = data.Taken.FirstOrDefault(t => !t.Klaar &&
            t.Tekst.StartsWith(VasteTaken.FacturenTaak, StringComparison.OrdinalIgnoreCase));
        var tekst = $"{VasteTaken.FacturenTaak} — {Samenvatting(p)}";
        if (taak is not null)
        {
            if (taak.Tekst != tekst)
            {
                taak.Tekst = tekst;
                MijnTaakStore.Save(data);
            }
            return;
        }
        // Geen weektaak (niet woensdag): alleen aandringen als er facturen vervallen zijn.
        if (p.Aangemeld && p.AantalVervallen > 0)
        {
            data.Taken.Add(new MijnTaak
            {
                Tekst = tekst,
                Categorie = "Urban IT",
                Prioriteit = 0,
                Deadline = DateOnly.FromDateTime(DateTime.Now),
            });
            MijnTaakStore.Save(data);
            TrayMelding.Toon("ISPnext", string.Create(Culture,
                $"{p.AantalVervallen} vervallen factuur/facturen wachten op goedkeuring (totaal € {p.Totaal:N2})"),
                null, 15000);
        }
    }

    private static State LoadState()
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
            // Opnieuw beginnen.
        }
        return new State();
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Dan peilt hij straks nog eens.
        }
    }
}
