using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Opent e-Box Enterprise (de elektronische brievenbus van BerMaCon) in een ingebedde
/// browser. De CSAM-login gaat volledig automatisch: digitale sleutel "beveiligingscode
/// via mobiele app", gebruikersnaam + wachtwoord uit <see cref="EboxSettings"/> en de
/// beveiligingscode lokaal berekend (<see cref="Totp"/>) — geen telefoon nodig. Een
/// aangeklikte bijlage wordt in Downloads bewaard en meteen geopend. Het openen dooft
/// het e-Box-signaal van de cockpit (de meldingsmail zelf is dan al gearchiveerd).
/// </summary>
public class EboxForm : Form
{
    // De app zelf (niet de landingspagina): zonder sessie dwingt die meteen de
    // CSAM-redirect af, zodat de login-assistent aan het werk kan.
    private const string AppUrl = "https://app.eboxenterprise.be/messages";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly EboxSettings _settings = EboxSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private bool _bezig;
    private bool _ingelogd;
    private int _totpPogingen;
    private int _onbekendeRondes;

    /// <summary>
    /// Is dit de "Nieuw e-Box bericht"-meldingsmail van socialsecurity.be? Die zet in de
    /// cockpit het e-Box-signaal aan en wordt daarna automatisch gearchiveerd.
    /// </summary>
    public static bool IsMeldingsmail(MailBericht m) =>
        m.VanAdres.Contains("socialsecurity.be", StringComparison.OrdinalIgnoreCase) &&
        m.Onderwerp.Contains("e-Box", StringComparison.OrdinalIgnoreCase);

    public EboxForm()
    {
        Text = "e-Box Enterprise – BerMaCon";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1300, 850);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        _status.Padding = new Padding(4, 14, 0, 0);
        toolbar.Controls.Add(_status);

