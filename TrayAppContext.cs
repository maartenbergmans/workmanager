using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WorkManager;

public class TrayAppContext : ApplicationContext
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "active-contexts.json");
    private static readonly string LogFile = Path.Combine(DataDir, "switch-log.jsonl");
    private static readonly string ReminderFile = Path.Combine(DataDir, "invoice-reminder.json");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "WorkManager";

    private static readonly (string Name, Color Color)[] Clients =
    {
        ("CED", Theme.KlantCed),
        ("Aqurat", Theme.KlantAqurat),
        ("RadiologyPartners", Theme.KlantRadiology),
    };

    /// <summary>De klantcontexten (naam + kleur), o.a. voor de knoppen in de cockpit.</summary>
    public static IReadOnlyList<(string Name, Color Color)> KlantContexten => Clients;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _snoozeTimer;
    private IntPtr _iconHandle = IntPtr.Zero;
    private readonly HashSet<string> _active;
    private InvoiceApprovalForm? _invoiceForm;
    private MailReplyForm? _mailForm;
    private TeamTasksForm? _tasksForm;
    private MijnTakenForm? _mijnTakenForm;
    private BriefingForm? _briefingForm;
    private FollowUpForm? _followUpForm;
    private bool _snoozeBusy;
    private bool _reminderShowing;
    private DateOnly _takenHerinnerd; // laatste dag waarop de taken-melding getoond is
    private DateOnly _briefingGemeld; // laatste dag waarop de dagstart-melding getoond is
    private DateOnly _nachtOnderhoud; // laatste dag waarop de sessies vers gemarkeerd zijn
    private DateOnly _backupGedaan; // laatste dag met een geslaagde WorkManager-backup
    private DateOnly _ochtendWarmup; // laatste dag met het ochtendritueel (warmup + MFA-check)
    private bool _herstartNodig; // geheugendrempel overschreden → vannacht de app herstarten
    private readonly CancellationTokenSource _voiceCts = new();
    private VoiceSync? _voiceSync;
    private AhWebSync? _ahWebSync;
    private WmWebSync? _wmWebSync;
    private DubbelCtrlHook? _dubbelCtrl;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>
    /// Maximaliseert het venster en dwingt het naar de voorgrond. Windows weigert normaal
    /// focus "stelen" door een proces zonder recente invoer; de Alt-tik-truc (een loze
    /// keybd_event) geeft die toestemming, en de TopMost-wissel legt hem zeker bovenop.
    /// </summary>
    private static void NaarVoorgrond(Form venster)
    {
        venster.WindowState = FormWindowState.Maximized;
        venster.Show();
        const byte vkMenu = 0x12; // Alt
        const uint keyUp = 0x0002;
        keybd_event(vkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(vkMenu, 0, keyUp, UIntPtr.Zero);
        SetForegroundWindow(venster.Handle);
        venster.Activate();
        venster.TopMost = true;
        venster.TopMost = false;
    }

    public TrayAppContext()
    {
        Directory.CreateDirectory(DataDir);
        _active = LoadState();

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _trayIcon.MouseUp += OnTrayMouseUp;

        UpdateIcon();
        EnsureStateFile();
        PromoteTrayIcon();

        // Globale sneltoets: twee keer kort op Ctrl tikken haalt de cockpit naar voren,
        // vanuit eender welk programma.
        _dubbelCtrl = new DubbelCtrlHook();
        _dubbelCtrl.Getikt += OpenCockpit;

        // Claude Code-meldingen: de Notification-hook zet signalen in een spoolmap; hier
        // begint de bewaking die daar klikbare meldingen van maakt (klik = terminal naar
        // de voorgrond).
        ClaudeAandacht.Start();

        // Het trayicoon is met de themakleuren getekend: bij een ander kleurenschema
        // opnieuw tekenen, anders blijft het oude accent in de systeembalk staan.
        Theme.ThemaGewijzigd += UpdateIcon;

        // Klantlogo's alvast binnenhalen (eenmalig, op de achtergrond), zodat het
        // Projecten-menu ze meteen toont in plaats van pas na de tweede keer openen.
        foreach (var klant in KlantLogo.Websites.Keys.ToList())
        {
            KlantLogo.Voor(klant);
        }

        // Claude-verbruik bewaken: elk uur de limieten peilen; komt er één boven de 50%
        // (of 80/95), dan één tray-melding per limiet per venster.
        var usageTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        usageTimer.Tick += async (_, _) => await CheckClaudeUsageAsync();
        usageTimer.Start();
        var usageStart = new System.Windows.Forms.Timer { Interval = 3 * 60 * 1000 };
        usageStart.Tick += async (_, _) =>
        {
            usageStart.Stop();
            usageStart.Dispose();
            await CheckClaudeUsageAsync();
        };
        usageStart.Start();

        // Gesnoozde mails terug in de inbox zetten zodra hun tijd verstreken is —
        // ook als het mailvenster niet open staat.
        _snoozeTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _snoozeTimer.Tick += async (_, _) => await CheckSnoozesAsync();
        _snoozeTimer.Start();
        var snoozeStart = new System.Windows.Forms.Timer { Interval = 15000 };
        snoozeStart.Tick += async (_, _) =>
        {
            snoozeStart.Stop();
            snoozeStart.Dispose();
            await CheckSnoozesAsync();
        };
        snoozeStart.Start();

        // Bij een allereerste start maakt Windows de NotifyIconSettings-sleutel soms pas na enkele
        // seconden aan; probeer het dan nog één keer.
        var retry = new System.Windows.Forms.Timer { Interval = 3000 };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            retry.Dispose();
            PromoteTrayIcon();
        };
        retry.Start();

        // Wekelijkse herinnering factuurgoedkeuring + dagelijkse taken-herinnering:
        // eerste check kort na de start (zodat de opstart niet geblokkeerd wordt),
        // daarna elke 10 minuten.
        var reminder = new System.Windows.Forms.Timer { Interval = 5000 };
        reminder.Tick += (_, _) =>
        {
            reminder.Interval = 10 * 60 * 1000;
            WerkAanmeldBadgeBij(); // oranje stip zolang Teams/Outlook op aanmelding wacht
            VasteTaken.ZorgVoorWeektaken(); // wo: facturen goedkeuren, vr: weekmail team
            AfvalTaken.ZorgVoorReminder(); // zo: afvalbakken buitenzetten (ophaling maandag)
            TeamVakantieCheck.ProbeerOpDonderdag(); // do: teamvakanties op de achtergrond ophalen
            DownloadsCleaner.ZorgVoorMaandelijks(); // 1×/maand Downloads > 1 week naar prullenbak
            _ = UpdateCheck.ZorgVoorAsync(CancellationToken.None); // PhpStorm/Claude-updates als taak
            _ = CheckAhBonusAsync(); // 1×/week: vaste AH-producten in de Bonus melden
            Verjaardagen.ZorgVoorTaken(); // cadeau bedenken/kopen + feliciteren (eigen lijst)
            _ = VerjaardagRadar.ZorgVoorAsync(CancellationToken.None); // 🎂-taak uit de agenda
            _ = PresentatieTaken.ZorgVoorTaakAsync(CancellationToken.None); // wo: Aqurat-presentatie bij vrijdagmeeting
            _ = MeetingPrep.ZorgVoorAsync(CancellationToken.None); // afspraak voorbereiden + vertrektijd bewaken
            CheckMijnTakenHerinnering();
            _ = CheckDagBriefingAsync();
            _ = FollowUpRadar.ZorgVoorMeldingAsync(CancellationToken.None); // wie wacht er op antwoord
            _ = OnbeantwoordRadar.ZorgVoorTakenAsync(CancellationToken.None); // ma: vragen die bij mij blijven liggen
            DossierPunten.ZorgVoorTaken(); // ma: openstaande punten uit de klantdossiers
            CheckNachtOnderhoud();
            CheckBackup();
            CheckGeheugen();
            CheckOchtendStart();
        };
        reminder.Start();
        LogWebViewVersie();

        // Meldingen van de reisassistent en de meetingvoorbereiding in de tray tonen.
        // Alle tray-meldingen lopen via TrayMelding (stil venster): de Windows-ballonnen
        // spelen altijd het systeemgeluid en dat wil Maarten niet.
        MeetingPrep.Melding += (titel, tekst, opentPrep) =>
        {
            if (_trayIcon.Visible)
            {
                TrayMelding.Toon(titel, tekst,
                    opentPrep ? OpenBriefing : null, opentPrep ? 8000 : 15000);
            }
        };

        // Mails waar nog niemand op geantwoord heeft.
        FollowUpRadar.Melding += (titel, tekst) =>
        {
            if (_trayIcon.Visible)
            {
                TrayMelding.Toon(titel, tekst, OpenFollowUp);
            }
        };

        // Berichten van iemand uit de VIP-lijst.
        VipLijst.Melding += (titel, tekst) =>
        {
            if (_trayIcon.Visible)
            {
                TrayMelding.Toon(titel, tekst, OpenCockpit);
            }
        };

        // Spraakcommando's uit de auto: de wachtrij op de hosting pollen en via Claude
        // verwerken (doet niets zolang voice-settings.json niet compleet is).
        _voiceSync = new VoiceSync(_voiceCts.Token);
        _voiceSync.TakenToegevoegd += melding =>
            TrayMelding.Toon("Taken via spraak toegevoegd", melding, OpenMijnTaken, 5000);
        var voiceTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(5, VoiceSettings.Load().PollSeconden) * 1000,
        };
        voiceTimer.Tick += async (_, _) => await _voiceSync.PollAsync();
        voiceTimer.Start();

        // AH-bestellingen van de gsm-pagina: wachtrij op de hosting pollen en het mandje
        // vullen (doet niets zolang ah-web-settings.json niet compleet is). Op de UI-thread,
        // want een binnengekomen bestelling opent het winkelvenster (WebView2).
        _ahWebSync = new AhWebSync();
        _ahWebSync.BestellingOntvangen += melding =>
            TrayMelding.Toon("AH-bestelling van de gsm", melding, duurMs: 8000);
        _ahWebSync.GerechtToegevoegd += naam =>
            TrayMelding.Toon("Nieuw AH-gerecht van de gsm",
                $"\"{naam}\" — nakijktaak staat in Mijn taken", OpenMijnTaken, 8000);
        var ahWebTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        ahWebTimer.Tick += async (_, _) => await _ahWebSync.PollAsync();
        ahWebTimer.Start();

        // Persoonlijke webversie (wm.php): snapshot omhoog en de acties van de gsm uitvoeren.
        // Doet niets zolang wm-web-settings.json niet compleet is.
        _wmWebSync = new WmWebSync();
        _wmWebSync.ActieVerwerkt += melding =>
            TrayMelding.Toon("Vanaf de webversie", melding, OpenMijnTaken, 6000);
        var wmWebTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        wmWebTimer.Tick += async (_, _) => await _wmWebSync.PollAsync();
        wmWebTimer.Start();

        // Algemene activiteitenlog: elke minuut het voorgrondvenster bijschrijven — het
        // bronmateriaal voor het timesheet-dagvoorstel in de cockpit.
        var activiteitenTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        activiteitenTimer.Tick += (_, _) => ActiviteitenLog.Noteer();
        activiteitenTimer.Start();
        ActiviteitenLog.Noteer();

        // Bij het starten meteen de cockpit tonen: dat is het startpunt van de dag.
        OpenCockpit();
    }

    /// <summary>Wekelijkse AH-bonusradar: balloon als vaste producten in de Bonus staan.</summary>
    private async Task CheckAhBonusAsync()
    {
        try
        {
            if (await AhBonusRadar.CheckWekelijksAsync(CancellationToken.None) is { } melding &&
                _trayIcon.Visible)
            {
                TrayMelding.Toon("AH-Bonus deze week", melding, duurMs: 10000);
            }
        }
        catch
        {
            // Best effort; volgende week opnieuw.
        }
    }

    // De vroegere woensdag-popup ("wil je de facturen goedkeuren?") is bewust weg: de
    // vaste woensdagtaak in de takenlijst (VasteTaken) is de enige herinnering.

    /// <summary>
    /// Markeert het icoon in Windows 11 als "altijd zichtbaar" op de taakbalk (i.p.v. het overloopmenu).
    /// </summary>
    private static void PromoteTrayIcon()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null)
            {
                return;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = Registry.CurrentUser.OpenSubKey(@$"Control Panel\NotifyIconSettings\{name}", writable: true);
                if (sub?.GetValue("ExecutablePath") is string exe &&
                    string.Equals(exe, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch
        {
            // Sleutel bestaat niet op oudere Windows-versies; icoon blijft dan in het overloopmenu.
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        Theme.Style(menu);

        var cockpit = new ToolStripMenuItem("Cockpit…");
        cockpit.Click += (_, _) => OpenCockpit();
        menu.Items.Add(cockpit);

        var dagstart = new ToolStripMenuItem("Dagstart…");
        dagstart.Click += (_, _) => OpenBriefing();
        menu.Items.Add(dagstart);

        menu.Items.Add(new ToolStripSeparator());

        foreach (var (name, color) in Clients)
        {
            var item = new ToolStripMenuItem(name) { Tag = name, Image = MaakKleurStip(color) };
            item.Click += (_, _) => ToggleClient(name);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        var mijnTaken = new ToolStripMenuItem("Mijn taken…") { Tag = "mijntaken" };
        mijnTaken.Click += (_, _) => OpenMijnTaken();
        menu.Items.Add(mijnTaken);

        var tasks = new ToolStripMenuItem("Taken team…");
        tasks.Click += (_, _) => OpenTeamTasks();
        menu.Items.Add(tasks);

        var cleaner = new ToolStripMenuItem("Bureaublad opruimen…");
        cleaner.Click += (_, _) =>
        {
            var form = new BureaubladCleanerForm();
            // Bij het sluiten meteen de opruimtaak bijwerken: het aantal in de tekst telt af,
            // en is het bureaublad opgeruimd dan vinkt de taak zichzelf af.
            form.FormClosed += (_, _) => VasteTaken.WerkBureaubladTaakBij();
            form.Show();
        };
        menu.Items.Add(cleaner);

        var invoices = new ToolStripMenuItem("Facturen goedkeuren (ISPnext)…");
        invoices.Click += (_, _) => OpenInvoiceApproval();
        menu.Items.Add(invoices);

        var topdesk = new ToolStripMenuItem("TopDesk-tickets (CED)…");
        topdesk.Click += (_, _) => OpenTopdesk();
        menu.Items.Add(topdesk);

        var devops = new ToolStripMenuItem("Azure DevOps (CAREX)…");
        devops.Click += (_, _) => OpenDevOps();
        menu.Items.Add(devops);

        var verlof = new ToolStripMenuItem("Verlof goedkeuren (SD Worx)…");
        verlof.Click += (_, _) => OpenSdWorxPortaal();
        menu.Items.Add(verlof);

        // Vast startpunt voor de boodschappen: de cockpit-taak staat er niet elke dag.
        var ah = new ToolStripMenuItem("AH-bestelling…");
        ah.Click += (_, _) =>
        {
            using var bestel = new AhBestelForm();
            bestel.ShowDialog();
        };
        menu.Items.Add(ah);

        var mail = new ToolStripMenuItem("Mail beantwoorden (Gmail)…");
        mail.Click += (_, _) => OpenMailReply();
        menu.Items.Add(mail);

        var followUp = new ToolStripMenuItem("Wacht op antwoord…") { Tag = "followup" };
        followUp.Click += (_, _) => OpenFollowUp();
        menu.Items.Add(followUp);

        var vip = new ToolStripMenuItem("VIP-lijst…");
        vip.Click += (_, _) => OpenVip();
        menu.Items.Add(vip);

        var verjaardagen = new ToolStripMenuItem("Verjaardagen & cadeaus…");
        verjaardagen.Click += (_, _) => OpenVerjaardagen();
        menu.Items.Add(verjaardagen);

        var webversie = new ToolStripMenuItem("WorkManager online…");
        webversie.Click += (_, _) => OpenWebversie();
        menu.Items.Add(webversie);

        menu.Items.Add(new ToolStripSeparator());

        // Kleurenschema: keuze uit de paletten in Themas.cs, met een vinkje bij het actieve.
        var thema = new ToolStripMenuItem("Kleurenschema");
        foreach (var palet in Themas.Alle)
        {
            // Omschrijving ín de tekst (zoals in het cockpit-⋯-menu), niet als tooltip:
            // de tooltip popte pal onder de cursor en ving de klik af — vooral op het
            // onderste item, waar je het langst naartoe beweegt.
            var keuze = new ToolStripMenuItem($"{palet.Naam} — {palet.Omschrijving}");
            keuze.Click += (_, _) => Theme.ZetThema(palet);
            thema.DropDownItems.Add(keuze);
        }
        thema.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem keuze in thema.DropDownItems)
            {
                keuze.Checked = keuze.Text!.StartsWith(Theme.Palet.Naam + " —", StringComparison.Ordinal);
            }
        };
        menu.Items.Add(thema);

        var autoStart = new ToolStripMenuItem("Automatisch starten met Windows") { Tag = "autostart" };
        autoStart.Click += (_, _) => SetAutoStart(!IsAutoStartEnabled());
        menu.Items.Add(autoStart);

        // Vangnet: raakt er ooit een venster "altijd bovenop" vast te zitten (een afgebroken
        // aanmeldscherm, of een schermafdrukhulpje van buitenaf), dan kom je niet meer bij
        // wat erachter staat. Dit zet dat voor alle vensters van de app terug en haalt de
        // andere vensters naar voren.
        var vensters = new ToolStripMenuItem("Vensters losmaken (altijd-bovenop uit)");
        vensters.Click += (_, _) => MaakVenstersLos();
        menu.Items.Add(vensters);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Afsluiten");
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        menu.Opening += (_, _) =>
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is not ToolStripMenuItem mi || mi.Tag is not string tag)
                {
                    continue;
                }

                if (tag == "mijntaken")
                {
                    // Aantal openstaande eigen taken meteen in het menu tonen.
                    var open = MijnTaakStore.OpenAantal();
                    mi.Text = open > 0 ? $"Mijn taken ({open} open)…" : "Mijn taken…";
                    continue;
                }
                if (tag == "followup")
                {
                    var wachtend = FollowUpRadar.Actief().Count;
                    mi.Text = wachtend > 0 ? $"Wacht op antwoord ({wachtend})…" : "Wacht op antwoord…";
                    continue;
                }
                mi.Checked = tag == "autostart" ? IsAutoStartEnabled() : _active.Contains(tag);
            }
        };

        return menu;
    }

    /// <summary>Rond kleurstipje als menu-icoon voor een klantcontext.</summary>
    private static Bitmap MaakKleurStip(Color kleur)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(kleur);
        g.FillEllipse(brush, 3, 3, 10, 10);
        return bmp;
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // NotifyIcon kent geen publieke methode om het menu te tonen; ook bij linksklik willen we het menu.
        typeof(NotifyIcon)
            .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_trayIcon, null);
    }

    private VipForm? _vipForm;

    private void OpenVip()
    {
        if (_vipForm is { IsDisposed: false })
        {
            _vipForm.Activate();
            return;
        }

        _vipForm = new VipForm();
        _vipForm.FormClosed += (_, _) => _vipForm = null;
        _vipForm.Show();
    }

    private VerjaardagenForm? _verjaardagenForm;

    private void OpenVerjaardagen()
    {
        if (_verjaardagenForm is { IsDisposed: false })
        {
            _verjaardagenForm.Activate();
            return;
        }

        _verjaardagenForm = new VerjaardagenForm();
        _verjaardagenForm.FormClosed += (_, _) => _verjaardagenForm = null;
        _verjaardagenForm.Show();
    }

    /// <summary>De persoonlijke webversie instellen en de link/QR tonen.</summary>
    private void OpenWebversie()
    {
        if (_wmWebForm is { IsDisposed: false })
        {
            _wmWebForm.Activate();
            return;
        }

        _wmWebForm = new WmWebForm();
        _wmWebForm.FormClosed += (_, _) => _wmWebForm = null;
        _wmWebForm.Show();
    }

    private WmWebForm? _wmWebForm;

    private CockpitForm? _cockpitForm;

    /// <summary>
    /// Zet "altijd bovenop" uit voor alle vensters van de app en haalt de andere vensters
    /// boven de cockpit. Bedoeld voor de situatie waarin een venster vastzit op de voorgrond
    /// en je niet meer bij een dialoog erachter kunt.
    /// </summary>
    private void MaakVenstersLos()
    {
        var losgemaakt = 0;
        foreach (Form venster in Application.OpenForms)
        {
            if (venster.TopMost)
            {
                venster.TopMost = false;
                losgemaakt++;
            }
        }
        // De cockpit is meestal het grote venster; alle andere er weer bovenop zetten.
        foreach (Form venster in Application.OpenForms)
        {
            if (!ReferenceEquals(venster, _cockpitForm) && venster.Visible)
            {
                venster.BringToFront();
            }
        }
        TrayMelding.Toon("Vensters losgemaakt",
            losgemaakt > 0
                ? $"{losgemaakt} venster(s) stonden op 'altijd bovenop'; dat staat nu uit."
                : "Geen venster stond op 'altijd bovenop'; de andere vensters staan nu vooraan.");
    }

    private void OpenCockpit()
    {
        if (_cockpitForm is { IsDisposed: false })
        {
            // Ctrl,Ctrl (of het tray-menu): altijd gemaximaliseerd en écht op de voorgrond,
            // ook als een ander programma de focus heeft.
            NaarVoorgrond(_cockpitForm);
            return;
        }

        _cockpitForm = new CockpitForm(
            () => _active, ToggleClient, OpenMailReply, OpenTeamTasks, OpenInvoiceApproval,
            OpenTopdesk, OpenDevOps, OpenVenster);
        _cockpitForm.FormClosed += (_, _) => _cockpitForm = null;
        _cockpitForm.Show();
        NaarVoorgrond(_cockpitForm);
    }

    /// <summary>
    /// Opent een tray-venster op naam. De cockpit is de vaste werkplek en biedt via zijn
    /// ⋯-menu alle tray-functies aan; dit is de ene ingang daarvoor.
    /// </summary>
    private void OpenVenster(string naam)
    {
        switch (naam)
        {
            case "dagstart":
                OpenBriefing();
                break;
            case "mijntaken":
                OpenMijnTaken();
                break;
            case "bureaublad":
                var cleaner = new BureaubladCleanerForm();
                // Bij het sluiten meteen de opruimtaak bijwerken, net als vanuit het tray-menu.
                cleaner.FormClosed += (_, _) => VasteTaken.WerkBureaubladTaakBij();
                cleaner.Show();
                break;
            case "ah":
                using (var bestel = new AhBestelForm())
                {
                    bestel.ShowDialog();
                }
                break;
            case "followup":
                OpenFollowUp();
                break;
            case "vip":
                OpenVip();
                break;
            case "verjaardagen":
                OpenVerjaardagen();
                break;
            case "webversie":
                OpenWebversie();
                break;
            case "verlof":
                OpenSdWorxPortaal();
                break;
        }
    }

    private TopdeskForm? _topdeskForm;

    private void OpenTopdesk()
    {
        if (_topdeskForm is { IsDisposed: false })
        {
            _topdeskForm.Activate();
            return;
        }

        _topdeskForm = new TopdeskForm();
        _topdeskForm.FormClosed += (_, _) => _topdeskForm = null;
        _topdeskForm.Show();
    }

    private SdWorxPortaalForm? _sdworxPortaalForm;

    private void OpenSdWorxPortaal()
    {
        if (_sdworxPortaalForm is { IsDisposed: false })
        {
            _sdworxPortaalForm.Activate();
            return;
        }

        _sdworxPortaalForm = new SdWorxPortaalForm();
        _sdworxPortaalForm.FormClosed += (_, _) => _sdworxPortaalForm = null;
        _sdworxPortaalForm.Show();
    }

    private DevOpsForm? _devOpsForm;

    private void OpenDevOps()
    {
        if (_devOpsForm is { IsDisposed: false })
        {
            _devOpsForm.Activate();
            return;
        }

        _devOpsForm = new DevOpsForm();
        _devOpsForm.FormClosed += (_, _) => _devOpsForm = null;
        _devOpsForm.Show();
    }

    private void OpenInvoiceApproval()
    {

        if (_invoiceForm is { IsDisposed: false })
        {
            _invoiceForm.Activate();
            return;
        }

        _invoiceForm = new InvoiceApprovalForm();
        _invoiceForm.FormClosed += (_, _) => _invoiceForm = null;
        _invoiceForm.Show();
    }

    /// <summary>
    /// Zet gesnoozde mails waarvan de snoozetijd verstreken is terug in de Gmail-inbox.
    /// Mislukt het terugzetten (bv. geen netwerk), dan blijft de snooze staan en lukt het
    /// bij een volgende controle; onvindbare mails worden opgegeven.
    /// </summary>
    private async Task CheckSnoozesAsync()
    {
        if (_snoozeBusy)
        {
            return;
        }

        var snoozes = SnoozeStore.LoadSnoozes();
        var nu = DateTimeOffset.Now;
        if (!snoozes.Any(s => s.Tot <= nu))
        {
            return;
        }
        var settings = MailReplySettings.Load();
        if (settings.AppWachtwoord.Length == 0)
        {
            return;
        }

        _snoozeBusy = true;
        try
        {
            var klaar = new List<SnoozeStore.SnoozeItem>();
            foreach (var item in snoozes.Where(s => s.Tot <= nu).ToList())
            {
                try
                {
                    var teruggezet = await GmailClient.TerugNaarInboxAsync(
                        settings, item.MessageId, CancellationToken.None);
                    klaar.Add(item); // ook bij niet gevonden (bv. handmatig verwijderd): opgeven
                    if (teruggezet)
                    {
                        TrayMelding.Toon("Snooze afgelopen",
                            $"{item.Van} – {item.Onderwerp}", duurMs: 4000);
                    }
                }
                catch
                {
                    // Netwerk-/IMAP-fout: snooze laten staan en later opnieuw proberen.
                }
            }

            if (klaar.Count > 0)
            {
                var actueel = SnoozeStore.LoadSnoozes();
                actueel.RemoveAll(s => klaar.Any(k => k.MessageId == s.MessageId));
                SnoozeStore.SaveSnoozes(actueel);
            }
        }
        finally
        {
            _snoozeBusy = false;
        }
    }

    private void OpenMijnTaken()
    {
        if (_mijnTakenForm is { IsDisposed: false })
        {
            _mijnTakenForm.Activate();
            return;
        }

        _mijnTakenForm = new MijnTakenForm();
        _mijnTakenForm.FormClosed += (_, _) => _mijnTakenForm = null;
        _mijnTakenForm.Show();
    }

    /// <summary>
    /// Herinnert één keer per dag (rond de eerste controle na 8u) aan eigen taken met een
    /// deadline van vandaag of eerder; klikken op de melding opent het takenvenster.
    /// </summary>
    private void CheckMijnTakenHerinnering()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (_takenHerinnerd == vandaag || DateTime.Now.Hour < 8)
        {
            return;
        }
        _takenHerinnerd = vandaag;

        var aandacht = MijnTaakStore.AandachtVandaag();
        if (aandacht.Count == 0)
        {
            return;
        }
        var voorbeeld = string.Join("\n", aandacht.Take(3).Select(t => "• " + t.Tekst)) +
                        (aandacht.Count > 3 ? $"\n… en nog {aandacht.Count - 3}" : "");
        TrayMelding.Toon(
            aandacht.Count == 1 ? "1 taak voor vandaag" : $"{aandacht.Count} taken voor vandaag",
            voorbeeld, OpenMijnTaken, 6000);
    }

    private void OpenBriefing()
    {
        if (_briefingForm is { IsDisposed: false })
        {
            _briefingForm.Activate();
            return;
        }

        _briefingForm = new BriefingForm();
        _briefingForm.FormClosed += (_, _) => _briefingForm = null;
        _briefingForm.Show();
    }

    /// <summary>
    /// Stelt op een werkdag vanaf 8u één keer de dagstartbriefing samen en meldt hem in de
    /// tray; klikken op de melding opent het dagstartvenster. Buiten de kantooruren of op een
    /// dag waarop de briefing al gemaakt is gebeurt er niets.
    /// </summary>
    private async Task CheckDagBriefingAsync()
    {
        var nu = DateTime.Now;
        var vandaag = DateOnly.FromDateTime(nu);
        if (_briefingGemeld == vandaag || nu.Hour < 8 || nu.Hour >= 20 ||
            nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            DagBriefing.Bezig)
        {
            return;
        }
        if (DagBriefing.VanVandaag() is not null)
        {
            // Al gemaakt (bv. via het venster): niet nog eens melden vandaag.
            _briefingGemeld = vandaag;
            return;
        }
        _briefingGemeld = vandaag;

        try
        {
            var briefing = await DagBriefing.MaakAsync(CancellationToken.None);
            var tekst = briefing.Samenvatting.Length > 0
                ? briefing.Samenvatting
                : $"{briefing.Afspraken} afspraken · {briefing.OpenTaken} open taken";
            TrayMelding.Toon("Dagstart", Kort(tekst, 240), OpenBriefing, 10000);
        }
        catch
        {
            // Geen Claude of geen net: morgen opnieuw, en het venster kan het handmatig.
        }
    }

    /// <summary>
    /// Nachtelijk onderhoud (tussen 03:00 en 05:00, één keer per dag): de verborgen
    /// browsersessies (Teams/Outlook/WhatsApp) markeren voor een verse start. Ze degraderen
    /// na dagen draaien (geheugen, throttling, scheefgegroeide lokale staat); de
    /// eerstvolgende poll bouwt ze opnieuw op — cookies blijven staan, dus geen extra
    /// MFA of QR-scan.
    /// </summary>
    private void CheckNachtOnderhoud()
    {
        var nu = DateTime.Now;
        var vandaag = DateOnly.FromDateTime(nu);
        if (nu.Hour is < 3 or >= 5 || _nachtOnderhoud == vandaag)
        {
            return;
        }
        _nachtOnderhoud = vandaag;
        // Heeft de geheugenbewaking overdag een herstart gepland, dan nu de héle app
        // vers opstarten (het diepste onderhoud); anders volstaan verse sessies.
        if (_herstartNodig)
        {
            OnderhoudLog("nachtelijk onderhoud: app-herstart (geheugendrempel was overschreden)");
            Program.PlanHerstart();
            ExitThread();
            return;
        }
        TeamsClient.Instance.MarkeerVoorVerseStart();
        OutlookClient.Instance.MarkeerVoorVerseStart();
        WhatsAppClient.Instance.MarkeerVoorVerseStart();
        OnderhoudLog("nachtelijk onderhoud: sessies gemarkeerd voor verse start");
    }

    /// <summary>
    /// Ochtendritueel (werkdagen, 7u30–10u, één keer per dag): de verborgen sessies alvast
    /// warmdraaien zodat de eerste cockpit-blik meteen gevuld is, en direct melden als de
    /// dagelijkse Outlook-MFA verlopen is — met een klikactie om meteen aan te melden, in
    /// plaats van pollfouten tot het toevallig opvalt.
    /// </summary>
    private void CheckOchtendStart()
    {
        var nu = DateTime.Now;
        var vandaag = DateOnly.FromDateTime(nu);
        if (_ochtendWarmup == vandaag ||
            nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            nu.Hour >= 10 || nu.Hour < 7 || (nu.Hour == 7 && nu.Minute < 30))
        {
            return;
        }
        _ochtendWarmup = vandaag;
        _ = OchtendWarmupAsync();
    }

    private async Task OchtendWarmupAsync()
    {
        try
        {
            if (OutlookClient.OoitGekoppeld &&
                !await OutlookClient.Instance.StartAsync(CancellationToken.None, wachtSeconden: 25))
            {
                TrayMelding.Toon("Outlook aanmelden (dagelijkse MFA)",
                    "De CED-Outlooksessie is verlopen — klik hier om nu aan te melden.",
                    () => _ = OutlookAanmeldenAsync(), 20000);
            }
            if (TeamsClient.OoitGekoppeld)
            {
                await TeamsClient.Instance.StartAsync(CancellationToken.None, wachtSeconden: 25);
            }
            if (WhatsAppClient.OoitGekoppeld)
            {
                await WhatsAppClient.Instance.StartAsync(CancellationToken.None, wachtSeconden: 25);
            }
            OnderhoudLog("ochtendwarmup afgerond");
        }
        catch (Exception ex)
        {
            OnderhoudLog($"ochtendwarmup: {ex.Message}");
        }
    }

    private async Task OutlookAanmeldenAsync()
    {
        try
        {
            await OutlookClient.Instance.KoppelAsync(CancellationToken.None);
            TrayMelding.Toon("Outlook aangemeld", "De CED-sessie is weer actief.");
        }
        catch (Exception ex)
        {
            TrayMelding.Toon("Outlook aanmelden mislukt", ex.Message);
        }
    }

    /// <summary>
    /// Houdt de WebView2-runtimeversie bij: een evergreen-update van Microsoft is een
    /// klassieke bron van "gisteren werkte alles nog" — met deze logregel is dat verband
    /// in één blik te leggen.
    /// </summary>
    private static void LogWebViewVersie()
    {
        try
        {
            var versie = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString();
            var marker = Path.Combine(DataDir, "webview2-versie.txt");
            var vorige = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (vorige != versie)
            {
                OnderhoudLog(vorige.Length == 0
                    ? $"WebView2-runtime: {versie}"
                    : $"WebView2-runtime bijgewerkt: {vorige} → {versie}");
                File.WriteAllText(marker, versie);
            }
        }
        catch
        {
            // Geen runtime-info: dan ook niets te loggen.
        }
    }

    private static void OnderhoudLog(string melding)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "sessie-onderhoud-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {melding}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>
    /// Geheugenbewaking: groeit de werkset van de app boven de drempel, dan plannen we een
    /// volledige herstart voor de eerstvolgende nacht (cookies en state staan op schijf,
    /// dus daar merkt niemand iets van).
    /// </summary>
    private void CheckGeheugen()
    {
        if (_herstartNodig)
        {
            return;
        }
        try
        {
            using var proces = System.Diagnostics.Process.GetCurrentProcess();
            if (proces.WorkingSet64 > 2_500_000_000L)
            {
                _herstartNodig = true;
                OnderhoudLog($"geheugendrempel overschreden ({proces.WorkingSet64 / 1_000_000} MB): " +
                    "app-herstart gepland voor vannacht");
            }
        }
        catch
        {
            // Meting mislukt: volgende tik opnieuw.
        }
    }

    /// <summary>
    /// Dagelijkse backup van %APPDATA%\WorkManager naar OneDrive (taken, regels, caches,
    /// koppelingen — de browserprofielen en dikke dumps blijven eruit). Zeven zips bewaard.
    /// </summary>
    private void CheckBackup()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (_backupGedaan == vandaag)
        {
            return;
        }
        _backupGedaan = vandaag;
        Task.Run(() =>
        {
            try
            {
                var oneDrive = Environment.GetEnvironmentVariable("OneDrive")
                    ?? Environment.GetEnvironmentVariable("OneDriveCommercial")
                    ?? Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "";
                if (oneDrive.Length == 0 || !Directory.Exists(oneDrive))
                {
                    return; // geen OneDrive: stilletjes overslaan
                }
                var doelMap = Path.Combine(oneDrive, "WorkManager-backup");
                Directory.CreateDirectory(doelMap);
                var doel = Path.Combine(doelMap, $"workmanager-{vandaag:yyyy-MM-dd}.zip");
                if (File.Exists(doel))
                {
                    return; // vandaag al gemaakt (bv. vóór een herstart)
                }
                var tmp = doel + ".tmp";
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    foreach (var pad in Directory.EnumerateFiles(
                                 DataDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(DataDir, pad);
                        // Browserprofielen zijn gigantisch en herbouwbaar; >20 MB is nooit
                        // een statebestand maar een dump of database.
                        if (rel.StartsWith("webview2", StringComparison.OrdinalIgnoreCase) ||
                            new FileInfo(pad).Length > 20_000_000)
                        {
                            continue;
                        }
                        try
                        {
                            zip.CreateEntryFromFile(pad, rel);
                        }
                        catch
                        {
                            // Bestand net in gebruik: dan zonder dit bestand.
                        }
                    }
                }
                File.Move(tmp, doel, overwrite: true);
                foreach (var oud in Directory.GetFiles(doelMap, "workmanager-*.zip")
                             .OrderByDescending(f => f, StringComparer.Ordinal).Skip(7))
                {
                    File.Delete(oud);
                }
                OnderhoudLog($"backup gemaakt: {doel}");
            }
            catch (Exception ex)
            {
                OnderhoudLog($"backup mislukt: {ex.Message}");
            }
        });
    }

    private static string Kort(string tekst, int max) => tekst.Length > max ? tekst[..max] + "…" : tekst;

    private void OpenFollowUp()
    {
        if (_followUpForm is { IsDisposed: false })
        {
            _followUpForm.Activate();
            return;
        }

        _followUpForm = new FollowUpForm();
        _followUpForm.FormClosed += (_, _) => _followUpForm = null;
        _followUpForm.Show();
    }

    private void OpenTeamTasks()
    {
        if (_tasksForm is { IsDisposed: false })
        {
            _tasksForm.Activate();
            return;
        }

        _tasksForm = new TeamTasksForm();
        _tasksForm.FormClosed += (_, _) => _tasksForm = null;
        _tasksForm.Show();
    }

    private void OpenMailReply()
    {
        if (_mailForm is { IsDisposed: false })
        {
            _mailForm.Activate();
            return;
        }

        _mailForm = new MailReplyForm();
        _mailForm.FormClosed += (_, _) => _mailForm = null;
        _mailForm.Show();
    }

    private void ToggleClient(string client)
    {
        var turnedOn = _active.Add(client);
        if (!turnedOn)
        {
            _active.Remove(client);
        }

        UpdateIcon();
        SaveState();

        var now = DateTimeOffset.Now;
        File.AppendAllText(LogFile, JsonSerializer.Serialize(
            new { timestamp = now, client, action = turnedOn ? "on" : "off" }) + Environment.NewLine);

        TrayMelding.Toon("WorkManager", $"{client}: {(turnedOn ? "aan" : "uit")}", duurMs: 2000);

        if (turnedOn)
        {
            Task.Run(() => ClientLauncher.LaunchFor(client));
        }
        else
        {
            Task.Run(() => ClientLauncher.CloseFor(client));
        }
    }

    private bool _aanmeldBadge;
    private readonly DateTimeOffset _gestart = DateTimeOffset.Now;

    /// <summary>
    /// Oranje stip op het tray-icoon zolang Teams of Outlook op heraanmelding wacht — stil
    /// (geen geluid of popup), maar wel altijd zichtbaar. Pas na wat opstarttijd: vlak na
    /// de start betekent "niet aangemeld" alleen dat de eerste poll nog moet lopen.
    /// </summary>
    private void WerkAanmeldBadgeBij()
    {
        var nodig = DateTimeOffset.Now - _gestart > TimeSpan.FromMinutes(10) &&
            ((TeamsClient.OoitGekoppeld && !TeamsClient.Aangemeld) ||
             (OutlookClient.OoitGekoppeld && !OutlookClient.Aangemeld));
        if (nodig != _aanmeldBadge)
        {
            _aanmeldBadge = nodig;
            UpdateIcon();
        }
    }

    private void UpdateIcon()
    {
        var active = Clients.Where(c => _active.Contains(c.Name)).ToArray();

        if (active.Length == 0 && Theme.AppIcon is not null && !_aanmeldBadge)
        {
            // Rustmodus: hetzelfde tegel-icoon als de exe en de vensters.
            _trayIcon.Icon = Theme.AppIcon;
            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
            _trayIcon.Text = "WorkManager – geen actieve context";
            return;
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (active.Length == 0)
            {
                // Rustmodus: accentverloop met een witte W.
                using var verloop = new LinearGradientBrush(
                    new Rectangle(1, 1, 30, 30), Theme.Accent, Theme.KlantCed, 55f);
                g.FillEllipse(verloop, 1, 1, 30, 30);
                using var wFont = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var wsf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString("W", wFont, Brushes.White, new RectangleF(1, 1.5f, 30, 30), wsf);
            }
            else
            {
                // Eén taartpunt per actieve context, elk met de beginletter.
                var sweep = 360f / active.Length;
                using var font = new Font(
                    "Segoe UI", active.Length == 1 ? 18f : 11f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };

                for (var i = 0; i < active.Length; i++)
                {
                    using var brush = new SolidBrush(active[i].Color);
                    g.FillPie(brush, 1, 1, 30, 30, -90f + i * sweep, sweep);
                }

                for (var i = 0; i < active.Length; i++)
                {
                    var center = new PointF(16f, 16f);
                    if (active.Length > 1)
                    {
                        var midAngle = (-90f + (i + 0.5f) * sweep) * Math.PI / 180.0;
                        center = new PointF(
                            16f + 7.5f * (float)Math.Cos(midAngle),
                            16f + 7.5f * (float)Math.Sin(midAngle));
                    }
                    g.DrawString(
                        active[i].Name[..1], font, Brushes.White,
                        new RectangleF(center.X - 8f, center.Y - 8f, 16f, 16f), sf);
                }
            }

            if (_aanmeldBadge)
            {
                // Donker randje zodat de stip op elke taartkleur leesbaar blijft.
                using var rand = new SolidBrush(Color.FromArgb(32, 32, 32));
                g.FillEllipse(rand, 19, 19, 13, 13);
                using var stip = new SolidBrush(Color.FromArgb(0xF7, 0x90, 0x09));
                g.FillEllipse(stip, 21, 21, 9, 9);
            }
        }

        var newHandle = bmp.GetHicon();
        _trayIcon.Icon = Icon.FromHandle(newHandle);
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
        _iconHandle = newHandle;

        _trayIcon.Text = (active.Length == 0
            ? "WorkManager – geen actieve context"
            : $"WorkManager – actief: {string.Join(" + ", active.Select(c => c.Name))}") +
            (_aanmeldBadge ? " ⚠ aanmelden nodig" : "");
    }

    private void SaveState()
    {
        File.WriteAllText(StateFile, JsonSerializer.Serialize(
            new { active = Clients.Where(c => _active.Contains(c.Name)).Select(c => c.Name), since = DateTimeOffset.Now },
            JsonOpts));
    }

    private void EnsureStateFile()
    {
        if (!File.Exists(StateFile))
        {
            SaveState();
        }
    }

    private static HashSet<string> LoadState()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StateFile))
            {
                return active;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            foreach (var element in doc.RootElement.GetProperty("active").EnumerateArray())
            {
                if (element.GetString() is { } name && Clients.Any(c => c.Name == name))
                {
                    active.Add(name);
                }
            }
        }
        catch
        {
            // Onleesbare state: start zonder actieve contexten.
        }
        return active;
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AutoStartValueName) is string;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
        {
            key.SetValue(AutoStartValueName, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Per limiet en per resetvenster hoogstens één melding per drempel (50/80/95).</summary>
    private readonly HashSet<string> _usageGemeld = new(StringComparer.Ordinal);

    private async Task CheckClaudeUsageAsync()
    {
        try
        {
            var limieten = await ClaudeUsage.OphalenAsync(CancellationToken.None);
            foreach (var l in limieten)
            {
                var drempel = l.Percent switch { >= 95 => 95, >= 80 => 80, >= 50 => 50, _ => 0 };
                if (drempel == 0)
                {
                    continue;
                }
                var sleutel = $"{l.Kind}|{l.Reset:yyyyMMddHH}|{drempel}";
                if (!_usageGemeld.Add(sleutel))
                {
                    continue;
                }
                TrayMelding.Toon("Claude-verbruik",
                    $"{l.Naam}: {l.Percent}% gebruikt — {l.ResetTekst}", duurMs: 10000);
            }
        }
        catch
        {
            // Geen login of geen net: volgend uur opnieuw.
        }
    }

    private void ExitApplication()
    {
        _voiceCts.Cancel();
        _dubbelCtrl?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        ExitThread();
    }
}
