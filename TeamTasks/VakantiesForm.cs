using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Haalt de teamvakanties uit de SD Worx-teamkalender (myworkandme.com) via een ingebedde
/// browser met blijvende sessie: automatische login met de bewaarde gegevens, daarna wordt
/// de kalender van de volgende werkweek uitgelezen (zo nodig over de maandgrens heen).
/// Het resultaat staat onderaan als bewerkbare tekst, klaar om in de weekmail-opmerking
/// te zetten. Bij een onverwachte loginstap (bv. MFA) kan er gewoon handmatig ingelogd
/// worden in het venster; het uitlezen gaat daarna vanzelf verder.
/// </summary>
public class VakantiesForm : Form
{
    private const string KalenderUrl =
        "https://www.myworkandme.com/ebloxhr/hrwwevo/#/-ebloxhr-hrwwevo-TeamAbsence-Calendar";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly TextBox _resultaat;
    private readonly ModernButton _okButton;
    private readonly SdWorxSettings _settings = SdWorxSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _alleenInspecteren;
    private readonly bool _achtergrond;
    private bool _bezig;
    private bool _klaar;
    private bool _mislukt;

    /// <summary>De (eventueel bijgewerkte) samenvatting voor de weekmail-opmerking.</summary>
    public string VakantieTekst => _resultaat.Text.Trim();

    /// <summary>Klaar met uitlezen (resultaat staat in <see cref="VakantieTekst"/>).</summary>
    public bool IsKlaar => _klaar;

    /// <summary>Automatisch ophalen is gestrand (bv. MFA of geen sessie).</summary>
    public bool IsMislukt => _mislukt;

    /// <summary>
    /// Probeert de teamvakanties op de achtergrond op te halen (donderdagroutine): een
    /// onzichtbaar venster dat de bewaarde sessie/autologin gebruikt en stil afbreekt als er
    /// MFA of handmatige login nodig is (geen herhaalde loginpogingen → geen lockout-risico).
    /// Geeft de samenvatting terug, of null als het niet lukte.
    /// </summary>
    public static async Task<string?> ProbeerAchtergrondAsync(CancellationToken ct)
    {
        using var form = new VakantiesForm(achtergrond: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };
        form.Show();
        for (var i = 0; i < 150 && !form.IsDisposed; i++) // max ~2,5 min
        {
            if (form.IsKlaar || form.IsMislukt)
            {
                break;
            }
            await Task.Delay(1000, ct);
        }
        var resultaat = form.IsKlaar ? form.VakantieTekst : null;
        if (!form.IsDisposed)
        {
            form.Close();
        }
        return string.IsNullOrWhiteSpace(resultaat) ? null : resultaat;
    }

    public VakantiesForm(bool alleenInspecteren = false, bool achtergrond = false)
    {
        _alleenInspecteren = alleenInspecteren;
        _achtergrond = achtergrond;
        Text = "Vakanties team – SD Worx";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1200, 800);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        _status.Padding = new Padding(4, 14, 0, 0);
        toolbar.Controls.Add(_status);

        var resultaatGroup = new ModernGroupBox
        {
            Text = "Afwezigheden komende drie weken (bewerkbaar)",
            Dock = DockStyle.Bottom,
            Height = 170,
            Padding = new Padding(10, 8, 10, 10),
        };
        _resultaat = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        resultaatGroup.Controls.Add(_resultaat);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        _okButton = new ModernButton
        {
            Text = "In opmerking zetten", Width = 180, Kind = ButtonKind.Accent,
            Glyph = Fluent.Kalender, Enabled = false,
        };
        _okButton.Click += (_, _) =>
        {
            if (VakantieTekst.Length > 0)
            {
                DialogResult = DialogResult.OK;
            }
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_okButton);
        CancelButton = cancel;

