using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Het urbanadmin-werkurenoverzicht in een ingebedde browser, zodat je je geboekte uren
/// bekijkt zonder de cockpit te verlaten. Eigen profielmap: de login blijft bewaard, dus na
/// de eerste keer opent het venster meteen op de lijst.
/// </summary>
public sealed class TimesheetDashboardForm : Form
{
    public const string DashboardUrl = "https://timesheets.urbanit.be/app/";
    private const string WerkurenUrl = "https://timesheets.urbanit.be/app/werkuren";
    private const string FacturatieUrl = "https://timesheets.urbanit.be/app/werkuren/facturatie";
    private const string InvoerUrl = "https://timesheets.urbanit.be/app/werkuur-toevoegen";

    /// <summary>
    /// UrbanAdmin rendert het dashboard alleen na een klik op "Dashboard" in het menu: bij een
    /// directe load van /app/ blijft de router-outlet leeg. Dit script doet die klik dus zelf,
    /// zodra de menulink bestaat, en stopt zodra &lt;app-dashboard&gt; er staat.
    /// </summary>
    private const string DashboardTonenScript = """
        (function () {
          var pogingen = 0;
          var t = setInterval(function () {
            if (document.querySelector('app-dashboard')) { clearInterval(t); return; }
            var link = document.querySelector('a[href="/app/"]');
            if (link) { link.click(); }
            if (++pogingen > 60) { clearInterval(t); }
          }, 250);
        })();
        """;

    /// <summary>
    /// Verbergt het linkermenu van urbanadmin: in dit venster navigeer je met de knoppen
    /// hierboven, dus die 250 px zijn beter besteed aan de tabel zelf. Draait bij elke
    /// documentcreatie, zodat het ook na een herlading meteen klopt.
    /// </summary>
    private const string MenuVerbergScript = """
        (function () {
          var css = '.layout-menu,.layout-sidebar,.layout-topbar-menubutton,' +
                    '.layout-menu-button,.p-panelmenu{display:none!important}' +
                    '.layout-main,.layout-main-container,.layout-content{' +
                    'margin-left:0!important;padding-left:0!important}';
          function zet() {
            var s = document.getElementById('wm-geen-menu');
            if (!s) {
              s = document.createElement('style');
              s.id = 'wm-geen-menu';
              (document.head || document.documentElement).appendChild(s);
            }
            if (s.textContent !== css) { s.textContent = css; }
            // Angular hangt de stijl soms opnieuw op: als hij verdwijnt, komt hij zo terug.
            if (s.parentNode !== (document.head || document.documentElement)) {
              (document.head || document.documentElement).appendChild(s);
            }
          }
          zet();
          document.addEventListener('DOMContentLoaded', zet);
          // Eerste seconden na het laden bouwt Angular de shell nog op: even blijven bewaken.
          var n = 0;
          var t = setInterval(function () { zet(); if (++n > 40) { clearInterval(t); } }, 250);
          window.wmMenuVerbergen = zet;
        })();
        """;

    /// <summary>Zet het menu weer terug (tegenhanger van <see cref="MenuVerbergScript"/>).</summary>
    private const string MenuTonenScript =
        "document.getElementById('wm-geen-menu')?.remove(); window.wmMenuVerbergen = null;";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _status;
    private readonly ModernButton _menuKnop;
    private bool _menuZichtbaar;

