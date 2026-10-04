using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>Eén wachtende factuur uit het ISPnext-overzicht "My Activities".</summary>
public sealed record IspFactuur(
    string Leverancier, string Factuurnummer, string Factuurdatum, string Vervaldatum,
    string Valuta, string BedragText, decimal? Bedrag, bool Vervallen, bool Auto, string Reden);

/// <summary>Wat een stille ronde mag doen: alleen lezen, het aanvinken proberen, of goedkeuren.</summary>
public enum IspModus
{
    Lezen,

    /// <summary>Droogloop: aanvinken en melden wat gevonden is, maar niets versturen.</summary>
    Proef,

    Goedkeuren,
}

/// <summary>Uitkomst van een stille peiling: aangemeld of niet, en de wachtende facturen.</summary>
public sealed record IspPeiling(DateTimeOffset Moment, bool Aangemeld, List<IspFactuur> Facturen)
{
    public decimal Totaal => Facturen.Sum(f => f.Bedrag ?? 0);
    public int AantalAuto => Facturen.Count(f => f.Auto);
    public int AantalVervallen => Facturen.Count(f => f.Vervallen);
}

/// <summary>
/// Stille lezer van de ISPnext-facturenlijst (CED): een verborgen WebView2 op hetzelfde
/// profiel als het goedkeurvenster (de SSO-sessie), die alleen kijkt en nooit goedkeurt.
/// Is de sessie verlopen, dan meldt hij zich zelf stil aan met de centrale CED-login
/// (zie <see cref="StilleLoginStapAsync"/>), zodat de dagelijkse peiling ook na een weekend
/// nog iets oplevert. Zo weet de cockpit hoeveel facturen er wachten, voor hoeveel, en
/// hoeveel de regels automatisch zouden goedkeuren. Na elke peiling wordt de webview weer weggegooid:
/// het profiel kan maar door één webview tegelijk gebruikt worden en het echte venster
/// heeft voorrang.
/// </summary>
public sealed class IspNextClient
{
    public static IspNextClient Instance { get; } = new();

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string PeilingFile = Path.Combine(DataDir, "ispnext-peiling.json");

