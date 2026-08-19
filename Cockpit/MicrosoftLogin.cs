using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Vult Microsoft-aanmeldschermen (login.microsoftonline.com en het ADFS-scherm van CED)
/// automatisch in met de centrale <see cref="CedLogin"/>: e-mailadres, wachtwoord en de
/// "Aangemeld blijven?"-vraag (ja). Op het MFA-scherm klikt de assistent van de standaard
/// push-goedkeuring ("keur goed in de Authenticator-app") door naar het codeinvoerscherm,
/// want Maarten gebruikt WinOTP (codes); de code zelf blijft handwerk — er wordt nooit een
/// 2FA-geheim uitgelezen. Het wachtwoord wordt per pagina maar één keer geprobeerd; meldt
/// het scherm een fout wachtwoord, dan stopt het invullen blijvend (zie
/// <see cref="CedLogin.MarkeerGeweigerd"/>) en verschijnt er één melding — daarna is
/// handmatig inloggen aan zet tot er een nieuw wachtwoord bewaard is.
/// </summary>
public static class MicrosoftLogin
{
    private static bool _gemeld;

    /// <summary>
    /// Script voor in de loginpagina. Resultaat: "fout" (wachtwoord geweigerd), "wachtwoord",
    /// "email" of "ja" (iets ingevuld/aangeklikt), "wacht" (al geprobeerd), "niets".
    /// </summary>
    public static string VulScript()
    {
        var email = JsonSerializer.Serialize(CedLogin.Email);
        var wachtwoord = JsonSerializer.Serialize(CedLogin.Wachtwoord());
        return $$"""
            (() => {
                // Fouttekst bij het wachtwoordveld = geweigerd: meteen stoppen met invullen.
                const foutEl = document.querySelector('#passwordError, #errorText');
                if (foutEl && foutEl.textContent.trim().length > 0) { return 'fout'; }
                const zichtbaar = e => e && e.offsetParent !== null && !e.disabled;
                const vuur = (el) => {
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                };
                const pw = [...document.querySelectorAll(
                    'input[type=password], #i0118, #passwordInput')].find(zichtbaar);
                const geheim = {{wachtwoord}};
                if (pw && geheim.length > 0) {
                    // Eén poging per pagina: bij een fout wachtwoord blijft het veld staan
                    // en zou de assistent anders pogingen blijven opstapelen (lockout!).
                    if (window.__wmPwIngevuld) { return 'wacht'; }
                    window.__wmPwIngevuld = true;
                    pw.value = geheim;
                    vuur(pw);
                    document.querySelector('#idSIButton9, #submitButton, input[type=submit]')
                        ?.click();
                    return 'wachtwoord';
                }
                const mail = [...document.querySelectorAll(
                    'input[type=email], #i0116, #userNameInput')].find(zichtbaar);
                if (mail && !mail.value) {
                    mail.value = {{email}};
                    vuur(mail);
                    document.querySelector('#idSIButton9, #nextButton, input[type=submit]')
                        ?.click();
                    return 'email';
                }
                // "Aangemeld blijven?" → Ja: dat scheelt heraanmeldingen. Alleen op het echte
                // KMSI-scherm (herkenbaar aan de checkbox), nooit blind ergens op klikken.
                if (document.querySelector('#KmsiCheckboxField') &&
                    document.querySelector('#idSIButton9')) {
                    document.querySelector('#idSIButton9').click();
                    return 'ja';
                }
                // MFA: Microsoft opent standaard op "keur de aanmelding goed in de Authenticator-
                // app" (push). Maarten gebruikt WinOTP, dat codes maakt — dus doorschakelen naar
                // het codeinvoerscherm. De code zelf blijft handwerk (nooit een geheim uitlezen).
                const tekstKlik = (patroon, verbod) => {
                    const el = [...document.querySelectorAll(
                        'a, [role=button], div[role=listitem], .table, .tile, .row')]
                        .find(e => {
                            if (!zichtbaar(e)) { return false; }
                            const t = (e.textContent || '').trim().toLowerCase();
                            return t.length > 0 && t.length < 140 && patroon.test(t) &&
                                (!verbod || !verbod.test(t));
                        });
                    if (el) { el.click(); return true; }
                    return false;
                };
                // 1. Staat het codeveld er al? Dan is het scherm klaar en typt Maarten de code.
                const otc = document.querySelector(
                    '#idTxtBx_SAOTCC_OTC, input[name=otc], input[autocomplete="one-time-code"]');
                if (otc && zichtbaar(otc)) {
                    if (!window.__wmOtcFocus) { window.__wmOtcFocus = true; otc.focus(); }
                    return 'code-klaar';
                }
                // 2. Toont Microsoft de lijst met methoden, kies dan de authenticator-code
                //    (niet sms/telefoon/e-mail — die willen we bewust niet).
                if (tekstKlik(/verification code|verificatiecode|code from|code uit/,
                              /text|sms|\bcall\b|\bbel\b|phone|telefoon|email|e-mail/)) {
                    return 'mfa-code-methode';
                }
                // 3. Nog op het push-scherm: doorklikken naar "andere manier" om die lijst te
                //    openen. Eerst de bekende Microsoft-links, anders op tekst.
                const anders = document.querySelector(
                    '#idA_SAOTCC_SwitchToList, #signInAnotherWay, #idA_PWD_SwitchToCredPicker');
                if (anders && zichtbaar(anders)) { anders.click(); return 'mfa-anders'; }
                if (tekstKlik(
                        /another way|andere manier|can.?t use|niet gebruiken|different method|andere verificatie/,
                        null)) {
                    return 'mfa-anders';
                }
                return 'niets';
            })()
            """;
    }