    public TimesheetDashboardForm()
    {
        Text = "Timesheets – urbanadmin";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1320, 880);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        var verversKnop = new ModernButton { Text = "Verversen", Glyph = Fluent.Sync };
        verversKnop.KrimpNaarInhoud();
        verversKnop.Click += (_, _) => _web.CoreWebView2?.Reload();
        var dashboardKnop = new ModernButton { Text = "Dashboard", Glyph = Fluent.Ster };
        dashboardKnop.KrimpNaarInhoud();
        dashboardKnop.Click += async (_, _) => await NaarDashboardAsync();
        var werkurenKnop = new ModernButton { Text = "Werkuren", Glyph = Fluent.Klok };
        werkurenKnop.KrimpNaarInhoud();
        werkurenKnop.Click += (_, _) => _web.CoreWebView2?.Navigate(WerkurenUrl);
        var facturatieKnop = new ModernButton { Text = "Facturatie", Glyph = Fluent.Document };
        facturatieKnop.KrimpNaarInhoud();
        facturatieKnop.Click += (_, _) => _web.CoreWebView2?.Navigate(FacturatieUrl);
        var invoerKnop = new ModernButton { Text = "Werkuur toevoegen", Glyph = Fluent.Kalender };
        invoerKnop.KrimpNaarInhoud();
        invoerKnop.Click += (_, _) => _web.CoreWebView2?.Navigate(InvoerUrl);
        // Wie tóch in het volledige urbanadmin wil grasduinen, zet het menu even terug.
        _menuKnop = new ModernButton { Text = "Menu tonen" };
        _menuKnop.KrimpNaarInhoud();
        _menuKnop.Click += async (_, _) => await SchakelMenuAsync();
        var browserKnop = new ModernButton { Text = "In browser openen", Glyph = Fluent.Globe };
        browserKnop.KrimpNaarInhoud();
        browserKnop.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(
                _web.CoreWebView2?.Source is { Length: > 0 } url ? url : WerkurenUrl)
            { UseShellExecute = true });
        _status = new Label { AutoSize = true, Padding = new Padding(8, 12, 0, 0) };
        Theme.AsStatus(_status);
        toolbar.Controls.Add(verversKnop);
        toolbar.Controls.Add(dashboardKnop);
        toolbar.Controls.Add(werkurenKnop);
        toolbar.Controls.Add(facturatieKnop);
        toolbar.Controls.Add(invoerKnop);
        toolbar.Controls.Add(_menuKnop);
        toolbar.Controls.Add(browserKnop);
        toolbar.Controls.Add(_status);

        Controls.Add(_web);
        Controls.Add(toolbar);
        Shown += async (_, _) => await InitAsync();
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "timesheet-dashboard");
        _web.DefaultBackgroundColor = Theme.Bg;
    }

    private async Task InitAsync()
    {
        try
        {
            _status.Text = "Dashboard laden…";
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-timesheets"));
            await _web.EnsureCoreWebView2Async(env);
            // Popups (bv. een SSO-scherm) in hetzelfde venster houden.
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (IsDisposed)
                {
                    return;
                }
                _status.Text = e.IsSuccess
                    ? ""
                    : $"Laden mislukt ({e.WebErrorStatus}) — probeer Verversen.";
                if (!e.IsSuccess)
                {
                    return;
                }
                // Na elke navigatie het menu opnieuw verbergen (tenzij je het zelf aan zette).
                if (!_menuZichtbaar)
                {
                    await _web.CoreWebView2.ExecuteScriptAsync(MenuVerbergScript);
                }
                // Landen we op de dashboardpagina zelf, dan die klik alsnog uitvoeren.
                if (OpDashboard())
                {
                    await _web.CoreWebView2.ExecuteScriptAsync(DashboardTonenScript);
                }
            };
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MenuVerbergScript);
            _web.CoreWebView2.Navigate(DashboardUrl);
        }
        catch (Exception ex)
        {
            _status.Text = $"Browser starten mislukt: {ex.Message}";
        }
    }

    /// <summary>Staat de webview op de dashboardpagina (/app/ zonder subpad)?</summary>
    private bool OpDashboard() =>
        _web.CoreWebView2?.Source is { } bron &&
        (bron.TrimEnd('/') == DashboardUrl.TrimEnd('/') || bron.EndsWith("/app/dashboard", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Naar het dashboard: zit de webview er al (client-side), dan volstaat de menuklik;
    /// anders eerst navigeren — de NavigationCompleted-handler doet de klik dan.
    /// </summary>
    private async Task NaarDashboardAsync()
    {
        if (_web.CoreWebView2 is not { } core)
        {
            return;
        }
        if (OpDashboard())
        {
            await core.ExecuteScriptAsync(DashboardTonenScript);
            return;
        }
        core.Navigate(DashboardUrl);
    }

    /// <summary>Zet het urbanadmin-menu aan of uit zonder de pagina te herladen.</summary>
    private async Task SchakelMenuAsync()
    {
        if (_web.CoreWebView2 is not { } core)
        {
            return;
        }
        _menuZichtbaar = !_menuZichtbaar;
        _menuKnop.Text = _menuZichtbaar ? "Menu verbergen" : "Menu tonen";
        await core.ExecuteScriptAsync(_menuZichtbaar ? MenuTonenScript : MenuVerbergScript);
    }
}
