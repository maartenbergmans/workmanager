using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Opent de AVG/Avast CloudCare-console (de.cloudcare.avg.com) in een ingebedde browser met
/// blijvende sessie om een remote-supportsessie te starten. Bij een persoonlijke afzender
/// wordt in de apparatenlijst automatisch op de voornaam gezocht, het toestel geopend en op
/// "Connect" geklikt; komt de mail van een algemeen adres, dan wordt gewoon de volledige
/// apparatenlijst ("all devices") getoond zonder te zoeken. Autologin gebeurt best effort met
/// de bewaarde gegevens; lukt een stap niet, dan blijft het venster gewoon open zodat Maarten
/// het handmatig kan afronden (het uitlezen/klikken is bewust niet-blokkerend opgezet).
/// </summary>
public sealed class AvgCloudCareForm : Form
{
    private const string DevicesUrl = "https://de.cloudcare.avg.com/console.aspx#/devices/all";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _status;
    private readonly AvgSettings _settings = AvgSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _voornaam;
    private bool _ingelogd;
    private bool _gezocht;
    private bool _mfaStap; // verificatiecode gevraagd: vanaf hier niets meer invullen
    private bool _lusBezig; // NavigationCompleted vuurt meermaals; één lus tegelijk volstaat
    private int _loginPogingen;
    private int _naarDevicesPogingen;
    private int _zoekPogingen;

    /// <param name="voornaam">Voornaam om op te zoeken; leeg = alleen de apparatenlijst openen.</param>
    public AvgCloudCareForm(string voornaam)
    {
        _voornaam = voornaam.Trim();
        Text = _voornaam.Length > 0
            ? $"AVG CloudCare – supportsessie ({_voornaam})"
            : "AVG CloudCare – apparaten";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1300, 860);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _status = new Label { AutoSize = true, Padding = new Padding(4, 12, 0, 0) };
        Theme.AsStatus(_status);
        toolbar.Controls.Add(_status);