    /// <summary>Interpreteert het scriptresultaat; een geweigerd wachtwoord meldt één keer.</summary>
    public static void Verwerk(string jsResultaat)
    {
        if (jsResultaat != "\"fout\"" || _gemeld)
        {
            return;
        }
        _gemeld = true;
        CedLogin.MarkeerGeweigerd();
        TrayMelding.Toon("CED-wachtwoord geweigerd",
            "Microsoft weigerde het bewaarde wachtwoord — log deze keer handmatig in. " +
            "Automatisch invullen staat uit tot er een nieuw wachtwoord bewaard is " +
            "(vraag Claude om het bij te werken).", duurMs: 15000);
    }

    private static string _laatsteGekopieerdeCode = "";

    /// <summary>
    /// Verwerkt één login-stap én zet, zodra het codeinvoerscherm klaar staat en er een
    /// TOTP-seed ingesteld is, de actuele code op het klembord — zodat Maarten enkel nog
    /// hoeft te plakken. Zonder seed blijft de code volledig handwerk. Herkopieert netjes
    /// wanneer de code na 30 s doorrolt.
    /// </summary>
    public static void NaLoginStap(string jsResultaat, Form? eigenaar)
    {
        Verwerk(jsResultaat);
        if (jsResultaat != "\"code-klaar\"")
        {
            _laatsteGekopieerdeCode = "";
            return;
        }
        var geheim = CedLogin.TotpGeheim();
        if (geheim.Length == 0)
        {
            return; // geen seed bewaard: de code blijft handwerk
        }
        var code = Totp.Genereer(geheim);
        if (code.Length == 0 || code == _laatsteGekopieerdeCode)
        {
            return; // ongeldige seed, of deze code al gekopieerd
        }
        _laatsteGekopieerdeCode = code;
        try
        {
            Clipboard.SetText(code);
        }
        catch
        {
            return; // klembord even bezet: volgende ronde opnieuw
        }
        var melding = $"MFA-code {code} gekopieerd — plak met Ctrl+V ({Totp.SecondenGeldig()} s geldig)";
        if (eigenaar is { IsDisposed: false })
        {
            Toast.Toon(eigenaar, melding, "🔐");
        }
        else
        {
            // Verborgen sessies (Outlook/Teams zonder eigen zichtbaar venster): tray-ballon.
            TrayMelding.Toon("MFA-code op het klembord", melding, duurMs: 20000);
        }
    }
}
