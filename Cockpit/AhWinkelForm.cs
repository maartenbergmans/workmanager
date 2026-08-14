using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Legt de gekozen AH-producten automatisch in het winkelmandje op ah.be: elke productpagina
/// wordt in de ingebedde browser geopend en daar wordt de "Voeg toe"-knop aangeklikt (met de
/// plusknop erbij tot het gevraagde aantal er staat — al hoger? dan blijft het zo). Het
/// browserprofiel is blijvend, dus de AH-login en de cookiekeuze hoef je maar één keer te
/// doen. Sluit af op het winkelmandje (/mijnlijst) om te controleren en af te rekenen.
/// </summary>
public class AhWinkelForm : Form
{
    private const string MandjeUrl = "https://www.ah.be/mijnlijst";

    /// <summary>
    /// Inlogpagina van ah.be; ben je al ingelogd, dan stuurt die door naar je accountpagina.
    /// Inloggen doe je hier zelf: het profiel is blijvend, dus daarna blijft de sessie staan.
    /// </summary>
    private const string InlogUrl = "https://www.ah.be/mijn/inloggen";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ModernListView _list;
    private readonly Label _status;
    private readonly ModernButton _vulButton;
    private readonly PulseBar _pulse = new();
    private readonly List<AhIngredient> _producten;
    private bool _busy;

    /// <summary>
    /// Waar wanneer het venster gesloten is met "Andere gerechten kiezen": de bestellijst
    /// blijft dan open zodat je opnieuw kunt kiezen (het vullen is idempotent, dus wat al in
    /// het mandje ligt blijft gewoon staan).
    /// </summary>
    public bool TerugGevraagd { get; private set; }

