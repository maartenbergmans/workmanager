using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WorkManager;

/// <summary>
/// Start Microsofts "Windows App" (package MicrosoftCorporationII.Windows365, de
/// AVD-client met werkruimte CED) en drijft de Microsoft-aanmelding via UI Automation:
/// accountkeuze of -wissel, e-mail, wachtwoord (uit <see cref="CedLogin"/> — hetzelfde
/// voor maarten.bergmans@ced.be en mber-admin@cedcloud.com) en de TOTP-code zodra er een
/// seed ingesteld is. De app is een ingebedde webpagina (deschutes.microsoft.com), dus
/// alles loopt via de accessibility-boom met naam-/id-patronen in NL en EN; in Chromium
/// is de UIA-AutomationId gelijk aan de DOM-id, vandaar de i0116/i0118/idSIButton9-ids
/// van het klassieke Microsoft-loginformulier. Elke stap komt in windowsapp-login-log.txt
/// zodat een gewijzigde opbouw bij te sturen is.
/// </summary>
public static class WindowsAppLogin
{
    private const string AppShellId =
        @"shell:AppsFolder\MicrosoftCorporationII.Windows365_8wekyb3d8bbwe!Windows365";

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "windowsapp-login-log.txt");

    /// <summary>
    /// Start (of activeert) de Windows App en meldt aan als <paramref name="email"/>.
    /// Draait op een eigen STA-thread (UIA-eis); geeft een statuszin terug voor de toast.
    /// MFA blijft handwerk zolang er geen TOTP-seed in de CED-login staat.
    /// </summary>
    public static Task<string> StartEnMeldAanAsync(string email, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Flow(email, ct));
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WindowsAppLogin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string Flow(string email, CancellationToken ct)
    {
        Log($"start — doelaccount {email}");
        if (VindHoofdvenster() is null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", AppShellId) { UseShellExecute = true });
        }
        var hoofd = WachtOpHoofdvenster(ct);
        if (hoofd is null)
        {
            return "Windows App kwam niet in beeld — start hem eens handmatig";
        }
        ZetVoorgrond(hoofd);

        // Toestand over de polls heen: elk scherm hooguit een paar keer bedienen, zodat
        // een hangend scherm geen invul-lus wordt (en zeker geen account-lockout).
        var emailIngevuld = 0;
        var wachtwoordIngevuld = 0;
        var laatsteOtp = "";
        var accountKnopGeprobeerd = false;
        var ooitActie = false;
        var dialoogHerstarts = 0;
        // Wordt gezet zodra een wachtwoordscherm een ánder account toont dan het doel:
        // dan stoppen we onmiddellijk (nooit een wachtwoord bij het verkeerde account).
        var verkeerdAccount = "";

        // Kort pollen (½ s) zodat elk nieuw scherm meteen bediend wordt; alle
        // wachtdrempels hieronder gaan daarom op de klok, niet op het aantal polls.
        var klok = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? dialoogSinds = null;
        var stilSinds = klok.Elapsed;

        while (klok.Elapsed < TimeSpan.FromMinutes(5)) // genoeg voor handmatige MFA
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(500);
            var actie = false;
            var kandidaten = KandidaatVensters();
            // De Microsoft-logindialoog in de Windows App is een BasicEmbeddedBrowser-
            // popup die zijn inhoud níét in de accessibility-boom zet. Daarvoor is er een
            // aparte route op basis van het gefocuste element (focus-events werken wél).
            var dialoog = VindLoginDialoog();
            if (dialoog is not null)
            {
                dialoogSinds ??= klok.Elapsed;
                var dialoogOpen = klok.Elapsed - dialoogSinds.Value;
                try
                {
                    // De eerste seconden laten laden: direct typen belandt in de
                    // "Een ogenblik geduld…"-spinner en gaat verloren.
                    if (dialoogOpen > TimeSpan.FromSeconds(3))
                    {
                        actie = HandelDialoogAf(dialoog, email,
                            blindToegestaan: dialoogOpen > TimeSpan.FromSeconds(6),
                            ref emailIngevuld, ref wachtwoordIngevuld, ref laatsteOtp,
                            ref verkeerdAccount);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Dialoog sloot net: volgende poll verder.
                }
                // Hangt de dialoog (de beruchte spinner die nooit doorlaadt, ~30 s zonder
                // ooit een veld): sluiten met Escape en nog één keer vers proberen via
                // het accountmenu. Maar niet als er intussen een echt CED-/Microsoft-
                // loginvenster openstaat (apart ApplicationFrameWindow) — dan is de
                // "spinner" gewoon de wachtende ouder en handelt HandelSchermAf het af.
                if (dialoogOpen > TimeSpan.FromSeconds(30) && dialoogHerstarts < 1 &&
                    !kandidaten.Skip(1).Any(HeeftLoginVeld))
                {
                    dialoogHerstarts++;
                    Log("dialoog hangt — Escape en opnieuw via het accountmenu");
                    if (VindHoofdvenster() is { } h)
                    {
                        ZetVoorgrond(h);
                        Thread.Sleep(300);
                        System.Windows.Forms.SendKeys.SendWait("{ESC}");
                    }
                    dialoogSinds = null;
                    emailIngevuld = 0;
                    accountKnopGeprobeerd = false;
                    ooitActie = false;
                    stilSinds = klok.Elapsed;
                    continue;
                }
            }
            else
            {
                dialoogSinds = null;
            }
            foreach (var venster in kandidaten)
            {
                try
                {
                    actie |= HandelSchermAf(venster, email,
                        ref emailIngevuld, ref wachtwoordIngevuld, ref laatsteOtp,
                        ref verkeerdAccount);
                }
                catch (ElementNotAvailableException)
                {
                    // Scherm verdween onder onze handen: volgende poll opnieuw kijken.
                }
                if (verkeerdAccount.Length > 0)
                {
                    Log($"gestopt — verkeerd account op scherm: {verkeerdAccount}");
                    return $"Windows App toont het account {verkeerdAccount} i.p.v. {email} — " +
                        "ik vul géén wachtwoord in. Kies eerst zelf het juiste account " +
                        "(of 'Ander account gebruiken'), dan neem ik het weer over";
                }
                if (wachtwoordIngevuld > 2)
                {
                    return "Wachtwoordscherm blijft terugkomen — wachtwoord geweigerd? " +
                        "Ik stop met invullen";
                }
            }
            if (verkeerdAccount.Length > 0)
            {
                Log($"gestopt — verkeerd account op scherm: {verkeerdAccount}");
                return $"Windows App toont het account {verkeerdAccount} i.p.v. {email} — " +
                    "ik vul géén wachtwoord in. Kies eerst zelf het juiste account " +
                    "(of 'Ander account gebruiken'), dan neem ik het weer over";
            }
            if (actie)
            {
                ooitActie = true;
                stilSinds = klok.Elapsed;
                continue;
            }
            var stil = klok.Elapsed - stilSinds;
            // Na een afgeronde aanmelding is het een tijdje stil: klaar.
            if (ooitActie && stil > TimeSpan.FromSeconds(9))
            {
                if (dialoog is not null)
                {
                    Log("dialoog blijft open zonder herkenbaar veld — handwerk gevraagd");
                    return "Windows App: e-mail is ingevuld, maar het vervolg kon ik niet " +
                        "veilig herkennen — maak de aanmelding even af in het venster";
                }
                Log("klaar — geen aanmeldschermen meer na acties");
                return $"Windows App aangemeld als {email}";
            }
            // Nooit een aanmeldscherm gezien: waarschijnlijk al aangemeld — maar met wélk
            // account? Eén keer de accountwisselaar proberen; die toont het menu met
            // accounts waar de klik op het doelaccount (of "account toevoegen") de rest
            // van deze lus weer werk geeft.
            if (!ooitActie && stil > TimeSpan.FromSeconds(6) && !accountKnopGeprobeerd &&
                dialoog is null)
            {
                accountKnopGeprobeerd = true;
                if (ProbeerAccountWissel(VindHoofdvenster(), email))
                {
                    ooitActie = true;
                    // Extra geduld: na de accountwissel kan de logindialoog ruim tien
                    // seconden op zich laten wachten — niet te vroeg "klaar" melden.
                    stilSinds = klok.Elapsed + TimeSpan.FromSeconds(12);
                }
                else
                {
                    Log("accountwisselaar niet gevonden");
                    return "Windows App staat open, maar ik vond de accountwisselaar " +
                        "niet — open het accountmenu handmatig, dan vul ik het " +
                        "aanmeldscherm verder in";
                }
            }
            if (!ooitActie && stil > TimeSpan.FromSeconds(30))
            {
                return "Windows App geopend — er verscheen geen aanmeldscherm " +
                    "(waarschijnlijk al aangemeld)";
            }
        }
        return "Windows App-aanmelding niet (op tijd) afgerond — zie windowsapp-login-log.txt";
    }

    /// <summary>
    /// De Microsoft-logindialoog van de Windows App (klasse BasicEmbeddedBrowser) als die
    /// openstaat; zijn inhoud is níét via de boom bereikbaar, alleen via focus.
    /// </summary>
    private static AutomationElement? VindLoginDialoog()
    {
        if (VindHoofdvenster() is not { } hoofd)
        {
            return null;
        }
        try
        {
            return hoofd.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "BasicEmbeddedBrowser"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bedient de logindialoog via het gefocuste element (de boom blijft leeg, maar
    /// focus-events geven het actieve veld wél prijs). Veiligheidsregels: er wordt nooit
    /// getypt als de voorgrond of de focus niet bij de Windows App hoort, en het
    /// wachtwoord alleen als het veld aantoonbaar een wachtwoordveld is (IsPassword) —
    /// blind typen zou het anders zichtbaar in een gewoon veld zetten.
    /// </summary>
    private static bool HandelDialoogAf(AutomationElement dialoog, string email,
        bool blindToegestaan,
        ref int emailIngevuld, ref int wachtwoordIngevuld, ref string laatsteOtp,
        ref string verkeerdAccount)
    {
        var pids = System.Diagnostics.Process.GetProcessesByName("Windows365")
            .Select(p => p.Id).ToHashSet();
        // Alleen naar de voorgrond halen (met bijbehorende wachttijd) als de app daar
        // niet al staat — meestal staat hij er al en scheelt dat 0,3 s per poll.
        if (!VoorgrondVanApp(pids) && VindHoofdvenster() is { } hoofd)
        {
            ZetVoorgrond(hoofd);
            Thread.Sleep(300);
        }
        if (!VoorgrondVanApp(pids))
        {
            Log("dialoog: voorgrond hoort niet bij de Windows App — niets getypt");
            return false;
        }
        AutomationElement? focus = null;
        try
        {
            focus = AutomationElement.FocusedElement;
        }
        catch
        {
            // Geen focusinfo: hieronder eventueel met Tab proberen.
        }
        var focusVanApp = focus is not null &&
            pids.Contains(Veilig(() => focus.Current.ProcessId));
        if (focusVanApp && Veilig(() => focus!.Current.ControlType) == ControlType.Edit)
        {
            var naam = Veilig(() => focus!.Current.Name) ?? "";
            var id = Veilig(() => focus!.Current.AutomationId) ?? "";
            if (Veilig(() => focus!.Current.IsPassword))
            {
                // Account verifiëren vóór het wachtwoord: het wachtwoordveld heet meestal
                // "Voer het wachtwoord voor <account> in". Alleen typen als dat aantoonbaar
                // het doelaccount is — staat er een ánder account, dan stoppen; kunnen we
                // het niet lezen, dan ook niet typen (nooit blind een wachtwoord).
                var opScherm = HaalEmail(naam);
                if (opScherm.Length > 0 &&
                    !opScherm.Equals(email, StringComparison.OrdinalIgnoreCase))
                {
                    verkeerdAccount = opScherm;
                    Log($"dialoog: wachtwoord NIET getypt — scherm toont {opScherm}, doel {email}");
                    return false;
                }
                if (opScherm.Length == 0)
                {
                    Log("dialoog: wachtwoordveld zonder leesbaar account — niet getypt (veiligheid)");
                    return false;
                }
                var wachtwoord = CedLogin.Wachtwoord();
                if (wachtwoord.Length == 0)
                {
                    Log("dialoog: wachtwoordveld, maar geen (bruikbaar) wachtwoord — handwerk");
                    return false;
                }
                if (wachtwoordIngevuld >= 2)
                {
                    return false;
                }
                wachtwoordIngevuld++;
                Log($"dialoog: wachtwoord getypt voor {opScherm} (poging {wachtwoordIngevuld})");
                TypMetEnter(wachtwoord);
                return true;
            }
            if (Regex.IsMatch(naam + " " + id, "code|otc", RegexOptions.IgnoreCase) &&
                CedLogin.TotpGeheim() is { Length: > 0 } seed)
            {
                var code = Totp.Genereer(seed);
                if (code == laatsteOtp)
                {
                    return false;
                }
                laatsteOtp = code;
                Log("dialoog: TOTP-code getypt");
                TypMetEnter(code);
                return true;
            }
            // E-mailveld: alleen zolang we nog aan de e-mailstap zijn (geen wachtwoord
            // getypt) én het veld echt naar een e-mail/gebruikersnaam vraagt. Anders zou
            // het na het wachtwoord het adres opnieuw in een verkeerd veld pompen — precies
            // wat de aanmelding eerder brak.
            var emailVeld = Regex.IsMatch(naam + " " + id,
                @"e-?mail|iemand@|someone@|gebruikersnaam|user ?name|i0116|loginfmt",
                RegexOptions.IgnoreCase);
            if (wachtwoordIngevuld == 0 && emailIngevuld < 2 && emailVeld)
            {
                emailIngevuld++;
                Log($"dialoog: e-mail getypt in veld '{naam}'");
                TypMetEnter(email);
                return true;
            }
            return false;
        }
        // Chromium geeft soms helemaal geen focusinfo prijs. De állereerste stap is dan
        // toch veilig blind te doen: het e-mailscherm heeft autofocus en een e-mailadres
        // is geen geheim. Maar ALLEEN als er echt geen focusinfo is (focus == null): staat
        // de focus wél op iets van de app dat géén invoerveld is, dan is dit vermoedelijk
        // een accountkeuzescherm ("Selecteer een account…"), en zou blind typen + Enter het
        // bovenste (verkeerde) account kiezen. Dan niets doen en de gebruiker laten kiezen.
        // Nooit ná het wachtwoord; het wachtwoord zelf wordt sowieso nooit blind getypt.
        if (focus is not null)
        {
            Log("dialoog: focus staat niet op een invoerveld (wsl. accountkeuze) — niets getypt");
            return false;
        }
        if (emailIngevuld == 0 && wachtwoordIngevuld == 0 && blindToegestaan)
        {
            emailIngevuld++;
            Log("dialoog: geen focusinfo — e-mail blind getypt (autofocusveld)");
            TypMetEnter(email);
            return true;
        }
        return false;
    }

    private static void TypMetEnter(string tekst)
    {
        System.Windows.Forms.SendKeys.SendWait("^a");
        System.Windows.Forms.SendKeys.SendWait(EscapeSendKeys(tekst));
        Thread.Sleep(150);
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
    }

    /// <summary>Eén poll over één venster; true zodra er iets bediend is.</summary>
    private static bool HandelSchermAf(AutomationElement venster, string email,
        ref int emailIngevuld, ref int wachtwoordIngevuld, ref string laatsteOtp,
        ref string verkeerdAccount)
    {
        var edits = Alle(venster, ControlType.Edit);
        var knoppen = Alle(venster, ControlType.Button);
        var teksten = Alle(venster, ControlType.Text);

        // Staat het te bedienen scherm in een apart loginvenster (niet het app-hoofd),
        // dan dat naar voren halen: de SendKeys-terugval van Vul/Klik/Enter typt naar het
        // actieve venster.
        if (edits.Count > 0 && Veilig(() => venster.Current.ClassName) != "MainWindow" &&
            !IsVoorgrond(venster))
        {
            ZetVoorgrond(venster);
            Thread.Sleep(300);
        }

        // Een echte wachtwoordfout: meteen stoppen met invullen (zelfde regel als de
        // WebView2-logins) — herhaalde foute pogingen zouden het account vergrendelen.
        if (teksten.Any(t => Regex.IsMatch(t.Current.Name ?? "",
                "onjuist|incorrect|verlopen|expired", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(t.Current.Name ?? "", "wachtwoord|password", RegexOptions.IgnoreCase)))
        {
            Log("fouttekst bij wachtwoord — invullen uitgezet (CedLogin.MarkeerGeweigerd)");
            CedLogin.MarkeerGeweigerd();
            return false;
        }

        // 1. "Aangemeld blijven?" → Ja.
        if (teksten.Any(t => Regex.IsMatch(t.Current.Name ?? "",
                "aangemeld blijven|blijf aangemeld|stay signed in", RegexOptions.IgnoreCase)))
        {
            var ja = ZoekKnop(knoppen, "^(ja|yes)$", "idSIButton9");
            if (ja is not null && Klik(ja))
            {
                Log("'aangemeld blijven' → ja");
                return true;
            }
        }

        // 2. TOTP-codeveld (alleen met ingestelde seed; elke code hooguit één keer).
        var otpVeld = ZoekEdit(edits, "code", "otc|OTC");
        if (otpVeld is not null && CedLogin.TotpGeheim() is { Length: > 0 } seed)
        {
            var code = Totp.Genereer(seed);
            if (code != laatsteOtp)
            {
                laatsteOtp = code;
                if (Vul(otpVeld, code))
                {
                    Log("TOTP-code ingevuld");
                    var verder = ZoekKnop(knoppen,
                        "verifi|controleren|volgende|next", "SAOTCC|idSIButton9");
                    if (verder is null || !Klik(verder))
                    {
                        Enter(otpVeld);
                    }
                    return true;
                }
            }
            return false;
        }

        // 3. MFA-methodelijst: kies de verificatiecode (nooit sms/bellen/e-mail).
        var codeOptie = teksten.Concat(knoppen).FirstOrDefault(e =>
            Regex.IsMatch(e.Current.Name ?? "", "verificatiecode|verification code",
                RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(e.Current.Name ?? "", @"sms|text|\bbel\b|call|e-?mail",
                RegexOptions.IgnoreCase));
        if (codeOptie is not null && edits.Count == 0 && Klik(codeOptie))
        {
            Log("MFA-methode: verificatiecode gekozen");
            return true;
        }

        // 4. Wachtwoordscherm.
        var wachtwoordVeld = edits.FirstOrDefault(e => Veilig(() => e.Current.IsPassword)) ??
            ZoekEdit(edits, "wachtwoord|password", "i0118|passwordEntry");
        if (wachtwoordVeld is not null)
        {
            // Account verifiëren vóór het wachtwoord: het veld heet meestal "Voer het
            // wachtwoord voor <account> in", anders staat het account als tekst op het
            // scherm. Alleen typen als dat het doelaccount is; een ander account → stoppen;
            // niet te lezen → ook niet typen (nooit blind een wachtwoord ergens invullen).
            var opScherm = AccountOpScherm(wachtwoordVeld, teksten);
            if (opScherm.Length > 0 &&
                !opScherm.Equals(email, StringComparison.OrdinalIgnoreCase))
            {
                verkeerdAccount = opScherm;
                Log($"wachtwoord NIET ingevuld — scherm toont {opScherm}, doel {email}");
                return false;
            }
            if (opScherm.Length == 0)
            {
                Log("wachtwoordscherm zonder leesbaar account — niet ingevuld (veiligheid)");
                return false;
            }
            var wachtwoord = CedLogin.Wachtwoord();
            if (wachtwoord.Length == 0)
            {
                Log("wachtwoord niet ingesteld of geweigerd — handwerk");
                return false;
            }
            wachtwoordIngevuld++;
            if (Vul(wachtwoordVeld, wachtwoord))
            {
                Log($"wachtwoord ingevuld voor {opScherm} (poging {wachtwoordIngevuld})");
                var aanmelden = ZoekKnop(knoppen,
                    "aanmelden|sign ?in|volgende|next", "idSIButton9");
                if (aanmelden is null || !Klik(aanmelden))
                {
                    Enter(wachtwoordVeld);
                }
                return true;
            }
            return false;
        }

        // 5. Accountkeuzescherm ("Kies een account"): het doelaccount aanklikken, en
        //    staat het er niet bij, dan "een ander account gebruiken".
        var accountItems = Alle(venster, ControlType.ListItem).Concat(knoppen).ToList();
        if (edits.Count == 0)
        {
            var doel = accountItems.FirstOrDefault(e =>
                (e.Current.Name ?? "").Contains(email, StringComparison.OrdinalIgnoreCase));
            var kiesScherm = teksten.Any(t => Regex.IsMatch(t.Current.Name ?? "",
                "kies een account|pick an account|account kiezen", RegexOptions.IgnoreCase));
            if (doel is not null && kiesScherm && Klik(doel))
            {
                Log($"accountkeuze: {email} aangeklikt");
                return true;
            }
            if (kiesScherm && doel is null)
            {
                var ander = accountItems.FirstOrDefault(e => Regex.IsMatch(
                    e.Current.Name ?? "", "ander account|another account",
                    RegexOptions.IgnoreCase));
                if (ander is not null && Klik(ander))
                {
                    Log("accountkeuze: 'ander account gebruiken'");
                    return true;
                }
            }
        }

        // 6. E-mailscherm (max. 3×, tegen een hangend formulier in).
        var emailVeld = ZoekEdit(edits,
            @"e-?mail|telefoon|phone|gebruikersnaam|user ?name|iemand@|someone@",
            "i0116|loginfmt|usernameEntry");
        if (emailVeld is not null && emailIngevuld < 3)
        {
            emailIngevuld++;
            if (Vul(emailVeld, email))
            {
                Log($"e-mail ingevuld: {email}");
                var volgende = ZoekKnop(knoppen, "volgende|next", "idSIButton9");
                if (volgende is null || !Klik(volgende))
                {
                    Enter(emailVeld);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Zoekt in de al-aangemelde app de accountwisselaar (avatar-knop met naam of
    /// e-mailadres) en kiest daarin het doelaccount of "account toevoegen".
    /// </summary>
    private static bool ProbeerAccountWissel(AutomationElement? hoofd, string email)
    {
        if (hoofd is null)
        {
            return false;
        }
        var knoppen = Alle(hoofd, ControlType.Button);
        var accountKnop = knoppen.FirstOrDefault(k =>
            (k.Current.Name ?? "").Contains('@')) ??
            knoppen.FirstOrDefault(k => Regex.IsMatch(k.Current.Name ?? "",
                "account|profiel|profile", RegexOptions.IgnoreCase));
        if (accountKnop is null || !Klik(accountKnop))
        {
            return false;
        }
        Log($"accountknop aangeklikt ('{accountKnop.Current.Name}')");
        Thread.Sleep(1500);
        var items = Alle(hoofd, ControlType.MenuItem)
            .Concat(Alle(hoofd, ControlType.ListItem))
            .Concat(Alle(hoofd, ControlType.Button)).ToList();
        var doel = items.FirstOrDefault(e =>
            (e.Current.Name ?? "").Contains(email, StringComparison.OrdinalIgnoreCase));
        if (doel is not null && Klik(doel))
        {
            Log($"accountmenu: {email} gekozen");
            return true;
        }
        var toevoegen = items.FirstOrDefault(e => Regex.IsMatch(e.Current.Name ?? "",
            "account toevoegen|add account|ander account|another account",
            RegexOptions.IgnoreCase));
        if (toevoegen is not null && Klik(toevoegen))
        {
            Log("accountmenu: 'account toevoegen' gekozen");
            return true;
        }
        return false;
    }

    // ── UIA-hulpjes ─────────────────────────────────────────────────────────────

    /// <summary>Het eerste e-mailadres in de tekst (of "" als er geen in staat).</summary>
    private static string HaalEmail(string? tekst)
    {
        var m = Regex.Match(tekst ?? "",
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}");
        return m.Success ? m.Value : "";
    }

    /// <summary>
    /// Welk account het wachtwoordscherm toont: eerst de naam van het wachtwoordveld
    /// ("Voer het wachtwoord voor X in"), anders het account als losse tekst op het
    /// scherm. "" = niet te bepalen (dan wordt er uit voorzorg geen wachtwoord getypt).
    /// </summary>
    private static string AccountOpScherm(
        AutomationElement wachtwoordVeld, List<AutomationElement> teksten)
    {
        var uitVeld = HaalEmail(Veilig(() => wachtwoordVeld.Current.Name));
        if (uitVeld.Length > 0)
        {
            return uitVeld;
        }
        foreach (var t in teksten)
        {
            var em = HaalEmail(Veilig(() => t.Current.Name));
            if (em.Length > 0)
            {
                return em;
            }
        }
        return "";
    }

    private static AutomationElement? VindHoofdvenster()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Windows365"))
        {
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    return AutomationElement.FromHandle(p.MainWindowHandle);
                }
                catch
                {
                    // Venster net weg: volgende proces.
                }
            }
        }
        return null;
    }

    private static AutomationElement? WachtOpHoofdvenster(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (VindHoofdvenster() is { } venster)
            {
                return venster;
            }
            Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>
    /// Alle vensters waar een aanmeldscherm in kan zitten: het app-venster zelf plus
    /// losse Microsoft-/CED-logindialogen. Die laatste openen als een apart
    /// ApplicationFrameWindow (proces explorer/broker) met vaak een lege titel, dus we
    /// herkennen ze aan de inhoud: een wachtwoordveld of het klassieke i0116/i0118-veld.
    /// </summary>
    private static List<AutomationElement> KandidaatVensters()
    {
        var vensters = new List<AutomationElement>();
        if (VindHoofdvenster() is { } hoofd)
        {
            vensters.Add(hoofd);
        }
        try
        {
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(
                TreeScope.Children, Condition.TrueCondition))
            {
                var naam = Veilig(() => top.Current.Name) ?? "";
                if (Regex.IsMatch(naam, "aanmelden bij|sign in to your account",
                        RegexOptions.IgnoreCase))
                {
                    vensters.Add(top);
                    continue;
                }
                // De inhoudscheck (HeeftLoginVeld) doorzoekt álle descendants en is bij
                // grote vensters (Chrome, Explorer) tergend traag; alleen doen bij de
                // venstertypes waarin zo'n login echt opent.
                var klasse = Veilig(() => top.Current.ClassName) ?? "";
                if ((klasse == "ApplicationFrameWindow" ||
                        klasse.Contains("Credential", StringComparison.OrdinalIgnoreCase)) &&
                    HeeftLoginVeld(top))
                {
                    vensters.Add(top);
                }
            }
        }
        catch
        {
            // Desktopscan is best effort; het hoofdvenster hebben we al.
        }
        return vensters;
    }

    /// <summary>True als het venster een wachtwoordveld of het i0116/i0118-loginveld bevat.</summary>
    private static bool HeeftLoginVeld(AutomationElement venster)
    {
        try
        {
            if (venster.FindFirst(TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.IsPasswordProperty, true)) is not null)
            {
                return true;
            }
            foreach (var id in new[] { "i0116", "i0118" })
            {
                if (venster.FindFirst(TreeScope.Descendants, new PropertyCondition(
                        AutomationElement.AutomationIdProperty, id)) is not null)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Venster net weg of niet doorzoekbaar.
        }
        return false;
    }

    private static List<AutomationElement> Alle(AutomationElement bron, ControlType type)
    {
        var lijst = new List<AutomationElement>();
        try
        {
            foreach (AutomationElement e in bron.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
            {
                lijst.Add(e);
            }
        }
        catch
        {
            // Venster verdween tijdens het zoeken.
        }
        return lijst;
    }

    private static AutomationElement? ZoekEdit(
        List<AutomationElement> edits, string naamPatroon, string idPatroon) =>
        edits.FirstOrDefault(e =>
            Regex.IsMatch(Veilig(() => e.Current.AutomationId) ?? "", idPatroon,
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(Veilig(() => e.Current.Name) ?? "", naamPatroon,
                RegexOptions.IgnoreCase));

    private static AutomationElement? ZoekKnop(
        List<AutomationElement> knoppen, string naamPatroon, string idPatroon) =>
        knoppen.FirstOrDefault(k =>
            Regex.IsMatch(Veilig(() => k.Current.AutomationId) ?? "", idPatroon,
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(Veilig(() => k.Current.Name) ?? "", naamPatroon,
                RegexOptions.IgnoreCase));

    private static bool Vul(AutomationElement veld, string tekst)
    {
        try
        {
            if (veld.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
            {
                ((ValuePattern)p).SetValue(tekst);
                return true;
            }
        }
        catch
        {
            // Chromium weigert SetValue geregeld (zeker op wachtwoordvelden): dan typen.
        }
        try
        {
            veld.SetFocus();
            Thread.Sleep(200);
            System.Windows.Forms.SendKeys.SendWait("^a");
            System.Windows.Forms.SendKeys.SendWait(EscapeSendKeys(tekst));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Enter(AutomationElement veld)
    {
        try
        {
            veld.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }
        catch
        {
            // Dan wacht de volgende poll op de submitknop.
        }
    }

    private static bool Klik(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
            {
                ((InvokePattern)p).Invoke();
                return true;
            }
        }
        catch
        {
            // Door naar de volgende strategie.
        }
        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s))
            {
                ((SelectionItemPattern)s).Select();
                return true;
            }
        }
        catch
        {
            // Door naar de volgende strategie.
        }
        try
        {
            el.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>SendKeys geeft {}+^%~() een eigen betekenis; letterlijk maken.</summary>
    private static string EscapeSendKeys(string tekst)
    {
        var sb = new StringBuilder(tekst.Length + 8);
        foreach (var c in tekst)
        {
            sb.Append(c is '{' or '}' or '+' or '^' or '%' or '~' or '(' or ')' or '[' or ']'
                ? $"{{{c}}}" : c.ToString());
        }
        return sb.ToString();
    }

    /// <summary>True als het voorgrondvenster bij een van de app-processen hoort.</summary>
    private static bool VoorgrondVanApp(HashSet<int> pids)
    {
        var voorgrond = NativeMethods.GetForegroundWindow();
        var pid = 0;
        _ = NativeMethods.GetWindowThreadProcessId(voorgrond, ref pid);
        return pids.Contains(pid);
    }

    private static bool IsVoorgrond(AutomationElement venster) =>
        Veilig(() => (IntPtr)venster.Current.NativeWindowHandle) ==
            NativeMethods.GetForegroundWindow();

    private static void ZetVoorgrond(AutomationElement venster)
    {
        try
        {
            NativeMethods.SetForegroundWindow((IntPtr)venster.Current.NativeWindowHandle);
        }
        catch
        {
            // Niet erg: UIA-acties werken ook zonder voorgrond.
        }
    }

    private static T? Veilig<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch
        {
            return default;
        }
    }

    private static void Log(string regel)
    {
        try
        {
            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {regel}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref int processId);
    }
}
