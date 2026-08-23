using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De gedeelde SD Worx-login-assistent: het invulscript voor de loginpagina's van
/// auth.sdworx.com (stap 1 e-mail, stap 2 wachtwoord, privacybanner weigeren) plus de
/// bijbehorende statusteksten. Gebruikt door de teamkalender (<see cref="VakantiesForm"/>)
/// en het verlofportaal (<see cref="SdWorxPortaalForm"/>). Een veld wordt maar één keer
/// gevuld en gesubmit ('…-wacht' daarna), zodat een haperende pagina nooit tot herhaalde
/// loginpogingen leidt (accountblokkering vermijden). MFA blijft handwerk in het venster.
/// </summary>
public static class SdWorxLogin
{
    /// <summary>Staat de browser op een SD Worx-loginpagina?</summary>
    public static bool IsLoginUrl(string url) =>
        url.Contains("auth.sdworx.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Het invulscript met de inloggegevens erin (JSON-veilig ge-escaped).</summary>
    public static string Script(string gebruiker, string wachtwoord) => LoginScript
        .Replace("__USER__", JsonSerializer.Serialize(gebruiker))
        .Replace("__PASS__", JsonSerializer.Serialize(wachtwoord));

    /// <summary>Statusregel bij het resultaat van <see cref="Script"/> (met aanhalingstekens).</summary>
    public static string StatusTekst(string? resultaat) => resultaat switch
    {
        "\"cookies\"" => "Privacybanner geweigerd…",
        "\"gebruiker\"" => "E-mailadres ingevuld…",
        "\"gebruiker-wacht\"" => "Wachten op de wachtwoordstap…",
        "\"wachtwoord\"" => "Wachtwoord ingevuld — aanmelden…",
        "\"wachtwoord-wacht\"" => "Aangemeld — wachten op SD Worx…",
        _ => "Inloggen bij SD Worx…",
    };

    // Exact afgestemd op de SD Worx-loginpagina's (auth.sdworx.com, geverifieerd via de
    // inspectiemodus van VakantiesForm): stap 1 = #lp_login-email + #lp_next, stap 2 =
    // #lp_login-password + #lp_next.
    private const string LoginScript =
        """
        (() => {
            // Privacybanner (in shadow DOM) eerst weigeren.
            const zoekOveral = selector => {
                const uit = [];
                const loop = root => {
                    uit.push(...root.querySelectorAll(selector));
                    root.querySelectorAll('*').forEach(el => {
                        if (el.shadowRoot) loop(el.shadowRoot);
                    });
                };
                loop(document);
                return uit;
            };
            const weiger = zoekOveral('button, a')
                .find(e => /^(alles )?weigeren$|^(refuse|decline|reject)( all)?$/i
                    .test((e.innerText || '').trim()));
            if (weiger) { weiger.click(); return 'cookies'; }

            const zet = (el, v) => {
                const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                s.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const klikVolgende = () => setTimeout(() => {
                const k = document.querySelector('#lp_next');
                if (k && !k.disabled) k.click();
            }, 400);

            const wachtwoord = document.querySelector('#lp_login-password');
            if (wachtwoord && wachtwoord.offsetParent !== null) {
                if (wachtwoord.value === __PASS__) return 'wachtwoord-wacht';
                zet(wachtwoord, __PASS__);
                klikVolgende();
                return 'wachtwoord';
            }
            const email = document.querySelector('#lp_login-email');
            if (email && email.offsetParent !== null) {
                if (email.value === __USER__) return 'gebruiker-wacht';
                zet(email, __USER__);
                klikVolgende();
                return 'gebruiker';
            }
            return null;
        })()
        """;
}
