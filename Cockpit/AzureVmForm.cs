using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Start de BI-werk-VM van CED (VMWS-BI-MB-1) via portal.azure.com. Het venster opent het
/// portaal met de vaste CED-login (de login-assistent vult e-mail en wachtwoord in, alleen
/// MFA is handwerk), zoekt de VM in de lijst met virtuele machines en klikt daar zelf op
/// Starten. De ingebedde browser blijft zichtbaar, dus meekijken of handmatig ingrijpen
/// kan altijd.
/// </summary>
public class AzureVmForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private const string VmNaam = "vmws-bi-mb-1";

    private const string BrowseUrl =
        "https://portal.azure.com/#view/HubsExtension/BrowseResource/resourceType/Microsoft.Compute%2FVirtualMachines";

    private WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ModernButton _startKnop;
    private readonly Label _status;
    private readonly TextBox _log;
    private readonly PulseBar _pulse = new();
    private readonly CancellationTokenSource _cts = new();

    private bool _loginAssistBezig;
    private bool _autoGestart;
    private bool _bezig;

    /// <summary>
    /// Alle iframes van het portaal: de bladen (waaronder de VM-lijst) zijn cross-origin
    /// iframes waar top-frame-JavaScript niet in kan kijken; via CoreWebView2Frame kan
    /// het script wél per frame draaien.
    /// </summary>
    private readonly List<CoreWebView2Frame> _frames = new();

    public AzureVmForm()
    {
        Text = $"Azure-VM starten – {VmNaam.ToUpperInvariant()} (CED)";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1400, 850);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _startKnop = new ModernButton
        {
            Text = "VM starten", Width = 140, Kind = ButtonKind.Accent, Glyph = Fluent.Play,
        };
        _startKnop.Click += async (_, _) => await StartVmAsync();
        var portalKnop = new ModernButton { Text = "Naar VM-lijst", Width = 140, Glyph = Fluent.Globe };
        portalKnop.Click += (_, _) => _web.CoreWebView2?.Navigate(BrowseUrl);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[] { _startKnop, portalKnop, _status });

        _log = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 110,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        Controls.Add(_web);
        Controls.Add(_log);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) => _cts.Cancel();
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "azurevm");
        Load += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigen profielmap zodat de Microsoft-sessie (cookies) bewaard blijft: de MFA
            // is dan alleen de eerste keer nodig.
            var profielMap = Path.Combine(DataDir, "webview2-azure");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profielMap);
            _web = await WebViewOpruimer.InitMetHerstelAsync(
                this, _web, env, profielMap, "Azure-portal", _cts.Token);

            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.FrameCreated += (_, e) =>
            {
                var frame = e.Frame;
                _frames.Add(frame);
                frame.Destroyed += (_, _) => _frames.Remove(frame);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    await OnPageChangedAsync();
                }
            };

            Log("Browser gestart; Azure-portal openen…");
            _web.CoreWebView2.Navigate(BrowseUrl);
        }
        catch (Exception ex)
        {
            Log($"WebView2 kon niet starten: {ex.Message}");
        }
    }

    private async Task OnPageChangedAsync()
    {
        if (IsDisposed || _web.CoreWebView2 is null)
        {
            return;
        }
        var bron = _web.CoreWebView2.Source ?? "";
        if (bron.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            bron.Contains("login.live.com", StringComparison.OrdinalIgnoreCase))
        {
            // Login-assistent: e-mail en wachtwoord automatisch, alleen MFA is handwerk.
            // De Microsoft-login is een SPA (geen navigaties tussen de stappen), dus even
            // blijven proberen tot de pagina van het logindomein af is.
            if (_loginAssistBezig)
            {
                return;
            }
            _loginAssistBezig = true;
            try
            {
                for (var i = 0; i < 40 && !IsDisposed && _web.CoreWebView2 is not null; i++)
                {
                    MicrosoftLogin.NaLoginStap(
                        await _web.CoreWebView2.ExecuteScriptAsync(MicrosoftLogin.VulScript()), this);
                    await Task.Delay(800);
                    if (!(_web.CoreWebView2?.Source ?? "").Contains("login.",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _loginAssistBezig = false;
            }
            return;
        }
        if (bron.Contains("portal.azure.com", StringComparison.OrdinalIgnoreCase) && !_autoGestart)
        {
            // Eén keer automatisch: het venster openen ís de opdracht "start de VM".
            _autoGestart = true;
            await StartVmAsync();
        }
    }

    /// <summary>
    /// Doorloopt de portaal-UI tot de startopdracht gegeven is: de VM in de lijst openen,
    /// op Starten klikken en een eventuele bevestigingsdialoog met Ja beantwoorden. Elke
    /// stap is één poll; het portaal laadt traag, dus ruim blijven proberen.
    /// </summary>
    private async Task StartVmAsync()
    {
        if (_bezig || IsDisposed || _web.CoreWebView2 is null)
        {
            return;
        }
        _bezig = true;
        _startKnop.Enabled = false;
        _startKnop.Bezig = true;
        _pulse.Actief = true;
        _status.Text = "Bezig…";
        var startGeklikt = false;
        var knopUitTeller = 0;
        try
        {
            for (var i = 0; i < 180 && !IsDisposed && _web.CoreWebView2 is not null; i++)
            {
                await Task.Delay(1000, _cts.Token);
                var bron = _web.CoreWebView2?.Source ?? "";
                if (bron.Contains("login.", StringComparison.OrdinalIgnoreCase))
                {
                    _status.Text = "Wachten op de login (MFA)…";
                    continue; // login-assistent draait apart; de teller loopt gewoon door
                }
                var resultaat = await VoerStapUitAsync();
                switch (resultaat.Trim('"'))
                {
                    case "vm-geklikt":
                        Log($"{VmNaam.ToUpperInvariant()} geopend in het portaal…");
                        _status.Text = "VM openen…";
                        break;
                    case "start-geklikt":
                        Log("Op Starten geklikt.");
                        _status.Text = "Startopdracht gegeven…";
                        startGeklikt = true;
                        break;
                    case "bevestigd":
                        Log("Bevestigingsdialoog beantwoord.");
                        startGeklikt = true;
                        break;
                    case "startknop-uit":
                        if (startGeklikt)
                        {
                            // De knop dooft zodra de opdracht loopt: klaar.
                            Klaar($"Startopdracht voor {VmNaam.ToUpperInvariant()} gegeven ✓");
                            return;
                        }
                        // Ook vóór het klikken kan de knop uit staan: dan draait de VM al.
                        if (++knopUitTeller >= 5)
                        {
                            Klaar("De Starten-knop is uitgeschakeld — de VM draait waarschijnlijk al.");
                            return;
                        }
                        break;
                    default:
                        if (startGeklikt && i % 8 == 0 && resultaat.Contains("wachten"))
                        {
                            // Even geen dialoog meer na het klikken: opdracht is binnen.
                            Klaar($"Startopdracht voor {VmNaam.ToUpperInvariant()} gegeven ✓");
                            return;
                        }
                        if (i % 20 == 19)
                        {
                            // Af en toe de ruwe stand loggen: zo is bij een DOM-wijziging
                            // van het portaal te zien wáár de zoektocht strandt.
                            Log($"Nog bezig: {resultaat.Trim('"')}");
                        }
                        break;
                }
            }
            if (!IsDisposed)
            {
                _status.Text = "Niet gelukt binnen 3 minuten.";
                Log($"De VM of de Starten-knop is niet gevonden — log in en/of klik zelf " +
                    $"in het portaal (VM: {VmNaam.ToUpperInvariant()}).");
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            Log($"VM starten mislukt: {ex.Message}");
        }
        finally
        {
            _bezig = false;
            if (!IsDisposed)
            {
                _startKnop.Bezig = false;
                _startKnop.Enabled = true;
                _pulse.Actief = false;
            }
        }
    }

    /// <summary>
    /// Voert de automatiseringsstap uit in het topdocument en daarna in elk (cross-origin)
    /// iframe, tot één ervan iets beslissends meldt. Geen resultaat = de samengevoegde
    /// wachten-diagnose van alle frames.
    /// </summary>
    private async Task<string> VoerStapUitAsync()
    {
        var samenvatting = new List<string>();
        var top = (await _web.CoreWebView2!.ExecuteScriptAsync(StapScript)).Trim('"');
        if (!top.StartsWith("wachten", StringComparison.Ordinal))
        {
            return top;
        }
        samenvatting.Add("top: " + top);
        foreach (var frame in _frames.ToList())
        {
            try
            {
                var r = (await frame.ExecuteScriptAsync(StapScript)).Trim('"');
                if (!r.StartsWith("wachten", StringComparison.Ordinal) && r != "null")
                {
                    return r;
                }
                samenvatting.Add("frame: " + r);
            }
            catch
            {
                // Frame net vernietigd tijdens het pollen: volgende ronde opnieuw.
            }
        }
        return string.Join("; ", samenvatting);
    }

    private void Klaar(string melding)
    {
        _status.Text = melding;
        Log(melding);
        Toast.Toon(this, melding, Fluent.Play);
    }

    /// <summary>
    /// Eén automatiseringsstap in de portaal-DOM (ook in same-origin iframes): eerst een
    /// open bevestigingsdialoog beantwoorden, anders de VM-link in de lijst aanklikken,
    /// anders de Starten-knop op de VM-pagina ("Opnieuw opstarten" telt niet mee).
    /// </summary>
    private static string StapScript => $$"""
        (() => {
          // Alle elementen verzamelen, ook binnen shadow-DOM en same-origin iframes:
          // het portaal rendert de resourcelijst in webcomponents waar een gewone
          // querySelectorAll niet in kijkt.
          const alles = [];
          const loop = root => {
            for (const el of root.querySelectorAll('*')) {
              alles.push(el);
              if (el.shadowRoot) { loop(el.shadowRoot); }
              if (el.tagName === 'IFRAME') {
                try { if (el.contentDocument) loop(el.contentDocument); } catch (e) { }
              }
            }
          };
          loop(document);
          const zichtbaar = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
          const tekst = el => ((el.getAttribute('aria-label') || el.innerText || ''))
            .trim().toLowerCase();
          const rol = el => el.getAttribute('role') || '';
          const dialogen = alles.filter(el =>
            (rol(el) === 'dialog' || rol(el) === 'alertdialog') && zichtbaar(el));
          for (const dlg of dialogen) {
            for (const kn of dlg.querySelectorAll('button,[role="button"]')) {
              const t = tekst(kn);
              if (t === 'ja' || t === 'ok' || t === 'starten' || t === 'start' || t === 'yes') {
                kn.click();
                return 'bevestigd';
              }
            }
          }
          let links = 0;
          for (const el of alles) {
            if (el.tagName !== 'A' && rol(el) !== 'link') { continue; }
            links++;
            if ((el.innerText || el.textContent || '').trim().toLowerCase() === '{{VmNaam}}' &&
                zichtbaar(el)) {
              el.click();
              return 'vm-geklikt';
            }
          }
          let knoppen = 0;
          for (const el of alles) {
            const r = rol(el);
            if (el.tagName !== 'BUTTON' && r !== 'button' && r !== 'menuitem') { continue; }
            const t = tekst(el);
            if (t !== 'starten' && t !== 'start') { continue; }
            knoppen++;
            if (!zichtbaar(el)) { continue; }
            if (el.getAttribute('aria-disabled') === 'true' || el.disabled ||
                (el.getAttribute('class') || '').includes('disabled')) {
              return 'startknop-uit';
            }
            el.click();
            return 'start-geklikt';
          }
          return 'wachten (' + alles.length + ' elementen, ' + links + ' links, ' +
            knoppen + ' startknoppen)';
        })()
        """;

    private void Log(string melding)
    {
        if (IsDisposed)
        {
            return;
        }
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {melding}\r\n");
    }
}
