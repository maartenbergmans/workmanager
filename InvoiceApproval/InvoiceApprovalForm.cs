using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Venster voor het goedkeuren van facturen in ISPnext AP Automation (CED).
/// Links de uitgelezen facturenlijst met auto-selectie op basis van de regels,
/// rechts de ingebedde browser (met blijvende SSO-sessie) waarin de acties gebeuren.
/// </summary>
public class InvoiceApprovalForm : Form
{
    private const string InvoicesUrl = "https://start.isp-online.net/ced/prd/invoices?filter=my_activities";
    private const string LoginEmail = "maarten.bergmans@ced.be"; // moet volledig in kleine letters staan
    private const string EmailJson = $"\"{LoginEmail}\"";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("nl-BE");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ListView _list;
    private readonly TextBox _log;
    private readonly Label _status;
    private readonly ModernButton _fetchButton;
    private readonly ModernButton _approveButton;
    private readonly PulseBar _pulse = new();

    private List<ApprovalRule> _rules;
    private List<InvoiceRow> _invoices = new();
    private bool _busy;
    private bool _loginAssistBusy;

    private sealed class InvoiceRow
    {
        public string Leverancier = "";
        public string Factuurnummer = "";
        public string Factuurdatum = "";
        public string Vervaldatum = "";
        public string Valuta = "";
        public string BedragText = "";
        public decimal? Bedrag;
        public bool Vervallen;
        public string Reden = "";
        public bool AutoGoedkeuren;
    }

    public InvoiceApprovalForm()
    {
        Text = "Facturen goedkeuren – ISPnext (CED)";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 900);
        WindowState = FormWindowState.Maximized;

        _rules = ApprovalRules.Load();