        Controls.Add(_web);
        Controls.Add(toolbar);
        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await InitAsync();
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        _web.DefaultBackgroundColor = Theme.Bg;
    }

    private void Status(string tekst)
    {
        if (!IsDisposed)
        {
            _status.Text = tekst;
        }
    }

    private async Task InitAsync()
    {
        try
        {
            Status("Browser starten…");
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-avg"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await OpPaginaAsync();
            Status("Naar de AVG-console…");
            _web.CoreWebView2.Navigate(DevicesUrl);
        }
        catch (Exception ex)
        {
            Status($"Browser starten mislukt: {ex.Message}");
        }
    }

    private async Task OpPaginaAsync()
    {
        if (_lusBezig)
        {
            return;
        }
        _lusBezig = true;
        try
        {
            await LusAsync();
        }
        finally
        {
            _lusBezig = false;
        }
    }

    private async Task LusAsync()
    {
        // De console is een SPA zonder navigatie-events tussen de stappen: een lus die eerst
        // (indien nodig) inlogt en daarna, bij een persoonlijke afzender, zoekt en verbindt.
        // Elke stap is best effort; mislukt hij, dan blijft het venster gewoon open. Ruim vijf
        // minuten, want de MFA-stap tikt Maarten zelf in.
        for (var i = 0; i < 300 && !IsDisposed; i++)
        {
            try
            {
                var url = await HuidigeUrlAsync();
                if (!_ingelogd && !_mfaStap && _loginPogingen < 3 && VraagtOmLogin(url))
                {
                    Status("Inloggen bij AVG CloudCare…");
                    _loginPogingen++;
                    await ProbeerLoginAsync();
                }
                else if (OpDevicesPagina(url) || await ToontApparatenlijstAsync())
                {
                    _ingelogd = true;
                    if (_voornaam.Length == 0)
                    {
                        Status("Apparatenlijst geopend — zoek en verbind zelf.");
                        return;
                    }
                    if (!_gezocht && _zoekPogingen < 4)
                    {
                        _zoekPogingen++;
                        Status($"Zoeken op \"{_voornaam}\"…");
                        _gezocht = await ZoekEnVerbindAsync();
                        if (_gezocht)
                        {
                            Status($"\"{_voornaam}\" gezocht — controleer en klik zo nodig zelf op Connect.");
                            return;
                        }
                        // Het grid tijd geven om te laden voor we het opnieuw proberen; anders
                        // onderbreken we telkens zijn eigen zoekopdracht.
                        await Task.Delay(3000, _cts.Token);
                    }
                    else if (!_gezocht)
                    {
                        Status($"Zoek zelf op \"{_voornaam}\" — het filterveld reageert niet zoals verwacht.");
                        return;
                    }
                }
                else if (OpConsole(url))
                {
                    // Ingelogd, maar de console gooit je op haar eigen startpagina in plaats van
                    // op de apparatenlijst waar we naartoe navigeerden. Zelf doorklikken dus.
                    _ingelogd = true;
                    if (_naarDevicesPogingen < 8)
                    {
                        _naarDevicesPogingen++;
                        Status("Naar de apparatenlijst…");
                        await NaarDevicesAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Volgende ronde opnieuw proberen.
            }
            await Task.Delay(1000, _cts.Token);
        }
    }

    private static bool VraagtOmLogin(string url) =>
        url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("signin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Op de console (ingelogd), maar niet noodzakelijk op de apparatenlijst.</summary>
    private static bool OpConsole(string url) =>
        url.Contains("cloudcare", StringComparison.OrdinalIgnoreCase) && !VraagtOmLogin(url);

    private static bool OpDevicesPagina(string url) =>
        url.Contains("cloudcare", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("device", StringComparison.OrdinalIgnoreCase);

    private async Task ProbeerLoginAsync()
    {
        var gebruiker = System.Text.Json.JsonSerializer.Serialize(_settings.Gebruiker);
        var wachtwoord = System.Text.Json.JsonSerializer.Serialize(_settings.Wachtwoord);
        // Generieke invulroutine: het zichtbare e-mail-/gebruikersveld en het wachtwoordveld
        // vullen en de aanmeldknop klikken. Een veld dat al de juiste waarde bevat wordt niet
        // opnieuw gezet (voorkomt herhaald submitten bij een trage pagina).
        //
        // Het MFA-scherm heeft óók een gewoon tekstveld. Zonder de codeAchtig-test hieronder
        // belandt het e-mailadres daarin en kan Maarten zijn code niet meer intikken; daarom
        // laat de app verificatievelden onaangeroerd en meldt ze gewoon 'mfa' terug.
        var script =
            $$"""
            (() => {
                const zichtbaar = el => el && el.offsetParent !== null && !el.disabled;
                const codeAchtig = el => {
                    const t = [el.name, el.id, el.placeholder, el.getAttribute('aria-label'),
                               el.getAttribute('autocomplete'), el.getAttribute('inputmode')]
                        .join(' ').toLowerCase();
                    if (/code|otp|token|verif|mfa|\bpin\b|2fa|one.?time|authenticat|numeric/.test(t)) return true;
                    const max = parseInt(el.getAttribute('maxlength') || '0', 10);
                    return max > 0 && max <= 10; // codevakjes zijn kort, een e-mailadres nooit
                };
                const zet = (el, v) => {
                    const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                    s.call(el, v);
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                };
                const pass = [...document.querySelectorAll('input[type=password]')].find(zichtbaar);
                const user = [...document.querySelectorAll(
                    'input[type=email], input[name*=user i], input[name*=email i], input[autocomplete=username], input[type=text]')]
                    .find(el => zichtbaar(el) && !codeAchtig(el));
                // Verificatiestap: geen wachtwoordveld meer, wél een codeveld. Handen eraf.
                if (!pass && [...document.querySelectorAll('input')].some(el => zichtbaar(el) && codeAchtig(el))) {
                    return 'mfa';
                }
                let deed = '';
                if (user && user.value !== {{gebruiker}}) { zet(user, {{gebruiker}}); deed += 'user '; }
                if (pass && pass.value !== {{wachtwoord}}) { zet(pass, {{wachtwoord}}); deed += 'pass '; }
                // Aanmeldknop: submit-knop of een knop met inlog-achtige tekst.
                const knop = [...document.querySelectorAll('button, input[type=submit], a[role=button]')]
                    .find(b => zichtbaar(b) && /log ?in|sign ?in|aanmelden|inloggen|next|continue|submit/i
                        .test(((b.innerText || b.value || '') + '').trim()));
                // Alleen klikken als beide velden ingevuld staan (of er geen wachtwoordveld is).
                if (knop && (!pass || pass.value.length > 0) && (!user || user.value.length > 0)) {
                    setTimeout(() => knop.click(), 400);
                    deed += 'klik';
                }
                return deed || 'niets';
            })()
            """;
        var res = await RunAsync(script);
        if (res is not null && res.Contains("mfa", StringComparison.OrdinalIgnoreCase))
        {
            _mfaStap = true;
            Status("Verificatiecode nodig — vul die zelf in; de app blijft van het veld af.");
        }
        await Task.Delay(1500, _cts.Token);
    }

    /// <summary>
    /// De URL zoals de pagina hem zelf ziet. Bewust niet <c>_web.Source</c>: de console is een
    /// hash-SPA en wisselt van scherm zonder dat WebView2 daar een navigatie van maakt.
    /// </summary>
    private async Task<string> HuidigeUrlAsync()
    {
        var ruw = await RunAsync("location.href");
        if (ruw is null)
        {
            return _web.Source?.ToString() ?? "";
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(ruw) ?? ruw;
        }
        catch (System.Text.Json.JsonException)
        {
            return ruw;
        }
    }

    /// <summary>
    /// Staat de apparatenlijst op het scherm? De klassieke console werkt met postbacks en zet de
    /// hash niet altijd mee, dus de URL alleen is geen betrouwbaar antwoord — en zonder deze test
    /// bleef de app om de seconde opnieuw op de Devices-tab klikken, waardoor het grid nooit
    /// uitgeladen raakte.
    /// </summary>
    private async Task<bool> ToontApparatenlijstAsync()
    {
        var res = await RunAsync(
            """
            (() => {
                const tekst = document.body ? document.body.innerText : '';
                if (/all devices|alle apparaten|device views/i.test(tekst)) return 'ja';
                const tab = [...document.querySelectorAll('a, li, button, [role=tab]')]
                    .find(e => /^(devices|apparaten)$/i.test((e.innerText || '').trim()) &&
                        /active|selected|current/i.test(
                            e.className + ' ' + (e.parentElement ? e.parentElement.className : '')));
                return tab ? 'ja' : 'nee';
            })()
            """);
        return res is not null && res.Contains("ja", StringComparison.Ordinal);
    }

    /// <summary>
    /// Van de startpagina van de console naar de apparatenlijst. Eerst het menu-item aanklikken —
    /// dat is wat de SPA verwacht — en pas als dat er niet staat de hash zelf zetten.
    /// </summary>
    private async Task NaarDevicesAsync()
    {
        var script =
            """
            (() => {
                const zichtbaar = el => el && el.offsetParent !== null;
                const link = [...document.querySelectorAll('a, button, [role=menuitem], [role=tab], li')]
                    .find(el => zichtbaar(el) &&
                        (/#\/devices/i.test(el.getAttribute('href') || '') ||
                         /^(devices|apparaten|geräte|appareils)$/i.test((el.innerText || '').trim())));
                if (link) { link.click(); return 'geklikt'; }
                if (!location.hash.startsWith('#/devices')) {
                    location.hash = '#/devices/all';
                    return 'hash';
                }
                return 'niets';
            })()
            """;
        await RunAsync(script);
        await Task.Delay(1500, _cts.Token);
    }

    private async Task<bool> ZoekEnVerbindAsync()
    {
        var naam = System.Text.Json.JsonSerializer.Serialize(_voornaam);
        // Best effort: het zoekveld vullen met de voornaam en Enter sturen; daarna proberen de
        // eerste rij die de voornaam bevat te openen. De klik op "Connect" laten we bewust aan
        // Maarten over (bevestiging), maar we markeren de rij wel.
        var script =
            $$"""
            (() => {
                const zichtbaar = el => el && el.offsetParent !== null && !el.disabled && !el.readOnly;
                // Op volgorde van zekerheid: een echt zoekveld eerst, een willekeurig tekstveld
                // pas als laatste redmiddel. Anders vult de app de eerste de beste invoer op de
                // pagina en gebeurt er zichtbaar niets.
                const kandidaten = [
                    'input[type=search]',
                    'input[placeholder*=search i], input[placeholder*=zoek i], input[placeholder*=filter i]',
                    'input[aria-label*=search i], input[aria-label*=zoek i], input[aria-label*=filter i]',
                    'input[name*=search i], input[id*=search i], input[class*=search i]',
                    'input[type=text]',
                ];
                let zoek = null;
                for (const sel of kandidaten) {
                    zoek = [...document.querySelectorAll(sel)].find(zichtbaar);
                    if (zoek) break;
                }
                if (!zoek) return 'geen-zoekveld';
                // Staat de filter er al (in het veld of als actief filtercriterium), dan niet
                // opnieuw tikken: het grid begint dan telkens van voren af aan te laden en komt
                // nooit tot rust.
                if (zoek.value === {{naam}}) return 'gezocht';
                const criteria = document.body.innerText || '';
                if (/current filter criteria|huidige filtercriteria/i.test(criteria) &&
                    criteria.toLowerCase().includes({{naam}}.toLowerCase())) {
                    return 'gezocht';
                }
                const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                zoek.focus();
                s.call(zoek, {{naam}});
                zoek.dispatchEvent(new Event('input', { bubbles: true }));
                zoek.dispatchEvent(new Event('change', { bubbles: true }));
                zoek.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true }));
                zoek.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', keyCode: 13, bubbles: true }));
                // Pas 'gezocht' melden als de waarde ook echt blijft staan; sommige grids wissen
                // hun filter bij het herladen en dan moet de volgende ronde het opnieuw proberen.
                return zoek.value === {{naam}} ? 'gezocht' : 'niet-blijven-staan';
            })()
            """;
        var res = await RunAsync(script);
        return res is "\"gezocht\"";
    }

    private async Task<string?> RunAsync(string script)
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
