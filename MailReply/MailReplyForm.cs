using System.Diagnostics;
using System.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Venster voor het beantwoorden van Gmail-mail. Links de opgehaalde mails met per mail of er
/// een conceptantwoord klaarstaat (aangevinkt = wordt verstuurd), rechts de originele mail en
/// het bewerkbare concept. Concepten worden gegenereerd via de Claude API; versturen gebeurt
/// via SMTP als reply in de juiste thread.
/// </summary>
public class MailReplyForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly ModernListView _list;
    private readonly TextBox _log;
    private readonly WebView2 _origineel = new() { Dock = DockStyle.Fill };
    private readonly TextBox _concept;
    private readonly TextBox _feedback;
    private readonly ModernButton _feedbackButton;
    private readonly CheckBox _replyAll;
    private readonly ModernGroupBox _origineelGroup;
    private readonly ToolTip _tip = new();
    private bool _replyAllUpdating; // programmatisch zetten niet als gebruikerskeuze opslaan
    private readonly System.Windows.Forms.Timer _kolomSaveTimer = new() { Interval = 600 };
    private double[] _kolomVerhoudingen = Array.Empty<double>();
    private bool _kolommenSchalen; // meeschalen met het venster niet als handmatige wijziging zien
    private readonly Label _status;
    private readonly ModernButton _fetchButton;
    private readonly ModernButton _sendButton;
    private readonly ModernButton _archiveButton;
    private readonly ModernButton _snoozeButton;
    private readonly PulseBar _pulse = new();
    private readonly TextBox _zoek;
    private readonly CancellationTokenSource _cts = new();

    private MailReplySettings _settings;
    private GoogleChatSettings _chatSettings = GoogleChatSettings.Load();
    private readonly WhatsAppClient _whatsapp = WhatsAppClient.Instance; // gedeelde sessie
    private List<MailBericht> _mails = new();
    private Dictionary<string, ConceptCache.Entry> _cache = ConceptCache.Load();
    private MailBericht? _getoond;
    private string? _wachtendeWeergave;
    private bool _busy;
    private bool _genereren; // concepten worden nog gemaakt; archiveren/snoozen mag intussen al
    private readonly HashSet<string> _gearchiveerd = new(); // tijdens genereren gearchiveerd: niet meer cachen

    public MailReplyForm()
    {
        Text = "Mail beantwoorden – Gmail";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 900);
        WindowState = FormWindowState.Maximized;

        _settings = MailReplySettings.Load();

        // Werkbalk
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _fetchButton = new ModernButton
        {
            Text = "Mails ophalen", Width = 155, Kind = ButtonKind.Accent, Glyph = "\uE72C",
        };
        _fetchButton.Click += async (_, _) => await FetchMailsAsync();
        _sendButton = new ModernButton
        {
            Text = "Geselecteerde versturen…", Width = 215, Enabled = false, Glyph = "\uE724",
        };
        _sendButton.Click += async (_, _) => await SendSelectedAsync();
        _archiveButton = new ModernButton { Text = "Archiveren", Width = 125, Enabled = false, Glyph = "\uE7B8" };
        _archiveButton.Click += async (_, _) => await ArchiveerSelectieAsync();
        _snoozeButton = new ModernButton { Text = "Snoozen…", Width = 120, Enabled = false, Glyph = "\uE823" };
        _snoozeButton.Click += async (_, _) => await SnoozeSelectieAsync();
        var instructiesButton = new ModernButton { Text = "Instructies beheren…", Width = 180, Glyph = "\uE70F" };
        instructiesButton.Click += (_, _) =>
        {
            using var form = new InstructionsForm();
            form.ShowDialog(this);
        };
        var settingsButton = new ModernButton { Text = "Instellingen…", Width = 140, Glyph = "\uE713" };
        settingsButton.Click += (_, _) => EditSettings();
        var whatsappButton = new ModernButton { Text = "WhatsApp koppelen…", Width = 180 };
        whatsappButton.Click += async (_, _) =>
        {
            try
            {
                Log("WhatsApp koppelen: er opent een venster met een QR-code — scan die met je telefoon " +
                    "(WhatsApp → Instellingen → Gekoppelde apparaten).");
                await _whatsapp.KoppelAsync(_cts.Token);
                Log("WhatsApp is gekoppeld; chats komen mee bij 'Mails ophalen'.");
                Toast.Toon(this, "WhatsApp gekoppeld", Fluent.Check);
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten tijdens het koppelen.
            }
            catch (Exception ex)
            {
                Log($"WhatsApp koppelen mislukt: {ex.Message}");
            }
        };
        _zoek = new TextBox { Width = 190, PlaceholderText = "Zoeken…", Margin = new Padding(12, 5, 3, 3) };
        _zoek.TextChanged += (_, _) => FillList();
        _zoek.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _zoek.Clear();
            }
        };
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[]
        {
            _fetchButton, _sendButton, _archiveButton, _snoozeButton, instructiesButton, settingsButton,
            whatsappButton, _zoek, _status,
        });

        // Maillijst
        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen mails — klik op 'Mails ophalen'.",
            LeegGlyph = Fluent.Mail,
        };
        _list.Columns.Add("Van", 220);
        _list.Columns.Add("Onderwerp", 380);
        _list.Columns.Add("Ontvangen", 120);
        _list.Columns.Add("Concept", 300);
        if (_settings.KolomBreedtes.Count == _list.Columns.Count)
        {
            for (var i = 0; i < _list.Columns.Count; i++)
            {
                _list.Columns[i].Width = Math.Max(60, _settings.KolomBreedtes[i]);
            }
        }
        _kolomVerhoudingen = BerekenKolomVerhoudingen();
        _list.ClientSizeChanged += (_, _) => SchaalKolommen();
        _list.ColumnWidthChanged += (_, _) =>
        {
            if (!_kolommenSchalen)
            {
                _kolomVerhoudingen = BerekenKolomVerhoudingen();
                _kolomSaveTimer.Stop();
                _kolomSaveTimer.Start(); // pas bewaren als het slepen even stilligt
            }
        };
        _kolomSaveTimer.Tick += (_, _) => BewaarKolomBreedtes();
        _list.SelectedIndexChanged += (_, _) => ToonSelectie();

        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var regenItem = new ToolStripMenuItem("Concept opnieuw genereren");
        regenItem.Click += async (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is MailBericht mail)
            {
                await GenereerConceptAsync(mail);
                ToonSelectie();
            }
        };
        listMenu.Items.Add(regenItem);
        var archiveItem = new ToolStripMenuItem("Archiveren");
        archiveItem.Click += async (_, _) => await ArchiveerSelectieAsync();
        listMenu.Items.Add(archiveItem);
        var snoozeItem = new ToolStripMenuItem("Snoozen…");
        snoozeItem.Click += async (_, _) => await SnoozeSelectieAsync();
        listMenu.Items.Add(snoozeItem);
        var taakItem = new ToolStripMenuItem("Taak maken in Mijn taken…");
        taakItem.Click += async (_, _) => await TaakVanMailAsync();
        listMenu.Items.Add(taakItem);
        // Google Chat: een duim is vaak antwoord genoeg — vandaar ook de sneltoets D.
        var duimItem = new ToolStripMenuItem("👍 Duim omhoog") { ShortcutKeyDisplayString = "D" };
        duimItem.Click += async (_, _) => await ReageerOpChatAsync();
        listMenu.Items.Add(duimItem);
        var reactieItem = new ToolStripMenuItem("Andere reactie");
        foreach (var emoji in Reacties)
        {
            var mi = new ToolStripMenuItem(emoji);
            mi.Click += async (_, _) => await ReageerOpChatAsync(emoji);
            reactieItem.DropDownItems.Add(mi);
        }
        listMenu.Items.Add(reactieItem);
        // Rechtstreeks naar Drive (via de API), met de favorieten en recente mappen als submenu.
        var driveItem = BijlagenNaarDrive.Submenu(async (id, naam) => await BijlagenNaarDriveAsync(id, naam));
        listMenu.Items.Add(driveItem);
        var bijlagenItem = new ToolStripMenuItem("Bijlagen opslaan op schijf…");
        bijlagenItem.Click += async (_, _) => await BijlagenOpslaanAsync();
        listMenu.Items.Add(bijlagenItem);
        var billitItem = new ToolStripMenuItem("Bijlage doorsturen naar Billit…");
        billitItem.Click += async (_, _) => await BillitDoorsturenAsync();
        listMenu.Items.Add(billitItem);
        var uitschrijfItem = new ToolStripMenuItem("Uitschrijven (afmeldlink)…");
        uitschrijfItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 &&
                _list.SelectedItems[0].Tag is MailBericht m &&
                m.UitschrijfUrl.Length > 0)
            {
                OpenExtern(m.UitschrijfUrl);
                Log($"Afmeldpagina geopend voor {m.Van}.");
            }
        };
        listMenu.Items.Add(uitschrijfItem);
        listMenu.Opening += (_, _) =>
        {
            var geselecteerdeMail = _list.SelectedItems.Count > 0
                ? _list.SelectedItems[0].Tag as MailBericht
                : null;
            var heeftBijlagen = geselecteerdeMail is not null &&
                (geselecteerdeMail.Bijlagen.Count > 0 || geselecteerdeMail.LinkBijlagen.Count > 0);
            bijlagenItem.Enabled = heeftBijlagen;
            driveItem.Enabled = heeftBijlagen;
            billitItem.Enabled = heeftBijlagen;
            uitschrijfItem.Enabled = geselecteerdeMail is { UitschrijfUrl.Length: > 0 };
            // Reageren kan alleen op een Google Chat-bericht.
            duimItem.Visible = reactieItem.Visible = IsChatRij(geselecteerdeMail);
        };
        _list.ContextMenuStrip = listMenu;
        _list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.D && e.Modifiers == Keys.None && IsChatRij(Geselecteerd()))
            {
                e.SuppressKeyPress = true;
                await ReageerOpChatAsync();
            }
        };

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

        // Rechts: originele mail met opmaak (boven) + bewerkbaar concept (onder)
        _concept = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        _origineelGroup = new ModernGroupBox
        {
            Text = "Ontvangen mail", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        _origineelGroup.Controls.Add(_origineel);
        var conceptGroup = new ModernGroupBox
        {
            Text = "Conceptantwoord (bewerkbaar)", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        _feedback = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "Feedback voor Claude over dit concept (bv. \"korter\", \"vermeld dat ik vrijdag " +
                "afwezig ben\")… Enter = toepassen, Shift+Enter = nieuwe regel.",
        };
        _feedback.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await VerwerkFeedbackAsync();
            }
        };
        _feedbackButton = new ModernButton { Text = "Pas concept aan", Height = 31, Dock = DockStyle.Top };
        _feedbackButton.Click += async (_, _) => await VerwerkFeedbackAsync();
        _replyAll = new CheckBox
        {
            Text = "Allen beantwoorden",
            Height = 31,
            Dock = DockStyle.Top,
            Enabled = false,
        };
        _replyAll.CheckedChanged += (_, _) =>
        {
            if (!_replyAllUpdating && _getoond is { } m)
            {
                m.AlleBeantwoorden = _replyAll.Checked;
                BewaarInCache(m);
            }
        };
        var checkboxKolom = new Panel { Dock = DockStyle.Left, Width = 200, Padding = new Padding(0, 0, 8, 0) };
        checkboxKolom.Controls.Add(_replyAll);
        var knopKolom = new Panel { Dock = DockStyle.Right, Width = 143, Padding = new Padding(8, 0, 0, 0) };
        knopKolom.Controls.Add(_feedbackButton);
        var feedbackPanel = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(0, 8, 0, 0) };
        feedbackPanel.Controls.Add(_feedback);
        feedbackPanel.Controls.Add(checkboxKolom);
        feedbackPanel.Controls.Add(knopKolom);
        conceptGroup.Controls.Add(_concept);
        conceptGroup.Controls.Add(feedbackPanel);

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 400,
        };
        rightSplit.Panel1.Controls.Add(_origineelGroup);
        rightSplit.Panel2.Controls.Add(conceptGroup);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 720,
        };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(rightSplit);

        Controls.Add(split);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) =>
        {
            BewaarConcept(); // laatste bewerking niet verliezen
            if (_kolomSaveTimer.Enabled)
            {
                BewaarKolomBreedtes(); // sleepactie vlak voor het sluiten niet verliezen
            }
            _cts.Cancel();
            // De WhatsApp-sessie bewust laten leven: gedeeld met de cockpit en het volgende venster.
        };
        Shown += async (_, _) =>
        {
            await InitWebViewAsync();

            // Bij het openen meteen de inbox tonen; zonder app-wachtwoord eerst instellen.
            if (_settings.AppWachtwoord.Length == 0)
            {
                Log("Nog geen Gmail-app-wachtwoord ingesteld — open eerst 'Instellingen…'.");
                return;
            }
            await FetchMailsAsync();
        };
        Theme.Apply(this, fade: false); // fade niet: WebView2 rendert niet in een gelaagd venster
        VensterGeheugen.Volg(this, "mailreply");
        _origineel.DefaultBackgroundColor = Theme.Bg; // geen witte flits bij het laden
        UpdateStatus();
    }

    /// <summary>
    /// Start de ingebedde browser voor de mailweergave. Scripts staan uit (mails zijn
    /// onvertrouwde inhoud); klikken op links opent de standaardbrowser.
    /// </summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-mail"));
            await _origineel.EnsureCoreWebView2Async(env);

            var core = _origineel.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExtern(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExtern(e.Uri);
                }
            };

            core.NavigateToString(_wachtendeWeergave ?? LegeWeergave);
            _wachtendeWeergave = null;
        }
        catch (Exception ex)
        {
            Log($"Mailweergave (WebView2) kon niet starten: {ex.Message}");
        }
    }

    private static void OpenExtern(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Geen standaardbrowser gevonden; link stilletjes negeren.
        }
    }

    // ---------- Mails ophalen + concepten genereren ----------

    private async Task FetchMailsAsync()
    {
        if (_busy || _genereren)
        {
            return;
        }

        _settings = MailReplySettings.Load();
        if (_settings.AppWachtwoord.Length == 0 && !EditSettings())
        {
            return;
        }

        _busy = true;
        _fetchButton.Enabled = false;
        _fetchButton.Bezig = true;
        _pulse.Actief = true;
        var conceptenFase = false;
        try
        {
            Log($"Inbox van {_settings.Email} uitlezen ({(_settings.AlleenOngelezen ? "ongelezen" : "alle")} mails, max. {_settings.MaxMails})…");
            _mails = await GmailClient.FetchAsync(_settings, _cts.Token);
            _chatSettings = GoogleChatSettings.Load();
            if (_chatSettings.Gekoppeld)
            {
                try
                {
                    Log("Google Chat-gesprekken ophalen…");
                    _mails.AddRange(await GoogleChatClient.FetchAsync(_chatSettings, _cts.Token));
                }
                catch (Exception ex)
                {
                    Log($"Google Chat ophalen mislukt: {ex.Message}");
                }
            }
            if (WhatsAppClient.OoitGekoppeld)
            {
                try
                {
                    Log("WhatsApp-chats met ongelezen berichten ophalen…");
                    _mails.AddRange(await _whatsapp.FetchAsync(Log, _cts.Token));
                }
                catch (Exception ex)
                {
                    Log($"WhatsApp ophalen mislukt: {ex.Message}");
                }
            }
            _gearchiveerd.Clear();

            // Eerder gegenereerde (of bewerkte) concepten uit de cache overnemen.
            _cache = ConceptCache.Load();
            foreach (var mail in _mails)
            {
                if (mail.MessageId.Length > 0 && _cache.TryGetValue(mail.MessageId, out var bewaard))
                {
                    mail.ConceptKlaar = bewaard.ConceptKlaar;
                    mail.Concept = bewaard.Concept;
                    mail.Reden = bewaard.Reden;
                    mail.AlleBeantwoorden = bewaard.AlleBeantwoorden && mail.OverigeOntvangers.Count > 0;
                    mail.Genegeerd = bewaard.Genegeerd;
                    mail.Urgent = bewaard.Urgent;
                }
            }
            // Eerder gescreende chats zonder antwoord/actie niet opnieuw tonen; zodra er
            // nieuwe berichten zijn, verandert de cachesleutel en volgt een verse beoordeling.
            var genegeerd = _mails.RemoveAll(m => m.IsChat && m.Genegeerd);
            if (genegeerd > 0)
            {
                Log($"{genegeerd} chat{(genegeerd == 1 ? "" : "s")} zonder antwoord of actie overgeslagen (eerder gescreend).");
            }
            FillList();

            if (_mails.Count == 0)
            {
                Log("Geen mails gevonden.");
                return;
            }

            var teGenereren = _mails
                .Where(m => m.MessageId.Length == 0 || !_cache.ContainsKey(m.MessageId))
                .ToList();
            var uitCache = _mails.Count - teGenereren.Count;
            if (teGenereren.Count == 0)
            {
                Log($"{_mails.Count} mails opgehaald; alle concepten kwamen uit de cache.");
                return;
            }
            Log($"{_mails.Count} mails opgehaald" +
                (uitCache > 0 ? $", {uitCache} concepten uit de cache" : "") +
                $". {teGenereren.Count} nieuwe mails beoordelen via Claude…");

            // Het IMAP-werk is klaar; vanaf hier lopen alleen nog Claude-aanroepen. De
            // vergrendeling loslaten zodat archiveren/snoozen intussen al kan (elke
            // Gmail-operatie opent toch een eigen verbinding).
            conceptenFase = true;
            _busy = false;
            _genereren = true;
            UpdateStatus();

            // Max. 3 mails tegelijk beoordelen; UI-updates gebeuren per mail zodra het concept klaar is.
            using var limiet = new SemaphoreSlim(3);
            var taken = teGenereren.Select(async mail =>
            {
                await limiet.WaitAsync(_cts.Token);
                try
                {
                    await GenereerConceptAsync(mail);
                }
                finally
                {
                    limiet.Release();
                }
            }).ToList();
            await Task.WhenAll(taken);

            Log($"Klaar: {_mails.Count(m => m.ConceptKlaar)} van {_mails.Count} mails hebben een conceptantwoord. " +
                "Kijk ze na (rechts, bewerkbaar) en klik op 'Geselecteerde versturen…'.");
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            Log($"Ophalen mislukt: {ex.Message}");
        }
        finally
        {
            _genereren = false;
            if (!conceptenFase)
            {
                // In de conceptenfase is _busy al vrijgegeven; niet overschrijven,
                // want een archiveer-/snoozeactie kan op dat moment nog bezig zijn.
                _busy = false;
            }
            _pulse.Actief = false;
            _fetchButton.Bezig = false;
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    private async Task GenereerConceptAsync(MailBericht mail)
    {
        UpdateRow(mail, "bezig…", kleur: Theme.Muted);
        try
        {
            var instructies = MailReplySettings.LoadInstructies();
            // Bij Gmail-mails de recente correspondentie met de afzender meegeven (laatste
            // 2 maanden), zodat het concept aansluit op lopende afspraken en de juiste toon.
            var historiek = "";
            if (!mail.IsChat && mail.VanAdres.Length > 0)
            {
                try
                {
                    historiek = string.Join("\n\n", await GmailClient.CorrespondentieAsync(
                        _settings, mail.VanAdres, maanden: 2, max: 12, _cts.Token));
                }
                catch
                {
                    // Zonder context gewoon een concept maken.
                }
            }
            var resultaat = await ClaudeDrafter.DraftAsync(mail, instructies, _settings, _cts.Token, historiek);
            if (mail.MessageId.Length > 0 && _gearchiveerd.Contains(mail.MessageId))
            {
                // Intussen gearchiveerd; concept stilletjes laten vallen.
                return;
            }
            if (mail.IsChat && !resultaat.Antwoorden && !resultaat.Actie)
            {
                // Screening: chat vraagt geen antwoord en geen actie — uit de lijst, oordeel cachen.
                mail.Genegeerd = true;
                mail.Reden = resultaat.Reden;
                BewaarInCache(mail);
                if (!IsDisposed)
                {
                    _mails.Remove(mail);
                    _list.Items.Cast<ListViewItem>()
                        .FirstOrDefault(i => ReferenceEquals(i.Tag, mail))?.Remove();
                    UpdateStatus();
                }
                Log($"Chat \"{mail.Van}\" uit de lijst gelaten ({resultaat.Reden}).");
                return;
            }
            mail.ConceptKlaar = resultaat.Antwoorden && !string.IsNullOrWhiteSpace(resultaat.Concept);
            mail.Concept = resultaat.Concept;
            mail.Reden = resultaat.Reden;
            mail.Urgent = resultaat.Urgent;
            BewaarInCache(mail);
            KleurUrgent(mail);
            UpdateRow(mail, mail.ConceptKlaar ? $"✔ {mail.Reden}" : mail.Reden, check: mail.ConceptKlaar);
            Log(mail.ConceptKlaar
                ? $"Concept klaar voor {mail.Van} – \"{mail.Onderwerp}\""
                : $"Geen antwoord voor {mail.Van} – \"{mail.Onderwerp}\" ({mail.Reden})");
        }
        catch (OperationCanceledException)
        {
            UpdateRow(mail, "afgebroken");
        }
        catch (Exception ex)
        {
            mail.ConceptKlaar = false;
            mail.Reden = "fout bij genereren";
            UpdateRow(mail, "fout bij genereren");
            Log($"Concept voor {mail.Van} mislukt: {ex.Message}");
        }
    }

    // ---------- Versturen ----------

    private async Task SendSelectedAsync()
    {
        if (_busy || _genereren)
        {
            return;
        }

        BewaarConcept();
        var geselecteerd = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MailBericht>().ToList();
        var rows = geselecteerd.Where(m => !string.IsNullOrWhiteSpace(m.Concept)).ToList();
        if (geselecteerd.Count > rows.Count)
        {
            Log($"{geselecteerd.Count - rows.Count} geselecteerde mail(s) zonder concept overgeslagen.");
        }
        if (rows.Count == 0)
        {
            return;
        }

        var vraag = $"{rows.Count} antwoord{(rows.Count == 1 ? "" : "en")} versturen via {_settings.Email}?\n\n" +
                    string.Join("\n", rows.Take(10).Select(m =>
                        $"• {m.Van} – {m.Onderwerp}" +
                        (m.AlleBeantwoorden && m.OverigeOntvangers.Count > 0
                            ? $" (allen, +{m.OverigeOntvangers.Count} cc)" : ""))) +
                    (rows.Count > 10 ? $"\n… en nog {rows.Count - 10}" : "");
        if (MessageBox.Show(this, vraag, "WorkManager", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return;
        }

        _busy = true;
        _fetchButton.Enabled = false;
        _sendButton.Enabled = false;
        _sendButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            Log($"Versturen gestart: {rows.Count} antwoorden…");
            var mailRows = rows.Where(m => !m.IsChat).ToList();
            var chatRows = rows.Where(m => m.IsChat).ToList();
            var verstuurd = new List<MailBericht>();
            if (mailRows.Count > 0)
            {
                verstuurd.AddRange(await GmailClient.VerstuurAsync(_settings, mailRows, Log, _cts.Token));
            }
            foreach (var chat in chatRows)
            {
                try
                {
                    if (chat.WhatsAppChat.Length > 0)
                    {
                        await _whatsapp.VerstuurAsync(chat.WhatsAppChat, chat.Concept, _cts.Token);
                    }
                    else
                    {
                        await GoogleChatClient.VerstuurAsync(_chatSettings, chat.ChatSpace, chat.Concept, _cts.Token);
                    }
                    verstuurd.Add(chat);
                    Log($"Chatbericht verstuurd in \"{chat.Van}\"");
                }
                catch (Exception ex)
                {
                    Log($"Chatbericht in \"{chat.Van}\" mislukt: {ex.Message}");
                }
            }

            // Beantwoorde mails meteen archiveren (zoals in Gmail: uit de inbox, blijft in 'Alle
            // berichten'); chats hebben geen inbox en verdwijnen alleen uit de lijst.
            var gearchiveerd = false;
            var teArchiveren = verstuurd.Where(m => !m.IsChat).ToList();
            if (teArchiveren.Count > 0)
            {
                try
                {
                    await GmailClient.ArchiveerAsync(_settings, teArchiveren, _cts.Token);
                    gearchiveerd = true;
                }
                catch (Exception ex)
                {
                    Log($"Automatisch archiveren mislukt (antwoorden zijn wél verstuurd): {ex.Message}");
                }
            }

            // Verstuurde mails uit de lijst én de conceptcache halen.
            _mails.RemoveAll(verstuurd.Contains);
            foreach (var mail in verstuurd)
            {
                if (mail.MessageId.Length > 0)
                {
                    _cache.Remove(mail.MessageId);
                }
            }
            ConceptCache.Save(_cache);
            FillList();
            Log($"Klaar: {verstuurd.Count} verstuurd" +
                (gearchiveerd ? " en gearchiveerd" : "") +
                $"; {_mails.Count} mails blijven over.");
            Toast.Toon(this, _mails.Count == 0
                ? "Alles verstuurd — inbox leeg 🎉"
                : verstuurd.Count == 1 ? "1 antwoord verstuurd" : $"{verstuurd.Count} antwoorden verstuurd",
                Fluent.Send);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het versturen.
        }
        catch (Exception ex)
        {
            Log($"Versturen mislukt: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _sendButton.Bezig = false;
            _pulse.Actief = false;
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    // ---------- Taak maken ----------

    /// <summary>Maakt van de geselecteerde mail een taak in "Mijn taken", optioneel met archiveren.</summary>
    private async Task TaakVanMailAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not MailBericht mail)
        {
            return;
        }

        using var dialog = new MailTaakForm(mail);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var data = MijnTaakStore.Load();
        data.Taken.Add(new MijnTaak
        {
            Tekst = dialog.TaakTekst,
            Categorie = dialog.Categorie,
            Prioriteit = dialog.Prioriteit,
            Deadline = dialog.Deadline,
            Startdatum = dialog.Startdatum,
            StartUur = dialog.StartUur,
        });
        MijnTaakStore.Save(data);
        Log($"Taak gemaakt in Mijn taken: \"{dialog.TaakTekst}\" ({dialog.Categorie}).");
        Toast.Toon(this, "Taak toegevoegd aan Mijn taken", Fluent.Checkbox);

        if (dialog.Archiveren)
        {
            await ArchiveerRowsAsync(new List<MailBericht> { mail });
        }
    }

    // ---------- Reageren op een chat ----------

    /// <summary>De emoji's die als reactie in het submenu staan (👍 heeft een eigen regel).</summary>
    private static readonly string[] Reacties = { "❤️", "😀", "🎉", "🙏", "✅", "👀" };

    /// <summary>Een rij waarop je kunt reageren: een Google Chat met een berichtnaam.</summary>
    private static bool IsChatRij(MailBericht? m) =>
        m is { ChatSpace.Length: > 0 } && m.MessageId.StartsWith("chat:", StringComparison.Ordinal);

    private MailBericht? Geselecteerd() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MailBericht : null;

    /// <summary>
    /// Zet een emoji-reactie op het laatste bericht van de geselecteerde chat en handelt de rij
    /// af. Zo hoef je voor een "ok, gezien" geen antwoord te typen.
    /// </summary>
    private async Task ReageerOpChatAsync(string emoji = "👍")
    {
        if (Geselecteerd() is not { } chat || !IsChatRij(chat))
        {
            return;
        }
        try
        {
            await GoogleChatClient.ReageerAsync(
                _chatSettings, chat.MessageId[5..], _cts.Token, emoji);
            // Afgehandeld: de rij verdwijnt tot er een nieuw bericht in de chat komt.
            chat.Genegeerd = true;
            BewaarInCache(chat);
            _mails.Remove(chat);
            ConceptCache.Save(_cache);
            FillList();
            Log($"{emoji} gestuurd naar {chat.Van}.");
            Toast.Toon(this, $"{emoji} gestuurd naar {chat.Van}", Fluent.Check);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het versturen.
        }
        catch (Exception ex)
        {
            Log($"Reactie sturen mislukt: {ex.Message}");
            Toast.Toon(this, $"Reactie sturen mislukt: {ex.Message}", Fluent.Send);
        }
    }

    // ---------- Archiveren ----------

    private async Task ArchiveerSelectieAsync()
    {
        var rows = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MailBericht>().ToList();
        if (rows.Count > 0)
        {
            await ArchiveerRowsAsync(rows);
        }
    }

    private async Task ArchiveerRowsAsync(List<MailBericht> rows)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _fetchButton.Enabled = false;
        _pulse.Actief = true;
        try
        {
            // Chats staan niet in de Gmail-inbox; alleen echte mails via IMAP archiveren.
            var mailRows = rows.Where(m => !m.IsChat).ToList();
            if (mailRows.Count > 0)
            {
                await GmailClient.ArchiveerAsync(_settings, mailRows, _cts.Token);
            }
            _mails.RemoveAll(rows.Contains);
            foreach (var mail in rows)
            {
                if (mail.MessageId.Length == 0)
                {
                    continue;
                }
                if (mail.IsChat)
                {
                    // Gearchiveerde chat onthouden: pas weer tonen bij nieuwe berichten
                    // (die geven een nieuwe cachesleutel).
                    mail.Genegeerd = true;
                    BewaarInCache(mail);
                }
                else
                {
                    _cache.Remove(mail.MessageId);
                }
                _gearchiveerd.Add(mail.MessageId); // lopende conceptgeneratie niet meer cachen
            }
            ConceptCache.Save(_cache);
            FillList();
            Log(rows.Count == 1
                ? $"Gearchiveerd: {rows[0].Van} – \"{rows[0].Onderwerp}\" (blijft in Gmail onder 'Alle berichten')."
                : $"{rows.Count} mails gearchiveerd (blijven in Gmail onder 'Alle berichten').");
            Toast.Toon(this, _mails.Count == 0
                ? "Alles gearchiveerd — inbox leeg 🎉"
                : rows.Count == 1 ? "1 mail gearchiveerd" : $"{rows.Count} mails gearchiveerd",
                Fluent.Archive);
            // Archiveren van een WhatsApp-chat = ook echt als gelezen zetten in WhatsApp
            // (blauwe vinkjes), net zoals de cockpit dat doet.
            foreach (var chat in rows.Where(m => m.WhatsAppChat.Length > 0)
                .Select(m => m.WhatsAppChat).Distinct().ToList())
            {
                try
                {
                    await _whatsapp.MarkeerGelezenAsync(chat, _cts.Token);
                    Log($"WhatsApp: \"{chat}\" als gelezen gezet.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"WhatsApp: \"{chat}\" als gelezen zetten lukte niet: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het archiveren.
        }
        catch (Exception ex)
        {
            Log($"Archiveren mislukt: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _pulse.Actief = _genereren; // balk blijft lopen zolang concepten nog genereren
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    // ---------- Snoozen ----------

    private async Task SnoozeSelectieAsync()
    {
        if (_busy)
        {
            return;
        }
        var rows = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MailBericht>()
            // Zonder Message-ID kunnen we de mail later niet terugvinden; chats kunnen niet snoozen.
            .Where(m => !m.IsChat && m.MessageId.Length > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var voorstel = SnoozeStore.Voorstel();
        // Bij één mail leest Claude mee en stelt hij het inhoudelijk logische moment voor.
        using var dialog = new SnoozeForm(rows.Count, voorstel,
            slimVoorstel: rows.Count == 1
                ? ct => ClaudeSnooze.VoorstelAsync(rows[0], ct)
                : null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        SnoozeStore.RegistreerKeuze(voorstel, dialog.Gekozen); // hieruit leert het volgende voorstel

        _busy = true;
        _fetchButton.Enabled = false;
        _pulse.Actief = true;
        try
        {
            // Uit de inbox én onder het Gmail-label "Gesnoozed", zodat je het ook in Gmail ziet.
            await GmailClient.SnoozeArchiveerAsync(_settings, rows, _cts.Token);

            var snoozes = SnoozeStore.LoadSnoozes();
            foreach (var mail in rows)
            {
                snoozes.RemoveAll(s => s.MessageId == mail.MessageId);
                snoozes.Add(new SnoozeStore.SnoozeItem
                {
                    MessageId = mail.MessageId,
                    Van = mail.Van,
                    Onderwerp = mail.Onderwerp,
                    Tot = dialog.Gekozen,
                });
            }
            SnoozeStore.SaveSnoozes(snoozes);

            // Conceptcache bewust laten staan: als de mail terugkomt, staat het concept klaar.
            _mails.RemoveAll(rows.Contains);
            FillList();
            Log(rows.Count == 1
                ? $"Gesnoozed tot {dialog.Gekozen:dddd d MMMM HH:mm}: {rows[0].Van} – \"{rows[0].Onderwerp}\" (in Gmail onder het label 'Gesnoozed')."
                : $"{rows.Count} mails gesnoozed tot {dialog.Gekozen:dddd d MMMM HH:mm} (in Gmail onder het label 'Gesnoozed').");
            Toast.Toon(this, $"Gesnoozed tot {dialog.Gekozen:ddd d MMM HH:mm}", Fluent.Klok);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het snoozen.
        }
        catch (Exception ex)
        {
            Log($"Snoozen mislukt: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _pulse.Actief = _genereren; // balk blijft lopen zolang concepten nog genereren
            _fetchButton.Enabled = true;
            UpdateStatus();
        }
    }

    // ---------- Bijlagen opslaan ----------

    private async Task BijlagenOpslaanAsync()
    {
        if (_list.SelectedItems.Count == 0 ||
            _list.SelectedItems[0].Tag is not MailBericht mail ||
            (mail.Bijlagen.Count == 0 && mail.LinkBijlagen.Count == 0))
        {
            return;
        }

        using var dialog = new BijlagenForm(mail, VindGoogleDrive()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Log($"Bijlagen opslaan van {mail.Van} – \"{mail.Onderwerp}\"…");
        try
        {
            Directory.CreateDirectory(dialog.Doelmap);
            var paden = new List<string>();
            if (dialog.Selectie.Count > 0)
            {
                paden.AddRange(await GmailClient.DownloadBijlagenAsync(
                    _settings, mail, dialog.Doelmap, dialog.Selectie, _cts.Token));
            }
            foreach (var (url, naam) in dialog.LinkSelectie)
            {
                try
                {
                    paden.Add(await GmailClient.DownloadLinkAsync(url, dialog.Doelmap, naam, _cts.Token));
                }
                catch (Exception ex)
                {
                    Log($"Downloaden van link \"{naam}\" mislukt: {ex.Message}");
                }
            }
            Log(paden.Count == 0
                ? "Geen bijlagen opgeslagen."
                : "Opgeslagen:" + string.Concat(paden.Select(p => Environment.NewLine + "  " + p)));
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het downloaden.
        }
        catch (Exception ex)
        {
            Log($"Bijlagen opslaan mislukt: {ex.Message}");
        }
    }

    /// <summary>Zet gekozen bijlage(n) rechtstreeks in een Google Drive-map (leeg id = map kiezen).</summary>
    private async Task BijlagenNaarDriveAsync(string mapId, string mapNaam)
    {
        if (_list.SelectedItems.Count == 0 ||
            _list.SelectedItems[0].Tag is not MailBericht mail ||
            !BijlagenNaarDrive.HeeftBijlagen(mail))
        {
            return;
        }
        await BijlagenNaarDrive.UitvoerenAsync(this, _settings, mail, mapId, mapNaam, Log, _cts.Token);
    }

    /// <summary>Stuurt gekozen bijlage(n) van de geselecteerde mail door naar het Billit-inboxadres.</summary>
    private async Task BillitDoorsturenAsync()
    {
        if (_list.SelectedItems.Count == 0 ||
            _list.SelectedItems[0].Tag is not MailBericht mail ||
            (mail.Bijlagen.Count == 0 && mail.LinkBijlagen.Count == 0))
        {
            return;
        }
        var adres = _settings.BillitAdres.Trim();
        if (adres.Length == 0)
        {
            Log("Geen Billit-adres ingesteld — vul dat in via 'Instellingen…'.");
            return;
        }

        using var dialog = new BijlagenForm(mail, "", doorsturen: true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Log($"Doorsturen naar Billit ({adres})…");
        try
        {
            var aantal = await GmailClient.DoorsturenAsync(
                _settings, mail, adres, dialog.Selectie, dialog.LinkSelectie, _cts.Token);
            Log($"Doorgestuurd naar Billit: {aantal} bijlage{(aantal == 1 ? "" : "n")} " +
                $"van \"{mail.Onderwerp}\".");
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het doorsturen.
        }
        catch (Exception ex)
        {
            Log($"Doorsturen naar Billit mislukt: {ex.Message}");
        }
    }

    /// <summary>
    /// Zoekt de standaardmap voor bijlagen: de submap "administratie" in de Google
    /// Drive-desktopmap ("Mijn Drive"/"My Drive" op een schijfroot), anders Drive zelf.
    /// </summary>
    private static string? VindGoogleDrive()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            foreach (var naam in new[] { "Mijn Drive", "My Drive" })
            {
                var pad = Path.Combine(drive.RootDirectory.FullName, naam);
                if (Directory.Exists(pad))
                {
                    var administratie = Path.Combine(pad, "administratie");
                    return Directory.Exists(administratie) ? administratie : pad;
                }
            }
        }
        return null;
    }

    // ---------- Concept aanpassen met feedback ----------

    private async Task VerwerkFeedbackAsync()
    {
        if (_getoond is not { } mail)
        {
            return;
        }
        var feedback = _feedback.Text.Trim();
        if (feedback.Length == 0)
        {
            return;
        }

        BewaarConcept(); // huidige (eventueel bewerkte) tekst meenemen als vertrekpunt
        _feedback.Enabled = false;
        _feedbackButton.Enabled = false;
        _pulse.Actief = true;
        Log($"Concept aanpassen op feedback voor {mail.Van} – \"{mail.Onderwerp}\"…");
        try
        {
            var instructies = MailReplySettings.LoadInstructies();
            var nieuw = await ClaudeDrafter.ReviseAsync(
                mail, mail.Concept, feedback, instructies, _settings, _cts.Token);
            if (string.IsNullOrWhiteSpace(nieuw))
            {
                Log("Claude gaf geen herwerkt concept terug; het huidige concept blijft staan.");
                return;
            }

            mail.Concept = nieuw;
            mail.ConceptKlaar = true;
            if (mail.Reden.Length == 0)
            {
                mail.Reden = "aangepast op feedback";
            }
            BewaarInCache(mail);
            UpdateRow(mail, $"✔ {mail.Reden}", check: true);
            if (ReferenceEquals(_getoond, mail))
            {
                _concept.Text = nieuw.ReplaceLineEndings("\r\n");
            }
            _feedback.Clear();
            Log("Concept aangepast.");
            Toast.Toon(this, "Concept aangepast", Fluent.Edit);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het herwerken.
        }
        catch (Exception ex)
        {
            Log($"Concept aanpassen mislukt: {ex.Message}");
        }
        finally
        {
            _feedback.Enabled = true;
            _feedbackButton.Enabled = true;
            _pulse.Actief = _genereren;
        }
    }

    // ---------- Lijst en detailweergave ----------

    private void FillList()
    {
        if (IsDisposed)
        {
            return;
        }
        _getoond = null;
        ToonWeergave(LegeWeergave);
        _concept.Clear();
        ToonReplyAll(null);

        var filter = _zoek.Text.Trim();
        _list.LegeTekst = filter.Length > 0
            ? "Geen mails gevonden voor je zoekopdracht."
            : "Nog geen mails — klik op 'Mails ophalen'.";
        bool Zichtbaar(MailBericht m) => filter.Length == 0 ||
            m.Van.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            m.VanAdres.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            m.Onderwerp.Contains(filter, StringComparison.OrdinalIgnoreCase);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var mail in _mails.Where(Zichtbaar))
        {
            var icoon = mail.WhatsAppChat.Length > 0 ? "🟢 " : mail.ChatSpace.Length > 0 ? "💬 " : "✉️ ";
            var item = new ListViewItem(icoon + mail.Van)
            {
                Tag = mail, UseItemStyleForSubItems = false,
            };
            var onderwerpSub = item.SubItems.Add(
                (mail.Bijlagen.Count > 0 || mail.LinkBijlagen.Count > 0 ? "📎 " : "") + mail.Onderwerp);
            if (mail.Urgent)
            {
                // Rood: vandaag best beantwoorden (oordeel van Claude).
                item.ForeColor = Theme.Danger;
                onderwerpSub.ForeColor = Theme.Danger;
            }
            item.SubItems.Add(mail.Datum.ToLocalTime().ToString("dd-MM HH:mm"));
            var concept = item.SubItems.Add(mail.ConceptKlaar ? $"✔ {mail.Reden}" : mail.Reden);
            concept.ForeColor = mail.ConceptKlaar ? Theme.Success : Theme.Warn;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateStatus();
    }

    private void UpdateRow(MailBericht mail, string conceptTekst, bool check = false, Color? kleur = null)
    {
        if (IsDisposed)
        {
            return;
        }
        var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, mail));
        if (item is null)
        {
            return;
        }
        item.SubItems[3].Text = conceptTekst;
        item.SubItems[3].ForeColor = kleur ?? (check ? Theme.Success : Theme.Warn);
    }

    private void ToonSelectie()
    {
        BewaarConcept();
        _getoond = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MailBericht : null;
        if (_getoond is null)
        {
            ToonWeergave(LegeWeergave);
            _concept.Clear();
            ToonReplyAll(null);
            UpdateStatus();
            return;
        }
        _origineelGroup.Text = _getoond.WhatsAppChat.Length > 0 ? "Ontvangen WhatsApp-gesprek"
            : _getoond.ChatSpace.Length > 0 ? "Ontvangen Google Chat-gesprek"
            : "Ontvangen mail";
        ToonWeergave(BouwWeergave(_getoond));
        _concept.Text = _getoond.Concept.ReplaceLineEndings("\r\n");
        ToonReplyAll(_getoond);
        UpdateStatus();
    }

    private void BewaarKolomBreedtes()
    {
        _kolomSaveTimer.Stop();
        _settings.KolomBreedtes = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToList();
        _settings.Save();
    }

    private double[] BerekenKolomVerhoudingen()
    {
        var totaal = Math.Max(1, _list.Columns.Cast<ColumnHeader>().Sum(c => c.Width));
        return _list.Columns.Cast<ColumnHeader>().Select(c => (double)c.Width / totaal).ToArray();
    }

    /// <summary>
    /// Laat de kolommen de volledige lijstbreedte vullen, met behoud van de onderlinge
    /// verhoudingen. Handmatig slepen past de verhoudingen aan (en wordt bewaard).
    /// </summary>
    private void SchaalKolommen()
    {
        var beschikbaar = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (beschikbaar <= 100 || _kolomVerhoudingen.Length != _list.Columns.Count)
        {
            return;
        }
        _kolommenSchalen = true;
        for (var i = 0; i < _list.Columns.Count; i++)
        {
            _list.Columns[i].Width = Math.Max(60, (int)(beschikbaar * _kolomVerhoudingen[i]));
        }
        _kolommenSchalen = false;
    }

    /// <summary>Zet de "Allen beantwoorden"-checkbox goed voor de getoonde mail.</summary>
    private void ToonReplyAll(MailBericht? mail)
    {
        _replyAllUpdating = true;
        var overige = mail?.OverigeOntvangers.Count ?? 0;
        _replyAll.Enabled = overige > 0;
        _replyAll.Checked = mail?.AlleBeantwoorden == true && overige > 0;
        _replyAll.Text = overige > 0
            ? $"Allen beantwoorden (+{overige} cc)"
            : "Allen beantwoorden";
        _tip.SetToolTip(_replyAll, overige > 0
            ? "Cc: " + string.Join(", ", mail!.OverigeOntvangers)
            : "Deze mail had geen andere ontvangers.");
        _replyAllUpdating = false;
    }

    private void ToonWeergave(string html)
    {
        if (_origineel.CoreWebView2 is { } core)
        {
            core.NavigateToString(html);
        }
        else
        {
            _wachtendeWeergave = html;
        }
    }

    /// <summary>
    /// Geen eigen const meer maar een eigenschap: de kleuren komen uit het actieve
    /// kleurenschema, en die staan pas bij het opvragen vast.
    /// </summary>
    internal static string LegeWeergave =>
        $"""
        <!doctype html><html><head><meta charset="utf-8"></head>
        <body style="font-family:'Segoe UI Variable Text','Segoe UI',Arial,sans-serif;font-size:13px;
                     color:{Theme.Hex(Theme.Muted)};background:{Theme.Hex(Theme.Bg)};margin:0;display:flex;align-items:center;
                     justify-content:center;height:100vh">
        <div style="text-align:center">
          <div style="font-size:34px;margin-bottom:10px;opacity:.6">✉</div>
          Selecteer een mail in de lijst om die hier te bekijken.
        </div>
        </body></html>
        """;

    /// <summary>Rendert de mail zoals in Gmail: nette kopregel + de originele HTML-opmaak.</summary>
    private const string ChipStijl =
        "display:inline-block;background:#e8eaed;border:1px solid #dadce0;border-radius:12px;" +
        "padding:2px 10px;margin:2px 6px 0 0;font-size:12px;color:#3c4043;" +
        "text-decoration:none;cursor:pointer";

    /// <summary>
    /// HTML-encodeert platte tekst en maakt http(s)-URL's klikbaar (leestekens aan het eind
    /// van een zin tellen niet mee als deel van de link). De webviews sturen kliks op links
    /// al naar de externe browser.
    /// </summary>
    internal static string EncodeMetLinks(string tekst)
    {
        var sb = new System.Text.StringBuilder();
        var laatst = 0;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(tekst, @"https?://[^\s<>""]+"))
        {
            sb.Append(WebUtility.HtmlEncode(tekst[laatst..m.Index]));
            var url = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?', '\'');
            sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(url))
              .Append("\" style=\"color:#1a73e8;word-break:break-all\">")
              .Append(WebUtility.HtmlEncode(url)).Append("</a>");
            sb.Append(WebUtility.HtmlEncode(m.Value[url.Length..]));
            laatst = m.Index + m.Value.Length;
        }
        sb.Append(WebUtility.HtmlEncode(tekst[laatst..]));
        return sb.ToString();
    }

    internal static string BouwWeergave(MailBericht mail, bool terugNaarCcOverzicht = false)
    {
        // HTML zonder leesbare inhoud (bv. alleen een <style>-blok, zoals wanneer een
        // scraper de echte body kwijtraakte) zou een lege kaart opleveren terwijl de
        // platte tekst er wél is: dan die tonen.
        var body = string.IsNullOrWhiteSpace(mail.Html) || HtmlZonderInhoud(mail.Html)
            ? "<pre style=\"white-space:pre-wrap;font-family:inherit;font-size:13px;margin:0\">" +
              EncodeMetLinks(mail.Tekst) + "</pre>"
            : mail.Html;

        // Chatweergaves (bubbels met een .wm-chat-container) worden schermvullend: de kop
        // blijft vast, de bubbels vullen de rest van het paneel en beginnen onderaan bij het
        // laatste bericht — geen scrollen nodig om te lezen wat er net gezegd is.
        var chatCss = mail.IsChat && body.Contains("wm-chat")
            ? """
              <style>
                html, body { height: 100%; box-sizing: border-box; }
                .wm-kaart { display: flex; flex-direction: column; height: 100%; }
                .wm-inhoud { flex: 1; min-height: 0; display: flex; flex-direction: column; }
                .wm-chat { flex: 1; min-height: 0; display: flex; flex-direction: column; }
                .wm-chat-scroll { max-height: none !important; flex: 1; min-height: 0; }
              </style>
              """
            : "";

        // Donkere pagina met de mail als witte afgeronde kaart (mails zijn op wit ontworpen).
        var html =
            $"""
            <!doctype html><html><head><meta charset="utf-8">{chatCss}</head>
            <body style="margin:0;background:{Theme.Hex(Theme.Bg)};font-family:'Segoe UI Variable Text','Segoe UI',Arial,sans-serif;padding:12px">
            {(terugNaarCcOverzicht
                ? "<a href=\"wm-ccterug:\" style=\"display:inline-block;margin:0 0 10px;padding:4px 10px;" +
                  $"background:{Theme.Hex(Theme.Card)};border-radius:14px;color:{Theme.Hex(Theme.Accent)};" +
                  "text-decoration:none;font-size:13px\">" +
                  "← Terug naar het CC-overzicht</a>"
                : "")}
            <div class="wm-kaart" style="background:#ffffff;border-radius:12px;overflow:hidden;
                 box-shadow:0 6px 28px rgba(0,0,0,{(Theme.Palet.Donker ? ".5" : ".14")})">
            <div style="padding:12px 16px;background:#f6f8fc;border-bottom:1px solid #e0e0e0;font-size:13px">
              <div style="font-size:16px;font-weight:600;color:#1f1f1f;margin-bottom:6px">{WebUtility.HtmlEncode(mail.Onderwerp)}</div>
              <div><b>{WebUtility.HtmlEncode(mail.Van)}</b> <span style="color:#5f6368">&lt;{WebUtility.HtmlEncode(mail.VanAdres)}&gt;</span></div>
            {(mail.Aan.Count > 1
                ? $"<div style=\"color:#5f6368\">Aan: {WebUtility.HtmlEncode(string.Join("; ", mail.Aan))}</div>"
                : "")}
            {(mail.Cc.Count > 0
                ? $"<div style=\"color:#5f6368\">Cc: {WebUtility.HtmlEncode(string.Join("; ", mail.Cc))}</div>"
                : "")}
              <div style="color:#5f6368">{mail.Datum.ToLocalTime():dddd d MMMM yyyy 'om' HH:mm}</div>
            {(mail.Bijlagen.Count + mail.LinkBijlagen.Count > 0
                ? "<div style=\"margin-top:7px\">" + string.Join("", mail.Bijlagen
                    .Select((n, i) => $"<a href=\"wm-bijlage:{i}\" style=\"{ChipStijl}\">📎 " +
                        WebUtility.HtmlEncode(n) + "</a>")
                    .Concat(mail.LinkBijlagen.Select(l =>
                        $"<a href=\"{WebUtility.HtmlEncode(l.Url)}\" style=\"{ChipStijl}\">📎 " +
                        WebUtility.HtmlEncode(l.Naam) + "</a>"))) + "</div>"
                : "")}
            </div>
            <div class="wm-inhoud" style="padding:16px">{body}{Vertaalblok(mail)}</div>
            </div>
            </body></html>
            """;

        // NavigateToString heeft een limiet van ~2 MB; val bij extreem grote mails terug op (ingekorte) platte tekst.
        return html.Length < 1_500_000 ? html : BouwWeergave(new MailBericht
        {
            Van = mail.Van, VanAdres = mail.VanAdres, Onderwerp = mail.Onderwerp,
            Datum = mail.Datum,
            Tekst = mail.Tekst.Length > 100_000 ? mail.Tekst[..100_000] + "\n[… ingekort …]" : mail.Tekst,
        });
    }

    /// <summary>
    /// True als de HTML na het strippen van style/script en tags geen tekst overhoudt —
    /// en ook geen afbeelding bevat (een mail die alléén uit een beeld bestaat is prima).
    /// </summary>
    private static bool HtmlZonderInhoud(string html)
    {
        var zonderStyle = System.Text.RegularExpressions.Regex.Replace(html,
            @"<(style|script)[^>]*>.*?</\1\s*>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (zonderStyle.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var kaal = System.Text.RegularExpressions.Regex.Replace(zonderStyle, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(kaal).Trim().Length == 0;
    }

    /// <summary>
    /// Rendert onder de mail een Nederlands vertaalblok als de mail Frans/Engels was en er
    /// een vertaling beschikbaar is. Bij chats (schermvullende bubbels) niet tonen.
    /// </summary>
    private static string Vertaalblok(MailBericht mail)
    {
        if (mail.VertaalVerborgen || string.IsNullOrWhiteSpace(mail.Vertaling))
        {
            return "";
        }
        return
            "<div style=\"margin-top:16px;padding:12px 14px;background:#eef4ff;border:1px solid #d3e0f7;" +
            "border-radius:10px;font-size:13px;color:#1f1f1f\">" +
            "<div style=\"font-weight:600;color:#1a56c4;margin-bottom:6px\">🌐 Vertaling (Nederlands)</div>" +
            "<div style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(mail.Vertaling) + "</div></div>";
    }

    /// <summary>Schrijft de (mogelijk bewerkte) concepttekst terug naar de getoonde mail en de cache.</summary>
    private void BewaarConcept()
    {
        if (_getoond is { } mail && mail.Concept != _concept.Text)
        {
            mail.Concept = _concept.Text;
            BewaarInCache(mail);
        }
    }

    private void BewaarInCache(MailBericht mail)
    {
        if (mail.MessageId.Length == 0)
        {
            return;
        }
        _cache[mail.MessageId] = new ConceptCache.Entry
        {
            ConceptKlaar = mail.ConceptKlaar,
            Concept = mail.Concept,
            Reden = mail.Reden,
            AlleBeantwoorden = mail.AlleBeantwoorden,
            Genegeerd = mail.Genegeerd,
            Urgent = mail.Urgent,
            Datum = mail.Datum,
        };
        ConceptCache.Save(_cache);
    }

    /// <summary>Rode rij voor mails die het best vandaag nog beantwoord worden.</summary>
    private void KleurUrgent(MailBericht mail)
    {
        if (IsDisposed || !mail.Urgent)
        {
            return;
        }
        var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, mail));
        if (item is not null)
        {
            item.ForeColor = Theme.Danger;
            item.SubItems[1].ForeColor = Theme.Danger;
        }
    }

    private void UpdateStatus()
    {
        if (IsDisposed)
        {
            return;
        }
        var geselecteerd = _list.SelectedItems.Count;
        _status.Text = _mails.Count == 0
            ? "Nog geen mails opgehaald."
            : $"{geselecteerd} van {_mails.Count} geselecteerd";
        var metConcept = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MailBericht>()
            .Any(m => !string.IsNullOrWhiteSpace(m.Concept));
        _sendButton.Enabled = metConcept && !_busy && !_genereren;
        _archiveButton.Enabled = geselecteerd > 0 && !_busy;
        _snoozeButton.Enabled = geselecteerd > 0 && !_busy;
    }

    private bool EditSettings()
    {
        using var form = new MailSettingsForm();
        var ok = form.ShowDialog(this) == DialogResult.OK;
        if (ok)
        {
            _settings = MailReplySettings.Load();
            Log("Instellingen opgeslagen.");
        }
        return ok;
    }

    private void Log(string message)
    {
        // Async-vervolgstappen (Claude, IMAP/SMTP) kunnen na het sluiten van het venster
        // nog loggen; dan is er niets meer om in te schrijven.
        if (IsDisposed || _log.IsDisposed)
        {
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + line);
    }
}