        // Werkbalk
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _fetchButton = new ModernButton
        {
            Text = "Facturen ophalen", Width = 160, Kind = ButtonKind.Accent, Glyph = Fluent.Refresh,
        };
        _fetchButton.Click += async (_, _) => await FetchInvoicesAsync();
        _approveButton = new ModernButton
        {
            Text = "Geselecteerde goedkeuren…", Width = 225, Enabled = false, Glyph = Fluent.Check,
        };
        _approveButton.Click += async (_, _) => await ApproveSelectedAsync();
        var rulesButton = new ModernButton { Text = "Regels beheren…", Width = 155, Glyph = Fluent.Lijst };
        rulesButton.Click += (_, _) => EditRules(null);
        var navButton = new ModernButton { Text = "Naar facturenlijst", Width = 155, Glyph = Fluent.Globe };
        navButton.Click += (_, _) => _web.CoreWebView2?.Navigate(InvoicesUrl);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[] { _fetchButton, _approveButton, rulesButton, navButton, _status });

        // Facturenlijst
        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LegeTekst = "Nog geen facturen — log in en klik op 'Facturen ophalen'.",
            LeegGlyph = Fluent.Factuur,
        };
        _list.Columns.Add("Leverancier", 260);
        _list.Columns.Add("Factuurnummer", 150);
        _list.Columns.Add("Factuurdatum", 115);
        _list.Columns.Add("Vervaldatum", 115);
        _list.Columns.Add("Bedrag", 115, HorizontalAlignment.Right);
        _list.Columns.Add("Valuta", 60);
        _list.Columns.Add("Auto-regel", 300);
        _list.ItemChecked += (_, _) => UpdateStatus();

        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var ruleItem = new ToolStripMenuItem("Regel maken/aanpassen voor deze leverancier…");
        ruleItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is InvoiceRow row)
            {
                EditRules(row.Leverancier);
            }
        };
        listMenu.Items.Add(ruleItem);
        _list.ContextMenuStrip = listMenu;

        // Logvenster
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 550,
            FixedPanel = FixedPanel.Panel2,
        };
        leftSplit.Panel1.Controls.Add(_list);
        leftSplit.Panel2.Controls.Add(_log);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 600,
        };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(_web);

        Controls.Add(split);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        Shown += async (_, _) =>
        {
            // Zelfde verdeling als het TopDesk-venster: de lijst krijgt precies zijn
            // kolommen, ál de rest is voor de ISPnext-site (waar het echte werk gebeurt).
            // Pas ná Shown, want dan is de echte (gemaximaliseerde) venstermaat bekend.
            try
            {
                var kolommen = _list.Columns.Cast<ColumnHeader>().Sum(c => c.Width) + 40;
                split.SplitterDistance = Math.Clamp(kolommen, 400, Math.Max(400, ClientSize.Width - 700));
            }
            catch
            {
                // Dan blijft de standaardverdeling staan.
            }
            await InitWebViewAsync();
        };
        Theme.Apply(this, fade: false); // fade niet: WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "facturen");
        UpdateStatus();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigen profielmap zodat de SSO-sessie (cookies) tussen sessies bewaard blijft.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ispnext"));
            await _web.EnsureCoreWebView2Async(env);

            // SSO-popups in hetzelfde venster afhandelen.
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };

            // Op de facturenpagina meteen de lijst ophalen; op andere pagina's de login-assistent
            // laten doorklikken. Ook op SourceChanged, want Microsoft en ISPnext wisselen
            // schermen soms zonder volledige navigatie.
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    await OnPageChangedAsync();
                }
            };
            _web.CoreWebView2.SourceChanged += async (_, _) => await OnPageChangedAsync();

            Log("Browser gestart. De login-assistent klikt zelf door tot aan de MFA-stap; " +
                "daarna worden de facturen automatisch opgehaald.");
            _web.CoreWebView2.Navigate(InvoicesUrl);
        }
        catch (Exception ex)
        {
            Log($"WebView2 kon niet starten: {ex.Message}");
            MessageBox.Show(this,
                "De ingebedde browser (WebView2) kon niet starten. Controleer of de WebView2-runtime geïnstalleerd is.",
                "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OnPageChangedAsync()
    {
        // WebView2-events kunnen nog binnenkomen terwijl het venster al gesloten is.
        if (IsDisposed)
        {
            return;
        }
        if ((_web.CoreWebView2?.Source ?? "").Contains("/invoices", StringComparison.OrdinalIgnoreCase))
        {
            await FetchInvoicesAsync();
        }
        else
        {
            await TryLoginAssistAsync();
        }
    }

    // ---------- Login-assistent ----------

    /// <summary>
    /// Klikt de loginflow automatisch door: gebruikersnaam invullen + "Ga verder met Single Sign-On"
    /// op de ISPnext-loginpagina, en de juiste account-tegel in het Microsoft-keuzescherm.
    /// MFA blijft handmatig.
    /// </summary>
    private async Task TryLoginAssistAsync()
    {
        if (_loginAssistBusy)
        {
            return;
        }

        _loginAssistBusy = true;
        try
        {
            // SPA/loginpagina's renderen soms pas na de navigatie; even blijven proberen —
            // en dóór de stappen heen (e-mail → wachtwoord → "aangemeld blijven?"), want
            // die wisselen zonder navigatie. Alleen de MFA-stap blijft handwerk.
            var gelogd = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var result = await RunScriptStringAsync(LoginAssistScript);
                if (result is "sso" or "account" or "email" && gelogd.Add(result))
                {
                    Log(result switch
                    {
                        "sso" => $"Gebruikersnaam '{LoginEmail}' ingevuld en 'Ga verder met " +
                                 "Single Sign-On' aangeklikt.",
                        "account" => "Microsoft-account automatisch geselecteerd.",
                        _ => $"E-mailadres '{LoginEmail}' ingevuld op de Microsoft-aanmeldpagina.",
                    });
                }
                // Wachtwoordstap: de centrale CED-login vult hem in.
                var ms = await RunScriptStringAsync(MicrosoftLogin.VulScript());
                MicrosoftLogin.Verwerk($"\"{ms}\"");
                if (ms == "wachtwoord" && gelogd.Add("wachtwoord"))
                {
                    Log("Wachtwoord ingevuld — alleen de MFA-stap is nog handwerk.");
                }
                await Task.Delay(500);
            }
        }
        finally
        {
            _loginAssistBusy = false;
        }
    }

    // ---------- Facturen ophalen ----------

    private async Task FetchInvoicesAsync()
    {
        if (_busy || IsDisposed || _web.CoreWebView2 is null)
        {
            return;
        }

        _busy = true;
        _fetchButton.Enabled = false;
        _fetchButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            _rules = ApprovalRules.Load();
            Log("Facturenlijst uitlezen…");

            JsonElement? result = null;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                var parsed = await RunScriptAsync(ExtractScript);
                if (parsed is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("invoices", out _))
                {
                    result = parsed;
                    break;
                }
                await Task.Delay(1000);
            }

            if (result is null)
            {
                Log("Geen facturentabel gevonden. Sta je op de pagina 'My Activities' en ben je ingelogd?");
                return;
            }

            _invoices = ParseInvoices(result.Value);
            Classify();
            FillList();

            var total = _invoices.Sum(i => i.Bedrag ?? 0);
            Log($"{_invoices.Count} facturen gevonden, totaal {FormatBedrag(total)}. " +
                $"{_invoices.Count(i => i.AutoGoedkeuren)} voldoen aan de auto-goedkeuringsregels.");
            if (_invoices.Count > 30)
            {
                Log("⚠ Meer dan 30 facturen — onverwacht hoog volume, controleer de lijst extra goed.");
            }
        }
        finally
        {
            _busy = false;
            _pulse.Actief = false;
            _fetchButton.Bezig = false;
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    private static List<InvoiceRow> ParseInvoices(JsonElement obj)
    {
        var rows = new List<InvoiceRow>();
        foreach (var el in obj.GetProperty("invoices").EnumerateArray())
        {
            string Get(string name) => el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            var row = new InvoiceRow
            {
                Leverancier = Get("leverancier"),
                Factuurnummer = Get("factuurnummer"),
                Factuurdatum = Get("factuurdatum"),
                Vervaldatum = Get("vervaldatum"),
                Valuta = Get("valuta"),
                BedragText = Get("bedrag"),
                Vervallen = el.TryGetProperty("vervallen", out var w) && w.ValueKind == JsonValueKind.True,
            };
            row.Bedrag = ParseBedrag(row.BedragText);
            rows.Add(row);
        }
        return rows;
    }

    private void Classify()
    {
        foreach (var inv in _invoices)
        {
            var rule = ApprovalRules.Match(_rules, inv.Leverancier);
            if (rule is null)
            {
                inv.AutoGoedkeuren = false;
                inv.Reden = "geen regel voor deze leverancier";
            }
            else if (inv.Bedrag is null)
            {
                inv.AutoGoedkeuren = false;
                inv.Reden = "bedrag niet leesbaar";
            }
            else if (!string.IsNullOrEmpty(inv.Valuta) &&
                     !inv.Valuta.Contains("EUR", StringComparison.OrdinalIgnoreCase) &&
                     !inv.Valuta.Contains('€'))
            {
                inv.AutoGoedkeuren = false;
                inv.Reden = $"valuta {inv.Valuta}, geen EUR";
            }
            else if (inv.Bedrag > rule.MaxBedrag)
            {
                inv.AutoGoedkeuren = false;
                inv.Reden = $"boven plafond van {FormatBedrag(rule.MaxBedrag)}";
            }
            else
            {
                inv.AutoGoedkeuren = true;
                inv.Reden = $"≤ plafond {FormatBedrag(rule.MaxBedrag)}";
            }
        }
    }

    private void FillList(bool checkFromRules = true)
    {
        if (IsDisposed)
        {
            return;
        }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var inv in _invoices)
        {
            var item = new ListViewItem(inv.Leverancier)
            {
                Tag = inv, Checked = checkFromRules && inv.AutoGoedkeuren, UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(inv.Factuurnummer);
            item.SubItems.Add(inv.Factuurdatum);
            var verval = item.SubItems.Add(inv.Vervaldatum + (inv.Vervallen ? " ⚠" : ""));
            if (inv.Vervallen)
            {
                verval.ForeColor = Theme.Warn;
            }
            item.SubItems.Add(inv.Bedrag is { } b ? FormatBedrag(b) : inv.BedragText);
            item.SubItems.Add(inv.Valuta);
            var reden = item.SubItems.Add(inv.Reden);
            reden.ForeColor = inv.AutoGoedkeuren ? Theme.Success : Theme.Warn;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (IsDisposed)
        {
            return;
        }
        var checkedRows = CheckedRows();
        var total = checkedRows.Sum(r => r.Bedrag ?? 0);
        _status.Text = _invoices.Count == 0
            ? "Nog geen facturen opgehaald."
            : $"{checkedRows.Count} van {_invoices.Count} geselecteerd – totaal {FormatBedrag(total)}";
        _approveButton.Enabled = checkedRows.Count > 0 && !_busy;
    }

    private List<InvoiceRow> CheckedRows() =>
        _list.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<InvoiceRow>().ToList();

    private void EditRules(string? leverancier)
    {
        using var form = new RulesForm(leverancier);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _rules = ApprovalRules.Load();
            if (_invoices.Count > 0)
            {
                Classify();
                FillList();
                Log("Regels bijgewerkt en opnieuw toegepast op de lijst.");
            }
        }
    }

    // ---------- Goedkeuren ----------

    private async Task ApproveSelectedAsync()
    {
        if (_busy || _web.CoreWebView2 is null)
        {
            return;
        }

        var rows = CheckedRows();
        if (rows.Count == 0)
        {
            return;
        }

        var total = rows.Sum(r => r.Bedrag ?? 0);
        Log($"Goedkeuren gestart: {rows.Count} facturen, totaal {FormatBedrag(total)}.");

        _busy = true;
        _fetchButton.Enabled = false;
        _approveButton.Enabled = false;
        _approveButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            // 1. Rijen aanvinken in ISPnext (matching op factuurnummer + leverancier).
            var targets = JsonSerializer.Serialize(rows.Select(r => new { l = r.Leverancier, f = r.Factuurnummer }));
            var selection = await RunScriptAsync(SelectScript.Replace("__TARGETS__", targets));
            var missing = selection?.TryGetProperty("missing", out var m) == true
                ? m.EnumerateArray().Select(x => x.GetString()).ToList()
                : null;
            if (selection is null || missing is null)
            {
                Log("Selecteren in de browser mislukt — geen facturentabel gevonden. Haal de lijst opnieuw op.");
                return;
            }
            if (missing.Count > 0)
            {
                Log($"Afgebroken: {missing.Count} facturen niet teruggevonden in de tabel " +
                    $"({string.Join(", ", missing)}). Haal de lijst opnieuw op en probeer opnieuw.");
                return;
            }
            Log($"{rows.Count} facturen aangevinkt in ISPnext.");

            // 2. Acties → Facturen goedkeuren → OK in de bevestigingsdialoog.
            // Staat het Acties-menu al open, dan volstaat de menu-optie meteen.
            if (!await ClickByTextAsync("Facturen goedkeuren", 1000))
            {
                if (!await ClickByTextAsync("Acties", 8000))
                {
                    Log("Knop 'Acties' niet gevonden. Voer de goedkeuring handmatig uit in de browser rechts.");
                    return;
                }
                if (!await ClickByTextAsync("Facturen goedkeuren", 8000))
                {
                    Log("Menu-optie 'Facturen goedkeuren' niet gevonden. Voer de goedkeuring handmatig uit in de browser.");
                    return;
                }
            }
            if (!await ClickByTextAsync("OK", 10000))
            {
                Log("Bevestigingsknop 'OK' niet gevonden. Bevestig handmatig in de browser.");
                return;
            }

            Log($"Goedkeuring verstuurd voor {rows.Count} facturen ({FormatBedrag(total)}).");

            // 3. Resultaatdialoog uitlezen (vinkje per factuur) en met OK sluiten.
            string? dialogText = null;
            for (var waited = 0; waited < 15000 && string.IsNullOrWhiteSpace(dialogText); waited += 1000)
            {
                await Task.Delay(1000);
                dialogText = await RunScriptStringAsync(DialogTextScript);
            }
            if (!string.IsNullOrWhiteSpace(dialogText))
            {
                Log("Resultaat ISPnext:\n" + dialogText.Trim());
                if (await ClickByTextAsync("OK", 8000))
                {
                    Log("Resultaatdialoog gesloten.");
                }
            }
            else
            {
                Log("Geen resultaatdialoog gezien; controleer het resultaat in de browser.");
            }

            // Goedgekeurde facturen uit de lijst halen; wat overblijft is de "niet automatisch"-groep
            // en blijft bewust onaangevinkt.
            _invoices.RemoveAll(rows.Contains);
            FillList(checkFromRules: false);
            Log($"Klaar. Goedgekeurde facturen uit de lijst verwijderd; {_invoices.Count} blijven over.");
            Toast.Toon(this, $"Goedkeuring verstuurd: {rows.Count} facturen ({FormatBedrag(total)})", Fluent.Check);
            VasteTaken.VinkAf(VasteTaken.FacturenTaak); // wekelijkse taak in Mijn taken afvinken
        }
        finally
        {
            _busy = false;
            _pulse.Actief = false;
            _approveButton.Bezig = false;
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    // ---------- Scripthelpers ----------

    private async Task<JsonElement?> RunScriptAsync(string script)
    {
        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw == "null")
            {
                return null;
            }
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (Exception ex)
        {
            Log($"Scriptfout: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> RunScriptStringAsync(string script)
    {
        var result = await RunScriptAsync(script);
        return result is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    private async Task<bool> ClickByTextAsync(string text, int timeoutMs)
    {
        var script = ClickByTextScript.Replace("__TEXT__", JsonSerializer.Serialize(text));
        for (var waited = 0; waited < timeoutMs; waited += 500)
        {
            var result = await RunScriptAsync(script);
            if (result is { ValueKind: JsonValueKind.True })
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    // Zoekt de tabel met de meeste checkbox-rijen (de facturentabel) en leest die generiek uit,
    // met kolomherkenning op de koptekst.
    private const string FindTableJs = """
        const norm = s => (s || '').replace(/\s+/g, ' ').trim();
        const findTable = () => {
            let best = null, bestCount = 0;
            for (const t of document.querySelectorAll('table')) {
                const count = [...t.querySelectorAll('tbody tr')]
                    .filter(r => r.querySelector('input[type=checkbox]')).length;
                if (count > bestCount) { bestCount = count; best = t; }
            }
            return best;
        };
        """;

    private const string ExtractScript = $$"""
        (() => {
            {{FindTableJs}}
            const table = findTable();
            if (!table) return null;
            const headers = [...table.querySelectorAll('thead th')].map(h => norm(h.innerText).toLowerCase());
            const col = key => headers.findIndex(h => h.includes(key));
            const idx = {
                leverancier: col('naam leverancier') >= 0 ? col('naam leverancier') : col('leverancier'),
                factuurnummer: col('factuurnummer'),
                factuurdatum: col('factuurdatum'),
                vervaldatum: col('vervaldatum'),
                valuta: col('valuta'),
                bedrag: col('bedrag'),
            };
            const invoices = [...table.querySelectorAll('tbody tr')]
                .filter(r => r.querySelector('input[type=checkbox]'))
                .map(r => {
                    const cells = [...r.querySelectorAll('td')];
                    const cell = i => (i >= 0 && i < cells.length) ? norm(cells[i].innerText) : '';
                    const vervCell = (idx.vervaldatum >= 0 && idx.vervaldatum < cells.length) ? cells[idx.vervaldatum] : null;
                    return {
                        leverancier: cell(idx.leverancier),
                        factuurnummer: cell(idx.factuurnummer),
                        factuurdatum: cell(idx.factuurdatum),
                        vervaldatum: cell(idx.vervaldatum),
                        valuta: cell(idx.valuta),
                        bedrag: cell(idx.bedrag),
                        vervallen: !!(vervCell && vervCell.querySelector('svg, i, img, [class*=warn]')),
                    };
                });
            if (invoices.length === 0) return null;
            return { headers, invoices };
        })()
        """;

    private const string SelectScript = $$"""
        (() => {
            {{FindTableJs}}
            const table = findTable();
            if (!table) return null;
            const rows = [...table.querySelectorAll('tbody tr')].filter(r => r.querySelector('input[type=checkbox]'));
            const targets = __TARGETS__;
            let checked = 0;
            const missing = [];
            for (const t of targets) {
                const row = rows.find(r => {
                    const text = norm(r.innerText);
                    return text.includes(t.f) && text.includes(t.l);
                });
                const cb = row ? row.querySelector('input[type=checkbox]') : null;
                if (!cb) { missing.push(t.f); continue; }
                if (!cb.checked) cb.click();
                checked++;
            }
            return { checked, missing };
        })()
        """;

    // Klikt op het zichtbare element met exact deze tekst (laatste in de DOM — dropdowns en
    // dialogen staan in overlays die achteraan de body hangen). Bewust geen offsetParent-check:
    // die is null voor elementen in een position:fixed overlay, ook als ze zichtbaar zijn.
    private const string ClickByTextScript = """
        (() => {
            const norm = s => (s || '').replace(/\s+/g, ' ').trim();
            const wanted = __TEXT__;
            const visible = e => {
                const r = e.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) return false;
                const st = getComputedStyle(e);
                return st.visibility !== 'hidden' && st.display !== 'none';
            };
            const candidates = [...document.querySelectorAll(
                    'button, a, [role=button], [role=menuitem], [role=option], li, span, div')]
                .filter(e => norm(e.innerText) === wanted && visible(e));
            if (candidates.length === 0) return false;
            // Klik op de echte knop (niet een span erin) en stuur de volledige muis-eventreeks:
            // dropdown-componenten reageren vaak op (pointer/mouse)down i.p.v. click.
            const el = candidates[candidates.length - 1];
            const target = el.closest('button, a, [role=button], [role=menuitem], [role=option], li') || el;
            const opts = { bubbles: true, cancelable: true, view: window };
            for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
                target.dispatchEvent(type.startsWith('pointer')
                    ? new PointerEvent(type, opts) : new MouseEvent(type, opts));
            }
            return true;
        })()
        """;

    private const string LoginAssistScript = $$"""
        (() => {
            const norm = s => (s || '').replace(/\s+/g, ' ').trim();
            const email = {{EmailJson}};

            // Microsoft-loginpagina's (accountkeuze of aanmeldscherm).
            if (location.hostname.includes('login.microsoftonline.com') ||
                location.hostname.includes('login.live.com')) {
                // Accountkeuze: klik de tegel met het juiste e-mailadres.
                const tiles = [...document.querySelectorAll('[role=button], [role=listitem], .table')]
                    .filter(e => e.offsetParent !== null &&
                                 norm(e.innerText).toLowerCase().includes(email));
                if (tiles.length > 0) { tiles[tiles.length - 1].click(); return 'account'; }

                // Aanmeldscherm ("E-mailadres, telefoonnummer of Skype"): e-mail invullen + Volgende.
                const emailField = [...document.querySelectorAll('input[type=email], input[name=loginfmt]')]
                    .find(e => e.offsetParent !== null);
                if (emailField) {
                    if (norm(emailField.value).toLowerCase() !== email) {
                        const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                        setter.call(emailField, email);
                        emailField.dispatchEvent(new Event('input', { bubbles: true }));
                        emailField.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    const next = document.querySelector('#idSIButton9') ||
                        [...document.querySelectorAll('input[type=submit], button')]
                            .find(e => e.offsetParent !== null &&
                                       ['volgende', 'next'].includes(norm(e.value || e.innerText).toLowerCase()));
                    if (next) { next.click(); return 'email'; }
                }
                return null;
            }

            // ISPnext-loginpagina: gebruikersnaam invullen en de SSO-knop klikken
            // (niet 'Inloggen', dat is voor een lokaal wachtwoord).
            const ssoBtn = [...document.querySelectorAll('button, a, input[type=submit], [role=button]')]
                .find(e => e.offsetParent !== null &&
                           norm(e.innerText || e.value).toLowerCase().includes('single sign-on'));
            if (!ssoBtn) return null;
            const field = [...document.querySelectorAll('input[type=text], input[type=email], input:not([type])')]
                .find(e => e.offsetParent !== null);
            if (field && norm(field.value).toLowerCase() !== email) {
                const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                setter.call(field, email);
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
            }
            ssoBtn.click();
            return 'sso';
        })()
        """;

    private const string DialogTextScript = """
        (() => {
            const visible = e => {
                const r = e.getBoundingClientRect();
                return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden';
            };
            const dialogs = [...document.querySelectorAll('[role=dialog], .modal, [class*=dialog]')]
                .filter(visible);
            if (dialogs.length === 0) return null;
            return dialogs[dialogs.length - 1].innerText;
        })()
        """;

    // ---------- Overige helpers ----------

    /// <summary>Parseert bedragen in zowel Belgische (1.234,56) als Engelse (1,234.56) notatie.</summary>
    internal static decimal? ParseBedrag(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var cleaned = new string(s.Where(c => char.IsDigit(c) || c is ',' or '.' or '-').ToArray());
        if (!cleaned.Any(char.IsDigit))
        {
            return null;
        }

        var lastComma = cleaned.LastIndexOf(',');
        var lastDot = cleaned.LastIndexOf('.');
        var decPos = Math.Max(lastComma, lastDot);

        // Eén soort scheidingsteken gevolgd door precies 3 cijfers en maar één keer aanwezig
        // kan een duizendtal zijn (bv. "1.234"); met meerdere ("1.234.567") zeker.
        if (decPos >= 0)
        {
            var sepChar = cleaned[decPos];
            var single = cleaned.Count(c => c is ',' or '.') == 1;
            var onlyOneKind = lastComma < 0 || lastDot < 0;
            if (onlyOneKind && cleaned.Length - decPos - 1 == 3 && !single)
            {
                decPos = -1; // meerdere duizendtal-scheiders, geen decimalen
            }
            else if (onlyOneKind && single && cleaned.Length - decPos - 1 == 3 && sepChar == '.')
            {
                decPos = -1; // "1.234" → duizendtal in Belgische notatie
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < cleaned.Length; i++)
        {
            if (char.IsDigit(cleaned[i]) || cleaned[i] == '-')
            {
                sb.Append(cleaned[i]);
            }
            else if (i == decPos)
            {
                sb.Append('.');
            }
        }

        return decimal.TryParse(sb.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static string FormatBedrag(decimal bedrag) => string.Create(Culture, $"€ {bedrag:N2}");

    private void Log(string message)
    {
        // Async-vervolgstappen (WebView2-events, lopende fetches) kunnen na het sluiten
        // van het venster nog loggen; dan is er niets meer om in te schrijven.
        if (IsDisposed || _log.IsDisposed)
        {
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + line);
    }
}
