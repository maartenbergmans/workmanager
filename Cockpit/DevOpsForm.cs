using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Venster voor de Azure DevOps-werkitems van CED (ced-cloud-tfs.visualstudio.com, project
/// CAREX NL). Links de lijst met werkitems die aan Maarten toegewezen zijn, rechts de
/// ingebedde browser met blijvende Microsoft-sessie (zelfde soort login als CED-Outlook;
/// de login-assistent vult alvast het e-mailadres in). De items komen via de REST-API uit
/// de ingelogde sessie zelf — geen PAT of apart wachtwoord nodig. Een item is met één klik
/// om te zetten naar een taak in "Mijn taken", mét de link terug naar het werkitem zodat
/// het daar afgesloten kan worden ("Bron openen in browser" op de taak).
/// </summary>
public class DevOpsForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private const string BasisUrl = "https://ced-cloud-tfs.visualstudio.com";
    private const string Project = "CAREX%20NL";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ModernListView _list;
    private readonly TextBox _log;
    private readonly Label _status;
    private readonly ModernButton _fetchButton;
    private readonly PulseBar _pulse = new();

    private bool _busy;
    private bool _loginAssistBezig;

    private sealed class ItemRow
    {
        public int Id;
        public string Type = "";
        public string Titel = "";
        public string Status = "";
        public string Prioriteit = "";
        public DateTimeOffset? Gewijzigd;
    }

    public DevOpsForm()
    {
        Text = "Azure DevOps – CAREX-werkitems";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 900);
        WindowState = FormWindowState.Maximized;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _fetchButton = new ModernButton
        {
            Text = "Werkitems ophalen", Width = 175, Kind = ButtonKind.Accent, Glyph = Fluent.Refresh,
        };
        _fetchButton.Click += async (_, _) => await FetchItemsAsync();
        var taakKnop = new ModernButton { Text = "Taak aanmaken", Width = 150, Glyph = Fluent.Checkbox };
        taakKnop.Click += (_, _) => MaakTaakVanItem();
        var navButton = new ModernButton { Text = "Naar DevOps", Width = 135, Glyph = Fluent.Globe };
        navButton.Click += (_, _) => _web.CoreWebView2?.Navigate(RecentUrl);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[] { _fetchButton, taakKnop, navButton, _status });

        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen werkitems — log in en klik op 'Werkitems ophalen'.",
            LeegGlyph = Fluent.Lijst,
        };
        _list.Columns.Add("#", 70);
        _list.Columns.Add("Type", 90);
        _list.Columns.Add("Titel", 330);
        _list.Columns.Add("Status", 100);
        _list.Columns.Add("Prio", 50);
        _list.Columns.Add("Gewijzigd", 110);
        _list.DoubleClick += (_, _) => OpenGeselecteerdItem();

        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var taakItem = new ToolStripMenuItem("Taak aanmaken (met link naar werkitem)");
        taakItem.Click += (_, _) => MaakTaakVanItem();
        listMenu.Items.Add(taakItem);
        var openItem = new ToolStripMenuItem("Werkitem openen in browser rechts");
        openItem.Click += (_, _) => OpenGeselecteerdItem();
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
            // De lijst houdt zijn vaste breedte; alle extra ruimte (het venster start
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
            // bekend): de lijst krijgt precies zijn kolommen, ál de rest is voor DevOps.
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
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "devops");
        UpdateStatus(0);
    }

    private static string RecentUrl => $"{BasisUrl}/{Project}/_workitems/recentlyupdated/";

    private static string EditUrl(int id) => $"{BasisUrl}/{Project}/_workitems/edit/{id}";

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigen profielmap zodat de Microsoft-sessie (cookies) tussen sessies bewaard blijft.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-devops"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.IsMuted = true;

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

            Log("Browser gestart. Na het inloggen worden de werkitems automatisch opgehaald.");
            _web.CoreWebView2.Navigate(RecentUrl);
        }
        catch (Exception ex)
        {
            Log($"WebView2 kon niet starten: {ex.Message}");
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
                    MicrosoftLogin.Verwerk(
                        await _web.CoreWebView2.ExecuteScriptAsync(MicrosoftLogin.VulScript()));
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
        if (bron.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase) &&
            !_busy && !_autoOpgehaald)
        {
            // Ingelogd in DevOps: één keer automatisch ophalen; daarna bepaalt de knop
            // het ritme (niet bij elke klik in de browser opnieuw).
            _autoOpgehaald = true;
            await FetchItemsAsync();
        }
    }

    // ---------- Werkitems ophalen ----------

    private async Task FetchItemsAsync()
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
            Log("Open tasks (aan mij toegewezen) ophalen…");
            await _web.CoreWebView2.ExecuteScriptAsync(FetchScript);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                if (IsDisposed || _web.CoreWebView2 is null)
                {
                    return;
                }
                var raw = await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__wmItems)");
                var json = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null")
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fout", out var fout))
                {
                    Log($"Ophalen mislukt: {fout.GetString()} — ben je ingelogd (browser rechts)?");
                    return;
                }
                var rows = new List<ItemRow>();
                foreach (var w in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    var row = new ItemRow
                    {
                        Id = w.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                        Titel = w.TryGetProperty("titel", out var t) ? t.GetString() ?? "" : "",
                        Status = w.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                        Type = w.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                        Prioriteit = w.TryGetProperty("prioriteit", out var p) ? p.ToString() : "",
                    };
                    if (w.TryGetProperty("gewijzigd", out var g) &&
                        DateTimeOffset.TryParse(g.GetString(), out var gewijzigd))
                    {
                        row.Gewijzigd = gewijzigd;
                    }
                    rows.Add(row);
                }
                FillList(rows);
                Log($"{rows.Count} werkitems opgehaald.");
                return;
            }
            Log("Geen antwoord van de pagina — log in (browser rechts) en probeer opnieuw.");
        }
        catch (Exception ex)
        {
            Log($"Ophalen mislukt: {ex.Message}");
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                _fetchButton.Enabled = true;
                _fetchButton.Bezig = false;
                _pulse.Actief = false;
            }
        }
    }

    // WIQL binnen de ingelogde sessie: eerst de id's van mijn open werkitems (nieuwste
    // wijziging eerst), dan de velden in één batch. Afgesloten items blijven weg, en
    // Active ook: daar is Maarten al mee bezig — de lijst dient voor wat nog te doen valt.
    private const string FetchScript = """
        (() => {
            window.__wmItems = null;
            (async () => {
                try {
                    const wiql = await fetch('/CAREX%20NL/_apis/wit/wiql?api-version=6.0', {
                        method: 'POST',
                        credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ query:
                            "SELECT [System.Id] FROM WorkItems " +
                            "WHERE [System.TeamProject] = @project " +
                            "AND [System.WorkItemType] = 'Task' " +
                            "AND [System.AssignedTo] = @Me " +
                            "AND [System.State] NOT IN ('Closed', 'Done', 'Removed', 'Active') " +
                            "ORDER BY [System.ChangedDate] DESC" }),
                        signal: AbortSignal.timeout(20000),
                    });
                    if (!wiql.ok) {
                        window.__wmItems = { fout: 'HTTP ' + wiql.status + ' op wiql' };
                        return;
                    }
                    const q = await wiql.json();
                    const ids = (q.workItems || []).slice(0, 100).map(w => w.id);
                    if (ids.length === 0) { window.__wmItems = { items: [] }; return; }
                    const velden = ['System.Title', 'System.State', 'System.WorkItemType',
                        'System.ChangedDate', 'Microsoft.VSTS.Common.Priority'].join(',');
                    const r = await fetch('/CAREX%20NL/_apis/wit/workitems?ids=' +
                        ids.join(',') + '&fields=' + velden + '&api-version=6.0', {
                        credentials: 'same-origin',
                        signal: AbortSignal.timeout(20000),
                    });
                    if (!r.ok) {
                        window.__wmItems = { fout: 'HTTP ' + r.status + ' op workitems' };
                        return;
                    }
                    const d = await r.json();
                    window.__wmItems = { items: (d.value || []).map(w => ({
                        id: w.id,
                        titel: w.fields['System.Title'] || '',
                        status: w.fields['System.State'] || '',
                        type: w.fields['System.WorkItemType'] || '',
                        prioriteit: w.fields['Microsoft.VSTS.Common.Priority'] ?? '',
                        gewijzigd: w.fields['System.ChangedDate'] || '',
                    })) };
                } catch (e) {
                    window.__wmItems = { fout: String(e) };
                }
            })();
            return 'gestart';
        })()
        """;

    private void FillList(List<ItemRow> items)
    {
        if (IsDisposed)
        {
            return;
        }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in items)
        {
            var item = new ListViewItem(t.Id.ToString()) { Tag = t };
            item.SubItems.Add(t.Type);
            item.SubItems.Add(t.Titel);
            item.SubItems.Add(t.Status);
            item.SubItems.Add(t.Prioriteit);
            item.SubItems.Add(t.Gewijzigd?.LocalDateTime.ToString("dd-MM HH:mm") ?? "");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateStatus(items.Count);
        // Het cockpit-signaal volgt wat hier echt staat: geen open tasks meer na het
        // ophalen = de DevOps-knop mag weer uit de werkbalk (en andersom weer aan).
        WerkSignaal.Zet("devops", items.Count > 0);
    }

    private void UpdateStatus(int aantal)
    {
        if (!IsDisposed)
        {
            _status.Text = aantal == 0 ? "Nog geen werkitems opgehaald." : $"{aantal} open werkitems";
        }
    }

    // ---------- Taak maken en doorklikken ----------

    /// <summary>
    /// Zet het geselecteerde werkitem om naar een taak in "Mijn taken" (categorie CED),
    /// met de link naar het werkitem als bron — vanuit de taak klik je dus zo door naar
    /// DevOps om het item af te sluiten.
    /// </summary>
    private void MaakTaakVanItem()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not ItemRow t)
        {
            return;
        }
        var data = MijnTaakStore.Load();
        var tekst = $"DevOps #{t.Id}: {t.Titel}".Trim().TrimEnd(':');
        if (data.Taken.Any(x => !x.Klaar && x.Tekst == tekst))
        {
            Toast.Toon(this, $"Er staat al een open taak voor #{t.Id}", Fluent.Checkbox);
            return;
        }
        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "CED",
            // Zonder deadline valt een taak buiten het horizonfilter van de cockpit
            // ("Deadline ≤ 2 dagen") en lijkt hij spoorloos — vandaag is een goed startpunt.
            Deadline = DateOnly.FromDateTime(DateTime.Today),
            Mail = new TaakMail
            {
                Onderwerp = $"DevOps #{t.Id} – {t.Titel}",
                Tekst = $"{t.Type} · status {t.Status}" +
                    (t.Prioriteit.Length > 0 ? $" · prio {t.Prioriteit}" : ""),
                Link = EditUrl(t.Id),
                Datum = DateTimeOffset.Now,
            },
        });
        MijnTaakStore.Save(data);
        Log($"Taak aangemaakt voor werkitem #{t.Id}, categorie CED, met link naar DevOps.");
        Toast.Toon(this, $"Taak aangemaakt voor #{t.Id}", Fluent.Check);
    }

    /// <summary>Dubbelklik op een rij: het werkitem openen in de browser rechts.</summary>
    private void OpenGeselecteerdItem()
    {
        if (_list.SelectedItems.Count > 0 &&
            _list.SelectedItems[0].Tag is ItemRow { Id: > 0 } t)
        {
            _web.CoreWebView2?.Navigate(EditUrl(t.Id));
        }
    }

    private void Log(string melding)
    {
        if (IsDisposed)
        {
            return;
        }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {melding}\r\n");
    }
}