        Controls.Add(_web);
        Controls.Add(resultaatGroup);
        Controls.Add(buttons);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await InitWebViewAsync();
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        _web.DefaultBackgroundColor = Theme.Bg;
    }

    private void Status(string tekst)
    {
        if (!IsDisposed)
        {
            _status.Text = tekst;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _pulse.Actief = true;
            Status("Browser starten…");
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-sdworx"));
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    await OnPaginaAsync();
                }
            };

            Status("Naar de teamkalender…");
            _web.CoreWebView2.Navigate(KalenderUrl);
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Browser starten mislukt: {ex.Message}");
        }
    }

    private async Task OnPaginaAsync()
    {
        if (IsDisposed || _bezig || _klaar)
        {
            return;
        }
        _bezig = true;
        try
        {
            // Op de kalenderpagina: wachten tot de SPA de rijen rendert; anders de loginflow.
            var kalender = OpKalenderPagina ? await WachtOpKalenderAsync(pogingen: 12) : null;
            if (kalender is null)
            {
                await ProbeerLoginAsync();
                return;
            }
            await LeesWeekAsync(kalender);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Uitlezen mislukt: {ex.Message}");
        }
        finally
        {
            _bezig = false;
        }
    }

    // ---------- Kalender uitlezen ----------

    private async Task LeesWeekAsync(SdWorxVakanties.MaandData eerste)
    {
        Status("Teamkalender uitlezen…");
        // Gemeld worden afwezigheden die binnen drie werkweken beginnen; om de terugkeer-
        // datum te vinden bladeren we zo ver vooruit als nodig (max. ~zes maanden).
        var maandag = SdWorxVakanties.VolgendeMaandag(DateOnly.FromDateTime(DateTime.Now));
        var meldEinde = maandag.AddDays(SdWorxVakanties.VensterDagen);
        var verzamelGrens = maandag.AddDays(200);
        static DateOnly EindeVanMaand(DateOnly start) =>
            new(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));

        var afwezig = SdWorxVakanties.VerzamelAfwezig(eerste, maandag, verzamelGrens, _settings.UitgeslotenNamen);
        var nietWerkLijst = SdWorxVakanties.VerzamelNietWerk(eerste, maandag, verzamelGrens);
        var feestdagen = SdWorxVakanties.VerzamelFeestdagen(eerste, maandag, meldEinde);

        Dictionary<string, HashSet<DateOnly>> NietWerk() => nietWerkLijst
            .GroupBy(n => n.Naam)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Datum).ToHashSet());

        var huidig = eerste;
        for (var extra = 0; extra < 6; extra++)
        {
            var horizon = EindeVanMaand(huidig.MaandStart);
            var verderNodig = meldEinde > horizon ||
                SdWorxVakanties.BouwReeksen(afwezig, NietWerk()).Any(r =>
                    r.Van <= meldEinde && r.Tot >= horizon.AddDays(-3));
            if (!verderNodig)
            {
                break;
            }
            Status($"Verder bladeren voor terugkeerdata ({huidig.MaandStart.AddMonths(1):MMMM})…");
            await RunScriptAsync("""document.querySelector('[data-bind*="gotoNextMonth"]')?.click()""");
            var volgende = await WachtOpAndereMaandAsync(huidig.MaandStart);
            if (volgende is null)
            {
                break;
            }
            afwezig.AddRange(SdWorxVakanties.VerzamelAfwezig(
                volgende, maandag, verzamelGrens, _settings.UitgeslotenNamen));
            nietWerkLijst.AddRange(SdWorxVakanties.VerzamelNietWerk(volgende, maandag, verzamelGrens));
            if (volgende.MaandStart <= meldEinde)
            {
                feestdagen.AddRange(SdWorxVakanties.VerzamelFeestdagen(volgende, maandag, meldEinde));
            }
            huidig = volgende;
        }

        _klaar = true;
        _pulse.Actief = false;
        var tekst = SdWorxVakanties.BouwSamenvatting(
            afwezig, feestdagen, maandag, EindeVanMaand(huidig.MaandStart), NietWerk());
        _resultaat.Text = tekst.Length > 0
            ? tekst.ReplaceLineEndings("\r\n")
            : "";
        _okButton.Enabled = tekst.Length > 0;
        Status(tekst.Length > 0
            ? $"Klaar — afwezigheden voor {maandag:dd/MM} t.e.m. {meldEinde:dd/MM}. Kijk na en klik 'In opmerking zetten'."
            : $"Klaar — geen afwezigheden gevonden voor {maandag:dd/MM} t.e.m. {meldEinde:dd/MM}.");
        if (tekst.Length == 0)
        {
            Toast.Toon(this, "Niemand afwezig de komende drie weken 🎉", Fluent.Kalender);
        }
    }

    private async Task<SdWorxVakanties.MaandData?> WachtOpKalenderAsync(int pogingen)
    {
        for (var i = 0; i < pogingen; i++)
        {
            var data = await LeesMaandAsync();
            if (data is not null)
            {
                return data;
            }
            await Task.Delay(700, _cts.Token);
        }
        return null;
    }

    private async Task<SdWorxVakanties.MaandData?> WachtOpAndereMaandAsync(DateOnly vorige)
    {
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(700, _cts.Token);
            var data = await LeesMaandAsync();
            if (data is not null && data.MaandStart != vorige)
            {
                return data;
            }
        }
        return null;
    }

    // Rij per medewerker (tr.employee): cel 0 = naam, cellen 1..n = dagcode per dag van de maand.
    // De getoonde periode staat als "01/07/2026 - 31/07/2026" in de paginakop.
    private const string LeesScript =
        """
        (() => {
            const rijen = [...document.querySelectorAll('tr.employee')].map(tr => {
                const cellen = [...tr.querySelectorAll('td')];
                return {
                    naam: (cellen[0].innerText || '').trim().replace(/\s+/g, ' '),
                    dagen: cellen.slice(1).map(td => (td.innerText || '').trim()),
                };
            });
            const m = document.body.innerText.match(/(\d{2})\/(\d{2})\/(\d{4})\s*-\s*\d{2}\/\d{2}\/\d{4}/);
            if (!m || rijen.length === 0) return null;
            return { start: m[3] + '-' + m[2] + '-' + m[1], rijen };
        })()
        """;

    private async Task<SdWorxVakanties.MaandData?> LeesMaandAsync()
    {
        var raw = await RunScriptAsync(LeesScript);
        if (raw is null or "null")
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!DateOnly.TryParse(root.GetProperty("start").GetString(), out var start))
            {
                return null;
            }
            var rijen = new List<SdWorxVakanties.MaandRij>();
            foreach (var el in root.GetProperty("rijen").EnumerateArray())
            {
                rijen.Add(new SdWorxVakanties.MaandRij(
                    el.GetProperty("naam").GetString() ?? "",
                    el.GetProperty("dagen").EnumerateArray().Select(d => d.GetString() ?? "").ToList()));
            }
            return new SdWorxVakanties.MaandData(start, rijen);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Login-assist ----------

    private bool OpKalenderPagina =>
        (_web.Source?.ToString() ?? "").Contains("/ebloxhr/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drijft de loginflow: privacybanner weigeren, gebruikersnaam en wachtwoord invullen
    /// en doorklikken — in een lus, want de SD Worx-login is een SPA zonder navigatie-events
    /// tussen de stappen. Stopt zodra de kalender verschijnt. MFA blijft handwerk in het
    /// venster; de lus pikt het daarna vanzelf weer op.
    /// </summary>
    private async Task ProbeerLoginAsync()
    {
        if (_alleenInspecteren)
        {
            // Diagnosemodus: geen wachtwoord invullen. Alleen het e-mailveld van stap 1
            // wordt (één keer) ingevuld om de structuur van stap 2 te kunnen vastleggen.
            Status("Inspectiemodus — wachtwoord wordt niet ingevuld.");
            var stap1Gezet = false;
            for (var i = 0; i < 20; i++)
            {
                var dump = await RunScriptAsync(DumpScript);
                if (!string.IsNullOrEmpty(dump) && dump != "null")
                {
                    File.WriteAllText(
                        Path.Combine(DataDir, $"sdworx-dump-{(stap1Gezet ? 2 : 1)}.json"),
                        JsonSerializer.Deserialize<JsonElement>(dump).GetRawText());
                }
                if (!stap1Gezet && dump is not null && dump.Contains("lp_login-email"))
                {
                    var vulEmail =
                        """
                        (() => {
                            const veld = document.querySelector('#lp_login-email');
                            const knop = document.querySelector('#lp_next');
                            if (!veld) return 'geen veld';
                            const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                            s.call(veld, __USER__);
                            veld.dispatchEvent(new Event('input', { bubbles: true }));
                            veld.dispatchEvent(new Event('change', { bubbles: true }));
                            setTimeout(() => { const k = document.querySelector('#lp_next'); if (k && !k.disabled) k.click(); }, 500);
                            return 'email gezet';
                        })()
                        """.Replace("__USER__", JsonSerializer.Serialize(_settings.Gebruiker));
                    await RunScriptAsync(vulEmail);
                    stap1Gezet = true;
                    Status("E-mail ingevuld — structuur van stap 2 vastleggen…");
                }
                await Task.Delay(1500, _cts.Token);
            }
            _pulse.Actief = false;
            Status("Inspectie klaar — structuur weggeschreven.");
            return;
        }

        if (_settings.Gebruiker.Length == 0 || _settings.Wachtwoord.Length == 0)
        {
            _pulse.Actief = false;
            _mislukt = true;
            Status("Geen SD Worx-inloggegevens gevonden — log handmatig in; daarna gaat het vanzelf verder.");
            return;
        }

        Status("Inloggen bij SD Worx…");
        var script = LoginScript
            .Replace("__USER__", JsonSerializer.Serialize(_settings.Gebruiker))
            .Replace("__PASS__", JsonSerializer.Serialize(_settings.Wachtwoord));
        var wachtwoordWachtrondes = 0;
        var herNavigaties = 0;
        for (var poging = 0; poging < 75 && !_klaar; poging++)
        {
            if (await LeesMaandAsync() is { } kalender)
            {
                await LeesWeekAsync(kalender);
                return;
            }
            var url = _web.Source?.ToString() ?? "";
            // Na de login landt de app op het dashboard (de hash-route gaat verloren
            // in de redirect); dan opnieuw naar de teamkalender sturen.
            if (OpKalenderPagina &&
                !url.Contains("TeamAbsence", StringComparison.OrdinalIgnoreCase) &&
                herNavigaties < 3)
            {
                herNavigaties++;
                Status("Naar de teamkalender…");
                _web.CoreWebView2?.Navigate(KalenderUrl);
                await Task.Delay(1500, _cts.Token);
                continue;
            }
            // Alleen invullen op de SD Worx-loginpagina's, nooit ergens anders.
            if (url.Contains("auth.sdworx.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("login", StringComparison.OrdinalIgnoreCase) && !OpKalenderPagina)
            {
                var resultaat = await RunScriptAsync(script);
                Status(resultaat switch
                {
                    "\"cookies\"" => "Privacybanner geweigerd…",
                    "\"gebruiker\"" => "E-mailadres ingevuld…",
                    "\"gebruiker-wacht\"" => "Wachten op de wachtwoordstap…",
                    "\"wachtwoord\"" => "Wachtwoord ingevuld — aanmelden…",
                    "\"wachtwoord-wacht\"" => "Aangemeld — wachten op de kalender…",
                    _ => "Inloggen bij SD Worx…",
                });
                if (resultaat is "\"wachtwoord-wacht\"" && ++wachtwoordWachtrondes > 15)
                {
                    // Wachtwoord is één keer gesubmit maar de kalender komt niet: MFA of een
                    // foutmelding. Bewust niet opnieuw proberen (accountblokkering vermijden).
                    _pulse.Actief = false;
                    _mislukt = true;
                    Status("Aangemeld maar de kalender verschijnt niet (MFA of foutmelding?) — " +
                           "werk verder in het venster; het uitlezen gaat daarna vanzelf door.");
                    return;
                }
            }
            await Task.Delay(1200, _cts.Token);
        }
        if (!_klaar)
        {
            _pulse.Actief = false;
            _mislukt = true;
            Status("Automatisch inloggen lukte niet — log handmatig in; daarna gaat het vanzelf verder.");
        }
    }

    // Exact afgestemd op de SD Worx-loginpagina's (auth.sdworx.com, geverifieerd via de
    // inspectiemodus): stap 1 = #lp_login-email + #lp_next, stap 2 = #lp_login-password +
    // #lp_next. Een veld wordt maar één keer gevuld en gesubmit ('…-wacht' daarna), zodat
    // een haperende pagina nooit tot herhaalde loginpogingen leidt.
    private const string LoginScript =
        """
        (() => {
            // Privacybanner (in shadow DOM) eerst weigeren.
            const zoekOveral = selector => {
                const uit = [];
                const loop = root => {
                    uit.push(...root.querySelectorAll(selector));
                    root.querySelectorAll('*').forEach(el => {
                        if (el.shadowRoot) loop(el.shadowRoot);
                    });
                };
                loop(document);
                return uit;
            };
            const weiger = zoekOveral('button, a')
                .find(e => /^(alles )?weigeren$|^(refuse|decline|reject)( all)?$/i
                    .test((e.innerText || '').trim()));
            if (weiger) { weiger.click(); return 'cookies'; }

            const zet = (el, v) => {
                const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                s.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const klikVolgende = () => setTimeout(() => {
                const k = document.querySelector('#lp_next');
                if (k && !k.disabled) k.click();
            }, 400);

            const wachtwoord = document.querySelector('#lp_login-password');
            if (wachtwoord && wachtwoord.offsetParent !== null) {
                if (wachtwoord.value === __PASS__) return 'wachtwoord-wacht';
                zet(wachtwoord, __PASS__);
                klikVolgende();
                return 'wachtwoord';
            }
            const email = document.querySelector('#lp_login-email');
            if (email && email.offsetParent !== null) {
                if (email.value === __USER__) return 'gebruiker-wacht';
                zet(email, __USER__);
                klikVolgende();
                return 'gebruiker';
            }
            return null;
        })()
        """;

    // Beschrijft alle invoervelden en knoppen (ook in shadow DOM/iframes) zonder waarden
    // te lezen of te wijzigen — alleen structuur, voor het afstemmen van de login-assist.
    private const string DumpScript =
        """
        (() => {
            const uit = { url: location.href, velden: [], knoppen: [] };
            const beschrijfVeld = e => ({
                tag: e.tagName, type: e.type || '', name: e.name || '', id: e.id || '',
                autocomplete: e.autocomplete || '', placeholder: e.placeholder || '',
                aria: e.getAttribute('aria-label') || '',
                label: (e.labels && e.labels[0] ? e.labels[0].innerText : '').trim().slice(0, 40),
                zichtbaar: e.offsetParent !== null, disabled: !!e.disabled,
            });
            const beschrijfKnop = e => ({
                tag: e.tagName, type: e.type || '',
                tekst: ((e.innerText || e.value || '') + '').trim().slice(0, 40),
                id: e.id || '', zichtbaar: e.offsetParent !== null, disabled: !!e.disabled,
            });
            const loop = root => {
                root.querySelectorAll('input, select, textarea').forEach(e => uit.velden.push(beschrijfVeld(e)));
                root.querySelectorAll('button, input[type=submit], a[role=button]').forEach(e => uit.knoppen.push(beschrijfKnop(e)));
                root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) loop(el.shadowRoot); });
            };
            loop(document);
            for (const fr of document.querySelectorAll('iframe')) {
                try { if (fr.contentDocument) loop(fr.contentDocument); } catch (e) { }
            }
            return uit;
        })()
        """;

    private async Task<string?> RunScriptAsync(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null)
        {
            return null;
        }
        try
        {
            return await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            return null;
        }
    }
}
