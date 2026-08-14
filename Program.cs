namespace WorkManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Een onverwachte UI-fout (zoals de bekende ListView.HitTest-uitglijder) mag nooit
        // de "Continue/Quit"-crashdialoog tonen: loggen en gewoon doordraaien.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        // Fataal (proces gaat hoe dan ook neer, bv. een fout op een achtergrondthread):
        // loggen en automatisch herstarten — met een teller zodat een crash-loop na
        // drie snelle herstarts stopt in plaats van eeuwig te blijven rondtollen.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash(e.ExceptionObject as Exception);
            if (e.IsTerminating && MagHerstartenNaCrash())
            {
                PlanHerstart();
            }
        };

        // Headless regressietests voor de kwetsbaarste tekstparsers (OWA-labels wijzigen
        // geregeld): resultaat in %APPDATA%\WorkManager\parser-tests.txt, exitcode = aantal fouten.
        if (args.Length == 1 && args[0] == "--parsertests")
        {
            Environment.ExitCode = ParserTests.Draai();
            return;
        }

        // Leesbaarheidscontrole van alle kleurenschema's (WCAG-contrast per combinatie).
        if (args.Length == 1 && args[0] == "--themacheck")
        {
            Environment.ExitCode = ThemaCheck.Draai();
            return;
        }

        // Diagnose: teken het beeldmerk van elk thema naar een PNG, zodat de tekening zelf
        // te beoordelen is zonder de app te openen.
        // Gebruik: WorkManager.exe --emblemen [map]
        if (args.Length is 1 or 2 && args[0] == "--emblemen")
        {
            var map = args.Length == 2 ? args[1] : Path.Combine(Path.GetTempPath(), "wm-emblemen");
            Directory.CreateDirectory(map);
            foreach (var palet in Themas.Alle)
            {
                Theme.ZetThema(palet);
                using var bmp = new Bitmap(220, 220);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Theme.Surface);
                    ThemaEmbleem.Teken(g, new Rectangle(10, 10, 200, 200), 0.55f, Theme.Surface);
                }
                var pad = Path.Combine(map, $"embleem-{palet.Naam}.png");
                bmp.Save(pad, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine(pad);
            }
            return;
        }

        // Diagnose: welk klantdossier krijgt Claude te zien bij een mail van dit adres?
        // Gebruik: WorkManager.exe --dossier nicolas@lauryssens.be
        if (args.Length == 2 && args[0] == "--dossier")
        {
            var dossier = KlantDossier.Voor(args[1]);
            if (dossier.Length == 0)
            {
                Console.WriteLine($"Geen klantdossier voor {args[1]} (map: {KlantDossier.Map()})");
                Environment.ExitCode = 1;
                return;
            }
            var titel = dossier.Split('\n').FirstOrDefault(r => r.StartsWith('#')) ?? "(geen titel)";
            Console.WriteLine($"{args[1]} → {titel.Trim('#', ' ')} ({dossier.Length} tekens)");
            return;
        }

        // Diagnose: welke openstaande punten haalt de radar uit de klantdossiers?
        // (De echte scan draait op maandag; hiermee kun je de uitkomst nu al zien.)
        if (args.Length == 1 && args[0] == "--dossierpunten")
        {
            var map = KlantDossier.Map();
            foreach (var pad in Directory.EnumerateFiles(map, "*.md"))
            {
                var punten = DossierPunten.PuntenUit(File.ReadAllText(pad));
                Console.WriteLine($"=== {Path.GetFileName(pad)} — {punten.Count} punt(en)");
                foreach (var punt in punten)
                {
                    Console.WriteLine("  • " + (punt.Length > 110 ? punt[..110] + "…" : punt));
                }
            }
            return;
        }

        // De persoonlijke webversie koppelen zonder het venster: --wmweb <url> <token>.
        // Zonder token: alleen de huidige stand tonen en één keer synchroniseren.
        if (args.Length is 1 or 3 && args[0] == "--wmweb")
        {
            var settings = WmWebSettings.Load();
            if (args.Length == 3)
            {
                settings.Url = args[1];
                settings.Token = args[2];
                settings.Save();
            }
            Console.WriteLine($"URL:      {(settings.Url.Length > 0 ? settings.Url : "(leeg)")}");
            Console.WriteLine($"Token:    {(settings.Token.Length > 0 ? "ingesteld" : "(leeg)")}");
            Console.WriteLine($"Compleet: {settings.Compleet}");
            if (settings.Compleet)
            {
                new WmWebSync().PollAsync().GetAwaiter().GetResult();
                Console.WriteLine("Snapshot verstuurd.");
            }
            return;
        }

        // Diagnose: een stuk JavaScript in de (verborgen) Outlook-sessie draaien en het
        // resultaat afdrukken. Handig als OWA zijn DOM weer eens wijzigt en je wilt weten
        // welke selector de maillijst nu oplevert. De tray-app moet dan afgesloten zijn:
        // maar één proces tegelijk kan het webview-profiel gebruiken.
        if (args.Length == 2 && args[0] == "--owajs")
        {
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(Theme.BaseFont);
            var klaar = new TaskCompletionSource<string>();
            var pomp = new System.Windows.Forms.Timer { Interval = 50 };
            pomp.Tick += async (_, _) =>
            {
                pomp.Stop();
                try
                {
                    klaar.SetResult(await OutlookClient.Instance.DiagnoseJsAsync(
                        args[1], CancellationToken.None));
                }
                catch (Exception ex)
                {
                    klaar.SetResult("FOUT: " + ex.Message);
                }
                Application.ExitThread();
            };
            pomp.Start();
            Application.Run();
            Console.WriteLine(klaar.Task.Result);
            return;
        }

        // Zelfde als --owajs, maar dan in de verborgen Teams-sessie.
        if (args.Length == 2 && args[0] == "--teamsjs")
        {
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(Theme.BaseFont);
            var klaarTeams = new TaskCompletionSource<string>();
            var pompTeams = new System.Windows.Forms.Timer { Interval = 50 };
            pompTeams.Tick += async (_, _) =>
            {
                pompTeams.Stop();
                try
                {
                    klaarTeams.SetResult(await TeamsClient.Instance.DiagnoseJsAsync(
                        args[1], CancellationToken.None));
                }
                catch (Exception ex)
                {
                    klaarTeams.SetResult("FOUT: " + ex.Message);
                }
                Application.ExitThread();
            };
            pompTeams.Start();
            Application.Run();
            Console.WriteLine(klaarTeams.Task.Result);
            return;
        }

        // Diagnose: waarom toont de cockpit minder mails dan Gmail?
        if (args.Length == 1 && args[0] == "--mailcheck")
        {
            var instellingen = MailReplySettings.Load();
            if (instellingen.AppWachtwoord.Length == 0)
            {
                Console.WriteLine("Geen Gmail-koppeling ingesteld.");
                return;
            }
            Console.WriteLine(GmailClient.DiagnoseAsync(instellingen, CancellationToken.None)
                .GetAwaiter().GetResult());
            return;
        }

        // Testmodus: logt naar %APPDATA%\WorkManager\launcher.log welke acties zouden draaien.
        if (args.Length == 2 && args[0] == "--dry-run")
        {
            ClientLauncher.LaunchFor(args[1], dryRun: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--dry-run-close")
        {
            ClientLauncher.CloseFor(args[1], dryRun: true);
            return;
        }
        if (args.Length == 3 && args[0] == "--timesheet" && args[1] is "start" or "stop")
        {
            ClientLauncher.TimesheetCli(args[1], args[2]);
            return;
        }

        // Ontwikkeltest: ICS-bestand parsen (venster: vandaag + optioneel aantal dagen)
        // en het resultaat naast het bestand wegschrijven.
        if (args.Length is 2 or 3 && args[0] == "--ics-test")
        {
            var dagen = args.Length == 3 && int.TryParse(args[2], out var d) ? d : 1;
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var items = AgendaClient.ParseIcs(File.ReadAllText(args[1]), vandaag, vandaag.AddDays(dagen));
            File.WriteAllLines(args[1] + ".out.txt", items.Select(i =>
                $"{i.Start:yyyy-MM-dd HH:mm} - {i.Einde:HH:mm} heledag={i.HeleDag} | {i.Titel}"));
            return;
        }

        // Visuele test: één venster rechtstreeks openen, zonder tray en zonder single-instance-slot.
        // Met een derde argument het kleurenschema erbij: --venster mijn 007
        if (args.Length is 2 or 3 && args[0] == "--venster")
        {
            ApplicationConfiguration.Initialize();
            // Eerst het lettertype: SetColorMode maakt intern al een venster aan,
            // waarna SetDefaultFont niet meer mag.
            Application.SetDefaultFont(Theme.BaseFont);
            // Optioneel derde argument: het kleurenschema om te testen ("--venster mijn 007").
            if (args.Length == 3 &&
                Themas.Alle.FirstOrDefault(t =>
                    t.Naam.Equals(args[2], StringComparison.OrdinalIgnoreCase)) is { } gekozenThema)
            {
                Theme.ZetThema(gekozenThema);
            }
#pragma warning disable WFO5001
            Application.SetColorMode(
                Theme.Palet.Donker ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
            Theme.ZetStandaardRenderer();
            using Form venster = args[1] switch
            {
                "taken" => new TeamTasksForm(),
                "mijn" or "fx" => new MijnTakenForm(),
                "instellingen" => new MailSettingsForm(),
                "regels" => new RulesForm(),
                "snooze" => new SnoozeForm(1, DateTimeOffset.Now.AddHours(3)),
                "mailtaak" => new MailTaakForm(new MailBericht
                {
                    Van = "Jan Peeters", Onderwerp = "Offerte servermigratie",
                }),
                "uittekst" => new TakenUitTekstForm(MijnTaakStore.Load().Categorieen),
                "teamuittekst" => new TeamUitTekstForm(
                    new List<string> { "Wim", "Kris", "Christophe", "Laurent" }, "Wim"),
                "timesheetdash" => new TimesheetDashboardForm(),
                "dagplan" => new DagPlanForm(new List<AgendaClient.AgendaItem>()),
                "git" => new GitStatusForm(
                    @"\\wsl.localhost\Ubuntu\home\maarten\projecten\aqurat", "aqurat"),
                "vakanties" => new VakantiesForm(),
                "vakantiesdump" => new VakantiesForm(alleenInspecteren: true),
                "teambewerk" => new TeamTaakBewerkForm(
                    new List<string> { "Wim", "Kris", "Christophe", "Laurent" },
                    new TeamTaak { Lid = "Kris", Tekst = "Facturatie-run van juli nakijken" }),
                "ah" => new AhBestelForm(),
                "ahlinks" => AhBestelForm.LinkEditor(),
                "ahrecept" => AhBestelForm.ReceptKaartTest(),
                "ahkeuze" => new AhIngredientKeuzeForm(
                    new List<AhIngredient>
                    {
                        new() { Naam = "spaghetti" },
                        new() { Naam = "rundergehakt" },
                        new() { Naam = "passata of tomatenblokjes" },
                        new() { Naam = "ui", Aantal = 2 },
                        new() { Naam = "knoflook" },
                        new() { Naam = "wortel", Url = "https://www.ah.be/producten/product/wi4076/ah-winterpeen" },
                        new() { Naam = "parmezaanse kaas", Aantal = 2 },
                        new() { Naam = "sambal oelek" },
                        new() { Naam = "gewone spaghetti (tarwe)", Url = "https://www.ah.be/producten/product/wi159760/ah-spaghetti" },
                    },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["spaghetti"] = "Pasta bolognese",
                        ["rundergehakt"] = "Pasta bolognese",
                        ["passata of tomatenblokjes"] = "Pasta bolognese",
                        ["ui"] = "Pasta bolognese, Pasta tonijn",
                        ["knoflook"] = "Pasta bolognese",
                        ["wortel"] = "Pasta bolognese",
                        ["parmezaanse kaas"] = "Pasta bolognese, Pasta pesto",
                        ["sambal oelek"] = "Pasta bolognese",
                        ["gewone spaghetti (tarwe)"] = "Pasta bolognese",
                    }),
                "ahwinkel" => new AhWinkelForm(
                    new List<AhIngredient>
                    {
                        new()
                        {
                            Naam = "wortelen",
                            Url = "https://www.ah.be/producten/product/wi4076/ah-winterpeen",
                        },
                    },
                    new List<string> { "fishsticks", "melk" }),
                "ahagenda" => new AhAgendaForm(new List<(string, int)>
                {
                    ("Pokébowl met zalm", 20),
                    ("Rijst met kerrie en kip", 30),
                    ("Zelfgemaakte pizza", 35),
                }),
                "thema" => new ThemaProefForm(),
                "anticipeer" => new AnticipeerForm(),
                "wadiag" => new WhatsAppDiagnoseForm(),
                "owadiag" => new OutlookDiagnoseForm(),
                "verjaardagen" => new VerjaardagenForm(),
                "wmweb" => new WmWebForm(),
                "mailvenster" => new MailReplyForm(),
                "asana" => new AsanaSettingsForm(),
                "agenda" => new AgendaSettingsForm(),
                "instructies" => new InstructionsForm(),
                _ => new TeamTasksForm(),
            };
            venster.StartPosition = FormStartPosition.Manual;
            venster.Location = new Point(60, 60);
            if (args[1] == "fx" && venster is MijnTakenForm fx)
            {
                // Effectendemo: toast + confetti meteen tonen.
                fx.Shown += (_, _) =>
                {
                    Toast.Toon(fx, "Alles afgevinkt! 🎉", Fluent.Check);
                    Confetti.Vier(fx);
                };
            }
            Application.Run(venster);
            return;
        }

        using var mutex = new Mutex(true, @"Local\WorkManager.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        // Eerst het lettertype: SetColorMode maakt intern al een venster aan,
        // waarna SetDefaultFont niet meer mag.
        Application.SetDefaultFont(Theme.BaseFont);
#pragma warning disable WFO5001 // SetColorMode is experimenteel maar stabiel genoeg voor deze app
        // Volgt het gekozen kleurenschema: bij een licht palet horen ook de systeemdelen
        // (scrollbalken, dropdowns, titelbalk) licht te zijn.
        Application.SetColorMode(
            Theme.Palet.Donker ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        // Onze menurenderer als standaard: ook submenu's en losse menu's volgen dan het thema.
        Theme.ZetStandaardRenderer();
        Application.Run(new TrayAppContext());
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WorkManager", "crash-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n");
        }
        catch
        {
            // Zelfs loggen mag de app niet omleggen.
        }
    }

    private static readonly string HerstartMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "crash-herstarts.txt");

    /// <summary>
    /// Crash-loop-rem: maximaal drie automatische herstarts binnen tien minuten; daarna
    /// geeft de app het op (de logregels vertellen dan waarom).
    /// </summary>
    private static bool MagHerstartenNaCrash()
    {
        try
        {
            var grens = DateTimeOffset.Now.AddMinutes(-10);
            var recent = File.Exists(HerstartMarker)
                ? File.ReadAllLines(HerstartMarker)
                    .Where(r => DateTimeOffset.TryParse(r, out var t) && t >= grens)
                    .ToList()
                : new List<string>();
            if (recent.Count >= 3)
            {
                return false;
            }
            recent.Add(DateTimeOffset.Now.ToString("O"));
            File.WriteAllLines(HerstartMarker, recent);
            return true;
        }
        catch
        {
            return false; // twijfel = niet herstarten (geen risico op een loop)
        }
    }

    /// <summary>
    /// Start de app opnieuw op zodra dit proces weg is: via een kort wachtende cmd, zodat
    /// de single-instance-mutex eerst vrijkomt. Gebruikt door het crash-vangnet én de
    /// geplande nachtelijke herstart (geheugenbewaking).
    /// </summary>
    internal static void PlanHerstart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 4 /nobreak >nul & start \"\" \"{Application.ExecutablePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // Dan blijft de app gewoon weg tot de gebruiker hem zelf start.
        }
    }
}