    public AhWinkelForm(List<AhIngredient> producten, List<string> handmatig)
    {
        _producten = producten;

        Text = "Albert Heijn – winkelmandje vullen";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1400, 850);
        WindowState = FormWindowState.Maximized;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _vulButton = new ModernButton
        {
            Text = "Opnieuw vullen", Width = 160, Enabled = false, Glyph = Fluent.Refresh,
        };
        _vulButton.Click += async (_, _) => await VulMandjeAsync();
        var mandjeButton = new ModernButton
        {
            Text = "Naar winkelmandje", Width = 175, Glyph = Fluent.Winkelwagen,
        };
        mandjeButton.Click += (_, _) => _web.CoreWebView2?.Navigate(MandjeUrl);
        var terugButton = new ModernButton
        {
            Text = "Andere gerechten kiezen", Width = 200, Glyph = Fluent.Terug,
        };
        terugButton.Click += (_, _) =>
        {
            TerugGevraagd = true;
            Close();
        };
        var inlogButton = new ModernButton { Text = "Inloggen bij AH", Width = 155, Glyph = Fluent.People };
        inlogButton.Click += (_, _) =>
        {
            _web.CoreWebView2?.Navigate(InlogUrl);
            Toast.Toon(this, "Log hier zelf in; de sessie blijft daarna bewaard", Fluent.People);
        };
        var wachtwoordButton = new ModernButton
        {
            Text = "Wachtwoord kopiëren", Width = 185, Glyph = Fluent.Copy,
        };
        wachtwoordButton.Click += (_, _) => AhLoginSettings.WachtwoordNaarKlembord(this);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[]
        {
            _vulButton, mandjeButton, terugButton, inlogButton, wachtwoordButton, _status,
        });

        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Geen producten gekozen.",
            LeegGlyph = Fluent.Winkelwagen,
            // Productfoto vóór de naam, net als in de keuzestap.
            RijHoogte = 48,
            IcoonGrootte = 38,
            RijIcoon = rij => rij.Tag is AhIngredient p ? AhAfbeeldingen.Voor(p.Url) : null,
        };
        _list.Columns.Add("Ingrediënt", 170);
        _list.Columns.Add("Status", 130);
        foreach (var product in producten)
        {
            var naam = product.Aantal > 1 ? $"{product.Naam} ({product.Aantal}×)" : product.Naam;
            _list.Items.Add(new ListViewItem(new[] { naam, "wachten…" }) { Tag = product });
        }
        foreach (var naam in handmatig)
        {
            _list.Items.Add(new ListViewItem(new[] { naam, "zelf zoeken" }) { ForeColor = Theme.Muted });
        }
        AhAfbeeldingen.BeeldKlaar += OpBeeldKlaar;
        FormClosed += (_, _) => AhAfbeeldingen.BeeldKlaar -= OpBeeldKlaar;
        AhAfbeeldingen.Voorladen(producten.Select(p => p.Url));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 330,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_web);

        Controls.Add(split);
        Controls.Add(_pulse);
        Controls.Add(toolbar);
        Controls.Add(new AhStappenBalk(4));

        Shown += async (_, _) => await InitWebViewAsync();
        Theme.Apply(this, fade: false); // fade niet: WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
    }

    /// <summary>Hertekent de productlijst (op de UI-thread) zodra er een foto binnen is.</summary>
    private void OpBeeldKlaar()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() => _list.Invalidate());
            }
        }
        catch (InvalidOperationException)
        {
            // Venster net gesloten: negeren.
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigen profielmap zodat de AH-login en de cookiekeuze tussen sessies bewaard blijven.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ah"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            await VulMandjeAsync();
        }
        catch (Exception ex)
        {
            _status.Text = $"Browser kon niet starten: {ex.Message}";
            MessageBox.Show(this,
                "De ingebedde browser (WebView2) kon niet starten. Controleer of de WebView2-runtime geïnstalleerd is.",
                "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task VulMandjeAsync()
    {
        if (_busy || _web.CoreWebView2 is null)
        {
            return;
        }
        _busy = true;
        _vulButton.Enabled = false;
        _vulButton.Bezig = true;
        _pulse.Actief = true;
        var gelukt = 0;
        var verslag = new List<object>();
        try
        {
            for (var i = 0; i < _producten.Count && !IsDisposed; i++)
            {
                var product = _producten[i];
                _status.Text = $"Toevoegen {i + 1}/{_producten.Count}: {product.Naam}…";
                ZetStatus(i, "bezig…", Theme.Text);
                var ok = await VoegProductToeAsync(product);
                verslag.Add(new { product.Naam, product.Url, product.Aantal, gelukt = ok });
                if (ok)
                {
                    gelukt++;
                    ZetStatus(i, "✓ in mandje", Theme.Text);
                }
                else
                {
                    // Niet-lukken is meestal een verlopen link of een uitverkocht product;
                    // de pagina blijft open staan, dus handmatig bijklikken kan meteen.
                    ZetStatus(i, "⚠ niet gelukt", Theme.Muted);
                }
            }
        }
        finally
        {
            SchrijfDebug(verslag, verificatie: null);
            _busy = false;
            if (!IsDisposed)
            {
                _pulse.Actief = false;
                _vulButton.Bezig = false;
                _vulButton.Enabled = true;
                _status.Text = gelukt == _producten.Count
                    ? $"Alle {gelukt} producten liggen in het mandje — controleer en reken af."
                    : $"{gelukt} van {_producten.Count} producten toegevoegd; " +
                      "los de rest handmatig op (of log in en klik 'Opnieuw vullen').";
                _web.CoreWebView2?.Navigate(MandjeUrl);
                _ = AccepteerCookiesAsync(); // ook op de mandjespagina kan de privacydialoog opduiken
                Toast.Toon(this, _status.Text, Fluent.Winkelwagen);
                // Eindcontrole: staat alles nu ook écht in het mandje? (Uitverkochte producten
                // verdwijnen er stilletjes uit.) Loopt op de achtergrond mee met de navigatie.
                _ = ControleerMandjeAsync(verslag);
            }
        }
    }

    /// <summary>Schrijft het vul- (en eventueel verificatie)verslag naar ah-winkel-debug.json —
    /// onmisbaar als ah.be weer eens van DOM verandert.</summary>
    private static void SchrijfDebug(List<object> verslag, object? verificatie)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "ah-winkel-debug.json"),
                JsonSerializer.Serialize(new { datum = DateTimeOffset.Now, verslag, verificatie },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>
    /// Controleert na het vullen op de mandjespagina welke van de gevraagde producten er echt
    /// in liggen (en zo mogelijk in welk aantal), en zet de uitkomst per regel in de lijst plus
    /// een samenvatting in de statusbalk. Puur rapportage: er wordt nergens geklikt.
    /// </summary>
    private int _controleRonde;

    private async Task ControleerMandjeAsync(List<object> verslag)
    {
        // Klikt Maarten intussen "Opnieuw vullen", dan start er een nieuwe controle en mag
        // deze oude niet meer in de lijst schrijven.
        var ronde = ++_controleRonde;
        // Wachten tot de mandjespagina zijn producten toont (of hij is echt leeg).
        Dictionary<string, int?>? mandje = null;
        for (var poging = 0; poging < 20 && !IsDisposed && ronde == _controleRonde; poging++)
        {
            await Task.Delay(600);
            var raw = await RunScriptStringAsync(MandjeLeesScript);
            if (raw.Length > 0 && raw != "wachten")
            {
                try
                {
                    mandje = JsonSerializer.Deserialize<Dictionary<string, int?>>(raw);
                    if (mandje is { Count: > 0 })
                    {
                        break;
                    }
                }
                catch
                {
                    // Onverwacht antwoord: nog even blijven proberen.
                }
            }
        }
        if (IsDisposed || mandje is null)
        {
            return; // pagina niet kunnen lezen: het vulverslag blijft de beste informatie
        }

        var teruggevonden = 0;
        var controle = new List<object>();
        for (var i = 0; i < _producten.Count; i++)
        {
            var product = _producten[i];
            var id = AhApi.WebshopId(product.Url);
            if (id is null)
            {
                continue;
            }
            var inMandje = mandje.TryGetValue(id, out var aantal);
            controle.Add(new { product.Naam, id, inMandje, aantal, gevraagd = product.Aantal });
            if (!inMandje)
            {
                ZetStatus(i, "⚠ niet in mandje", Theme.Danger);
            }
            else if (aantal is { } n && n < product.Aantal)
            {
                teruggevonden++;
                ZetStatus(i, $"⚠ {n} van {product.Aantal} in mandje", Theme.Warn);
            }
            else
            {
                teruggevonden++;
                ZetStatus(i, "✓ in mandje bevestigd", Theme.Text);
            }
        }
        SchrijfDebug(verslag, controle);
        _status.Text = teruggevonden == _producten.Count
            ? $"Mandje gecontroleerd: alle {teruggevonden} producten teruggevonden."
            : $"Mandje gecontroleerd: {teruggevonden} van {_producten.Count} teruggevonden — " +
              "kijk de ⚠-regels even na.";
    }

    /// <summary>
    /// Leest op de mandjespagina per product (wi-id) het aantal uit de bijbehorende stepper;
    /// geen stepper gevonden → null (dan telt aanwezigheid alleen). "wachten" zolang er nog
    /// geen enkele productlink op de pagina staat.
    /// </summary>
    private const string MandjeLeesScript = """
        (function () {
            // Alleen op de mandjespagina zelf lezen: tijdens de navigatie staat er anders nog
            // een productpagina open, en die heeft ook productlinks en steppers.
            if (location.pathname.indexOf('mijnlijst') === -1) { return 'wachten'; }
            var links = document.querySelectorAll('a[href*="/producten/product/"]');
            if (links.length === 0) { return 'wachten'; }
            var mandje = {};
            for (var i = 0; i < links.length; i++) {
                var m = (links[i].getAttribute('href') || '').match(/\/product\/wi(\d+)/);
                if (!m) { continue; }
                var id = m[1];
                var kaart = links[i].closest('article, li, [data-testid*="product"]');
                var aantal = null;
                if (kaart) {
                    var input = kaart.querySelector('input');
                    if (input && /^\d+$/.test(input.value || '')) {
                        aantal = parseInt(input.value, 10);
                    }
                }
                if (!(id in mandje) || (aantal !== null && (mandje[id] === null || aantal > mandje[id]))) {
                    mandje[id] = aantal;
                }
            }
            return JSON.stringify(mandje);
        })()
        """;

    private void ZetStatus(int index, string tekst, Color kleur)
    {
        if (!IsDisposed && index < _list.Items.Count)
        {
            _list.Items[index].SubItems[1].Text = tekst;
            _list.Items[index].ForeColor = kleur;
        }
    }

    private async Task<bool> VoegProductToeAsync(AhIngredient product)
    {
        if (!await NavigeerAsync(product.Url!))
        {
            return false;
        }
        // De pagina rendert (en klikt) in stapjes: cookiemuur wegklikken, "Voeg toe",
        // daarna de plusknop tot het gevraagde aantal er staat. Max ~15 s per product.
        for (var poging = 0; poging < 30 && !IsDisposed; poging++)
        {
            var result = await RunScriptStringAsync(StapScript(product.Aantal));
            if (result.StartsWith("klaar", StringComparison.Ordinal))
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task<bool> NavigeerAsync(string url)
    {
        if (_web.CoreWebView2 is not { } core)
        {
            return false;
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Klaar(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Klaar;
        try
        {
            core.Navigate(url);
            return await Task.WhenAny(tcs.Task, Task.Delay(30_000)) == tcs.Task && await tcs.Task;
        }
        catch
        {
            return false;
        }
        finally
        {
            core.NavigationCompleted -= Klaar;
        }
    }

    /// <summary>
    /// Klikt de cookie-/privacydialoog weg als die in beeld staat ("Privacy voorkeuren" met
    /// een "Alles accepteren"-knop; /accepte/ dekt ook accepteer én het Franse accepter).
    /// </summary>
    internal const string CookieJs = """
            var cookie = document.querySelector('#accept-cookies, [data-testhook="accept-cookies"]');
            if (!cookie) {
                var knoppen = Array.prototype.slice.call(document.querySelectorAll('button'));
                cookie = knoppen.find(function (b) {
                    var omgeving = b.closest('[class*="cookie" i], [id*="cookie" i], ' +
                        '[class*="consent" i], [id*="consent" i], [class*="privacy" i], ' +
                        '[aria-modal="true"], dialog, section');
                    return /accepte|toestaan|akkoord/i.test(b.textContent || '') &&
                        omgeving && /cookie|privacy/i.test(omgeving.textContent || '');
                });
            }
            if (cookie) { cookie.click(); return 'cookies'; }
        """;

    /// <summary>Blijft even proberen de privacydialoog weg te klikken (na een navigatie).</summary>
    private async Task AccepteerCookiesAsync()
    {
        for (var poging = 0; poging < 10 && !IsDisposed; poging++)
        {
            if (await RunScriptStringAsync($$"""
                (function () {
                    {{CookieJs}}
                    return 'geen';
                })()
                """) == "cookies")
            {
                return;
            }
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// Eén stap op de productpagina; wordt herhaald tot hij "klaar" meldt. Selectors
    /// gecontroleerd op ah.be (juli 2026): de "Voeg toe"-knop en de aantal-stepper zitten in
    /// [data-testid="pdp-hero-basket-actions"]; suggesties elders op de pagina hebben hun
    /// eigen steppers, vandaar het scopen op die zone.
    /// </summary>
    private static string StapScript(int doel) => $$"""
        (function (doel) {
            {{CookieJs}}

            var zone = document.querySelector('[data-testid="pdp-hero-basket-actions"]');
            if (!zone) { return 'wachten'; }
            var input = zone.querySelector('[data-testid="pdp-hero-basket-actions-quantity-stepper"] input');
            var huidig = input ? (parseInt(input.value, 10) || 0) : 0;
            if (huidig >= doel) { return 'klaar:' + huidig; }
            if (input) {
                var plus = zone.querySelector('[data-testid="quantity-stepper-increase-button"]');
                if (plus) { plus.click(); return 'plus'; }
            }
            var voegToe = zone.querySelector('[data-testid="pdp-hero-basket-actions-add-to-cart-button"]');
            if (voegToe) { voegToe.click(); return 'toegevoegd'; }
            return 'wachten';
        })({{doel}})
        """;

    private async Task<string> RunScriptStringAsync(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null)
        {
            return "";
        }
        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