    private static readonly System.Globalization.CultureInfo Cultuur =
        System.Globalization.CultureInfo.GetCultureInfo("nl-BE");

    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>De laatst bewaarde peiling (null als er nog nooit gepeild is).</summary>
    public static IspPeiling? Laatste()
    {
        try
        {
            return File.Exists(PeilingFile)
                ? JsonSerializer.Deserialize<IspPeiling>(File.ReadAllText(PeilingFile))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Peilt de facturenlijst. Null als het goedkeurvenster open staat (dat gebruikt het
    /// profiel) of de pagina binnen de tijd noch een tabel noch een loginscherm toonde.
    /// </summary>
    public async Task<IspPeiling?> PeilAsync(CancellationToken ct, Action<string>? log = null) =>
        (await RondeAsync(ct, log, IspModus.Lezen)).Peiling;

    /// <summary>
    /// Peilt de lijst en keurt in dezelfde ronde alles goed wat de regels automatisch
    /// goedkeuren (zie <see cref="GoedkeurAsync"/>). Geeft de peiling terug zoals ze vóór het
    /// goedkeuren was, plus wat er effectief goedgekeurd is. Met
    /// <paramref name="proef"/> wordt alleen het aanvinken geprobeerd en gaat er niets de deur
    /// uit — zo is na een wijziging van ISPnext te controleren of de rijen nog gevonden worden.
    /// </summary>
    public async Task<(IspPeiling? Peiling, List<IspFactuur> Goedgekeurd)> PeilEnGoedkeurAsync(
        CancellationToken ct, Action<string>? log = null, bool proef = false,
        IReadOnlyCollection<string>? alleen = null) =>
        await RondeAsync(ct, log, proef ? IspModus.Proef : IspModus.Goedkeuren, alleen);

    /// <summary>
    /// De sleutel waarmee een factuur herkend wordt tussen twee peilingen: leverancier +
    /// factuurnummer. Zo keurt een bevestigde ronde precies de facturen goed die ook op het
    /// scherm stonden, ook al is de lijst intussen gegroeid.
    /// </summary>
    public static string Sleutel(IspFactuur f) => $"{f.Leverancier.Trim()}|{f.Factuurnummer.Trim()}";

    private async Task<(IspPeiling? Peiling, List<IspFactuur> Goedgekeurd)> RondeAsync(
        CancellationToken ct, Action<string>? log, IspModus modus,
        IReadOnlyCollection<string>? alleen = null)
    {
        log ??= _ => { };
        var goedgekeurd = new List<IspFactuur>();
        if (InvoiceApprovalForm.IsOpen)
        {
            return (null, goedgekeurd);
        }
        await _slot.WaitAsync(ct);
        _gemeld.Clear(); // elke peiling logt zijn eigen loginstappen opnieuw
        Form? venster = null;
        WebView2? web = null;
        try
        {
            venster = new StilVenster
            {
                Size = new Size(1400, 1000), StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000), ShowInTaskbar = false,
            };
            web = new WebView2 { Dock = DockStyle.Fill };
            venster.Controls.Add(web);
            venster.Show();
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ispnext"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
            core.Navigate(InvoiceApprovalForm.InvoicesUrl);

            // Max. 40 s wachten tot de facturentabel staat. Komt er in plaats daarvan een
            // loginscherm (verlopen ISPnext-sessie), dan klikt de stille aanmelder zichzelf
            // naar binnen en krijgt hij daar ruimere tijd voor.
            IspPeiling? uit = null;
            var loginGezien = false;
            var invullen = true; // gaat uit na een geweigerd wachtwoord of één MFA-code
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (uit is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
                if (await Js(core, InvoiceApprovalForm.HeeftTabelScript) == "true")
                {
                    await Task.Delay(1500, ct); // rijen laten renderen
                    var json = await Js(core, InvoiceApprovalForm.ExtractScript);
                    if (json is not ("null" or ""))
                    {
                        uit = new IspPeiling(DateTimeOffset.Now, true, Parse(json));
                    }
                    continue;
                }
                if (await Js(core, InvoiceApprovalForm.LoginSchermScript) != "true")
                {
                    continue;
                }
                if (!loginGezien)
                {
                    loginGezien = true;
                    // Met een bewaarde CED-login loopt de hele keten (SSO-knop → accountkeuze
                    // → wachtwoord → MFA-code → "aangemeld blijven?") en dat duurt. Een koude
                    // login (alle schermen + redirects terug naar ISPnext) kan ruim twee
                    // minuten vragen; krapper afkappen liet de aanmelding net op de laatste
                    // redirect stranden. Zonder wachtwoord is het enkel doorklikken.
                    var metWachtwoord = CedLogin.Wachtwoord().Length > 0;
                    deadline = DateTime.UtcNow.AddSeconds(metWachtwoord ? 240 : 40);
                    log($"loginscherm op {core.Source} — stil aanmelden" +
                        (metWachtwoord ? "" : " (geen bewaard wachtwoord: alleen doorklikken)"));
                }
                if (!await StilleLoginStapAsync(core, log, invullen))
                {
                    // Wachtwoord geweigerd of MFA-code al één keer ingediend: vanaf hier niets
                    // meer invullen. Het is een CED-account — een tweede verkeerde poging kan
                    // het blokkeren, dus vanaf nu alleen nog kijken of de pagina alsnog opent.
                    invullen = false;
                }
            }
            // Loginscherm gezien maar niet binnengekomen: dat is een uitkomst ("sessie
            // verlopen"), geen leesfout — anders blijft de radar het dezelfde dag herkansen.
            if (uit is null)
            {
                log($"tijd om, niets gelezen — laatste pagina: {core.Source}");
                if (loginGezien)
                {
                    uit = new IspPeiling(DateTimeOffset.Now, false, []);
                }
            }
            if (uit is not null)
            {
                try
                {
                    File.WriteAllText(PeilingFile, JsonSerializer.Serialize(uit));
                }
                catch
                {
                    // Cache is comfort.
                }
            }
            if (modus != IspModus.Lezen && uit is { Aangemeld: true })
            {
                goedgekeurd = await GoedkeurAsync(core, uit, log, ct, modus == IspModus.Proef, alleen);
            }
            return (uit, goedgekeurd);
        }
        finally
        {
            try { web?.Dispose(); } catch { /* al weg */ }
            try { venster?.Dispose(); } catch { /* al weg */ }
            _slot.Release();
        }
    }

    /// <summary>
    /// Boven dit aantal facturen keurt de radar niets stil goed: zo'n lijst is ongewoon en
    /// hoort door Maarten zelf bekeken te worden (het venster waarschuwt daar ook voor).
    /// </summary>
    private const int VolumeGrens = 30;

    /// <summary>
    /// Keurt in de verborgen webview alles goed wat de regels automatisch goedkeuren, met
    /// precies dezelfde stappen als het venster: rijen aanvinken (op leverancier +
    /// factuurnummer) → Acties → Facturen goedkeuren → OK → de resultaatdialoog wegklikken.
    /// Twijfel betekent niets doen: wordt niet élke rij teruggevonden, is de lijst ongewoon
    /// lang, of ontbreekt een knop, dan wordt er niet goedgekeurd en blijft het handwerk.
    /// Wat wél verstuurd is, gaat in het goedkeuringslogboek — dat vangt dubbels bij een
    /// volgende ronde.
    /// </summary>
    private static async Task<List<IspFactuur>> GoedkeurAsync(
        CoreWebView2 core, IspPeiling peiling, Action<string> log, CancellationToken ct, bool proef,
        IReadOnlyCollection<string>? alleen)
    {
        var niets = new List<IspFactuur>();
        var auto = peiling.Facturen.Where(f => f.Auto).ToList();
        if (alleen is not null)
        {
            // Alleen wat bij het vragen op het scherm stond: is er intussen een factuur
            // bijgekomen, dan blijft die liggen tot de volgende ronde.
            var overgeslagen = auto.RemoveAll(f => !alleen.Contains(Sleutel(f)));
            if (overgeslagen > 0)
            {
                log($"{overgeslagen} factuur/facturen stonden niet in de bevestigde lijst — overgeslagen");
            }
        }
        if (auto.Count == 0)
        {
            log("geen enkele factuur voldoet aan de regels — niets stil goedgekeurd");
            return niets;
        }
        if (peiling.Facturen.Count > VolumeGrens)
        {
            log($"{peiling.Facturen.Count} facturen in de lijst (meer dan {VolumeGrens}): " +
                "ongewoon veel, dus niets stil goedgekeurd");
            return niets;
        }
        var doelen = JsonSerializer.Serialize(
            auto.Select(x => new { l = x.Leverancier, f = x.Factuurnummer }));
        var ontbreekt = TelOntbrekend(
            await Js(core, InvoiceApprovalForm.SelectScript.Replace("__TARGETS__", doelen)));
        if (ontbreekt is null or > 0)
        {
            log(ontbreekt is null
                ? "aanvinken mislukt: geen facturentabel in beeld — niets goedgekeurd"
                : $"{ontbreekt} van de {auto.Count} facturen niet teruggevonden in de tabel — " +
                  "niets goedgekeurd");
            return niets;
        }
        if (proef)
        {
            log(string.Create(Cultuur, $"proef: alle {auto.Count} facturen teruggevonden en " +
                $"aangevinkt (€ {auto.Sum(f => f.Bedrag ?? 0):N2}) — niets verstuurd"));
            return niets;
        }
        // Staat het Acties-menu al open, dan volstaat de menu-optie meteen.
        if (!await KlikAsync(core, "Facturen goedkeuren", 1000, ct) &&
            !(await KlikAsync(core, "Acties", 8000, ct) &&
              await KlikAsync(core, "Facturen goedkeuren", 8000, ct)))
        {
            log("'Acties → Facturen goedkeuren' niet gevonden — niets goedgekeurd");
            return niets;
        }
        if (!await KlikAsync(core, "OK", 10000, ct))
        {
            log("bevestiging 'OK' niet gevonden — niets goedgekeurd");
            return niets;
        }
        var totaal = auto.Sum(f => f.Bedrag ?? 0);
        log(string.Create(Cultuur, $"goedkeuring verstuurd: {auto.Count} facturen, € {totaal:N2}"));
        // Resultaatdialoog afwachten en wegklikken; de tekst erin is het bewijs van wat ISPnext
        // ervan vond en komt in het logboek te staan.
        for (var gewacht = 0; gewacht < 15000; gewacht += 1000)
        {
            await Task.Delay(1000, ct);
            var dialoog = await Js(core, InvoiceApprovalForm.DialogTextScript);
            if (dialoog is "null" or "")
            {
                continue;
            }
            log("resultaat ISPnext: " + dialoog.Trim('"').Replace("\\n", " · ").Replace("\\r", ""));
            await KlikAsync(core, "OK", 8000, ct);
            break;
        }
        ApprovalLog.Voeg(auto.Select(f => new Goedkeuring(
            DateTimeOffset.Now, f.Leverancier, f.Factuurnummer, f.Bedrag, true)));
        return auto;
    }

    /// <summary>Hoeveel aan te vinken rijen het script niet terugvond; null als er geen tabel was.</summary>
    private static int? TelOntbrekend(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("missing", out var m) ? m.GetArrayLength() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Klikt op het zichtbare element met deze tekst; wacht er zo nodig op.</summary>
    private static async Task<bool> KlikAsync(CoreWebView2 core, string tekst, int timeoutMs, CancellationToken ct)
    {
        var script = InvoiceApprovalForm.ClickByTextScript.Replace("__TEXT__", JsonSerializer.Serialize(tekst));
        for (var gewacht = 0; gewacht < timeoutMs; gewacht += 500)
        {
            if (await Js(core, script) == "true")
            {
                return true;
            }
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// Eén stille aanmeldstap op het scherm dat nu in beeld staat: dezelfde login-assistent als
    /// in het goedkeurvenster (gebruikersnaam + "Ga verder met Single Sign-On", of de juiste
    /// Microsoft-accounttegel) en, als <paramref name="invullen"/> nog mag en er een bruikbare
    /// CED-login klaarstaat, ook de Microsoft-schermen zelf — met de lokaal uit de TOTP-seed
    /// berekende MFA-code en "Aangemeld blijven? → ja".
    /// <para>
    /// Dit is het CED-account, dus hier wordt niets herhaald: false betekent "vul niets meer
    /// in". Dat gebeurt zodra Microsoft het wachtwoord weigert (dan zet
    /// <see cref="MicrosoftLogin"/> het invullen ook blijvend uit) én zodra er één MFA-code
    /// ingediend is — klopt die niet, dan is een reeks nieuwe codes precies de weg naar een
    /// geblokkeerd account. Zonder bewaard wachtwoord blijft het bij doorklikken: staat de
    /// Microsoft-sessie nog, dan is dat genoeg, en anders wordt er niets ingevuld.
    /// </para>
    /// </summary>
    private static async Task<bool> StilleLoginStapAsync(CoreWebView2 core, Action<string> log, bool invullen)
    {
        var assist = await Js(core, InvoiceApprovalForm.LoginAssistScript);
        Meld(log, "assistent", assist);
        if (!invullen || CedLogin.Wachtwoord().Length == 0)
        {
            return invullen; // zonder bewaard wachtwoord blijft het bij doorklikken
        }
        var stap = await Js(core, MicrosoftLogin.VulScript());
        Meld(log, "microsoft", stap);
        MicrosoftLogin.NaLoginStap(stap, null);
        var klaar = stap.Trim('"');
        if (klaar is "fout" or "mfa-ingevuld")
        {
            log(klaar == "fout"
                ? "wachtwoord geweigerd — stil aanmelden stopt hier (CED-account)"
                : "MFA-code ingediend — bij een afwijzing niet opnieuw proberen (CED-account)");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Logt één loginstap, maar elke uitkomst maar één keer: de lus draait elke seconde en
    /// zou het logboek anders volschrijven met dezelfde regel.
    /// </summary>
    private static void Meld(Action<string> log, string wie, string resultaat)
    {
        var tekst = resultaat.Trim('"');
        if (tekst is "" or "null" or "niets" or "wacht" || !_gemeld.Add($"{wie}:{tekst}"))
        {
            return;
        }
        log($"{wie}: {tekst}");
    }

    private static readonly HashSet<string> _gemeld = new(StringComparer.Ordinal);

    private static async Task<string> Js(CoreWebView2 core, string script)
    {
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch
        {
            return "null";
        }
    }

    private static List<IspFactuur> Parse(string json)
    {
        var regels = ApprovalRules.Load();
        var uit = new List<IspFactuur>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("invoices", out var invoices))
        {
            return uit;
        }
        foreach (var el in invoices.EnumerateArray())
        {
            string Get(string naam) => el.TryGetProperty(naam, out var v) ? v.GetString() ?? "" : "";
            var bedragText = Get("bedrag");
            var bedrag = InvoiceApprovalForm.ParseBedrag(bedragText);
            var leverancier = Get("leverancier");
            var factuurnummer = Get("factuurnummer");
            var valuta = Get("valuta");
            var (auto, reden) = ApprovalRules.Beoordeel(regels, leverancier, factuurnummer, bedrag, valuta);
            uit.Add(new IspFactuur(leverancier, factuurnummer, Get("factuurdatum"), Get("vervaldatum"),
                valuta, bedragText, bedrag,
                el.TryGetProperty("vervallen", out var w) && w.ValueKind == JsonValueKind.True, auto, reden));
        }
        return uit;
    }
}
