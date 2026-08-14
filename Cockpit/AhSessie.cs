using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Eén blijvende (meestal onzichtbare) ah.be-browsersessie in de tray-app, zoals de
/// Outlook-koppeling: AH's "Even controleren"-herbevestiging voor accountpagina's is
/// sessiegebonden, dus een wegwerpbrowser per check verliest de login telkens weer. Hier
/// logt Maarten één keer in (venster verschijnt on-screen) en daarna leest de bezorgradar
/// in dezelfde levende sessie /mijnbestellingen uit. Sluiten van het venster = verbergen;
/// de sessie blijft op de achtergrond bestaan tot de app stopt.
/// </summary>
public sealed class AhSessie
{
    public static AhSessie Instance { get; } = new();

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private Form? _venster;
    private WebView2? _web;
    private bool _zichtbaar;
    private readonly SemaphoreSlim _slot = new(1);

    private AhSessie()
    {
    }

    /// <summary>Of het loginvenster nu on-screen staat (dan blijft de radar er even af).</summary>
    public bool VensterZichtbaar => _zichtbaar;

    /// <summary>
    /// Haalt de paginatekst van /mijnbestellingen op in de levende sessie. Leeg of
    /// "FOUT: …" als het niet lukt. Alleen vanaf de UI-thread gebruiken (WebView2).
    /// </summary>
    public async Task<string> BestellingenTekstAsync(CancellationToken ct)
    {
        if (_zichtbaar)
        {
            return ""; // Maarten is (mogelijk) aan het inloggen: niet onder zijn handen navigeren
        }
        await _slot.WaitAsync(ct);
        try
        {
            if (!await InitAsync(ct))
            {
                return "FOUT: de ingebedde AH-browser start niet";
            }
            _web!.CoreWebView2!.Navigate("https://www.ah.be/mijnbestellingen");
            var tekst = "";
            var loginGeprobeerd = false;
            for (var poging = 0; poging < 30; poging++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                var raw = await _web.CoreWebView2.ExecuteScriptAsync($$"""
                    (function () {
                        {{AhWinkelForm.CookieJs}}
                        return document.body ? document.body.innerText : '';
                    })()
                    """);
                tekst = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                if (tekst.Contains("verwacht tussen", StringComparison.OrdinalIgnoreCase))
                {
                    break; // het bezorgvenster staat er — klaar
                }
                // Loginscherm ("Even controleren")? Eén keer zelf inloggen met de bewaarde
                // gegevens; alleen een captcha-challenge houdt dat tegen (dan blijft de
                // pagina staan en volgt de gewone venster-route).
                if (!loginGeprobeerd && LijktLoginPagina(tekst) &&
                    AhLoginSettings.Load() is { Compleet: true } login)
                {
                    loginGeprobeerd = true;
                    var uitkomst = await _web.CoreWebView2.ExecuteScriptAsync(
                        LoginScript(login.Email, login.Wachtwoord));
                    await Task.Delay(3000, ct);
                    LaatsteLoginDiagnose = $"{DateTime.Now:HH:mm:ss} script={uitkomst} " +
                        $"url-daarna={_web.CoreWebView2.Source}";
                }
            }
            return tekst;
        }
        catch (Exception ex)
        {
            return "FOUT: " + ex.Message;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Verzamelt de productlinks van /producten/eerder-gekocht in de levende sessie. De pagina
    /// laadt lazy, dus er wordt elke poll naar beneden gescrold tot het aantal links een paar
    /// keer op rij stabiel is. Lege lijst als het niet lukt (bv. login vereist en de
    /// auto-login komt er niet doorheen). Alleen vanaf de UI-thread gebruiken (WebView2).
    /// </summary>
    public async Task<(List<string> Links, string Tekst)> EerderGekochtLinksAsync(CancellationToken ct)
    {
        if (_zichtbaar)
        {
            return (new List<string>(), ""); // Maarten is (mogelijk) aan het inloggen
        }
        await _slot.WaitAsync(ct);
        var paginaTekst = "";
        try
        {
            if (!await InitAsync(ct))
            {
                return (new List<string>(), "browser start niet");
            }
            _web!.CoreWebView2!.Navigate("https://www.ah.be/producten/eerder-gekocht");
            var loginGeprobeerd = false;
            var links = new List<string>();
            var vorigAantal = -1;
            var stabiel = 0;
            for (var poging = 0; poging < 45; poging++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                var raw = await _web.CoreWebView2.ExecuteScriptAsync($$"""
                    (function () {
                        {{AhWinkelForm.CookieJs}}
                        window.scrollTo(0, document.body ? document.body.scrollHeight : 0);
                        var uniek = {};
                        var links = document.querySelectorAll('a[href*="/producten/product/"]');
                        for (var i = 0; i < links.length; i++) {
                            var href = (links[i].getAttribute('href') || '').split('?')[0];
                            if (/\/product\/wi\d+/.test(href)) { uniek[href] = 1; }
                        }
                        return JSON.stringify({
                            tekst: document.body ? document.body.innerText.slice(0, 1500) : '',
                            links: Object.keys(uniek)
                        });
                    })()
                    """);
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                if (json.Length == 0)
                {
                    continue;
                }
                string tekst;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    tekst = doc.RootElement.TryGetProperty("tekst", out var t) ? t.GetString() ?? "" : "";
                    paginaTekst = tekst;
                    if (doc.RootElement.TryGetProperty("links", out var lijst) &&
                        lijst.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        links = lijst.EnumerateArray()
                            .Select(el => el.GetString())
                            .OfType<string>()
                            .ToList();
                    }
                }
                catch
                {
                    continue;
                }
                if (!loginGeprobeerd && LijktLoginPagina(tekst) &&
                    AhLoginSettings.Load() is { Compleet: true } login)
                {
                    loginGeprobeerd = true;
                    await _web.CoreWebView2.ExecuteScriptAsync(
                        LoginScript(login.Email, login.Wachtwoord));
                    await Task.Delay(3000, ct);
                    continue;
                }
                // Klaar zodra het aantal links een paar polls op rij niet meer groeit.
                if (links.Count > 0 && links.Count == vorigAantal)
                {
                    if (++stabiel >= 4)
                    {
                        break;
                    }
                }
                else
                {
                    stabiel = 0;
                }
                vorigAantal = links.Count;
            }
            // Relatieve links ("/producten/product/…") normaliseren naar volledige urls.
            return (links
                .Select(l => l.StartsWith('/') ? "https://www.ah.be" + l : l)
                .ToList(), paginaTekst);
        }
        catch (Exception ex)
        {
            return (new List<string>(), "FOUT: " + ex.Message);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Toont het sessievenster on-screen zodat Maarten kan inloggen. Sluiten verbergt het
    /// alleen (de sessie blijft leven) en geeft de bezorgradar een snelle herkansing.
    /// </summary>
    public async Task ToonLoginAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await InitAsync(ct))
            {
                return;
            }
            _web!.CoreWebView2!.Navigate("https://www.ah.be/mijnbestellingen");
            // De velden alvast invullen en op Inloggen klikken: meestal blijft er dan alleen
            // een eventuele captcha over voor Maarten.
            if (AhLoginSettings.Load() is { Compleet: true } login)
            {
                _ = ProbeerLoginAsync(login, ct);
            }
            var scherm = Screen.PrimaryScreen!.WorkingArea;
            _venster!.Location = new Point(
                scherm.Left + (scherm.Width - _venster.Width) / 2,
                scherm.Top + (scherm.Height - _venster.Height) / 2);
            _venster.ShowInTaskbar = true;
            _zichtbaar = true;
            // Vanuit een achtergrondcontext (toast) geeft Windows geen focus; afdwingen.
            _venster.TopMost = true;
            _venster.TopMost = false;
            _venster.Activate();
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Wat de laatste auto-loginpoging deed en opleverde — voor de debugdump.</summary>
    public static string LaatsteLoginDiagnose { get; private set; } = "";

    private static bool LijktLoginPagina(string tekst) =>
        tekst.Contains("opnieuw te laten weten wie je bent", StringComparison.OrdinalIgnoreCase) ||
        tekst.Contains("Log in met een Passkey", StringComparison.OrdinalIgnoreCase);

    /// <summary>Wacht tot het loginformulier er staat en vult het dan één keer in.</summary>
    private async Task ProbeerLoginAsync(AhLoginSettings login, CancellationToken ct)
    {
        try
        {
            for (var poging = 0; poging < 10; poging++)
            {
                await Task.Delay(1000, ct);
                if (_web?.CoreWebView2 is not { } core)
                {
                    return;
                }
                var r = await core.ExecuteScriptAsync(LoginScript(login.Email, login.Wachtwoord));
                if (r != "\"geen-login\"")
                {
                    return; // ingevuld/geklikt, of de pagina is al voorbij het loginscherm
                }
            }
        }
        catch
        {
            // Best effort: het venster staat toch open, handmatig kan altijd nog.
        }
    }

    /// <summary>
    /// Vult e-mail en wachtwoord in (op de React-manier: native setter + input-event, anders
    /// ziet de pagina de waarde niet) en klikt op de Inloggen-knop. De hCaptcha wordt bewust
    /// niet aangeraakt: toont die een challenge, dan is dat aan Maarten.
    /// </summary>
    private static string LoginScript(string email, string wachtwoord)
    {
        var e = System.Text.Json.JsonSerializer.Serialize(email);
        var w = System.Text.Json.JsonSerializer.Serialize(wachtwoord);
        return $$"""
            (function () {
                var pw = document.querySelector('input[type="password"]');
                if (!pw) { return 'geen-login'; }
                function vul(el, waarde) {
                    var setter = Object.getOwnPropertyDescriptor(
                        window.HTMLInputElement.prototype, 'value').set;
                    setter.call(el, waarde);
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                var mail = document.querySelector('input[type="email"], ' +
                    'input[autocomplete="username"], input[name*="mail" i], input[id*="mail" i]');
                if (mail && !mail.disabled && !mail.readOnly) { vul(mail, {{e}}); }
                vul(pw, {{w}});
                var knoppen = Array.prototype.slice.call(
                    document.querySelectorAll('button, input[type="submit"]'));
                var knop = knoppen.find(function (b) {
                    return /^(inloggen|log ?in)$/i.test((b.textContent || b.value || '').trim());
                });
                var info = {
                    mailVeld: !!mail, pwWaarde: pw.value.length,
                    knop: knop ? (knop.textContent || knop.value || '').trim() : null,
                    knopDisabled: knop ? !!knop.disabled : null,
                    knoppen: knoppen.length,
                };
                if (knop && !knop.disabled) { knop.click(); info.stap = 'geklikt'; }
                else {
                    var form = pw.closest('form');
                    if (form) {
                        if (form.requestSubmit) { form.requestSubmit(); } else { form.submit(); }
                        info.stap = 'submit';
                    } else { info.stap = 'geen-knop-of-form'; }
                }
                return JSON.stringify(info);
            })()
            """;
    }

    private void Verberg()
    {
        if (_venster is not null)
        {
            _venster.Location = new Point(-4000, -4000);
            _venster.ShowInTaskbar = false;
        }
        _zichtbaar = false;
    }

    /// <summary>Maakt venster + WebView2 één keer aan (offscreen). True als de browser er is.</summary>
    private async Task<bool> InitAsync(CancellationToken ct)
    {
        if (_web?.CoreWebView2 is not null)
        {
            return true;
        }
        try
        {
            _venster = new Form
            {
                Text = "Albert Heijn – inloggen",
                Size = new Size(1100, 850),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000), // buiten beeld, maar wel gerenderd
                ShowInTaskbar = false,
            };
            _venster.FormClosing += (_, e) =>
            {
                e.Cancel = true; // sessie blijft leven; sluiten = verbergen
                var wasZichtbaar = _zichtbaar;
                Verberg();
                if (wasZichtbaar)
                {
                    AhBezorgRadar.PlanSnelleHerkansing(); // verse login meteen benutten
                }
            };
            _web = new WebView2 { Dock = DockStyle.Fill };
            // Klein kopieerknopje bovenaan: vraagt ah.be tóch opnieuw om het wachtwoord
            // (bv. na een captcha), dan plakt Maarten het zelf uit het klembord.
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
            Theme.AsToolbar(toolbar);
            var wachtwoordButton = new ModernButton
            {
                Text = "Wachtwoord kopiëren", Width = 185, Glyph = Fluent.Copy,
            };
            wachtwoordButton.Click += (_, _) => AhLoginSettings.WachtwoordNaarKlembord(_venster!);
            toolbar.Controls.Add(wachtwoordButton);
            _venster.Controls.Add(_web);
            _venster.Controls.Add(toolbar);
            _venster.Show();

            // Zelfde profielmap én zelfde (lege) opties als het winkelmandje-venster: twee
            // environments op één map met afwijkende opties weigert WebView2.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ah"));
            var init = _web.EnsureCoreWebView2Async(env);
            if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20), ct)) != init)
            {
                return false; // profiel vergrendeld of runtime-probleem
            }
            await init;
            // Geen geluid: de bezorgradar ververst deze sessie op de achtergrond.
            _web.CoreWebView2!.IsMuted = true;
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
