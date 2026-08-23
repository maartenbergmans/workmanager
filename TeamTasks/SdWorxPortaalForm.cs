using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Opent het SD Worx-portaal (myworkandme.com) in een ingebedde browser om een
/// verlofaanvraag van een teamlid goed te keuren. Gebruikt hetzelfde browserprofiel en
/// dezelfde automatische login als de teamkalender, dus meestal sta je meteen ingelogd
/// op het eBlox HR-startscherm en hoef je alleen de aanvraag te openen en goed te
/// keuren. Bij een onverwachte stap (bv. MFA) werk je gewoon handmatig verder in het
/// venster. Het openen dooft het verlofsignaal van de cockpit (het portaal kan niet
/// tellen hoeveel aanvragen er openstaan, dus geopend = opgepakt).
/// </summary>
public class SdWorxPortaalForm : Form
{
    // De eBlox HR-app zelf (niet de publieke landingspagina): zonder sessie dwingt die
    // meteen de redirect naar auth.sdworx.com af, zodat de login-assistent kan invullen.
    private const string PortaalUrl = "https://www.myworkandme.com/ebloxhr/hrwwevo/#/";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly SdWorxSettings _settings = SdWorxSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private bool _bezig;
    private bool _ingelogd;

    public SdWorxPortaalForm()
    {
        Text = "Verlof goedkeuren – SD Worx";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1200, 800);

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
            WerkSignaal.Zet("sdworx", false);
            await InitWebViewAsync();
        };
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "sdworx-portaal");
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
            // Zelfde profielmap als de teamkalender: één blijvende SD Worx-sessie voor beide.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-sdworx"));
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
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    // Toch (opnieuw) op de loginpagina beland — bv. sessie net verlopen:
                    // de assistent weer aan het werk zetten.
                    if (SdWorxLogin.IsLoginUrl(_web.Source?.ToString() ?? ""))
                    {
                        _ingelogd = false;
                    }
                    await ProbeerLoginAsync();
                }
            };

            Status("Naar het SD Worx-portaal…");
            _web.CoreWebView2.Navigate(PortaalUrl);
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Browser starten mislukt: {ex.Message}");
        }
    }

    /// <summary>
    /// Drijft de loginflow met de gedeelde SD Worx-assistent, in een lus omdat de login
    /// een SPA zonder navigatie-events tussen de stappen is. Stopt zodra de browser terug
    /// op myworkandme.com staat; MFA blijft handwerk in het venster.
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
            var script = _settings.Gebruiker.Length > 0 && _settings.Wachtwoord.Length > 0
                ? SdWorxLogin.Script(_settings.Gebruiker, _settings.Wachtwoord)
                : null;
            var wachtwoordWachtrondes = 0;
            var stabielOpPortaal = 0;
            for (var poging = 0; poging < 75; poging++)
            {
                var url = _web.Source?.ToString() ?? "";
                if (url.Contains("myworkandme.com", StringComparison.OrdinalIgnoreCase) &&
                    !SdWorxLogin.IsLoginUrl(url))
                {
                    // Pas na een paar stabiele rondes "ingelogd" concluderen: zonder sessie
                    // stuurt de eBlox-app pas ná het laden door naar auth.sdworx.com.
                    if (++stabielOpPortaal >= 3)
                    {
                        _ingelogd = true;
                        _pulse.Actief = false;
                        Status("Ingelogd — open de verlofaanvraag en keur ze goed.");
                        return;
                    }
                    await Task.Delay(1200, _cts.Token);
                    continue;
                }
                stabielOpPortaal = 0;
                if (SdWorxLogin.IsLoginUrl(url))
                {
                    if (script is null)
                    {
                        _pulse.Actief = false;
                        Status("Geen SD Worx-inloggegevens gevonden — log handmatig in.");
                        return;
                    }
                    var resultaat = await RunScriptAsync(script);
                    Status(SdWorxLogin.StatusTekst(resultaat));
                    if (resultaat is "\"wachtwoord-wacht\"" && ++wachtwoordWachtrondes > 15)
                    {
                        // Wachtwoord is één keer gesubmit maar het portaal komt niet: MFA of
                        // een foutmelding. Bewust niet opnieuw (accountblokkering vermijden).
                        _pulse.Actief = false;
                        Status("Aangemeld maar het portaal verschijnt niet (MFA of foutmelding?) — " +
                               "werk handmatig verder in het venster.");
                        return;
                    }
                }
                await Task.Delay(1200, _cts.Token);
            }
            _pulse.Actief = false;
            Status("Automatisch inloggen lukte niet — log handmatig in.");
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
