using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Vult Microsoft-aanmeldschermen (login.microsoftonline.com en het ADFS-scherm van CED)
/// automatisch in met de centrale <see cref="CedLogin"/>: e-mailadres, wachtwoord en de
/// "Aangemeld blijven?"-vraag (ja). MFA blijft handwerk. Het wachtwoord wordt per pagina
/// maar één keer geprobeerd; meldt het scherm een fout wachtwoord, dan stopt het invullen
/// blijvend (zie <see cref="CedLogin.MarkeerGeweigerd"/>) en verschijnt er één melding —
/// daarna is handmatig inloggen aan zet tot er een nieuw wachtwoord bewaard is.
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
}