        Controls.Add(_web);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) =>
        {
            WerkSignaal.Zet("ebox", false);
            await InitWebViewAsync();
        };
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "ebox");
        _web.DefaultBackgroundColor = Theme.Bg;
    }

    private void Status(string tekst)
    {
        if (!IsDisposed)
        {
            _status.Text = tekst;
        }
        Log(tekst);
    }

    /// <summary>
    /// Diagnoselog in %APPDATA%\WorkManager\ebox-login.log: elke statuswissel met URL,
    /// zodat een haperende CSAM-stap achteraf te herleiden is zonder het venster te zien.
    /// </summary>
    private string _laatsteLog = "";

    private void Log(string tekst)
    {
        var regel = $"{tekst}  [{_web.Source}]";
        if (regel == _laatsteLog)
        {
            return; // dezelfde status elke lusronde herhalen vervuilt het log alleen maar
        }
        _laatsteLog = regel;
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "ebox-login.log"),
                $"{DateTimeOffset.Now:HH:mm:ss} {regel}\r\n");
        }
        catch
        {
            // Diagnose is bijzaak.
        }
    }

    /// <summary>Schrijft de huidige pagina naar ebox-diagnose.html (voor selector-onderzoek).</summary>
    private async Task DumpPaginaAsync()
    {
        var html = await RunScriptAsync("document.documentElement.outerHTML");
        if (html is { Length: > 2 })
        {
            try
            {
                File.WriteAllText(Path.Combine(DataDir, "ebox-diagnose.html"),
                    System.Text.Json.JsonSerializer.Deserialize<string>(html) ?? "");
                Log("Pagina gedumpt naar ebox-diagnose.html");
            }
            catch
            {
                // Diagnose is bijzaak.
            }
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _pulse.Actief = true;
            Status("Browser starten…");
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ebox"));
            await _web.EnsureCoreWebView2Async(env);
            try
            {
                _web.CoreWebView2.IsMuted = true; // WorkManager is stil
            }
            catch
            {
                // Oudere runtime zonder IsMuted: dan blijft alles werken zoals voorheen.
            }

            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            // Bijlagen (de e-Box-berichten zelf) naar Downloads en meteen openen: dat is
            // het "bericht tonen" waar de meldingsmail om vroeg.
            _web.CoreWebView2.DownloadStarting += (_, e) => RichtDownloadIn(e);
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    if (EboxLogin.IsLoginUrl(_web.Source?.ToString() ?? ""))
                    {
                        _ingelogd = false; // sessie (net) verlopen: assistent weer aan het werk
                    }
                    await ProbeerLoginAsync();
                }
            };

            Status("Naar e-Box Enterprise…");
            _web.CoreWebView2.Navigate(AppUrl);
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Browser starten mislukt: {ex.Message}");
        }
    }

    /// <summary>Stuurt een download naar de Downloads-map en opent het bestand zodra het er staat.</summary>
    private void RichtDownloadIn(CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var map = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var naam = Path.GetFileName(e.ResultFilePath);
            var doel = Path.Combine(map, naam);
            for (var i = 1; File.Exists(doel); i++)
            {
                doel = Path.Combine(map,
                    $"{Path.GetFileNameWithoutExtension(naam)} ({i}){Path.GetExtension(naam)}");
            }
            e.ResultFilePath = doel;
            var operatie = e.DownloadOperation;
            operatie.StateChanged += (_, _) =>
            {
                if (operatie.State != CoreWebView2DownloadState.Completed || IsDisposed)
                {
                    return;
                }
                BeginInvoke(() =>
                {
                    Status($"Gedownload en geopend: {Path.GetFileName(doel)}");
                    try
                    {
                        Process.Start(new ProcessStartInfo(doel) { UseShellExecute = true });
                    }
                    catch
                    {
                        // Geen viewer: het bestand staat hoe dan ook in Downloads.
                    }
                });
            };
        }
        catch
        {
            // Dan valt de download terug op het standaardgedrag van WebView2.
        }
    }

    /// <summary>
    /// Drijft de CSAM-loginflow met <see cref="EboxLogin"/>, in een lus omdat de login
    /// deels een SPA zonder navigatie-events is. De TOTP-code wordt per ronde vers
    /// berekend; na twee ingevulde codes zonder resultaat stoppen we bewust (verkeerd
    /// geheim → geen accountblokkering riskeren).
    /// </summary>
    private async Task ProbeerLoginAsync()
    {
        if (IsDisposed || _bezig || _ingelogd)
        {
            return;
        }
        _bezig = true;
        try
        {
            var loginWachtrondes = 0;
            var stabielOpApp = 0;
            for (var poging = 0; poging < 75; poging++)
            {
                var url = _web.Source?.ToString() ?? "";
                if (url.Contains("app.eboxenterprise.be", StringComparison.OrdinalIgnoreCase) &&
                    !EboxLogin.IsLoginUrl(url))
                {
                    // Pas na een paar stabiele rondes "ingelogd" concluderen: zonder sessie
                    // stuurt de app pas ná het laden door naar CSAM.
                    if (++stabielOpApp >= 3)
                    {
                        _ingelogd = true;
                        _pulse.Actief = false;
                        Status("Ingelogd — klik een bericht aan; de bijlage wordt " +
                               "gedownload en meteen geopend.");
                        return;
                    }
                    await Task.Delay(1200, _cts.Token);
                    continue;
                }
                stabielOpApp = 0;
                if (EboxLogin.IsSiteUrl(url))
                {
                    // Zonder sessie beland je op de publieke site: taalkeuze en
                    // "e-Box openen"/"Verdergaan naar CSAM" wegklikken tot de login komt.
                    var siteResultaat = await RunScriptAsync(EboxLogin.SiteScript);
                    Status(EboxLogin.StatusTekst(siteResultaat));
                    await Task.Delay(1200, _cts.Token);
                    continue;
                }
                if (EboxLogin.IsLoginUrl(url))
                {
                    if (!_settings.Compleet)
                    {
                        _pulse.Actief = false;
                        Status("Geen (volledige) e-Box-inloggegevens gevonden — log handmatig in.");
                        return;
                    }
                    // Een TOTP-code die minder dan 5 s geldig is niet meer insturen; even
                    // wachten op de volgende levert een code op die zeker aankomt.
                    if (Totp.SecondenGeldig() < 5)
                    {
                        await Task.Delay(5000, _cts.Token);
                    }
                    var script = EboxLogin.Script(
                        _settings.Gebruiker, _settings.Wachtwoord, Totp.Genereer(_settings.TotpSecret));
                    var resultaat = await RunScriptAsync(script);
                    Status(EboxLogin.StatusTekst(resultaat));
                    // Onbekende loginstap (het script herkent niets): na een paar rondes de
                    // pagina dumpen, zodat de selectors bijgewerkt kunnen worden.
                    if (resultaat is null or "null")
                    {
                        if (++_onbekendeRondes % 8 == 0)
                        {
                            await DumpPaginaAsync();
                        }
                    }
                    else
                    {
                        _onbekendeRondes = 0;
                    }
                    if (resultaat is "\"totp\"" && ++_totpPogingen >= 2)
                    {
                        _pulse.Actief = false;
                        Status("Twee beveiligingscodes ingestuurd zonder resultaat — " +
                               "controleer het TOTP-geheim of werk handmatig verder.");
                        await DumpPaginaAsync();
                        return;
                    }
                    if (resultaat is "\"login-wacht\"" && ++loginWachtrondes > 15)
                    {
                        // Wachtwoord is één keer gesubmit maar er komt geen volgende stap:
                        // foutmelding of onverwachte pagina. Bewust niet opnieuw proberen.
                        _pulse.Actief = false;
                        Status("Aangemeld maar CSAM gaat niet verder (foutmelding?) — " +
                               "werk handmatig verder in het venster.");
                        await DumpPaginaAsync();
                        return;
                    }
                }
                await Task.Delay(1200, _cts.Token);
            }
            _pulse.Actief = false;
            Status("Automatisch inloggen lukte niet — log handmatig in.");
            await DumpPaginaAsync();
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Inloggen mislukt: {ex.Message}");
        }
        finally
        {
            _bezig = false;
        }
    }

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
