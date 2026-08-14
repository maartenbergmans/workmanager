using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Venster voor de CED-servicedesk in TopDesk (ced.topdesk.net). Links de uitgelezen
/// ticketlijst, rechts de ingebedde browser met blijvende behandelaarssessie. De
/// login-assistent vult het "Log in as Operator"-formulier zelf in met de bewaarde
/// gegevens; de tickets komen uit de ingelogde sessie zelf (de JSON-endpoints waar de
/// TopDesk-webinterface ook op draait), dus er is geen apart API-wachtwoord nodig.
/// </summary>
public class TopdeskForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ModernListView _list;
    private readonly TextBox _log;
    private readonly Label _status;
    private readonly ModernButton _fetchButton;
    private readonly PulseBar _pulse = new();

    private TopdeskSettings _settings;
    private bool _busy;
    private bool _loginAssistBusy;

    /// <summary>Geziene interne requests (methode + pad), voor de endpoint-diagnose.</summary>
    private readonly HashSet<string> _netwerk = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TicketRow
    {
        public string Id = "";
        public string Nummer = "";
        public string Lijn = "";
        public string Omschrijving = "";
        public string Aanmelder = "";
        public string Status = "";
        public string Prioriteit = "";
        public string Behandelaar = "";
        public DateTimeOffset? Aangemaakt;
    }

    public TopdeskForm()
    {
        Text = "TopDesk – CED-servicedesk";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 900);
        WindowState = FormWindowState.Maximized;

        _settings = TopdeskSettings.Load();
        // Nog geen (werkende) login bewaard? De centrale CED-login gebruikt hetzelfde
        // wachtwoord, alleen met de admin-gebruiker — dan hoeft hier niets ingesteld.
        if (!_settings.Compleet && CedLogin.Wachtwoord() is { Length: > 0 } centraal)
        {
            _settings.Gebruikersnaam = CedLogin.TopdeskGebruiker;
            _settings.Wachtwoord = centraal;
            _settings.Save();
        }

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _fetchButton = new ModernButton
        {
            Text = "Tickets ophalen", Width = 160, Kind = ButtonKind.Accent, Glyph = Fluent.Refresh,
        };
        _fetchButton.Click += async (_, _) => await FetchTicketsAsync();
        var navButton = new ModernButton { Text = "Naar TopDesk", Width = 140, Glyph = Fluent.Globe };
        navButton.Click += (_, _) => _web.CoreWebView2?.Navigate(OperatorUrl);
        var loginButton = new ModernButton { Text = "Login-gegevens…", Width = 160, Glyph = Fluent.Settings };
        loginButton.Click += (_, _) => EditLogin();
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[] { _fetchButton, navButton, loginButton, _status });

        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen tickets — klik op 'Tickets ophalen'.",
            LeegGlyph = Fluent.Lijst,
        };
        _list.Columns.Add("Nummer", 110);
        _list.Columns.Add("Lijn", 50);
        _list.Columns.Add("Omschrijving", 300);
        _list.Columns.Add("Aanmelder", 160);
        _list.Columns.Add("Status", 130);
        _list.Columns.Add("Prioriteit", 90);
        _list.Columns.Add("Behandelaar", 140);
        _list.Columns.Add("Streefdatum", 110);
        _list.DoubleClick += (_, _) => OpenGeselecteerdTicket();

        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var taakItem = new ToolStripMenuItem("Taak aanmaken (met link naar ticket)");
        taakItem.Click += async (_, _) => await MaakTaakVanTicketAsync();
        listMenu.Items.Add(taakItem);
        var openItem = new ToolStripMenuItem("Ticket openen in browser rechts");
        openItem.Click += (_, _) => OpenGeselecteerdTicket();
        listMenu.Items.Add(openItem);
        _list.ContextMenuStrip = listMenu;

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
            SplitterDistance = 720,
            // De lijst houdt zijn vaste 720 px; alle extra breedte (het venster start
            // gemaximaliseerd) gaat naar de ingebedde browser.
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(_web);

        Controls.Add(split);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        Shown += async (_, _) =>
        {
            // De verdeling pas ná Shown zetten (dan is de echte, gemaximaliseerde maat
            // bekend — een vaste 720 px vooraf pakte op hoge DPI verkeerd uit): de lijst
            // krijgt precies zijn kolommen, ál de rest is voor de TopDesk-site.
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
        VensterGeheugen.Volg(this, "topdesk");
        UpdateStatus(0);
    }

    private string BasisUrl => _settings.Url.TrimEnd('/');

    /// <summary>Startpunt van de behandelaarsinterface; zonder sessie toont TopDesk hier de login.</summary>
    private string OperatorUrl => BasisUrl + "/tas/secure/";

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigen profielmap zodat de behandelaarssessie (cookies) tussen sessies bewaard blijft.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-topdesk"));
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
                    await OnPageChangedAsync();
                }
            };

            // /tas/api weigert de sessiecookie (401: applicatiewachtwoord vereist), dus we
            // moeten de interne endpoints leren kennen die de TopDesk-UI zelf gebruikt — die
            // accepteren de sessie wél. Alle XHR/fetch/document-requests worden (ontdubbeld)
            // gelogd in topdesk-netwerk.txt; klikken op de cijfertjes in het Taken-blok
            // verraadt zo het endpoint achter die lijsten.
            _web.CoreWebView2.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.XmlHttpRequest);
            _web.CoreWebView2.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.Fetch);
            _web.CoreWebView2.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.Document);
            _web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    if (!uri.Host.EndsWith("topdesk.net", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    var regel = $"{e.Request.Method} {uri.PathAndQuery}";
                    lock (_netwerk)
                    {
                        if (_netwerk.Add(regel))
                        {
                            File.AppendAllText(Path.Combine(DataDir, "topdesk-netwerk.txt"),
                                $"{DateTime.Now:HH:mm:ss} {regel}\r\n");
                        }
                    }
                }
                catch
                {
                    // Alleen diagnose; het request zelf gaat gewoon door.
                }
            };

            if (!_settings.Compleet)
            {
                Log("Nog geen login-gegevens bewaard — klik op 'Login-gegevens…' om ze in te stellen.");
            }
            Log("Browser gestart. Na het inloggen worden de tickets automatisch opgehaald.");
            _web.CoreWebView2.Navigate(OperatorUrl);
        }
        catch (Exception ex)
        {
            Log($"WebView2 kon niet starten: {ex.Message}");
            MessageBox.Show(this,
                "De ingebedde browser (WebView2) kon niet starten. Controleer of de WebView2-runtime geïnstalleerd is.",
                "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Is er al één keer automatisch opgehaald? Daarna alleen nog via de knop.</summary>
    private bool _autoOpgehaald;

    private async Task OnPageChangedAsync()
    {
        if (IsDisposed || _web.CoreWebView2 is null)
        {
            return;
        }
        var bron = _web.CoreWebView2.Source ?? "";
        if (!bron.Contains("topdesk", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // Niet op de URL afgaan (redirect-tussenstappen bevatten soms "login" terwijl je al
        // ingelogd bent): gewoon in de pagina kijken of het loginformulier er echt staat.
        var heeftLoginForm = await _web.CoreWebView2.ExecuteScriptAsync(
            "!!document.querySelector('#loginname, input[name=form_username]')");
        if (heeftLoginForm == "true")
        {
            await TryLoginAssistAsync();
        }
        else if (bron.Contains("/tas/", StringComparison.OrdinalIgnoreCase) &&
                 !_busy && !_autoOpgehaald)
        {
            // Ingelogd in de behandelaarsinterface: één keer automatisch de tickets ophalen;
            // daarna bepaalt de knop het ritme (niet bij elke klik in de browser opnieuw).
            _autoOpgehaald = true;
            await FetchTicketsAsync();
        }
    }

    // ---------- Login-assistent ----------

    /// <summary>
    /// Vult op de "Log in as Operator"-pagina gebruikersnaam en wachtwoord in en klikt op
    /// Login. Het formulier is een klassiek POST-formulier (#loginname/#password/#login).
    /// </summary>
    private async Task TryLoginAssistAsync()
    {
        if (_loginAssistBusy || !_settings.Compleet)
        {
            return;
        }

        _loginAssistBusy = true;
        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsDisposed || _web.CoreWebView2 is null)
                {
                    return;
                }
                var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                    LoginScript(_settings.Gebruikersnaam, _settings.Wachtwoord));
                if (raw == "\"ingelogd\"")
                {
                    Log($"Ingelogd als behandelaar '{_settings.Gebruikersnaam}'.");
                    return;
                }
                await Task.Delay(500);
            }
            Log("Loginformulier niet gevonden — log zo nodig handmatig in in de browser rechts.");
        }
        catch (Exception ex)
        {
            Log($"Auto-login mislukt: {ex.Message}");
        }
        finally
        {
            _loginAssistBusy = false;
        }
    }

    private static string LoginScript(string gebruikersnaam, string wachtwoord)
    {
        var g = JsonSerializer.Serialize(gebruikersnaam);
        var w = JsonSerializer.Serialize(wachtwoord);
        return $$"""
            (() => {
                const naam = document.querySelector('#loginname, input[name=form_username]');
                const pw = document.querySelector('#password, input[name=form_password]');
                if (!naam || !pw) { return 'geen-login'; }
                naam.value = {{g}};
                pw.value = {{w}};
                const knop = document.querySelector('#login, input[name=submit-button]');
                if (knop) { knop.click(); } else { naam.closest('form')?.submit(); }
                return 'ingelogd';
            })()
            """;
    }

    // ---------- Tickets ophalen ----------

    private async Task FetchTicketsAsync()
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
            Log("Openstaande tickets ophalen…");
            var kick = await _web.CoreWebView2.ExecuteScriptAsync(StartFetchScript);
            if (kick != "\"gestart\"")
            {
                // Een JS-fout in het startscript levert hier "null" op — dan weten we
                // meteen dat het script zelf stukliep in deze TopDesk-pagina.
                Log($"Startscript gaf onverwacht antwoord: {kick}");
            }

            JsonElement? result = null;
            var laatsteVoortgang = "";
            for (var attempt = 0; attempt < 45; attempt++)
            {
                await Task.Delay(1000);
                if (IsDisposed || _web.CoreWebView2 is null)
                {
                    return;
                }
                // Live voortgang: het script schrijft elke stap meteen in __wmDiag.
                var diagRaw = await _web.CoreWebView2.ExecuteScriptAsync(
                    "window.__wmDiag ? window.__wmDiag.join(' | ') : ''");
                if (JsonSerializer.Deserialize<string>(diagRaw) is { Length: > 0 } voortgang &&
                    voortgang != laatsteVoortgang)
                {
                    laatsteVoortgang = voortgang;
                    Log("Voortgang: " + voortgang);
                }
                var raw = await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__wmTickets)");
                var json = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null")
                {
                    continue;
                }
                result = JsonDocument.Parse(json).RootElement.Clone();
                try
                {
                    // Ruwe dump voor het bijstellen van de kolomherkenning.
                    File.WriteAllText(Path.Combine(DataDir, "topdesk-grid-dump.json"), json);
                }
                catch
                {
                    // Alleen diagnose.
                }
                break;
            }

            if (result is null)
            {
                Log(laatsteVoortgang.Length > 0
                    ? "Na 45 s nog geen resultaat; laatste voortgang hierboven."
                    : "Geen antwoord van TopDesk en geen voortgang — het script draaide niet in deze pagina.");
                return;
            }
            if (result.Value.TryGetProperty("diagnose", out var diag) &&
                diag.GetString() is { Length: > 0 } diagnose)
            {
                Log("Diagnose:\n" + diagnose);
            }
            if (result.Value.TryGetProperty("fout", out var fout))
            {
                Log($"Tickets ophalen mislukt: {fout.GetString()}.");
                List<string> kandidaten;
                lock (_netwerk)
                {
                    kandidaten = _netwerk
                        .Where(r => r.Contains("incident", StringComparison.OrdinalIgnoreCase) ||
                                    r.Contains("task", StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .ToList();
                }
                if (kandidaten.Count > 0)
                {
                    Log("Interne endpoints tot nu toe gezien (klik op de cijfers in het " +
                        "Taken-blok om er meer te verzamelen):\n" + string.Join("\n", kandidaten));
                }
                return;
            }

            // Vroegste streefdatum bovenaan; tickets zonder streefdatum achteraan.
            var tickets = ParseGrid(result.Value)
                .OrderBy(t => t.Aangemaakt ?? DateTimeOffset.MaxValue)
                .ToList();
            FillList(tickets);
            Log($"{tickets.Count} openstaande meldingen opgehaald " +
                $"({tickets.Count(t => t.Lijn == "1e")} eerstelijns, " +
                $"{tickets.Count(t => t.Lijn == "2e")} tweedelijns, toegewezen aan jou).");
        }
        finally
        {
            _busy = false;
            _pulse.Actief = false;
            _fetchButton.Bezig = false;
            _fetchButton.Enabled = true;
        }
    }

    // Haalt de openstaande eerste- en tweedelijnsmeldingen op via de grid-route van de
    // klassieke behandelaarsinterface — exact wat er gebeurt als je in het Taken-blok op
    // de cijfertjes klikt (afgeluisterd via topdesk-netwerk.txt): action=monitor met
    // operator=1 (toegewezen aan mij) maakt per lijn een grid aan (sleutel "sm…"), en
    // gridpart=gridframe levert daarna de HTML-tabel met de tickets. De /tas/api-route
    // kan niet: die weigert de sessiecookie (401, applicatiewachtwoord vereist).
    private const string StartFetchScript = """
        (() => {
            const diagnose = [];
            window.__wmDiag = diagnose;
            window.__wmTickets = null;
            (async () => {
                const tekst = async (u) => {
                    diagnose.push('GET ' + u.replace(/^\/tas\/secure\//, '').slice(0, 60));
                    const r = await fetch(u, {
                        credentials: 'same-origin',
                        signal: AbortSignal.timeout(20000),
                    });
                    const t = await r.text();
                    diagnose.push('HTTP ' + r.status + ' (' + t.length + ' tekens)');
                    if (!r.ok) { throw new Error('HTTP ' + r.status + ' op ' + u); }
                    return t;
                };
                const parse = (html) => new DOMParser().parseFromString(html, 'text/html');
                try {
                    const ruw = {};
                    const haalLijn = async (line) => {
                        const monitor = await tekst('/tas/secure/incident?action=monitor' +
                            '&operator=1&showunassigned=false&line=' + line);
                        ruw['monitor' + line] = monitor;
                        const m = monitor.match(/key=(sm\d+)/i) || monitor.match(/\b(sm\d{10,})\b/);
                        if (!m) {
                            diagnose.push('lijn ' + line + ': geen gridsleutel in het antwoord');
                            return { koppen: [], rijen: [] };
                        }
                        // gridframe is een schil: de kolomdefinities staan erin als JS
                        // (Grid.columns met captions), de rijen zitten in gridpart=columns.
                        const gridHtml = await tekst('/tas/secure/grid?gridpart=gridframe&key=' + m[1]);
                        const kolommenHtml = await tekst('/tas/secure/grid?gridpart=columns&key=' + m[1]);
                        ruw['grid' + line] = gridHtml;
                        ruw['columns' + line] = kolommenHtml;
                        const defs = gridHtml.match(/Grid\.columns\s*=\s*\[([\s\S]*?)\];/);
                        const koppen = defs
                            ? [...defs[1].matchAll(/caption:\s*'([^']*)'/g)].map(x => x[1])
                            : [];
                        // De grid is kolom-georiënteerd: elke <div class="column"> bevat de
                        // spans van één kolom (id="row_{r}_cell_{c}"); de unids staan als
                        // JS-array (Columns.unids) in dezelfde pagina. Eerst dat raster
                        // terugvouwen naar rijen; de oude <tr>-route blijft als terugval.
                        const doc = parse(kolommenHtml);
                        const matrix = [];
                        for (const span of doc.querySelectorAll('[id^="row_"]')) {
                            const cel = span.id.match(/^row_(\d+)_cell_(\d+)$/);
                            if (!cel) { continue; }
                            const r = +cel[1], c = +cel[2];
                            (matrix[r] ||= [])[c] =
                                (span.textContent || '').replace(/\s+/g, ' ').trim();
                        }
                        const unids = ((kolommenHtml.match(
                                /Columns\.unids\s*=\s*\[([\s\S]*?)\]/) || [, ''])[1]
                            .match(/[0-9A-Fa-f]{16,}/g)) || [];
                        let rijen = matrix
                            .map((cellen, r) => ({ unid: unids[r] || '', cellen: cellen || [] }))
                            .filter(r => r.cellen.filter(c => c).length > 1);
                        if (rijen.length === 0) {
                            rijen = [...doc.querySelectorAll('tr')].map(tr => ({
                                unid: (tr.outerHTML.match(/unid=([0-9A-Fa-f-]{8,})/) || [])[1] || '',
                                cellen: [...tr.querySelectorAll('td')].map(td =>
                                    (td.textContent || '').replace(/\s+/g, ' ').trim()),
                            })).filter(r => r.cellen.filter(c => c).length > 1);
                        }
                        diagnose.push('lijn ' + line + ': ' + rijen.length + ' rijen, ' +
                            koppen.length + ' koppen');
                        return { koppen, rijen };
                    };
                    const lijn1 = await haalLijn(1);
                    const lijn2 = await haalLijn(2);
                    window.__wmTickets = { lijn1, lijn2, ruw, diagnose: diagnose.join('\n') };
                } catch (e) {
                    window.__wmTickets = { fout: String(e), diagnose: diagnose.join('\n') };
                }
            })();
            return 'gestart';
        })()
        """;

    /// <summary>
    /// Vertaalt de twee uitgelezen grids (koppen + celrijen) naar ticketrijen. De kolommen
    /// worden op kopnaam herkend; zonder herkenbare koppen tonen we de cellen ruw in de
    /// omschrijving zodat er in elk geval iets staat (de dump helpt dan bij het bijstellen).
    /// </summary>
    private static List<TicketRow> ParseGrid(JsonElement obj)
    {
        var rows = new List<TicketRow>();
        var nl = System.Globalization.CultureInfo.GetCultureInfo("nl-BE");
        foreach (var (prop, lijnNaam) in new[] { ("lijn1", "1e"), ("lijn2", "2e") })
        {
            if (!obj.TryGetProperty(prop, out var lijn) || lijn.ValueKind != JsonValueKind.Object ||
                !lijn.TryGetProperty("rijen", out var rijen) || rijen.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var koppen = lijn.TryGetProperty("koppen", out var k) && k.ValueKind == JsonValueKind.Array
                ? k.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();
            // Exacte kopnaam wint van "bevat": anders plakt "Behandelaar" op "Behandelaarsgroep".
            int Kolom(params string[] zoek)
            {
                var exact = koppen.FindIndex(h =>
                    zoek.Any(z => string.Equals(h, z, StringComparison.OrdinalIgnoreCase)));
                return exact >= 0 ? exact : koppen.FindIndex(h =>
                    zoek.Any(z => h.Contains(z, StringComparison.OrdinalIgnoreCase)));
            }
            var iNummer = Kolom("Meldingnummer", "nummer");
            var iOmschrijving = Kolom("Korte omschrijving (Details)", "omschrijving", "verzoek");
            var iAanmelder = Kolom("Naam aanmelder", "aanmelder", "melder");
            var iStatus = Kolom("Status");
            var iPrioriteit = Kolom("Prioriteit", "prior");
            var iBehandelaar = Kolom("Behandelaar");
            var iDatum = Kolom("Streefdatum", "datum");

            foreach (var rij in rijen.EnumerateArray())
            {
                var cellen = rij.TryGetProperty("cellen", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : new List<string>();
                // Een extra eerste cel (checkbox-kolom) verschuift alles één plek.
                var offset = cellen.Count == koppen.Count + 1 ? 1 : 0;
                string Cel(int i) => i >= 0 && i + offset < cellen.Count ? cellen[i + offset] : "";
                var row = new TicketRow
                {
                    Id = rij.TryGetProperty("unid", out var u) ? u.GetString() ?? "" : "",
                    Lijn = lijnNaam,
                    Nummer = Cel(iNummer),
                    Omschrijving = Cel(iOmschrijving),
                    Aanmelder = Cel(iAanmelder),
                    Status = Cel(iStatus),
                    Prioriteit = Cel(iPrioriteit),
                    Behandelaar = Cel(iBehandelaar),
                };
                if (row.Nummer.Length == 0 && row.Omschrijving.Length == 0)
                {
                    // Koppen niet herkend: ruwe celinhoud tonen in plaats van niets.
                    row.Omschrijving = string.Join(" · ", cellen.Where(x => x.Length > 0).Take(6));
                }
                if (DateTime.TryParse(Cel(iDatum), nl,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var aangemaakt))
                {
                    row.Aangemaakt = aangemaakt;
                }
                // Afgehandelde tickets horen niet in de werklijst: die staan alleen nog te
                // wachten op de bevestiging van de melder.
                if (row.Status.Contains("resolved", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    private void FillList(List<TicketRow> tickets)
    {
        if (IsDisposed)
        {
            return;
        }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in tickets)
        {
            var item = new ListViewItem(t.Nummer) { Tag = t };
            item.SubItems.Add(t.Lijn);
            item.SubItems.Add(t.Omschrijving);
            item.SubItems.Add(t.Aanmelder);
            item.SubItems.Add(t.Status);
            item.SubItems.Add(t.Prioriteit);
            item.SubItems.Add(t.Behandelaar);
            item.SubItems.Add(t.Aangemaakt?.LocalDateTime.ToString("dd-MM HH:mm") ?? "");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateStatus(tickets.Count);
        // Het cockpit-signaal volgt wat hier echt staat: geen open tickets meer na het
        // ophalen = de TopDesk-knop mag weer uit de werkbalk (en andersom weer aan).
        WerkSignaal.Zet("topdesk", tickets.Count > 0);
    }

    private void UpdateStatus(int aantal)
    {
        if (!IsDisposed)
        {
            _status.Text = aantal == 0 ? "Nog geen tickets opgehaald." : $"{aantal} openstaande tickets";
        }
    }

    /// <summary>
    /// Maakt van het geselecteerde ticket een taak in "Mijn taken" (categorie CED): de
    /// verzoektekst wordt binnen de ingelogde sessie van de ticketpagina geplukt, de
    /// streefdatum wordt de deadline en de taak krijgt een link naar het ticket — zodat
    /// "Bron openen in browser" in de cockpit er meteen naartoe springt.
    /// </summary>
    private async Task MaakTaakVanTicketAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not TicketRow t)
        {
            return;
        }

        var verzoek = "";
        if (t.Id.Length > 0 && _web.CoreWebView2 is not null)
        {
            Log($"Verzoektekst van {t.Nummer} ophalen…");
            try
            {
                await _web.CoreWebView2.ExecuteScriptAsync(VerzoekScript.Replace("__UNID__", t.Id));
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (IsDisposed || _web.CoreWebView2 is null)
                    {
                        return;
                    }
                    var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                        "JSON.stringify(window.__wmTicketTekst)");
                    var json = JsonSerializer.Deserialize<string>(raw);
                    if (string.IsNullOrEmpty(json) || json == "null")
                    {
                        continue;
                    }
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fout", out var f))
                    {
                        Log($"Verzoektekst ophalen mislukt: {f.GetString()} — taak zonder verzoektekst.");
                    }
                    else
                    {
                        verzoek = doc.RootElement.TryGetProperty("tekst", out var vt)
                            ? vt.GetString() ?? "" : "";
                        if (verzoek.Length == 0 &&
                            doc.RootElement.TryGetProperty("ruw", out var ruw) &&
                            ruw.GetString() is { Length: > 0 } dump)
                        {
                            // Voor het bijstellen van de veldherkenning op de ticketpagina.
                            File.WriteAllText(Path.Combine(DataDir, "topdesk-ticket-dump.html"), dump);
                            Log("Verzoekveld niet gevonden op de ticketpagina (dump geschreven) — " +
                                "taak zonder verzoektekst.");
                        }
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"Verzoektekst ophalen mislukt: {ex.Message} — taak zonder verzoektekst.");
            }
        }

        var link = t.Id.Length > 0 ? $"{BasisUrl}/tas/secure/incident?unid={t.Id}" : OperatorUrl;
        var data = MijnTaakStore.Load();
        data.Taken.Add(new MijnTaak
        {
            Tekst = $"TopDesk {t.Nummer}: {t.Omschrijving}".Trim().TrimEnd(':'),
            Categorie = "CED",
            Deadline = t.Aangemaakt is { } streef ? DateOnly.FromDateTime(streef.LocalDateTime) : null,
            Mail = new TaakMail
            {
                Van = t.Aanmelder,
                Onderwerp = $"TopDesk {t.Nummer} – {t.Omschrijving}",
                Tekst = verzoek,
                Link = link,
                Datum = DateTimeOffset.Now,
            },
        });
        MijnTaakStore.Save(data);
        Log($"Taak aangemaakt: TopDesk {t.Nummer}, categorie CED" +
            $"{(t.Aangemaakt is not null ? ", deadline = streefdatum" : "")}" +
            $"{(verzoek.Length > 0 ? ", met verzoektekst" : "")}.");
        Toast.Toon(this, $"Taak aangemaakt voor {t.Nummer}", Fluent.Check);
    }

    // Haalt de verzoektekst van één ticket op (binnen de sessie). De ticketpagina is de
    // klassieke incidentkaart; het verzoekveld wordt op naam/id gezocht. Lukt dat niet,
    // dan gaat de ruwe HTML mee terug zodat de veldherkenning bijgesteld kan worden.
    private const string VerzoekScript = """
        (() => {
            window.__wmTicketTekst = null;
            (async () => {
                try {
                    const r = await fetch('/tas/secure/incident?unid=__UNID__', {
                        credentials: 'same-origin',
                        signal: AbortSignal.timeout(20000),
                    });
                    const html = await r.text();
                    const doc = new DOMParser().parseFromString(html, 'text/html');
                    const veld = doc.querySelector(
                        'textarea[name*="verzoek" i], textarea[id*="verzoek" i], ' +
                        '[id*="verzoek" i], textarea[name*="request" i]');
                    const tekst = veld
                        ? (veld.value || veld.textContent || '').trim() : '';
                    window.__wmTicketTekst = {
                        status: r.status,
                        tekst: tekst,
                        ruw: tekst ? '' : html.slice(0, 60000),
                    };
                } catch (e) {
                    window.__wmTicketTekst = { fout: String(e) };
                }
            })();
            return 'gestart';
        })()
        """;

    /// <summary>Dubbelklik op een rij: het ticket openen in de browser rechts.</summary>
    private void OpenGeselecteerdTicket()
    {
        if (_list.SelectedItems.Count > 0 &&
            _list.SelectedItems[0].Tag is TicketRow { Id.Length: > 0 } t)
        {
            _web.CoreWebView2?.Navigate($"{BasisUrl}/tas/secure/incident?unid={t.Id}");
        }
    }

    // ---------- Login-gegevens ----------

    /// <summary>Klein dialoogje om gebruikersnaam en wachtwoord (DPAPI-versleuteld) te bewaren.</summary>
    private void EditLogin()
    {
        using var dlg = new Form
        {
            Text = "TopDesk – login-gegevens",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 160),
        };
        var naam = new TextBox { Left = 140, Top = 16, Width = 260, Text = _settings.Gebruikersnaam };
        var wachtwoord = new TextBox
        {
            Left = 140, Top = 52, Width = 260, UseSystemPasswordChar = true,
            Text = _settings.Wachtwoord,
        };
        var ok = new ModernButton
        {
            Text = "Bewaren", Left = 300, Top = 100, Width = 100,
            Kind = ButtonKind.Accent, DialogResult = DialogResult.OK,
        };
        dlg.Controls.Add(new Label { Text = "Gebruikersnaam", Left = 16, Top = 19, AutoSize = true });
        dlg.Controls.Add(new Label { Text = "Wachtwoord", Left = 16, Top = 55, AutoSize = true });
        dlg.Controls.AddRange(new Control[] { naam, wachtwoord, ok });
        dlg.AcceptButton = ok;
        Theme.Apply(dlg);

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Gebruikersnaam = naam.Text.Trim();
            _settings.Wachtwoord = wachtwoord.Text;
            _settings.Save();
            Log("Login-gegevens bewaard (wachtwoord DPAPI-versleuteld).");
        }
    }

    private void Log(string message)
    {
        if (IsDisposed || _log.IsDisposed)
        {
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + line);
    }
}
