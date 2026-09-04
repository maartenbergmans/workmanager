using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Centrale cockpit: bovenaan de binnengekomen berichten (mail ✉️, Google Chat 💬,
/// WhatsApp 🟢) met een detailpaneel zoals het mailscherm (weergave + Claude-concept +
/// versturen/archiveren), onderaan de open taken (Mijn taken + Asana-taken mét deadline)
/// en de meetings van vandaag/morgen, plus knoppen voor de klantcontexten, teamtaken en
/// (op woensdag of zolang de taak open staat) de factuurgoedkeuring. Ververst elke vijf
/// minuten; bij het openen wordt eerst de laatst bekende lijst uit de cache getoond.
/// </summary>
public class CockpitForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly Func<IReadOnlyCollection<string>> _actieveContexts;
    private readonly Action<string> _toggleContext;
    private readonly Action _openMail;
    private readonly Action _openTeamTasks;
    private readonly Action _openInvoices;
    private readonly Action _openTopdesk;
    private readonly Action _openDevOps;

    /// <summary>
    /// Opent een tray-venster op naam (dagstart, mijntaken, vip, …) — zo zijn álle
    /// tray-functies ook vanuit de cockpit bereikbaar zonder per venster een delegate.
    /// </summary>
    private readonly Action<string> _openVenster;

    private readonly ModernListView _berichten;
    private readonly ModernListView _taken;
    private List<TaakRij> _taakRijen = new();
    private int? _takenHorizon = 2; // alleen taken met deadline binnen dit aantal dagen (null = alles)
    private bool _sorteerOpPlan; // takenlijst in de volgorde van de dagplanning i.p.v. deadline
    private bool _toonAfgevinkte; // recent afgevinkte taken onderaan tonen (uitvinken = terugzetten)
    private bool _toonGepland; // gesnoozde en nog-niet-gestarte taken onderaan tonen
    private readonly ModernListView _meetings;
    private readonly ModernGroupBox _meetingsGroup;
    private readonly Label _weerLabel; // weersvoorspelling onderaan de kalender voor de getoonde dag
    private int _meetingsOffset; // 0 = vandaag, 1 = morgen, …
    private bool _toonVoorbije; // voorbije afspraken van vandaag tonen (standaard verborgen)

    // Agendacache: één venster vooruit ophalen zodat datum-bladeren (◀/▶) niets herlaadt;
    // de 5-min-poll en "Nu verversen" halen wél altijd vers op.
    private List<AgendaClient.AgendaItem> _agendaEigen = new();
    private List<AgendaClient.AgendaItem> _agendaHilke = new();
    private DateTimeOffset _agendaGeladen = DateTimeOffset.MinValue;
    private DateOnly _agendaTot = DateOnly.MinValue;

    /// <summary>
    /// De CED/Outlook-agenda komt per dag binnen en is de trage schakel bij het bladeren.
    /// Daarom houden we ze per dag bij mét het moment van ophalen. Er zit bewust de Task in
    /// en niet het resultaat: wie een dag opvraagt die al onderweg is, wacht op datzelfde
    /// ophalen in plaats van er een tweede naast te starten.
    /// </summary>
    private readonly Dictionary<DateOnly, (DateTimeOffset Gestart, Task<List<AgendaClient.AgendaItem>> Taak)>
        _cedCache = new();

    private const int AgendaVensterDagen = 14;

    /// <summary>Hoeveel dagen vooruit alvast op de achtergrond opgehaald worden bij het bladeren.</summary>
    private const int VooruitLaden = 2;
    private readonly Label _status;
    private readonly Label _sessieStatus = new() { AutoSize = true }; // 🟢/🟠/⚪ per bron
    private OutlookDiagnoseForm? _owaDiagForm;
    private readonly ModernButton _verversButton;
    private readonly ModernButton _facturenButton;
    /// <summary>Alleen in de werkbalk als de dagelijkse check een nieuwe Claude-versie vond.</summary>
    private readonly ModernButton _claudeUpdateKnop;
    /// <summary>Alleen in de werkbalk zolang het TopDesk-signaal aan staat.</summary>
    private readonly ModernButton _topdeskKnop;
    /// <summary>Alleen in de werkbalk zolang het DevOps-signaal aan staat.</summary>
    private readonly ModernButton _devopsKnop;
    /// <summary>Alleen in de werkbalk zolang het SD Worx-verlofsignaal aan staat.</summary>
    private readonly ModernButton _verlofKnop;
    /// <summary>Rode alarmknop, alleen zichtbaar zolang de Docker-engine niet draait.</summary>
    private readonly ModernButton _dockerKnop;
    /// <summary>Per klant een eigen projectknop; op een smal venster vervangt "Projecten ▾" ze.</summary>
    private readonly List<(ModernButton Knop, string Label, List<string> Mappen)> _projectKnoppen = new();
    private ModernButton? _projectenHoofdknop;
    private bool _facturenGeklikt; // knop verdwijnt na de eerste klik (tot de app herstart)
    private readonly List<ModernButton> _contextKnoppen = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2 * 60 * 1000 };

    // Git-status per projectmap: het Projecten-menu toont de dagcache (1× per dag automatisch
    // bijgewerkt via de poll; "Git controleren" onder ▾ ververst actief).
    private readonly List<(ToolStripMenuItem Item, string Map, string Naam)> _gitMenuItems = new();
    private readonly GitStatusCache.Data _gitCache = GitStatusCache.Load();
    private bool _gitControleBezig;
    private readonly CancellationTokenSource _cts = new();
    private readonly WebView2 _detail = new() { Dock = DockStyle.Fill };
    /// <summary>Antwoordblok onder de weergave; alleen zichtbaar als er een bericht getoond wordt.</summary>
    private readonly Panel _conceptPanel;
    private readonly TextBox _detailConcept;
    private readonly TextBox _detailFeedback;
    private readonly ModernButton _feedbackButton;
    private readonly ModernButton _claudeButton;
    private readonly ModernButton _verstuurButton;
    private readonly ModernButton _uitschrijfButton;
    private readonly ModernButton _openButton;
    private readonly ModernButton _outlookLeesButton;
    private ModernButton _vertaalButton = null!;
    private readonly ModernButton _teamsKoppelButton;
    private readonly ModernButton _outlookKoppelButton;
    private readonly ModernButton _waKoppelButton;
    private MailBericht? _getoond;
    // Staat de detailweergave los van de berichtenlijst (taak-mail, chat-transcript of
    // meetingdetail)? Dan mag een berichtenverversing het paneel niet leegmaken.
    private bool _detailLosVanLijst;
    private bool _gevierd; // confetti maar één keer per lege takenlijst
    private bool _takenLaden; // ItemChecked negeren terwijl de lijst gevuld wordt
    private bool _negeerTaakCheck; // dubbelklik op een taak = bewerken, niet toggelen

    // Reistijd per meeting met een echt adres (sleutel = MeetingSleutel): weergavetekst voor
    // het detailpaneel + de rijtijd zelf voor de vertrekwaarschuwing.
    private readonly Dictionary<string, (string Tekst, TimeSpan Duur)> _reis = new();
    private readonly HashSet<string> _reisBezig = new();
    private readonly HashSet<string> _vertrekGemeld = new(); // één waarschuwing per afspraak

    // Details van O365/CED-afspraken (genodigden, omschrijving), per afspraak uit de
    // webagenda geplukt en daarna gecachet — ook op schijf, zodat een herstart ze niet kwijt
    // is. Mislukkingen krijgen alleen een cooldown, zodat het later gewoon opnieuw probeert.
    private readonly Dictionary<string, string> _o365Details = new();
    private readonly Dictionary<string, DateTimeOffset> _o365Mislukt = new();
    private readonly HashSet<string> _o365Bezig = new();
    private bool _o365PrefetchBezig;

    // v2: sinds de details ook de Teams-deelnamelink bevatten; de oude cache (zonder link)
    // wordt genegeerd zodat elke afspraak één keer opnieuw opgehaald wordt.
    private static readonly string O365DetailsFile = Path.Combine(DataDir, "o365-details-v2.json");

    private void LaadO365DetailsCache()
    {
        try
        {
            if (File.Exists(O365DetailsFile) &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(O365DetailsFile)) is { } cache)
            {
                foreach (var (sleutel, details) in cache)
                {
                    _o365Details[sleutel] = details;
                }
            }
        }
        catch
        {
            // Cache is best effort.
        }
    }

    private void BewaarO365DetailsCache()
    {
        try
        {
            // Verlopen afspraken opruimen: het startmoment zit achteraan in de sleutel.
            var grens = DateTimeOffset.Now.AddDays(-1);
            var houdbaar = _o365Details.Where(kv =>
                    kv.Key.LastIndexOf('|') is var p and >= 0 &&
                    DateTimeOffset.TryParse(kv.Key[(p + 1)..], out var start) && start >= grens)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(O365DetailsFile,
                System.Text.Json.JsonSerializer.Serialize(houdbaar));
        }
        catch
        {
            // Cache is best effort.
        }
    }

    /// <summary>
    /// Haalt op de achtergrond alvast de details op van álle CED-afspraken in de lijst, één
    /// voor één (de Outlook-sessie kan er maar één tegelijk aan). Zo staat alles klaar tegen
    /// dat je een afspraak aanklikt, in plaats van 15 seconden wachten per keer.
    /// </summary>
    private async Task PrefetchO365DetailsAsync()
    {
        if (_o365PrefetchBezig)
        {
            return;
        }
        _o365PrefetchBezig = true;
        try
        {
            foreach (var lvi in _meetings.Items.Cast<ListViewItem>().ToList())
            {
                if (IsDisposed || _cts.IsCancellationRequested)
                {
                    return;
                }
                if (lvi.Name != "outlook" || lvi.Tag is not AgendaClient.AgendaItem m)
                {
                    continue;
                }
                var sleutel = MeetingSleutel(m);
                if (_o365Details.ContainsKey(sleutel) ||
                    (_o365Mislukt.TryGetValue(sleutel, out var w) &&
                     DateTimeOffset.Now - w < TimeSpan.FromMinutes(10)) ||
                    !_o365Bezig.Add(sleutel))
                {
                    continue;
                }
                await HaalO365DetailsAsync(m, sleutel);
            }
        }
        finally
        {
            _o365PrefetchBezig = false;
        }
    }
    private string? _feedbackVoor; // MessageId waar de getypte feedback bij hoort
    private List<MailBericht> _laatsteBerichten = new();

    /// <summary>Genegeerde Outlook-mails die tóch nog in het postvak staan: teller per poll.</summary>
    private readonly Dictionary<string, int> _genegeerdMaarAanwezig = new();
    /// <summary>Opeenvolgende lege Outlook-scrapes: bevestigt "echt leeg" als de DOM-heuristiek faalt.</summary>
    private int _outlookLeegOpeenvolgend;
    private List<string>? _laatsteFouten;
    private int _sortKolom = 2; // standaard op "Ontvangen"
    private bool _sortOplopend = true; // chronologisch, oudste bovenaan
    private readonly ComboBox _bronFilter;
    private readonly TextBox _zoekFilter;
    private readonly ModernGroupBox _berichtenGroup;
    private readonly ModernGroupBox _takenGroup;
    private string? _wachtendeWeergave;
    private bool _bezig;
    private ModernButton? _cedDagKnop;      // alleen zichtbaar als de getoonde dag een CED-dag is
    private Label? _volgendeMeetingLabel;   // balkje "over X min: …" boven de meetinglijst
    private ModernButton? _deelnemenKnop;   // de bijbehorende join-knop
    private Panel? _volgendeMeetingBalk;

    public CockpitForm(
        Func<IReadOnlyCollection<string>> actieveContexts,
        Action<string> toggleContext,
        Action openMail,
        Action openTeamTasks,
        Action openInvoices,
        Action openTopdesk,
        Action openDevOps,
        Action<string> openVenster)
    {
        _actieveContexts = actieveContexts;
        _toggleContext = toggleContext;
        _openMail = openMail;
        _openTeamTasks = openTeamTasks;
        _openInvoices = openInvoices;
        _openTopdesk = openTopdesk;
        _openDevOps = openDevOps;
        _openVenster = openVenster;

        WerkVensterTitelBij();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1400, 900); // terugval voor wie uit maximalisatie klikt
        WindowState = FormWindowState.Maximized;
        // Venster-brede sneltoetsen: F5 = verversen, Ctrl+F = zoeken, Ctrl+N = kladblok.
        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                await VerversAsync();
            }
            else if (e.Control && e.KeyCode == Keys.F)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _zoekFilter.Focus();
            }
            else if (e.Control && e.KeyCode == Keys.N)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ScratchpadForm.Toon(this);
            }
            else
            {
                LuisterNaarKonami(e.KeyCode);
            }
        };

        // Werkbalk: contextknoppen + verversen
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        // De klantcontexten (CED, Aqurat, RadiologyPartners) staan voorlopig niet in de cockpit —
        // aan- en uitzetten gebeurt via het tray-menu. Op true zetten brengt de knoppen terug.
        const bool toonContextKnoppen = false;
#pragma warning disable CS0162 // onbereikbare code: bewuste schakelaar hierboven
        if (toonContextKnoppen)
        {
            foreach (var (naam, kleur) in TrayAppContext.KlantContexten)
            {
                var knop = new ModernButton { Text = naam, Tag = naam };
                knop.KrimpNaarInhoud(); // niet breder dan nodig
                knop.Click += (_, _) =>
                {
                    _toggleContext(naam);
                    UpdateContextKnoppen();
                };
                _contextKnoppen.Add(knop);
                toolbar.Controls.Add(knop);
                _ = kleur; // kleurstip zit al in het tray-menu; hier volstaat de tekststatus
            }
        }
#pragma warning restore CS0162

        // Dev-launchers per klant: een compacte dropdown die een Claude-sessie in de
        // projectmap start, PhpStorm opent en (waar relevant) Visual Studio. WSL-mappen
        // openen native in WSL; Windows-mappen via PowerShell/Visual Studio.
        const string wsl = @"\\wsl.localhost\Ubuntu\home\maarten\projecten\";
        var dg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DataGripProjects");
        // Per actie ook (optioneel) de Claude-projectmap: die krijgt een status-lampje en een
        // "sluiten"-item zodat je een draaiende sessie ziet en kunt afsluiten.
        // Alle klanten zitten samen achter één "Projecten ▾"-knop (submenu per klant) —
        // vijf losse dropdowns maakten de werkbalk onleesbaar vol.
        var projectenMenu = new ContextMenuStrip();
        Theme.Style(projectenMenu);
        // Ruimte voor de klantlogo's: woordmerken zijn breder dan hoog.
        projectenMenu.ImageScalingSize = new Size(28, 18);
        var projectKlanten = new List<(ToolStripMenuItem Item, List<string> Mappen)>();
        foreach (var (label, acties) in new (string Label, (string Item, Action Doe, string? Claude)[] Acties)[]
        {
            ("Aqurat ▾", new (string, Action, string?)[]
            {
                ("Claude — aqurat", () => ClientLauncher.StartClaude(wsl + "aqurat"), wsl + "aqurat"),
                ("PhpStorm — aqurat", () => ClientLauncher.StartPhpStorm(wsl + "aqurat"), null),
                // De draaiende dev-omgeving: eerst de app opstarten (start.sh = npm start in
                // webapp/), dan de app zelf en de mailvanger ernaast, in Firefox.
                ("App starten — start.sh", () => ClientLauncher.StartWslScript(wsl + "aqurat", "start.sh"), null),
                ("App — localhost:4200", () => ClientLauncher.StartFirefox("http://localhost:4200/app/"), null),
                ("Mailpit — localhost:8025", () => ClientLauncher.StartFirefox("http://localhost:8025/"), null),
                ("DataGrip — Aqurat", () => ClientLauncher.StartDataGrip(Path.Combine(dg, "Aqurat")), null),
                ("Productie-DB → localhost…", () => new ProdDbKopieForm(ProdDbKopie.Aqurat).Show(this), null),
                ("Deploytool — aqurat (default)", () => ClientLauncher.StartDeploytool(wsl + "aqurat", "default"), null),
            }),
            ("RadiologyP. ▾", new (string, Action, string?)[]
            {
                ("Claude — bloom-datawarehouse", () => ClientLauncher.StartClaude(wsl + "bloom-datawarehouse"), wsl + "bloom-datawarehouse"),
                ("Claude — BloomDataUploader", () => ClientLauncher.StartClaude(@"C:\Data\Projecten\BloomDataUploader"), @"C:\Data\Projecten\BloomDataUploader"),
                ("PhpStorm — bloom-datawarehouse", () => ClientLauncher.StartPhpStorm(wsl + "bloom-datawarehouse"), null),
                ("Visual Studio — BloomDataUploader", () => ClientLauncher.StartVisualStudio(@"C:\Data\Projecten\BloomDataUploader"), null),
                ("Datastatus (browser)", () => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        "https://datawarehouse.bloom-caregroup.com/datastatus.php") { UseShellExecute = true }), null),
                ("DataGrip — RadiologypartnersEurope", () => ClientLauncher.StartDataGrip(Path.Combine(dg, "RadiologypartnersEurope")), null),
                ("Productie-DB → localhost…", () => new ProdDbKopieForm(ProdDbKopie.RadiologyPartners).Show(this), null),
                ("Deploytool — bloom-datawarehouse (default)", () => ClientLauncher.StartDeploytool(wsl + "bloom-datawarehouse", "default"), null),
            }),
            ("Vriesveemlog. ▾", new (string, Action, string?)[]
            {
                ("Claude — movaware-backend", () => ClientLauncher.StartClaude(wsl + "movaware-backend"), wsl + "movaware-backend"),
                ("Claude — movaware-frontend", () => ClientLauncher.StartClaude(wsl + "movaware-frontend"), wsl + "movaware-frontend"),
                ("PhpStorm — movaware-backend", () => ClientLauncher.StartPhpStorm(wsl + "movaware-backend"), null),
                ("PhpStorm — movaware-frontend", () => ClientLauncher.StartPhpStorm(wsl + "movaware-frontend"), null),
                ("DataGrip — Movaware", () => ClientLauncher.StartDataGrip(Path.Combine(dg, "Movaware")), null),
                ("Productie-DB → localhost…", () => new ProdDbKopieForm(ProdDbKopie.Movaware).Show(this), null),
                ("Deploytool — movaware-backend (default)", () => ClientLauncher.StartDeploytool(wsl + "movaware-backend", "default"), null),
            }),
            ("Vriesveem ▾", new (string, Action, string?)[]
            {
                ("Claude — cellaware-backend", () => ClientLauncher.StartClaude(wsl + "cellaware-backend"), wsl + "cellaware-backend"),
                ("Claude — cellaware-frontend", () => ClientLauncher.StartClaude(wsl + "cellaware-frontend"), wsl + "cellaware-frontend"),
                ("PhpStorm — cellaware-backend", () => ClientLauncher.StartPhpStorm(wsl + "cellaware-backend"), null),
                ("PhpStorm — cellaware-frontend", () => ClientLauncher.StartPhpStorm(wsl + "cellaware-frontend"), null),
                ("DataGrip — Cellaware", () => ClientLauncher.StartDataGrip(Path.Combine(dg, "Cellaware")), null),
                ("Productie-DB → localhost (nemijtek)…", () => new ProdDbKopieForm(ProdDbKopie.CellawareNemijtek).Show(this), null),
                ("Productie-DB → localhost (vriesveem)…", () => new ProdDbKopieForm(ProdDbKopie.CellawareVriesveem).Show(this), null),
                ("Deploytool — cellaware-backend (nemijtek)", () => ClientLauncher.StartDeploytool(wsl + "cellaware-backend", "nemijtek"), null),
                ("Deploytool — cellaware-backend (vriesveem)", () => ClientLauncher.StartDeploytool(wsl + "cellaware-backend", "vriesveem"), null),
            }),
            ("Lauryssens ▾", new (string, Action, string?)[]
            {
                ("Claude — laurapp", () => ClientLauncher.StartClaude(wsl + "laurapp"), wsl + "laurapp"),
                ("Claude — herstel-calculator", () => ClientLauncher.StartClaude(wsl + "lauryssens-herstel-calculator"), wsl + "lauryssens-herstel-calculator"),
                ("PhpStorm — laurapp", () => ClientLauncher.StartPhpStorm(wsl + "laurapp"), null),
                ("PhpStorm — herstel-calculator", () => ClientLauncher.StartPhpStorm(wsl + "lauryssens-herstel-calculator"), null),
                ("Claude — glascalculator (Drive)", () => ClientLauncher.StartClaude(
                    @"G:\Gedeelde drives\UrbanIT\Lauryssens\glascalculator"),
                    @"G:\Gedeelde drives\UrbanIT\Lauryssens\glascalculator"),
                ("Map — glascalculator (Drive)", () => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        @"G:\Gedeelde drives\UrbanIT\Lauryssens\glascalculator") { UseShellExecute = true }), null),
            }),
            // WorkManager zelf als "klant": zo krijgt hij dezelfde eigen knop in de brede
            // werkbalk als de echte klanten, mét 🟢-lampje, git-status en sluiten-item.
            ("WorkManager ▾", new (string, Action, string?)[]
            {
                ("Claude — WorkManager", () => ClientLauncher.StartClaude(@"C:\Data\Projecten\Workmanager"), @"C:\Data\Projecten\Workmanager"),
            }),
        })
        {
            // Elk klant-submenu krijgt het logo van de klant (of een initiaal in de klantkleur
            // zolang het logo nog niet opgehaald is).
            var klantItem = new ToolStripMenuItem(label.Replace(" ▾", ""))
            {
                Image = KlantLogo.Voor(label.Replace(" ▾", "")),
            };
            var claudeMappen = acties.Where(a => a.Claude is not null).Select(a => a.Claude!).Distinct().ToList();
            // Bouwt één volledige set klant-items (acties + git-status + Claude-sluiten) in
            // het gegeven menu, inclusief de Opening-verversing. Dit gebeurt twee keer: voor
            // het submenu in "Projecten ▾" én voor het eigen menu van de losse klantknop.
            // Items kunnen in WinForms maar in één menu tegelijk leven, en het submenu
            // standalone tonen kan niet: zolang de dropdown een OwnerItem heeft, herrekent
            // WinForms de positie vanaf het (onzichtbare) verzamelmenu — linksboven dus.
            void VulKlantMenu(ToolStripDropDown menu)
            {
                foreach (var (item, doe, _) in acties)
                {
                    var mi = new ToolStripMenuItem(item);
                    mi.Click += (_, _) =>
                    {
                        try
                        {
                            doe();
                            Toast.Toon(this, ThemaStem.Gestart(item), Fluent.Globe);
                        }
                        catch (Exception ex)
                        {
                            Toast.Toon(this, $"Starten mislukt: {ex.Message}", Fluent.Globe);
                        }
                    };
                    menu.Items.Add(mi);
                }
                // Git-status per projectmap: het aantal ongecommitte bestanden komt in het
                // label te staan (asynchroon, want een git-call in WSL duurt bijna een
                // seconde) en klikken opent de volledige lijst.
                var gitItems = new List<(ToolStripMenuItem Item, string Map, string Naam)>();
                if (claudeMappen.Count > 0)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    foreach (var map in claudeMappen)
                    {
                        var projectNaam = map.TrimEnd('\\', '/').Split('\\', '/').Last();
                        var git = new ToolStripMenuItem($"◆ Git-status — {projectNaam}");
                        git.Click += (_, _) =>
                        {
                            using var form = new GitStatusForm(map, projectNaam);
                            form.ShowDialog(this);
                        };
                        menu.Items.Add(git);
                        gitItems.Add((git, map, projectNaam));
                        _gitMenuItems.Add((git, map, projectNaam));
                        WerkGitLabelBij(git, map, projectNaam); // laatst bekende stand meteen erbij
                    }
                }
                // Per Claude-projectmap een sluiten-item dat alleen aan staat als er een sessie draait.
                var sluitItems = new List<(ToolStripMenuItem Item, string Map)>();
                if (claudeMappen.Count > 0)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    foreach (var map in claudeMappen)
                    {
                        var naam = map.TrimEnd('\\', '/').Split('\\', '/').Last();
                        var sluit = new ToolStripMenuItem($"⏹ Claude sluiten — {naam}");
                        sluit.Click += (_, _) =>
                        {
                            try
                            {
                                ClientLauncher.StopClaude(map);
                                Toast.Toon(this, $"Claude-sessie gesloten — {naam}", Fluent.Globe);
                            }
                            catch (Exception ex)
                            {
                                Toast.Toon(this, $"Sluiten mislukt: {ex.Message}", Fluent.Globe);
                            }
                        };
                        menu.Items.Add(sluit);
                        sluitItems.Add((sluit, map));
                    }
                }
                // Bij het openen: sluiten-items aan/uit en git-tellingen verversen zonder
                // het menu te laten wachten — de labels komen uit de dagcache (1× per dag
                // automatisch ververst, of via "Git controleren" onder ▾).
                menu.Opening += (_, _) =>
                {
                    foreach (var (sluit, map) in sluitItems)
                    {
                        sluit.Enabled = ClientLauncher.IsClaudeActief(map);
                    }
                    foreach (var (item, map, naam) in gitItems)
                    {
                        WerkGitLabelBij(item, map, naam);
                    }
                };
            }
            VulKlantMenu(klantItem.DropDown);
            projectKlanten.Add((klantItem, claudeMappen));
            projectenMenu.Items.Add(klantItem);
            // Op een breed venster staat elke klant gewoon naast elkaar in de balk; de knop
            // krijgt een eigen, zelfstandig menu met dezelfde inhoud.
            var losMenu = new ContextMenuStrip();
            Theme.Style(losMenu);
            VulKlantMenu(losMenu);
            var klantKnop = new ModernButton { Text = label };
            klantKnop.KrimpNaarInhoud();
            klantKnop.Click += (_, _) =>
                losMenu.Show(klantKnop, new Point(0, klantKnop.Height + 4));
            _projectKnoppen.Add((klantKnop, label, claudeMappen));
        }

        // De ene Projecten-knop: bij het openen krijgen klanten met een draaiende
        // Claude-sessie een 🟢, en de knop zelf ook zodra er ergens één draait.
        var projectenKnop = new ModernButton { Text = "Projecten ▾", Glyph = Fluent.Settings };
        projectenMenu.Opening += (_, _) =>
        {
            var ergens = false;
            foreach (var (item, mappen) in projectKlanten)
            {
                var actief = mappen.Any(ClientLauncher.IsClaudeActief);
                ergens |= actief;
                item.Text = (actief ? "🟢 " : "") + item.Text!.Replace("🟢 ", "");
                // Het logo kan intussen binnengehaald zijn (of het thema gewisseld).
                item.Image = KlantLogo.Voor(item.Text.Replace("🟢 ", ""));
            }
            projectenKnop.Text = (ergens ? "🟢 " : "") + "Projecten ▾";
            projectenKnop.KrimpNaarInhoud(dropdown: true);
        };
        projectenKnop.KrimpNaarInhoud(dropdown: true);
        projectenKnop.Click += (_, _) =>
            projectenMenu.Show(projectenKnop, new Point(0, projectenKnop.Height + 4));
        // Breed venster: de klantknoppen naast elkaar; smal venster: alleen "Projecten ▾".
        _projectenHoofdknop = projectenKnop;
        // CED is geen dev-project maar wel een dagelijkse werkplek: een dropdown naast de
        // Lauryssens-klantknop met de Azure-portal en de Windows App (AVD), waarbij
        // WorkManager de Microsoft-aanmelding voor het gekozen account invult.
        var cedMenu = new ContextMenuStrip();
        Theme.Style(cedMenu);
        // De Outlook VBA-modules (Mobility/Property/MailModule) zijn CED-werk: de
        // Claude-sessie hoort in dit ene CED-menu, niet als aparte projectgroep.
        var automaticmailItem = new ToolStripMenuItem("Claude — automaticmail");
        automaticmailItem.Click += (_, _) =>
        {
            try
            {
                ClientLauncher.StartClaude(@"C:\Data\Projecten\automaticmail");
                Toast.Toon(this, ThemaStem.Gestart("Claude — automaticmail"), Fluent.Globe);
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Starten mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        cedMenu.Items.Add(automaticmailItem);
        cedMenu.Items.Add(new ToolStripSeparator());
        var azurePortalItem = new ToolStripMenuItem("Azure-portal…");
        azurePortalItem.Click += (_, _) => OpenExtern("https://portal.azure.com/");
        cedMenu.Items.Add(azurePortalItem);
        // Facturen goedkeuren hoort bij het CED-werk: ook hier bereikbaar, niet alleen via
        // de (week)taakknop in de balk en het tray-menu.
        var ispnextItem = new ToolStripMenuItem("Facturen goedkeuren (ISPnext)…");
        ispnextItem.Click += (_, _) => _openInvoices();
        cedMenu.Items.Add(ispnextItem);
        cedMenu.Items.Add(new ToolStripSeparator());
        var windowsAppItems = new List<ToolStripMenuItem>();
        foreach (var account in new[] { CedLogin.TopdeskGebruiker, CedLogin.Email })
        {
            var mi = new ToolStripMenuItem($"Windows App — {account}");
            mi.Click += async (_, _) =>
            {
                Toast.Toon(this, $"Windows App starten, aanmelden als {account}…", Fluent.Globe);
                try
                {
                    Toast.Toon(this, await WindowsAppLogin.StartEnMeldAanAsync(account, _cts.Token),
                        Fluent.Globe);
                }
                catch (OperationCanceledException)
                {
                    // Cockpit gesloten tijdens het aanmelden.
                }
                catch (Exception ex)
                {
                    Toast.Toon(this, $"Windows App-aanmelding mislukt: {ex.Message}", Fluent.Globe);
                }
            };
            cedMenu.Items.Add(mi);
            windowsAppItems.Add(mi);
        }
        var cedKnop = new ModernButton { Text = "CED ▾", Glyph = Fluent.Globe };
        cedKnop.KrimpNaarInhoud(dropdown: true);
        cedKnop.Click += (_, _) => cedMenu.Show(cedKnop, new Point(0, cedKnop.Height + 4));
        foreach (var (knop, klantLabel, _) in _projectKnoppen)
        {
            toolbar.Controls.Add(knop);
            if (klantLabel.StartsWith("Lauryssens", StringComparison.OrdinalIgnoreCase))
            {
                toolbar.Controls.Add(cedKnop);
            }
        }
        toolbar.Controls.Add(projectenKnop);
        Resize += (_, _) => WerkProjectWeergaveBij();
        WerkProjectWeergaveBij();

        // Live sessiepaneel voor de multiclauder: één knop met badge (aantal sessies dat
        // op input wacht) en per draaiende Claude-sessie een regel met status; klikken
        // haalt het terminalvenster naar voren. Gevoed door de hook-events die de tray
        // al verwerkt (ClaudeSessies) — werkt dus ook voor WSL-sessies.
        var claudeMenu = new ContextMenuStrip();
        Theme.Style(claudeMenu);
        var claudeKnop = new ModernButton { Text = "🤖 Claude ▾" };
        claudeKnop.KrimpNaarInhoud(dropdown: true);
        claudeMenu.Opening += (_, _) =>
        {
            claudeMenu.Items.Clear();
            var sessies = ClaudeSessies.Snapshot();
            if (sessies.Count == 0)
            {
                claudeMenu.Items.Add(new ToolStripMenuItem("Geen actieve Claude-sessies")
                {
                    Enabled = false,
                });
                return;
            }
            foreach (var s in sessies)
            {
                var (icoon, status) = s.Status switch
                {
                    ClaudeSessies.Wacht => ("🟠", "wacht op input"),
                    ClaudeSessies.Klaar => ("✅", "klaar — wacht op vervolg"),
                    ClaudeSessies.Bezig => ("🔵", "bezig"),
                    _ => ("⚪", "gestart"),
                };
                var minuten = (int)(DateTimeOffset.Now - s.Sinds).TotalMinutes;
                var mi = new ToolStripMenuItem(
                    $"{icoon} {ClientLauncher.SessieLabel(s.Map)} — {status} " +
                    $"({(minuten < 1 ? "net" : $"{minuten} min")})")
                {
                    ToolTipText = s.Boodschap,
                };
                var sessie = s;
                mi.Click += (_, _) => ClaudeAandacht.ActiveerTerminal(
                    sessie.VensterPid, sessie.VensterHandle, sessie.Map);
                claudeMenu.Items.Add(mi);
            }
        };
        claudeKnop.Click += (_, _) =>
            claudeMenu.Show(claudeKnop, new Point(0, claudeKnop.Height + 4));
        // Badge in de knoptekst: aantal sessies dat op input wacht, elke 5 s ververst.
        var claudeBadgeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        claudeBadgeTimer.Tick += (_, _) =>
        {
            var wachtend = ClaudeSessies.AantalWachtend();
            var tekst = wachtend > 0 ? $"🤖 Claude ({wachtend}) ▾" : "🤖 Claude ▾";
            if (claudeKnop.Text != tekst)
            {
                claudeKnop.Text = tekst;
                claudeKnop.KrimpNaarInhoud(dropdown: true);
            }
        };
        claudeBadgeTimer.Start();
        toolbar.Controls.Add(claudeKnop);

        // Snelkoppelingen naar de Drive-boekhoudmappen: openen in de Verkenner via de lokale
        // "Drive voor desktop"-spiegel (G:\Mijn Drive). Begin volgend jaar de 2026-paden
        // verversen naar de nieuwe jaarmap.
        var driveMenu = new ContextMenuStrip();
        Theme.Style(driveMenu);
        foreach (var (label, map) in new[]
        {
            ("Maarten 2026", @"G:\Mijn Drive\administratie\maarten\2026"),
            ("Hilke 2026", @"G:\Mijn Drive\administratie\hilke\2026"),
            ("Lisa 2026", @"G:\Mijn Drive\administratie\lisa\2026"),
            ("Emilia 2026", @"G:\Mijn Drive\administratie\emilia\2026"),
            ("—", ""),
            ("Bermacon", @"G:\Mijn Drive\Bermacon"),
            ("UrbanIT", @"G:\Gedeelde drives\UrbanIT"),
            ("Aqurat", @"G:\Gedeelde drives\Aqurat"),
        })
        {
            if (map.Length == 0)
            {
                driveMenu.Items.Add(new ToolStripSeparator());
                continue;
            }
            var mi = new ToolStripMenuItem(label);
            mi.Click += (_, _) =>
            {
                if (Directory.Exists(map))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"\"{map}\"") { UseShellExecute = true });
                }
                else
                {
                    Toast.Toon(this, $"Map niet gevonden ({map}) — draait Drive voor desktop?", Fluent.Globe);
                }
            };
            driveMenu.Items.Add(mi);
        }
        // De reischecklists: rechtstreeks de spreadsheets in de browser, want die wil je
        // openen om af te vinken, niet de map errond.
        driveMenu.Items.Add(new ToolStripSeparator());
        foreach (var (label, url) in new[]
        {
            ("🧳 Reischecklist Maarten",
                "https://drive.google.com/file/d/0B0FCDFP5GQE1UnlfWWw2NVpQWGs/view?usp=drivesdk&resourcekey=0-u_Lq4P1yy-HQw2vuMWzS5w"),
            ("🧳 Reischecklist Hilke",
                "https://drive.google.com/file/d/0B0FCDFP5GQE1SkIwYTJsRTk2X2M/view?usp=drivesdk&resourcekey=0-oCwTz-96-fxLIasHl_Hhnw"),
            ("🧳 Reischecklist Lisa & Emilia",
                "https://drive.google.com/file/d/1UhYNWYrvlYyOfRxS5d1m6da06ixKQLug/view?usp=drivesdk"),
        })
        {
            var mi = new ToolStripMenuItem(label);
            mi.Click += (_, _) => OpenExtern(url);
            driveMenu.Items.Add(mi);
        }
        var driveKnop = new ModernButton { Text = "Drive ▾", Glyph = Fluent.Document };
        driveKnop.KrimpNaarInhoud(dropdown: true);
        driveKnop.Click += (_, _) => driveMenu.Show(driveKnop, new Point(0, driveKnop.Height + 4));
        toolbar.Controls.Add(driveKnop);

        // Paars (accent) is in de cockpit voorbehouden aan knoppen die om actie vragen —
        // facturen die klaarstaan, een sessie die heraanmelding nodig heeft, een Claude-update.
        // Gewone bedieningsknoppen zoals verversen en filters blijven neutraal.
        _verversButton = new ModernButton { Text = "Nu verversen", Width = 140 };
        _verversButton.Click += async (_, _) => await VerversAsync(handmatig: true);
        // Dropdown naast de verversknop: "Volledige synchronisatie" herstelt de Outlook-
        // lijst (verbergmarkeringen weg) en herlaadt de verborgen sessies vers.
        var verversMenu = new ContextMenuStrip();
        Theme.Style(verversMenu);
        var volledigeSyncItem = new ToolStripMenuItem("Volledige synchronisatie");
        volledigeSyncItem.ToolTipText =
            "Outlook-lijst herstellen (alles wat echt in het postvak staat weer tonen) " +
            "en de verborgen sessies vers herladen";
        volledigeSyncItem.Click += async (_, _) => await VolledigeSyncAsync();
        verversMenu.Items.Add(volledigeSyncItem);
        // Ook hier bereikbaar: de sessielampjes (met hun menu) staan alleen nog in beeld
        // als er echt een probleem is.
        var gezondheidVerversItem = new ToolStripMenuItem("Gezondheid bronnen…");
        gezondheidVerversItem.Click += (_, _) => ToonGezondheid();
        verversMenu.Items.Add(gezondheidVerversItem);
        // Actieve git-controle van alle projectmappen; de automatische controle (1× per
        // dag, via de poll) houdt de labels in het Projecten-menu verder bij.
        var gitControleItem = new ToolStripMenuItem("Git controleren (alle projecten)");
        gitControleItem.Click += async (_, _) => await ControleerGitAsync(handmatig: true);
        verversMenu.Items.Add(gitControleItem);
        var verversMeerKnop = new ModernButton { Text = "▾", Width = 34 };
        verversMeerKnop.Click += (_, _) =>
            verversMenu.Show(verversMeerKnop, new Point(0, verversMeerKnop.Height));
        var teamButton = new ModernButton { Text = "Teamtaken…", Width = 130 };
        teamButton.Click += (_, _) => _openTeamTasks();
        _facturenButton = new ModernButton
        {
            Text = "Facturen goedkeuren…", Width = 190, Visible = false, Kind = ButtonKind.Accent,
        };
        _facturenButton.Click += (_, _) =>
        {
            // De klik vinkt meteen de weektaak af: zo onthoudt de app — ook na een herstart —
            // dat de facturen deze week al opgepakt zijn, en blijft de knop weg.
            _facturenGeklikt = true;
            _facturenButton.Visible = false;
            VasteTaken.VinkAf(VasteTaken.FacturenTaak);
            _openInvoices();
        };
        // Teams en CED-Outlook vragen dagelijks MFA: de knoppen verschijnen alleen wanneer
        // de sessie niet (meer) aangemeld is (zie WerkAanmeldKnoppenBij).
        // Verschijnt alleen als de sessie echt heraanmelding nodig heeft: dat vraagt actie.
        _teamsKoppelButton = new ModernButton
        {
            Text = "Teams aanmelden…", Width = 165, Kind = ButtonKind.Accent,
        };
        _teamsKoppelButton.Click += async (_, _) =>
        {
            try
            {
                await TeamsClient.Instance.KoppelAsync(_cts.Token);
                _teamsKoppelButton.Visible = false; // meteen weg, niet wachten op de sync
                Toast.Toon(this, "Teams aangemeld", Fluent.Check);
                await VerversNaAanmeldenAsync();
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten tijdens het aanmelden.
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Teams aanmelden mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        _outlookKoppelButton = new ModernButton
        {
            Text = "Outlook aanmelden…", Width = 180, Kind = ButtonKind.Accent,
        };
        _outlookKoppelButton.Click += async (_, _) =>
        {
            try
            {
                await OutlookClient.Instance.KoppelAsync(_cts.Token);
                _outlookKoppelButton.Visible = false; // meteen weg, niet wachten op de sync
                Toast.Toon(this, "Outlook (CED) aangemeld", Fluent.Check);
                await VerversNaAanmeldenAsync();
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten tijdens het aanmelden.
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Outlook aanmelden mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        // WhatsApp logt gekoppelde apparaten na een tijd uit (dan is een nieuwe QR-scan
        // nodig): dezelfde alleen-bij-actie-knop als voor Teams en Outlook, maar de scan
        // gebeurt in een QR-venster in plaats van een Microsoft-login.
        _waKoppelButton = new ModernButton
        {
            Text = "WhatsApp koppelen…", Width = 185, Kind = ButtonKind.Accent,
            Visible = false, // pas tonen als een poll de sessiestatus echt kent
        };
        _waKoppelButton.Click += async (_, _) =>
        {
            try
            {
                Toast.Toon(this, "Er opent een venster met een QR-code — scan die met je " +
                    "telefoon (WhatsApp → Instellingen → Gekoppelde apparaten)", Fluent.Mail);
                await WhatsAppClient.Instance.KoppelAsync(_cts.Token);
                _waKoppelButton.Visible = false; // meteen weg, niet wachten op de sync
                Toast.Toon(this, "WhatsApp gekoppeld", Fluent.Check);
                await VerversNaAanmeldenAsync();
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten tijdens het koppelen.
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"WhatsApp koppelen mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        // Snelkoppelingen voor Jan Van Dyck: zijn DM in het detailpaneel en de vaste Meet-link.
        var chatJanKnop = new ModernButton { Text = "Chat Jan", Width = 105 };
        chatJanKnop.Click += async (_, _) =>
        {
            chatJanKnop.Enabled = false;
            chatJanKnop.Bezig = true;
            try
            {
                await OpenChatJanAsync();
            }
            finally
            {
                chatJanKnop.Bezig = false;
                chatJanKnop.Enabled = true;
            }
        };
        var meetJanKnop = new ModernButton { Text = "Meet Jan", Width = 105 };
        meetJanKnop.Click += (_, _) =>
        {
            OpenExtern("https://meet.google.com/geo-uitu-nrb");
            Toast.Toon(this, "Meet met Jan geopend in je browser", Fluent.Globe);
        };
        // Snelkoppeling naar urbanadmin om een werkuur/timesheet toe te voegen.
        var timesheetKnop = new ModernButton
        {
            Text = "Timesheet toevoegen", Width = 175, Glyph = Fluent.Kalender,
        };
        timesheetKnop.Click += (_, _) =>
        {
            OpenExtern("https://timesheets.urbanit.be/app/werkuur-toevoegen");
            Toast.Toon(this, "Timesheet-invoer geopend in je browser", Fluent.Globe);
        };
        // Het dashboard van urbanadmin zelf (overzicht geboekte uren, niet de invoer): opent
        // ín WorkManager, in een ingebedde browser met bewaarde login.
        var timesheetDashboardKnop = new ModernButton
        {
            Text = "Timesheets", Width = 140, Glyph = Fluent.Kalender,
        };
        timesheetDashboardKnop.Click += (_, _) =>
        {
            using var form = new TimesheetDashboardForm();
            form.ShowDialog(this);
        };
        // Dinsdag en donderdag zijn CED-dagen: in één klik de hele dag boeken, opgesplitst
        // rond de meetings uit de Office 365-agenda. Werkt op de dag die in de meetinglijst
        // getoond wordt, zodat je met ◀ ▶ ook een andere dag kunt afrekenen — de knop staat
        // er dan ook alleen als de getoonde dag een CED-dag is (minder werkbalkruis).
        var cedDagKnop = _cedDagKnop = new ModernButton { Text = "CED-dag…", Glyph = Fluent.Klok };
        cedDagKnop.KrimpNaarInhoud();
        cedDagKnop.Visible = DateTime.Now.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday;
        cedDagKnop.Click += async (_, _) => await CedDagTimesheetsAsync(cedDagKnop);
        // Dagvoorstel: Claude vertaalt de activiteitenlog (+ switches, launcher, meetings)
        // naar timesheetregels over alle klanten heen — nakijken, aanpassen, boeken.
        var dagvoorstelKnop = new ModernButton { Text = "Dagvoorstel…", Glyph = Fluent.Document };
        dagvoorstelKnop.KrimpNaarInhoud();
        dagvoorstelKnop.Click += async (_, _) => await DagvoorstelTimesheetsAsync(dagvoorstelKnop);
        // De CED-servicedesk in TopDesk: ticketlijst + ingebedde behandelaarssessie. De knop
        // staat alleen in de balk zolang er werk vermoed wordt (zie TopdeskSignaal); via het
        // ⋯-menu kan het altijd.
        var topdeskKnop = _topdeskKnop = new ModernButton { Text = "TopDesk…", Glyph = Fluent.Lijst };
        topdeskKnop.KrimpNaarInhoud();
        topdeskKnop.Visible = WerkSignaal.Actief("topdesk");
        topdeskKnop.Click += (_, _) => _openTopdesk();
        // Zelfde patroon voor Azure DevOps: knop alleen zolang er werk vermoed wordt.
        var devopsKnop = _devopsKnop = new ModernButton { Text = "DevOps…", Glyph = Fluent.Lijst };
        devopsKnop.KrimpNaarInhoud();
        devopsKnop.Visible = WerkSignaal.Actief("devops");
        devopsKnop.Click += (_, _) => _openDevOps();
        // Zelfde patroon voor SD Worx: een meldingsmail over een verlofaanvraag zet de knop
        // aan; het portaalvenster logt automatisch in, zodat je alleen nog hoeft goed te keuren.
        var verlofKnop = _verlofKnop = new ModernButton { Text = "Verlof…", Glyph = Fluent.Kalender };
        verlofKnop.KrimpNaarInhoud();
        verlofKnop.Visible = WerkSignaal.Actief("sdworx");
        verlofKnop.Click += (_, _) => _openVenster("verlof");
        // Docker-check bij het openen van de cockpit: ligt de engine plat, dan staat hier
        // een opvallend rode startknop (devenv-mysql en de projectstacks draaien in Docker).
        // De knop verdwijnt zodra de engine draait; elke ophaalronde kijkt opnieuw.
        var dockerKnop = _dockerKnop = new ModernButton
            { Text = "Docker starten", Glyph = Fluent.Play, Kind = ButtonKind.Danger };
        dockerKnop.KrimpNaarInhoud();
        dockerKnop.Visible = DockerStatus.Geinstalleerd && !DockerStatus.Draait;
        dockerKnop.Click += async (_, _) =>
        {
            dockerKnop.Bezig = true;
            dockerKnop.Enabled = false;
            try
            {
                var ok = await DockerStatus.StartAsync(_cts.Token);
                dockerKnop.Visible = !ok;
                if (ok)
                {
                    Toast.Toon(this, "Docker draait", Fluent.Check);
                }
                else
                {
                    Toast.Fout(this, "Docker start niet",
                        "Docker Desktop is gestart maar de engine kwam niet binnen 2 minuten op. " +
                        "Kijk zelf even in Docker Desktop wat er hapert.");
                }
            }
            catch (OperationCanceledException)
            {
                // Cockpit gesloten tijdens het wachten.
            }
            finally
            {
                dockerKnop.Bezig = false;
                dockerKnop.Enabled = true;
            }
        };
        // Claude Code CLI bijwerken naar de nieuwste versie ('claude update' is een no-op als
        // je al up-to-date bent). De knop staat altijd in de balk; alleen bij een échte
        // versiesprong (2.1 → 2.2, taak van UpdateCheck) kleurt hij accent met de versies erbij.
        var claudeUpdateKnop = _claudeUpdateKnop =
            new ModernButton { Text = "Claude bijwerken", Glyph = Fluent.Sync };
        claudeUpdateKnop.KrimpNaarInhoud();
        claudeUpdateKnop.Click += async (_, _) =>
        {
            claudeUpdateKnop.Bezig = true;
            claudeUpdateKnop.Enabled = false;
            try
            {
                // winget kan de CLI-exe niet vervangen zolang er nog een sessie draait. Draaien er
                // interactieve sessies, sluit die (en PhpStorm, dat zo'n sessie vaak host) dan zelf
                // zonder te vragen en werk daarna bij. De Claude Desktop-app blijft gewoon open.
                if (LopendeClaudeCliSessies() > 0)
                {
                    SluitClaudeCliSessies();
                    SluitPhpStorm();
                    // Even wachten tot de processen echt weg zijn en de exe-lock los is.
                    await Task.Delay(1500);
                }

                var (huidig, nieuw, melding) = await UpgradeClaudeAsync();
                // Gelukt (of bleek al up-to-date): de update-taak afvinken, zodat het menu-item
                // meteen weer inactief staat.
                if (nieuw.Length > 0 && (huidig != nieuw || melding.Contains("up-to-date")))
                {
                    UpdateCheck.VinkTaakAf("Claude bijwerken");
                    await VerversTakenAsync();
                }
                Toast.Toon(this, melding, Fluent.Sync);
            }
            finally
            {
                claudeUpdateKnop.Bezig = false;
                claudeUpdateKnop.Enabled = true;
            }
        };
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        Theme.AsStatus(_sessieStatus);
        _sessieStatus.Padding = new Padding(6, 0, 0, 0);
        // De lampjes zijn klikbaar: één menu om de bron met 🟠 meteen aan te melden.
        _sessieStatus.Cursor = Cursors.Hand;
        var sessieMenu = new ContextMenuStrip();
        Theme.Style(sessieMenu);
        var teamsAanmeldItem = new ToolStripMenuItem("Teams aanmelden…");
        teamsAanmeldItem.Click += (_, _) => _teamsKoppelButton.PerformClick();
        sessieMenu.Items.Add(teamsAanmeldItem);
        var outlookAanmeldItem = new ToolStripMenuItem("Outlook aanmelden…");
        outlookAanmeldItem.Click += (_, _) => _outlookKoppelButton.PerformClick();
        sessieMenu.Items.Add(outlookAanmeldItem);
        var waKoppelItem = new ToolStripMenuItem("WhatsApp koppelen… (QR-scan)");
        waKoppelItem.Click += (_, _) => _waKoppelButton.PerformClick();
        sessieMenu.Items.Add(waKoppelItem);
        var gezondheidItem = new ToolStripMenuItem("Gezondheid bronnen…");
        gezondheidItem.Click += (_, _) => ToonGezondheid();
        sessieMenu.Items.Add(gezondheidItem);
        // Als het verversen van Outlook "niets doet": hier zie je waar het spaak loopt.
        var owaDiagItem = new ToolStripMenuItem("Outlook-diagnose…");
        owaDiagItem.Click += (_, _) =>
        {
            if (_owaDiagForm is { IsDisposed: false })
            {
                _owaDiagForm.Activate();
                return;
            }
            _owaDiagForm = new OutlookDiagnoseForm();
            _owaDiagForm.FormClosed += (_, _) => _owaDiagForm = null;
            _owaDiagForm.Show(this);
        };
        sessieMenu.Items.Add(owaDiagItem);
        sessieMenu.Opening += (_, _) =>
        {
            // Alleen tonen wat nú actie vraagt; het gezondheidsoverzicht staat er altijd.
            teamsAanmeldItem.Visible = !TeamsClient.Aangemeld;
            outlookAanmeldItem.Visible = !OutlookClient.Aangemeld;
            waKoppelItem.Visible = !WhatsAppClient.OoitGekoppeld || !WhatsAppClient.Aangemeld;
        };
        _sessieStatus.Click += (_, _) =>
            sessieMenu.Show(_sessieStatus, new Point(0, _sessieStatus.Height + 4));
        // De klantcontexten staan niet meer in de werkbalk, dus is er plaats: wat vroeger achter
        // "⋯ meer" zat, staat nu gewoon als knop in de balk.
        var regelsKnop = new ModernButton { Text = "Archiveerregels…", Glyph = Fluent.Archive };
        regelsKnop.KrimpNaarInhoud();
        regelsKnop.Click += (_, _) =>
        {
            using var form = new ArchiveerRegelsForm();
            form.ShowDialog(this);
        };
        meetJanKnop.KrimpNaarInhoud();
        timesheetKnop.KrimpNaarInhoud();
        timesheetDashboardKnop.KrimpNaarInhoud();
        claudeUpdateKnop.KrimpNaarInhoud();

        // Dagplanning: volgorde, duurschatting en of je rond geraakt.
        var dagPlanKnop = new ModernButton { Text = "Dagplanning", Glyph = Fluent.Ster };
        dagPlanKnop.KrimpNaarInhoud();
        dagPlanKnop.Click += (_, _) =>
        {
            using var form = new DagPlanForm(HuidigeMeetings());
            form.ShowDialog(this);
            _ = VerversTakenAsync(); // afgevinkte planitems meteen uit de takenlijst
        };
        // Focusbalk: klein strookje bovenaan het scherm met alleen de volgende actie.
        var focusKnop = new ModernButton { Text = "Focus", Glyph = Fluent.Ster };
        focusKnop.KrimpNaarInhoud();
        focusKnop.Click += (_, _) =>
        {
            FocusBarForm.Toggle();
            focusKnop.Text = FocusBarForm.Actief ? "🟢 Focus" : "Focus";
        };

        toolbar.Controls.Add(_verversButton);
        toolbar.Controls.Add(verversMeerKnop);
        toolbar.Controls.Add(dagPlanKnop);
        toolbar.Controls.Add(focusKnop);
        toolbar.Controls.Add(teamButton);
        toolbar.Controls.Add(_facturenButton);
        // Chat Jan en Meet Jan horen bij elkaar: naast elkaar in de balk.
        toolbar.Controls.Add(chatJanKnop);
        toolbar.Controls.Add(meetJanKnop);
        toolbar.Controls.Add(_teamsKoppelButton);
        toolbar.Controls.Add(_outlookKoppelButton);
        toolbar.Controls.Add(_waKoppelButton);
        toolbar.Controls.Add(cedDagKnop);
        toolbar.Controls.Add(dagvoorstelKnop);
        toolbar.Controls.Add(topdeskKnop);
        toolbar.Controls.Add(devopsKnop);
        toolbar.Controls.Add(verlofKnop);
        toolbar.Controls.Add(dockerKnop);
        toolbar.Controls.Add(timesheetKnop);
        toolbar.Controls.Add(timesheetDashboardKnop);
        // Claude-abonnementsverbruik (zelfde bron als /usage in de CLI).
        var usageKnop = new ModernButton { Text = "Claude-usage", Glyph = Fluent.Ster };
        usageKnop.KrimpNaarInhoud();
        usageKnop.Click += (_, _) =>
        {
            using var form = new ClaudeUsageForm();
            form.ShowDialog(this);
        };
        // Zelden gebruikte knoppen samen achter één "⋯"-knop: archiveerregels en usage
        // hoeven geen vaste plek in de (overvolle) werkbalk.
        var meerMenu = new ContextMenuStrip();
        Theme.Style(meerMenu);
        // Gegroepeerd en per groep alfabetisch, zodat je een functie meteen terugvindt.
        // Kleine helpers houden het toevoegen kort en de groepen leesbaar.
        ToolStripMenuItem Venster(string label, string naam)
        {
            var it = new ToolStripMenuItem(label);
            it.Click += (_, _) => _openVenster(naam);
            return it;
        }
        ToolStripMenuItem Actie(string label, Action doe)
        {
            var it = new ToolStripMenuItem(label);
            it.Click += (_, _) => doe();
            return it;
        }
        static ToolStripMenuItem Kop(string label) =>
            new(label) { Enabled = false }; // niet-klikbaar groepskopje

        // Kleurenschema-submenu (naast het traymenu): meestal kies je het terwijl je in de
        // cockpit zit te kijken.
        var themaMenuItem = new ToolStripMenuItem("Kleurenschema");
        foreach (var palet in Themas.Alle)
        {
            var keuze = new ToolStripMenuItem($"{palet.Naam} — {palet.Omschrijving}");
            keuze.Click += (_, _) =>
            {
                Theme.ZetThema(palet);
                Toast.Toon(this, ThemaStem.Welkom(), Fluent.Color);
            };
            themaMenuItem.DropDownItems.Add(keuze);
        }
        themaMenuItem.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem keuze in themaMenuItem.DropDownItems)
            {
                keuze.Checked = keuze.Text!.StartsWith(Theme.Palet.Naam + " —", StringComparison.Ordinal);
            }
        };

        // De cockpit is de vaste werkplek: elke functie moet hier bereikbaar zijn. Vier
        // duidelijke groepen in plaats van één lange, ongeordende lijst.
        meerMenu.Items.AddRange(new ToolStripItem[]
        {
            Kop("Taken & werk"),
            Venster("Dagstart…", "dagstart"),
            Venster("Mijn taken…", "mijntaken"),
            Actie("Taken team…", () => _openTeamTasks()),
            Venster("Verlof goedkeuren (SD Worx)…", "verlof"),
            Venster("Wacht op antwoord…", "followup"),
            new ToolStripSeparator(),

            Kop("CED / Microsoft"),
            Actie("Azure-portal (CED)…", () => OpenExtern("https://portal.azure.com/")),
            Actie($"Windows App — {CedLogin.TopdeskGebruiker}…",
                () => windowsAppItems[0].PerformClick()),
            Actie($"Windows App — {CedLogin.Email}…",
                () => windowsAppItems[1].PerformClick()),
            Actie("Azure DevOps…", () => _openDevOps()),
            Venster("Azure-VM BI starten (VMWS-BI-MB-1)…", "azurevm"),
            Actie("Facturen goedkeuren (ISPnext)…", () => _openInvoices()),
            Actie("Mail beantwoorden (Gmail)…", () => _openMail()),
            Actie("TopDesk-tickets…", () => _openTopdesk()),
            new ToolStripSeparator(),

            Kop("Privé & huishouden"),
            Venster("AH-bestelling…", "ah"),
            Venster("Bureaublad opruimen…", "bureaublad"),
            Venster("Verjaardagen & cadeaus…", "verjaardagen"),
            Venster("VIP-lijst…", "vip"),
            new ToolStripSeparator(),

            Kop("Instellingen & extra"),
            Actie("Archiveerregels…", () => regelsKnop.PerformClick()),
            Actie("Claude-usage…", () => usageKnop.PerformClick()),
            themaMenuItem,
            Venster("WorkManager online…", "webversie"),
        });
        var meerKnop = new ModernButton { Text = "⋯", Width = 44 };
        meerKnop.Click += (_, _) => meerMenu.Show(meerKnop, new Point(0, meerKnop.Height + 4));
        toolbar.Controls.Add(meerKnop);
        // Meldingenlog: toasts zijn vluchtig — hier staan de laatste ~30 nog eens op een rij.
        var meldingenKnop = new ModernButton { Text = "🔔", Width = 44 };
        meldingenKnop.Click += (_, _) =>
        {
            var log = new ContextMenuStrip();
            Theme.Style(log);
            foreach (var (moment, tekst) in Toast.Recent.Take(20))
            {
                log.Items.Add(new ToolStripMenuItem($"{moment:HH:mm}  {Kort(tekst, 80)}")
                {
                    Enabled = false,
                });
            }
            if (log.Items.Count == 0)
            {
                log.Items.Add(new ToolStripMenuItem("Nog geen meldingen deze sessie") { Enabled = false });
            }
            log.Show(meldingenKnop, new Point(0, meldingenKnop.Height + 4));
        };
        toolbar.Controls.Add(meldingenKnop);
        // De prijzenkast met verborgen prestaties: klein knopje, grote ontdekkingsvreugde.
        var prestatiesKnop = new ModernButton { Text = "🏆", Width = 44 };
        prestatiesKnop.Click += (_, _) =>
        {
            using var form = new PrestatiesForm();
            form.ShowDialog(this);
        };
        toolbar.Controls.Add(prestatiesKnop);
        toolbar.Controls.Add(claudeUpdateKnop);
        toolbar.Controls.Add(_status);
        toolbar.Controls.Add(_sessieStatus);
        // De werkbalk is te vol voor één rij: laat hem wrappen en meegroeien, met een vaste
        // knip na de projectknoppen zodat de indeling voorspelbaar blijft (rij 1 = projecten,
        // rij 2 = dagelijkse acties). Zonder dit vallen de laatste knoppen buiten beeld.
        toolbar.AutoSize = true;
        toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        toolbar.Padding = new Padding(10, 9, 10, 7);
        toolbar.SetFlowBreak(driveKnop, true);
        // De aanmeldknoppen normaal pas tonen als de eerste ophaalbeurt de sessiestatus echt
        // kent (anders flitsen ze bij elke start even in beeld terwijl de sessies opstarten).
        // Uitzondering: is het CED-24u-MFA-venster al verlopen, dan tonen we de knop meteen
        // bij het opstarten — dan weet je zeker dat er opnieuw aangemeld moet worden.
        _teamsKoppelButton.Visible = TeamsClient.OoitGekoppeld && MfaTijd.Verlopen("teams");
        _outlookKoppelButton.Visible = OutlookClient.OoitGekoppeld && MfaTijd.Verlopen("outlook");

        // Boven: berichten
        _berichten = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Geen berichten — alles is bij 🎉",
            LeegSoort = "berichten",
            LeegGlyph = Fluent.Mail,
        };
        _berichten.Columns.Add("Van", 260);
        _berichten.Columns.Add("Bericht", 620);
        _berichten.Columns.Add("Ontvangen", 130);
        _berichten.DoubleClick += (_, _) => Beantwoorden();
        var berichtenMenu = new ContextMenuStrip();
        Theme.Style(berichtenMenu);
        var antwoordItem = new ToolStripMenuItem("Beantwoorden…");
        antwoordItem.Click += (_, _) => Beantwoorden();
        berichtenMenu.Items.Add(antwoordItem);
        var archiveerItem = new ToolStripMenuItem("Archiveren");
        archiveerItem.Click += async (_, _) => await ArchiveerBerichtAsync();
        berichtenMenu.Items.Add(archiveerItem);
        var herstelItem = new ToolStripMenuItem("Laatste Outlook-archivering terugzetten");
        herstelItem.Click += async (_, _) => await HerstelLaatsteArchiveringAsync();
        berichtenMenu.Items.Add(herstelItem);
        var snoozeItem = new ToolStripMenuItem("Snoozen");
        berichtenMenu.Items.Add(snoozeItem);
        var supportItem = new ToolStripMenuItem("Support-sessie starten (AVG)…");
        supportItem.Click += (_, _) => StartSupportSessie();
        berichtenMenu.Items.Add(supportItem);
        var vipItem = new ToolStripMenuItem("Als VIP markeren");
        vipItem.Click += (_, _) => WisselVip();
        berichtenMenu.Items.Add(vipItem);
        berichtenMenu.Opening += (_, _) =>
        {
            // Dezelfde regel schakelt heen en weer: wie al VIP is, kan je er zo weer af halen.
            vipItem.Visible = GeselecteerdBericht() is not null;
            vipItem.Text = GeselecteerdBericht() is { } vb && VipLijst.IsVip(vb, _vipSleutels)
                ? "Uit VIP-lijst halen"
                : "Als VIP markeren";
            // Snoozen kan voor Gmail (label) en Outlook (OWA's eigen sluimerfunctie).
            snoozeItem.Enabled = GeselecteerdBericht() is { MessageId.Length: > 0 } b &&
                (!b.IsChat || b.OutlookMail.Length > 0);
            // Snooze-presets (tijdsafhankelijk) telkens vers opbouwen.
            snoozeItem.DropDownItems.Clear();
            foreach (var (label, moment) in SnoozePresets())
            {
                var mi = new ToolStripMenuItem(label);
                mi.Click += async (_, _) => await SnoozeBerichtAsync(moment);
                snoozeItem.DropDownItems.Add(mi);
            }
            snoozeItem.DropDownItems.Add(new ToolStripSeparator());
            var kies = new ToolStripMenuItem("Kies datum…");
            kies.Click += async (_, _) => await SnoozeBerichtAsync();
            snoozeItem.DropDownItems.Add(kies);
            herstelItem.Enabled = File.Exists(OutlookHerstelFile);
            // De AVG-supportactie alleen bij mail van een supportklant.
            supportItem.Visible = GeselecteerdBericht() is { } sb && IsSupportBericht(sb);
        };
        var taakItem = new ToolStripMenuItem("Taak maken in Mijn taken…");
        taakItem.Click += async (_, _) => await TaakVanBerichtAsync();
        berichtenMenu.Items.Add(taakItem);
        // Google Chat: een duim is vaak antwoord genoeg. Reageert op het laatste bericht en
        // handelt de rij af.
        var duimItem = new ToolStripMenuItem("👍 Duim omhoog") { ShortcutKeyDisplayString = "D" };
        duimItem.Click += async (_, _) => await DuimOpBerichtAsync();
        berichtenMenu.Items.Add(duimItem);
        var reactieItem = new ToolStripMenuItem("Andere reactie");
        foreach (var emoji in new[] { "❤️", "😀", "🎉", "🙏", "✅", "👀" })
        {
            var mi = new ToolStripMenuItem(emoji);
            mi.Click += async (_, _) => await DuimOpBerichtAsync(emoji);
            reactieItem.DropDownItems.Add(mi);
        }
        berichtenMenu.Items.Add(reactieItem);
        berichtenMenu.Opening += (_, _) =>
            duimItem.Visible = reactieItem.Visible = IsChatBericht(GeselecteerdBericht());
        var afspraakVanBerichtItem = new ToolStripMenuItem("Afspraak voorstellen…");
        afspraakVanBerichtItem.Click += async (_, _) => await AfspraakVanBerichtAsync();
        berichtenMenu.Items.Add(afspraakVanBerichtItem);
        var regelItem = new ToolStripMenuItem("Regel maken van dit bericht…");
        regelItem.Click += (_, _) =>
        {
            // Voor mails (Gmail of Outlook), niet voor chats: afzender voorinvullen.
            if (GeselecteerdBericht() is { } b && (!b.IsChat || b.OutlookMail.Length > 0))
            {
                using var form = new ArchiveerRegelsForm(
                    b.VanAdres.Length > 0 && b.VanAdres != "CED Outlook" ? b.VanAdres : b.Van,
                    b.Onderwerp == "bericht" ? "" : b.Onderwerp);
                form.ShowDialog(this);
            }
        };
        berichtenMenu.Items.Add(regelItem);
        var teamTaakItem = new ToolStripMenuItem("Teamtaak maken…");
        teamTaakItem.Click += (_, _) => MaakTeamTaakVanBericht();
        berichtenMenu.Items.Add(teamTaakItem);
        var mailTimesheetItem = new ToolStripMenuItem("Timesheet maken…");
        mailTimesheetItem.Click += async (_, _) =>
        {
            if (GeselecteerdBericht() is not { } bericht)
            {
                return;
            }
            await MaakTimesheetAsync(
                bericht.OutlookMail.Length > 0 ? "CED" : null,
                DateOnly.FromDateTime(DateTime.Now),
                minuten: 30, bericht.Onderwerp, bron: "mail");
        };
        berichtenMenu.Items.Add(mailTimesheetItem);
        var driveItem = BijlagenNaarDrive.Submenu(async (id, naam) => await BijlagenNaarDriveAsync(id, naam));
        berichtenMenu.Items.Add(driveItem);
        // Bijlage(n) van een Gmail-mail (facturen) doorsturen naar het Billit-inboxadres —
        // dezelfde actie als in het mailvenster, zodat de cockpit de volledige werkplek blijft.
        var billitItem = new ToolStripMenuItem("Bijlage doorsturen naar Billit…");
        billitItem.Click += async (_, _) => await BillitDoorsturenAsync();
        berichtenMenu.Items.Add(billitItem);
        // Aparte handler: driveItem bestaat pas hier, en de eerste Opening-handler staat hierboven.
        berichtenMenu.Opening += (_, _) =>
        {
            var b = GeselecteerdBericht();
            // Beide acties lopen via IMAP op de Gmail-inbox: Smartschool-bijlagen kunnen
            // alleen via de chips in de berichtkop (verborgen schoolsessie).
            driveItem.Visible = b is { SmartschoolBericht.Length: 0 } &&
                BijlagenNaarDrive.HeeftBijlagen(b);
            billitItem.Visible = b is
                { IsChat: false, OutlookMail.Length: 0, SmartschoolBericht.Length: 0 } &&
                BijlagenNaarDrive.HeeftBijlagen(b);
        };
        var mailvensterItem = new ToolStripMenuItem("Openen in mailvenster…");
        mailvensterItem.Click += (_, _) => _openMail();
        berichtenMenu.Items.Add(mailvensterItem);
        _berichten.ContextMenuStrip = berichtenMenu;
        _berichten.SelectedIndexChanged += (_, _) => ToonDetail();
        _berichten.ShowItemToolTips = true;
        // Sneltoetsen in de lijst: Del/E = archiveren, S = snoozen, D = duim op een chat,
        // Enter = naar het antwoordvak.
        _berichten.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.D && e.Modifiers == Keys.None && IsChatBericht(GeselecteerdBericht()))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await DuimOpBerichtAsync();
            }
            else if (e.KeyCode is Keys.Delete or Keys.E)
            {
                e.Handled = true;
                await ArchiveerBerichtAsync();
            }
            else if (e.KeyCode == Keys.S)
            {
                e.Handled = true;
                await SnoozeBerichtAsync();
            }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.R)
            {
                // Enter of 'r' = naar het antwoordvak.
                e.Handled = true;
                e.SuppressKeyPress = true;
                _detailConcept.Focus();
            }
            else if (e.KeyCode is Keys.J or Keys.K)
            {
                // Vim-achtige navigatie: j = volgende, k = vorige.
                e.Handled = true;
                e.SuppressKeyPress = true;
                VerplaatsBerichtSelectie(e.KeyCode == Keys.J ? 1 : -1);
            }
            else if (e.KeyCode == Keys.T)
            {
                // 't' = vertaling aan/uit.
                e.Handled = true;
                e.SuppressKeyPress = true;
                await ToggleVertalingAsync();
            }
        };
        // Een bericht naar de takenlijst slepen = er een taak van maken.
        _berichten.ItemDrag += (_, e) =>
        {
            if (e.Item is ListViewItem { Tag: MailBericht m })
            {
                _berichten.DoDragDrop(new DataObject("wm-bericht", m), DragDropEffects.Copy);
            }
        };
        // Gekleurde bron-badges (Gmail/Chat/WhatsApp/Teams/Outlook) vóór de afzender.
        _berichten.RijIcoon = item => item.Tag is MailBericht m
            ? BronIconen.Voor(m switch
            {
                { TeamsChat.Length: > 0 } => "teams",
                { OutlookMail.Length: > 0 } => "outlook",
                { WhatsAppChat.Length: > 0 } => "whatsapp",
                { ChatSpace.Length: > 0 } => "chat",
                _ => "gmail",
            })
            : null;
        // Sorteren via de kolomkoppen (nogmaals klikken keert de volgorde om).
        _berichten.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortKolom)
            {
                _sortOplopend = !_sortOplopend;
            }
            else
            {
                _sortKolom = e.Column;
                _sortOplopend = true;
            }
            HervulBerichtenLijst();
        };
        // Filterbalk: bron/urgentie + vrije zoektekst.
        _bronFilter = new ComboBox { Width = 130, Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList };
        _bronFilter.Items.AddRange(new object[]
        {
            "Alle bronnen", "Gmail", "Google Chat", "WhatsApp", "Teams", "Outlook", "Urgent", "Focus",
        });
        _bronFilter.SelectedIndex = 0;
        _bronFilter.SelectedIndexChanged += (_, _) => HervulBerichtenLijst();
        _zoekFilter = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Zoeken in afzender, onderwerp of tekst…",
        };
        _zoekFilter.TextChanged += (_, _) => HervulBerichtenLijst();
        _zoekFilter.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _zoekFilter.Clear(); // Esc = filter wissen
            }
        };
        // Eigen verversknop voor alléén de berichten (sneller dan de volledige "Nu verversen").
        var berichtenVerversKnop = new ModernButton { Text = "⟳", Width = 44, Dock = DockStyle.Right };
        berichtenVerversKnop.Click += async (_, _) =>
        {
            if (_berichtenBezig)
            {
                // Niet stilletjes niets doen: de lopende beurt is met oudere gegevens
                // begonnen, dus na afloop alsnog een verse ophaalbeurt draaien — anders
                // leek de knop te werken terwijl de lijst oud bleef (2 sep 2026).
                Toast.Toon(this, "Berichten worden al opgehaald — daarna volgt meteen " +
                    "een verse beurt", Fluent.Klok);
            }
            berichtenVerversKnop.Bezig = true;
            berichtenVerversKnop.Enabled = false;
            try
            {
                while (_berichtenBezig && !IsDisposed)
                {
                    await Task.Delay(500, _cts.Token);
                }
                await VerversBerichtenAsync();
                Toast.Toon(this, $"Berichten ververst ({DateTime.Now:HH:mm})", Fluent.Mail);
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten.
            }
            finally
            {
                berichtenVerversKnop.Bezig = false;
                berichtenVerversKnop.Enabled = true;
            }
        };
        var filterPanel = new Panel { Dock = DockStyle.Bottom, Height = 39, Padding = new Padding(0, 8, 0, 0) };
        filterPanel.Controls.Add(_zoekFilter);
        filterPanel.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        filterPanel.Controls.Add(_bronFilter);
        filterPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        filterPanel.Controls.Add(berichtenVerversKnop);
        _berichtenGroup = new ModernGroupBox
        {
            Text = "Berichten (dubbelklik = beantwoorden)", Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };
        _berichtenGroup.Controls.Add(_berichten);
        _berichtenGroup.Controls.Add(filterPanel);
        _berichtenGroup.Accent = Theme.VoorBron("gmail"); // inbox: warm rood, per thema afgestemd

        // Detailpaneel rechts van de lijst: weergave zoals het mailscherm + antwoordvak.
        _detailConcept = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        // Ctrl+Enter = versturen, rechtstreeks vanuit het antwoordvak.
        _detailConcept.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await VerstuurDetailAsync();
            }
        };
        _claudeButton = new ModernButton { Text = "Claude-concept", Width = 165, Dock = DockStyle.Left };
        _claudeButton.Click += async (_, _) => await ClaudeConceptAsync();
        _verstuurButton = new ModernButton
        {
            Text = "Versturen", Width = 150, Dock = DockStyle.Right, Kind = ButtonKind.Accent,
        };
        _verstuurButton.Click += async (_, _) => await VerstuurDetailAsync();
        var archiveerKnop = new ModernButton { Text = "Archiveren", Width = 140, Dock = DockStyle.Right };
        archiveerKnop.Click += async (_, _) => await ArchiveerBerichtAsync();
        // Toont het Outlook-venster met de Archief-map open (terugbladeren of iets
        // terugslepen); het venster sluiten verbergt het gewoon weer. Bewust "Archiefmap"
        // genoemd én niet naast "Archiveren" geplaatst: die twee werden door elkaar gehaald,
        // waardoor het Outlook-venster onverwacht op het scherm kwam.
        var archiefKnop = new ModernButton { Text = "🗂 Archiefmap", Width = 138, Dock = DockStyle.Right };
        new ToolTip().SetToolTip(archiefKnop,
            "Opent de Archief-map in het Outlook-venster — om een eerder gearchiveerde " +
            "mail terug te vinden of terug te slepen naar Postvak IN.");
        archiefKnop.Click += async (_, _) =>
        {
            try
            {
                await OutlookClient.Instance.ToonArchiefAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Archief tonen mislukt: {ex.Message}", Fluent.Archive);
            }
        };
        var kopieerKnop = new ModernButton { Text = "Kopiëren", Width = 115, Dock = DockStyle.Right };
        kopieerKnop.Click += (_, _) =>
        {
            var tekst = _detailConcept.Text.Trim();
            if (tekst.Length == 0)
            {
                Toast.Toon(this, "Geen concepttekst om te kopiëren", Fluent.Edit);
                return;
            }
            Clipboard.SetText(tekst);
            Toast.Toon(this, "Concept gekopieerd — plak het in Outlook/Teams", Fluent.Edit);
        };
        _uitschrijfButton = new ModernButton
        {
            Text = "Uitschrijven", Width = 130, Dock = DockStyle.Left, Visible = false,
        };
        _uitschrijfButton.Click += (_, _) =>
        {
            if (_getoond is { UitschrijfUrl.Length: > 0 } m)
            {
                OpenExtern(m.UitschrijfUrl);
                Toast.Toon(this, "Afmeldpagina geopend in je browser", Fluent.Globe);
            }
        };
        _openButton = new ModernButton
        {
            Text = "Openen in browser", Width = 165, Dock = DockStyle.Left, Visible = false,
        };
        _openButton.Click += (_, _) => OpenBerichtInBrowser();
        _outlookLeesButton = new ModernButton
        {
            Text = "Volledige mail ophalen", Width = 175, Dock = DockStyle.Left, Visible = false,
        };
        _outlookLeesButton.Click += async (_, _) => await HaalOutlookMailAsync();
        _vertaalButton = new ModernButton
        {
            Text = "🌐 Vertaling", Width = 130, Dock = DockStyle.Left, Visible = false,
        };
        _vertaalButton.Click += async (_, _) => await ToggleVertalingAsync();

        // Bijsturen: feedback voor Claude om het concept aan te passen (Enter = toepassen).
        _detailFeedback = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "Bijsturen: feedback voor Claude (bv. \"korter\", \"vermeld dat ik " +
                "vrijdag vrij ben\") — Enter = toepassen",
        };
        _detailFeedback.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await PasConceptAanAsync();
            }
        };
        _feedbackButton = new ModernButton { Text = "Pas aan", Width = 110, Dock = DockStyle.Right };
        _feedbackButton.Click += async (_, _) => await PasConceptAanAsync();
        var feedbackPanel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        feedbackPanel.Controls.Add(_detailFeedback);
        feedbackPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        feedbackPanel.Controls.Add(_feedbackButton);

        // Eén nette rij die zo nodig naar een tweede regel wrapt: met losse links/rechts-
        // docking schoven de rechtse knoppen (Archiveren, Versturen) bij een smal paneel óver
        // de linkse heen. Verborgen knoppen nemen in een FlowLayoutPanel ook geen ruimte in,
        // dus er vallen geen gaten.
        var detailKnoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Padding = new Padding(0, 4, 0, 0),
        };
        foreach (var knop in new Control[]
        {
            _claudeButton, _openButton, _outlookLeesButton, _vertaalButton, _uitschrijfButton,
            archiefKnop, kopieerKnop, archiveerKnop, _verstuurButton,
        })
        {
            knop.Dock = DockStyle.None;
            knop.Margin = new Padding(0, 4, 8, 0);
            detailKnoppen.Controls.Add(knop);
        }
        // Kopstrip vlak boven het conceptvak: label + directe kopieerknop.
        var conceptKop = new Panel { Dock = DockStyle.Top, Height = 30 };
        var conceptLabel = new Label
        {
            Text = "Claude-concept", Dock = DockStyle.Left, AutoSize = true,
            Padding = new Padding(2, 6, 0, 0),
        };
        Theme.AsStatus(conceptLabel);
        var conceptKopieerKnop = new ModernButton { Text = "📋 Kopieer", Width = 110, Dock = DockStyle.Right };
        conceptKopieerKnop.Click += (_, _) =>
        {
            var tekst = _detailConcept.Text.Trim();
            if (tekst.Length == 0)
            {
                Toast.Toon(this, "Geen concepttekst om te kopiëren", Fluent.Edit);
                return;
            }
            Clipboard.SetText(tekst);
            Toast.Toon(this, "Concept gekopieerd", Fluent.Copy);
        };
        conceptKop.Controls.Add(conceptLabel);
        conceptKop.Controls.Add(conceptKopieerKnop);

        // Het hele antwoordblok (conceptvak, bijsturen, knoppenrij) hoort bij een bericht.
        // Staat er geen bericht in het detailpaneel — een taak zonder bron, een meeting of
        // niets geselecteerd — dan verdwijnt het blok en krijgt de weergave de volle hoogte.
        _conceptPanel = new Panel
        {
            Dock = DockStyle.Bottom, Height = 264, Padding = new Padding(0, 8, 0, 0), Visible = false,
        };
        var conceptPanel = _conceptPanel;
        conceptPanel.Controls.Add(_detailConcept);
        conceptPanel.Controls.Add(feedbackPanel);
        conceptPanel.Controls.Add(detailKnoppen);
        conceptPanel.Controls.Add(conceptKop);
        var detailGroup = new ModernGroupBox
        {
            Text = "Bericht en antwoord", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        detailGroup.Controls.Add(_detail);
        detailGroup.Controls.Add(conceptPanel);
        detailGroup.Accent = Theme.Accent; // antwoordvak: het huisaccent

        var bovenSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400,
        };
        bovenSplit.Panel1.Controls.Add(_berichtenGroup);
        bovenSplit.Panel2.Controls.Add(detailGroup);
        // Bij het tonen (venster is dan gemaximaliseerd): lijst en detailpaneel elk de helft,
        // en de kolommen op de definitieve breedtes zetten. Daarna blijft de splitter sleepbaar.
        Shown += (_, _) =>
        {
            bovenSplit.SplitterDistance = Math.Max(350, bovenSplit.ClientSize.Width / 2);
            SchaalAlleKolommen();
        };

        // Onder links: taken
        _taken = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Geen open taken 🎉",
            LeegSoort = "taken",
            LeegGlyph = Fluent.Checkbox,
            CheckBoxes = true, // aanvinken = afwerken; de undo-toast vangt een misklik op
        };
        _taken.ShowItemToolTips = true; // volledige omschrijving bij het zweven over een rij
        // Vooruitblik- en gepland-rijen (start later / komt uit snooze) zijn geen echt werk:
        // geen checkbox.
        _taken.HeeftCheckbox = item =>
            item.Tag is TaakRij r && r.Bron is not ("Later" or "Snooze" or "Gepland");
        _taken.MouseDown += (_, e) => _negeerTaakCheck = e.Clicks > 1;
        _taken.ItemCheck += (_, e) =>
        {
            // Bij dubbelklik (= bewerken) niets toggelen; vooruitblik-rijen hebben geen
            // checkbox en dus ook geen toggle. (Tijdens het vullen mag het wél: zo krijgen
            // afgevinkte rijen hun vinkje; ItemChecked negeert dat via _takenLaden.)
            if (_takenLaden)
            {
                return;
            }
            if (_negeerTaakCheck ||
                (_taken.Items.Count > e.Index && _taken.Items[e.Index].Tag is TaakRij { Bron: "Later" or "Snooze" or "Gepland" }))
            {
                e.NewValue = e.CurrentValue;
                _negeerTaakCheck = false;
            }
        };
        _taken.ItemChecked += async (_, e) =>
        {
            if (_takenLaden || e.Item.Tag is not TaakRij rij)
            {
                return;
            }
            // Uitvinken van een afgevinkte taak = terugzetten op de open lijst.
            if (rij is { Bron: "Klaar", Lokaal: { } terug })
            {
                if (!e.Item.Checked)
                {
                    var data = MijnTaakStore.Load();
                    if (data.Taken.FirstOrDefault(t => t.Id == terug.Id) is { } t)
                    {
                        t.Klaar = false;
                        t.KlaarOp = null;
                        MijnTaakStore.Save(data);
                    }
                    Toast.Toon(this, $"Teruggezet: {Kort(rij.Tekst, 40)}", Fluent.Checkbox);
                    await VerversTakenAsync();
                }
                return;
            }
            if (e.Item.Checked)
            {
                await VinkRijAfAsync(e.Item, rij);
            }
        };
        _taken.Columns.Add("Taak", 380);
        _taken.Columns.Add("Deadline", 110);
        _taken.Columns.Add("Bron", 90);
        // Slepen naar de takenlijst: een bericht wordt een taak (met de mail eraan), een
        // bestand uit de Verkenner wordt een taak met het pad als link.
        _taken.AllowDrop = true;
        // In dagplan-modus kun je de volgorde direct hier verslepen; de wijziging gaat naar
        // het dagplan zelf, zodat venster, focusbalk en "▶ NU:" meteen meeschuiven.
        _taken.ItemDrag += (_, e) =>
        {
            if (_sorteerOpPlan &&
                e.Item is ListViewItem { Tag: TaakRij { Lokaal: not null, Bron: not ("Later" or "Snooze" or "Klaar" or "Gepland") } } rij)
            {
                _taken.DoDragDrop(new DataObject("wm-taakrij", rij), DragDropEffects.Move);
            }
        };
        _taken.DragEnter += (_, e) =>
        {
            e.Effect = e.Data is { } d
                ? d.GetDataPresent("wm-taakrij") ? DragDropEffects.Move
                : d.GetDataPresent("wm-bericht") || d.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None
                : DragDropEffects.None;
        };
        _taken.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData("wm-taakrij") is ListViewItem { Tag: TaakRij versleept })
            {
                var punt = _taken.PointToClient(new Point(e.X, e.Y));
                var doel = _taken.GetItemAt(punt.X, punt.Y)?.Tag as TaakRij;
                VerplaatsTaakInPlan(versleept, doel);
                return;
            }
            if (e.Data?.GetData("wm-bericht") is MailBericht bericht)
            {
                await TaakVanBerichtAsync(bericht);
                return;
            }
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paden && paden.Length > 0)
            {
                var data = MijnTaakStore.Load();
                foreach (var pad in paden)
                {
                    data.Taken.Add(new MijnTaak
                    {
                        Tekst = $"Verwerken: {Path.GetFileName(pad)}",
                        Categorie = data.Categorieen.FirstOrDefault() ?? "",
                        Deadline = DateOnly.FromDateTime(DateTime.Now),
                        Mail = new TaakMail { Onderwerp = Path.GetFileName(pad), Link = pad },
                    });
                }
                MijnTaakStore.Save(data);
                Toast.Toon(this, paden.Length == 1
                    ? $"Taak gemaakt voor {Path.GetFileName(paden[0])}"
                    : $"{paden.Length} taken gemaakt", Fluent.Checkbox);
                await VerversTakenAsync();
            }
        };
        // Klein logo vóór herkenbare taken: AH-boodschappen, de wekelijkse afvaltaak en de
        // update-taken (Claude Code CLI / PhpStorm) krijgen hun eigen productlogo.
        _taken.RijIcoon = item => item.Tag is TaakRij r
            ? r.Tekst.Contains("Albert Heijn", StringComparison.OrdinalIgnoreCase)
                ? BronIconen.Voor("ah")
                : r.Tekst.StartsWith("Afvalbakken buitenzetten", StringComparison.OrdinalIgnoreCase)
                    ? BronIconen.Voor("afval")
                    : r.Tekst.StartsWith("Claude bijwerken", StringComparison.OrdinalIgnoreCase)
                        ? BronIconen.Voor("claude")
                        : r.Tekst.StartsWith("PhpStorm bijwerken", StringComparison.OrdinalIgnoreCase)
                            ? BronIconen.Voor("phpstorm")
                            : null
            : null;
        _taken.DoubleClick += async (_, _) =>
        {
            // De AH-taak opent de bestelflow (gerechten kiezen → ah.be); afvinken kan via rechtsklik.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij rij &&
                rij.Tekst.Contains("Albert Heijn", StringComparison.OrdinalIgnoreCase))
            {
                using var ah = new AhBestelForm();
                ah.ShowDialog(this);
                return;
            }
            // De Aqurat-presentatietaak opent Claude Desktop met de opdracht op het klembord.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij presRij &&
                presRij.Tekst.StartsWith(PresentatieTaken.TaakPrefix, StringComparison.OrdinalIgnoreCase))
            {
                PresentatieTaken.OpenClaudeDesktop(presRij.Tekst);
                Toast.Toon(this, "Claude Desktop geopend — plak de opdracht met Ctrl+V", Fluent.Ster);
                return;
            }
            // Een taak van de cadeauradar opent het verjaardagsvenster (ideeën, geschiedenis).
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij cadeauRij &&
                Verjaardagen.IsRadarTaak(cadeauRij.Tekst))
            {
                var verjaardagen = new VerjaardagenForm();
                verjaardagen.Show(this);
                return;
            }
            // De maandelijkse Bermacon-factuurtaak opent Billit; afvinken via rechtsklik.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij bermaconRij &&
                bermaconRij.Tekst.Contains(VasteTaken.BermaconTaak, StringComparison.OrdinalIgnoreCase))
            {
                OpenExtern("https://my.billit.be/");
                Toast.Toon(this, "Billit geopend — maak de Bermacon-factuur op", Fluent.Globe);
                return;
            }
            // De git-opruimtaak opent het statusvenster van het drukste project.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij gitRij &&
                gitRij.Tekst.StartsWith(GitTaken.TaakPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (GitTaken.EersteProjectUit(gitRij.Tekst) is { } projectMap)
                {
                    using var gitForm = new GitStatusForm(
                        projectMap, projectMap.TrimEnd('\\', '/').Split('\\', '/').Last());
                    gitForm.ShowDialog(this);
                }
                return;
            }
            // De automatische opruimtaak opent de bureaubladcleaner.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij bureaubladRij &&
                bureaubladRij.Tekst.StartsWith(VasteTaken.BureaubladTaak, StringComparison.OrdinalIgnoreCase))
            {
                var cleaner = new BureaubladCleanerForm();
                cleaner.FormClosed += async (_, _) =>
                {
                    // Eerst de taak gelijkzetten met wat er nu op het bureaublad staat,
                    // dan pas de lijst hertekenen — anders zie je het oude aantal nog.
                    VasteTaken.WerkBureaubladTaakBij();
                    await VerversTakenAsync();
                };
                cleaner.Show(this);
                return;
            }
            // Update-taken (van UpdateCheck) voeren de update meteen uit.
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij updateRij &&
                updateRij.Tekst.StartsWith("Claude bijwerken", StringComparison.OrdinalIgnoreCase))
            {
                Toast.Toon(this, "Claude bijwerken gestart…", Fluent.Sync);
                var (huidig, nieuw, melding) = await UpgradeClaudeAsync();
                // Echt bijgewerkt (of bleek al up-to-date): taak afvinken.
                if (nieuw.Length > 0 && (huidig != nieuw || melding.Contains("up-to-date")))
                {
                    UpdateCheck.VinkTaakAf("Claude bijwerken");
                    await VerversTakenAsync();
                }
                Toast.Toon(this, melding, Fluent.Sync);
                return;
            }
            if (_taken.SelectedItems.Count > 0 && _taken.SelectedItems[0].Tag is TaakRij psRij &&
                psRij.Tekst.StartsWith("PhpStorm bijwerken", StringComparison.OrdinalIgnoreCase))
            {
                await StartPhpStormUpdateAsync(psRij);
                return;
            }
            // Gewone taak: dubbelklik opent de bewerk-dialoog (ook voor Asana-taken —
            // BewerkTaakAsync kiest zelf de juiste dialoog). Afvinken blijft bewust op de
            // rechtsklik (te snel per ongeluk).
            if (_taken.SelectedItems.Count > 0 &&
                _taken.SelectedItems[0].Tag is TaakRij)
            {
                await BewerkTaakAsync();
                return;
            }
            await Task.CompletedTask;
        };
        var takenMenu = new ContextMenuStrip();
        Theme.Style(takenMenu);
        var nieuweTaakItem = new ToolStripMenuItem("Nieuwe taak…");
        nieuweTaakItem.Click += async (_, _) => await MaakNieuweTaakAsync();
        takenMenu.Items.Add(nieuweTaakItem);
        takenMenu.Items.Add(new ToolStripSeparator());
        var afvinkItem = new ToolStripMenuItem("Afvinken");
        afvinkItem.Click += async (_, _) => await VinkTaakAfAsync();
        takenMenu.Items.Add(afvinkItem);
        var bewerkItem = new ToolStripMenuItem("Taak bewerken…");
        bewerkItem.Click += async (_, _) => await BewerkTaakAsync();
        takenMenu.Items.Add(bewerkItem);
        var verzetItem = new ToolStripMenuItem("Deadline verzetten…");
        verzetItem.Click += async (_, _) => await VerzetTaakDeadlineAsync();
        takenMenu.Items.Add(verzetItem);
        // De twee gewone gevallen zonder dialoog: meteen naar morgen of overmorgen.
        var morgenItem = new ToolStripMenuItem("Verzet naar morgen");
        morgenItem.Click += async (_, _) => await VerzetTaakSnelAsync(1);
        takenMenu.Items.Add(morgenItem);
        var overmorgenItem = new ToolStripMenuItem("Verzet naar overmorgen");
        overmorgenItem.Click += async (_, _) => await VerzetTaakSnelAsync(2);
        takenMenu.Items.Add(overmorgenItem);
        // Vervolg op de uitstel-por: bij een 🙈-taak (3+ keer uitgesteld) drie uitwegen —
        // kleiner maken, weggeven of gewoon toegeven dat hij nooit gaat gebeuren.
        var uitstelMenu = new ToolStripMenuItem("🙈 Vaak uitgesteld");
        var blokjesItem = new ToolStripMenuItem("In blokjes hakken (Claude)");
        blokjesItem.Click += (_, _) =>
        {
            if (GeselecteerdeLokaleTaak() is { } t)
            {
                _ = HakTaakInBlokjesAsync(t.Id);
            }
        };
        uitstelMenu.DropDownItems.Add(blokjesItem);
        var naarTeamItem = new ToolStripMenuItem("Omzetten naar teamtaak…");
        naarTeamItem.Click += (_, _) => ZetTaakOmNaarTeamtaak();
        uitstelMenu.DropDownItems.Add(naarTeamItem);
        var schrapItem = new ToolStripMenuItem("Taak schrappen…");
        schrapItem.Click += async (_, _) => await SchrapTaakAsync();
        uitstelMenu.DropDownItems.Add(schrapItem);
        takenMenu.Items.Add(uitstelMenu);
        // Snel boeken: één klik zet 20 minuten op de klant die bij de taakcategorie hoort.
        var timesheetSnelItem = new ToolStripMenuItem("Timesheet 20 min");
        timesheetSnelItem.Click += async (_, _) => await BoekTaakTimesheetAsync(20, vraag: false);
        takenMenu.Items.Add(timesheetSnelItem);
        var timesheetItem = new ToolStripMenuItem("Timesheet…");
        timesheetItem.Click += async (_, _) => await BoekTaakTimesheetAsync(20, vraag: true);
        takenMenu.Items.Add(timesheetItem);
        // De timer: starten op de geselecteerde taak; stoppen boekt de echte verstreken tijd.
        var timerItem = new ToolStripMenuItem($"⏱ {char.ToUpperInvariant(ThemaStem.TimerNaam()[0])}"
            + $"{ThemaStem.TimerNaam()[1..]} starten");
        timerItem.Click += async (_, _) => await ToggleTimerAsync();
        takenMenu.Items.Add(timerItem);
        var taakSnoozeItem = new ToolStripMenuItem("Snooze (tijdelijk verbergen)");
        takenMenu.Items.Add(taakSnoozeItem);
        var taakMailItem = new ToolStripMenuItem("Bron openen in browser");
        taakMailItem.Click += (_, _) =>
        {
            if (_taken.SelectedItems.Count > 0 &&
                _taken.SelectedItems[0].Tag is TaakRij { Lokaal: { } lokaal } &&
                BepaalTaakBron(lokaal) is { Link.Length: > 0 } bron)
            {
                OpenExtern(bron.Link);
            }
        };
        takenMenu.Items.Add(taakMailItem);
        takenMenu.Opening += (_, _) =>
        {
            var lokaal = _taken.SelectedItems.Count > 0 &&
                _taken.SelectedItems[0].Tag is TaakRij { Lokaal: { } l } ? l : null;
            taakSnoozeItem.Enabled = lokaal is not null;
            uitstelMenu.Visible = lokaal is { UitstelTeller: >= 3 };
            taakSnoozeItem.DropDownItems.Clear();
            foreach (var (label, moment) in SnoozePresets())
            {
                var mi = new ToolStripMenuItem(label);
                mi.Click += (_, _) => SnoozeTaak(moment);
                taakSnoozeItem.DropDownItems.Add(mi);
            }
            // De klant staat meteen in het menu-item, zodat je vóór het klikken ziet waarop
            // geboekt wordt. Zonder duidelijke klant valt het terug op de dialoog.
            var klant = lokaal is null ? null : KlantVoorCategorie(lokaal.Categorie);
            timesheetSnelItem.Enabled = klant is not null;
            timesheetSnelItem.Text = klant is null
                ? "Timesheet 20 min (klant onbekend)"
                : $"Timesheet 20 min — {klant}";
            // Timer-item: tonen wat er loopt, of aanbieden te starten op de selectie.
            if (TaakTimer.Huidig() is { } lopend)
            {
                timerItem.Text = $"⏹ Timer stoppen en boeken ({lopend.Ruw} min — {Kort(lopend.Tekst, 30)})";
                timerItem.Enabled = true;
            }
            else
            {
                timerItem.Text = "⏱ Timer starten";
                timerItem.Enabled = _taken.SelectedItems.Count > 0 &&
                    _taken.SelectedItems[0].Tag is TaakRij { Bron: not ("Later" or "Snooze" or "Gepland") };
            }
        };
        _taken.ContextMenuStrip = takenMenu;
        _taken.SelectedIndexChanged += (_, _) => ToonTaakMail();
        // Alle weergave-instellingen (deadline-horizon, volgorde, extra rijen) achter één
        // knop met vinkjes. Eerder stonden ze als vier losse knoppen tussen de actieknoppen
        // en was niet te zien wat een actie was, wat een instelling, en wat er aan stond.
        var weergaveMenu = new ContextMenuStrip();
        Theme.Style(weergaveMenu);
        var weergaveKnop = new ModernButton { Width = 200 };
        var volgordeDeadlineItem = new ToolStripMenuItem("Volgorde: deadline");
        var volgordePlanItem = new ToolStripMenuItem("Volgorde: dagplanning");
        var afgevinkteItem = new ToolStripMenuItem("Afgevinkte tonen (laatste 2 weken)");
        var geplandItem = new ToolStripMenuItem("Geplande en gesnoozde tonen");
        var horizonItems = new List<(ToolStripMenuItem Item, int? Dagen)>();
        foreach (var (label, dagen) in new (string, int?)[]
                 {
                     ("Deadline ≤ 2 dagen", 2),
                     ("Deadline ≤ 7 dagen", 7),
                     ("Deadline ≤ 14 dagen", 14),
                     ("Alle deadlines", null),
                 })
        {
            var keuzeDagen = dagen;
            var keuze = new ToolStripMenuItem(label);
            keuze.Click += (_, _) =>
            {
                _takenHorizon = keuzeDagen;
                WerkWeergaveKnopBij();
                VulTakenLijst();
            };
            weergaveMenu.Items.Add(keuze);
            horizonItems.Add((keuze, dagen));
        }
        weergaveMenu.Items.Add(new ToolStripSeparator());
        // Sorteerkeuze: op deadline (klassiek) of in de volgorde van de dagplanning.
        volgordeDeadlineItem.Click += (_, _) =>
        {
            _sorteerOpPlan = false;
            WerkWeergaveKnopBij();
            VulTakenLijst();
        };
        volgordePlanItem.Click += (_, _) =>
        {
            _sorteerOpPlan = true;
            if (DagPlan.LaadVandaag() is null)
            {
                Toast.Toon(this, "Nog geen dagplanning — maak er één via de knop Dagplanning", Fluent.Ster);
            }
            WerkWeergaveKnopBij();
            VulTakenLijst();
        };
        weergaveMenu.Items.Add(volgordeDeadlineItem);
        weergaveMenu.Items.Add(volgordePlanItem);
        weergaveMenu.Items.Add(new ToolStripSeparator());
        // Afgevinkte taken van de laatste twee weken tonen — om iets terug te vinden of
        // (via het vinkje uitzetten) terug op de lijst te zetten.
        afgevinkteItem.Click += (_, _) =>
        {
            _toonAfgevinkte = !_toonAfgevinkte;
            WerkWeergaveKnopBij();
            VulTakenLijst();
        };
        weergaveMenu.Items.Add(afgevinkteItem);
        // Gesnoozde en nog-niet-gestarte taken onderaan tonen — dezelfde blik vooruit als
        // "Gesnoozed/gepland tonen" in het venster Mijn taken.
        geplandItem.Click += (_, _) =>
        {
            _toonGepland = !_toonGepland;
            WerkWeergaveKnopBij();
            VulTakenLijst();
        };
        weergaveMenu.Items.Add(geplandItem);
        // Vinkjes en knoptekst gelijk houden met de echte stand; de knop vat samen wat je
        // nu ziet ("Weergave: ≤ 2 dgn", "· plan" bij dagplanvolgorde, "+" bij extra rijen).
        void WerkWeergaveKnopBij()
        {
            foreach (var (item, dagen) in horizonItems)
            {
                item.Checked = _takenHorizon == dagen;
            }
            volgordeDeadlineItem.Checked = !_sorteerOpPlan;
            volgordePlanItem.Checked = _sorteerOpPlan;
            afgevinkteItem.Checked = _toonAfgevinkte;
            geplandItem.Checked = _toonGepland;
            weergaveKnop.Text = (_takenHorizon is { } h ? $"Weergave: ≤ {h} dgn" : "Weergave: alles")
                + (_sorteerOpPlan ? " · plan" : "")
                + (_toonAfgevinkte || _toonGepland ? " +" : "") + " ▾";
        }
        WerkWeergaveKnopBij();
        weergaveKnop.Click += (_, _) =>
            weergaveMenu.Show(weergaveKnop, new Point(0, weergaveKnop.Height + 4));
        // Wrappende knoppenbalk: past de rij niet meer (het takenpaneel is maar half zo
        // breed als het venster), dan komt er een tweede regel bij in plaats van dat de
        // links- en rechtsgedockte knoppen over elkaar heen schuiven.
        var horizonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 5, 0, 0),
        };
        // Compact: "+" is genoeg — de balk moet in het halve venster passen.
        var nieuweTaakKnop = new ModernButton
        {
            Text = "+", Width = 44,
        };
        nieuweTaakKnop.Click += async (_, _) => await MaakNieuweTaakAsync();
        new ToolTip().SetToolTip(nieuweTaakKnop, "Nieuwe taak");
        // Rechtstreeks een teamtaak ingeven, zonder eerst het Taken team-venster te openen.
        var nieuweTeamTaakKnop = new ModernButton
        {
            Text = "+ team", Width = 80,
        };
        nieuweTeamTaakKnop.Click += (_, _) => MaakNieuweTeamTaak();
        new ToolTip().SetToolTip(nieuweTeamTaakKnop, "Nieuwe teamtaak");
        // Vooruitblik: de vijf taken die als eerste op je afkomen, inclusief de taken die nu
        // nog verborgen zijn omdat hun startdatum later ligt of omdat ze gesnoozed zijn.
        var anticipeerKnop = new ModernButton
        {
            Text = "Anticiperen", Width = 130, Glyph = Fluent.Ster,
        };
        anticipeerKnop.Click += async (_, _) =>
        {
            using var vooruit = new AnticipeerForm(BewerkLokaleTaakAsync);
            vooruit.ShowDialog(this);
            await VerversTakenAsync(); // naar voren gehaalde taken meteen in de lijst
        };
        // Acties links (toevoegen, vooruitblik), de ene weergave-knop rechts daarvan.
        horizonPanel.Controls.Add(nieuweTaakKnop);
        horizonPanel.Controls.Add(nieuweTeamTaakKnop);
        horizonPanel.Controls.Add(anticipeerKnop);
        horizonPanel.Controls.Add(weergaveKnop);
        _takenGroup = new ModernGroupBox
        {
            Text = "Open taken (afvinken via rechtsklik)", Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };
        _takenGroup.Controls.Add(_taken);
        _takenGroup.Controls.Add(horizonPanel);
        _takenGroup.Accent = Theme.Success; // taken: groen, dat is afwerken

        // Onder rechts: meetings (vandaag, met knop om naar morgen te kijken)
        _meetings = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Geen meetings 🎉",
            LeegSoort = "meetings",
            LeegGlyph = Fluent.Kalender,
        };
        _meetings.Columns.Add("Tijd", 120);
        _meetings.Columns.Add("Meeting", 330);
        var meetingsMenu = new ContextMenuStrip();
        Theme.Style(meetingsMenu);
        var deelnemenItem = new ToolStripMenuItem("Deelnemen (online)");
        deelnemenItem.Click += (_, _) => DeelnemenAanMeeting();
        meetingsMenu.Items.Add(deelnemenItem);
        var nieuweAfspraakItem = new ToolStripMenuItem("Nieuwe afspraak…");
        nieuweAfspraakItem.Click += async (_, _) => await NieuweAfspraakAsync();
        meetingsMenu.Items.Add(nieuweAfspraakItem);
        var bewerkAfspraakItem = new ToolStripMenuItem("Afspraak bewerken…");
        bewerkAfspraakItem.Click += async (_, _) => await BewerkAfspraakAsync();
        meetingsMenu.Items.Add(bewerkAfspraakItem);
        var verwijderAfspraakItem = new ToolStripMenuItem("Afspraak verwijderen…");
        verwijderAfspraakItem.Click += async (_, _) => await VerwijderAfspraakAsync();
        meetingsMenu.Items.Add(verwijderAfspraakItem);
        // Dezelfde snelverzetters als bij taken: één klik, zelfde uur, andere dag.
        var afspraakMorgenItem = new ToolStripMenuItem("Verzet naar morgen");
        afspraakMorgenItem.Click += async (_, _) => await VerzetAfspraakSnelAsync(1);
        meetingsMenu.Items.Add(afspraakMorgenItem);
        var afspraakOvermorgenItem = new ToolStripMenuItem("Verzet naar overmorgen");
        afspraakOvermorgenItem.Click += async (_, _) => await VerzetAfspraakSnelAsync(2);
        meetingsMenu.Items.Add(afspraakOvermorgenItem);
        // Voor afspraken die je hier niet kunt bewerken (uitnodigingen, herhalend, CED):
        // lokaal markeren dat je er gewoon bij kunt doorwerken — de dagplanner plant er dan
        // werk overheen in plaats van eromheen.
        var werkbaarItem = new ToolStripMenuItem("Blokkeert mijn agenda niet");
        werkbaarItem.Click += (_, _) =>
        {
            if (_meetings.SelectedItems.Count == 0 ||
                _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem wm)
            {
                return;
            }
            var aan = WerkbaarStore.Wissel(wm);
            Toast.Toon(this, aan
                ? "Genoteerd: hier kun je doorwerken — de dagplanner plant er werk overheen"
                : "Blokkeert weer gewoon je agenda", Fluent.Klok);
            WerkDagPlanBij(_laatsteBerichten); // ankers in het dagplan meteen bijwerken
            ToonMeetingDetail();
        };
        meetingsMenu.Items.Add(werkbaarItem);
        meetingsMenu.Items.Add(new ToolStripSeparator());
        meetingsMenu.Opening += (_, _) =>
        {
            var geselecteerd = _meetings.SelectedItems.Count > 0
                ? _meetings.SelectedItems[0].Tag as AgendaClient.AgendaItem : null;
            deelnemenItem.Visible = geselecteerd is { } mm && MeetingLink(mm) is not null;
            // Bewerken/verwijderen alleen voor de eigen Google-agenda, inclusief de
            // recept-afspraken (CED/Hilke zijn hier alleen-lezen).
            bewerkAfspraakItem.Visible = geselecteerd is not null &&
                _meetings.SelectedItems[0].Name is "gagenda" or "recept";
            verwijderAfspraakItem.Visible = bewerkAfspraakItem.Visible;
            afspraakMorgenItem.Visible = afspraakOvermorgenItem.Visible = bewerkAfspraakItem.Visible;
            werkbaarItem.Enabled = geselecteerd is not null;
            werkbaarItem.Checked = geselecteerd is { } wb && DagPlan.KanDoorwerken(wb);
        };
        // Dubbelklik op een online meeting = meteen deelnemen.
        _meetings.DoubleClick += (_, _) => DeelnemenAanMeeting();
        // Del = geselecteerde afspraak verwijderen (zelfde pad en bevestiging als het
        // rechtsklikmenu; doet niets op alleen-lezen rijen zoals CED/Hilke).
        _meetings.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                await VerwijderAfspraakAsync();
            }
        };
        // Snoozen met dezelfde presets als bij berichten: één klik voor de gangbare
        // momenten, de dialoog alleen nog voor een afwijkende datum.
        var meetingSnoozeItem = new ToolStripMenuItem("Snoozen");
        meetingsMenu.Items.Add(meetingSnoozeItem);
        meetingsMenu.Opening += (_, _) =>
        {
            meetingSnoozeItem.DropDownItems.Clear();
            foreach (var (label, moment) in SnoozePresets())
            {
                var mi = new ToolStripMenuItem(label);
                mi.Click += async (_, _) => await SnoozeMeetingAsync(moment);
                meetingSnoozeItem.DropDownItems.Add(mi);
            }
            meetingSnoozeItem.DropDownItems.Add(new ToolStripSeparator());
            var kies = new ToolStripMenuItem("Kies datum…");
            kies.Click += async (_, _) => await SnoozeMeetingAsync();
            meetingSnoozeItem.DropDownItems.Add(kies);
        };
        var meetingTimesheetItem = new ToolStripMenuItem("Timesheet maken…");
        meetingTimesheetItem.Click += async (_, _) =>
        {
            if (_meetings.SelectedItems.Count == 0 ||
                _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem meeting)
            {
                return;
            }
            var titel = meeting.Titel
                .Replace("CED · ", "", StringComparison.Ordinal)
                .Replace("Hilke · ", "", StringComparison.Ordinal);
            await MaakTimesheetAsync(
                _meetings.SelectedItems[0].Name == "outlook" ? "CED" : null,
                DateOnly.FromDateTime(meeting.Start.LocalDateTime),
                Math.Max(5, (int)(meeting.Einde - meeting.Start).TotalMinutes),
                titel, bron: "meeting",
                van: TimeOnly.FromDateTime(meeting.Start.LocalDateTime));
        };
        meetingsMenu.Items.Add(meetingTimesheetItem);
        _meetings.ContextMenuStrip = meetingsMenu;
        _meetings.SelectedIndexChanged += (_, _) => ToonMeetingDetail();
        // Agenda-badge per rij: Google Agenda, CED (Outlook), Hilke of recept (item.Name).
        // Meetings met een videolink tonen het Teams/Meet-logo — klikken op dat icoontje
        // opent de vergadering meteen.
        _meetings.RijIcoon = item =>
        {
            var join = MeetingJoinUrl(item);
            if (join.Contains("meet.google", StringComparison.OrdinalIgnoreCase))
            {
                return BronIconen.Voor("meet");
            }
            if (join.Length > 0)
            {
                return BronIconen.Voor("teams");
            }
            return item.Name.Length > 0 ? BronIconen.Voor(item.Name) : null;
        };
        _meetings.MouseClick += (_, e) =>
        {
            if (_meetings.HitTest(e.Location).Item is not { } meetingRij)
            {
                return;
            }
            var vak = meetingRij.GetBounds(ItemBoundsPortion.Entire);
            if (e.X < vak.X + 8 || e.X > vak.X + 8 + 18)
            {
                return; // alleen een klik op het icoontje zelf joint de meeting
            }
            var join = MeetingJoinUrl(meetingRij);
            if (join.Length > 0)
            {
                OpenExtern(join);
            }
        };
        _meetingsGroup = new ModernGroupBox
        {
            Text = "Meetings vandaag", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        var vorigeDag = new ModernButton { Text = "◀", Width = 45, Dock = DockStyle.Left };
        var vandaagKnop = new ModernButton { Text = "Vandaag", Width = 100, Dock = DockStyle.Left };
        var volgendeDag = new ModernButton { Text = "▶", Width = 45, Dock = DockStyle.Left };
        vorigeDag.Click += async (_, _) => await MeetingsNaarDagAsync(_meetingsOffset - 1);
        vandaagKnop.Click += async (_, _) => await MeetingsNaarDagAsync(0);
        volgendeDag.Click += async (_, _) => await MeetingsNaarDagAsync(_meetingsOffset + 1);
        // Voorbije afspraken van vandaag standaard verbergen; de knop toont ze weer.
        var voorbijeKnop = new ModernButton { Text = "Voorbije tonen", Width = 145, Dock = DockStyle.Right };
        voorbijeKnop.Click += async (_, _) =>
        {
            _toonVoorbije = !_toonVoorbije;
            voorbijeKnop.Text = _toonVoorbije ? "Voorbije verbergen" : "Voorbije tonen";
            await VerversMeetingsAsync(forceer: false);
        };
        // Hilkes agenda aan/uit (de keuze blijft bewaard in de agenda-instellingen).
        var hilkeKnop = new ModernButton
        {
            Text = AgendaSettings.Load().HilkeTonen ? "Hilke ✓" : "Hilke", Width = 90,
            Dock = DockStyle.Right,
        };
        hilkeKnop.Click += async (_, _) =>
        {
            var agenda = AgendaSettings.Load();
            agenda.HilkeTonen = !agenda.HilkeTonen;
            agenda.Save();
            hilkeKnop.Text = agenda.HilkeTonen ? "Hilke ✓" : "Hilke";
            await VerversMeetingsAsync(forceer: false);
        };
        // Weersvoorspelling voor de getoonde dag: op dezelfde regel als de dag-navigatie,
        // in de ruimte tussen "Vandaag ◀ ▶" en de knoppen rechts. Een eigen regel eronder
        // kostte hoogte die de meetinglijst beter kan gebruiken.
        _weerLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 8, 0),
            Font = new Font("Segoe UI Emoji", 10.5f),
            AutoEllipsis = true,
            Visible = false,
        };
        Theme.AsStatus(_weerLabel);
        // Volgorde: Vandaag ◀ ▶ (laatst toegevoegd dockt het meest links); het weerlabel
        // vult wat er tussen overblijft en moet daarom als eerste toegevoegd worden.
        var morgenPanel = new Panel { Dock = DockStyle.Bottom, Height = 39, Padding = new Padding(0, 8, 0, 0) };
        morgenPanel.Controls.Add(_weerLabel);
        morgenPanel.Controls.Add(voorbijeKnop);
        morgenPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        morgenPanel.Controls.Add(hilkeKnop);
        morgenPanel.Controls.Add(volgendeDag);
        morgenPanel.Controls.Add(vorigeDag);
        morgenPanel.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        morgenPanel.Controls.Add(vandaagKnop);
        _meetingsGroup.Controls.Add(_meetings);
        _meetingsGroup.Controls.Add(morgenPanel);
        // Volgende-meeting-balk: zodra een meeting binnen het uur begint (of bezig is)
        // verschijnt hij prominent boven de lijst, met de videolink als één-klik-knop.
        _volgendeMeetingLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.SemiBold,
            Padding = new Padding(8, 0, 0, 0),
        };
        _deelnemenKnop = new ModernButton
        {
            Text = "Deelnemen", Width = 118, Dock = DockStyle.Right, Kind = ButtonKind.Accent,
        };
        _deelnemenKnop.Click += (_, _) =>
        {
            if (_deelnemenKnop?.Tag is string url && url.Length > 0)
            {
                OpenExtern(url);
            }
        };
        _volgendeMeetingBalk = new Panel
        {
            Dock = DockStyle.Top, Height = 40, Visible = false, Padding = new Padding(0, 3, 0, 5),
        };
        _volgendeMeetingBalk.Controls.Add(_volgendeMeetingLabel);
        _volgendeMeetingBalk.Controls.Add(_deelnemenKnop);
        _meetingsGroup.Controls.Add(_volgendeMeetingBalk);
        // Elke halve minuut de countdown verversen (de agenda zelf komt uit de gewone poll).
        var meetingBalkTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        meetingBalkTimer.Tick += (_, _) => WerkVolgendeMeetingBalkBij();
        meetingBalkTimer.Start();
        _meetingsGroup.Accent = Theme.KlantCed; // agenda: blauw

        var onderSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 620,
        };
        onderSplit.Panel1.Controls.Add(_takenGroup);
        onderSplit.Panel2.Controls.Add(_meetingsGroup);
        // Agenda standaard ruimer: taken 45%, meetings 55% van de onderste helft.
        Shown += (_, _) =>
            onderSplit.SplitterDistance = Math.Max(350, (int)(onderSplit.ClientSize.Width * 0.45));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 400,
        };
        split.Panel1.Controls.Add(bovenSplit);
        split.Panel2.Controls.Add(onderSplit);

        Controls.Add(split);
        Controls.Add(toolbar);

        _berichten.Resize += (_, _) => SchaalAlleKolommen();
        _taken.Resize += (_, _) => SchaalAlleKolommen();
        _meetings.Resize += (_, _) => SchaalAlleKolommen();

        _timer.Tick += async (_, _) =>
        {
            // Eén automatische git-controle per dag, meeliftend op de poll (los van de
            // typen-guard hieronder: de controle raakt het antwoordvak niet).
            if (_gitCache.LaatsteControle.LocalDateTime.Date != DateTime.Now.Date)
            {
                _ = ControleerGitAsync(handmatig: false);
            }
            // Nooit onder de handen van de gebruiker uit verversen: aan het typen in het
            // antwoordvak = deze beurt overslaan (de volgende tick probeert opnieuw).
            if ((_detailConcept.Focused && _detailConcept.Text.Trim().Length > 0) ||
                (_detailFeedback.Focused && _detailFeedback.Text.Trim().Length > 0) ||
                !_feedbackButton.Enabled)
            {
                return;
            }
            await VerversAsync();
        };
        // Beheervenster of rechtsklik heeft de VIP-lijst gewijzigd: meteen herschikken in plaats
        // van tot de volgende ophaalronde te wachten.
        void VipsGewijzigd()
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    _vipSleutels = VipLijst.AlsSet(VipLijst.Laad());
                    HervulBerichtenLijst();
                });
            }
        }
        VipLijst.Gewijzigd += VipsGewijzigd;
        FormClosed += (_, _) =>
        {
            VipLijst.Gewijzigd -= VipsGewijzigd;
            BewaarDetailConcept();
            _timer.Stop();
            _cts.Cancel();
        };
        LaadO365DetailsCache(); // eerder opgehaalde CED-details meteen beschikbaar
        Shown += async (_, _) =>
        {
            await InitWebViewAsync();
            VulBerichtenLijst(CockpitCache.Load(), fouten: null); // meteen de laatst bekende lijst
            await ToonMeetingsUitCacheAsync(); // en meteen de laatst bekende meetings
            UpdateContextKnoppen();
            _timer.Start();
            ThemaIntro.SpeelEenmaalPerDag(this); // gun barrel, zon of scanlijn naargelang het thema
            BegroetMaarten();
            await VerversAsync();
            _ = AutoPlanDagAsync(); // eerste start van de dag: meteen de dag plannen
            if (_gitCache.LaatsteControle.LocalDateTime.Date != DateTime.Now.Date)
            {
                _ = ControleerGitAsync(handmatig: false); // dagelijkse git-controle
            }
        };
        Theme.Apply(this, fade: false); // WebView2 rendert niet betrouwbaar in een gelaagd venster
        VensterGeheugen.Volg(this, "cockpit");
        // Bij een themawissel de kleuren die hier hardgecodeerd staan (paneelaccenten en de
        // rijkleuren in de lijsten) opnieuw zetten, zodat de cockpit meteen klopt.
        void HerkleurCockpit()
        {
            if (IsDisposed)
            {
                return;
            }
            KlantLogo.Vergeet(); // initialen zijn in de klantkleur getekend
            WerkVensterTitelBij(); // titel en streaksymbool volgen het thema
            ThemaIntro.Speel(this); // meteen de sfeer van het nieuwe thema tonen
            Toast.Toon(this, ThemaStem.Dagdeel(), Fluent.Ster);
            _berichtenGroup.Accent = Theme.VoorBron("gmail");
            detailGroup.Accent = Theme.Accent;
            _takenGroup.Accent = Theme.Success;
            _meetingsGroup.Accent = Theme.KlantCed;
            _status.ForeColor = Theme.Muted;
            _sessieStatus.ForeColor = Theme.Muted;
            _weerLabel.ForeColor = Theme.Muted;
            HervulBerichtenLijst();
            VulTakenLijst();
            Invalidate(true);
        }
        Theme.ThemaGewijzigd += HerkleurCockpit;
        FormClosed += (_, _) => Theme.ThemaGewijzigd -= HerkleurCockpit;
        _detail.DefaultBackgroundColor = Theme.Bg;
    }

    /// <summary>Ingebedde browser voor de berichtweergave (zelfde opzet als het mailvenster).</summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-mail"));
            await _detail.EnsureCoreWebView2Async(env);
            var core = _detail.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExtern(e.Uri);
            };
            core.NavigationStarting += async (_, e) =>
            {
                if (e.Uri.StartsWith("wm-bijlage:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    if (int.TryParse(e.Uri["wm-bijlage:".Length..], out var index))
                    {
                        await OpenBijlageAsync(index);
                    }
                }
                else if (e.Uri.StartsWith("wm-ccmail:", StringComparison.OrdinalIgnoreCase))
                {
                    // Klik op een mail in het CC-overzicht: het volledige bericht in het
                    // detailpaneel tonen (blijft binnen de overzichtsrij).
                    e.Cancel = true;
                    if (int.TryParse(e.Uri["wm-ccmail:".Length..], out var ccIndex) &&
                        _getoond is { CcDetails.Count: > 0 } overzicht &&
                        ccIndex >= 0 && ccIndex < overzicht.CcDetails.Count &&
                        _detail.CoreWebView2 is { } ccCore)
                    {
                        ccCore.NavigateToString(MailReplyForm.BouwWeergave(
                            overzicht.CcDetails[ccIndex], terugNaarCcOverzicht: true));
                    }
                }
                else if (e.Uri.StartsWith("wm-ccterug:", StringComparison.OrdinalIgnoreCase))
                {
                    // Terug van een geopende CC-mail naar de overzichtslijst.
                    e.Cancel = true;
                    if (_getoond is { CcDetails.Count: > 0 } ccLijst &&
                        _detail.CoreWebView2 is { } terugCore)
                    {
                        terugCore.NavigateToString(MailReplyForm.BouwWeergave(ccLijst));
                    }
                }
                else if (e.Uri.StartsWith("wm-verjaardag:", StringComparison.OrdinalIgnoreCase))
                {
                    // De knop "Cadeau-ideeën openen" bij een taak van de cadeauradar.
                    e.Cancel = true;
                    new VerjaardagenForm().Show(this);
                }
                else if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExtern(e.Uri);
                }
            };
            core.NavigateToString(_wachtendeWeergave ?? MailReplyForm.LegeWeergave);
            _wachtendeWeergave = null;
        }
        catch
        {
            // Zonder WebView2 blijft de rest van de cockpit gewoon werken.
        }
    }

    /// <summary>Downloadt een aangeklikte bijlage-chip naar een tijdelijke map en opent hem.</summary>
    private async Task OpenBijlageAsync(int index)
    {
        if (_getoond is not { } bericht)
        {
            return;
        }
        if (bericht.SmartschoolBericht.Length > 0)
        {
            await OpenSmartschoolBijlageAsync(bericht, index);
            return;
        }
        if (bericht.Uid == 0)
        {
            Toast.Toon(this, "Bijlage openen kan alleen bij Gmail-mails uit de lijst", Fluent.Mail);
            return;
        }
        try
        {
            Toast.Toon(this, "Bijlage downloaden…", Fluent.Mail);
            var naam = index < bericht.Bijlagen.Count ? bericht.Bijlagen[index] : $"bijlage-{index}";
            var map = Path.Combine(Path.GetTempPath(), "WorkManager-bijlagen");
            Directory.CreateDirectory(map);
            var paden = await GmailClient.DownloadBijlagenAsync(
                MailReplySettings.Load(), bericht, map, new[] { (index, naam) }, _cts.Token);
            if (paden.Count > 0)
            {
                Process.Start(new ProcessStartInfo(paden[0]) { UseShellExecute = true });
            }
            else
            {
                Toast.Toon(this, "Bijlage niet gevonden in de mail", Fluent.Mail);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Bijlage openen mislukt: {ex.Message}", Fluent.Mail);
        }
    }

    /// <summary>
    /// Downloadt de bijlagen van een Smartschool-bericht via de verborgen schoolsessie
    /// (de bijlagen in de berichtweergave zijn daar geen echte links) en opent daarna de
    /// aangeklikte bijlage; komt de naam niet overeen, dan gaat de map open.
    /// </summary>
    private async Task OpenSmartschoolBijlageAsync(MailBericht bericht, int index)
    {
        if (bericht.SmartschoolBericht.Split('|', 2) is not { Length: 2 } delen)
        {
            return;
        }
        try
        {
            // De pollronde downloadt bijlagen proactief naar de lokale bijlagenmap;
            // normaal opent de chip dus meteen. Alleen als er (nog) niets lokaal staat
            // haalt de verborgen sessie ze alsnog op — dat duurt even.
            var paden = SmartschoolClient.LokaleBijlagen(delen[1]);
            if (paden.Count == 0)
            {
                Toast.Toon(this, "Bijlage uit Smartschool downloaden…", Fluent.Mail);
                paden = await SmartschoolClient.Instance.DownloadBijlagenAsync(
                    delen[0], delen[1], _cts.Token);
            }
            var naam = index < bericht.Bijlagen.Count ? bericht.Bijlagen[index] : "";
            // Ook een "naam (2).pdf" van een eerdere download telt als treffer.
            var pad = paden.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), naam, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(Path.GetExtension(p), Path.GetExtension(naam),
                    StringComparison.OrdinalIgnoreCase) &&
                 Path.GetFileNameWithoutExtension(p).StartsWith(
                     Path.GetFileNameWithoutExtension(naam), StringComparison.OrdinalIgnoreCase)));
            if (pad is null && paden.Count == 1)
            {
                pad = paden[0];
            }
            if (pad is not null)
            {
                Process.Start(new ProcessStartInfo(pad) { UseShellExecute = true });
            }
            else if (paden.Count > 0 && Path.GetDirectoryName(paden[0]) is { } map)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{map}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Toast.Toon(this, "Geen bijlage binnengekregen uit Smartschool", Fluent.Mail);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het downloaden.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Bijlage downloaden mislukt: {ex.Message}", Fluent.Mail);
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

    private void UpdateContextKnoppen()
    {
        var actief = _actieveContexts();
        foreach (var knop in _contextKnoppen)
        {
            var naam = (string)knop.Tag!;
            var aan = actief.Contains(naam);
            knop.Text = (aan ? "✔ " : "") + naam;
            knop.Kind = aan ? ButtonKind.Accent : ButtonKind.Normal;
        }
    }

    // ---------- Verversen ----------

    /// <summary>
    /// Verse verversbeurt zodra dat kan: net na het aanmelden van Teams/Outlook mag het
    /// resultaat niet op de volgende timerbeurt wachten. Loopt er al een beurt (die de
    /// bron mogelijk nog als "niet aangemeld" heeft overgeslagen), dan wachten we tot
    /// die klaar is en halen we daarna alsnog vers op.
    /// </summary>
    private async Task VerversNaAanmeldenAsync()
    {
        while (_bezig && !IsDisposed)
        {
            await Task.Delay(500, _cts.Token);
        }
        await VerversAsync();
    }

    private bool _verseBeurtGepland;

    /// <param name="handmatig">Een klik op ⟳: bij een al lopende beurt niet overslaan maar
    /// na afloop meteen een verse beurt draaien. De timer laat dit uit — die tikt vanzelf
    /// weer en hoeft niet te stapelen.</param>
    private async Task VerversAsync(bool handmatig = false)
    {
        if (_bezig || IsDisposed)
        {
            if (_bezig && !IsDisposed && handmatig && !_verseBeurtGepland)
            {
                // De lopende beurt is met oudere gegevens begonnen: na afloop meteen een
                // verse beurt draaien, zodat een klik op ⟳ nooit stilletjes verdampt.
                _verseBeurtGepland = true;
                Toast.Toon(this, "Er loopt al een verversbeurt — daarna volgt meteen " +
                    "een verse", Fluent.Klok);
                try
                {
                    while (_bezig && !IsDisposed)
                    {
                        await Task.Delay(500, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // venster gesloten tijdens het wachten
                }
                finally
                {
                    _verseBeurtGepland = false;
                }
                await VerversAsync();
            }
            return;
        }
        _bezig = true;
        _verversButton.Bezig = true; // de spinner op de knop is genoeg als "bezig"-signaal
        BewaarDetailConcept(); // getypte tekst veiligstellen vóór de lijst herbouwd wordt
        var begonnen = DateTimeOffset.Now;
        try
        {
            await Task.WhenAll(VerversBerichtenAsync(), VerversTakenAsync(), VerversMeetingsAsync());
            // De komende twee weken CED-agenda op de achtergrond warmhouden: bladeren met ▶
            // hoeft dan niets meer op te halen.
            _ = WarmCedCacheAsync();
            // Stil zolang alles gewoon werkt: fouten staan al als rode regels in de lijst,
            // dus de statusregel spreekt alleen nog bij een abnormaal trage beurt.
            var duur = (DateTimeOffset.Now - begonnen).TotalSeconds;
            _status.Text = duur > 60
                ? $"Trage verversbeurt: {duur:0.0} s  ({BronGezondheid.DurenKort()})"
                : "";
            // Meeloopregel in %APPDATA%\WorkManagerervers-log.txt: zo is achteraf te zien
            // welke bron een trage beurt veroorzaakte, ook als je er niet bij zat.
            LogVerversDuur(duur);
            // Factuurknop volgt de weektaak: zichtbaar zolang "Facturen goedkeuren (ISPnext)"
            // openstaat (die verschijnt woensdag automatisch), en weg zodra er geklikt of
            // goedgekeurd is — ook na een herstart, want de taakstatus is persistent.
            _facturenButton.Visible = !_facturenGeklikt &&
                MijnTaakStore.Load().Taken.Any(t =>
                    !t.Klaar && t.Tekst.Contains("Facturen goedkeuren", StringComparison.OrdinalIgnoreCase));
            ToonPlekVoorstelEenmalig();
            WerkProjectKnoppenBij(); // 🟢-lampjes op de klantknoppen actueel houden
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het verversen.
        }
        finally
        {
            _bezig = false;
            if (!IsDisposed)
            {
                _verversButton.Bezig = false;
            }
        }
    }

    /// <summary>Houdt de laatste ~200 verversbeurten bij, met de duur per bron.</summary>
    private static void LogVerversDuur(double seconden)
    {
        try
        {
            var pad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "ververs-log.txt");
            var regels = File.Exists(pad) ? File.ReadAllLines(pad).TakeLast(200).ToList() : new List<string>();
            regels.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  totaal={seconden:0.0}s  " +
                BronGezondheid.DurenKort());
            File.WriteAllLines(pad, regels);
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    private bool _berichtenBezig;

    private async Task VerversBerichtenAsync()
    {
        if (_berichtenBezig)
        {
            return; // nooit twee berichten-ophaalbeurten tegelijk (timer + knop)
        }
        _berichtenBezig = true;
        try
        {
            await VerversBerichtenKernAsync();
        }
        finally
        {
            _berichtenBezig = false;
        }
    }

    /// <summary>
    /// Is dit de Gmail-meldingsmail "Nieuw bericht van …" van Smartschool? Die wordt in de
    /// lijst vervangen door het échte schoolbericht zodra dat uit Smartschool is opgehaald;
    /// alleen als dat mislukt, blijft de melding zelf zichtbaar.
    /// </summary>
    private static bool IsSmartschoolMelding(MailBericht m) =>
        !m.IsChat &&
        (m.VanAdres.Contains("smartschoolmail", StringComparison.OrdinalIgnoreCase) ||
         m.Van.Contains("smartschool", StringComparison.OrdinalIgnoreCase)) &&
        m.Onderwerp.Contains("nieuw bericht", StringComparison.OrdinalIgnoreCase);

    private async Task VerversBerichtenKernAsync()
    {
        var berichten = new List<MailBericht>();
        var fouten = new List<string>();
        // Faalt een bron (bv. Teams/Outlook-MFA verlopen), dan blijven zijn berichten
        // uit de vorige cache staan in plaats van uit de lijst te verdwijnen.
        var vorigeCache = CockpitCache.Load();
        // Deelresultaten meteen tonen: sommige bronnen (Teams met verse herlaadbeurt,
        // Outlook) zijn traag — wat al opgehaald is verschijnt direct in de lijst, voor
        // de nog lopende bronnen blijven de rijen uit de vorige cache staan.
        var versGehaald = new HashSet<string>();
        void ToonTussenstand()
        {
            if (IsDisposed)
            {
                return;
            }
            var snapshot = new List<MailBericht>(berichten);
            if (!versGehaald.Contains("gmail"))
            {
                snapshot.AddRange(vorigeCache.Where(m => !m.IsChat && m.VanAdres != "CC-map"));
            }
            if (!versGehaald.Contains("chat"))
            {
                snapshot.AddRange(vorigeCache.Where(m => m.ChatSpace.Length > 0));
            }
            if (!versGehaald.Contains("wa"))
            {
                snapshot.AddRange(vorigeCache.Where(m => m.WhatsAppChat.Length > 0));
            }
            if (!versGehaald.Contains("teams"))
            {
                snapshot.AddRange(vorigeCache.Where(m => m.TeamsChat.Length > 0));
            }
            if (!versGehaald.Contains("outlook"))
            {
                snapshot.AddRange(vorigeCache.Where(m => m.OutlookMail.Length > 0));
            }
            if (!versGehaald.Contains("smartschool"))
            {
                snapshot.AddRange(vorigeCache.Where(m => m.SmartschoolBericht.Length > 0));
                // De Gmail-meldingsmail ("Nieuw bericht van …") nog niet laten opflitsen:
                // zo meteen wordt eerst het échte bericht uit Smartschool gehaald en de
                // melding gearchiveerd. Alleen als dat mislukt, komt de melding alsnog in
                // de eindstand terecht.
                if (SmartschoolLogin.Geconfigureerd)
                {
                    snapshot.RemoveAll(IsSmartschoolMelding);
                }
            }
            snapshot.AddRange(vorigeCache.Where(m => !m.IsChat && m.VanAdres == "CC-map" &&
                snapshot.All(s => s.MessageId != m.MessageId)));
            // Dezelfde overlay als de eindverwerking, zodat de tussenstand geen
            // weggescreende of gearchiveerde rijen laat opflitsen.
            var cacheNu = ConceptCache.Load();
            foreach (var m in snapshot)
            {
                if (m.MessageId.Length > 0 && cacheNu.TryGetValue(m.MessageId, out var bewaard))
                {
                    m.ConceptKlaar = bewaard.ConceptKlaar;
                    m.Concept = bewaard.Concept;
                    m.Reden = bewaard.Reden;
                    m.Genegeerd = bewaard.Genegeerd && m.TeamsChat.Length == 0;
                    m.Urgent = bewaard.Urgent;
                }
            }
            snapshot.RemoveAll(m => m.IsChat && m.Genegeerd);
            VulBerichtenLijst(snapshot, fouten);
        }

        // Eerst kijken of er überhaupt internet is: zonder verbinding zou elke bron met een
        // eigen cryptische fout in de lijst komen. Dan liever één duidelijke regel, en de
        // laatst opgehaalde berichten gewoon laten staan tot de verbinding terug is.
        if (!await Internet.CheckAsync())
        {
            fouten.Add("📡 Geen internetverbinding — de lijst toont de laatst opgehaalde berichten");
            ToonTussenstand();
            return;
        }

        // De drie trage bronnen meteen op weg sturen in plaats van ze om de beurt af te
        // wachten: Gmail zit op IMAP, Teams en Outlook op een verborgen browser. Ze storen
        // elkaar niet (elk zijn eigen sessie en slot), dus de ophaalbeurt duurt voortaan
        // zolang de traagste — niet de som van alle drie. De verwerking eronder blijft in
        // dezelfde volgorde staan; daar wordt alleen nog het resultaat opgehaald.
        var startTijd = DateTimeOffset.Now;
        TimeSpan Duur() => DateTimeOffset.Now - startTijd;
        var gmailSettings = MailReplySettings.Load();
        var gmailTaak = gmailSettings.AppWachtwoord.Length > 0 &&
                        !BronGezondheid.Gepauzeerd("Gmail", out _)
            ? GmailClient.FetchAsync(gmailSettings, _cts.Token)
            : null;
        var teamsTaak = TeamsClient.OoitGekoppeld && !BronGezondheid.Gepauzeerd("Teams", out _)
            ? TeamsClient.Instance.OngelezenAsync(_cts.Token)
            : null;
        var outlookTaak = OutlookClient.OoitGekoppeld && !BronGezondheid.Gepauzeerd("Outlook", out _)
            ? OutlookClient.Instance.VolledigeMailsAsync(_cts.Token)
            : null;
        var chatInstellingen = GoogleChatSettings.Load();
        var chatTaak = chatInstellingen.Gekoppeld && !BronGezondheid.Gepauzeerd("Google Chat", out _)
            ? GoogleChatClient.FetchAsync(chatInstellingen, _cts.Token)
            : null;
        var waTaak = WhatsAppClient.OoitGekoppeld && !BronGezondheid.Gepauzeerd("WhatsApp", out _)
            ? WhatsAppClient.Instance.OngelezenChatsAsync(_cts.Token)
            : null;
        // Per bron noteren wanneer zijn gegevens binnen zijn (zie "Gezondheid bronnen…" en
        // ververs-log.txt). Meteen ook de fout van een niet-afgewachte taak opvangen: die zou
        // anders als "unobserved" bij de garbage collector belanden.
        foreach (var (naam, taak) in new (string, Task?)[]
                 {
                     ("Gmail", gmailTaak), ("Google Chat", chatTaak), ("WhatsApp", waTaak),
                     ("Teams", teamsTaak), ("Outlook", outlookTaak),
                 })
        {
            if (taak is null)
            {
                continue;
            }
            var bron = naam;
            _ = taak.ContinueWith(t =>
            {
                _ = t.Exception;
                BronGezondheid.Klaar(bron, Duur());
                // Bleek de sessie tijdens het ophalen niet (meer) aangemeld, dan moet de
                // aanmeldknop nú in beeld — niet pas als de hele verversronde klaar is
                // (de andere bronnen kunnen nog minuten bezig zijn).
                if (bron is "Teams" or "Outlook" or "WhatsApp" && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(WerkAanmeldKnoppenBij);
                    }
                    catch
                    {
                        // Venster net gesloten: dan is er ook geen knop meer te tonen.
                    }
                }
            }, TaskScheduler.Default);
        }

        try
        {
            var mailSettings = gmailSettings;
            if (mailSettings.AppWachtwoord.Length > 0)
            {
                if (BronGezondheid.Gepauzeerd("Gmail", out var gmailTot))
                {
                    fouten.Add($"✉️ Gmail: ⏸ tot {gmailTot.ToLocalTime():HH:mm} na herhaalde fouten");
                    berichten.AddRange(vorigeCache.Where(m => !m.IsChat));
                }
                else if (gmailTaak is not null)
                {
                    berichten.AddRange(await gmailTaak);
                    BronGezondheid.Succes("Gmail");
                }
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"✉️ Gmail: {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => !m.IsChat));
            if (BronGezondheid.Fout("Gmail", ex.Message) && !IsDisposed)
            {
                Toast.Toon(this, "Gmail tijdelijk gepauzeerd na 5 fouten op rij", Fluent.Mail);
            }
        }
        // Vaste regels: routinemails in Gmail meteen archiveren én als gelezen zetten —
        // Netflix-bevestigingen, de JAAN bv "SMS credits bijgeschreven"-meldingen
        // (het aantal in het onderwerp varieert, dus op de vaste kern matchen) en de
        // maandelijkse Apple-factuur van € 0,99 (één per jaar tonen, in januari).
        var eigenRegels = ArchiveerRegels.Load(); // zelfgemaakte regels (archiveer-regels.json)
        var netflix = berichten.Where(m => !m.IsChat && m.Uid > 0 &&
            (m.VanAdres.Contains("account.netflix.com", StringComparison.OrdinalIgnoreCase) ||
             System.Text.RegularExpressions.Regex.IsMatch(m.Onderwerp,
                 @"SMS[\s-]*credits zijn bijgeschreven",
                 System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
             AlarmMails.Matcht(m) ||
             AppleFactuur.MoetArchiveren(m) ||
             ArchiveerRegels.Matcht(m, eigenRegels))).ToList();
        // Storingsmails (MailMobility/MailProperty van IT-support) éérst registreren: dat zet
        // de rode taak en houdt de laatste-mailtijd bij, ook als het archiveren zo mislukt.
        AlarmMails.Registreer(netflix);
        if (netflix.Count > 0)
        {
            try
            {
                await GmailClient.ArchiveerAsync(MailReplySettings.Load(), netflix, _cts.Token);
                berichten.RemoveAll(netflix.Contains);
                if (!IsDisposed)
                {
                    Toast.Toon(this, $"{netflix.Count} routinemail(s) automatisch gearchiveerd",
                        Fluent.Archive);
                }
            }
            catch
            {
                // Even niet gelukt: de volgende poll probeert opnieuw.
            }
        }
        versGehaald.Add("gmail");
        ToonTussenstand();
        try
        {
            var chatSettings = chatInstellingen;
            if (chatSettings.Gekoppeld)
            {
                if (BronGezondheid.Gepauzeerd("Google Chat", out var chatTot))
                {
                    fouten.Add($"💬 Google Chat: ⏸ tot {chatTot.ToLocalTime():HH:mm} na herhaalde fouten");
                    berichten.AddRange(vorigeCache.Where(m => m.ChatSpace.Length > 0));
                }
                else if (chatTaak is not null)
                {
                    berichten.AddRange(await chatTaak);
                    BronGezondheid.Succes("Google Chat");
                }
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"💬 Google Chat: {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => m.ChatSpace.Length > 0));
            if (BronGezondheid.Fout("Google Chat", ex.Message) && !IsDisposed)
            {
                Toast.Toon(this, "Google Chat tijdelijk gepauzeerd na 5 fouten op rij", Fluent.Mail);
            }
        }
        versGehaald.Add("chat");
        ToonTussenstand();
        try
        {
            if (WhatsAppClient.OoitGekoppeld &&
                BronGezondheid.Gepauzeerd("WhatsApp", out var waTot))
            {
                fouten.Add($"🟢 WhatsApp: ⏸ tot {waTot.ToLocalTime():HH:mm} na herhaalde fouten");
                berichten.AddRange(vorigeCache.Where(m => m.WhatsAppChat.Length > 0));
            }
            else if (WhatsAppClient.OoitGekoppeld)
            {
                // Alleen de zijbalk lezen: chats worden niet geopend, dus niets wordt gelezen gemarkeerd.
                var (waTotaal, waChats) = waTaak is not null
                    ? await waTaak
                    : await WhatsAppClient.Instance.OngelezenChatsAsync(_cts.Token);
                if (waTotaal == 0)
                {
                    // Lege zijbalk = (nog) niet gerenderd; cache aanhouden.
                    berichten.AddRange(vorigeCache.Where(m => m.WhatsAppChat.Length > 0));
                }
                // Een rij verschijnt pas als het hele gesprek er is: klikken toont dan meteen
                // alles. Chats waarvan de cache het nieuwste bericht nog niet kent, worden
                // eerst op de achtergrond volledig geladen (StartWaVoorladen) en pas daarna
                // getoond — het WaVers-register houdt ze vast tot ze gearchiveerd zijn.
                var waHistorie = LaadWaHistorie();
                var waVers = VersRegister.WaVers.Load();
                var waVersGewijzigd = false;
                var waRijen = new List<MailBericht>();
                var waTeLaden = new List<MailBericht>();
                foreach (var c in waChats)
                {
                    var rij = new MailBericht
                    {
                        // Sleutel = naam + laatste preview: archiveren blijft onthouden tot
                        // er een nieuw bericht is (zelfde werking als Teams/Google Chat).
                        MessageId = $"wa:{c.Naam}|{c.Preview}",
                        WhatsAppChat = c.Naam,
                        Van = c.Naam,
                        Onderwerp = c.Preview.Length > 0
                            ? (c.Preview.Length > 90 ? c.Preview[..90] + "…" : c.Preview)
                            : "ongelezen berichten",
                        Tekst = (c.Preview.Length > 0 ? c.Preview + "\n\n" : "") +
                            "(Laatste bericht uit de chatlijst — een antwoord hieronder wordt " +
                            "gewoon via WhatsApp verstuurd.)",
                        Datum = DateTimeOffset.Now,
                    };
                    // Een nieuwer bericht vervangt een eerdere wachtende rij van die chat.
                    foreach (var oudId in waVers.Values
                        .Where(v => v.Chat.Equals(c.Naam, StringComparison.OrdinalIgnoreCase) &&
                            v.MessageId != rij.MessageId)
                        .Select(v => v.MessageId).ToList())
                    {
                        waVers.Remove(oudId);
                        waVersGewijzigd = true;
                    }
                    if (vorigeCache.FirstOrDefault(m =>
                            m.MessageId == rij.MessageId) is { Html.Length: > 0 } oud)
                    {
                        rij.Html = oud.Html;
                        rij.Tekst = oud.Tekst;
                    }
                    else if (waVers.TryGetValue(rij.MessageId, out var geladen) && geladen.Geladen)
                    {
                        rij.Html = geladen.Html;
                        if (geladen.Tekst.Length > 0)
                        {
                            rij.Tekst = geladen.Tekst;
                        }
                        if (geladen.Html.Length == 0)
                        {
                            // De vorige laadbeurt leverde niets op (bv. een DOM-hapering bij
                            // deze ene chat): de rij gewoon tonen mét preview, maar op de
                            // achtergrond opnieuw proberen tot het volledige gesprek er is.
                            waTeLaden.Add(rij);
                        }
                    }
                    else if (waHistorie.TryGetValue(c.Naam, out var h) && h.Berichten.Count > 0 &&
                             WaHistorieActueel(h, c.Preview))
                    {
                        // De cache kent het nieuwste bericht al: meteen volledig tonen.
                        rij.Html = BouwWhatsAppHtml(h.Berichten, c.Naam, h.Avatar);
                        rij.Tekst += $"\n\n{HistorieKop}\n" + string.Join("\n",
                            h.Berichten.AsEnumerable().Reverse()
                                .Select(b => $"[{b.Tijd}] {b.Afzender}: {b.Tekst}"));
                    }
                    else
                    {
                        // Nog niet volledig: eerst laden, daarna pas in de lijst.
                        waVers[rij.MessageId] = new VersRegister.Rij
                        {
                            MessageId = rij.MessageId, Chat = c.Naam,
                            Onderwerp = rij.Onderwerp, Datum = rij.Datum,
                        };
                        waVersGewijzigd = true;
                        waTeLaden.Add(rij);
                        continue;
                    }
                    waRijen.Add(rij);
                }
                // Voorgeladen rijen die op afhandeling wachten: de chat staat in WhatsApp al
                // op gelezen (het laden opende hem), dus de zijbalk noemt hem niet meer —
                // tonen tot Maarten archiveert.
                foreach (var v in waVers.Values.Where(v => v.Geladen &&
                             waRijen.All(r => r.MessageId != v.MessageId)))
                {
                    waRijen.Add(new MailBericht
                    {
                        MessageId = v.MessageId, WhatsAppChat = v.Chat, Van = v.Chat,
                        Onderwerp = v.Onderwerp, Tekst = v.Tekst, Html = v.Html, Datum = v.Datum,
                    });
                }
                if (waVersGewijzigd)
                {
                    VersRegister.WaVers.Bewaar(waVers);
                }
                berichten.AddRange(waRijen);
                if (waTeLaden.Count > 0)
                {
                    _ = WaVoorladenAsync(waTeLaden); // géén Task.Run: WebView2 = UI-thread
                }
                BronGezondheid.Succes("WhatsApp");
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"🟢 WhatsApp: {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => m.WhatsAppChat.Length > 0));
            if (BronGezondheid.Fout("WhatsApp", ex.Message) && !IsDisposed)
            {
                Toast.Toon(this, "WhatsApp tijdelijk gepauzeerd na 5 fouten op rij", Fluent.Mail);
            }
        }
        versGehaald.Add("wa");
        ToonTussenstand();
        try
        {
            if (TeamsClient.OoitGekoppeld &&
                BronGezondheid.Gepauzeerd("Teams", out var teamsTot))
            {
                // Bij een aanmeldprobleem is "gepauzeerd na fouten" misleidend: dan moet je
                // gewoon opnieuw aanmelden (dat wist de pauze meteen weer).
                fouten.Add(BronGezondheid.LaatsteFoutIsAanmelding("Teams")
                    ? "🟪 Teams: 🔑 niet aangemeld — klik 'Teams aanmelden…'"
                    : $"🟪 Teams: ⏸ tot {teamsTot.ToLocalTime():HH:mm} na herhaalde fouten");
                berichten.AddRange(vorigeCache.Where(m => m.TeamsChat.Length > 0));
            }
            else if (TeamsClient.OoitGekoppeld)
            {
                // Alleen signaleren (uitlezen); antwoorden gebeurt in Teams zelf.
                var (totaal, ongelezen, teamsPreviews) = teamsTaak is not null
                    ? await teamsTaak
                    : await TeamsClient.Instance.OngelezenAsync(_cts.Token);
                if (totaal < 10)
                {
                    // Nauwelijks chats in de lijst (normaal 100+) = de UI is nog aan het
                    // renderen/synchroniseren na een verse sessie. Zo'n deels geladen lijst
                    // is onbetrouwbaar (valse "ongelezen"-rijen): cache aanhouden en de
                    // verse waarneming deze ronde negeren.
                    berichten.AddRange(vorigeCache.Where(m => m.TeamsChat.Length > 0));
                    ongelezen = new List<TeamsClient.TeamsBericht>();
                }
                var teamsRijen = ongelezen.Select(t => new MailBericht
                {
                    // Zelfde werking als Google Chat: sleutel = naam + laatste preview, dus
                    // archiveren blijft onthouden tot er écht nieuwe berichten zijn.
                    MessageId = $"teams:{t.Naam}|{t.Preview}",
                    TeamsChat = t.Naam,
                    Van = t.Naam,
                    VanAdres = "Teams",
                    Onderwerp = t.Preview.Length > 0
                        ? (t.Preview.Length > 90 ? t.Preview[..90] + "…" : t.Preview)
                        : "ongelezen berichten",
                    Tekst = (t.Preview.Length > 0 ? t.Preview + "\n\n" : "") +
                        "(Volledige chat en beantwoorden: in Teams zelf.)",
                    Datum = DateTimeOffset.Now,
                }).ToList();
                // Een rij verschijnt pas als het hele gesprek er is: klikken toont dan meteen
                // alles (zelfde werking als WhatsApp). Chats waarvan de cache het nieuwste
                // bericht nog niet kent, worden eerst op de achtergrond volledig geladen en
                // pas daarna getoond — het TeamsVers-register houdt ze vast tot ze
                // gearchiveerd zijn (het laden opent de chat, dus Teams zet hem op gelezen
                // en de zijbalk noemt hem daarna niet meer).
                var teamsHistorie = LaadTeamsHistorie();
                var teamsVers = VersRegister.TeamsVers.Load();
                var teamsVersGewijzigd = false;
                // Afgehandeld in Teams zelf: heeft Maarten intussen in de chat geantwoord,
                // dan begint de zijbalkpreview met "U:" (of "You:"/"Vous"). De wachtende
                // rij is dan klaar en verdwijnt vanzelf — niet eindeloos blijven tonen tot
                // er handmatig gearchiveerd wordt. Alleen bij een volwaardige scrape
                // (totaal >= 10): een half gerenderde lijst heeft geen betrouwbare previews.
                static bool BeantwoordInTeams(string preview) =>
                    preview.StartsWith("U:", StringComparison.Ordinal) ||
                    preview.StartsWith("You:", StringComparison.OrdinalIgnoreCase) ||
                    preview.StartsWith("Vous", StringComparison.OrdinalIgnoreCase);
                if (totaal >= 10)
                {
                    foreach (var beantwoord in teamsVers.Values
                        .Where(v => teamsPreviews.TryGetValue(v.Chat, out var p) &&
                            BeantwoordInTeams(p))
                        .Select(v => v.MessageId).ToList())
                    {
                        teamsVers.Remove(beantwoord);
                        teamsVersGewijzigd = true;
                    }
                }
                var teamsKlaar = new List<MailBericht>();
                var teamsTeLaden = new List<MailBericht>();
                foreach (var rij in teamsRijen)
                {
                    // Een nieuwer bericht vervangt een eerdere wachtende rij van die chat.
                    foreach (var oudId in teamsVers.Values
                        .Where(v => v.Chat.Equals(rij.TeamsChat, StringComparison.OrdinalIgnoreCase) &&
                            v.MessageId != rij.MessageId)
                        .Select(v => v.MessageId).ToList())
                    {
                        teamsVers.Remove(oudId);
                        teamsVersGewijzigd = true;
                    }
                    if (vorigeCache.FirstOrDefault(m =>
                            m.MessageId == rij.MessageId) is { Html.Length: > 0 } oud)
                    {
                        rij.Html = oud.Html;
                        rij.Tekst = oud.Tekst;
                    }
                    else if (teamsVers.TryGetValue(rij.MessageId, out var geladen) && geladen.Geladen)
                    {
                        rij.Html = geladen.Html;
                        if (geladen.Tekst.Length > 0)
                        {
                            rij.Tekst = geladen.Tekst;
                        }
                        if (geladen.Html.Length == 0)
                        {
                            // De vorige laadbeurt leverde niets op: de rij gewoon tonen mét
                            // preview, maar op de achtergrond opnieuw proberen.
                            teamsTeLaden.Add(rij);
                        }
                    }
                    else if (teamsHistorie.TryGetValue(rij.TeamsChat, out var h) &&
                        h.Berichten.Count > 0 && TeamsHistorieActueel(h, rij.Onderwerp))
                    {
                        // De cache kent het nieuwste bericht al: meteen volledig tonen.
                        rij.Html = BouwTeamsHtml(h.Berichten, rij.TeamsChat);
                        rij.Tekst += $"\n\n{HistorieKop}\n" + string.Join("\n", h.Berichten
                            .Select(b => $"[{b.Tijd}] {(b.Uitgaand ? "Maarten (ikzelf)" : b.Auteur)}: " +
                                $"{(b.Beeld.Length > 0 ? "[📷 afbeelding] " : "")}{b.Tekst}"));
                    }
                    else
                    {
                        // Nog niet volledig: eerst laden, daarna pas in de lijst.
                        teamsVers[rij.MessageId] = new VersRegister.Rij
                        {
                            MessageId = rij.MessageId, Chat = rij.TeamsChat,
                            Onderwerp = rij.Onderwerp, Datum = rij.Datum,
                        };
                        teamsVersGewijzigd = true;
                        teamsTeLaden.Add(rij);
                        continue;
                    }
                    teamsKlaar.Add(rij);
                }
                // Voorgeladen rijen die op afhandeling wachten: de chat staat in Teams al op
                // gelezen (het laden opende hem), dus de zijbalk noemt hem niet meer — tonen
                // tot Maarten archiveert.
                foreach (var v in teamsVers.Values.Where(v => v.Geladen &&
                             teamsKlaar.All(r => r.MessageId != v.MessageId)))
                {
                    teamsKlaar.Add(new MailBericht
                    {
                        MessageId = v.MessageId, TeamsChat = v.Chat, Van = v.Chat,
                        VanAdres = "Teams", Onderwerp = v.Onderwerp, Tekst = v.Tekst,
                        Html = v.Html, Datum = v.Datum,
                    });
                }
                if (teamsVersGewijzigd)
                {
                    VersRegister.TeamsVers.Bewaar(teamsVers);
                }
                berichten.AddRange(teamsKlaar);
                if (teamsTeLaden.Count > 0)
                {
                    _ = TeamsVoorladenAsync(teamsTeLaden); // géén Task.Run: WebView2 = UI-thread
                }
                BronGezondheid.Succes("Teams");
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"🟪 Teams: {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => m.TeamsChat.Length > 0));
            if (BronGezondheid.Fout("Teams", ex.Message) && !IsDisposed)
            {
                Toast.Toon(this, BronGezondheid.IsAanmeldFout(ex.Message)
                    ? "Teams wacht op aanmelding — klik 'Teams aanmelden…'"
                    : "Teams tijdelijk gepauzeerd na 5 fouten op rij", Fluent.Mail);
            }
        }
        versGehaald.Add("teams");
        ToonTussenstand();
        try
        {
            if (OutlookClient.OoitGekoppeld &&
                BronGezondheid.Gepauzeerd("Outlook", out var outlookTot))
            {
                fouten.Add(BronGezondheid.LaatsteFoutIsAanmelding("Outlook")
                    ? "🔷 Outlook: 🔑 niet aangemeld — klik 'Outlook aanmelden…' (dagelijkse MFA)"
                    : $"🔷 Outlook: ⏸ tot {outlookTot.ToLocalTime():HH:mm} na herhaalde fouten");
                berichten.AddRange(vorigeCache.Where(m => m.OutlookMail.Length > 0));
            }
            else if (OutlookClient.OoitGekoppeld)
            {
                // Alle inboxmails, met volledige tekst uit de cache; antwoorden in Outlook zelf.
                var outlookMails = outlookTaak is not null
                    ? await outlookTaak
                    : await OutlookClient.Instance.VolledigeMailsAsync(_cts.Token);
                if (outlookMails.Count > 0)
                {
                    _outlookLeegOpeenvolgend = 0; // er stáán mails: geen twijfel meer
                }
                else
                {
                    // Lege scrape: dat is óf "nog aan het laden" óf een écht lege map. De
                    // DOM-heuristiek (LaatsteScrapeEchtLeeg) herkent leeg niet altijd — de
                    // CED-inbox rendert soms geen betrouwbaar leeg-signaal. Vangnet: pas na
                    // twee opeenvolgende lege scrapes de cache loslaten. Een trage laadbeurt
                    // levert hooguit één lege poll op; echte mails resetten de teller meteen,
                    // dus ze worden nooit ten onrechte verborgen.
                    _outlookLeegOpeenvolgend++;
                    // Twee lege scrapes op rij, altijd. De DOM-heuristiek alleen was te
                    // gretig: OWA rendert een verborgen lijst soms even niet, en dan
                    // verdwenen alle CED-rijen om twee minuten later terug te komen. Eén
                    // pollronde later loslaten is ruim snel genoeg.
                    var echtLeeg = _outlookLeegOpeenvolgend >= 2;
                    if (!echtLeeg)
                    {
                        // Nog niet zeker (eerste lege poll, laadbeurt): cache aanhouden
                        // (zelfde vangnet als bij Teams en WhatsApp).
                        berichten.AddRange(vorigeCache.Where(m => m.OutlookMail.Length > 0));
                    }
                }
                berichten.AddRange(outlookMails
                    .Select(b => new MailBericht
                    {
                        MessageId = b.Sleutel,
                        OutlookMail = $"{b.Van}|{b.Onderwerp}",
                        OutlookUrl = b.Url,
                        Van = b.Van,
                        VanAdres = "CED Outlook",
                        Onderwerp = b.Onderwerp.Length > 0 ? b.Onderwerp : "bericht",
                        Tekst = (b.Tekst.Length > 0
                            ? b.Tekst
                            : OutlookClient.Aangemeld
                                ? "(Geen tekst gevonden — open de mail in Outlook.)"
                                : "(Tekst nog niet opgehaald: Outlook is niet aangemeld " +
                                  "(wachtwoord-/MFA-scherm). Klik 'Outlook aanmelden…' — " +
                                  "daarna wordt de tekst vanzelf opgehaald en bewaard.)") +
                            "\n\n(Beantwoorden: in Outlook zelf.)",
                        Html = b.Html,
                        Datum = b.Datum,
                        Aan = SplitsAdresregel(b.Aan),
                        Cc = SplitsAdresregel(b.Cc),
                    }));
                BronGezondheid.Succes("Outlook");
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"🔷 Outlook (CED): {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => m.OutlookMail.Length > 0));
            if (BronGezondheid.Fout("Outlook", ex.Message) && !IsDisposed)
            {
                Toast.Toon(this, BronGezondheid.IsAanmeldFout(ex.Message)
                    ? "Outlook wacht op aanmelding — klik 'Outlook aanmelden…'"
                    : "Outlook tijdelijk gepauzeerd na 5 fouten op rij", Fluent.Mail);
            }
        }
        versGehaald.Add("outlook");
        ToonTussenstand();

        try
        {
            if (SmartschoolLogin.Geconfigureerd)
            {
                // Schoolberichten van Emilia en Lisa (elk hun eigen Postvak IN). Er wordt
                // alleen écht bij Smartschool ingelogd als er een meldingsmail ("Nieuw
                // bericht" van smartschoolmail.be) in de inbox staat of het uur om is;
                // anders komt alles meteen uit de lokale cache.
                var meldingsMail = berichten.Any(m => !m.Genegeerd && IsSmartschoolMelding(m));
                var smartschoolBerichten =
                    await SmartschoolClient.Instance.BerichtenAsync(meldingsMail, _cts.Token);
                if (SmartschoolClient.Instance.LaatsteAutoGearchiveerd is { Count: > 0 } dubbels &&
                    !IsDisposed)
                {
                    Toast.Toon(this, $"{dubbels.Count} dubbel schoolbericht(en) bij Emilia " +
                        "gearchiveerd (zelfde bericht stond ook bij Lisa)", Fluent.Archive);
                }
                berichten.AddRange(smartschoolBerichten
                    .Select(b => new MailBericht
                    {
                        MessageId = b.Sleutel,
                        SmartschoolBericht = $"{b.Kind}|{b.MsgId}",
                        Van = $"{b.Van} · {b.Kind}",
                        VanAdres = "Smartschool",
                        Onderwerp = b.Onderwerp.Length > 0 ? b.Onderwerp : "bericht",
                        Tekst = (b.Tekst.Length > 0
                            ? b.Tekst
                            : "(Geen tekst gevonden — open het bericht in Smartschool.)") +
                            (b.Bijlagen.Length > 0 ? $"\n\n📎 Bijlagen: {b.Bijlagen}" : "") +
                            "\n\n(Beantwoorden: in Smartschool zelf.)",
                        // Als chips in de berichtkop: aanklikken downloadt de bijlage via
                        // de verborgen schoolsessie (de weergave zelf heeft geen links).
                        Bijlagen = b.Bijlagen
                            .Split("; ", StringSplitOptions.RemoveEmptyEntries).ToList(),
                        Html = b.Html,
                        Datum = b.Datum,
                    }));
                // De Gmail-meldingsmail ("Nieuw bericht van …: …") heeft zijn werk gedaan
                // zodra het aangekondigde bericht hier staat: automatisch archiveren, net
                // als de Netflix-routinemails. Alleen bij een match op het aangekondigde
                // onderwerp — een melding waarvan het bericht (nog) niet opgehaald is,
                // blijft staan voor een volgende beurt. Dubbels die deze beurt bij Emilia
                // opgeruimd zijn tellen ook als opgehaald.
                if (meldingsMail)
                {
                    static string NormOnderwerp(string s) => System.Text.RegularExpressions
                        .Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
                    var opgehaald = smartschoolBerichten
                        .Select(b => NormOnderwerp(b.Onderwerp))
                        .Concat(SmartschoolClient.Instance.LaatsteAutoGearchiveerd
                            .Select(NormOnderwerp))
                        .ToHashSet();
                    bool Aangekondigd(MailBericht m)
                    {
                        if (m.Onderwerp.Split(':', 2) is not { Length: 2 } delen)
                        {
                            return false;
                        }
                        // De melding kapt lange onderwerpen soms af: ook een prefix-match
                        // op het opgehaalde onderwerp telt.
                        var kern = NormOnderwerp(delen[1]).TrimEnd('…', '.', ' ');
                        return kern.Length > 0 && opgehaald.Any(o =>
                            o == kern || o.StartsWith(kern, StringComparison.Ordinal));
                    }
                    var klaar = berichten.Where(m => m.Uid > 0 && !m.Genegeerd &&
                        IsSmartschoolMelding(m) && Aangekondigd(m)).ToList();
                    if (klaar.Count > 0)
                    {
                        // Het definitieve bericht staat in de lijst: de melding hoort daar
                        // sowieso niet meer naast — ook als het Gmail-archiveren zo faalt
                        // (dan probeert de volgende poll het archiveren gewoon opnieuw).
                        berichten.RemoveAll(klaar.Contains);
                        try
                        {
                            await GmailClient.ArchiveerAsync(
                                MailReplySettings.Load(), klaar, _cts.Token);
                            if (!IsDisposed)
                            {
                                Toast.Toon(this, $"{klaar.Count} Smartschool-melding(en) " +
                                    "in Gmail gearchiveerd", Fluent.Archive);
                            }
                        }
                        catch
                        {
                            // Even niet gelukt: de volgende poll probeert opnieuw.
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            fouten.Add($"🎒 Smartschool: {ex.Message}");
            berichten.AddRange(vorigeCache.Where(m => m.SmartschoolBericht.Length > 0));
        }
        versGehaald.Add("smartschool");
        ToonTussenstand();

        try
        {
            // Dagelijks CC-overzicht (map "CC" in Outlook): samenvattingen via Claude,
            // en mails waarin Maarten genoemd wordt komen als volledige rij mee.
            berichten.AddRange(await CcOverzicht.RijenAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            fouten.Add($"📋 CC-overzicht: {ex.Message}");
        }

        // AH-leveringsbevestigingen automatisch verwerken (taak opschuiven + agenda-event).
        try
        {
            var ahMelding = await AhLevering.VerwerkAsync(berichten, _cts.Token);
            if (ahMelding.Length > 0 && !IsDisposed)
            {
                Toast.Toon(this, ahMelding, Fluent.Kalender);
            }
        }
        catch
        {
            // Best effort; de berichtenlijst mag hier nooit op stranden.
        }

        // Concepten en oordelen uit de gedeelde conceptcache overnemen; weggescreende
        // chats niet tonen (zelfde gedrag als het mailvenster).
        var cache = ConceptCache.Load();
        foreach (var m in berichten)
        {
            if (m.MessageId.Length > 0 && cache.TryGetValue(m.MessageId, out var bewaard))
            {
                m.ConceptKlaar = bewaard.ConceptKlaar;
                m.Concept = bewaard.Concept;
                m.Reden = bewaard.Reden;
                // Teams is signaal-only: "ongelezen in Teams" is dé bron van waarheid. Neem
                // de screening-vlag (mailvenster: "geen actie nodig") daarom NIET over voor
                // Teams — anders verdwijnt een chat die in Teams nog ongelezen staat uit de
                // cockpit. Archiveren zet de chat echt op gelezen in Teams, waardoor hij bij
                // de volgende poll niet meer als ongelezen gedetecteerd wordt en zo vanzelf
                // uit de lijst valt.
                m.Genegeerd = bewaard.Genegeerd && m.TeamsChat.Length == 0;
                m.Urgent = bewaard.Urgent;
            }
        }
        // Zelfherstellend: een Outlook-mail die als gearchiveerd gemarkeerd is maar bij twee
        // opeenvolgende verversingen nog gewoon in het postvak staat, is dus NIET verplaatst
        // (de klik in de verborgen sessie deed niets) — markering eraf, mail terug in de
        // lijst. Zo kan cockpit en Outlook nooit stil uit elkaar groeien.
        foreach (var m in berichten.Where(m => m.OutlookMail.Length > 0 && m.Genegeerd))
        {
            var n = _genegeerdMaarAanwezig.GetValueOrDefault(m.MessageId) + 1;
            _genegeerdMaarAanwezig[m.MessageId] = n;
            if (n >= 3) // 3 waarnemingen: nooit een race met een nog lopende archivering
            {
                m.Genegeerd = false;
                SchrijfConceptCache(m);
                _genegeerdMaarAanwezig.Remove(m.MessageId);
            }
        }
        foreach (var oud in _genegeerdMaarAanwezig.Keys
            .Where(id => berichten.All(m => m.MessageId != id)).ToList())
        {
            _genegeerdMaarAanwezig.Remove(oud); // echt weg uit het postvak: teller opruimen
        }
        // Vaste regels: routinemails (CED) meteen archiveren in Outlook en niet in de
        // cockpit tonen — zelfde idee als de Netflix-regel in Gmail. Nu: "Reactie(s)
        // dagelijks overzicht", de telefoniestatistieken van NoReply Belgium
        // ("…;Employee Group Performance by Employee;…") en het maandelijkse
        // CyberVadis-rapport.
        var cedRegels = ArchiveerRegels.Load(); // ook de zelfgemaakte regels gelden voor CED
        var cedTeArchiveren = berichten.Where(m => m.OutlookMail.Length > 0 && !m.Genegeerd &&
            (System.Text.RegularExpressions.Regex.IsMatch(m.Onderwerp,
                 @"reacties?\s+dagelijks\s+overzicht",
                 System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
             (m.Van.Contains("NoReply Belgium", StringComparison.OrdinalIgnoreCase) &&
              m.Onderwerp.Contains("Employee Group Performance", StringComparison.OrdinalIgnoreCase)) ||
             m.Onderwerp.Contains("monthly CyberVadis report", StringComparison.OrdinalIgnoreCase) ||
             AlarmMails.Matcht(m) ||
             ArchiveerRegels.Matcht(m, cedRegels)))
            .ToList();
        // Storingsmails ook hier eerst registreren (rode taak + laatste-mailtijd).
        AlarmMails.Registreer(cedTeArchiveren);
        foreach (var m in cedTeArchiveren)
        {
            m.Genegeerd = true;
            SchrijfConceptCache(m);
            try
            {
                await OutlookClient.Instance.ArchiveerAsync(
                    m.Van, m.Onderwerp, _cts.Token, m.OutlookUrl);
            }
            catch
            {
                // Outlook-sessie niet actief: lokaal verbergen volstaat voor nu.
            }
        }

        // TopDesk-signaal: een "aan jou toegewezen"-mail van noreply@ced.nl betekent dat er
        // een ticket klaarstaat — vanaf dan staat de TopDesk-knop in de werkbalk, tot het
        // TopDesk-venster na het ophalen geen open tickets meer ziet.
        if (berichten.Any(m => m.OutlookMail.Length > 0 &&
                m.Van.Contains("noreply", StringComparison.OrdinalIgnoreCase) &&
                m.Onderwerp.Contains("aan jou toegewezen", StringComparison.OrdinalIgnoreCase)))
        {
            WerkSignaal.Zet("topdesk", true);
        }
        _topdeskKnop.Visible = WerkSignaal.Actief("topdesk");
        // Zelfde patroon voor Azure DevOps: een toewijzingsmail zet de knop aan, tot het
        // DevOps-venster na het ophalen geen open tasks meer ziet.
        if (berichten.Any(m => m.OutlookMail.Length > 0 &&
                m.Van.Contains("DevOps", StringComparison.OrdinalIgnoreCase) &&
                (m.Onderwerp.Contains("assigned", StringComparison.OrdinalIgnoreCase) ||
                 m.Onderwerp.Contains("toegewezen", StringComparison.OrdinalIgnoreCase))))
        {
            WerkSignaal.Zet("devops", true);
        }
        _devopsKnop.Visible = WerkSignaal.Actief("devops");
        // Verlofsignaal: een meldingsmail van SD Worx (myworkandme) over een aanvraag
        // betekent dat er een verlofaanvraag van een teamlid klaarstaat om goed te keuren.
        // Het signaal dooft zodra het portaalvenster geopend wordt.
        if (berichten.Any(m => !m.Genegeerd &&
                (m.Van.Contains("sdworx", StringComparison.OrdinalIgnoreCase) ||
                 m.Van.Contains("sd worx", StringComparison.OrdinalIgnoreCase) ||
                 m.Van.Contains("workandme", StringComparison.OrdinalIgnoreCase)) &&
                (m.Onderwerp.Contains("aanvraag", StringComparison.OrdinalIgnoreCase) ||
                 m.Onderwerp.Contains("goedkeur", StringComparison.OrdinalIgnoreCase) ||
                 m.Onderwerp.Contains("goed te keuren", StringComparison.OrdinalIgnoreCase) ||
                 m.Onderwerp.Contains("approv", StringComparison.OrdinalIgnoreCase) ||
                 m.Onderwerp.Contains("request", StringComparison.OrdinalIgnoreCase))))
        {
            WerkSignaal.Zet("sdworx", true);
        }
        _verlofKnop.Visible = WerkSignaal.Actief("sdworx");
        // Docker-knop bijwerken: verschijnt als de engine intussen plat ligt, verdwijnt
        // zodra hij (bv. handmatig) weer draait. Niet aankomen terwijl de start nog loopt.
        if (!_dockerKnop.Bezig)
        {
            _dockerKnop.Visible = DockerStatus.Geinstalleerd && !DockerStatus.Draait;
        }

        berichten.RemoveAll(m => m.IsChat && m.Genegeerd);

        // @maarten in een Teams-chat of CED-mail: rood + automatische reageer-taak.
        try
        {
            MentionTaken.Verwerk(berichten);
        }
        catch
        {
            // Best effort; de berichtenlijst mag hier nooit op stranden.
        }

        // Altijd bewaren: falende bronnen zijn hierboven al aangevuld vanuit de vorige cache,
        // dus Teams/Outlook-berichten overleven ook een verlopen MFA-sessie of herstart.
        CockpitCache.Save(berichten);
        if (!IsDisposed)
        {
            // Mislukte schrijfacties (archiveren, gelezen zetten) opnieuw proberen; geen
            // await — de wachtrij deelt de sessies en werkt zelf met het slot per client.
            _ = ActieWachtrij.VerwerkAsync(_cts.Token,
                m => { if (!IsDisposed) { Toast.Toon(this, m, Fluent.Archive); } });
            VulBerichtenLijst(berichten, fouten);
            WerkAanmeldKnoppenBij();
            // CED-mails standaard van een Claude-concept voorzien (achtergrond, met cache).
            _ = GenereerOutlookConceptenAsync(berichten
                .Where(m => m.OutlookMail.Length > 0 && !m.Genegeerd &&
                    m.Concept.Length == 0 && m.Reden.Length == 0)
                .ToList());
            // Eén keer per dag de structurele selectors van de verborgen sessies toetsen:
            // zo valt een UI-omgooi van Microsoft/Meta dezelfde dag op met één melding.
            if (_domZelftestGedaan != DateOnly.FromDateTime(DateTime.Now) && DateTime.Now.Hour >= 9)
            {
                _domZelftestGedaan = DateOnly.FromDateTime(DateTime.Now);
                _ = DoeDomZelftestAsync();
            }
        }
    }

    private DateOnly _domZelftestGedaan;

    private async Task DoeDomZelftestAsync()
    {
        var problemen = new List<string>();
        var tests = new (bool Actief, Func<Task<string>> Test)[]
        {
            (OutlookClient.OoitGekoppeld, () => OutlookClient.Instance.ZelftestAsync(_cts.Token)),
            (TeamsClient.OoitGekoppeld, () => TeamsClient.Instance.ZelftestAsync(_cts.Token)),
            (WhatsAppClient.OoitGekoppeld, () => WhatsAppClient.Instance.ZelftestAsync(_cts.Token)),
        };
        foreach (var (actief, test) in tests)
        {
            if (!actief)
            {
                continue;
            }
            try
            {
                if (await test() is { Length: > 0 } probleem)
                {
                    problemen.Add(probleem);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Sessie even niet beschikbaar: de gewone poll meldt dat al.
            }
        }
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "sessie-onderhoud-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} dom-zelftest: " +
                (problemen.Count > 0 ? string.Join(" | ", problemen) : "ok") + "\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
        if (problemen.Count > 0 && !IsDisposed)
        {
            Toast.Toon(this, "⚠ UI-wijziging gedetecteerd: " + string.Join(" · ", problemen),
                Fluent.Ster);
        }
    }

    private bool _conceptenBezig;

    /// <summary>
    /// Genereert Claude-concepten voor alle CED-mails die er nog geen hebben; elk oordeel
    /// gaat direct de cache in, dus afbreken kost geen voortgang. Het concept is alleen om
    /// over te nemen — versturen gebeurt nooit vanuit WorkManager voor Outlook of Teams.
    /// </summary>
    private async Task GenereerOutlookConceptenAsync(List<MailBericht> mails)
    {
        if (_conceptenBezig || mails.Count == 0)
        {
            return;
        }
        _conceptenBezig = true;
        try
        {
            var instructies = MailReplySettings.LoadInstructies();
            var settings = MailReplySettings.Load();
            foreach (var mail in mails)
            {
                var resultaat = await ClaudeDrafter.DraftAsync(mail, instructies, settings, _cts.Token);
                mail.Concept = resultaat.Concept;
                mail.ConceptKlaar = !string.IsNullOrWhiteSpace(resultaat.Concept);
                mail.Reden = resultaat.Reden.Length > 0 ? resultaat.Reden : "beoordeeld";
                mail.Urgent = mail.Urgent || resultaat.Urgent;
                SchrijfConceptCache(mail); // ook "geen antwoord nodig" cachen: niet opnieuw beoordelen
                if (IsDisposed)
                {
                    return;
                }
                if (ReferenceEquals(_getoond, mail))
                {
                    _detailConcept.Text = mail.Concept.ReplaceLineEndings("\r\n");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch
        {
            // Best effort; volgende poll probeert opnieuw.
        }
        finally
        {
            _conceptenBezig = false;
        }
    }

    /// <summary>De VIP-sleutels, vers ingelezen bij elke ophaalronde (het bestand is piepklein).</summary>
    private HashSet<string> _vipSleutels = VipLijst.AlsSet(VipLijst.Laad());

    /// <summary>
    /// De VIP-berichten van de vorige ronde. null = we hebben nog niets gezien: dan is alles
    /// "nieuw" en zou je bij het openen van de cockpit meteen een melding krijgen over berichten
    /// die er al stonden. Vandaar dat de eerste ronde stil blijft.
    /// </summary>
    private HashSet<string>? _vipVorigeRonde;

    /// <summary>
    /// Chat-rijen die in deze sessie gearchiveerd zijn. Vangnet tegen de race waarbij een
    /// verversbeurt die al líep tijdens de archiveerklik de rij weer terugzette (die beurt
    /// las de conceptcache van vóór de klik) — waardoor je twee keer moest klikken.
    /// </summary>
    private readonly HashSet<string> _zojuistGearchiveerd = new(StringComparer.Ordinal);

    private void VulBerichtenLijst(List<MailBericht> berichten, List<string>? fouten)
    {
        berichten.RemoveAll(m => _zojuistGearchiveerd.Contains(m.MessageId));
        var wasGevuld = _laatsteBerichten.Count > 0;
        _laatsteBerichten = berichten;
        _laatsteFouten = fouten;
        _vipSleutels = VipLijst.AlsSet(VipLijst.Laad());
        MeldNieuweVips(berichten);
        HervulBerichtenLijst();
        // Plantje in venster- en paneltitel actueel houden (ook over dagwissels heen; de
        // constructor liep nog vóór het berichtenpaneel bestond).
        WerkVensterTitelBij();
        // Inbox zero verdient muziek — maar alleen als je hem zelf leeggewerkt hebt, niet
        // telkens de cockpit opent terwijl er toch al niets stond. De melding stelt een nummer
        // voor; Spotify gaat pas open als je erop klikt.
        if (wasGevuld && berichten.Count == 0)
        {
            // De inbox-zero-reeks groeit per wérkdag waarop de lijst leeg raakte; het
            // plantje in de titelbalk groeit mee.
            var (reeks, nieuwVandaag) = InboxZeroReeks.Registreer();
            WerkVensterTitelBij();
            if (nieuwVandaag)
            {
                Prestaties.Gebeurtenis(this, "inboxzero", reeks.ToString());
            }
            if (nieuwVandaag && reeks >= 2)
            {
                Toast.Toon(this,
                    $"{InboxZeroReeks.Plant(reeks)} Inbox zero — al {reeks} werkdagen op rij" +
                    (reeks == InboxZeroReeks.Record() ? " (record!)" : "") +
                    $"  ·  {ThemaStem.Gevierd()}", Fluent.Ster);
            }
            if (InboxZeroMuziek.Voorstel() is { } suggestie)
            {
                Confetti.Vier(this);
                Toast.ToonActie(this, suggestie.Melding, suggestie.KnopTekst, suggestie.Speel, Fluent.Ster);
            }
        }
        // Snelheidsduivel: op vrijdagmiddag één keer het weekgemiddelde melden.
        if (Snelheid.WeekOverzicht() is { } snelheidsWeek)
        {
            Toast.Toon(this, snelheidsWeek, Fluent.Ster);
        }
        // En de weekafsluiter zelf: taken, uren en inbox-zero-reeks, in de toon van het thema.
        if (WeekDebriefing.Voorstel() is { } debriefing)
        {
            Toast.Toon(this, debriefing, Fluent.Ster);
        }
        // De dagafsluiter: wat af is, en vooral wat er nog niet geboekt is. Met een knop naar
        // het dagvoorstel, want dan kun je het gat meteen dichten in plaats van te onthouden.
        if (DagAfsluiter.Voorstel(HuidigeMeetings()) is { } dag)
        {
            if (dag.OngeboekteMinuten > 0)
            {
                Toast.ToonActie(this, $"{dag.Kop}\n{dag.Tekst}", "Uren aanvullen…",
                    () => _ = DagvoorstelTimesheetsAsync(null), Fluent.Klok);
            }
            else
            {
                Toast.Toon(this, $"{dag.Kop}\n{dag.Tekst}", Fluent.Ster);
            }
            _ = PushMelding.StuurAsync(dag.Kop, dag.Tekst, "dagafsluiter");
        }
        WerkDagPlanBij(berichten);
    }

    /// <summary>
    /// Venstertitel én berichtenpaneel met het inbox-zero-plantje: hoe langer de reeks
    /// werkdagen met een lege inbox, hoe groter de plant. De venstertitel alleen bleek te
    /// onopvallend (zeker gemaximaliseerd), dus de plant staat ook groot in de cockpit zelf.
    /// Geen reeks = gewoon de kale titels.
    /// </summary>
    private void WerkVensterTitelBij()
    {
        var reeks = InboxZeroReeks.Huidig();
        Text = reeks > 0
            ? $"{ThemaStem.CockpitTitel()}   {InboxZeroReeks.Plant(reeks)} {reeks}"
            : ThemaStem.CockpitTitel();
        if (_berichtenGroup is not null) // in de constructor bestaat het paneel nog niet
        {
            _berichtenGroup.Text = reeks > 0
                ? $"Berichten (dubbelklik = beantwoorden)   ·   {InboxZeroReeks.Plant(reeks)} " +
                  $"{reeks} {(reeks == 1 ? "werkdag" : "werkdagen")} inbox zero"
                : "Berichten (dubbelklik = beantwoorden)";
        }
    }

    /// <summary>Een bericht identificeren over ophaalrondes heen, ook als er geen Message-ID is.</summary>
    private static string VipKenmerk(MailBericht m) =>
        m.MessageId.Length > 0 ? m.MessageId : $"{m.Van}|{m.Onderwerp}|{m.Datum.UtcTicks}";

    /// <summary>Tray-melding zodra er een bericht van een VIP bij komt.</summary>
    private void MeldNieuweVips(List<MailBericht> berichten)
    {
        var vips = berichten.Where(m => VipLijst.IsVip(m, _vipSleutels)).ToList();
        var vorige = _vipVorigeRonde;
        _vipVorigeRonde = vips.Select(VipKenmerk).ToHashSet(StringComparer.Ordinal);
        if (vorige is null)
        {
            return;
        }
        var nieuw = vips.Where(m => !vorige.Contains(VipKenmerk(m))).ToList();
        if (nieuw.Count == 0)
        {
            return;
        }
        var eerste = nieuw[0];
        var kop = eerste.Onderwerp.Length > 90 ? eerste.Onderwerp[..90] + "…" : eerste.Onderwerp;
        VipLijst.Meld("Nieuw VIP-bericht",
            nieuw.Count == 1
                ? $"{eerste.Van}: {kop}"
                : $"{eerste.Van}: {kop}\n(+ {nieuw.Count - 1} ander{(nieuw.Count == 2 ? "" : "e")})");
    }

    /// <summary>Zet de afzender of chat van het geselecteerde bericht op of van de VIP-lijst.</summary>
    private void WisselVip()
    {
        if (GeselecteerdBericht() is not { } bericht)
        {
            return;
        }
        var lijst = VipLijst.Laad();
        var sleutels = VipLijst.AlsSet(lijst);
        if (VipLijst.IsVip(bericht, sleutels))
        {
            var weg = VipLijst.SleutelsVan(bericht).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lijst.RemoveAll(v => weg.Contains(v.Sleutel.Trim()));
        }
        else
        {
            lijst.Add(VipLijst.VoorstelVoor(bericht));
        }
        VipLijst.Bewaar(lijst); // herschikt de lijst via het Gewijzigd-event
    }

    /// <summary>Vult de lijst opnieuw vanuit de laatst opgehaalde berichten, met filter en sortering.</summary>
    private void HervulBerichtenLijst()
    {
        // Beloning voor inbox zero, in de toon van het gekozen kleurenschema.
        _berichten.LegeTekst = ThemaStem.LegeInbox();
        var fouten = _laatsteFouten;
        BewaarDetailConcept();
        var geselecteerd = _getoond?.MessageId ?? ""; // selectie na het vullen herstellen

        // Filteren op bron, urgentie en zoektekst.
        IEnumerable<MailBericht> berichten = _laatsteBerichten;
        berichten = _bronFilter.SelectedIndex switch
        {
            1 => berichten.Where(m => !m.IsChat),
            2 => berichten.Where(m => m.ChatSpace.Length > 0),
            3 => berichten.Where(m => m.WhatsAppChat.Length > 0),
            4 => berichten.Where(m => m.TeamsChat.Length > 0),
            5 => berichten.Where(m => m.OutlookMail.Length > 0),
            6 => berichten.Where(m => m.Urgent),
            _ => berichten,
        };
        var zoek = _zoekFilter.Text.Trim();
        if (zoek.Length > 0)
        {
            berichten = berichten.Where(m =>
                m.Van.Contains(zoek, StringComparison.OrdinalIgnoreCase) ||
                m.Onderwerp.Contains(zoek, StringComparison.OrdinalIgnoreCase) ||
                m.Tekst.Contains(zoek, StringComparison.OrdinalIgnoreCase));
        }

        // Sorteren op de aangeklikte kolom (standaard chronologisch, oudste bovenaan).
        berichten = (_sortKolom, _sortOplopend) switch
        {
            (0, true) => berichten.OrderBy(m => m.Van, StringComparer.OrdinalIgnoreCase),
            (0, false) => berichten.OrderByDescending(m => m.Van, StringComparer.OrdinalIgnoreCase),
            (1, true) => berichten.OrderBy(m => m.Onderwerp, StringComparer.OrdinalIgnoreCase),
            (1, false) => berichten.OrderByDescending(m => m.Onderwerp, StringComparer.OrdinalIgnoreCase),
            (_, false) => berichten.OrderByDescending(m => m.Datum),
            _ => berichten.OrderBy(m => m.Datum),
        };

        // VIP's bovenaan. Dit gaat ná de gewone sortering en niet ervoor: OrderBy in LINQ is
        // stabiel, dus binnen de VIP's en binnen de rest blijft de gekozen volgorde staan.
        if (_vipSleutels.Count > 0)
        {
            berichten = berichten.OrderByDescending(m => VipLijst.IsVip(m, _vipSleutels));
        }
        // Focus-stand: het belangrijkste bovenaan — VIP (8) + urgent (4) + supportklant (2) —
        // in plaats van chronologisch; binnen gelijke score blijft de gewone volgorde staan.
        if (_bronFilter.SelectedIndex == 7)
        {
            berichten = berichten.OrderByDescending(m =>
                (VipLijst.IsVip(m, _vipSleutels) ? 8 : 0) +
                (m.Urgent ? 4 : 0) +
                (IsSupportBericht(m) ? 2 : 0));
        }

        // Sorteerpijltje op de actieve kolom.
        var koppen = new[] { "Van", "Bericht", "Ontvangen" };
        for (var k = 0; k < koppen.Length && k < _berichten.Columns.Count; k++)
        {
            _berichten.Columns[k].Text = k == _sortKolom
                ? $"{koppen[k]} {(_sortOplopend ? "▲" : "▼")}"
                : koppen[k];
        }

        _berichten.BeginUpdate();
        _berichten.Items.Clear();
        var urgentAantal = 0;
        foreach (var m in berichten)
        {
            urgentAantal += m.Urgent ? 1 : 0;
            // Klant met supportcontract: 🛟 vóór de naam en een rechtsklikactie om meteen een
            // AVG-remotesessie te starten.
            var support = IsSupportBericht(m);
            var vip = VipLijst.IsVip(m, _vipSleutels);
            // Taalvlag voor Franse/Engelse mails (niet voor chats); Nederlands krijgt geen vlag.
            var vlag = m.IsChat ? "" : TaalDetectie.Vlag(
                m.Onderwerp + " " + (m.Tekst.Length > 400 ? m.Tekst[..400] : m.Tekst));
            var naamTekst = (vip ? "⭐ " : "") + (vlag.Length > 0 ? vlag + " " : "") +
                (m.BronIcoon.Length > 0 ? m.BronIcoon + " " : "") +
                (support ? $"🛟 {m.Van}" : m.Van);
            var item = new ListViewItem(naamTekst)
            {
                Tag = m,
                UseItemStyleForSubItems = false,
                // Tooltip met de volledige kop (afgekapte onderwerpen blijven zo leesbaar).
                ToolTipText = (vip ? "VIP — krijgt voorrang\n" : "") +
                    (support ? "Supportklant — rechtsklik voor een AVG-remotesessie\n" : "") +
                    $"{m.Van}\n{m.Onderwerp}\n{ToonMoment(m.Datum)}",
            };
            var onderwerp = item.SubItems.Add(
                (m.Bijlagen.Count + m.LinkBijlagen.Count > 0 ? "📎 " : "") + m.Onderwerp);
            item.SubItems.Add(m.WhatsAppChat.Length > 0 ? "" : ToonMoment(m.Datum));
            if (vip)
            {
                // Accentkleur voor VIP's; urgent hieronder mag dat nog overschrijven, want
                // "vandaag beantwoorden" is de dringender boodschap van de twee.
                item.ForeColor = Theme.Accent;
                onderwerp.ForeColor = Theme.Accent;
            }
            if (m.Urgent)
            {
                // Rood: vandaag best beantwoorden (oordeel van Claude uit de screening).
                item.ForeColor = Theme.Danger;
                onderwerp.ForeColor = Theme.Danger;
            }
            _berichten.Items.Add(item);
        }
        foreach (var fout in fouten ?? new List<string>())
        {
            var item = new ListViewItem("⚠") { UseItemStyleForSubItems = false };
            var sub = item.SubItems.Add(fout);
            sub.ForeColor = Theme.Warn;
            item.SubItems.Add("");
            _berichten.Items.Add(item);
        }
        _berichten.EndUpdate();

        // Teller in de groepstitel: hoeveel berichten staan er (en hoeveel urgent).
        WerkBerichtenTitelBij(urgentAantal);

        // Hoort de detailweergave niet bij deze lijst (taak-mail, chat-transcript,
        // meetingdetail), dan blijft die gewoon staan zolang de gebruiker ermee bezig is.
        if (_detailLosVanLijst)
        {
            return;
        }
        // Hetzelfde bericht weer selecteren (het nieuwe object heeft het concept — inclusief
        // net getypte tekst — al uit de conceptcache meegekregen).
        if (geselecteerd.Length > 0 &&
            _berichten.Items.Cast<ListViewItem>().FirstOrDefault(i =>
                i.Tag is MailBericht m && m.MessageId == geselecteerd) is { } terugItem)
        {
            _getoond = null;
            terugItem.Selected = true; // ToonDetail volgt via SelectedIndexChanged
            return;
        }
        _getoond = null;
        ToonDetail();
    }

    /// <summary>
    /// Verwijdert een rij uit de berichtenlijst en selecteert meteen de volgende, zodat je
    /// zonder muisklikken door de inbox kunt werken (archiveren/versturen/snoozen op rij).
    /// </summary>
    /// <summary>
    /// Zet de groepstitel op de actuele stand van de lijst. Ook aanroepen na het los
    /// verwijderen van een rij (archiveren) — de teller telt wat er nu écht staat.
    /// </summary>
    private void WerkBerichtenTitelBij(int? urgent = null)
    {
        var rijen = _berichten.Items.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MailBericht>().ToList();
        var urgentAantal = urgent ?? rijen.Count(m => m.Urgent);
        _berichtenGroup.Text = rijen.Count == 0
            ? "Berichten"
            : $"Berichten · {rijen.Count}{(urgentAantal > 0 ? $" ({urgentAantal} urgent)" : "")}" +
              "  —  dubbelklik = beantwoorden";
    }

    /// <summary>
    /// Zet de taaktekst op de rij. Een lijstrij is één regel, dus een omschrijving van
    /// meerdere regels wordt met " · " aan elkaar geplakt — en verder niets afgekapt: de
    /// lijst tekent zelf met EndEllipsis, dus de rij benut altijd de volle kolombreedte.
    /// De volledige tekst komt in de tooltip, maar alleen als er echt iets wegvalt.
    /// </summary>
    private static void ZetTaakTekst(ListView lijst, ListViewItem item, string prefix, string tekst)
    {
        var regels = tekst.ReplaceLineEndings("\n").Split('\n')
            .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        var eenRegel = string.Join(" · ", regels);
        item.Text = prefix + eenRegel;

        // Past het in de kolom? Meten met het lettertype van de lijst is preciezer dan een
        // tekenlimiet, die bij lange woorden altijd verkeerd gokt.
        var breedte = lijst.Columns.Count > 0 ? lijst.Columns[0].Width : 0;
        var nodig = TextRenderer.MeasureText(item.Text, lijst.Font).Width + 28; // checkbox + marge
        item.ToolTipText = breedte > 0 && nodig > breedte ? tekst.Trim() : "";
    }

    private void VerwijderRijEnSelecteerVolgende(ListViewItem? item)
    {
        _getoond = null;
        if (item is null)
        {
            ToonDetail();
            return;
        }
        var idx = item.Index;
        item.Remove();
        WerkBerichtenTitelBij(); // anders blijft "Berichten · 1" staan boven een lege lijst
        CockpitCache.Save(HuidigeBerichten());
        if (_berichten.Items.Count > 0)
        {
            var volgende = _berichten.Items[Math.Min(idx, _berichten.Items.Count - 1)];
            if (volgende.Tag is MailBericht)
            {
                volgende.Selected = true; // ToonDetail volgt via SelectedIndexChanged
                _berichten.Focus();
                return;
            }
        }
        ToonDetail();
    }

    /// <summary>Vriendelijke tijdweergave: vandaag alleen de tijd, gisteren benoemd, anders dag+datum+tijd.</summary>
    private static string ToonMoment(DateTimeOffset d)
    {
        var lokaal = d.ToLocalTime();
        return lokaal.Date == DateTime.Today ? lokaal.ToString("HH:mm")
            : lokaal.Date == DateTime.Today.AddDays(-1) ? $"gisteren {lokaal:HH:mm}"
            // Precies middernacht = alleen de datum bekend: dan geen misleidende "00:00".
            : lokaal.TimeOfDay == TimeSpan.Zero ? lokaal.ToString("ddd d/M")
            : lokaal.ToString("ddd d/M HH:mm");
    }

    /// <summary>
    /// Kolombreedtes meeschalen met de lijstbreedte: smalle kolommen (datum, tijd, bron)
    /// blijven vast, de inhoudskolom vult de rest — geen lege ruimte of afgekapte tekst meer.
    /// </summary>
    private void SchaalAlleKolommen()
    {
        var wb = _berichten.ClientSize.Width - 6;
        if (wb > 300)
        {
            var van = Math.Clamp((int)(wb * 0.24), 160, 320);
            _berichten.Columns[0].Width = van;      // Van
            _berichten.Columns[2].Width = 110;      // Ontvangen
            _berichten.Columns[1].Width = Math.Max(150, wb - van - 110); // Bericht
        }
        var wt = _taken.ClientSize.Width - 6;
        if (wt > 260)
        {
            _taken.Columns[1].Width = 90;           // Deadline
            _taken.Columns[2].Width = 70;           // Bron
            _taken.Columns[0].Width = Math.Max(120, wt - 160); // Taak
        }
        var wm = _meetings.ClientSize.Width - 6;
        if (wm > 260)
        {
            _meetings.Columns[0].Width = 150;       // Tijd (breed genoeg voor begin–eindtijd)
            _meetings.Columns[1].Width = Math.Max(120, wm - 150); // Meeting
        }
    }

    /// <summary>De berichten die nu in de lijst staan (voor het bijwerken van de cache).</summary>
    private List<MailBericht> HuidigeBerichten() =>
        _berichten.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<MailBericht>().ToList();

    // ---------- Detailpaneel ----------

    /// <summary>
    /// Toont of verbergt het antwoordblok (conceptvak, bijsturen, Claude-concept en de andere
    /// mailknoppen). Zonder bericht in de weergave heeft geen van die knoppen betekenis.
    /// </summary>
    private void WerkAntwoordblokBij() => _conceptPanel.Visible = _getoond is not null;

    /// <summary>Zet de lege placeholder in de weergave (niets geselecteerd of niets te tonen).</summary>
    private void ToonLegeWeergave()
    {
        if (_detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(MailReplyForm.LegeWeergave);
        }
        else
        {
            _wachtendeWeergave = MailReplyForm.LegeWeergave;
        }
    }

    private void ToonDetail()
    {
        BewaarDetailConcept();
        _detailLosVanLijst = false;
        _getoond = GeselecteerdBericht();
        var html = _getoond is null ? MailReplyForm.LegeWeergave : MailReplyForm.BouwWeergave(_getoond);
        if (_detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(html);
        }
        else
        {
            _wachtendeWeergave = html;
        }
        _detailConcept.Text = _getoond?.Concept.ReplaceLineEndings("\r\n") ?? "";
        if (_getoond?.MessageId != _feedbackVoor)
        {
            _detailFeedback.Clear(); // feedback hoort bij het vorige bericht
            _feedbackVoor = _getoond?.MessageId;
        }
        _uitschrijfButton.Visible = _getoond is { UitschrijfUrl.Length: > 0 };
        _openButton.Visible = _getoond is not null;
        _outlookLeesButton.Visible = _getoond is { OutlookMail.Length: > 0 };
        _vertaalButton.Visible = _getoond is { Tekst.Length: > 3 };
        _vertaalButton.Text = _getoond is { Vertaling.Length: > 0, VertaalVerborgen: false }
            ? "🌐 Origineel" : "🌐 Vertaling";
        // Teams en CED-mails worden nooit vanuit WorkManager verstuurd (alleen concept-tekst
        // om over te nemen); de knop staat er dan bewust uit.
        _verstuurButton.Enabled =
            _getoond is not ({ TeamsChat.Length: > 0 } or { OutlookMail.Length: > 0 });
        WerkAntwoordblokBij();
        if (_getoond is { } geselecteerd)
        {
            _ = LaadHistorieAsync(geselecteerd); // eerdere berichten er lazy onder plakken
            _ = VertaalEnHertoonAsync(geselecteerd); // FR/EN-mail: NL-vertaling eronder plakken
        }
    }

    private const string HistorieKop = "————— Eerdere berichten —————";

    /// <summary>
    /// WhatsApp-stijl chatweergave: kop met profielfoto en naam (zoals WhatsApp Web),
    /// bubbels (groen rechts = ikzelf, wit links = de ander) op de kenmerkende beige
    /// achtergrond, nieuwste onderaan en meteen in beeld.
    /// </summary>
    private static string BouwWhatsAppHtml(List<WhatsAppClient.WaBericht> berichten,
        string chatNaam = "", string avatarDataUrl = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"wm-chat\" style=\"margin:-16px;background:#efeae2;" +
            "border-radius:0 0 6px 6px\">");
        if (chatNaam.Length > 0)
        {
            // Kopbalk zoals in WhatsApp Web: lichtgrijs met ronde profielfoto en de naam.
            var foto = avatarDataUrl.Length > 0
                ? $"<img src=\"{avatarDataUrl}\" style=\"width:40px;height:40px;" +
                  "border-radius:50%;object-fit:cover;flex:none\">"
                : "<div style=\"width:40px;height:40px;border-radius:50%;background:#00a884;" +
                  "color:#fff;display:flex;align-items:center;justify-content:center;" +
                  "font-size:18px;font-weight:600;flex:none\">" +
                  System.Net.WebUtility.HtmlEncode(chatNaam[..1].ToUpperInvariant()) + "</div>";
            sb.Append("<div style=\"background:#f0f2f5;padding:9px 16px;display:flex;" +
                "align-items:center;gap:12px;border-bottom:1px solid #e2e2e2\">" + foto +
                "<div style=\"font-size:15px;font-weight:600;color:#111b21\">" +
                $"{System.Net.WebUtility.HtmlEncode(chatNaam)}</div></div>");
        }
        sb.Append("<div class=\"wm-chat-scroll\" style=\"padding:14px;display:flex;" +
            "flex-direction:column-reverse;max-height:520px;overflow-y:auto\">");
        foreach (var b in berichten.AsEnumerable().Reverse())
        {
            var kleur = b.Uitgaand ? "#d9fdd3" : "#ffffff";
            var kant = b.Uitgaand ? "flex-end" : "flex-start";
            // Eigen bubbels met een puntje rechtsboven, andermans linksboven (WhatsApp-look).
            var hoeken = b.Uitgaand ? "8px 2px 8px 8px" : "2px 8px 8px 8px";
            sb.Append($"<div style=\"align-self:{kant};max-width:78%;margin:3px 0\">");
            sb.Append($"<div style=\"background:{kleur};border-radius:{hoeken};padding:6px 9px 4px;" +
                "box-shadow:0 1px 1px rgba(0,0,0,.15);font-size:13.5px;color:#111b21;" +
                "white-space:pre-wrap;word-break:break-word\">");
            if (!b.Uitgaand)
            {
                sb.Append("<div style=\"font-size:12px;font-weight:600;color:#1f7aec;" +
                    $"margin-bottom:2px\">{System.Net.WebUtility.HtmlEncode(b.Afzender)}</div>");
            }
            // Foto's in de bubbel, zoals in WhatsApp zelf (data-URL, dus offline zichtbaar).
            if (b.Beeld.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"<img src=\"{b.Beeld}\" style=\"max-width:100%;max-height:340px;" +
                    "border-radius:6px;display:block;margin:2px 0 4px\">");
            }
            sb.Append(System.Net.WebUtility.HtmlEncode(b.Tekst));
            // Eigen berichten krijgen zoals in WhatsApp vinkjes naast het tijdstip.
            sb.Append("<div style=\"font-size:10.5px;color:#667781;text-align:right;margin-top:3px\">" +
                System.Net.WebUtility.HtmlEncode(b.Tijd) +
                (b.Uitgaand ? " <span style=\"color:#53bdeb\">✓✓</span>" : "") + "</div>");
            sb.Append("</div>");
            // Emoji-reacties (❤️ 👍 …) als wit pilletje dat net als in WhatsApp half over de
            // onderrand van de bubbel hangt.
            if (b.Reacties.Length > 0)
            {
                var reactieKant = b.Uitgaand ? "flex-end" : "flex-start";
                sb.Append("<div style=\"display:flex;justify-content:" + reactieKant +
                    ";margin:-7px 6px 0\"><span style=\"background:#ffffff;border-radius:11px;" +
                    "box-shadow:0 1px 2px rgba(0,0,0,.25);padding:1px 7px;font-size:12px;" +
                    "color:#111b21\">" + System.Net.WebUtility.HtmlEncode(b.Reacties) +
                    "</span></div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>
    /// Teams-stijl chatweergave: kop met paarse initiaalcirkel, eigen berichten in lichtpaars
    /// rechts, andermans in lichtgrijs links met naam en tijd erboven, nieuwste onderaan.
    /// </summary>
    private static string BouwTeamsHtml(List<TeamsClient.TeamsChatBericht> berichten, string chatNaam)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"wm-chat\" style=\"margin:-16px;background:#ffffff;" +
            "border-radius:0 0 6px 6px\">");
        if (chatNaam.Length > 0)
        {
            sb.Append("<div style=\"background:#f5f5f5;padding:9px 16px;display:flex;" +
                "align-items:center;gap:12px;border-bottom:1px solid #e0e0e0\">" +
                "<div style=\"width:40px;height:40px;border-radius:50%;background:#5b5fc7;" +
                "color:#fff;display:flex;align-items:center;justify-content:center;" +
                "font-size:18px;font-weight:600;flex:none\">" +
                System.Net.WebUtility.HtmlEncode(chatNaam[..1].ToUpperInvariant()) + "</div>" +
                "<div style=\"font-size:15px;font-weight:600;color:#242424\">" +
                $"{System.Net.WebUtility.HtmlEncode(chatNaam)}</div></div>");
        }
        sb.Append("<div class=\"wm-chat-scroll\" style=\"padding:14px;display:flex;" +
            "flex-direction:column-reverse;max-height:520px;overflow-y:auto\">");
        foreach (var b in berichten.AsEnumerable().Reverse())
        {
            var kleur = b.Uitgaand ? "#e8ebfa" : "#f5f5f5";
            var kant = b.Uitgaand ? "flex-end" : "flex-start";
            sb.Append($"<div style=\"align-self:{kant};max-width:78%;margin:4px 0\">");
            if (!b.Uitgaand && (b.Auteur.Length > 0 || b.Tijd.Length > 0))
            {
                sb.Append("<div style=\"font-size:11.5px;color:#616161;margin:0 0 2px 4px\">" +
                    System.Net.WebUtility.HtmlEncode(b.Auteur) +
                    (b.Tijd.Length > 0 ? $" · {System.Net.WebUtility.HtmlEncode(b.Tijd)}" : "") +
                    "</div>");
            }
            sb.Append($"<div style=\"background:{kleur};border-radius:6px;padding:7px 11px;" +
                "font-size:13.5px;color:#242424;white-space:pre-wrap;word-break:break-word\">");
            // Foto's in de bubbel, zoals in Teams zelf (data-URL, dus offline zichtbaar).
            if (b.Beeld.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"<img src=\"{b.Beeld}\" style=\"max-width:100%;max-height:340px;" +
                    "border-radius:6px;display:block;margin:2px 0 4px\">");
            }
            sb.Append(System.Net.WebUtility.HtmlEncode(b.Tekst));
            if (b.Uitgaand && b.Tijd.Length > 0)
            {
                sb.Append("<div style=\"font-size:10.5px;color:#616161;text-align:right;" +
                    $"margin-top:3px\">{System.Net.WebUtility.HtmlEncode(b.Tijd)}</div>");
            }
            sb.Append("</div></div>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
    }

    private sealed record TeamsHistorie(
        List<TeamsClient.TeamsChatBericht> Berichten, DateTimeOffset Opgehaald);

    private static readonly string TeamsHistorieFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "teams-historie.json");

    private static Dictionary<string, TeamsHistorie> LaadTeamsHistorie()
    {
        try
        {
            if (File.Exists(TeamsHistorieFile) &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TeamsHistorie>>(
                    File.ReadAllText(TeamsHistorieFile)) is { } cache)
            {
                return new Dictionary<string, TeamsHistorie>(cache, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Onleesbare cache: gewoon opnieuw opbouwen.
        }
        return new Dictionary<string, TeamsHistorie>(StringComparer.OrdinalIgnoreCase);
    }

    private static void BewaarTeamsHistorie(Dictionary<string, TeamsHistorie> cache)
    {
        try
        {
            foreach (var sleutel in cache
                .Where(p => p.Value.Opgehaald < DateTimeOffset.Now.AddMonths(-1))
                .Select(p => p.Key).ToList())
            {
                cache.Remove(sleutel);
            }
            File.WriteAllText(TeamsHistorieFile, System.Text.Json.JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Cache is comfort, geen noodzaak.
        }
    }

    /// <summary>
    /// Kent de gecachte Teams-historiek het bericht uit de zijbalk-preview al? Zo ja, dan is
    /// de bubbelweergave actueel en mag de rij meteen (volledig) getoond worden.
    /// </summary>
    private static bool TeamsHistorieActueel(TeamsHistorie h, string preview)
    {
        var kern = preview.Trim().TrimEnd('…');
        // Zijbalkvorm "Naam: bericht" (of "Jij: bericht") → alleen het bericht zelf.
        var dp = kern.IndexOf(": ", StringComparison.Ordinal);
        if (dp > 0 && dp <= 30)
        {
            kern = kern[(dp + 2)..].Trim();
        }
        if (kern.Length < 4)
        {
            return false; // te weinig houvast (media zonder tekst): dan gewoon vers laden
        }
        if (kern.Length > 30)
        {
            kern = kern[..30];
        }
        return h.Berichten.TakeLast(3).Any(b =>
            b.Tekst.Contains(kern, StringComparison.OrdinalIgnoreCase));
    }

    private readonly HashSet<string> _teamsVoorladenBezig = new(StringComparer.Ordinal);

    /// <summary>
    /// Laadt verse Teams-chats één voor één volledig (dat opent de chat in de verborgen
    /// sessie), zet het resultaat in het TeamsVers-register en ververst daarna de lijst —
    /// pas dan verschijnt de rij, compleet en klikklaar. Draait op de UI-thread (WebView2).
    /// </summary>
    private async Task TeamsVoorladenAsync(List<MailBericht> rijen)
    {
        var klaar = 0;
        foreach (var rij in rijen)
        {
            if (!_teamsVoorladenBezig.Add(rij.MessageId))
            {
                continue; // vorige poging loopt nog
            }
            try
            {
                await LaadHistorieAsync(rij);
            }
            catch
            {
                // Mislukt: de rij verschijnt dan met alleen de preview — beter dan zoekraken,
                // want de chat kan intussen al als gelezen gemarkeerd zijn.
            }
            finally
            {
                _teamsVoorladenBezig.Remove(rij.MessageId);
            }
            var vers = VersRegister.TeamsVers.Load();
            if (vers.TryGetValue(rij.MessageId, out var v))
            {
                v.Geladen = true;
                v.Html = rij.Html;
                v.Tekst = rij.Tekst;
                VersRegister.TeamsVers.Bewaar(vers);
                klaar++;
            }
        }
        if (klaar > 0 && !IsDisposed)
        {
            await VerversBerichtenAsync();
        }
    }

    private sealed record WaHistorie(
        List<WhatsAppClient.WaBericht> Berichten, string Avatar, DateTimeOffset Opgehaald);

    private static readonly string WaHistorieFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "wa-historie.json");

    /// <summary>
    /// Persistente cache van de laatst opgehaalde WhatsApp-historiek per chat: bij een klik
    /// staan de bubbels er dan meteen, terwijl de verse berichten nog geladen worden.
    /// </summary>
    private static Dictionary<string, WaHistorie> LaadWaHistorie()
    {
        try
        {
            if (File.Exists(WaHistorieFile) &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, WaHistorie>>(
                    File.ReadAllText(WaHistorieFile)) is { } cache)
            {
                return new Dictionary<string, WaHistorie>(cache, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Onleesbare cache: gewoon opnieuw opbouwen.
        }
        return new Dictionary<string, WaHistorie>(StringComparer.OrdinalIgnoreCase);
    }

    private static void BewaarWaHistorie(Dictionary<string, WaHistorie> cache)
    {
        try
        {
            // Oude chats na een maand opruimen zodat het bestand niet blijft groeien.
            foreach (var sleutel in cache
                .Where(p => p.Value.Opgehaald < DateTimeOffset.Now.AddMonths(-1))
                .Select(p => p.Key).ToList())
            {
                cache.Remove(sleutel);
            }
            File.WriteAllText(WaHistorieFile, System.Text.Json.JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Cache is comfort, geen noodzaak.
        }
    }

    /// <summary>
    /// Kent de gecachte historiek het bericht uit de zijbalk-preview al? Zo ja, dan is de
    /// bubbelweergave actueel en mag de rij meteen (volledig) getoond worden.
    /// </summary>
    private static bool WaHistorieActueel(WaHistorie h, string preview)
    {
        // Voorvoegsels van de zijbalk ("Jij:", vinkjes, media-icoontjes) doen niet mee.
        var kern = System.Text.RegularExpressions.Regex
            .Replace(preview, @"^(Jij|You|✓+|🎤|📷|📄|🖼)[:\s]*", "").Trim().TrimEnd('…');
        if (kern.Length < 4)
        {
            return false; // te weinig houvast (media zonder tekst): dan gewoon vers laden
        }
        if (kern.Length > 30)
        {
            kern = kern[..30];
        }
        return h.Berichten.TakeLast(3).Any(b =>
            b.Tekst.Contains(kern, StringComparison.OrdinalIgnoreCase));
    }

    private readonly HashSet<string> _waVoorladenBezig = new(StringComparer.Ordinal);

    /// <summary>
    /// Laadt verse WhatsApp-chats één voor één volledig (dat opent de chat in de verborgen
    /// sessie), zet het resultaat in het WaVers-register en ververst daarna de lijst — pas
    /// dan verschijnt de rij, compleet en klikklaar. Draait op de UI-thread (WebView2).
    /// </summary>
    private async Task WaVoorladenAsync(List<MailBericht> rijen)
    {
        var klaar = 0;
        foreach (var rij in rijen)
        {
            if (!_waVoorladenBezig.Add(rij.MessageId))
            {
                continue; // vorige poging loopt nog
            }
            try
            {
                await LaadHistorieAsync(rij);
            }
            catch
            {
                // Mislukt: de rij verschijnt dan met alleen de preview — beter dan zoekraken,
                // want de chat kan intussen al als gelezen gemarkeerd zijn.
            }
            finally
            {
                _waVoorladenBezig.Remove(rij.MessageId);
            }
            var vers = VersRegister.WaVers.Load();
            if (vers.TryGetValue(rij.MessageId, out var v))
            {
                v.Geladen = true;
                v.Html = rij.Html;
                v.Tekst = rij.Tekst;
                VersRegister.WaVers.Bewaar(vers);
                klaar++;
            }
        }
        if (klaar > 0 && !IsDisposed)
        {
            await VerversBerichtenAsync();
        }
    }

    /// <summary>Diagnose voor de historie-ophaler (%APPDATA%\WorkManager\historie-debug.txt).</summary>
    private static void LogHistorie(string melding)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WorkManager", "historie-debug.txt"),
                $"{DateTime.Now:HH:mm:ss} {melding}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>
    /// Plakt de laatste ±10 eerdere berichten van de conversatie onder het bericht:
    /// bij Gmail de rest van de thread (ook gelezen mails), bij WhatsApp de laatste
    /// chatberichten (dat opent de chat, dus WhatsApp zet hem op gelezen).
    /// </summary>
    private async Task LaadHistorieAsync(MailBericht bericht)
    {
        if (bericht.MessageId.Length == 0 || bericht.Tekst.Contains(HistorieKop))
        {
            return;
        }
        List<string> regels;
        try
        {
            if (bericht.WhatsAppChat.Length > 0)
            {
                // Eerst de gecachte historiek van de vorige keer tonen: klikken voelt dan
                // meteen vlot, terwijl de verse berichten op de achtergrond binnenkomen.
                var waCacheAlles = LaadWaHistorie();
                if (waCacheAlles.TryGetValue(bericht.WhatsAppChat, out var waCache) &&
                    waCache.Berichten.Count > 0 && bericht.Html.Length == 0)
                {
                    bericht.Html = BouwWhatsAppHtml(
                        waCache.Berichten, bericht.WhatsAppChat, waCache.Avatar);
                    if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } cacheCore)
                    {
                        cacheCore.NavigateToString(MailReplyForm.BouwWeergave(bericht));
                    }
                }
                // WhatsApp krijgt een echte bubbelweergave in plaats van platte regels.
                var (wa, avatar) = await WhatsAppClient.Instance.LaatsteBerichtenAsync(
                    bericht.WhatsAppChat, 15, _cts.Token);
                if (wa.Count == 0)
                {
                    LogHistorie($"{bericht.Van}: 0 berichten gevonden");
                    return;
                }
                waCacheAlles[bericht.WhatsAppChat] = new WaHistorie(wa, avatar, DateTimeOffset.Now);
                BewaarWaHistorie(waCacheAlles);
                if (bericht.Tekst.Contains(HistorieKop))
                {
                    return;
                }
                bericht.Html = BouwWhatsAppHtml(wa, bericht.WhatsAppChat, avatar);
                // Ook als tekst bewaren: daar leest Claude uit voor concepten.
                bericht.Tekst += $"\n\n{HistorieKop}\n" + string.Join("\n",
                    wa.AsEnumerable().Reverse()
                        .Select(b => $"[{b.Tijd}] {b.Afzender}: {b.Tekst}"));
                if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } waCore)
                {
                    waCore.NavigateToString(MailReplyForm.BouwWeergave(bericht));
                }
                return;
            }
            if (bericht.TeamsChat.Length > 0)
            {
                // Zelfde patroon als WhatsApp: eerst de gecachte bubbels direct tonen,
                // daarna de verse berichten ophalen (dat opent de chat in de verborgen
                // Teams-sessie en markeert hem daar als gelezen).
                var tCacheAlles = LaadTeamsHistorie();
                if (tCacheAlles.TryGetValue(bericht.TeamsChat, out var tCache) &&
                    tCache.Berichten.Count > 0 && bericht.Html.Length == 0)
                {
                    bericht.Html = BouwTeamsHtml(tCache.Berichten, bericht.TeamsChat);
                    if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } tCore)
                    {
                        tCore.NavigateToString(MailReplyForm.BouwWeergave(bericht));
                    }
                }
                var tb = await TeamsClient.Instance.LaatsteBerichtenAsync(
                    bericht.TeamsChat, 15, _cts.Token);
                if (tb.Count == 0)
                {
                    LogHistorie($"{bericht.Van}: 0 berichten gevonden");
                    return;
                }
                tCacheAlles[bericht.TeamsChat] = new TeamsHistorie(tb, DateTimeOffset.Now);
                BewaarTeamsHistorie(tCacheAlles);
                if (bericht.Tekst.Contains(HistorieKop))
                {
                    return;
                }
                bericht.Html = BouwTeamsHtml(tb, bericht.TeamsChat);
                // Ook als tekst bewaren: daar leest Claude uit voor concepten.
                bericht.Tekst += $"\n\n{HistorieKop}\n" + string.Join("\n", tb
                    .Select(b => $"[{b.Tijd}] {(b.Uitgaand ? "Maarten (ikzelf)" : b.Auteur)}: " +
                        $"{(b.Beeld.Length > 0 ? "[📷 afbeelding] " : "")}{b.Tekst}"));
                if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } tCore2)
                {
                    tCore2.NavigateToString(MailReplyForm.BouwWeergave(bericht));
                }
                return;
            }
            if (!bericht.IsChat)
            {
                regels = await GmailClient.ThreadAsync(
                    MailReplySettings.Load(), bericht.MessageId, 10, _cts.Token);
            }
            else
            {
                return; // Google Chat toont zijn transcript al; Outlook: volledige mail staat er al
            }
        }
        catch (Exception ex)
        {
            LogHistorie($"{bericht.Van}: FOUT {ex.Message}");
            return; // best effort: geen geschiedenis is geen ramp
        }
        if (regels.Count == 0)
        {
            LogHistorie($"{bericht.Van}: 0 berichten gevonden");
            return;
        }
        if (bericht.Tekst.Contains(HistorieKop))
        {
            return;
        }
        regels.Reverse(); // nieuwste bovenaan: minder scrollen naar wat er net gezegd is
        bericht.Tekst += $"\n\n{HistorieKop}\n{string.Join("\n\n", regels)}";
        if (bericht.Html.Length > 0)
        {
            // HTML-mails tonen mail.Html in de weergave, dus daar hoort de historie ook bij.
            bericht.Html +=
                "<hr style=\"margin:24px 0;border:none;border-top:1px solid #ddd\">" +
                "<div style=\"font-size:12px;color:#444\"><b>Eerdere berichten</b>" +
                string.Join("", regels.Select(r =>
                    $"<p style=\"white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(r)}</p>")) +
                "</div>";
        }
        if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(MailReplyForm.BouwWeergave(bericht));
        }
    }

    /// <summary>Aanmeldknoppen alleen tonen als aanmelden nodig is (eerste koppeling of MFA verlopen).</summary>
    private void WerkAanmeldKnoppenBij()
    {
        _teamsKoppelButton.Visible = !TeamsClient.Aangemeld;
        _outlookKoppelButton.Visible = !OutlookClient.Aangemeld;
        _waKoppelButton.Visible = WhatsAppClient.OoitGekoppeld && !WhatsAppClient.Aangemeld;
        WerkSessieLampjesBij();
    }

    /// <summary>
    /// Statuslampjes per verborgen sessie — alleen in beeld als er iets mis is: 🟠 = wacht
    /// op aanmelding, ⏸ = gepauzeerd na fouten. Een rij groene lampjes is ruis; het menu
    /// erachter (aanmelden, gezondheid) blijft via het ▾-verversmenu bereikbaar.
    /// </summary>
    private void WerkSessieLampjesBij()
    {
        var problemen = new List<string>();
        if (TeamsClient.OoitGekoppeld && !TeamsClient.Aangemeld)
        {
            problemen.Add("🟠 Teams");
        }
        if (OutlookClient.OoitGekoppeld && !OutlookClient.Aangemeld)
        {
            problemen.Add("🟠 Outlook");
        }
        if (WhatsAppClient.OoitGekoppeld && !WhatsAppClient.Aangemeld)
        {
            problemen.Add("🟠 WhatsApp");
        }
        foreach (var bron in new[] { "Gmail", "Google Chat", "WhatsApp", "Teams", "Outlook" })
        {
            if (BronGezondheid.Gepauzeerd(bron, out _) &&
                !problemen.Any(p => p.EndsWith(bron, StringComparison.Ordinal)))
            {
                problemen.Add($"⏸ {bron}");
            }
        }
        _sessieStatus.Text = string.Join("   ", problemen);
        _sessieStatus.Visible = problemen.Count > 0;
    }

    /// <summary>
    /// Splitst een OWA-adresregel ("Jan Jansen; Piet Peeters (CED BE)") in losse namen
    /// voor de Aan/Cc-weergave in de mailkop.
    /// </summary>
    private static List<string> SplitsAdresregel(string regel) =>
        regel.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .ToList();

    /// <summary>
    /// Klein overzichtsvenster met de gezondheid per bron (laatste sync, fouten, pauzes)
    /// en de crashtellers van vandaag — alles wat anders in losse debugbestanden zit.
    /// </summary>
    private void ToonGezondheid()
    {
        using var venster = new Form
        {
            Text = "Gezondheid bronnen – WorkManager",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(860, 420),
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var tekst = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Cascadia Mono", 9.5f),
            Text = BronGezondheid.Overzicht(),
            BorderStyle = BorderStyle.None,
        };
        venster.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), Controls = { tekst },
        });
        Theme.Apply(venster);
        venster.ShowDialog(this);
    }

    /// <summary>Haalt de volledige tekst van een CED-mail op (de mail wordt daarbij als gelezen gemarkeerd).</summary>
    private async Task HaalOutlookMailAsync()
    {
        if (_getoond is not { OutlookMail.Length: > 0 } bericht)
        {
            return;
        }
        _outlookLeesButton.Enabled = false;
        _outlookLeesButton.Bezig = true;
        try
        {
            var (tekst, html, exact, url, aan, cc) = await OutlookClient.Instance.LeesMailAsync(
                bericht.Van, bericht.Onderwerp, _cts.Token);
            if (tekst.Length == 0 && html.Length == 0)
            {
                Toast.Toon(this, "Mail niet gevonden in de Outlook-weergave", Fluent.Mail);
                return;
            }
            bericht.Tekst = tekst + "\n\n(Beantwoorden: in Outlook zelf.)";
            bericht.Html = html;
            bericht.Aan = SplitsAdresregel(aan);
            bericht.Cc = SplitsAdresregel(cc);
            if (url.Length > 0)
            {
                bericht.OutlookUrl = url;
            }
            if (exact is { } moment)
            {
                bericht.Datum = moment;
            }
            if (ReferenceEquals(_getoond, bericht) && _detail.CoreWebView2 is { } core)
            {
                core.NavigateToString(MailReplyForm.BouwWeergave(bericht));
            }
            Toast.Toon(this, "Volledige mail opgehaald (in Outlook nu als gelezen)", Fluent.Mail);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Mail ophalen mislukt: {ex.Message}", Fluent.Mail);
        }
        finally
        {
            _outlookLeesButton.Bezig = false;
            _outlookLeesButton.Enabled = true;
        }
    }

    /// <summary>
    /// Opent het timesheet-dialoog en boekt de regel direct door als werkuur in urbanadmin;
    /// lukt dat niet, dan blijft hij in de lokale wachtrij en probeert de app het later opnieuw.
    /// </summary>
    private async Task MaakTimesheetAsync(string? klantVoorstel, DateOnly datum, int minuten,
        string omschrijving, string bron, TimeOnly? van = null)
    {
        using var dialog = new TimesheetForm(klantVoorstel, datum, minuten, omschrijving);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        // Zonder bekende starttijd (mail, taak): je boekt achteraf, dus het blok eindigt nú
        // (afgerond op vijf minuten) en de starttijd is dat moment min de minuten. Maak je de
        // duur in de dialoog langer, dan schuift de starttijd dus vroeger — de eindtijd blijft.
        // Bij meetings blijft de echte starttijd van de afspraak staan.
        if (van is null)
        {
            var nu = DateTime.Now;
            van = new TimeOnly(nu.Hour, nu.Minute / 5 * 5).AddMinutes(-dialog.Minuten);
        }
        TimesheetStore.Voeg(new TimesheetRegel
        {
            Datum = dialog.Datum,
            Van = van,
            Klant = dialog.Klant,
            Minuten = dialog.Minuten,
            Omschrijving = dialog.Omschrijving,
            Bron = bron,
        });
        try
        {
            var blok = $"{van:HH\\:mm}–{van.Value.AddMinutes(dialog.Minuten):HH\\:mm}";
            var n = await TimesheetStore.BoekDoorAsync(_cts.Token);
            Toast.Toon(this, n > 0
                ? $"Timesheet in urbanadmin geboekt: {dialog.Klant} · {dialog.Minuten} min ({blok})"
                : $"Timesheet in wachtrij: {dialog.Klant} · {dialog.Minuten} min ({blok})", Fluent.Klok);
        }
        catch (Exception ex)
        {
            Toast.Toon(this,
                $"Timesheet in wachtrij (doorboeken mislukte: {ex.Message})", Fluent.Klok);
        }
    }

    /// <summary>
    /// Boekt een volledige CED-dag: de werkdag opgedeeld in blokken rond de meetings uit de
    /// Office 365-agenda. Toont eerst het voorstel — pas na bevestiging gaat er iets naar de
    /// wachtrij en naar urbanadmin.
    /// </summary>
    private async Task CedDagTimesheetsAsync(ModernButton knop)
    {
        var dag = DateOnly.FromDateTime(DateTime.Now).AddDays(_meetingsOffset);

        List<AgendaClient.AgendaItem> meetings;
        knop.Bezig = true;
        try
        {
            meetings = OutlookClient.OoitGekoppeld
                ? await CedVoorDagAsync(dag)
                : new List<AgendaClient.AgendaItem>();
        }
        catch (Exception ex)
        {
            // Zonder agenda kun je nog altijd één blok van 8 tot 17 boeken; zeg alleen eerlijk
            // dat de meetings ontbreken, anders lijkt een lege dag een volle dag.
            Toast.Toon(this, $"O365-agenda niet leesbaar ({ex.Message}) — voorstel zonder meetings", Fluent.Klok);
            meetings = new List<AgendaClient.AgendaItem>();
        }
        finally
        {
            knop.Bezig = false;
        }

        // De lader voor als er in het venster naar een andere dag gewisseld wordt (bv. gisteren
        // vergeten te boeken); zonder agenda gewoon een leeg voorstel, net als hierboven.
        async Task<List<AgendaClient.AgendaItem>> LaadDagAsync(DateOnly d)
        {
            try
            {
                return OutlookClient.OoitGekoppeld
                    ? await CedVoorDagAsync(d)
                    : new List<AgendaClient.AgendaItem>();
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"O365-agenda niet leesbaar ({ex.Message}) — voorstel zonder meetings", Fluent.Klok);
                return new List<AgendaClient.AgendaItem>();
            }
        }

        using var dialog = new CedDagForm(dag, meetings, LaadDagAsync);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var blok in dialog.Gekozen)
        {
            TimesheetStore.Voeg(new TimesheetRegel
            {
                Datum = dialog.Dag,
                Van = blok.Van,
                Klant = "CED",
                Minuten = blok.Minuten,
                Omschrijving = blok.Omschrijving,
                Bron = blok.IsMeeting ? "meeting" : "ced-dag",
            });
        }

        try
        {
            var n = await TimesheetStore.BoekDoorAsync(_cts.Token);
            Toast.Toon(this, n > 0
                ? $"{dialog.Gekozen.Count} CED-timesheet(s) aangemaakt, {n} geboekt in urbanadmin"
                : $"{dialog.Gekozen.Count} CED-timesheet(s) in de wachtrij", Fluent.Klok);
        }
        catch (Exception ex)
        {
            Toast.Toon(this,
                $"{dialog.Gekozen.Count} CED-timesheet(s) in wachtrij (doorboeken mislukte: {ex.Message})",
                Fluent.Klok);
        }
    }

    /// <summary>
    /// Dagvoorstel over álle klanten: Claude vat de activiteitenlog, contextswitches,
    /// launcher-log en meetings van de getoonde dag samen tot timesheetregels. Eerst het
    /// controlvenster, pas daarna de wachtrij en urbanadmin — zelfde stramien als de CED-dag.
    /// </summary>
    private async Task DagvoorstelTimesheetsAsync(ModernButton? knop)
    {
        var dag = DateOnly.FromDateTime(DateTime.Now).AddDays(_meetingsOffset);

        if (knop is not null)
        {
            knop.Bezig = true;
        }
        List<TimesheetRegel> voorstel;
        string toelichting;
        try
        {
            List<AgendaClient.AgendaItem> meetings;
            try
            {
                meetings = OutlookClient.OoitGekoppeld
                    ? await CedVoorDagAsync(dag)
                    : new List<AgendaClient.AgendaItem>();
            }
            catch
            {
                meetings = new List<AgendaClient.AgendaItem>(); // voorstel kan ook zonder agenda
            }
            (voorstel, toelichting) = await ActiviteitenLog.VoorstelAsync(dag, meetings, _cts.Token);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Dagvoorstel mislukte: {ex.Message}", Fluent.Document);
            return;
        }
        finally
        {
            if (knop is not null)
            {
                knop.Bezig = false;
            }
        }
        if (voorstel.Count == 0)
        {
            Toast.Toon(this, "Geen bruikbaar voorstel — nog te weinig sporen vandaag?", Fluent.Document);
            return;
        }

        using var dialog = new TimesheetVoorstelForm(dag, voorstel, toelichting);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        foreach (var regel in dialog.Gekozen)
        {
            TimesheetStore.Voeg(regel);
        }
        try
        {
            var n = await TimesheetStore.BoekDoorAsync(_cts.Token);
            Toast.Toon(this, n > 0
                ? $"{dialog.Gekozen.Count} timesheet(s) aangemaakt, {n} geboekt in urbanadmin"
                : $"{dialog.Gekozen.Count} timesheet(s) in de wachtrij", Fluent.Klok);
        }
        catch (Exception ex)
        {
            Toast.Toon(this,
                $"{dialog.Gekozen.Count} timesheet(s) in wachtrij (doorboeken mislukte: {ex.Message})",
                Fluent.Klok);
        }
    }

    /// <summary>Maakt een losse taak in "Mijn taken" rechtstreeks vanuit de cockpit.</summary>
    private async Task MaakNieuweTaakAsync()
    {
        using var dialog = new MailTaakForm();
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
        Toast.Toon(this, "Taak toegevoegd aan Mijn taken", Fluent.Checkbox);
        await VerversTakenAsync();
    }

    /// <summary>
    /// Maakt een teamtaak rechtstreeks vanuit de cockpit, met dezelfde dialoog als het
    /// Taken team-venster (teamlid, prioriteit, subtaken).
    /// </summary>
    private void MaakNieuweTeamTaak()
    {
        using var dialog = new TeamTaakBewerkForm(TeamTaskStore.Load().Leden, new TeamTaak());
        dialog.Text = "Nieuwe teamtaak";
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var taak = new TeamTaak
        {
            Tekst = dialog.TaakTekst,
            Lid = dialog.Lid,
            Prioriteit = dialog.Prioriteit,
            Subtaken = dialog.Subtaken,
        };
        // Staat het Taken team-venster open, dan telt zíjn geheugenkopie: rechtstreeks in
        // het bestand schrijven zou bij de eerstvolgende save daar weer verdwijnen.
        if (Application.OpenForms.OfType<TeamTasksForm>().FirstOrDefault() is { } open)
        {
            open.VoegTaakToe(taak);
        }
        else
        {
            var data = TeamTaskStore.Load();
            data.Taken.Add(taak);
            TeamTaskStore.Save(data);
        }
        Toast.Toon(this, $"Teamtaak voor {taak.Lid} toegevoegd", Fluent.People);
    }

    private bool _plekVoorstelGetoond;

    /// <summary>
    /// Kom je vaker op dezelfde naamloze plek, stel dan één keer per sessie voor om die te
    /// benoemen: met een naam worden positiepunten herkend en krijgen bezoeken een klant.
    /// </summary>
    private void ToonPlekVoorstelEenmalig()
    {
        if (_plekVoorstelGetoond || LocatieLog.NaamloosCluster() is not { } cluster)
        {
            return;
        }
        _plekVoorstelGetoond = true;
        Toast.ToonActie(this,
            $"Je kwam op {cluster.Dagen} dagen op dezelfde plek zonder naam", "Plek benoemen…",
            () => VraagPlekNaam(cluster.Lat, cluster.Lon), Fluent.Globe);
    }

    private void VraagPlekNaam(double lat, double lon)
    {
        using var dialog = new Form
        {
            Text = "Plek benoemen",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(400, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var uitleg = new Label
        {
            Text = "Naam voor deze plek (met de klantnaam erin, zoals \"Lauryssens\"):",
            AutoSize = true, Location = new Point(16, 16),
        };
        var naamBox = new TextBox { Location = new Point(16, 44), Width = 368 };
        var kaartKnop = new ModernButton
        {
            Text = "Toon op kaart", Width = 125, Location = new Point(16, 106),
        };
        var coords = FormattableString.Invariant($"{lat:0.#####},{lon:0.#####}");
        kaartKnop.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo($"https://www.google.com/maps?q={coords}")
            {
                UseShellExecute = true,
            });
        var negeerKnop = new ModernButton
        {
            Text = "Niet meer vragen", Width = 145, Location = new Point(149, 106),
        };
        negeerKnop.Click += (_, _) =>
        {
            LocatieLog.NegeerCluster(lat, lon);
            dialog.DialogResult = DialogResult.Cancel;
        };
        var ok = new ModernButton
        {
            Text = "Opslaan", Width = 84, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(300, 106),
        };
        dialog.Controls.AddRange(new Control[] { uitleg, naamBox, kaartKnop, negeerKnop, ok });
        dialog.AcceptButton = ok;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) == DialogResult.OK && naamBox.Text.Trim().Length > 0)
        {
            LocatieLog.BenoemCluster(lat, lon, naamBox.Text.Trim());
            Toast.Toon(this, $"\"{naamBox.Text.Trim()}\" onthouden als plek", Fluent.Check);
        }
    }

    /// <summary>
    /// Geeft het bronbericht dat aan een lokale taak hangt. Ontbreekt het (oudere taken,
    /// bv. @mention-taken van vóór de bron-koppeling), dan wordt de afzender uit de
    /// taaktekst gehaald ("Reageren op &lt;naam&gt; (…)") en opgezocht in de laatst bekende
    /// cockpit-berichten; een treffer wordt meteen op de taak bewaard (backfill), zodat de
    /// koppeling daarna blijvend is. Geeft null als er geen bron te bepalen valt.
    /// </summary>
    private static TaakMail? BepaalTaakBron(MijnTaak taak)
    {
        if (taak.Mail is { } bestaand &&
            (bestaand.Tekst.Length > 0 || bestaand.Link.Length > 0 || bestaand.MessageId.Length > 0))
        {
            return bestaand;
        }
        // Afzender uit "Reageren op <naam> (" of "Opvolgen: <naam> …" afleiden.
        var naam = System.Text.RegularExpressions.Regex.Match(
            taak.Tekst, @"^Reageren op (.+?)\s*\(").Groups[1].Value.Trim();
        if (naam.Length == 0)
        {
            return taak.Mail;
        }
        var bron = CockpitCache.Load().FirstOrDefault(b =>
            b.Van.Equals(naam, StringComparison.OrdinalIgnoreCase) ||
            b.Van.Contains(naam, StringComparison.OrdinalIgnoreCase));
        if (bron is null)
        {
            return taak.Mail; // afzender (nog) niet in de lijst — niets om aan te koppelen
        }
        var taakMail = new TaakMail
        {
            Van = bron.Van,
            VanAdres = bron.VanAdres,
            AntwoordAan = bron.AntwoordAan,
            Onderwerp = bron.Onderwerp,
            Tekst = bron.Tekst.Length > 8000 ? bron.Tekst[..8000] + "…" : bron.Tekst,
            Link = BerichtUrl(bron),
            Datum = bron.Datum,
            MessageId = bron.MessageId,
            Referenties = bron.Referenties.ToList(),
            ChatSpace = bron.ChatSpace,
            WhatsAppChat = bron.WhatsAppChat,
        };
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } opgeslagen)
        {
            opgeslagen.Mail = taakMail;
            MijnTaakStore.Save(data);
        }
        taak.Mail = taakMail;
        return taakMail;
    }

    /// <summary>Bewerkt tekst, categorie, prioriteit en deadline van de geselecteerde (lokale) taak.</summary>
    private async Task BewerkTaakAsync()
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        if (rij.Bron == "Snooze")
        {
            Toast.Toon(this, "Dit is een vooruitblik — de mail komt vanzelf terug in de inbox", Fluent.Klok);
            return;
        }
        if (rij.Lokaal is not { } taak)
        {
            await BewerkAsanaTaakAsync(rij);
            return;
        }
        await BewerkLokaleTaakAsync(taak);
    }

    /// <summary>
    /// De volwaardige bewerkdialoog voor een lokale taak; ook het Anticiperen-venster
    /// gebruikt hem (via de callback in zijn constructor).
    /// </summary>
    internal async Task BewerkLokaleTaakAsync(MijnTaak taak)
    {
        using var dialog = new Form
        {
            Text = "Taak bewerken",
            StartPosition = FormStartPosition.CenterParent,
            // Iets breder dan vroeger: op de start- en deadlineregel staan ook uitstelknopjes.
            // ClientSize, niet Size: dan is de ruimte waarin de knoppen moeten passen exact
            // bekend en hangt de layout niet af van de randdikte van het thema.
            ClientSize = new Size(504, 394),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var tekstLabel = new Label { Text = "Taak", AutoSize = true, Location = new Point(16, 16) };
        var tekstBox = new TextBox
        {
            Text = taak.Tekst,
            Location = new Point(16, 38),
            Width = 470,
            Multiline = true,
            // Ruimte voor een echte omschrijving van een paar regels, met een schuifbalk als
            // het er meer worden. De lijst toont de eerste regel, de rest zie je in de tooltip.
            Height = 110,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
        };
        var catLabel = new Label { Text = "Categorie", AutoSize = true, Location = new Point(16, 112) };
        var catBox = new ComboBox
        {
            Location = new Point(96, 108),
            Width = 150,
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        var categorieen = MijnTaakStore.Load().Categorieen;
        catBox.Items.AddRange(categorieen.Cast<object>().ToArray());
        catBox.Text = taak.Categorie;
        var prioLabel = new Label { Text = "Prioriteit", AutoSize = true, Location = new Point(266, 112) };
        var prioBox = new ComboBox
        {
            Location = new Point(336, 108),
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        prioBox.Items.AddRange(new object[] { "Hoog", "Normaal", "Laag" });
        prioBox.SelectedIndex = Math.Clamp(taak.Prioriteit, 0, 2);
        // Datums in de volgorde waarin je erover denkt: eerst wanneer je eraan kúnt beginnen
        // (start), dan wanneer het klaar moet zijn (deadline). De deadline kan nooit vóór de
        // start liggen, en met "= start" neem je die datum in één klik over.
        var startLabel = new Label { Text = "Vanaf", AutoSize = true, Location = new Point(16, 156) };
        var startPicker = new DatumKiezer
        {
            Waarde = taak.Startdatum,
            LeegTekst = "meteen zichtbaar",
            Location = new Point(96, 152),
            Width = 190,
        };
        // De uitleg over verbergen zit in een tooltip: op de regel zelf staan nu de
        // uitstelknopjes, net als bij de deadline.
        new ToolTip().SetToolTip(startPicker, "Vóór deze dag blijft de taak verborgen in de lijst");
        var deadlineLabel = new Label { Text = "Uiterlijk", AutoSize = true, Location = new Point(16, 196) };
        var picker = new DatumKiezer
        {
            Waarde = taak.Deadline,
            LeegTekst = "geen deadline",
            MinimumDatum = taak.Startdatum,
            Location = new Point(96, 192),
            Width = 190,
        };
        var zelfdeDagKnop = new ModernButton
        {
            // Vaste breedte in plaats van krimpen naar de tekst: anders schuift deze knop
            // over de uitstelknopjes ernaast zodra het lettertype iets groter is.
            Text = "= start", Height = 28, Width = 64, Location = new Point(296, 193),
        };
        zelfdeDagKnop.Click += (_, _) => picker.Waarde = startPicker.Waarde;
        // Uitstellen met één tik: het gewone geval is "morgen" of "overmorgen". Zonder
        // datum rekenen we vanaf vandaag, anders vanaf de datum die er staat. Dezelfde
        // knopjes staan op de start- én de deadlineregel: een taak verzetten is vaak
        // allebei verschuiven.
        ModernButton DagKnop(string tekst, int x, int y) =>
            new() { Text = tekst, Height = 28, Width = tekst.Length > 6 ? 66 : 58, Location = new Point(x, y) };
        var startPlus1 = DagKnop("+1 dag", 368, 153);
        var startPlus2 = DagKnop("+2 dagen", 430, 153);
        var plus1 = DagKnop("+1 dag", 368, 193);
        var plus2 = DagKnop("+2 dagen", 430, 193);
        foreach (var (knop, kiezer, dagen) in new[]
        {
            (startPlus1, startPicker, 1), (startPlus2, startPicker, 2),
            (plus1, picker, 1), (plus2, picker, 2),
        })
        {
            knop.Click += (_, _) =>
            {
                var basis = kiezer.Waarde ?? DateOnly.FromDateTime(DateTime.Today);
                kiezer.Waarde = basis.AddDays(dagen);
            };
        }
        // De startdatum begrenst de deadline; de knop heeft alleen zin als er een start is.
        void WerkKoppelingBij()
        {
            picker.MinimumDatum = startPicker.Waarde;
            zelfdeDagKnop.Visible = startPicker.Waarde is not null;
            // Schuift de start voorbij de deadline (bv. met de knopjes), dan schuift de
            // deadline mee: die mag nooit vóór de startdatum liggen.
            if (startPicker.Waarde is { } start && picker.Waarde is { } deadline && deadline < start)
            {
                picker.Waarde = start;
            }
        }
        startPicker.WaardeGewijzigd += (_, _) => WerkKoppelingBij();
        WerkKoppelingBij();
        // Vroegste uur: de dagplanning plant de taak niet eerder (de lijst toont hem gewoon).
        var uurLabel = new Label { Text = "Niet vóór", AutoSize = true, Location = new Point(16, 236) };
        var uurPicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            ShowCheckBox = true,
            Checked = taak.StartUur is not null,
            Value = DateTime.Today.Add((taak.StartUur ?? new TimeOnly(9, 0)).ToTimeSpan()),
            Location = new Point(96, 232),
            Width = 100,
        };
        var uurHint = new Label
        {
            Text = "uur; dagplanning plant niet eerder",
            AutoSize = true,
            Location = new Point(206, 237),
        };
        // De bron: waar deze taak vandaan komt. Bij een taak uit een bericht staat hier de
        // link naar die mail of chat; bij een taak die je zelf typte kun je er nu zelf een
        // link, bestand of map in zetten — dat is precies wat "Bron openen in browser" en
        // de webversie gebruiken.
        var huidigeBron = BepaalTaakBron(taak);
        var bronLabel = new Label { Text = "Bron", AutoSize = true, Location = new Point(16, 280) };
        var bronBox = new TextBox
        {
            Text = huidigeBron?.Link ?? "",
            Location = new Point(96, 276),
            Width = 320,
            AllowDrop = true,
        };
        // Slepen vanuit de Verkenner is de snelste manier om een map of bestand te koppelen.
        bronBox.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        bronBox.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paden)
            {
                bronBox.Text = paden[0];
            }
        };
        var bronKnop = new ModernButton
        {
            Text = "Openen", Height = 28, Width = 66, Location = new Point(420, 275),
        };
        bronKnop.Click += (_, _) => TaakBron.Open(bronBox.Text);
        var bronHint = new Label { AutoSize = true, Location = new Point(96, 308) };
        void WerkBronHintBij()
        {
            bronKnop.Enabled = bronBox.Text.Trim().Length > 0;
            // Hangt er een echt bericht aan, dan zeggen we welk: dan weet je dat het
            // antwoordblok van die mail blijft werken als je de link aanpast.
            bronHint.Text = TaakBron.Omschrijving(huidigeBron);
        }
        bronBox.TextChanged += (_, _) => WerkBronHintBij();
        WerkBronHintBij();
        var ok = new ModernButton
        {
            Text = "Opslaan", Width = 115, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(371, 336),
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(261, 336),
        };
        dialog.Controls.AddRange(new Control[]
        {
            tekstLabel, tekstBox, catLabel, catBox, prioLabel, prioBox,
            startLabel, startPicker, startPlus1, startPlus2, deadlineLabel, picker, zelfdeDagKnop,
            plus1, plus2, uurLabel, uurPicker, uurHint,
            bronLabel, bronBox, bronKnop, bronHint, ok, cancel,
        });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Theme.Apply(dialog);
        uurHint.ForeColor = Theme.Muted;
        bronHint.ForeColor = Theme.Muted;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var nieuweTekst = tekstBox.Text.Trim();
        if (nieuweTekst.Length == 0)
        {
            Toast.Toon(this, "Taaktekst mag niet leeg zijn", Fluent.Edit);
            return;
        }

        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } opgeslagen)
        {
            opgeslagen.Tekst = nieuweTekst;
            opgeslagen.Categorie = catBox.Text.Trim();
            opgeslagen.Prioriteit = prioBox.SelectedIndex;
            // Deadline naar later geschoven? Dan telt de uitstel-detector mee.
            if (picker.Waarde is { } nieuweDeadline && opgeslagen.Deadline is { } oudeDeadline &&
                nieuweDeadline > oudeDeadline)
            {
                opgeslagen.UitstelTeller++;
            }
            opgeslagen.Deadline = picker.Waarde;
            opgeslagen.Startdatum = startPicker.Waarde;
            opgeslagen.StartUur = uurPicker.Checked ? TimeOnly.FromDateTime(uurPicker.Value) : null;
            var nieuweBron = bronBox.Text.Trim();
            if (!string.Equals(nieuweBron, huidigeBron?.Link ?? "", StringComparison.Ordinal))
            {
                opgeslagen.Mail = TaakBron.UitLink(nieuweBron, opgeslagen.Mail ?? huidigeBron);
            }
            MijnTaakStore.Save(data);
            Toast.Toon(this, "Taak bijgewerkt", Fluent.Check);
        }
        await VerversTakenAsync();
    }

    /// <summary>
    /// De timesheetklant die bij een taakcategorie hoort. De categorieën van "Mijn taken" en de
    /// klanten in urbanadmin lopen bijna gelijk; alleen "Privé" en "Urban IT" hebben een eigen
    /// bestemming. Null = niet te bepalen (dan is de dialoog de veiligste weg).
    /// </summary>
    private static string? KlantVoorCategorie(string categorie) => categorie.Trim() switch
    {
        "CED" => "CED",
        "Aqurat" => "Aqurat",
        "RadiologyPartners" => "RadiologyPartners",
        "Urban IT" or "UrbanIT" => "UrbanIT",
        "Privé" or "Prive" => "Niet factureerbaar",
        _ => null,
    };

    /// <summary>
    /// De klantcontext van een bericht voor de context-switch-teller; null = niet te bepalen
    /// (gewone Gmail kan iedereen zijn, dus die telt bewust niet mee).
    /// </summary>
    private static string? KlantVoorBericht(MailBericht m) => m switch
    {
        { OutlookMail.Length: > 0 } or { TeamsChat.Length: > 0 } => "CED",
        { WhatsAppChat.Length: > 0 } => "Privé",
        { ChatSpace.Length: > 0 } => "Urban IT",
        _ => null,
    };

    /// <summary>
    /// Boekt tijd op de geselecteerde taak. Standaard rechtstreeks: 20 minuten op de klant van
    /// de categorie, met de taaktekst als omschrijving, eindigend op dit moment. Met
    /// <paramref name="vraag"/> (of bij een onbekende klant) komt eerst de timesheetdialoog.
    /// </summary>
    private async Task BoekTaakTimesheetAsync(int minuten, bool vraag)
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        var categorie = rij.Lokaal?.Categorie ?? (rij.Bron == "Asana" ? "Aqurat" : "");
        var klant = KlantVoorCategorie(categorie);
        var omschrijving = Kort(rij.Tekst, 120);
        var datum = DateOnly.FromDateTime(DateTime.Now);
        // Je boekt achteraf: het blok eindigt nu, afgerond op vijf minuten.
        var nu = DateTime.Now;
        var eind = new TimeOnly(nu.Hour, nu.Minute / 5 * 5);
        var van = eind.AddMinutes(-minuten);

        if (vraag || klant is null)
        {
            using var dialog = new TimesheetForm(klant, datum, minuten, omschrijving);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            klant = dialog.Klant;
            datum = dialog.Datum;
            minuten = dialog.Minuten;
            omschrijving = dialog.Omschrijving;
            van = eind.AddMinutes(-minuten);
            if (klant.Length == 0)
            {
                Toast.Toon(this, "Geen klant gekozen — niets geboekt", Fluent.Klok);
                return;
            }
        }

        TimesheetStore.Voeg(new TimesheetRegel
        {
            Datum = datum,
            Van = van,
            Klant = klant,
            Minuten = minuten,
            Omschrijving = omschrijving,
            Bron = "taak",
        });
        Toast.Toon(this, $"{minuten} min geboekt op {klant} ({van:HH:mm}–{eind:HH:mm})", Fluent.Klok);
        try
        {
            // Meteen doorboeken; lukt het niet, dan pikt de retry bij de volgende ververs het op.
            await TimesheetStore.BoekDoorAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Staat in de wachtrij — urbanadmin gaf: {ex.Message}", Fluent.Klok);
        }
    }

    /// <summary>Start de timer op de geselecteerde taak, of stopt en boekt de lopende.</summary>
    private async Task ToggleTimerAsync()
    {
        if (TaakTimer.Huidig() is not null)
        {
            await StopTimerEnBoekAsync();
            VulTakenLijst();
            return;
        }
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        var categorie = rij.Lokaal?.Categorie ?? (rij.Bron == "Asana" ? "Aqurat" : "");
        TaakTimer.Start(rij.Lokaal?.Id, rij.AsanaGid, rij.Tekst,
            KlantVoorCategorie(categorie) ?? "");
        Toast.Toon(this, $"⏱ Timer gestart: {Kort(rij.Tekst, 40)}", Fluent.Klok);
        VulTakenLijst(); // de titel toont de lopende timer
    }

    /// <summary>
    /// Stopt de lopende timer en boekt de verstreken tijd (afgerond op 5 min) als timesheet.
    /// Zonder bekende klant komt eerst de dialoog; annuleren = tijd niet geboekt.
    /// </summary>
    private async Task StopTimerEnBoekAsync()
    {
        if (TaakTimer.Stop() is not { } timer)
        {
            return;
        }
        var klant = timer.Klant;
        var minuten = timer.Minuten;
        var omschrijving = Kort(timer.Tekst, 120);
        var van = TimeOnly.FromDateTime(timer.Start.LocalDateTime);
        if (klant.Length == 0)
        {
            using var dialog = new TimesheetForm(null,
                DateOnly.FromDateTime(timer.Start.LocalDateTime), minuten, omschrijving);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Klant.Length == 0)
            {
                Toast.Toon(this, $"Timer gestopt ({timer.Ruw} min) — niets geboekt", Fluent.Klok);
                return;
            }
            klant = dialog.Klant;
            minuten = dialog.Minuten;
            omschrijving = dialog.Omschrijving;
        }
        TimesheetStore.Voeg(new TimesheetRegel
        {
            Datum = DateOnly.FromDateTime(timer.Start.LocalDateTime),
            Van = van,
            Klant = klant,
            Minuten = minuten,
            Omschrijving = omschrijving,
            Bron = "timer",
        });
        Toast.Toon(this, $"⏱ {minuten} min geboekt op {klant}", Fluent.Klok);
        try
        {
            await TimesheetStore.BoekDoorAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Staat in de wachtrij — urbanadmin gaf: {ex.Message}", Fluent.Klok);
        }
    }

    /// <summary>Snoozet de geselecteerde (lokale) taak: tijdelijk verbergen tot het gekozen moment.</summary>
    private void SnoozeTaak(DateTimeOffset moment)
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij { Lokaal: { } taak })
        {
            return;
        }
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } opgeslagen)
        {
            opgeslagen.SnoozeTot = moment;
            opgeslagen.UitstelTeller++; // de uitstel-detector telt mee
            MijnTaakStore.Save(data);
            Prestaties.Gebeurtenis(this, "snooze");
            Toast.Toon(this, $"Taak gesnoozed tot {moment:ddd d MMM HH:mm}", Fluent.Klok);
        }
        _ = VerversTakenAsync();
    }

    /// <summary>Verzet de deadline van de geselecteerde (lokale) taak via een datumkiezer.</summary>
    /// <summary>
    /// Bewerkt een Asana-taak zonder de cockpit te verlaten: deadline verzetten en de
    /// omschrijving (notes) aanpassen. De titel staat er alleen ter herkenning bij — die
    /// verandert wie de taak ook opvolgt, dus die blijft in Asana zelf.
    /// </summary>
    private async Task BewerkAsanaTaakAsync(TaakRij rij)
    {
        var asana = AsanaSettings.Load();
        if (!asana.Compleet || rij.AsanaGid.Length == 0)
        {
            Toast.Toon(this, "Geen Asana-koppeling ingesteld", Fluent.Edit);
            return;
        }

        using var dialog = new Form
        {
            Text = "Asana-taak bewerken",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(540, 420),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var titel = new Label
        {
            Text = Kort(rij.Tekst, 70), AutoSize = false, Location = new Point(16, 16),
            Size = new Size(490, 22), Font = Theme.SemiBold,
        };
        var deadlineLabel = new Label { Text = "Uiterlijk", AutoSize = true, Location = new Point(16, 56) };
        var picker = new DatumKiezer
        {
            Waarde = rij.Deadline,
            LeegTekst = "geen deadline",
            Location = new Point(96, 52),
            Width = 190,
        };
        var omschrijvingLabel = new Label
        {
            Text = "Omschrijving", AutoSize = true, Location = new Point(16, 96),
        };
        var omschrijvingBox = new TextBox
        {
            Text = rij.AsanaOmschrijving.ReplaceLineEndings("\r\n"),
            Location = new Point(16, 120),
            Size = new Size(490, 200),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        var ok = new ModernButton
        {
            Text = "Opslaan in Asana", Width = 160, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(346, 336),
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(236, 336),
        };
        dialog.Controls.AddRange(new Control[]
        {
            titel, deadlineLabel, picker, omschrijvingLabel, omschrijvingBox, ok, cancel,
        });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var nieuweDeadline = picker.Waarde;
        var nieuweOmschrijving = omschrijvingBox.Text.ReplaceLineEndings("\n");
        // Alleen echt gewijzigde velden meesturen (scheelt ruis in de Asana-activiteit).
        var omschrijvingGewijzigd = nieuweOmschrijving != rij.AsanaOmschrijving.ReplaceLineEndings("\n");
        if (nieuweDeadline == rij.Deadline && !omschrijvingGewijzigd)
        {
            return;
        }
        try
        {
            await AsanaClient.WijzigAsync(
                asana, rij.AsanaGid,
                nieuweDeadline, deadlineWissen: nieuweDeadline is null && rij.Deadline is not null,
                omschrijvingGewijzigd ? nieuweOmschrijving : null, _cts.Token);
            Toast.Toon(this, "Asana-taak bijgewerkt", Fluent.Check);
            await VerversTakenAsync();
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Asana bijwerken mislukt: {ex.Message}", Fluent.Edit);
        }
    }

    /// <summary>Alleen de deadline van een Asana-taak verzetten (zonder de rest te openen).</summary>
    private async Task VerzetAsanaDeadlineAsync(TaakRij rij)
    {
        var asana = AsanaSettings.Load();
        if (!asana.Compleet || rij.AsanaGid.Length == 0)
        {
            Toast.Toon(this, "Geen Asana-koppeling ingesteld", Fluent.Kalender);
            return;
        }
        if (!VraagAsanaDatum(rij, out var gekozen) || gekozen == rij.Deadline)
        {
            return;
        }
        try
        {
            await AsanaClient.WijzigAsync(
                asana, rij.AsanaGid, gekozen,
                deadlineWissen: gekozen is null, omschrijving: null, _cts.Token);
            Toast.Toon(this, gekozen is { } d
                ? $"Asana-deadline verzet naar {d:ddd d MMM}"
                : "Asana-deadline gewist", Fluent.Kalender);
            await VerversTakenAsync();
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Asana bijwerken mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>Datumkiezer voor een Asana-taak; false = geannuleerd.</summary>
    private bool VraagAsanaDatum(TaakRij rij, out DateOnly? datum)
    {
        datum = null;
        using var dialog = new Form
        {
            Text = "Asana-deadline verzetten",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(340, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var picker = new DatumKiezer
        {
            Waarde = rij.Deadline,
            LeegTekst = "geen deadline",
            Location = new Point(16, 18),
            Width = 200,
        };
        var ok = new ModernButton
        {
            Text = "Verzetten", Width = 115, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(196, 70),
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(86, 70),
        };
        dialog.Controls.AddRange(new Control[] { picker, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }
        datum = picker.Waarde;
        return true;
    }

    private async Task VerzetTaakDeadlineAsync()
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        if (rij.Lokaal is not { } taak)
        {
            await VerzetAsanaDeadlineAsync(rij);
            return;
        }

        using var dialog = new Form
        {
            Text = "Deadline verzetten",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(340, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        // De deadline kan niet vóór de startdatum liggen — de kiezer sluit die dagen uit.
        var picker = new DatumKiezer
        {
            Waarde = taak.Deadline,
            LeegTekst = "geen deadline",
            MinimumDatum = taak.Startdatum,
            Location = new Point(16, 18),
            Width = 200,
        };
        var ok = new ModernButton
        {
            Text = "Verzetten", Width = 115, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(196, 70),
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(86, 70),
        };
        dialog.Controls.Add(picker);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } opgeslagen)
        {
            opgeslagen.Deadline = picker.Waarde;
            MijnTaakStore.Save(data);
            Toast.Toon(this, picker.Waarde is { } nieuw
                ? $"Deadline verzet naar {nieuw:ddd d MMM}"
                : "Deadline verwijderd", Fluent.Kalender);
        }
        await VerversTakenAsync();
    }

    /// <summary>Zet de deadline van de geselecteerde taak in één klik op morgen of overmorgen.</summary>
    private async Task VerzetTaakSnelAsync(int dagen)
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        if (rij.Lokaal is not { } taak)
        {
            await VerzetAsanaDeadlineAsync(rij);
            return;
        }
        var doel = DateOnly.FromDateTime(DateTime.Today).AddDays(dagen);
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } opgeslagen)
        {
            // Naar later geschoven? Dan telt de uitstel-detector mee, net als in de dialoog.
            if (opgeslagen.Deadline is { } oud && doel > oud)
            {
                opgeslagen.UitstelTeller++;
            }
            // De hele taak schuift op: ook de startdatum gaat naar die dag, zodat de taak
            // tot dan uit de lijst verdwijnt — dat is precies wat "verzetten" bedoelt.
            opgeslagen.Deadline = doel;
            opgeslagen.Startdatum = doel;
            MijnTaakStore.Save(data);
            Toast.Toon(this, $"Taak verzet naar {DatumKiezer.Toon(doel)}", Fluent.Kalender);
        }
        await VerversTakenAsync();
    }

    /// <summary>
    /// Toont de mail die aan de geselecteerde taak hangt in het detailpaneel en maakt hem
    /// beantwoordbaar: het antwoordvak (en de Claude-knop) werken dan op déze mail.
    /// </summary>
    private void ToonTaakMail()
    {
        if (_taken.SelectedItems.Count == 0 ||
            _taken.SelectedItems[0].Tag is not TaakRij { Lokaal: { } lokaal })
        {
            return;
        }
        // Ook taken die (nog) geen bronbericht meekregen — bv. oudere @mention-taken —
        // krijgen er hier alsnog één toegewezen door de afzender op te zoeken in de laatst
        // bekende cockpit-berichten, zodat je vanuit de taak kunt doorklikken naar de mail.
        if (BepaalTaakBron(lokaal) is not { } mail)
        {
            // Taak zonder bronbericht: niets om te beantwoorden, dus ook geen antwoordblok.
            BewaarDetailConcept(); // getypte tekst niet verliezen
            _getoond = null;
            _detailLosVanLijst = true;
            ToonLegeWeergave();
            WerkAntwoordblokBij();
            return;
        }
        var beantwoordbaar = mail.AntwoordAan.Length > 0 ||
            mail.ChatSpace.Length > 0 || mail.WhatsAppChat.Length > 0;
        var bericht = new MailBericht
        {
            Van = mail.Van,
            VanAdres = mail.VanAdres,
            AntwoordAan = mail.AntwoordAan,
            Onderwerp = mail.Onderwerp, // bewust zonder 📌: het antwoord wordt "Re: <onderwerp>"
            Tekst = mail.Tekst +
                (mail.Link.Length > 0 ? $"\n\nBron: {mail.Link}" : "") +
                // De "oudere versie"-uitleg slaat alleen op taken die echt uit een mail
                // kwamen; bij radartaken (verjaardagen, vaste taken) zonder afzender is
                // hij pure ruis.
                (beantwoordbaar || mail.Van.Length == 0 ? "" :
                    "\n\n(Beantwoorden kan niet: deze taak is met een " +
                    "oudere versie gemaakt — maak hem opnieuw vanuit de mail.)"),
            Datum = mail.Datum,
            MessageId = mail.MessageId,
            Referenties = mail.Referenties.ToList(),
            ChatSpace = mail.ChatSpace,
            WhatsAppChat = mail.WhatsAppChat,
        };
        // Een taak van de cadeauradar krijgt een échte knop in het detailpaneel: de hint
        // "dubbelklik op deze taak" leidde tot dubbelklikken in dit paneel, en daar
        // selecteert een dubbelklik alleen maar tekst (de lijstrij ernaast werkt wél).
        if (Verjaardagen.IsRadarTaak(lokaal.Tekst))
        {
            bericht.Html =
                "<pre style=\"white-space:pre-wrap;font-family:inherit;font-size:13px;margin:0\">" +
                System.Net.WebUtility.HtmlEncode(bericht.Tekst) + "</pre>" +
                "<div style=\"margin-top:16px\"><a href=\"wm-verjaardag:\" " +
                "style=\"display:inline-block;padding:8px 16px;background:#1a56c4;color:#ffffff;" +
                "border-radius:8px;text-decoration:none;font-size:13px;font-weight:600\">" +
                "🎁 Cadeau-ideeën openen</a></div>";
        }
        _berichten.SelectedItems.Clear();
        _getoond = bericht;
        _detailLosVanLijst = true;
        _detailConcept.Clear();
        // Ligt er een concept klaar voor deze mail (bv. de taak-bevestiging "ik pak dit
        // dan op"), dan meteen in het antwoordvak zetten.
        if (mail.MessageId.Length > 0 &&
            ConceptCache.Load().TryGetValue(mail.MessageId, out var taakConcept) &&
            taakConcept.Concept.Length > 0)
        {
            _detailConcept.Text = taakConcept.Concept.ReplaceLineEndings("\r\n");
        }
        _detailFeedback.Clear();
        _verstuurButton.Enabled = beantwoordbaar;
        _openButton.Visible = mail.Link.Length > 0;
        _uitschrijfButton.Visible = false;
        _outlookLeesButton.Visible = false;
        WerkAntwoordblokBij();
        var html = MailReplyForm.BouwWeergave(bericht);
        if (_detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(html);
        }
        else
        {
            _wachtendeWeergave = html;
        }
    }

    /// <summary>
    /// Opent de directe chat met Jan Van Dyck ín de cockpit: transcript van de laatste dagen
    /// in het detailpaneel, antwoorden via het gewone antwoordvak (Versturen → Google Chat).
    /// De DM-space wordt één keer opgezocht via de Chat API en daarna gecachet.
    /// </summary>
    private async Task OpenChatJanAsync()
    {
        const string naam = "Jan Van Dyck";
        var s = GoogleChatSettings.Load();
        if (!s.Gekoppeld)
        {
            Toast.Toon(this, "Google Chat is niet gekoppeld (zie mailvenster → Instellingen)", Fluent.Ster);
            return;
        }
        try
        {
            if (!s.DmCache.TryGetValue(naam.ToLowerInvariant(), out var space) || space.Length == 0)
            {
                space = await GoogleChatClient.ZoekDmAsync(s, naam, _cts.Token);
                if (space.Length > 0)
                {
                    s.DmCache[naam.ToLowerInvariant()] = space;
                    s.Save();
                }
            }
            if (space.Length == 0)
            {
                Toast.Toon(this, "DM met Jan niet gevonden — chat.google.com geopend", Fluent.Globe);
                OpenExtern("https://chat.google.com/");
                return;
            }

            var regels = await GoogleChatClient.TranscriptRegelsAsync(s, space, 7, _cts.Token);
            var bericht = new MailBericht
            {
                MessageId = "chatjan:" + space,
                ChatSpace = space,
                Van = naam,
                Onderwerp = "Directe chat",
                Tekst = regels.Count > 0
                    ? string.Join("\n", regels.Select(r =>
                        $"[{r.Tijd.ToLocalTime():d MMM HH:mm}] {r.Naam}: " +
                        GoogleChatClient.TekstMetBijlagen(r.Tekst, r.Afbeeldingen, r.Bestanden)))
                    : "(Geen berichten in de afgelopen 7 dagen — typ hieronder om het gesprek te starten.)",
                Html = regels.Count > 0 ? GoogleChatClient.BouwChatHtml(regels) : "",
                Datum = DateTimeOffset.Now,
            };
            // In het detailpaneel tonen alsof hij geselecteerd is: het antwoordvak verstuurt
            // dan rechtstreeks naar deze DM.
            _berichten.SelectedItems.Clear();
            _getoond = bericht;
            _detailLosVanLijst = true;
            var html = MailReplyForm.BouwWeergave(bericht);
            if (_detail.CoreWebView2 is { } core)
            {
                core.NavigateToString(html);
            }
            else
            {
                _wachtendeWeergave = html;
            }
            _detailConcept.Clear();
            _detailFeedback.Clear();
            _verstuurButton.Enabled = true;
            _openButton.Visible = true;
            _uitschrijfButton.Visible = false;
            _outlookLeesButton.Visible = false;
            WerkAntwoordblokBij();
            Toast.Toon(this, "Chat met Jan geladen — typ je bericht en klik Versturen", Fluent.Send);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Chat met Jan laden mislukt: {ex.Message}", Fluent.Ster);
        }
    }

    /// <summary>Domeinen van supportklanten: mail van hen krijgt de AVG-remotesessie-actie.</summary>
    private static readonly string[] SupportDomeinen =
        { "vriesveem.nl", "vriesveemlogistics.nl", "nemijtek.nl" };

    /// <summary>Lokale delen die op een algemeen postbusadres wijzen (dan niet op voornaam zoeken).</summary>
    private static readonly string[] AlgemeneAdressen =
        { "info", "support", "sales", "admin", "contact", "noreply", "no-reply", "mail",
          "office", "hello", "team", "it", "helpdesk", "boekhouding", "facturen", "order" };

    /// <summary>Komt dit bericht van een supportklant (vriesveem/nemijtek)?</summary>
    private static bool IsSupportBericht(MailBericht m)
    {
        var adres = (m.VanAdres.Length > 0 ? m.VanAdres : m.AntwoordAan).ToLowerInvariant();
        return !m.IsChat && SupportDomeinen.Any(d =>
            adres.EndsWith("@" + d) || adres.EndsWith("." + d) || adres == d ||
            adres.Contains("@" + d));
    }

    /// <summary>
    /// De voornaam om in de AVG-console op te zoeken. Bij een algemeen adres (info@, support@…)
    /// of als er geen persoonsnaam is, leeg — dan wordt alleen de apparatenlijst geopend.
    /// </summary>
    private static string SupportVoornaam(MailBericht m)
    {
        var adres = (m.VanAdres.Length > 0 ? m.VanAdres : m.AntwoordAan).ToLowerInvariant();
        var lokaal = adres.Split('@')[0];
        if (AlgemeneAdressen.Any(a => lokaal == a || lokaal.StartsWith(a + ".") || lokaal.StartsWith(a + "-")))
        {
            return "";
        }
        // Voornaam uit de weergavenaam ("Voornaam Achternaam"); die is betrouwbaarder dan het
        // lokale deel van het adres. Geen spatie/persoonsnaam → leeg (algemeen behandelen).
        var naam = m.Van.Trim();
        if (naam.Contains('<'))
        {
            naam = naam[..naam.IndexOf('<')].Trim();
        }
        var eerste = naam.Split(new[] { ' ', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        // Als de weergavenaam eigenlijk het e-mailadres is, valt er geen voornaam uit te halen.
        return eerste.Contains('@') ? "" : eerste;
    }

    /// <summary>Start een AVG-remotesessie voor het geselecteerde supportbericht.</summary>
    private void StartSupportSessie()
    {
        if (GeselecteerdBericht() is not { } m || !IsSupportBericht(m))
        {
            return;
        }
        var voornaam = SupportVoornaam(m);
        var form = new AvgCloudCareForm(voornaam);
        form.Show(); // niet-modaal: cockpit blijft bruikbaar tijdens de sessie
        Toast.Toon(this, voornaam.Length > 0
            ? $"AVG-console openen en zoeken op \"{voornaam}\"…"
            : "AVG-apparatenlijst openen…", Fluent.Globe);
    }

    /// <summary>
    /// Werkt de Claude Code CLI echt bij. De CLI wordt op deze machine door <c>winget</c> beheerd;
    /// <c>claude update</c> print dan alleen een instructie en werkt niks bij. Daarom draaien we
    /// zelf <c>winget upgrade Anthropic.ClaudeCode</c>. Belangrijk: winget kan de exe niet vervangen
    /// zolang er nog een Claude-sessie draait (bestand vergrendeld → "Access is denied"); in dat
    /// geval melden we dat en blijft de taak bewust openstaan. Geeft de versie vóór/na en een
    /// leesbare melding terug.
    /// </summary>
    private static async Task<(string Huidig, string Nieuw, string Melding)> UpgradeClaudeAsync()
    {
        static async Task<string> Draai(string exe, string args, int timeoutMs)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {exe} {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return "";
            }
            var uit = await proc.StandardOutput.ReadToEndAsync();
            var fout = await proc.StandardError.ReadToEndAsync();
            using var ct = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(ct.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            return (uit + "\n" + fout).Trim();
        }

        try
        {
            var huidig = await Draai("claude", "--version", 15000);
            if (huidig.Length == 0)
            {
                return ("", "", "Claude CLI niet gevonden op het PATH.");
            }

            // winget vervangt claude.exe; dat lukt niet zolang de CLI-exe zelf nog vergrendeld is
            // door een draaiende sessie. Let op: de Claude Desktop-app heet óók "claude.exe" maar
            // draait onder ...\WindowsApps\Claude_... en heeft niets met de winget-CLI te maken; die
            // mag de update niet blokkeren. Daarom tellen we alleen processen die vanuit het
            // winget-package-pad (...\WinGet\Packages\Anthropic.ClaudeCode...) draaien.
            if (LopendeClaudeCliSessies() > 0)
            {
                return (huidig, huidig,
                    "Sluit eerst alle Claude Code-terminalsessies — daarna nogmaals klikken (winget " +
                    "kan de CLI anders niet vervangen). De Claude Desktop-app mag gewoon openblijven.");
            }

            var updateUit = await Draai("winget",
                "upgrade --id Anthropic.ClaudeCode --silent --accept-source-agreements " +
                "--accept-package-agreements --disable-interactivity", 300000);
            var nieuw = await Draai("claude", "--version", 15000);

            if (!string.Equals(huidig, nieuw, StringComparison.OrdinalIgnoreCase))
            {
                return (huidig, nieuw, $"Claude bijgewerkt: {huidig} → {nieuw}".Replace("\n", " "));
            }
            if (updateUit.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
                updateUit.Contains("No newer", StringComparison.OrdinalIgnoreCase) ||
                updateUit.Contains("geen", StringComparison.OrdinalIgnoreCase))
            {
                return (huidig, nieuw, $"Claude is al up-to-date ({huidig}).");
            }
            if (updateUit.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                return (huidig, nieuw,
                    "Bijwerken geblokkeerd: sluit alle Claude-sessies en probeer opnieuw.");
            }
            return (huidig, nieuw, "Bijwerken lukte niet — versie ongewijzigd.");
        }
        catch (Exception ex)
        {
            return ("", "", $"Bijwerken mislukt: {ex.Message}");
        }
    }

    /// <summary>
    /// De draaiende Claude Code CLI-processen (winget-package) die de exe vergrendelen. De Claude
    /// Desktop-app heet óók claude.exe, maar draait onder ...\WindowsApps\Claude_... en houdt de
    /// winget-CLI niet vergrendeld; die filteren we weg op basis van het exe-pad. Headless sessies
    /// (--print/stream-json, o.a. WorkManagers eigen achtergrond-drafts) tellen niet mee.
    /// </summary>
    private static List<System.Diagnostics.Process> ClaudeCliProcessen()
    {
        var result = new List<System.Diagnostics.Process>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude"))
        {
            string? pad = null;
            try { pad = p.MainModule?.FileName; } catch { /* Desktop-app/ander proces: niet leesbaar */ }
            var isWingetCli = pad is not null
                && pad.Contains(@"\WinGet\", StringComparison.OrdinalIgnoreCase)
                && pad.Contains("ClaudeCode", StringComparison.OrdinalIgnoreCase);

            var cmd = ProcessInspector.GetCommandLine(p.Id) ?? "";
            var isHeadless = cmd.Contains("stream-json", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("--print", StringComparison.OrdinalIgnoreCase);

            if (isWingetCli && !isHeadless)
            {
                result.Add(p);
            }
            else
            {
                p.Dispose();
            }
        }
        return result;
    }

    /// <summary>Aantal interactieve Claude Code-terminalsessies dat de winget-update zou blokkeren.</summary>
    private static int LopendeClaudeCliSessies()
    {
        var procs = ClaudeCliProcessen();
        foreach (var p in procs)
        {
            p.Dispose();
        }
        return procs.Count;
    }

    /// <summary>
    /// Beëindigt de interactieve Claude Code-sessies. De sessie draait in een shell (powershell/
    /// pwsh/cmd) binnen een terminal; de shell-ouder beëindigen sluit ook die tab netjes mee.
    /// </summary>
    private static void SluitClaudeCliSessies()
    {
        foreach (var claude in ClaudeCliProcessen())
        {
            using (claude)
            {
                var target = claude;
                if (ProcessInspector.GetParentProcessId(claude.Id) is { } parentPid)
                {
                    try
                    {
                        var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                        if (parent.ProcessName is "powershell" or "pwsh" or "cmd")
                        {
                            target = parent;
                        }
                        else
                        {
                            parent.Dispose();
                        }
                    }
                    catch { /* ouder bestaat niet meer; sluit claude zelf */ }
                }
                try { target.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Sluit PhpStorm netjes af (CloseMainWindow: de IDE kan nog om opslaan vragen).</summary>
    private static void SluitPhpStorm()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("phpstorm64"))
        {
            using (p)
            {
                try { p.CloseMainWindow(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Vertaalt een Frans/Engelse mail op de achtergrond naar het Nederlands en herrendert het
    /// detailpaneel met de vertaling eronder, mits de mail nog steeds getoond wordt.
    /// </summary>
    private async Task VertaalEnHertoonAsync(MailBericht mail)
    {
        try
        {
            if (await Vertaler.VertaalAlsNodigAsync(mail, _cts.Token) &&
                ReferenceEquals(_getoond, mail) && !IsDisposed &&
                _detail.CoreWebView2 is { } core)
            {
                core.NavigateToString(MailReplyForm.BouwWeergave(mail));
            }
        }
        catch
        {
            // Vertalen is optioneel; de mail blijft gewoon zonder vertaling staan.
        }
    }

    /// <summary>De online-vergaderlink (Teams/Meet/Zoom/Webex) uit een afspraak, of null.</summary>
    private static string? MeetingLink(AgendaClient.AgendaItem m)
    {
        if (m.MeetLink.Length > 0)
        {
            return m.MeetLink;
        }
        var bron = m.Locatie + " " + m.Omschrijving;
        var match = System.Text.RegularExpressions.Regex.Match(bron,
            @"https?://[^\s""'<>]*(teams\.microsoft\.com|teams\.live\.com|meet\.google\.com|" +
            @"zoom\.us|webex\.com|whereby\.com)[^\s""'<>]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    /// <summary>Opent de online-vergaderlink van de geselecteerde meeting in de browser.</summary>
    private void DeelnemenAanMeeting()
    {
        if (_meetings.SelectedItems.Count > 0 &&
            _meetings.SelectedItems[0].Tag is AgendaClient.AgendaItem m && MeetingLink(m) is { } url)
        {
            OpenExtern(url);
            Toast.Toon(this, "Meeting geopend in je browser", Fluent.Globe);
        }
    }

    /// <summary>
    /// Stelt vanuit het geselecteerde bericht een agenda-afspraak voor: de dialoog opent
    /// voorgevuld met het onderwerp als titel en de afzender (+ link) in de notitie.
    /// </summary>
    private async Task AfspraakVanBerichtAsync()
    {
        if (GeselecteerdBericht() is not { } bericht)
        {
            return;
        }
        if (!CalendarClient.Beschikbaar)
        {
            Toast.Toon(this, "Geen Gmail-koppeling om in de agenda te schrijven", Fluent.Kalender);
            return;
        }
        var url = BerichtUrl(bericht);
        var straks = DateTime.Now.AddHours(1);
        var voorstel = new AgendaClient.AgendaItem(
            straks, straks.AddHours(1), false,
            bericht.Onderwerp.Length > 0 ? bericht.Onderwerp : $"Afspraak met {bericht.Van}",
            Omschrijving: $"Naar aanleiding van bericht van {bericht.Van}." +
                (url.Length > 0 ? $"\n{url}" : ""));
        using var dialog = new AgendaAfspraakForm(voorstel, alsNieuw: true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var ok = await CalendarClient.MaakAfspraakAsync(
                dialog.Titel, dialog.Start, dialog.Duur, dialog.Omschrijving, _cts.Token, dialog.Locatie);
            // "Blokkeert mijn agenda niet" ook lokaal vastleggen: Gmail kent die vlag niet,
            // dus WorkManager onthoudt hem zelf (naast de [werkbaar]-marker in de omschrijving).
            if (ok && dialog.Omschrijving.Contains(
                    AgendaAfspraakForm.WerkbaarMarker, StringComparison.OrdinalIgnoreCase))
            {
                WerkbaarStore.Zet(dialog.Titel, new DateTimeOffset(dialog.Start), aan: true);
            }
            Toast.Toon(this, ok ? "Afspraak toegevoegd aan Google Agenda" : "Aanmaken mislukt",
                Fluent.Kalender);
            if (ok)
            {
                await VerversMeetingsAsync(forceer: true);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Aanmaken mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>Maakt een nieuwe afspraak aan in de Google-agenda via een dialoog.</summary>
    private async Task NieuweAfspraakAsync()
    {
        if (!CalendarClient.Beschikbaar)
        {
            Toast.Toon(this, "Geen Gmail-koppeling om in de agenda te schrijven", Fluent.Kalender);
            return;
        }
        using var dialog = new AgendaAfspraakForm();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var ok = await CalendarClient.MaakAfspraakAsync(
                dialog.Titel, dialog.Start, dialog.Duur, dialog.Omschrijving, _cts.Token, dialog.Locatie);
            // "Blokkeert mijn agenda niet" ook lokaal vastleggen: Gmail kent die vlag niet,
            // dus WorkManager onthoudt hem zelf (naast de [werkbaar]-marker in de omschrijving).
            if (ok && dialog.Omschrijving.Contains(
                    AgendaAfspraakForm.WerkbaarMarker, StringComparison.OrdinalIgnoreCase))
            {
                WerkbaarStore.Zet(dialog.Titel, new DateTimeOffset(dialog.Start), aan: true);
            }
            Toast.Toon(this, ok ? "Afspraak toegevoegd aan Google Agenda" : "Aanmaken mislukt",
                Fluent.Kalender);
            if (ok)
            {
                await VerversMeetingsAsync(forceer: true);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Aanmaken mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>
    /// Bewerkt de geselecteerde Google-afspraak via een dialoog. Herhalende afspraken of events
    /// zonder UID zijn hier niet bewerkbaar: die opent hij ter bewerking in Google Agenda.
    /// </summary>
    private async Task BewerkAfspraakAsync()
    {
        if (_meetings.SelectedItems.Count == 0 ||
            _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem m)
        {
            return;
        }
        if (!m.Bewerkbaar)
        {
            OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
            Toast.Toon(this, "Herhalend/alleen-lezen — geopend in Google Agenda", Fluent.Kalender);
            return;
        }
        using var dialog = new AgendaAfspraakForm(m);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var werkbaar = dialog.Omschrijving.Contains(
                AgendaAfspraakForm.WerkbaarMarker, StringComparison.OrdinalIgnoreCase);
            var ok = await CalendarClient.WijzigViaUidAsync(
                m.Uid, dialog.Titel, dialog.Start, dialog.Duur, dialog.Omschrijving, _cts.Token, dialog.Locatie);
            if (ok)
            {
                // Vlag lokaal meeverhuizen naar de (mogelijk gewijzigde) titel/tijd; de oude
                // sleutel opruimen zodat er geen spookmarkering achterblijft.
                WerkbaarStore.Zet(m.Titel, m.Start, aan: false);
                WerkbaarStore.Zet(dialog.Titel, new DateTimeOffset(dialog.Start), werkbaar);
                Toast.Toon(this, "Afspraak bijgewerkt", Fluent.Kalender);
                await VerversMeetingsAsync(forceer: true);
            }
            else
            {
                // Google weigerde (uitnodiging, herhalend, CalDAV-kuren): de vlag dan tóch
                // lokaal onthouden — precies waarvoor de werkbaar-lijst bestaat. De rest van
                // de wijzigingen moet wel via Google Agenda zelf.
                WerkbaarStore.Zet(m.Titel, m.Start, werkbaar);
                if (werkbaar)
                {
                    Toast.Toon(this,
                        "Google weigerde de wijziging, maar \"blokkeert mijn agenda niet\" is " +
                        "lokaal onthouden", Fluent.Kalender);
                }
                else
                {
                    OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
                    Toast.Toon(this, "Kon niet in de app wijzigen — geopend in Google Agenda", Fluent.Kalender);
                }
                WerkDagPlanBij(_laatsteBerichten); // dagplan-ankers meteen mee
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Wijzigen mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>
    /// Verwijdert de geselecteerde Google-agenda-afspraak (na bevestiging). Een herhalende
    /// eigen afspraak kan als hele reeks weg (CalDAV verwijdert het event, dus de reeks);
    /// alleen zonder UID (CED/Hilke) blijft Google Agenda zelf de weg.
    /// </summary>
    private async Task VerwijderAfspraakAsync()
    {
        if (_meetings.SelectedItems.Count == 0 ||
            _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem m ||
            _meetings.SelectedItems[0].Name is not ("gagenda" or "recept"))
        {
            return;
        }
        if (m.Uid.Length == 0)
        {
            OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
            Toast.Toon(this, "Alleen-lezen afspraak — geopend in Google Agenda", Fluent.Kalender);
            return;
        }
        // CalDAV kent geen losse instanties: een herhalende afspraak verwijderen = de hele
        // reeks. Dat expliciet vragen; wil je maar één keer schrappen, dan is Google Agenda
        // zelf de plek.
        var vraag = m.Herhalend
            ? $"\"{m.Titel}\" is een herhalende afspraak.\n\nDe héle reeks verwijderen uit je " +
              "agenda? (Eén enkele keer overslaan kan alleen in Google Agenda zelf.)"
            : $"\"{m.Titel}\" ({m.Start.LocalDateTime:ddd d MMM HH:mm}) verwijderen uit je agenda?";
        if (MessageBox.Show(this, vraag,
                "Afspraak verwijderen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return;
        }
        try
        {
            if (await CalendarClient.VerwijderViaUidAsync(m.Uid, _cts.Token))
            {
                WerkbaarStore.Zet(m.Titel, m.Start, aan: false); // geen spookmarkering achterlaten
                Toast.Toon(this, "Afspraak verwijderd", Fluent.Kalender);
                await VerversMeetingsAsync(forceer: true);
            }
            else
            {
                OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
                Toast.Toon(this, "Kon niet in de app verwijderen — geopend in Google Agenda",
                    Fluent.Kalender);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Verwijderen mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>Verzet de geselecteerde Google-afspraak in één klik (zelfde uur, andere dag).</summary>
    private async Task VerzetAfspraakSnelAsync(int dagen)
    {
        if (_meetings.SelectedItems.Count == 0 ||
            _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem m)
        {
            return;
        }
        if (!m.Bewerkbaar || m.HeleDag)
        {
            OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
            Toast.Toon(this, "Deze afspraak kan alleen in Google Agenda zelf verzet worden",
                Fluent.Kalender);
            return;
        }
        var doelDag = DateOnly.FromDateTime(DateTime.Today).AddDays(dagen);
        var nieuwStart = doelDag.ToDateTime(TimeOnly.FromDateTime(m.Start.LocalDateTime));
        try
        {
            var kanDoorwerken = DagPlan.KanDoorwerken(m);
            if (await CalendarClient.WijzigViaUidAsync(
                    m.Uid, m.Titel, nieuwStart, m.Einde - m.Start, m.Omschrijving, _cts.Token,
                    m.Locatie))
            {
                // De werkbaar-vlag verhuist mee naar het nieuwe moment.
                WerkbaarStore.Zet(m.Titel, m.Start, aan: false);
                WerkbaarStore.Zet(m.Titel, new DateTimeOffset(nieuwStart), kanDoorwerken);
                Toast.Toon(this, $"Afspraak verzet naar {DatumKiezer.Toon(doelDag)}", Fluent.Kalender);
                await VerversMeetingsAsync(forceer: true);
            }
            else
            {
                OpenExtern($"https://calendar.google.com/calendar/r/day/{m.Start:yyyy/MM/dd}");
                Toast.Toon(this, "Kon niet in de app verzetten — geopend in Google Agenda",
                    Fluent.Kalender);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Verzetten mislukt: {ex.Message}", Fluent.Kalender);
        }
    }

    /// <summary>
    /// Zichtbaarheid van de projectknoppen: breed venster = klantknoppen naast elkaar,
    /// smal venster = alleen de verzamelknop "Projecten ▾". Goedkoop, mag op elke Resize.
    /// </summary>
    private void WerkProjectWeergaveBij()
    {
        var breed = ClientSize.Width >= 1500;
        foreach (var (knop, _, _) in _projectKnoppen)
        {
            knop.Visible = breed;
        }
        if (_projectenHoofdknop is not null)
        {
            _projectenHoofdknop.Visible = !breed;
        }
    }

    /// <summary>De 🟢-lampjes (draaiende Claude-sessie) op de losse klantknoppen, per poll.</summary>
    private void WerkProjectKnoppenBij()
    {
        foreach (var (knop, label, mappen) in _projectKnoppen)
        {
            var tekst = (mappen.Any(ClientLauncher.IsClaudeActief) ? "🟢 " : "") + label;
            if (knop.Text != tekst)
            {
                knop.Text = tekst;
                knop.KrimpNaarInhoud();
            }
        }
    }

    private MijnTaak? GeselecteerdeLokaleTaak() =>
        _taken.SelectedItems.Count > 0 &&
        _taken.SelectedItems[0].Tag is TaakRij { Lokaal: { } lokaal } ? lokaal : null;

    /// <summary>
    /// Delegeert een (vaak uitgestelde) taak: teamtaak-dialoog met de taaktekst voorgevuld;
    /// bij opslaan wordt de eigen taak afgevinkt — hij is dan van iemand anders.
    /// </summary>
    private void ZetTaakOmNaarTeamtaak()
    {
        if (GeselecteerdeLokaleTaak() is not { } taak)
        {
            return;
        }
        using var dialog = new TeamTaakBewerkForm(
            TeamTaskStore.Load().Leden, new TeamTaak { Tekst = taak.Tekst });
        dialog.Text = "Omzetten naar teamtaak";
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var nieuw = new TeamTaak
        {
            Lid = dialog.Lid,
            Tekst = dialog.TaakTekst,
            Prioriteit = dialog.Prioriteit,
            Subtaken = dialog.Subtaken,
        };
        if (Application.OpenForms.OfType<TeamTasksForm>().FirstOrDefault() is { } open)
        {
            open.VoegTaakToe(nieuw);
        }
        else
        {
            var teamData = TeamTaskStore.Load();
            teamData.Taken.Add(nieuw);
            TeamTaskStore.Save(teamData);
        }
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } eigen)
        {
            eigen.Klaar = true;
            eigen.KlaarOp = DateTimeOffset.Now;
            MijnTaakStore.Save(data);
        }
        Toast.Toon(this, $"Gedelegeerd aan {dialog.Lid} — eigen taak afgevinkt", Fluent.People);
        _ = VerversTakenAsync();
    }

    /// <summary>Schrapt een taak definitief (na bevestiging) — soms is dat het eerlijkste.</summary>
    private async Task SchrapTaakAsync()
    {
        if (GeselecteerdeLokaleTaak() is not { } taak)
        {
            return;
        }
        if (MessageBox.Show(this, $"\"{Kort(taak.Tekst, 60)}\" definitief schrappen?",
                "Taak schrappen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return;
        }
        var data = MijnTaakStore.Load();
        data.Taken.RemoveAll(t => t.Id == taak.Id);
        MijnTaakStore.Save(data);
        Toast.Toon(this, "Taak geschrapt", Fluent.Delete);
        await VerversTakenAsync();
    }

    /// <summary>Verschuift de selectie in de berichtenlijst (j/k-navigatie).</summary>
    private void VerplaatsBerichtSelectie(int delta)
    {
        if (_berichten.Items.Count == 0)
        {
            return;
        }
        var huidig = _berichten.SelectedIndices.Count > 0 ? _berichten.SelectedIndices[0] : -1;
        var nieuw = Math.Clamp(huidig + delta, 0, _berichten.Items.Count - 1);
        _berichten.Items[nieuw].Selected = true;
        _berichten.Items[nieuw].Focused = true;
        _berichten.Items[nieuw].EnsureVisible();
    }

    /// <summary>
    /// Zet de vertaling van het getoonde bericht aan/uit (🌐-knop). Is er nog geen vertaling —
    /// bv. bij een Teams-/WhatsApp-chat — dan wordt die nu opgehaald (ook voor chats).
    /// </summary>
    private async Task ToggleVertalingAsync()
    {
        if (_getoond is not { } mail)
        {
            return;
        }
        if (mail.Vertaling.Length > 0)
        {
            mail.VertaalVerborgen = !mail.VertaalVerborgen;
        }
        else
        {
            _vertaalButton.Bezig = true;
            _vertaalButton.Enabled = false;
            try
            {
                await Vertaler.VertaalAlsNodigAsync(mail, _cts.Token, forceerChat: true);
            }
            catch
            {
                // Vertalen mislukte; melding hieronder.
            }
            finally
            {
                _vertaalButton.Bezig = false;
                _vertaalButton.Enabled = true;
            }
            mail.VertaalVerborgen = false;
            if (mail.Vertaling.Length == 0)
            {
                Toast.Toon(this, "Geen vertaling nodig (al Nederlands of onbekend)", Fluent.Globe);
            }
        }
        if (ReferenceEquals(_getoond, mail) && !IsDisposed && _detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(MailReplyForm.BouwWeergave(mail));
        }
        _vertaalButton.Text = mail is { Vertaling.Length: > 0, VertaalVerborgen: false }
            ? "🌐 Origineel" : "🌐 Vertaling";
    }

    /// <summary>
    /// Voert de PhpStorm-update volledig automatisch uit: installer downloaden (link uit de
    /// taak, anders live uit de JetBrains-API), stil installeren over de bestaande map heen
    /// (NSIS "/S /D=…", met één UAC-prompt) en na afloop de versie controleren en de taak
    /// afvinken. Geen downloadpagina's — het gebeurt echt.
    /// </summary>
    private async Task StartPhpStormUpdateAsync(TaakRij taak)
    {
        // Draaiende PhpStorm eerst laten sluiten: de installer kan er niet overheen schrijven.
        if (System.Diagnostics.Process.GetProcessesByName("phpstorm64").Length > 0)
        {
            Toast.Toon(this, "Sluit PhpStorm eerst — daarna nogmaals dubbelklikken", Fluent.Sync);
            return;
        }
        try
        {
            // Installerlink: uit de taak, of anders vers uit de JetBrains-API.
            var link = taak.Lokaal?.Mail?.Link ?? "";
            if (!link.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Toast.Toon(this, "Installerlink opzoeken…", Fluent.Sync);
                link = (await UpdateCheck.NieuwstePhpStormAsync(_cts.Token)).InstallerUrl ?? "";
            }
            if (!link.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Toast.Toon(this, "Geen installerlink gevonden — probeer later opnieuw", Fluent.Sync);
                return;
            }
            // De huidige installatiemap: nodig om na afloop de oude versie op te ruimen.
            const string root = @"C:\Program Files\JetBrains";
            var installDir = Directory.Exists(root)
                ? Directory.GetDirectories(root, "PhpStorm*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "product-info.json")))
                : null;
            if (installDir is null)
            {
                Toast.Toon(this, "PhpStorm-installatiemap niet gevonden", Fluent.Sync);
                return;
            }

            Toast.Toon(this, "PhpStorm-installer downloaden… (paar honderd MB)", Fluent.Sync);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var bytes = await http.GetByteArrayAsync(link, _cts.Token);
            var pad = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(link).LocalPath));
            await File.WriteAllBytesAsync(pad, bytes, _cts.Token);

            // In een verse, versie-genummerde map naast de oude installeren — zoals de
            // installer zelf standaard doet. Over de bestaande map heen kan niet meer: de
            // stille JetBrains-installer weigert dan met "folder is not empty".
            // ("/D=" moet als laatste, zónder aanhalingstekens; runas = één UAC-klik.)
            var doelMap = Path.Combine(root,
                Path.GetFileNameWithoutExtension(pad).Replace('-', ' '));
            Toast.Toon(this, "Update wordt geïnstalleerd… (PhpStorm niet starten)", Fluent.Sync);
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = pad,
                Arguments = $"/S /D={doelMap}",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc is null)
            {
                Toast.Toon(this, "Installer wilde niet starten", Fluent.Sync);
                return;
            }
            await proc.WaitForExitAsync(_cts.Token);

            var nieuweVersie = UpdateCheck.GeinstalleerdePhpStormVersie();
            if (proc.ExitCode == 0)
            {
                UpdateCheck.VinkTaakAf("PhpStorm bijwerken");
                await VerversTakenAsync();
                Toast.Toon(this, $"PhpStorm bijgewerkt naar {nieuweVersie} ✔", Fluent.Check);
                // De oude versie stil opruimen (eigen UAC-prompt; weiger je die, dan blijft
                // hij staan en kan hij later weg via Instellingen > Apps).
                var uninstall = new[]
                {
                    Path.Combine(installDir, "bin", "Uninstall.exe"),
                    Path.Combine(installDir, "Uninstall.exe"),
                }.FirstOrDefault(File.Exists);
                if (uninstall is not null &&
                    !doelMap.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uninstall,
                        Arguments = "/S",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                }
            }
            else
            {
                Toast.Toon(this, $"Installer gaf exitcode {proc.ExitCode} — versie nu: {nieuweVersie}",
                    Fluent.Sync);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Toast.Toon(this, "Update geannuleerd (UAC geweigerd)", Fluent.Sync);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens de update.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Bijwerken mislukt: {ex.Message}", Fluent.Sync);
        }
    }

    /// <summary>Opent het geselecteerde bericht in de bijbehorende webapp.</summary>
    private void OpenBerichtInBrowser()
    {
        if (_getoond is not { } m)
        {
            return;
        }
        OpenExtern(BerichtUrl(m));
    }

    /// <summary>Webadres van het bericht in de bijbehorende webapp.</summary>
    internal static string BerichtUrl(MailBericht m) => m switch
    {
        // Directe link naar de mail zelf als die bekend is (opgevangen bij het openen in
        // de verborgen OWA-sessie); anders de algemene inbox.
        { OutlookMail.Length: > 0 } =>
            m.OutlookUrl.Length > 0 ? m.OutlookUrl : "https://outlook.office.com/mail/",
        { TeamsChat.Length: > 0 } => "https://teams.cloud.microsoft/",
        { WhatsAppChat.Length: > 0 } => "https://web.whatsapp.com/",
        { ChatSpace.Length: > 0 } =>
            "https://chat.google.com/room/" + m.ChatSpace.Replace("spaces/", ""),
        { MessageId.Length: > 0 } =>
            "https://mail.google.com/mail/u/0/#search/rfc822msgid:" +
            Uri.EscapeDataString(m.MessageId),
        _ => "https://mail.google.com/",
    };

    /// <summary>Bewerkte antwoordtekst terugschrijven naar het bericht en de conceptcache.</summary>
    private void BewaarDetailConcept()
    {
        if (_getoond is { } m && m.Concept != _detailConcept.Text)
        {
            m.Concept = _detailConcept.Text;
            SchrijfConceptCache(m);
        }
    }

    private static void SchrijfConceptCache(MailBericht m)
    {
        if (m.MessageId.Length == 0)
        {
            return;
        }
        var cache = ConceptCache.Load();
        cache[m.MessageId] = new ConceptCache.Entry
        {
            ConceptKlaar = !string.IsNullOrWhiteSpace(m.Concept),
            Concept = m.Concept,
            Reden = m.Reden,
            AlleBeantwoorden = m.AlleBeantwoorden,
            Urgent = m.Urgent,
            Genegeerd = m.Genegeerd,
            Datum = m.Datum,
        };
        ConceptCache.Save(cache);
    }

    /// <summary>
    /// Correspondentie met de afzender uit de laatste 2 maanden, als context zodat Claude
    /// een beter passend concept schrijft (alleen voor Gmail-mails; leeg bij chats/fouten).
    /// </summary>
    private async Task<string> GmailHistoriekAsync(MailBericht bericht, MailReplySettings settings)
    {
        if (bericht.IsChat || bericht.VanAdres.Length == 0 || settings.AppWachtwoord.Length == 0)
        {
            return "";
        }
        try
        {
            return string.Join("\n\n", await GmailClient.CorrespondentieAsync(
                settings, bericht.VanAdres, maanden: 2, max: 12, _cts.Token));
        }
        catch
        {
            return ""; // zonder context gewoon een concept maken
        }
    }

    private async Task ClaudeConceptAsync()
    {
        if (_getoond is not { } bericht)
        {
            return;
        }
        _claudeButton.Enabled = false;
        _claudeButton.Bezig = true;
        try
        {
            var settings = MailReplySettings.Load();
            var resultaat = await ClaudeDrafter.DraftAsync(
                bericht, MailReplySettings.LoadInstructies(), settings, _cts.Token,
                await GmailHistoriekAsync(bericht, settings));
            if (!string.IsNullOrWhiteSpace(resultaat.Concept))
            {
                bericht.Concept = resultaat.Concept;
                bericht.Reden = resultaat.Reden;
                bericht.Urgent = resultaat.Urgent;
                SchrijfConceptCache(bericht);
                if (ReferenceEquals(_getoond, bericht))
                {
                    _detailConcept.Text = resultaat.Concept.ReplaceLineEndings("\r\n");
                }
            }
            else
            {
                Toast.Toon(this, $"Claude stelt geen antwoord voor ({resultaat.Reden})", Fluent.Ster);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het genereren.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Concept genereren mislukt: {ex.Message}", Fluent.Ster);
        }
        finally
        {
            _claudeButton.Bezig = false;
            _claudeButton.Enabled = true;
        }
    }

    /// <summary>Laat Claude het huidige concept herwerken op basis van de feedback in het bijstuurveld.</summary>
    private async Task PasConceptAanAsync()
    {
        if (_getoond is not { } bericht)
        {
            return;
        }
        var feedback = _detailFeedback.Text.Trim();
        if (feedback.Length == 0)
        {
            return;
        }

        BewaarDetailConcept(); // huidige (eventueel bewerkte) tekst meenemen als vertrekpunt
        _detailFeedback.Enabled = false;
        _feedbackButton.Enabled = false;
        _feedbackButton.Bezig = true;
        try
        {
            var nieuw = await ClaudeDrafter.ReviseAsync(
                bericht, _detailConcept.Text, feedback, MailReplySettings.LoadInstructies(),
                MailReplySettings.Load(), _cts.Token);
            if (string.IsNullOrWhiteSpace(nieuw))
            {
                Toast.Toon(this, "Claude gaf geen herwerkt concept terug", Fluent.Ster);
                return;
            }

            bericht.Concept = nieuw;
            bericht.ConceptKlaar = true;
            if (bericht.Reden.Length == 0)
            {
                bericht.Reden = "aangepast op feedback";
            }
            SchrijfConceptCache(bericht);
            if (ReferenceEquals(_getoond, bericht))
            {
                _detailConcept.Text = nieuw.ReplaceLineEndings("\r\n");
            }
            _detailFeedback.Clear();
            Toast.Toon(this, "Concept aangepast", Fluent.Edit);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het herwerken.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Concept aanpassen mislukt: {ex.Message}", Fluent.Ster);
        }
        finally
        {
            _detailFeedback.Enabled = true;
            _feedbackButton.Enabled = true;
            _feedbackButton.Bezig = false;
        }
    }

    private async Task VerstuurDetailAsync()
    {
        if (_getoond is not { } bericht || _detailConcept.Text.Trim().Length == 0)
        {
            return;
        }
        if (bericht.TeamsChat.Length > 0 || bericht.OutlookMail.Length > 0)
        {
            Toast.Toon(this, bericht.TeamsChat.Length > 0
                ? "Teams-chats beantwoord je in Teams zelf"
                : "CED-mails beantwoord je in Outlook zelf", Fluent.Send);
            return;
        }
        var tekst = _detailConcept.Text.Trim();
        _verstuurButton.Enabled = false;
        _verstuurButton.Bezig = true;
        try
        {
            bericht.Concept = tekst;
            if (bericht.WhatsAppChat.Length > 0)
            {
                await WhatsAppClient.Instance.VerstuurAsync(bericht.WhatsAppChat, tekst, _cts.Token);
            }
            else if (bericht.ChatSpace.Length > 0)
            {
                await GoogleChatClient.VerstuurAsync(
                    GoogleChatSettings.Load(), bericht.ChatSpace, tekst, _cts.Token);
            }
            else
            {
                var settings = MailReplySettings.Load();
                var verstuurd = await GmailClient.VerstuurAsync(
                    settings, new[] { bericht }, _ => { }, _cts.Token);
                if (verstuurd.Count == 0)
                {
                    throw new InvalidOperationException("De mail kon niet verstuurd worden.");
                }
                try
                {
                    await GmailClient.ArchiveerAsync(settings, verstuurd, _cts.Token);
                }
                catch
                {
                    // Antwoord is verstuurd; archiveren kan later nog handmatig.
                }
            }
            if (bericht.MessageId.StartsWith("chatjan:", StringComparison.Ordinal))
            {
                // De Jan-chat blijft open: vers transcript laden zodat het verzonden
                // bericht meteen zichtbaar is in het gesprek.
                Toast.Toon(this, "Bericht naar Jan verstuurd", Fluent.Send);
                await Task.Delay(1000, _cts.Token); // Chat even het bericht laten verwerken
                await OpenChatJanAsync();
                return;
            }
            VerwijderRijEnSelecteerVolgende(_berichten.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => ReferenceEquals(i.Tag, bericht)));
            Toast.Toon(this, "Antwoord verstuurd", Fluent.Send);
            // Snelheidsduivel: beantwoorden is de mooiste afhandeling om te klokken.
            if (Snelheid.Registreer(bericht, "beantwoord") is { } snelheidsRecord)
            {
                Confetti.Vier(this);
                Toast.Toon(this, snelheidsRecord, Fluent.Ster);
            }
            ContextSwitch.Registreer(KlantVoorBericht(bericht));
            Prestaties.Gebeurtenis(this, "antwoord",
                ((DateTimeOffset.Now - bericht.Datum).TotalSeconds).ToString("F0"));
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het versturen.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Versturen mislukt: {ex.Message}", Fluent.Send);
        }
        finally
        {
            _verstuurButton.Bezig = false;
            _verstuurButton.Enabled = true;
        }
    }

    /// <summary>
    /// Snoozet de geselecteerde mail: Gmail via het label (zoals in het mailvenster),
    /// Outlook via OWA's eigen sluimerfunctie (staat dan ook echt zo in Outlook).
    /// Chats kunnen dit niet.
    /// </summary>
    /// <summary>
    /// Snoozet het geselecteerde bericht. Zonder <paramref name="preset"/> verschijnt de
    /// datumkiezer; met een preset (bv. vanmiddag/morgenvroeg/maandag) gaat het meteen.
    /// </summary>
    private async Task SnoozeBerichtAsync(DateTimeOffset? preset = null)
    {
        if (GeselecteerdBericht() is not { MessageId.Length: > 0 } bericht ||
            (bericht.IsChat && bericht.OutlookMail.Length == 0))
        {
            return;
        }
        DateTimeOffset gekozen;
        if (preset is { } p)
        {
            gekozen = p;
        }
        else
        {
            var voorstel = SnoozeStore.Voorstel();
            // Naast het lerende tijdstip ook een inhoudelijk voorstel: Claude leest de mail
            // en kiest het logische moment ("factuur eind maand" → de 28e).
            using var dialog = new SnoozeForm(1, voorstel,
                slimVoorstel: ct => ClaudeSnooze.VoorstelAsync(bericht, ct));
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            SnoozeStore.RegistreerKeuze(voorstel, dialog.Gekozen); // hieruit leert het volgende voorstel
            gekozen = dialog.Gekozen;
        }
        if (bericht.OutlookMail.Length > 0)
        {
            try
            {
                var resultaat = await OutlookClient.Instance.SnoozeAsync(
                    bericht.Van, bericht.Onderwerp, gekozen, _cts.Token, bericht.OutlookUrl);
                if (resultaat != "ok")
                {
                    Toast.Toon(this, $"Snoozen in Outlook mislukt ({resultaat} — " +
                        "zie outlook-snooze-debug.json)", Fluent.Klok);
                    return;
                }
                // Niet als "genegeerd" cachen: Outlook haalt de mail zelf uit Postvak IN en
                // zet hem er op de gekozen datum weer in; de cockpit volgt gewoon de inbox.
                VerwijderRijEnSelecteerVolgende(_berichten.Items.Cast<ListViewItem>()
                    .FirstOrDefault(i => ReferenceEquals(i.Tag, bericht)));
                Toast.Toon(this,
                    $"In Outlook gesnoozed tot {gekozen:ddd d MMM}", Fluent.Klok);
            }
            catch (OperationCanceledException)
            {
                // Venster gesloten tijdens het snoozen.
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Snoozen mislukt: {ex.Message}", Fluent.Klok);
            }
            return;
        }
        try
        {
            await GmailClient.SnoozeArchiveerAsync(
                MailReplySettings.Load(), new[] { bericht }, _cts.Token);
            var snoozes = SnoozeStore.LoadSnoozes();
            snoozes.RemoveAll(s => s.MessageId == bericht.MessageId);
            snoozes.Add(new SnoozeStore.SnoozeItem
            {
                MessageId = bericht.MessageId,
                Van = bericht.Van,
                Onderwerp = bericht.Onderwerp,
                Tot = gekozen,
            });
            SnoozeStore.SaveSnoozes(snoozes);

            VerwijderRijEnSelecteerVolgende(_berichten.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => ReferenceEquals(i.Tag, bericht)));
            var gesnoozed = bericht;
            Toast.ToonUndo(this, $"Gesnoozed tot {gekozen:ddd d MMM HH:mm}",
                () => _ = HerstelGmailSnoozeAsync(gesnoozed), Fluent.Klok);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Snoozen mislukt: {ex.Message}", Fluent.Klok);
        }
    }

    /// <summary>Haalt een zojuist gesnoozede Gmail-mail meteen terug in de inbox (undo).</summary>
    private async Task HerstelGmailSnoozeAsync(MailBericht bericht)
    {
        try
        {
            if (await GmailClient.TerugNaarInboxAsync(MailReplySettings.Load(), bericht.MessageId, _cts.Token))
            {
                var snoozes = SnoozeStore.LoadSnoozes();
                snoozes.RemoveAll(s => s.MessageId == bericht.MessageId);
                SnoozeStore.SaveSnoozes(snoozes);
                Toast.Toon(this, "Snooze ongedaan — terug in de inbox", Fluent.Klok);
                await VerversBerichtenAsync();
            }
            else
            {
                Toast.Toon(this, "Kon de mail niet terugvinden om te herstellen", Fluent.Klok);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Herstellen mislukt: {ex.Message}", Fluent.Klok);
        }
    }

    /// <summary>Snooze-presets (label + moment) voor het rechtsklikmenu.</summary>
    private static IEnumerable<(string Label, DateTimeOffset Moment)> SnoozePresets()
    {
        var nu = DateTimeOffset.Now;
        var vandaag = nu.Date;
        DateTimeOffset Op(DateTime dag, int uur) => new(dag.AddHours(uur), nu.Offset);
        // Vanmiddag alleen aanbieden als 14:00 nog niet voorbij is.
        if (nu.Hour < 14)
        {
            yield return ("Vanmiddag (14:00)", Op(vandaag, 14));
        }
        if (nu.Hour < 18)
        {
            yield return ("Vanavond (18:00)", Op(vandaag, 18));
        }
        yield return ("Morgenvroeg (08:00)", Op(vandaag.AddDays(1), 8));
        var maandag = vandaag.AddDays(((int)DayOfWeek.Monday - (int)vandaag.DayOfWeek + 7) % 7 is var d && d == 0 ? 7 : d);
        yield return ($"Maandag ({maandag:d/M} 08:00)", Op(maandag, 8));
    }

    /// <summary>Maakt van het geselecteerde bericht een taak in "Mijn taken" (rechtsklikmenu).</summary>
    /// <summary>Maakt van het geselecteerde bericht een taak voor een teamlid (Taken team).</summary>
    private void MaakTeamTaakVanBericht()
    {
        if (GeselecteerdBericht() is not { } bericht)
        {
            return;
        }
        var data = TeamTaskStore.Load();
        var voorstel = new TeamTaak { Tekst = $"{bericht.Onderwerp} ({bericht.Van})" };
        using var dialog = new TeamTaakBewerkForm(data.Leden, voorstel)
        {
            Text = "Teamtaak maken van bericht",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var nieuw = new TeamTaak
        {
            Lid = dialog.Lid,
            Tekst = dialog.TaakTekst,
            Prioriteit = dialog.Prioriteit,
            Subtaken = dialog.Subtaken,
        };
        // Staat het Taken team-venster open, dan telt zíjn geheugenkopie: rechtstreeks in
        // het bestand schrijven zou bij de eerstvolgende save daar weer verdwijnen.
        if (Application.OpenForms.OfType<TeamTasksForm>().FirstOrDefault() is { } open)
        {
            open.VoegTaakToe(nieuw);
        }
        else
        {
            data = TeamTaskStore.Load(); // vers laden: het venster kan intussen geschreven hebben
            data.Taken.Add(nieuw);
            TeamTaskStore.Save(data);
        }
        Toast.Toon(this, $"Teamtaak voor {dialog.Lid} toegevoegd", Fluent.Checkbox);
    }

    private async Task TaakVanBerichtAsync(MailBericht? bron = null)
    {
        if ((bron ?? GeselecteerdBericht()) is not { } bericht)
        {
            return;
        }
        using var dialog = new MailTaakForm(bericht);
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
            // De mail zelf aan de taak hangen: selecteren van de taak toont hem weer,
            // beantwoorden kan rechtstreeks, en de link opent hem in de webapp.
            Mail = new TaakMail
            {
                Van = bericht.Van,
                VanAdres = bericht.VanAdres,
                AntwoordAan = bericht.AntwoordAan,
                Onderwerp = bericht.Onderwerp,
                Tekst = bericht.Tekst.Length > 8000 ? bericht.Tekst[..8000] + "…" : bericht.Tekst,
                Link = BerichtUrl(bericht),
                Datum = bericht.Datum,
                MessageId = bericht.MessageId,
                Referenties = bericht.Referenties.ToList(),
                ChatSpace = bericht.ChatSpace,
                WhatsAppChat = bericht.WhatsAppChat,
            },
        });
        MijnTaakStore.Save(data);
        // Duidelijke feedback bij een startdatum in de toekomst: de taak is er wél, maar
        // blijft tot die dag verborgen (toggle "Gesnoozed/gepland tonen" toont hem eerder).
        var vandaagDag = DateOnly.FromDateTime(DateTime.Now);
        Toast.Toon(this, dialog.Startdatum is { } sd && sd > vandaagDag
            ? $"Taak toegevoegd — gepland vanaf {sd.ToDateTime(TimeOnly.MinValue):ddd d MMM} " +
              "(tot dan alleen zichtbaar via 'Gesnoozed/gepland tonen')"
            : "Taak toegevoegd aan Mijn taken", Fluent.Checkbox);
        await VerversTakenAsync();
        // Op de achtergrond een korte bevestiging laten schrijven ("ik pak dit dan op,
        // ik houd je op de hoogte"): staat zo klaar in het antwoordvak van bericht én taak.
        _ = GenereerTaakBevestigingAsync(bericht, dialog.Startdatum ?? dialog.Deadline);
        if (dialog.Archiveren)
        {
            await ArchiveerBerichtAsync();
        }
    }

    /// <summary>
    /// Maakt op de achtergrond het bevestigingsconcept bij een taak-van-bericht en zet het
    /// in de conceptcache: het verschijnt dan in het antwoordvak bij het bericht en bij de
    /// taak (die laadt het concept via dezelfde cache).
    /// </summary>
    private async Task GenereerTaakBevestigingAsync(MailBericht bericht, DateOnly? datum)
    {
        if (bericht.MessageId.Length == 0)
        {
            return;
        }
        try
        {
            var concept = await ClaudeDrafter.TaakBevestigingAsync(
                bericht, datum, MailReplySettings.LoadInstructies(), MailReplySettings.Load(),
                _cts.Token);
            if (concept.Length == 0 || IsDisposed)
            {
                return;
            }
            bericht.Concept = concept;
            bericht.ConceptKlaar = true;
            if (bericht.Reden.Length == 0)
            {
                bericht.Reden = "taak gepland — bevestiging staat klaar";
            }
            SchrijfConceptCache(bericht);
            if (_getoond is { } getoond && getoond.MessageId == bericht.MessageId)
            {
                _detailConcept.Text = concept.ReplaceLineEndings("\r\n");
            }
            Toast.Toon(this, "Bevestigingsconcept klaar (in het antwoordvak)", Fluent.Edit);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch
        {
            // Best effort — het concept is een extraatje bij de taak.
        }
    }

    // ---------- Berichtacties ----------

    private MailBericht? GeselecteerdBericht() =>
        _berichten.SelectedItems.Count > 0 ? _berichten.SelectedItems[0].Tag as MailBericht : null;

    /// <summary>
    /// Zet de bijlagen van het geselecteerde bericht in een Google Drive-map. Een leeg
    /// <paramref name="mapId"/> betekent: eerst de mapkiezer tonen.
    /// </summary>
    private async Task BijlagenNaarDriveAsync(string mapId, string mapNaam)
    {
        if (GeselecteerdBericht() is not { } bericht || !BijlagenNaarDrive.HeeftBijlagen(bericht))
        {
            return;
        }
        var resultaat = await BijlagenNaarDrive.UitvoerenAsync(
            this, MailReplySettings.Load(), bericht, mapId, mapNaam,
            melding => Toast.Toon(this, melding, Fluent.Document), _cts.Token);
        if (resultaat.Count > 0)
        {
            Toast.Toon(this, $"{resultaat.Count} bijlage(n) in Drive gezet", Fluent.Document);
        }
    }

    /// <summary>Stuurt gekozen bijlage(n) van de geselecteerde Gmail-mail door naar Billit.</summary>
    private async Task BillitDoorsturenAsync()
    {
        if (GeselecteerdBericht() is not { IsChat: false, OutlookMail.Length: 0 } bericht ||
            !BijlagenNaarDrive.HeeftBijlagen(bericht))
        {
            return;
        }
        var settings = MailReplySettings.Load();
        var adres = settings.BillitAdres.Trim();
        if (adres.Length == 0)
        {
            Toast.Toon(this, "Geen Billit-adres ingesteld — vul dat in via het mailvenster → Instellingen", Fluent.Globe);
            return;
        }
        // Zelfde keuzedialoog als in het mailvenster: per bijlage aanvinken (standaard niets,
        // tenzij er maar één is) en de naam eventueel aanpassen.
        using var dialog = new BijlagenForm(bericht, "", doorsturen: true);
        if (dialog.ShowDialog(this) != DialogResult.OK ||
            (dialog.Selectie.Count == 0 && dialog.LinkSelectie.Count == 0))
        {
            return;
        }
        try
        {
            var aantal = await GmailClient.DoorsturenAsync(
                settings, bericht, adres, dialog.Selectie, dialog.LinkSelectie, _cts.Token);
            Toast.Toon(this,
                $"Naar Billit gestuurd: {aantal} bijlage{(aantal == 1 ? "" : "n")}", Fluent.Send);
        }
        catch (OperationCanceledException)
        {
            // Cockpit gesloten tijdens het doorsturen.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Doorsturen naar Billit mislukt: {ex.Message}", Fluent.Globe);
        }
    }

    private void Beantwoorden()
    {
        if (GeselecteerdBericht() is not { } bericht)
        {
            return;
        }
        using var dialog = new SnelAntwoordForm(bericht);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _berichten.SelectedItems[0].Remove();
            Toast.Toon(this, "Antwoord verstuurd", Fluent.Send);
        }
    }

    private async Task ArchiveerBerichtAsync()
    {
        if (GeselecteerdBericht() is not { } bericht)
        {
            return;
        }
        // Snelheidsduivel: handmatig archiveren telt als afhandeling.
        if (Snelheid.Registreer(bericht, "gearchiveerd") is { } snelheidsRecord)
        {
            Confetti.Vier(this);
            Toast.Toon(this, snelheidsRecord, Fluent.Ster);
        }
        ContextSwitch.Registreer(KlantVoorBericht(bericht));
        Prestaties.Gebeurtenis(this, "archief");
        try
        {
            var melding = "Uit de lijst gehaald";
            if (bericht.VanAdres == "CC-map")
            {
                // CC-overzicht (of een "genoemd"-CC-mail): archiveren = álle mails in de
                // CC-map van Outlook op gelezen zetten, en de rij definitief uit het overzicht
                // halen. De verplaatsing/markering loopt optimistisch op de achtergrond.
                CcOverzicht.Verwijder(bericht.MessageId);
                VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                    ? _berichten.SelectedItems[0] : null);
                Toast.Toon(this, "CC-mails als gelezen zetten…", Fluent.Archive);
                _ = MarkeerCcGelezenOpAchtergrondAsync();
                return;
            }
            if (!bericht.IsChat)
            {
                // Optimistisch, zoals bij Outlook: de rij gaat direct uit de lijst, de
                // IMAP-archivering loopt op de achtergrond. Mislukt die, dan komt de rij
                // meteen terug in de lijst.
                if (bericht.MessageId.Length > 0)
                {
                    _zojuistGearchiveerd.Add(bericht.MessageId);
                }
                VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                    ? _berichten.SelectedItems[0] : null);
                _ = ArchiveerGmailOpAchtergrondAsync(bericht);
                return;
            }
            else if (bericht.OutlookMail.Length > 0)
            {
                // Optimistisch: de rij gaat direct uit de lijst, de verplaatsing loopt op de
                // achtergrond. Bevestigt Outlook hem niet, dan komt de rij meteen terug —
                // de cockpit blijft zo altijd in sync met wat er werkelijk in Outlook staat.
                bericht.Genegeerd = true;
                SchrijfConceptCache(bericht);
                VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                    ? _berichten.SelectedItems[0] : null);
                Toast.ToonUndo(this, "Archiveren in Outlook…",
                    () => _ = HerstelLaatsteArchiveringAsync(), Fluent.Archive);
                _ = ArchiveerOutlookOpAchtergrondAsync(bericht);
                return;
            }
            else if (bericht.SmartschoolBericht.Length > 0)
            {
                // Optimistisch, zoals bij Outlook: de rij gaat direct uit de lijst, het
                // verplaatsen naar "Berichten archief" op Smartschool loopt op de
                // achtergrond (met een toast als het daar toch niet lukte).
                bericht.Genegeerd = true;
                _zojuistGearchiveerd.Add(bericht.MessageId);
                SchrijfConceptCache(bericht);
                VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                    ? _berichten.SelectedItems[0] : null);
                Toast.Toon(this, "Archiveren in Smartschool…", Fluent.Archive);
                _ = ArchiveerSmartschoolOpAchtergrondAsync(bericht);
                return;
            }
            else if ((bericht.TeamsChat.Length > 0 || bericht.WhatsAppChat.Length > 0) &&
                     bericht.MessageId.Length > 0)
            {
                // Archiveren van een chat = ook echt als gelezen zetten in Teams/WhatsApp.
                // Ook hier optimistisch: de rij verdwijnt meteen, het gelezen zetten loopt
                // op de achtergrond (en zo nodig via de wachtrij bij een volgende poll).
                bericht.Genegeerd = true;
                _zojuistGearchiveerd.Add(bericht.MessageId);
                SchrijfConceptCache(bericht);
                if (bericht.WhatsAppChat.Length > 0)
                {
                    VersRegister.WaVers.Verwijder(bericht.MessageId); // voorgeladen rij is afgehandeld
                }
                else
                {
                    VersRegister.TeamsVers.Verwijder(bericht.MessageId);
                }
                VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                    ? _berichten.SelectedItems[0] : null);
                Toast.Toon(this, "Uit de lijst gehaald", Fluent.Archive);
                _ = MarkeerChatGelezenOpAchtergrondAsync(bericht);
                return;
            }
            else if (bericht.MessageId.Length > 0)
            {
                // Gearchiveerde chat onthouden: niet opnieuw tonen tot er nieuwe berichten zijn.
                bericht.Genegeerd = true;
                _zojuistGearchiveerd.Add(bericht.MessageId);
                SchrijfConceptCache(bericht);
            }
            VerwijderRijEnSelecteerVolgende(_berichten.SelectedItems.Count > 0
                ? _berichten.SelectedItems[0] : null);
            Toast.Toon(this, melding, Fluent.Archive);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Archiveren mislukt: {ex.Message}", Fluent.Archive);
        }
    }

    /// <summary>
    /// Archiveert een Smartschool-bericht op de achtergrond (verplaatsing naar
    /// "Berichten archief" op de site zelf); een mislukking meldt zich met een toast
    /// en de rij komt bij de volgende verversbeurt vanzelf terug.
    /// </summary>
    private async Task ArchiveerSmartschoolOpAchtergrondAsync(MailBericht bericht)
    {
        try
        {
            var delen = bericht.SmartschoolBericht.Split('|', 2);
            var gelukt = delen.Length == 2 &&
                await SmartschoolClient.Instance.ArchiveerAsync(delen[0], delen[1], _cts.Token);
            if (!gelukt && !IsDisposed)
            {
                _zojuistGearchiveerd.Remove(bericht.MessageId);
                bericht.Genegeerd = false; // anders filtert de conceptcache hem blijvend weg
                SchrijfConceptCache(bericht);
                Toast.Toon(this,
                    "Smartschool bevestigde het archiveren niet — het bericht komt terug in de lijst",
                    Fluent.Archive);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _zojuistGearchiveerd.Remove(bericht.MessageId);
                bericht.Genegeerd = false;
                SchrijfConceptCache(bericht);
                Toast.Toon(this, $"Smartschool-archivering mislukt: {ex.Message}", Fluent.Archive);
            }
        }
    }

    /// <summary>Zet een zojuist gearchiveerde Gmail-mail terug in de inbox (undo).</summary>
    private async Task HerstelGmailArchiefAsync(MailBericht bericht)
    {
        try
        {
            // Anders filtert de eerstvolgende verversbeurt de teruggezette mail weer weg.
            _zojuistGearchiveerd.Remove(bericht.MessageId);
            if (await GmailClient.TerugNaarInboxAsync(MailReplySettings.Load(), bericht.MessageId, _cts.Token))
            {
                Toast.Toon(this, "Terug in de inbox", Fluent.Archive);
                await VerversBerichtenAsync();
            }
            else
            {
                Toast.Toon(this, "Kon de mail niet terugvinden om te herstellen", Fluent.Archive);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Herstellen mislukt: {ex.Message}", Fluent.Archive);
        }
    }

    // ---------- Laatste Outlook-archivering ongedaan maken ----------

    private static readonly string OutlookHerstelFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "outlook-laatst-gearchiveerd.json");

    private sealed record LaatsteArchivering(
        string Van, string Onderwerp, string MessageId, DateTimeOffset Moment);

    private static void BewaarLaatsteArchivering(MailBericht bericht)
    {
        try
        {
            File.WriteAllText(OutlookHerstelFile, System.Text.Json.JsonSerializer.Serialize(
                new LaatsteArchivering(bericht.Van, bericht.Onderwerp, bericht.MessageId,
                    DateTimeOffset.Now),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: zonder dit bestand ontbreekt alleen de terugzet-optie.
        }
    }

    /// <summary>Zet de laatst gearchiveerde Outlook-mail terug in Postvak IN (rechtsklikmenu).</summary>
    private async Task HerstelLaatsteArchiveringAsync()
    {
        try
        {
            if (!File.Exists(OutlookHerstelFile) ||
                System.Text.Json.JsonSerializer.Deserialize<LaatsteArchivering>(
                    File.ReadAllText(OutlookHerstelFile)) is not { } info)
            {
                Toast.Toon(this, "Geen Outlook-archivering om terug te zetten", Fluent.Archive);
                return;
            }
            Toast.Toon(this, $"Terugzetten: \"{info.Onderwerp}\"…", Fluent.Archive);
            var resultaat = await OutlookClient.Instance.HerstelUitArchiefAsync(
                info.Van, info.Onderwerp, _cts.Token);
            if (resultaat != "ok")
            {
                Toast.Toon(this, resultaat == "rij-niet-gevonden"
                    ? $"\"{info.Onderwerp}\" niet gevonden in de Archief-map"
                    : "Terugzetten mislukt (Verplaatsen-knop niet gevonden in Outlook)",
                    Fluent.Archive);
                return;
            }
            // De genegeerd-markering weghalen zodat de mail weer in de cockpit verschijnt.
            var cache = ConceptCache.Load();
            if (info.MessageId.Length > 0 && cache.TryGetValue(info.MessageId, out var entry))
            {
                entry.Genegeerd = false;
                ConceptCache.Save(cache);
            }
            File.Delete(OutlookHerstelFile);
            Toast.Toon(this, $"\"{info.Onderwerp}\" staat weer in Postvak IN", Fluent.Archive);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het terugzetten.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Terugzetten mislukt: {ex.Message}", Fluent.Archive);
        }
    }

    /// <summary>
    /// Volledige synchronisatie (dropdown naast "Nu verversen"): alle verbergmarkeringen
    /// van Outlook-mails wissen zodat de lijst weer exact het echte postvak toont, de
    /// verborgen sessies vers herladen, en daarna gewoon volledig verversen.
    /// </summary>
    private async Task VolledigeSyncAsync()
    {
        var cache = ConceptCache.Load();
        var hersteld = 0;
        foreach (var (id, entry) in cache)
        {
            if (id.StartsWith("owa:", StringComparison.OrdinalIgnoreCase) && entry.Genegeerd)
            {
                entry.Genegeerd = false;
                hersteld++;
            }
        }
        if (hersteld > 0)
        {
            ConceptCache.Save(cache);
        }
        _genegeerdMaarAanwezig.Clear();
        foreach (var m in _laatsteBerichten.Where(m => m.OutlookMail.Length > 0))
        {
            m.Genegeerd = false;
        }
        OutlookClient.Instance.ForceerHerlaad();
        TeamsClient.Instance.ForceerHerlaad();
        // Ook de CED-agendacache weg: een in Office 365 verwijderde of verzette afspraak
        // bleef anders tot een halfuur staan (de dagcache gold nog). Volledige sync moet
        // álles opnieuw ophalen, ook de agenda.
        _cedCache.Clear();
        Toast.Toon(this, $"Volledige synchronisatie gestart ({hersteld} verborgen mail(s) hersteld)",
            Fluent.Sync);
        await VerversAsync();
    }

    /// <summary>
    /// Zet na het archiveren van de CC-overzichtsrij de volledige CC-map in Outlook op
    /// gelezen (achtergrond, zodat de UI niet blokkeert). Best effort: lukt het niet, dan
    /// blijft de rij toch uit de lijst (op gelezen zetten kan altijd nog in Outlook zelf).
    /// </summary>
    private async Task MarkeerCcGelezenOpAchtergrondAsync()
    {
        try
        {
            var ok = await OutlookClient.Instance.MarkeerCcGelezenAsync(_cts.Token);
            if (!IsDisposed)
            {
                Toast.Toon(this, ok
                    ? "CC-mails als gelezen gezet in Outlook"
                    : "CC-rij uit de lijst — 'als gelezen' niet bevestigd door Outlook",
                    Fluent.Archive);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Toast.Toon(this, $"CC als gelezen zetten mislukt: {ex.Message}", Fluent.Archive);
            }
        }
    }

    /// <summary>
    /// Voert de Gmail-archivering uit nadat de rij al (optimistisch) uit de lijst is:
    /// gelukt = undo-toast, mislukt = de rij komt direct terug in de lijst.
    /// </summary>
    private async Task ArchiveerGmailOpAchtergrondAsync(MailBericht bericht)
    {
        try
        {
            await GmailClient.ArchiveerAsync(
                MailReplySettings.Load(), new[] { bericht }, _cts.Token);
            if (IsDisposed)
            {
                return;
            }
            // Gmail-archivering is direct terug te draaien (mail terug uit "Alle berichten").
            if (bericht.MessageId.Length > 0)
            {
                Toast.ToonUndo(this, "Gearchiveerd",
                    () => _ = HerstelGmailArchiefAsync(bericht), Fluent.Archive);
            }
            else
            {
                Toast.Toon(this, "Gearchiveerd", Fluent.Archive);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            if (IsDisposed)
            {
                return;
            }
            _zojuistGearchiveerd.Remove(bericht.MessageId);
            if (!_laatsteBerichten.Any(m => m.MessageId == bericht.MessageId))
            {
                _laatsteBerichten.Add(bericht);
            }
            HervulBerichtenLijst();
            Toast.Toon(this,
                $"Archiveren mislukt ({ex.Message}) — de mail staat weer in de lijst",
                Fluent.Archive);
        }
    }

    /// <summary>
    /// Zet een chat als gelezen in Teams/WhatsApp nadat de rij al (optimistisch) uit de
    /// lijst is. Lukt het niet meteen, dan neemt de duurzame wachtrij het over bij de
    /// volgende polls — de rij blijft dan gewoon weg.
    /// </summary>
    private async Task MarkeerChatGelezenOpAchtergrondAsync(MailBericht bericht)
    {
        var teams = bericht.TeamsChat.Length > 0;
        try
        {
            if (teams)
            {
                await TeamsClient.Instance.MarkeerGelezenAsync(bericht.TeamsChat, _cts.Token);
            }
            else
            {
                await WhatsAppClient.Instance.MarkeerGelezenAsync(bericht.WhatsAppChat, _cts.Token);
            }
            if (!IsDisposed)
            {
                Toast.Toon(this, teams
                    ? "In Teams als gelezen gezet"
                    : "In WhatsApp als gelezen gezet", Fluent.Archive);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch
        {
            // In de wachtrij: volgende polls proberen het gelezen zetten opnieuw.
            ActieWachtrij.Voeg(new ActieWachtrij.Actie
            {
                Soort = teams ? "teams-gelezen" : "wa-gelezen",
                Chat = teams ? bericht.TeamsChat : bericht.WhatsAppChat,
            });
            if (!IsDisposed)
            {
                Toast.Toon(this,
                    teams
                        ? "Gelezen zetten in Teams volgt automatisch bij een volgende poging"
                        : "Gelezen zetten in WhatsApp volgt automatisch bij een volgende poging",
                    Fluent.Archive);
            }
        }
    }

    /// <summary>
    /// Voert de Outlook-archivering uit nadat de rij al (optimistisch) uit de lijst is:
    /// bij een bevestigde verplaatsing alleen een toast, anders komt de rij direct terug.
    /// </summary>
    private async Task ArchiveerOutlookOpAchtergrondAsync(MailBericht bericht)
    {
        string resultaat;
        try
        {
            resultaat = await OutlookClient.Instance.ArchiveerAsync(
                bericht.Van, bericht.Onderwerp, _cts.Token, bericht.OutlookUrl);
        }
        catch (OperationCanceledException)
        {
            return; // venster gesloten
        }
        catch (Exception ex)
        {
            resultaat = ex.Message;
        }
        if (IsDisposed)
        {
            return;
        }
        if (resultaat == "ok")
        {
            // Onthouden voor "Laatste Outlook-archivering terugzetten" (rechtsklikmenu).
            BewaarLaatsteArchivering(bericht);
            Toast.Toon(this, "In Outlook verwerkt (bevestigd)", Fluent.Archive);
            return;
        }
        bericht.Genegeerd = false;
        SchrijfConceptCache(bericht);
        if (!_laatsteBerichten.Any(m => m.MessageId == bericht.MessageId))
        {
            _laatsteBerichten.Add(bericht);
        }
        HervulBerichtenLijst();
        // In de duurzame wachtrij: volgende polls proberen het opnieuw (met backoff);
        // lukt het alsnog, dan verdwijnt de mail vanzelf weer uit de lijst.
        ActieWachtrij.Voeg(new ActieWachtrij.Actie
        {
            Soort = "outlook-archief",
            Van = bericht.Van,
            Onderwerp = bericht.Onderwerp,
            Url = bericht.OutlookUrl,
        });
        Toast.Toon(this, resultaat switch
        {
            "rij-niet-gevonden" =>
                "Niet gearchiveerd: mail niet gevonden in Outlook — staat weer in de lijst; ik blijf het proberen",
            "knop-niet-gevonden" =>
                "Niet gearchiveerd: knop 'Verwerkt'/'Archiveren' niet gevonden — staat weer in de lijst; ik blijf het proberen",
            "niet-verdwenen" =>
                "Niet gearchiveerd: Outlook bevestigde de verplaatsing niet — staat weer in de lijst; ik blijf het proberen",
            _ => $"Niet gearchiveerd ({resultaat}) — staat weer in de lijst; ik blijf het proberen",
        }, Fluent.Archive);
    }

    /// <summary>
    /// Houdt de dagplanning bij de tijd bij elke ophaalronde: nieuw binnengekomen dringende
    /// mails en nieuwe taken schuiven erin, afgehandelde vallen eruit. Alleen als er vandaag
    /// een plan gemaakt is — anders valt er niets bij te werken.
    /// </summary>
    private void WerkDagPlanBij(List<MailBericht> berichten)
    {
        if (DagPlan.LaadVandaag() is not { } plan)
        {
            return;
        }
        var (bij, weg) = DagPlan.VulAan(plan, berichten, HuidigeMeetings());
        _ = weg;
        if (bij > 0)
        {
            Toast.Toon(this, bij == 1
                ? "Dagplanning bijgewerkt: 1 item erbij"
                : $"Dagplanning bijgewerkt: {bij} items erbij", Fluent.Ster);
        }
        VulTakenLijst(); // "▶ NU:" in de groepstitel klopt weer
    }

    private bool _autoPlanGeprobeerd; // hooguit één automatische poging per sessie

    /// <summary>
    /// Maakt bij de eerste start van de dag automatisch de dagplanning (dezelfde als de knop
    /// "Plan mijn dag"). Bestaat er al een plan voor vandaag, dan gebeurt er niets — dus
    /// effectief één keer per dag. Mislukt de Claude-aanroep, dan blijft de knop de weg.
    /// </summary>
    private async Task AutoPlanDagAsync()
    {
        if (_autoPlanGeprobeerd || DagPlan.LaadVandaag() is not null)
        {
            return;
        }
        _autoPlanGeprobeerd = true;
        try
        {
            var plan = await DagPlan.MaakAsync(HuidigeMeetings(), "17:30", _cts.Token);
            if (IsDisposed)
            {
                return;
            }
            VulTakenLijst(); // "▶ NU:" meteen in de titel
            var werk = plan.Items.Count(i => !i.VastBlok && !i.Afgehandeld);
            Toast.Toon(this, werk == 0
                ? "Dagplanning klaar — niets te plannen vandaag 🎉"
                : $"Dagplanning klaar: {werk} items — kijk via de knop Dagplanning", Fluent.Ster);
        }
        catch (OperationCanceledException)
        {
            // Cockpit gesloten tijdens het plannen.
        }
        catch
        {
            // Claude niet beschikbaar: geen drama, de knop "Dagplanning" blijft werken.
        }
    }

    /// <summary>
    /// De eerstvolgende actie uit de dagplanning van vandaag, klaar om achter de titel van het
    /// takenpaneel te hangen. Leeg als er (nog) geen plan is of alles afgewerkt is.
    /// </summary>
    private static string VolgendeUitDagPlan()
    {
        if (DagPlan.LaadVandaag() is not { } plan)
        {
            return "";
        }
        var volgende = DagPlan.Tijdlijn(plan).FirstOrDefault(r => !r.Item.VastBlok).Item;
        if (volgende is null)
        {
            return "";
        }
        var duur = volgende.Minuten >= 60
            ? $"{volgende.Minuten / 60}u{(volgende.Minuten % 60 == 0 ? "" : $"{volgende.Minuten % 60:00}")}"
            : $"{volgende.Minuten} min";
        return $"      ▶ NU: {Kort(volgende.Tekst, 50)} (~{duur})";
    }

    /// <summary>Een bericht waarop je kunt reageren: een Google Chat met een berichtnaam.</summary>
    private static bool IsChatBericht(MailBericht? m) =>
        m is { ChatSpace.Length: > 0 } && m.MessageId.StartsWith("chat:", StringComparison.Ordinal);

    /// <summary>
    /// Reageert met een emoji (standaard 👍) op het laatste bericht van de geselecteerde Google
    /// Chat en handelt de rij af — een duim is vaak alles wat een chatbericht nodig heeft.
    /// </summary>
    private async Task DuimOpBerichtAsync(string emoji = "👍")
    {
        if (GeselecteerdBericht() is not { } bericht || !IsChatBericht(bericht))
        {
            return;
        }
        try
        {
            await GoogleChatClient.ReageerAsync(
                GoogleChatSettings.Load(), bericht.MessageId[5..], _cts.Token, emoji);
            // Afgehandeld: rij weg, en genegeerd markeren zodat de volgende poll hem niet
            // terugbrengt (tot er een nieuw bericht in de chat komt).
            bericht.Genegeerd = true;
            SchrijfConceptCache(bericht);
            VerwijderRijEnSelecteerVolgende(_berichten.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => ReferenceEquals(i.Tag, bericht)));
            Toast.Toon(this, $"{emoji} gestuurd naar {bericht.Van}", Fluent.Check);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Duim sturen mislukt: {ex.Message}", Fluent.Send);
        }
    }

    /// <summary>
    /// Verschuift een taak in het dagplan (versleept in de takenlijst, dagplan-modus): de
    /// versleepte komt vlak vóór het doel te staan; zonder doel achteraan.
    /// </summary>
    private void VerplaatsTaakInPlan(TaakRij versleept, TaakRij? doel)
    {
        if (DagPlan.LaadVandaag() is not { } plan || versleept.Lokaal is not { } bron)
        {
            return;
        }
        var item = plan.Items.FirstOrDefault(i => i.TaakId == bron.Id && !i.Afgehandeld);
        if (item is null)
        {
            Toast.Toon(this, "Deze taak staat niet in de dagplanning van vandaag", Fluent.Ster);
            return;
        }
        plan.Items.Remove(item);
        var doelItem = doel?.Lokaal is { } d
            ? plan.Items.FirstOrDefault(i => i.TaakId == d.Id && !i.Afgehandeld)
            : null;
        if (doelItem is null)
        {
            plan.Items.Add(item);
        }
        else
        {
            plan.Items.Insert(plan.Items.IndexOf(doelItem), item);
        }
        item.Waarom = "zelf gekozen volgorde";
        DagPlan.Bewaar(plan);
        VulTakenLijst(); // nieuwe volgorde + geplande uren meteen zichtbaar
    }

    /// <summary>Dezelfde afspraak in twee agenda's: zelfde UID, of zelfde titel + tijdslot.</summary>
    private static bool ZelfdeAfspraak(AgendaClient.AgendaItem a, AgendaClient.AgendaItem b) =>
        (a.Uid.Length > 0 && a.Uid == b.Uid) ||
        (a.Start == b.Start && a.Einde == b.Einde &&
         string.Equals(a.Titel.Trim(), b.Titel.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>De meetings die nu in de lijst staan (voor de dagplanning).</summary>
    private List<AgendaClient.AgendaItem> HuidigeMeetings() =>
        _meetings.Items.Cast<ListViewItem>()
            // Hilkes afspraken staan er ter info bij, maar zijn niet Maartens agenda: ze horen
            // niet in de dagplanning en de reisassistent hoeft er niet voor te rekenen.
            .Where(i => i.Name != "hilke")
            .Select(i => i.Tag).OfType<AgendaClient.AgendaItem>().ToList();

    /// <summary>
    /// Houdt de volgende-meeting-balk bij: zichtbaar zodra vandaag een echte meeting (geen
    /// recept, geen "werkbaar") binnen het uur begint of bezig is, met de videolink als knop.
    /// </summary>
    private void WerkVolgendeMeetingBalkBij()
    {
        if (_volgendeMeetingBalk is null || _volgendeMeetingLabel is null || _deelnemenKnop is null)
        {
            return;
        }
        var nu = DateTimeOffset.Now;
        var volgende = _meetingsOffset != 0
            ? null
            : HuidigeMeetings()
                .Where(m => !m.HeleDag && m.Einde > nu && !DagPlan.KanDoorwerken(m) &&
                            !IsReceptTitel(m.Titel) && m.Start <= nu.AddMinutes(60))
                .MinBy(m => m.Start);
        if (volgende is null)
        {
            _volgendeMeetingBalk.Visible = false;
            return;
        }
        var titel = Kort(volgende.Titel.Replace("CED · ", ""), 60);
        _volgendeMeetingLabel.Text = volgende.Start <= nu
            ? $"▶ Nu bezig: {titel}  (tot {volgende.Einde.ToLocalTime():HH:mm})"
            : $"⏰ Over {Math.Max(1, (int)Math.Ceiling((volgende.Start - nu).TotalMinutes))} min: " +
              $"{titel}  ({volgende.Start.ToLocalTime():HH:mm})";
        var link = MeetingLink(volgende);
        _deelnemenKnop.Visible = link is not null;
        _deelnemenKnop.Tag = link ?? "";
        _volgendeMeetingBalk.Visible = true;
    }

    /// <summary>
    /// Zet het Projecten-menu-item van één projectmap op de laatst bekende git-stand uit de
    /// dagcache, met het controlemoment erbij. ⬇ vooraan zodra de repo achterloopt op de
    /// remote: het signaal om eerst te pullen voordat je verder werkt of deployt.
    /// </summary>
    private void WerkGitLabelBij(ToolStripMenuItem item, string map, string naam)
    {
        if (!_gitCache.PerMap.TryGetValue(map, out var stand))
        {
            item.Text = $"◆ Git-status — {naam} (nog niet gecontroleerd)";
            return;
        }
        var lokaal = stand.Moment.LocalDateTime;
        var moment = lokaal.Date == DateTime.Now.Date ? $"{lokaal:HH:mm}" : $"{lokaal:d MMM}";
        var teken = stand.Achter > 0 ? "⬇" : "◆";
        item.Text = $"{teken} Git-status — {naam} ({stand.Kort} · {moment})";
    }

    /// <summary>
    /// Controleert de git-status van alle projectmappen en werkt de dagcache en de
    /// menulabels bij. Loopt automatisch één keer per dag (poll) en handmatig via
    /// "Git controleren" in het ▾-menu naast "Nu verversen".
    /// </summary>
    private async Task ControleerGitAsync(bool handmatig)
    {
        if (_gitControleBezig)
        {
            if (handmatig)
            {
                Toast.Toon(this, "Git-controle loopt al", Fluent.Sync);
            }
            return;
        }
        _gitControleBezig = true;
        try
        {
            var mappen = _gitMenuItems
                .Select(g => g.Map)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var metWerk = 0;
            foreach (var map in mappen)
            {
                var rapport = await GitStatus.OphalenAsync(map, _cts.Token);
                _gitCache.PerMap[map] = new GitStatusCache.Stand
                {
                    Kort = rapport.Kort,
                    Achter = rapport.Achter,
                    Moment = DateTimeOffset.Now,
                };
                if (rapport.Fout is not null || rapport.Aantal > 0 || rapport.Achter > 0)
                {
                    metWerk++;
                }
                foreach (var (item, m, naam) in _gitMenuItems)
                {
                    if (string.Equals(m, map, StringComparison.OrdinalIgnoreCase))
                    {
                        WerkGitLabelBij(item, m, naam);
                    }
                }
            }
            _gitCache.LaatsteControle = DateTimeOffset.Now;
            GitStatusCache.Save(_gitCache);
            if (handmatig && !IsDisposed)
            {
                Toast.Toon(this, metWerk == 0
                    ? $"Git gecontroleerd: alle {mappen.Count} projecten schoon en up-to-date"
                    : $"Git gecontroleerd: {metWerk} van {mappen.Count} projecten met openstaand werk — zie Projecten ▾",
                    Fluent.Sync);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        finally
        {
            _gitControleBezig = false;
        }
    }

    private sealed record TaakRij(
        string Tekst, DateOnly? Deadline, string Bron, MijnTaak? Lokaal, string AsanaGid,
        int Prioriteit = 1, string AsanaOmschrijving = "");

    /// <summary>
    /// De "Claude bijwerken"-knop staat altijd in de werkbalk, zodat je patch-updates op eigen
    /// tempo kunt binnenhalen. Alleen bij een versiesprong-taak van UpdateCheck (bv. 2.1 → 2.2)
    /// kleurt hij accent en toont hij de taaktekst met de versienummers.
    /// </summary>
    private void WerkClaudeUpdateKnopBij()
    {
        // Alleen in beeld als er geen Claude-CLI-sessies draaien: winget kan de exe toch
        // niet vervangen zolang er één open staat, dus tot die tijd is de knop alleen ruis.
        _claudeUpdateKnop.Visible = LopendeClaudeCliSessies() == 0;
        var taak = UpdateCheck.OpenUpdateTaak("Claude bijwerken");
        _claudeUpdateKnop.Kind = taak is null ? ButtonKind.Normal : ButtonKind.Accent;
        _claudeUpdateKnop.Text = taak ?? "Claude bijwerken";
        _claudeUpdateKnop.KrimpNaarInhoud();
        _claudeUpdateKnop.Invalidate();
    }

    private async Task VerversTakenAsync()
    {
        WerkClaudeUpdateKnopBij();
        // Storingstaken (MailMobility/MailProperty) automatisch afvinken als het 20 minuten
        // stil is; komt er daarna weer een mail, dan zet de mailpoll een nieuwe taak.
        try
        {
            AlarmMails.VinkStilleAf();
        }
        catch
        {
            // Best effort.
        }
        // Cellaware data-checks (max. 1× per 6 uur): fouten worden een rode taak, en zodra
        // alles weer OK is vinkt hij zichzelf af.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await DataCheckRadar.ZorgVoorAsync(_cts.Token) && !IsDisposed)
                {
                    BeginInvoke(() => _ = VerversTakenAsync());
                }
            }
            catch
            {
                // Volgende ronde opnieuw.
            }
        }, _cts.Token);

        // AH-leverdag: om de 2 uur op ah.be kijken of er al een exacter bezorgvenster bekend
        // is en de agenda-afspraak daar meteen op aanpassen. Bewust géén Task.Run: WebView2
        // mag alleen vanaf de UI-thread bediend worden.
        _ = AhBezorgRadar.ZorgVoorAsync(this, _cts.Token);

        // Maandelijks de AH-producttabel aanvullen met nieuwe producten uit
        // /producten/eerder-gekocht (zelfde levende AH-sessie, dus ook UI-thread).
        _ = AhProductOogst.ZorgVoorAsync(this, _cts.Token);

        // Eén keer per maand een nieuw AH-recept laten verzinnen door Claude (achtergrond;
        // alleen de toast gaat via BeginInvoke terug naar de UI-thread).
        _ = Task.Run(() => AhReceptVanDeMaand.ZorgVoorAsync(this, _cts.Token), _cts.Token);

        // 's Avonds herinneren dat het mandje nog aangevuld kan worden als er morgen een
        // AH-levering gepland staat (max. 1 agenda-check per dag).
        _ = Task.Run(() => AhBestelDeadline.ZorgVoorAsync(this, _cts.Token), _cts.Token);

        // Gerechten zonder recept krijgen er automatisch één van Claude (max. 5 per dag).
        _ = Task.Run(() => AhReceptAanvuller.ZorgVoorAsync(this, _cts.Token), _cts.Token);

        // Deploy-vreugde: pushes in de projectrepo's vieren. De upstream-ref verschuift
        // alleen bij een eigen push, dus dit werkt zonder netwerk (max. 1×/10 min).
        _ = Task.Run(async () =>
        {
            try
            {
                var vieringen = await DeployVreugde.CheckAsync(_cts.Token);
                if (vieringen.Count > 0 && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        Confetti.Vier(this);
                        foreach (var viering in vieringen)
                        {
                            Toast.Toon(this, viering, Fluent.Ster);
                            Prestaties.Gebeurtenis(this, "deploy");
                        }
                    });
                }
            }
            catch
            {
                // Volgende ronde opnieuw.
            }
        }, _cts.Token);

        // Lunch-luider: rond de middag zonder pauze in het plan? Dan komt er een blokje bij.
        try
        {
            if (DagPlan.VoegLunchToe())
            {
                Toast.Toon(this,
                    "🍽️ Ook machines hebben stroom nodig — 20 min lunchpauze in je dagplan gezet",
                    Fluent.Klok);
            }
            // En na de middag: een nooit-afgevinkte lunchpauze stilletjes opruimen.
            DagPlan.RuimLunchOp();
        }
        catch
        {
            // Best effort.
        }
        // Context-switch-teller: rond 16:00 één keer het dagrapport.
        if (ContextSwitch.DagRapport() is { } sprongRapport)
        {
            Toast.Toon(this, sprongRapport, Fluent.Ster);
        }
        // Timesheet-gatendetector: voorbije meetings zonder boeking worden een taak — een
        // toast alleen is te vluchtig voor iets dat letterlijk geld waard is.
        try
        {
            // Geplande avondmaaltijden (🍴-recepten) zijn geen werk — nooit een timesheet voor
            // voorstellen, hoe blokkerend ze ook in de agenda staan.
            if (_meetingsOffset == 0 && TimesheetGaten.Controleer(
                    HuidigeMeetings().Where(m => !IsReceptTitel(m.Titel)).ToList()) is { Count: > 0 } gaten)
            {
                var regels = gaten.Select(m =>
                    $"{m.Start.ToLocalTime():HH:mm}–{m.Einde.ToLocalTime():HH:mm} " +
                    m.Titel.Replace("CED · ", "").Replace("Hilke · ", ""));
                var taken = MijnTaakStore.Load();
                var tekst = gaten.Count == 1
                    ? $"🕳️ Timesheet ontbreekt: {Kort(gaten[0].Titel.Replace("CED · ", ""), 60)}"
                    : $"🕳️ Timesheets aanvullen: {gaten.Count} meetings niet geboekt";
                if (!taken.Taken.Any(t => !t.Klaar &&
                        t.Tekst.StartsWith("🕳️", StringComparison.Ordinal)))
                {
                    taken.Taken.Add(new MijnTaak
                    {
                        Tekst = tekst,
                        Categorie = gaten.All(g =>
                            g.Titel.StartsWith("CED · ", StringComparison.Ordinal)) ? "CED" : "Werk",
                        Prioriteit = 1,
                        Deadline = DateOnly.FromDateTime(DateTime.Now),
                        Mail = new TaakMail
                        {
                            Onderwerp = tekst,
                            Tekst = "Niet teruggevonden in de timesheets van vandaag:\n" +
                                string.Join("\n", regels) +
                                "\n\nBoeken: rechtsklik op de meeting → \"Timesheet maken…\".",
                        },
                    });
                    MijnTaakStore.Save(taken);
                    Toast.Toon(this, $"{tekst} — staat als taak klaar", Fluent.Klok);
                }
            }
        }
        catch
        {
            // Best effort.
        }

        // Meeliftende retry: eerder mislukte timesheet-doorboekingen alsnog naar urbanadmin
        // sturen (stil; komt meteen terug als de wachtrij leeg is).
        _ = Task.Run(async () =>
        {
            try
            {
                await TimesheetStore.BoekDoorAsync(_cts.Token);
            }
            catch
            {
                // Volgende verversing opnieuw proberen.
            }
        }, _cts.Token);
        // Eerst de (trage) Asana-call, en pas daarná de lokale taken laden: vink je tijdens
        // die seconden een taak af, dan bouwde een al-lopende verversing de lijst anders op
        // met verouderde data en dook de net afgevinkte rij meteen weer op.
        var asanaRijen = new List<TaakRij>();
        try
        {
            var asana = AsanaSettings.Load();
            if (asana.Compleet)
            {
                // Bewust alleen Asana-taken mét deadline (de rest is daar backlog).
                foreach (var t in (await AsanaClient.OpenTakenAsync(asana, _cts.Token))
                    .Where(t => t.Deadline is not null))
                {
                    asanaRijen.Add(new TaakRij(t.Naam, t.Deadline, "Asana", null, t.Gid,
                        AsanaOmschrijving: t.Omschrijving));
                }
            }
        }
        catch
        {
            // Asana even niet bereikbaar: lokale taken gewoon tonen.
        }
        var rijen = new List<TaakRij>();
        foreach (var t in MijnTaakStore.Load().Taken.Where(t =>
                     !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt))
        {
            rijen.Add(new TaakRij(t.Tekst, t.Deadline, "Mijn", t, "", t.Prioriteit));
        }
        rijen.AddRange(asanaRijen);
        rijen.AddRange(VooruitblikRijen());

        if (IsDisposed)
        {
            return;
        }
        _taakRijen = rijen;
        if (rijen.Count > 0)
        {
            _gevierd = false; // er staat weer werk open: de volgende lege lijst mag opnieuw feest zijn
        }
        VulTakenLijst();

        // Rustige lijst? Dan is het een goed moment om achterstallige, ongecommitte
        // wijzigingen op te ruimen. De scan draait op de achtergrond (git in WSL is traag).
        var openTaken = rijen.Count(r => r.Bron is not ("Later" or "Snooze"));
        _ = Task.Run(async () =>
        {
            if (await GitTaken.ZorgVoorAsync(openTaken, _cts.Token) && !IsDisposed)
            {
                BeginInvoke(() => _ = VerversTakenAsync());
            }
        }, _cts.Token);
    }

    /// <summary>
    /// Blader je met ◀ ▶ naar een latere dag, dan komt daar de vooruitblik bij: taken waarvan
    /// de startdatum tegen dan bereikt is, taken die dan uit hun snooze komen, en gesnoozde
    /// berichten die dan terug in de inbox verschijnen. Op "vandaag" levert dit niets op.
    /// </summary>
    private List<TaakRij> VooruitblikRijen()
    {
        var rijen = new List<TaakRij>();
        if (_meetingsOffset <= 0)
        {
            return rijen;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var dag = vandaag.AddDays(_meetingsOffset);
        var totEindeDag = new DateTimeOffset(dag.ToDateTime(new TimeOnly(23, 59, 59)));
        // Elke rij draagt zijn éígen datum: wie naar vrijdag bladert, ziet taken die al
        // donderdag starten ook als "Start morgen"/"Start do 3 sep" — niet als vrijdag.
        string DagLabel(DateOnly d) =>
            d.DayNumber - vandaag.DayNumber == 1 ? "morgen" : d.ToString("ddd d MMM");

        foreach (var t in MijnTaakStore.Load().Taken.Where(t => !t.Klaar))
        {
            if (t.NogNietGestart && t.Startdatum is { } start && start <= dag)
            {
                rijen.Add(new TaakRij($"⏳ Start {DagLabel(start)}: {t.Tekst}", t.Deadline, "Later", t, "", t.Prioriteit));
            }
            else if (t.Gesnoozed && t.SnoozeTot is { } tot && tot <= totEindeDag)
            {
                rijen.Add(new TaakRij($"💤 Terug {DagLabel(DateOnly.FromDateTime(tot.LocalDateTime))}: {t.Tekst}",
                    t.Deadline, "Later", t, "", t.Prioriteit));
            }
        }

        // Gesnoozde mails komen uit dezelfde bron als de snooze-actie zelf.
        try
        {
            foreach (var s in SnoozeStore.LoadSnoozes()
                .Where(s => s.Tot > DateTimeOffset.Now && s.Tot <= totEindeDag))
            {
                var wat = s.Onderwerp.Length > 0 ? s.Onderwerp : "(geen onderwerp)";
                rijen.Add(new TaakRij(
                    $"📬 Mail terug {DagLabel(DateOnly.FromDateTime(s.Tot.LocalDateTime))}: {wat} — {s.Van}",
                    dag, "Snooze", null, ""));
            }
        }
        catch
        {
            // Geen snoozebestand of onleesbaar: dan gewoon zonder.
        }
        return rijen;
    }

    /// <summary>
    /// De uitstel-por: is een taak 3× of vaker vooruitgeschoven, bied dan één keer aan om
    /// hem door Claude in kleine blokjes te laten hakken — grote brokken blijven anders
    /// eindeloos doorschuiven.
    /// </summary>
    private void ToonUitstelPor()
    {
        var kandidaat = _taakRijen.Select(r => r.Lokaal).FirstOrDefault(t =>
            t is { UitstelTeller: >= 3, UitstelPorGehad: false, Klaar: false });
        if (kandidaat is null)
        {
            return;
        }
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == kandidaat.Id) is not { } taak)
        {
            return;
        }
        taak.UitstelPorGehad = true; // één por per taak, ook als hij "nee" kiest
        MijnTaakStore.Save(data);
        var id = taak.Id;
        Toast.ToonActie(this,
            $"🙈 \"{Kort(taak.Tekst, 40)}\" schuift al {taak.UitstelTeller}× vooruit",
            "In blokjes hakken", () => _ = HakTaakInBlokjesAsync(id), Fluent.Edit);
    }

    /// <summary>Laat Claude de taak opdelen in 2–4 behapbare deelstappen en vervangt hem daardoor.</summary>
    private async Task HakTaakInBlokjesAsync(Guid taakId)
    {
        var data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taakId) is not { Klaar: false } taak)
        {
            return;
        }
        Toast.Toon(this, "Claude hakt de taak in behapbare blokjes…", Fluent.Ster);
        string output;
        try
        {
            output = await ClaudeDrafter.RunClaudeAsync(
                "Deel deze taak op in 2 tot 4 kleine, concrete deelstappen van elk hooguit een " +
                "half uur werk. Antwoord UITSLUITEND met één JSON-array van korte Nederlandse " +
                "zinnen (geen nummering, geen tekst eromheen).\n\n" +
                $"Taak: {taak.Tekst}" +
                (taak.Categorie.Length > 0 ? $"\nContext/categorie: {taak.Categorie}" : ""),
                _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Blokjes hakken mislukte: {ex.Message}", Fluent.Edit);
            return;
        }
        var stappen = new List<string>();
        try
        {
            var start = output.IndexOf('[');
            var einde = output.LastIndexOf(']');
            if (start >= 0 && einde > start)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(output[start..(einde + 1)]);
                stappen = doc.RootElement.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Trim().Length > 0)
                    .Take(4)
                    .ToList();
            }
        }
        catch
        {
            // Geen bruikbare JSON: hieronder netjes melden.
        }
        if (stappen.Count < 2)
        {
            Toast.Toon(this, "Claude kreeg de taak niet zinnig opgedeeld — hij blijft zoals hij was",
                Fluent.Edit);
            return;
        }
        // Vers laden (er kan intussen afgevinkt zijn) en de grote taak vervangen door de blokjes.
        data = MijnTaakStore.Load();
        if (data.Taken.FirstOrDefault(t => t.Id == taakId) is not { Klaar: false } origineel)
        {
            return;
        }
        data.Taken.Remove(origineel);
        foreach (var stap in stappen)
        {
            data.Taken.Add(new MijnTaak
            {
                Tekst = $"🧩 {stap.Trim()}",
                Categorie = origineel.Categorie,
                Prioriteit = origineel.Prioriteit,
                Deadline = origineel.Deadline,
                Startdatum = origineel.Startdatum,
                StartUur = origineel.StartUur,
            });
        }
        MijnTaakStore.Save(data);
        Prestaties.Gebeurtenis(this, "blokjes");
        Toast.Toon(this, $"🧩 {stappen.Count} blokjes staan klaar — de grote brok is opgeruimd",
            Fluent.Check);
        await VerversTakenAsync();
    }

    private void VulTakenLijst()
    {
        var grens = _takenHorizon is { } h
            ? DateOnly.FromDateTime(DateTime.Now).AddDays(h) : (DateOnly?)null;
        _takenLaden = true;
        // In dagplan-modus krijgt elke lokale taak de positie uit de planning; wat niet in het
        // plan zit (Asana, vooruitblik) komt daarna, op deadline.
        Dictionary<Guid, int>? planVolgorde = null;
        Dictionary<Guid, DateTimeOffset>? planTijd = null;
        if (_sorteerOpPlan && DagPlan.LaadVandaag() is { } plan)
        {
            planVolgorde = new Dictionary<Guid, int>();
            planTijd = new Dictionary<Guid, DateTimeOffset>();
            var positie = 0;
            foreach (var (item, start) in DagPlan.Tijdlijn(plan))
            {
                if (item.TaakId is { } id && !planVolgorde.ContainsKey(id))
                {
                    planVolgorde[id] = positie++;
                    planTijd[id] = start;
                }
            }
        }
        // In dagplan-modus toont de middenkolom het geplande uur in plaats van de deadline.
        _taken.Columns[1].Text = planVolgorde is not null ? "Gepland" : "Deadline";
        _taken.BeginUpdate();
        _taken.Items.Clear();
        foreach (var rij in _taakRijen
            // Vooruitblik-rijen ("Start vr 7 aug", "Mail terug…") horen bij de gekozen
            // dag onder Meetings en vallen buiten het deadline-horizonfilter — anders
            // verdwijnen ze meteen weer (deadline te ver weg of geen deadline).
            // Dossierpunten (📁 Openstaand …) hebben bewust geen deadline maar horen wél
            // altijd zichtbaar te zijn: zo kloppen "Open taken" en de "▶ NU"-aanwijzer met
            // wat je in de lijst ziet (ze staan als een blok onderaan).
            .Where(r => r.Bron is "Later" or "Snooze" ||
                r.Tekst.StartsWith(DossierPunten.TaakPrefix, StringComparison.Ordinal) ||
                grens is null || (r.Deadline is { } dl && dl <= grens))
            // Een lopende storing gaat vóór alles — ongeacht de gekozen sortering.
            .OrderBy(r => r.Tekst.StartsWith(AlarmMails.TaakPrefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(r => planVolgorde is not null && r.Lokaal is { } l &&
                         planVolgorde.TryGetValue(l.Id, out var p) ? p : int.MaxValue)
            .ThenBy(r => r.Deadline ?? DateOnly.MaxValue)
            .ThenBy(r => r.Tekst, StringComparer.OrdinalIgnoreCase))
        {
            // Uitstel-detector: 3× of vaker vooruitgeschoven verdient een 🙈 in de lijst.
            var uitgesteld = rij.Lokaal is { UitstelTeller: >= 3 };
            var item = new ListViewItem { Tag = rij, UseItemStyleForSubItems = false };
            ZetTaakTekst(_taken, item, uitgesteld ? "🙈 " : "", rij.Tekst);
            if (rij.Prioriteit == 0)
            {
                item.ForeColor = Theme.Danger; // urgent
            }
            if (rij.Bron is "Later" or "Snooze")
            {
                item.ForeColor = Theme.Muted; // vooruitblik: staat er nu nog niet echt
            }
            // In dagplan-modus: het geplande uur uit de tijdlijn; anders (of niet in het plan)
            // de deadline zoals altijd.
            if (planTijd is not null && rij.Lokaal is { } pt && planTijd.TryGetValue(pt.Id, out var gepland))
            {
                var uur = item.SubItems.Add($"▶ {gepland.ToLocalTime():HH:mm}");
                uur.ForeColor = Theme.AccentHover;
            }
            else
            {
                // Deadline, met het eventuele startuur erachter ("02-08 ⏰09:00").
                var tekst = rij.Deadline?.ToString("dd-MM") ?? "";
                if (rij.Lokaal?.StartUur is { } su)
                {
                    tekst += (tekst.Length > 0 ? " " : "") + $"⏰{su:HH\\:mm}";
                }
                var deadline = item.SubItems.Add(tekst);
                if (rij.Deadline is { } d && d <= DateOnly.FromDateTime(DateTime.Now))
                {
                    deadline.ForeColor = Theme.Warn;
                }
            }
            // De bronkolom in de kleur van de klant: één blik volstaat om te zien of iets
            // van CED, Aqurat, RadiologyPartners, Urban IT of privé is. Dossierpunten krijgen
            // hun eigen "Dossier"-label (gedempt), zodat het blok onderaan herkenbaar is.
            var isDossier = rij.Tekst.StartsWith(DossierPunten.TaakPrefix, StringComparison.Ordinal);
            var bronTekst = isDossier
                ? "Dossier"
                : rij.Lokaal is { Categorie.Length: > 0 } lokaal ? lokaal.Categorie : rij.Bron;
            var bronCel = item.SubItems.Add(bronTekst);
            bronCel.ForeColor = rij.Bron is "Later" or "Snooze" || isDossier
                ? Theme.Muted
                : Theme.VoorKlant(bronTekst);
            _taken.Items.Add(item);
        }
        // Gepland: gesnoozde taken en taken met een startdatum (of startuur) in de toekomst,
        // gedempt onderaan met het moment waarop ze actief worden. Rechtsklikken werkt gewoon
        // (bewerken, ontsnoozen); afvinken kan pas als de taak echt op de lijst staat.
        if (_toonGepland)
        {
            foreach (var t in MijnTaakStore.Load().Taken
                         .Where(t => !t.Klaar && (t.Gesnoozed || t.NogNietGestart || t.NogNietAanDeBeurt))
                         .OrderBy(t => t.Startdatum ?? DateOnly.MaxValue)
                         .ThenBy(t => t.SnoozeTot ?? DateTimeOffset.MaxValue))
            {
                var rij = new TaakRij(t.Tekst, t.Deadline, "Gepland", t, "", t.Prioriteit);
                var item = new ListViewItem
                {
                    Tag = rij, UseItemStyleForSubItems = false, ForeColor = Theme.Muted,
                };
                ZetTaakTekst(_taken, item, t.Gesnoozed ? "💤 " : "⏳ ", t.Tekst);
                var wanneer = item.SubItems.Add(t switch
                {
                    { Gesnoozed: true, SnoozeTot: { } tot } => $"💤 {tot.ToLocalTime():dd-MM HH:mm}",
                    { NogNietGestart: true, Startdatum: { } start } => $"⏳ {start:dd-MM}",
                    { StartUur: { } uur } => $"⏰ {uur:HH\\:mm}",
                    _ => "",
                });
                wanneer.ForeColor = Theme.Muted;
                var cat = item.SubItems.Add(t.Categorie.Length > 0 ? t.Categorie : "Gepland");
                cat.ForeColor = Theme.Muted;
                _taken.Items.Add(item);
            }
        }
        // Recent afgevinkte taken (14 dagen) onderaan, aangevinkt en gedempt; het vinkje
        // uitzetten zet de taak terug op de open lijst.
        if (_toonAfgevinkte)
        {
            var grensKlaar = DateTimeOffset.Now.AddDays(-14);
            foreach (var t in MijnTaakStore.Load().Taken
                         .Where(t => t.Klaar && (t.KlaarOp is null || t.KlaarOp >= grensKlaar))
                         .OrderByDescending(t => t.KlaarOp ?? DateTimeOffset.MinValue)
                         .Take(40))
            {
                var rij = new TaakRij(t.Tekst, t.Deadline, "Klaar", t, "", t.Prioriteit);
                var item = new ListViewItem
                {
                    Tag = rij, UseItemStyleForSubItems = false, ForeColor = Theme.Muted,
                    Checked = true,
                };
                ZetTaakTekst(_taken, item, "", t.Tekst);
                var wanneer = item.SubItems.Add(t.KlaarOp is { } op
                    ? $"✓ {op.ToLocalTime():ddd d MMM}" : "✓");
                wanneer.ForeColor = Theme.Muted;
                var cat = item.SubItems.Add(t.Categorie.Length > 0 ? t.Categorie : "Klaar");
                cat.ForeColor = Theme.Muted;
                _taken.Items.Add(item);
            }
        }
        _taken.EndUpdate();
        _takenLaden = false;

        // De lege lijst zegt alleen wat er nú is; taken die pas later spelen laten we hier
        // bewust ongenoemd (die zie je vanzelf als hun dag nadert).
        // Alles af of alleen niets binnen het filter: andere tekst én ander silhouet.
        var takenLeeg = _taakRijen.Count == 0;
        _taken.LeegSoort = takenLeeg ? "taken" : "deadline";
        _taken.LegeTekst = takenLeeg ? ThemaStem.GeenTaken() : ThemaStem.NietsBinnenDeadline();

        ToonUitstelPor();
        WerkTakenTitelBij();
    }

    /// <summary>
    /// Titel boven de takenlijst: het aantal moet altijd kloppen met de rijen die er op dat
    /// moment écht onder staan (dus ook meteen na afvinken en na elke weergavewissel — de
    /// lijst zelf is de waarheid, niet de laatste verversing). Loopt er een streak van
    /// leeggewerkte dagen, dan hangt die er als vlammetje achter, met daarachter de
    /// eerstvolgende actie uit de dagplanning — zo zie je zonder klikken wat je nu het
    /// best doet.
    /// </summary>
    private void WerkTakenTitelBij()
    {
        var streak = Vieringen.HuidigeStreak();
        var vlam = streak >= 2 ? $"   🔥 {streak} dagen" : "";
        var nu = VolgendeUitDagPlan();
        var timer = TaakTimer.Huidig() is { } lopend
            ? $"   ⏱ {lopend.Ruw} min — {Kort(lopend.Tekst, 30)}"
            : "";
        // Afgevinkte rijen tellen niet mee als open werk.
        var openAantal = _taken.Items.Cast<ListViewItem>()
            .Count(i => i.Tag is TaakRij { Bron: not ("Klaar" or "Gepland") });
        _takenGroup.Text = $"Open taken · {openAantal}" + timer + vlam + nu;
    }

    private async Task VerversMeetingsAsync(bool forceer = true)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var dag = vandaag.AddDays(_meetingsOffset);
        // Weer hangt niet aan de agenda-config: los (fire-and-forget) verversen, ook als er
        // geen agenda gekoppeld is en vóór de vroege return hieronder.
        _ = VerversWeerAsync(dag);

        var agenda = AgendaSettings.Load();
        if (!agenda.Compleet)
        {
            return;
        }

        // Eigen + Hilke: het hele venster in één keer ophalen en cachen. Bladeren binnen dat
        // venster raakt het net niet aan; de 5-min-poll en "Nu verversen" halen wél vers op.
        if (forceer || dag > _agendaTot || _agendaGeladen == DateTimeOffset.MinValue)
        {
            try
            {
                var tot = vandaag.AddDays(Math.Max(AgendaVensterDagen, _meetingsOffset + 1));
                _agendaEigen = await AgendaClient.OphalenAsync(agenda.Urls, vandaag, tot, _cts.Token);
                if (agenda.HilkeUrls.Count > 0)
                {
                    try
                    {
                        _agendaHilke = await AgendaClient.OphalenAsync(agenda.HilkeUrls, vandaag, tot, _cts.Token);
                    }
                    catch
                    {
                        // Hilkes agenda even niet bereikbaar; eigen agenda gewoon tonen.
                    }
                }
                _agendaGeladen = DateTimeOffset.Now;
                _agendaTot = tot;
                // De CED-cache niet leegmaken: verlopen dagen halen zichzelf hieronder
                // opnieuw op, de rest blijft staan zodat bladeren snel blijft.
                RuimCedCacheOp(vandaag);
            }
            catch
            {
                if (_agendaGeladen == DateTimeOffset.MinValue)
                {
                    return; // nog nooit iets geladen én niet bereikbaar
                }
                // Anders: op de bestaande cache verder werken.
            }
        }
        var items = ItemsVoorDag(_agendaEigen, dag);
        // Identieke duplicaten binnen de eigen agenda (zelfde titel + tijdslot) één keer tonen.
        items = items
            .GroupBy(i => (Titel: i.Titel.Trim().ToLowerInvariant(), i.Start, i.Einde))
            .Select(g => g.First())
            .ToList();
        var hilkeItems = agenda.HilkeTonen
            ? ItemsVoorDag(_agendaHilke, dag)
            : new List<AgendaClient.AgendaItem>();
        // Afspraken die in beide agenda's staan (bv. de AH-levering) horen maar één keer in de
        // lijst — de eigen agenda wint, Hilkes exemplaar valt weg.
        hilkeItems = hilkeItems.Where(h => !items.Any(e => ZelfdeAfspraak(e, h))).ToList();

        var cedFout = false;
        if (OutlookClient.OoitGekoppeld)
        {
            try
            {
                // Niet wáchten op een dag die nog niet in de cache zit: dan blijft het scherm
                // seconden hangen bij elke tik op ▶ (de OWA-agenda moet dan eerst navigeren).
                // De Google-afspraken verschijnen meteen; zodra de CED-dag binnen is, tekenen
                // we opnieuw. Dat gebeurt hooguit één keer per dag, want daarna zit hij in
                // de cache.
                var cedTaak = CedVoorDagAsync(dag);
                if (!cedTaak.IsCompleted)
                {
                    var gevraagdeDag = dag;
                    _ = cedTaak.ContinueWith(t =>
                    {
                        if (t.IsFaulted || IsDisposed ||
                            DateOnly.FromDateTime(DateTime.Now).AddDays(_meetingsOffset) != gevraagdeDag)
                        {
                            return;
                        }
                        BeginInvoke(() => _ = VerversMeetingsAsync(forceer: false));
                    }, TaskScheduler.Default);
                    throw new OperationCanceledException(); // deze ronde zonder CED tonen
                }
                var ced = await cedTaak;
                // Afspraken die ook al in de eigen (Google-)agenda staan niet dubbel tonen
                // (Office 365 / CED-meetings komen zo naast de Google-agenda in de lijst).
                items.AddRange(ced
                    .Where(c => !items.Any(e => e.Start == c.Start && e.Einde == c.Einde))
                    .Select(c => c with { Titel = "CED · " + c.Titel }));
                items = items.OrderBy(i => i.Start).ThenBy(i => !i.HeleDag).ToList();
            }
            catch (OperationCanceledException)
            {
                // CED-dag wordt nog opgehaald; hij komt er zo bij.
            }
            catch
            {
                // CED/Office 365-agenda even niet leesbaar (bv. MFA verlopen): niet stil
                // inslikken, maar in de groepstitel melden zodat duidelijk is waaróm de
                // O365-meetings ontbreken (Outlook opnieuw aanmelden).
                cedFout = true;
            }
        }

        if (IsDisposed)
        {
            return;
        }
        var nu = DateTimeOffset.Now;
        // Voorbije afspraken van vandaag standaard verbergen (lopende blijven staan).
        if (_meetingsOffset == 0 && !_toonVoorbije)
        {
            items = items.Where(m => m.HeleDag || m.Einde > nu).ToList();
            hilkeItems = hilkeItems.Where(m => m.HeleDag || m.Einde > nu).ToList();
        }
        var snoozes = LaadMeetingSnoozes();
        _meetings.BeginUpdate();
        _meetings.Items.Clear();
        foreach (var m in items)
        {
            if (snoozes.Any(s => s.Sleutel == MeetingSleutel(m) && s.Tot > nu))
            {
                continue; // gesnoozde meeting verbergen tot het gekozen moment
            }
            var item = new ListViewItem(m.HeleDag
                ? "hele dag"
                : $"{m.Start.ToLocalTime():HH:mm}–{m.Einde.ToLocalTime():HH:mm}")
            {
                UseItemStyleForSubItems = false,
                Tag = m,
                Name = m.Titel.StartsWith("CED · ", StringComparison.Ordinal) ? "outlook"
                    : IsReceptTitel(m.Titel) ? "recept"
                    : "gagenda",
            };
            var titel = item.SubItems.Add(m.Titel);
            // De tijdkolom in de kleur van de agenda: blauw voor CED (Office 365), lila voor
            // de eigen Google-agenda. Zo zie je de bron zonder de titel te lezen.
            item.ForeColor = item.Name == "outlook" ? Theme.KlantCed : Theme.KlantUrbanIt;
            // Oranje: bezig of hij begint binnen het halfuur (alleen in de vandaag-weergave).
            if (!m.HeleDag && _meetingsOffset == 0 &&
                m.Start <= nu.AddMinutes(30) && m.Einde > nu)
            {
                item.ForeColor = Theme.Warn;
                titel.ForeColor = Theme.Warn;
            }
            _meetings.Items.Add(item);
        }
        // Hilkes afspraken apart onderaan, in lichter grijs.
        foreach (var m in hilkeItems)
        {
            if (snoozes.Any(s => s.Sleutel == MeetingSleutel(m) && s.Tot > nu))
            {
                continue;
            }
            var item = new ListViewItem(m.HeleDag
                ? "hele dag"
                : $"{m.Start.ToLocalTime():HH:mm}–{m.Einde.ToLocalTime():HH:mm}")
            {
                UseItemStyleForSubItems = false,
                Tag = m,
                ForeColor = Theme.Muted,
                Name = "hilke",
            };
            var titel = item.SubItems.Add($"Hilke · {m.Titel}");
            titel.ForeColor = Theme.Muted;
            _meetings.Items.Add(item);
        }
        _meetings.EndUpdate();

        // Lege agenda in de toon van het thema ("Niets op de radar vandaag").
        _meetings.LegeTekst = ThemaStem.GeenMeetings(_meetingsOffset == 0);

        // Teller + eerstvolgende meeting in de groepstitel (alleen in de vandaag-weergave).
        var basis = _meetingsOffset switch
        {
            0 => "Meetings vandaag",
            1 => "Meetings morgen",
            _ => $"Meetings {dag:dddd d MMMM}",
        };
        var volgende = _meetingsOffset == 0
            ? items.FirstOrDefault(m => !m.HeleDag && m.Start > DateTimeOffset.Now)
            : null;
        var cedMelding = cedFout ? "  —  ⚠ O365/CED-agenda niet bereikbaar (Outlook aanmelden)" : "";
        _meetingsGroup.Text = (_meetings.Items.Count == 0
            ? basis
            : $"{basis} · {_meetings.Items.Count}" +
              (volgende is not null ? $"  —  volgende om {volgende.Start.ToLocalTime():HH:mm}" : ""))
            + cedMelding;
        // De CED-dagknop staat er alleen als de getoonde dag een CED-dag (di/do) is.
        if (_cedDagKnop is not null)
        {
            _cedDagKnop.Visible = dag.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday;
        }
        WerkVolgendeMeetingBalkBij();

        ControleerVertrek(); // vertrekwaarschuwing voor afspraken met een adres
        _ = PrefetchO365DetailsAsync(); // CED-details alvast klaarzetten (cache)

        // Bij bladeren (niet bij de poll — die mag de Outlook-sessie niet bezet houden) de
        // eerstvolgende dagen alvast binnenhalen, zodat ▶ meteen iets kan tonen.
        if (!forceer)
        {
            _ = LaadVooruitAsync(dag);
        }

        // De actuele stand naar schijf, zodat de lijst bij een herstart meteen gevuld is.
        MeetingsCache.Save(_agendaEigen, _agendaHilke,
            _cedCache.Where(kv => kv.Value.Taak.IsCompletedSuccessfully)
                .Select(kv => new KeyValuePair<DateOnly, List<AgendaClient.AgendaItem>>(
                    kv.Key, kv.Value.Taak.Result)),
            _agendaTot);
    }

    /// <summary>
    /// Haalt de weersvoorspelling voor de getoonde dag op en toont die (icoon + graden) onderaan
    /// de kalender. Zonder ingesteld thuisadres of zonder verwachting blijft het label verborgen.
    /// </summary>
    private async Task VerversWeerAsync(DateOnly dag)
    {
        try
        {
            var reis = ReisSettings.Load();
            var weer = reis.HeeftThuis
                ? await Weer.VoorDagAsync(reis.ThuisLat, reis.ThuisLon, dag, _cts.Token)
                : null;
            if (IsDisposed || dag != DateOnly.FromDateTime(DateTime.Now).AddDays(_meetingsOffset))
            {
                return; // ondertussen naar een andere dag gebladerd
            }
            if (weer is null)
            {
                _weerLabel.Visible = false;
                return;
            }
            _weerLabel.Text = $"{weer.Kort}   ·   {weer.Omschrijving}" +
                (weer.ParapluNodig ? "  —  paraplu mee" : "");
            _weerLabel.Visible = true;
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch
        {
            // Weer is bijzaak.
        }
    }

    /// <summary>
    /// Toont bij het opstarten meteen de meetings uit de schijfcache (zonder netwerk), zodat
    /// de lijst nooit leeg wacht op de agenda-fetch en de CED-scrape. De gewone verversing
    /// erna haalt alles alsnog vers op en overschrijft de weergave.
    /// </summary>
    private async Task ToonMeetingsUitCacheAsync()
    {
        if (MeetingsCache.Load() is not { } cache)
        {
            return;
        }
        _agendaEigen = cache.Eigen;
        _agendaHilke = cache.Hilke;
        _agendaTot = cache.Tot;
        foreach (var (sleutel, items) in cache.Ced)
        {
            if (DateOnly.TryParse(sleutel, out var cachedag) && !_cedCache.ContainsKey(cachedag))
            {
                // Titels ook hier opschonen: oudere caches bevatten nog het volledige
                // OWA-label ("IT-meeting, , Dinsdag, 4 Augustus, 2026, …").
                var schoon = items
                    .Select(i => i with { Titel = OutlookClient.SchoonAgendaTitel(i.Titel) })
                    .ToList();
                // Als "net gestart" markeren zodat de cache-render hieronder niet gaat scrapen;
                // de eerstvolgende geforceerde verversing haalt hoe dan ook vers op.
                _cedCache[cachedag] = (DateTimeOffset.Now, Task.FromResult(schoon));
            }
        }
        _agendaGeladen = DateTimeOffset.Now; // render zonder fetch
        try
        {
            await VerversMeetingsAsync(forceer: false);
        }
        catch
        {
            // Cache-weergave is best effort.
        }
        finally
        {
            // De echte start-verversing moet wél vers ophalen.
            _agendaGeladen = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Hoe lang een opgehaalde CED-dag bruikbaar blijft. Vandaag verandert nog (afspraken
    /// worden verzet of geannuleerd), verder in de week zelden.
    /// </summary>
    private static TimeSpan CedGeldig(DateOnly dag) =>
        dag == DateOnly.FromDateTime(DateTime.Now) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);

    /// <summary>De CED-agenda van één dag, uit de cache als die nog vers genoeg is.</summary>
    private Task<List<AgendaClient.AgendaItem>> CedVoorDagAsync(DateOnly dag)
    {
        if (_cedCache.TryGetValue(dag, out var bewaard) &&
            !bewaard.Taak.IsFaulted && !bewaard.Taak.IsCanceled &&
            DateTimeOffset.Now - bewaard.Gestart < CedGeldig(dag))
        {
            return bewaard.Taak;
        }
        // Eén navigatie levert doorgaans de hele werkweek op: die andere dagen meteen mee
        // cachen, zodat bladeren met ▶ daarna niets meer hoeft op te halen.
        var gestart = DateTimeOffset.Now;
        var taak = HaalCedOpAsync(dag, gestart);
        _cedCache[dag] = (gestart, taak);
        return taak;
    }

    private async Task<List<AgendaClient.AgendaItem>> HaalCedOpAsync(DateOnly dag, DateTimeOffset gestart)
    {
        var perDag = await OutlookClient.Instance.AgendaDagenAsync(dag, _cts.Token);
        foreach (var (andere, items) in perDag)
        {
            // Een intussen nieuwer opgehaalde dag niet overschrijven met deze zijvangst.
            if (andere != dag &&
                (!_cedCache.TryGetValue(andere, out var bestaand) || bestaand.Gestart <= gestart))
            {
                _cedCache[andere] = (gestart, Task.FromResult(items));
            }
        }
        return perDag.GetValueOrDefault(dag) ?? new List<AgendaClient.AgendaItem>();
    }

    private bool _cedWarmBezig;

    /// <summary>
    /// Houdt de CED-agenda van de komende twee weken warm. Eén navigatie in OWA levert een
    /// hele week op, dus dit kost hooguit een paar navigaties — maar het scheelt dat je bij
    /// elke tik op ▶ staat te wachten. Draait op de achtergrond, na de gewone verversbeurt.
    /// </summary>
    private async Task WarmCedCacheAsync()
    {
        if (_cedWarmBezig || !OutlookClient.OoitGekoppeld)
        {
            return;
        }
        _cedWarmBezig = true;
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            for (var i = 0; i <= AgendaVensterDagen; i++)
            {
                if (IsDisposed || _cts.IsCancellationRequested)
                {
                    return;
                }
                var dag = vandaag.AddDays(i);
                // Weekends overslaan: daar staan geen CED-meetings, en elke navigatie kost tijd.
                if (dag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    continue;
                }
                if (_cedCache.ContainsKey(dag))
                {
                    continue; // deze dag (of zijn week) is al opgehaald
                }
                try
                {
                    await CedVoorDagAsync(dag);
                }
                catch
                {
                    return; // sessie niet beschikbaar: volgende ronde opnieuw
                }
            }
        }
        finally
        {
            _cedWarmBezig = false;
        }
    }

    /// <summary>Gooit voorbije dagen uit de cache; de rest blijft staan tot ze verlopen is.</summary>
    private void RuimCedCacheOp(DateOnly vandaag)
    {
        foreach (var dag in _cedCache.Keys.Where(d => d < vandaag).ToList())
        {
            _cedCache.Remove(dag);
        }
    }

    /// <summary>
    /// Haalt de CED-agenda van de dagen ná de getoonde dag stilletjes op de achtergrond op.
    /// Raakt de weergave niet aan: het enige doel is dat de volgende klik op ▶ uit de cache
    /// komt in plaats van te moeten wachten op Outlook.
    /// </summary>
    private async Task LaadVooruitAsync(DateOnly dag)
    {
        if (!OutlookClient.OoitGekoppeld)
        {
            return;
        }
        for (var i = 1; i <= VooruitLaden; i++)
        {
            if (IsDisposed || _cts.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await CedVoorDagAsync(dag.AddDays(i));
            }
            catch
            {
                return; // Outlook doet even niet mee; de gewone weg meldt dat wel.
            }
        }
    }

    /// <summary>Afspraken uit de cache die (deels) op de gevraagde dag vallen.</summary>
    private static List<AgendaClient.AgendaItem> ItemsVoorDag(
        List<AgendaClient.AgendaItem> bron, DateOnly dag)
    {
        var start = new DateTimeOffset(dag.ToDateTime(TimeOnly.MinValue));
        var eind = start.AddDays(1);
        return bron
            .Where(i => i.Start < eind && i.Einde > start)
            .OrderBy(i => i.Start).ThenBy(i => !i.HeleDag)
            .ToList();
    }

    private async Task MeetingsNaarDagAsync(int offset)
    {
        _meetingsOffset = Math.Max(0, offset);
        var dag = DateOnly.FromDateTime(DateTime.Now).AddDays(_meetingsOffset);
        _meetingsGroup.Text = _meetingsOffset switch
        {
            0 => "Meetings vandaag",
            1 => "Meetings morgen",
            _ => $"Meetings {dag:dddd d MMMM}",
        };
        await VerversMeetingsAsync(forceer: false); // uit de cache — bladeren herlaadt niets
        // De takenlijst toont voor een latere dag ook wat er dan opduikt (startdatum, snoozes).
        _taakRijen = _taakRijen.Where(r => r.Bron is not ("Later" or "Snooze")).ToList();
        _taakRijen.AddRange(VooruitblikRijen());
        VulTakenLijst();
    }

    /// <summary>Gerechtnamen uit ah-gerechten.json, voor het recept-icoon in de meetinglijst.</summary>
    private static readonly Lazy<HashSet<string>> GerechtNamen = new(() =>
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(DataDir, "ah-gerechten.json")));
            return doc.RootElement.GetProperty("gerechten").EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    });

    /// <summary>Is deze agenda-afspraak een gepland avondeten (AH-gerecht)?</summary>
    private static bool IsReceptTitel(string titel) =>
        titel.StartsWith("🍴", StringComparison.Ordinal) ||
        GerechtNamen.Value.Contains(titel.Trim());

    /// <summary>
    /// Videolink van een meeting: Teams of Google Meet, uit locatie/omschrijving
    /// (Google-agenda) of uit de opgehaalde O365-details (CED). Leeg = geen link bekend.
    /// </summary>
    private string MeetingJoinUrl(ListViewItem lvi)
    {
        if (lvi.Tag is not AgendaClient.AgendaItem m)
        {
            return "";
        }
        if (m.MeetLink.Length > 0)
        {
            return m.MeetLink;
        }
        var tekst = m.Locatie + "\n" + m.Omschrijving;
        if (lvi.Name == "outlook" && _o365Details.TryGetValue(MeetingSleutel(m), out var det))
        {
            tekst += "\n" + det;
        }
        var match = System.Text.RegularExpressions.Regex.Match(tekst,
            @"https?://teams\.(microsoft|live)\.com/\S*meetup-join\S*|" +
            @"https?://meet\.google\.com/[a-z0-9\-]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', '>', ']', ';') : "";
    }

    /// <summary>Toont de details van de geselecteerde meeting in het berichtvenster.</summary>
    private void ToonMeetingDetail()
    {
        if (_meetings.SelectedItems.Count == 0 ||
            _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem m)
        {
            return;
        }
        // Een meeting is geen bericht: het antwoordblok hoort er niet onder te blijven staan.
        BewaarDetailConcept();
        _getoond = null;
        _detailLosVanLijst = true;
        WerkAntwoordblokBij();

        var start = m.Start.ToLocalTime();
        var einde = m.Einde.ToLocalTime();
        var duur = einde - start;
        var uitOutlook = _meetings.SelectedItems[0].Name == "outlook";
        var bron = uitOutlook ? "Office 365" : "Google Agenda";

        // Alles wat de agenda meegeeft in één blok: locatie, genodigden, omschrijving en de
        // online-link. Wat leeg is, laten we weg (geen lege kopjes).
        static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        static string Rij(string kop, string waarde) =>
            $"""
             <div style="margin-top:14px">
               <div style="font-size:12px;color:#5f6368;text-transform:uppercase;letter-spacing:.4px">{kop}</div>
               <div style="font-size:14px;color:#1f1f1f;margin-top:2px;white-space:pre-wrap;word-break:break-word">{waarde}</div>
             </div>
             """;

        var extra = new System.Text.StringBuilder();
        if (m.Locatie.Trim().Length > 0)
        {
            extra.Append(Rij("Waar", Enc(m.Locatie.Trim())));
            // Echt adres: rijtijd en vertrekmoment erbij. De berekening loopt asynchroon;
            // zodra ze er is wordt dit paneel opnieuw getekend.
            if (MeetingPrep.IsEchtAdres(m.Locatie))
            {
                if (_reis.TryGetValue(MeetingSleutel(m), out var reisInfo))
                {
                    extra.Append(Rij("Onderweg", Enc(reisInfo.Tekst)));
                }
                else
                {
                    StartReisBerekening(m);
                }
            }
        }
        if (m.Genodigden.Count > 0)
        {
            extra.Append(Rij($"Wie ({m.Genodigden.Count})",
                string.Join("<br>", m.Genodigden.Select(Enc))));
        }
        if (MeetingLink(m) is { } link)
        {
            extra.Append(Rij("Online",
                $"""<a href="{Enc(link)}" style="color:#1a73e8">{Enc(link)}</a>"""));
        }
        if (m.Omschrijving.Trim().Length > 0)
        {
            extra.Append(Rij("Omschrijving",
                MailReplyForm.EncodeMetLinks(m.Omschrijving.Trim()).Replace("\n", "<br>")));
        }
        // O365/CED-afspraken hebben in de lijst alleen tijd en titel; de genodigden en de
        // omschrijving worden per afspraak uit de webagenda geplukt (en daarna gecachet).
        if (uitOutlook)
        {
            var o365Sleutel = MeetingSleutel(m);
            if (_o365Details.TryGetValue(o365Sleutel, out var o365))
            {
                extra.Append(Rij("Uit Outlook",
                    MailReplyForm.EncodeMetLinks(o365).Replace("\n", "<br>")));
            }
            else if (_o365Mislukt.TryGetValue(o365Sleutel, out var wanneer) &&
                     DateTimeOffset.Now - wanneer < TimeSpan.FromSeconds(90))
            {
                extra.Append(Rij("Uit Outlook",
                    "Details ophalen lukte net niet — selecteer de afspraak straks opnieuw " +
                    "voor een nieuwe poging."));
            }
            else
            {
                StartO365Details(m);
                extra.Append(Rij("Uit Outlook",
                    "Genodigden en omschrijving worden opgehaald… (± 15 s)"));
            }
        }
        if (extra.Length == 0)
        {
            extra.Append(Rij("Details", uitOutlook
                ? "Kon de details niet uit Outlook ophalen."
                : "Deze afspraak heeft geen locatie, genodigden of omschrijving."));
        }

        var html =
            $"""
            <!doctype html><html><head><meta charset="utf-8"></head>
            <body style="margin:0;background:{Theme.Hex(Theme.Bg)};font-family:'Segoe UI Variable Text','Segoe UI',Arial,sans-serif;padding:12px">
            <div style="background:#ffffff;border-radius:12px;padding:20px;box-shadow:0 6px 28px rgba(0,0,0,.5)">
              <div style="font-size:13px;color:#5f6368;margin-bottom:6px">📅 Meeting · {Enc(bron)}{(m.Herhalend ? " · herhalend" : "")}</div>
              <div style="font-size:19px;font-weight:600;color:#1f1f1f;margin-bottom:12px">{Enc(m.Titel)}</div>
              <div style="font-size:14px;color:#1f1f1f">{start:dddd d MMMM yyyy}</div>
              <div style="font-size:14px;color:#1f1f1f">{(m.HeleDag
                  ? "Hele dag"
                  : $"{start:HH:mm} – {einde:HH:mm} ({(int)duur.TotalMinutes} min)")}</div>
              {extra}
            </div>
            </body></html>
            """;
        if (_detail.CoreWebView2 is { } core)
        {
            core.NavigateToString(html);
        }
        else
        {
            _wachtendeWeergave = html;
        }
    }

    // ---------- O365-meetingdetails ----------

    /// <summary>
    /// Haalt op de achtergrond de genodigden en omschrijving van een CED-afspraak uit de
    /// Outlook-webagenda en hertekent het detailpaneel zodra ze er zijn. Eén keer per
    /// afspraak; ook een mislukking wordt onthouden (anders blijft hij het proberen).
    /// </summary>
    private void StartO365Details(AgendaClient.AgendaItem m)
    {
        var sleutel = MeetingSleutel(m);
        if (_o365Details.ContainsKey(sleutel) || !_o365Bezig.Add(sleutel))
        {
            return;
        }
        // Bewust géén Task.Run: de verborgen Outlook-sessie (WebView2) mag alleen vanaf de
        // UI-thread bediend worden. De awaits erin geven de UI gewoon lucht.
        _ = HaalO365DetailsAsync(m, sleutel);
    }

    private async Task HaalO365DetailsAsync(AgendaClient.AgendaItem m, string sleutel)
    {
        var details = "";
        try
        {
            var titel = m.Titel.Replace("CED · ", "", StringComparison.Ordinal).Trim();
            details = SchoonO365Details(await OutlookClient.Instance.MeetingDetailsAsync(
                DateOnly.FromDateTime(m.Start.LocalDateTime),
                m.Start.ToLocalTime().ToString("HH:mm"),
                titel.Length > 12 ? titel[..12] : titel, _cts.Token), titel);
        }
        catch
        {
            // Outlook niet aangemeld of paneel niet gevonden: cooldown hieronder.
        }
        if (IsDisposed)
        {
            return;
        }
        if (details.Length > 0)
        {
            _o365Details[sleutel] = details;
            _o365Mislukt.Remove(sleutel);
            BewaarO365DetailsCache(); // overleeft een herstart
            _meetings.Invalidate(); // join-icoontje (Teams) kan nu verschijnen
        }
        else
        {
            _o365Mislukt[sleutel] = DateTimeOffset.Now; // cooldown, daarna nieuwe poging
        }
        _o365Bezig.Remove(sleutel);
        if (_meetings.SelectedItems.Count > 0 &&
            _meetings.SelectedItems[0].Tag is AgendaClient.AgendaItem sel &&
            MeetingSleutel(sel) == sleutel)
        {
            ToonMeetingDetail();
        }
    }

    /// <summary>
    /// Destilleert uit de ruwe Outlook-paginatekst wat er echt toe doet: wie er komt
    /// (deelnemerslijst en organisator), de aanvaard-status en de omschrijving. Alle
    /// UI-ruis — losse icoontekens, banners, dubbele tijd/locatieregels, Copilot — gaat eruit.
    /// </summary>
    internal static string SchoonO365Details(string ruw, string titel)
    {
        if (ruw.Trim().Length == 0)
        {
            return "";
        }
        // Regels die alleen UI zijn (knoppen, banners, herhalingen van wat het paneel al toont).
        var ruisBevat = new[]
        {
            "Externe Mail", "Mail de l'extérieur", "Veuillez être", "contactez le support",
            "Neem bij twijfel contact", "Sommige inhoud in dit bericht is geblokkeerd",
            "Geblokkeerde inhoud weergeven", "Vertrouwelijkheid", "Voorbereiden op deze vergadering",
            "Copilot", "Geen locatie toegevoegd", "Teams-vergadering", "E-mail verzenden",
            // Chrome van de volledige (bewerk)weergave:
            "Gebeurtenisexemplaar", " - Agenda", "U bewerkt", "Reeks bewerken",
            "volgende gebeurtenissen bewerken", "Viva Insights", "Mijn sjablonen",
            "vergadering voorbereiden", "Voorbereiding voor vergadering", "Gesprekspunten",
            "Een vraag voorstellen", "Alle exemplaren", "In gesprek", "Tijdzone",
            "Facilitator is niet aanwezig", "Voor organisatoren", "Heeft u hulp nodig",
        };
        var ruisExact = new[]
        {
            "RSVP", "Ja", "Nee", "Misschien", "Bewerken", "Verwijderen", "Sluiten", "Chatten",
            "Meer opties", "Beantwoorden", "Doorsturen", "Deelnemen", "Join", "Edit", "Delete",
            "Close", "More options", "Reply", "Forward", "Tentative", "Accept", "Decline",
            "Agenda", "Bijhouden", "Planner", "Geaccepteerd", "Meeting", "Vergadering",
            "Outlook", "Annuleren", "Opslaan", "Verzenden", "Bezet", "Vrij",
        };
        var regels = new List<string>();
        string? wie = null;
        string? organisator = null;
        string? status = null;
        foreach (var raw in ruw.ReplaceLineEndings("\n").Split('\n'))
        {
            var r = raw.Trim();
            // Icoontekens (private-use unicode), avatar-initialen en lege regels overslaan.
            if (r.Length <= 2 || r.All(c => c is (>= '\uE000' and <= '\uF8FF') or ' '))
            {
                continue;
            }
            if (r.Equals(titel, StringComparison.OrdinalIgnoreCase) ||
                ruisExact.Contains(r, StringComparer.OrdinalIgnoreCase) ||
                ruisBevat.Any(x => r.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            // Tijd/datumregels ("Di 4-8-2026 15:00 - 16:00"): staan al boven in het paneel.
            if (System.Text.RegularExpressions.Regex.IsMatch(r, @"\d{1,2}:\d{2}\s*[-–]\s*\d{1,2}:\d{2}") ||
                System.Text.RegularExpressions.Regex.IsMatch(r, @"^\w{2,9}\s\d{1,2}[-/ ]\d{1,2}[-/ ]\d{4}"))
            {
                continue;
            }
            // De deelnemersregel: namen gescheiden door puntkomma's ("LONNOY Michael; …").
            if (wie is null && r.Contains(';') && r.Split(';').Length >= 2 &&
                !r.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                wie = r.TrimEnd(';').Trim();
                continue;
            }
            if (r.Contains("heeft u uitgenodigd", StringComparison.OrdinalIgnoreCase))
            {
                organisator = r.Replace("heeft u uitgenodigd", "", StringComparison.OrdinalIgnoreCase)
                    .Trim(' ', '.', ',');
                continue;
            }
            if (r.Contains("geaccepteerd", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Niet geantwoord", StringComparison.OrdinalIgnoreCase))
            {
                status = r;
                continue;
            }
            if (regels.Count == 0 || regels[^1] != r)
            {
                regels.Add(r); // opeenvolgende duplicaten overslaan
            }
        }

        // In de volledige weergave staan de genodigden als losse naamregels onder elkaar:
        // die bundelen we tot één nette wie-regel.
        static bool LijktNaam(string r)
        {
            var woorden = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (woorden.Length is < 2 or > 4 || r.Any(char.IsDigit))
            {
                return false;
            }
            var tussen = new[] { "van", "de", "der", "den", "ter", "te", "het" };
            return woorden.All(w =>
                       tussen.Contains(w, StringComparer.Ordinal) ||
                       (char.IsUpper(w[0]) && w.All(c => char.IsLetter(c) || c is '\'' or '-' or '.'))) &&
                   woorden.Count(w => char.IsUpper(w[0])) >= 2;
        }
        var namen = regels.Where(LijktNaam).Distinct().Take(15).ToList();
        if (namen.Count >= 2)
        {
            wie ??= string.Join("; ", namen);
            regels.RemoveAll(namen.Contains);
        }

        var uit = new List<string>();
        if (wie is not null)
        {
            uit.Add($"👥 {wie}");
        }
        if (organisator is not null)
        {
            uit.Add($"Organisator: {organisator}");
        }
        if (status is not null)
        {
            uit.Add(status);
        }
        if (regels.Count > 0)
        {
            if (uit.Count > 0)
            {
                uit.Add("");
            }
            uit.AddRange(regels);
        }
        var tekst = string.Join("\n", uit).Trim();
        return tekst.Length > 1500 ? tekst[..1500] + "…" : tekst;
    }

    // ---------- Reistijd ----------

    /// <summary>
    /// Berekent op de achtergrond de rijtijd van thuis naar deze afspraak (geocode + route,
    /// met verkeer) en hertekent het detailpaneel als de afspraak nog geselecteerd staat.
    /// Eén berekening per afspraak; het resultaat blijft de sessie lang bewaard.
    /// </summary>
    private void StartReisBerekening(AgendaClient.AgendaItem m)
    {
        var sleutel = MeetingSleutel(m);
        if (_reis.ContainsKey(sleutel) || !_reisBezig.Add(sleutel))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var reis = ReisSettings.Load();
                if (!reis.Aan || !reis.HeeftThuis)
                {
                    return;
                }
                var naar = await Reistijd.GeocodeAsync(m.Locatie, _cts.Token);
                if (naar is null)
                {
                    return;
                }
                var route = await Reistijd.BerekenAsync(
                    new Reistijd.Punt(reis.ThuisLat, reis.ThuisLon), naar, _cts.Token);
                if (route is null || IsDisposed)
                {
                    return;
                }
                var vertrek = m.Start - route.Duur - TimeSpan.FromMinutes(reis.BufferMinuten);
                var tekst = $"🚗 {route.Duur.TotalMinutes:0} min rijden ({route.Kilometer:0.#} km)" +
                            (route.FileOpDeWeg ? $" · {route.Vertraging.TotalMinutes:0} min file" : "") +
                            $" — vertrek om {vertrek.ToLocalTime():HH:mm}";
                BeginInvoke(() =>
                {
                    _reis[sleutel] = (tekst, route.Duur);
                    if (_meetings.SelectedItems.Count > 0 &&
                        _meetings.SelectedItems[0].Tag is AgendaClient.AgendaItem sel &&
                        MeetingSleutel(sel) == sleutel)
                    {
                        ToonMeetingDetail(); // het paneel staat nog op deze afspraak: bijtekenen
                    }
                });
            }
            catch
            {
                // Geen route te vinden: dan gewoon geen reisregel.
            }
            finally
            {
                _reisBezig.Remove(sleutel);
            }
        }, _cts.Token);
    }

    /// <summary>
    /// Waarschuwt (één keer per afspraak) wanneer het tijd wordt om te vertrekken: rijtijd +
    /// buffer vóór de start, met de marge uit de reisinstellingen. Draait mee met elke
    /// meetings-verversing, dus ruim vaak genoeg voor een venster van een kwartier.
    /// </summary>
    private void ControleerVertrek()
    {
        var reis = ReisSettings.Load();
        if (!reis.Aan || !reis.HeeftThuis || _meetingsOffset != 0)
        {
            return;
        }
        var nu = DateTimeOffset.Now;
        foreach (var m in HuidigeMeetings().Where(m =>
                     !m.HeleDag && m.Start > nu && m.Start - nu < TimeSpan.FromHours(4) &&
                     MeetingPrep.IsEchtAdres(m.Locatie)))
        {
            var sleutel = MeetingSleutel(m);
            if (!_reis.TryGetValue(sleutel, out var info))
            {
                StartReisBerekening(m); // volgende ronde is de rijtijd er wel
                continue;
            }
            if (info.Duur.TotalMinutes < reis.MinimumRijMinuten)
            {
                continue; // om de hoek: geen melding waard
            }
            var vertrek = m.Start - info.Duur - TimeSpan.FromMinutes(reis.BufferMinuten);
            if (nu >= vertrek.AddMinutes(-reis.WaarschuwMinuten) && nu <= vertrek.AddMinutes(2) &&
                _vertrekGemeld.Add(sleutel))
            {
                var minuten = Math.Max(0, (int)(vertrek - nu).TotalMinutes);
                Toast.Toon(this, minuten == 0
                    ? $"🚗 Nu vertrekken naar {Kort(m.Titel, 40)} ({info.Duur.TotalMinutes:0} min rijden)"
                    : $"🚗 Over {minuten} min vertrekken naar {Kort(m.Titel, 40)} " +
                      $"({info.Duur.TotalMinutes:0} min rijden)", Fluent.Kalender);
            }
        }
    }

    // ---------- Meeting-snoozes (lokaal; alleen voor de cockpitweergave) ----------

    private static readonly string MeetingSnoozeFile = Path.Combine(DataDir, "meeting-snoozes.json");

    private sealed class MeetingSnooze
    {
        public string Sleutel { get; set; } = "";
        public DateTimeOffset Tot { get; set; }
    }

    private static string MeetingSleutel(AgendaClient.AgendaItem m) => $"{m.Titel}|{m.Start:O}";

    private static List<MeetingSnooze> LaadMeetingSnoozes()
    {
        try
        {
            if (File.Exists(MeetingSnoozeFile) &&
                System.Text.Json.JsonSerializer.Deserialize<List<MeetingSnooze>>(
                    File.ReadAllText(MeetingSnoozeFile)) is { } lijst)
            {
                return lijst;
            }
        }
        catch
        {
            // Onleesbaar: zonder snoozes verder.
        }
        return new List<MeetingSnooze>();
    }

    private static void BewaarMeetingSnoozes(List<MeetingSnooze> snoozes)
    {
        snoozes.RemoveAll(s => s.Tot <= DateTimeOffset.Now); // verlopen snoozes opruimen
        File.WriteAllText(MeetingSnoozeFile,
            System.Text.Json.JsonSerializer.Serialize(snoozes,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task SnoozeMeetingAsync(DateTimeOffset? preset = null)
    {
        if (_meetings.SelectedItems.Count == 0 ||
            _meetings.SelectedItems[0].Tag is not AgendaClient.AgendaItem meeting)
        {
            return;
        }
        DateTimeOffset gekozen;
        if (preset is { } p)
        {
            gekozen = p;
        }
        else
        {
            using var dialog = new SnoozeForm(1, DateTimeOffset.Now.AddHours(1));
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            gekozen = dialog.Gekozen;
        }
        var snoozes = LaadMeetingSnoozes();
        snoozes.RemoveAll(s => s.Sleutel == MeetingSleutel(meeting));
        snoozes.Add(new MeetingSnooze { Sleutel = MeetingSleutel(meeting), Tot = gekozen });
        BewaarMeetingSnoozes(snoozes);
        await VerversMeetingsAsync(forceer: false); // alleen herfilteren, niet herladen
        // Zelfde undo-patroon als bij berichten en taken: één klik en hij staat er weer.
        var sleutel = MeetingSleutel(meeting);
        Toast.ToonUndo(this, $"Meeting gesnoozed tot {gekozen:ddd d MMM HH:mm}", () =>
        {
            var terug = LaadMeetingSnoozes();
            terug.RemoveAll(s => s.Sleutel == sleutel);
            BewaarMeetingSnoozes(terug);
            _ = VerversMeetingsAsync(forceer: false);
        }, Fluent.Klok);
    }

    // ---------- Taken afvinken ----------

    private async Task VinkTaakAfAsync()
    {
        if (_taken.SelectedItems.Count == 0 || _taken.SelectedItems[0].Tag is not TaakRij rij)
        {
            return;
        }
        await VinkRijAfAsync(_taken.SelectedItems[0], rij);
    }

    /// <summary>
    /// Vinkt één taakrij af — via het contextmenu of rechtstreeks via de checkbox op de rij.
    /// Liep er een timer op deze taak, dan wordt die meteen gestopt en geboekt.
    /// </summary>
    private async Task VinkRijAfAsync(ListViewItem item, TaakRij rij)
    {
        // Vooruitblik-rijen ("Mail terug…") horen niet afvinkbaar te zijn: er is niets om
        // klaar te zetten, dus de rij stilletjes weghalen zou liegen.
        if (rij.Lokaal is null && rij.AsanaGid.Length == 0)
        {
            Toast.Toon(this, "Deze rij is een vooruitblik — hier valt niets af te vinken", Fluent.Checkbox);
            return;
        }
        // De rij meteen uit de lijst: het resultaat moet direct zichtbaar zijn, ook als het
        // boeken van de timer of de Asana-call hierna even duurt (trage wifi). Gaat er iets
        // mis, dan zet de ververs in de catch de echte staat terug. Ook uit _taakRijen, want
        // elke tussentijdse VulTakenLijst() (dagplan-update, "▶ NU:"-klok) bouwt de lijst
        // daaruit opnieuw op en zou de net afgevinkte rij meteen terugzetten.
        item.Remove();
        _taakRijen.Remove(rij);
        // De titel telt de rijen in de lijst: meteen mee laten zakken, niet pas bij de
        // volgende vijfminutenverversing ("Open taken · 27" boven een veel korter lijstje).
        WerkTakenTitelBij();
        try
        {
            // Zelfkennis en prijzenkast: klantsprong registreren + prestatiecheck.
            ContextSwitch.Registreer(rij.Lokaal?.Categorie is { Length: > 0 } cat
                ? cat : (rij.Bron == "Asana" ? "Aqurat" : null));
            Prestaties.Gebeurtenis(this, "taak-af", rij.Tekst);
            // Timer op deze taak? Stoppen en boeken — zo eindigt de tijd op het echte moment.
            if (TaakTimer.Huidig() is { } timer &&
                ((rij.Lokaal is { } lt && timer.TaakId == lt.Id) ||
                 (rij.AsanaGid.Length > 0 && timer.AsanaGid == rij.AsanaGid)))
            {
                await StopTimerEnBoekAsync();
            }
            if (rij.Lokaal is { } lokaal)
            {
                var data = MijnTaakStore.Load();
                var taak = data.Taken.FirstOrDefault(t => t.Id == lokaal.Id);
                if (taak is not null)
                {
                    taak.Klaar = true;
                    taak.KlaarOp = DateTimeOffset.Now;
                    MijnTaakStore.Save(data);
                }
                // Undo: het afvinken meteen ongedaan kunnen maken (lokale taken).
                Toast.ToonUndo(this, $"Afgevinkt: {Kort(rij.Tekst, 40)}", () =>
                {
                    var terug = MijnTaakStore.Load();
                    if (terug.Taken.FirstOrDefault(t => t.Id == lokaal.Id) is { } t)
                    {
                        t.Klaar = false;
                        t.KlaarOp = null;
                        MijnTaakStore.Save(terug);
                    }
                    _ = VerversTakenAsync();
                }, Fluent.Check);
            }
            else if (rij.AsanaGid.Length > 0)
            {
                await AsanaClient.VoltooiAsync(AsanaSettings.Load(), rij.AsanaGid, _cts.Token);
                Toast.Toon(this, $"Afgevinkt: {Kort(rij.Tekst, 40)}", Fluent.Check);
            }
            VierAlsLijstLeeg();
            ToonBadge();
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Afvinken mislukt: {ex.Message}", Fluent.Checkbox);
            // De rij was al optimistisch weggehaald: de lijst verversen zet de echte staat terug.
            _ = VerversTakenAsync();
        }
    }

    /// <summary>
    /// Kleine prestaties belonen: vijf taken op een dag, de eerste vóór negenen, nog bezig na
    /// tienen 's avonds. Elke badge hooguit één keer per dag, en nooit tegelijk met de confetti
    /// van een lege lijst (dat feestje is groter).
    /// </summary>
    private void ToonBadge()
    {
        if (_taken.Items.Count == 0)
        {
            return;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var afgevinkt = MijnTaakStore.Load().Taken
            .Count(t => t.Klaar && t.KlaarOp is { } op && DateOnly.FromDateTime(op.LocalDateTime.Date) == vandaag);
        if (Sfeer.BadgeVoorAfvinken(afgevinkt) is { } badge)
        {
            Toast.Toon(this, badge, Fluent.Ster);
        }
    }

    /// <summary>
    /// Eén begroeting bij het openen van de cockpit: dagdeel of speciale dag, met wat er op
    /// stapel staat. Op een echte feestdag (verjaardag, nieuwjaar) mag er confetti bij.
    /// </summary>
    private async void BegroetMaarten()
    {
        var openTaken = MijnTaakStore.OpenAantal();
        var meetings = _meetings.Items.Count;
        // De begroeting krijgt de toon van het gekozen kleurenschema mee ("Goedemorgen,
        // 007. M verwacht je rapport"), gevolgd door de gewone stand van zaken.
        var begroeting = $"{ThemaStem.Dagdeel()}  ·  {Sfeer.Begroeting(openTaken, meetings)}";
        // 's Ochtends hoort er een drankje bij, gekozen op het weer van vandaag (Open-Meteo);
        // lukt dat niet binnen een paar seconden, dan gewoon de kale begroeting.
        try
        {
            if (await WeerDrankje.VoorstelAsync(_cts.Token) is { } drankje)
            {
                begroeting += $"  ·  {drankje}";
            }
        }
        catch
        {
            // Geen weer, geen drama.
        }
        if (IsDisposed)
        {
            return;
        }
        Toast.Toon(this, begroeting, Fluent.Ster);
        if (Sfeer.FeestDag())
        {
            Confetti.Vier(this);
        }
    }

    // ---------- Easter egg ----------

    private static readonly Keys[] Konami =
    {
        Keys.Up, Keys.Up, Keys.Down, Keys.Down,
        Keys.Left, Keys.Right, Keys.Left, Keys.Right, Keys.B, Keys.A,
    };

    private int _konamiStand;

    /// <summary>
    /// De klassieke cheatcode (↑↑↓↓←→←→ B A) ergens in de cockpit ingetikt geeft confetti en
    /// je streakrecord. Verder volstrekt nutteloos, en dat is precies de bedoeling.
    /// </summary>
    private void LuisterNaarKonami(Keys toets)
    {
        _konamiStand = toets == Konami[_konamiStand] ? _konamiStand + 1 : (toets == Konami[0] ? 1 : 0);
        if (_konamiStand < Konami.Length)
        {
            return;
        }
        _konamiStand = 0;
        Confetti.Vier(this);
        Prestaties.Gebeurtenis(this, "konami");
        var record = Vieringen.Record();
        Toast.Toon(this, record > 0
            ? $"🎮 Cheatcode geactiveerd — geen extra levens, wel je record: {record} dagen op rij leeg"
            : "🎮 Cheatcode geactiveerd — helaas, taken afvinken moet je nog steeds zelf",
            Fluent.Ster);
    }

    /// <summary>
    /// Laatste taak weg? Dan mag dat gezien worden: confetti over de cockpit, een wisselende
    /// felicitatie en de streak van dagen die je zo afsloot. Eén keer per lege lijst.
    /// </summary>
    private void VierAlsLijstLeeg()
    {
        if (_taken.Items.Count > 0 || _gevierd)
        {
            return;
        }
        _gevierd = true;
        Confetti.Vier(this);
        // Even later, zodat de undo-toast van het afvinken niet meteen overschreven wordt.
        var timer = new System.Windows.Forms.Timer { Interval = 900 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!IsDisposed)
            {
                Toast.Toon(this, Vieringen.VierLegeLijst(), Fluent.Ster);
                VulTakenLijst(); // streak meteen in de groepstitel
            }
        };
        timer.Start();
    }

    private static string Kort(string tekst, int max) =>
        tekst.Length <= max ? tekst : tekst[..max] + "…";
}
