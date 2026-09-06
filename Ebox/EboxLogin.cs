using System.Text.Json;

namespace WorkManager;

/// <summary>
/// De CSAM-login-assistent voor e-Box Enterprise: kiest de digitale sleutel
/// "beveiligingscode via mobiele app", vult gebruikersnaam + wachtwoord in en daarna de
/// lokaal berekende TOTP-code. Zelfde aanpak als <see cref="SdWorxLogin"/>: een veld wordt
/// maar één keer met dezelfde waarde gevuld en gesubmit ('…-wacht' daarna), zodat een
/// haperende pagina nooit tot herhaalde loginpogingen leidt. De selectors zijn bewust
/// ruim (tekst-matching): CSAM/FAS wisselt weleens van opmaak.
/// </summary>
public static class EboxLogin
{
    /// <summary>Staat de browser op een CSAM-aanmeldpagina? e-Box Enterprise gebruikt de
    /// IdP van de sociale zekerheid (login.socialsecurity.be, "WALIBIS"), maar de andere
    /// CSAM-domeinen vangen we voor de zekerheid ook op.</summary>
    public static bool IsLoginUrl(string url) =>
        url.Contains("login.socialsecurity.be", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("iamfas.belgium.be", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("iamapps.belgium.be", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("csam.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Staat de browser op de publieke e-Box-website? Zonder sessie stuurt de app daarheen
    /// (taalkeuze → homepage → "e-Box openen" → "Verdergaan naar CSAM"); die tussenstappen
    /// klikt <see cref="SiteScript"/> weg.
    /// </summary>
    public static bool IsSiteUrl(string url) =>
        url.Contains("www.eboxenterprise.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>Klikt op de publieke site door richting de CSAM-login.</summary>
    public const string SiteScript =
        """
        (() => {
            const zichtbaar = el => el && el.offsetParent !== null;
            const knoppen = [...document.querySelectorAll('button, a')];
            const tekst = el => (el.innerText || '').replace(/\s+/g, ' ').trim();
            const csam = knoppen.find(e => zichtbaar(e) && /verdergaan naar csam/i.test(tekst(e)));
            if (csam) { csam.click(); return 'csam'; }
            const open = knoppen.find(e => zichtbaar(e) && /e-?box openen/i.test(tekst(e)));
            if (open) { open.click(); return 'openen'; }
            const taal = knoppen.find(e => zichtbaar(e) && /^nederlands$/i.test(tekst(e)));
            if (taal) { taal.click(); return 'taal'; }
            return null;
        })()
        """;

    /// <summary>Het invulscript met de gegevens erin (JSON-veilig ge-escaped).</summary>
    public static string Script(string gebruiker, string wachtwoord, string totpCode) => LoginScript
        .Replace("__USER__", JsonSerializer.Serialize(gebruiker))
        .Replace("__PASS__", JsonSerializer.Serialize(wachtwoord))
        .Replace("__CODE__", JsonSerializer.Serialize(totpCode));

    /// <summary>Statusregel bij het resultaat van <see cref="Script"/> (met aanhalingstekens).</summary>
    public static string StatusTekst(string? resultaat) => resultaat switch
    {
        "\"taal\"" => "Taal gekozen (Nederlands)…",
        "\"openen\"" => "e-Box openen aangeklikt…",
        "\"csam\"" => "Doorverwijzing naar CSAM bevestigd…",
        "\"cookies\"" => "Cookiebanner weggeklikt…",
        "\"doelpubliek\"" => "Doelpubliek gekozen: onderneming…",
        "\"csam-route\"" => "Naar de CSAM-aanmelding…",
        "\"onderneming\"" => "Onderneming gekozen: BerMaCon…",
        "\"methode\"" => "Digitale sleutel gekozen: beveiligingscode via mobiele app…",
        "\"login\"" => "Gebruikersnaam en wachtwoord ingevuld — aanmelden…",
        "\"login-wacht\"" => "Aangemeld — wachten op de beveiligingscode-stap…",
        "\"totp\"" => "Beveiligingscode ingevuld — bevestigen…",
        "\"totp-wacht\"" => "Wachten tot CSAM de code aanvaardt…",
        _ => "Inloggen bij CSAM…",
    };

    private const string LoginScript =
        """
        (() => {
            const zichtbaar = el => el && el.offsetParent !== null;
            const tekstVan = el => ((el.innerText || el.value || '') + ' ' +
                (el.getAttribute && (el.getAttribute('aria-label') || ''))).replace(/\s+/g, ' ').trim();
            const alleKnoppen = () => [...document.querySelectorAll('button, a, input[type=submit]')];
            const zet = (el, v) => {
                const s = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                s.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const submit = () => setTimeout(() => {
                const k = document.querySelector('#loginButton_0') ||
                    alleKnoppen().find(e => zichtbaar(e) &&
                        /aanmelden|inloggen|volgende|verder|bevestig|log ?in|next|continue|submit/i
                            .test(tekstVan(e))) ||
                    document.querySelector('button[type=submit], input[type=submit]');
                if (k) k.click();
            }, 400);

            // Cookiebanner: zo privacyvriendelijk mogelijk sluiten (weigeren/alleen noodzakelijke).
            const cookie = alleKnoppen().find(e => zichtbaar(e) &&
                /^(alles |alle cookies )?weigeren$|alleen (strikt )?noodzakelijke|refuse|decline|reject/i
                    .test(tekstVan(e)));
            if (cookie) { cookie.click(); return 'cookies'; }

            const wachtwoordVeld = [...document.querySelectorAll('input[type=password]')].find(zichtbaar);

            // Stap 1a: doelpubliek kiezen — het uman-account is een ondernemingslogin.
            if (!wachtwoordVeld) {
                const doelpubliek = document.querySelector('a[data-usertype="uman"]') ||
                    alleKnoppen().find(e => zichtbaar(e) && /^onderneming$/i.test(tekstVan(e)));
                if (doelpubliek) { doelpubliek.click(); return 'doelpubliek'; }
                // Manier-van-aanmelden-pagina van login.socialsecurity.be: de route via CSAM
                // nemen — daar zit "gebruikersnaam/wachtwoord met code via mobiele app" achter.
                // (De "Toegangscodes"-modal ernaast is de variant zónder beveiligingscode.)
                const fas = document.querySelector('#connect_fas');
                if (fas && zichtbaar(fas)) { fas.click(); return 'csam-route'; }
                // Na de login: de onderneming kiezen. Bij voorkeur BerMaCon (op tekst in de
                // omliggende blokken); entiteiten zonder toegang hebben geen keuzelink.
                const bedrijven = [...document.querySelectorAll('a[href*="selectEnterprise"]')]
                    .filter(zichtbaar);
                if (bedrijven.length) {
                    const buurt = a => {
                        let el = a, t = '';
                        for (let i = 0; i < 3 && el.parentElement; i++) {
                            el = el.parentElement;
                            t = el.innerText || t;
                        }
                        return t;
                    };
                    (bedrijven.find(a => /bermacon/i.test(buurt(a))) || bedrijven[0]).click();
                    return 'onderneming';
                }
            }

            // Stap 1b: digitale sleutel kiezen (alleen als er nog geen loginformulier staat).
            if (!wachtwoordVeld) {
                const sleutel = alleKnoppen().find(e => zichtbaar(e) &&
                    /beveiligingscode.*mobiele? app|security code.*mobile app|code de s.curit.*application mobile/i
                        .test(tekstVan(e)));
                if (sleutel) { sleutel.click(); return 'methode'; }
            }

            // Stap 2: gebruikersnaam + wachtwoord.
            if (wachtwoordVeld) {
                if (wachtwoordVeld.value === __PASS__) return 'login-wacht';
                const gebruikerVeld = [...document.querySelectorAll(
                    'input[type=text], input[type=email], input:not([type])')].find(zichtbaar);
                if (gebruikerVeld && gebruikerVeld.value !== __USER__) zet(gebruikerVeld, __USER__);
                zet(wachtwoordVeld, __PASS__);
                submit();
                return 'login';
            }

            // Stap 3: de beveiligingscode (TOTP). Alleen invullen als de pagina er echt om
            // vraagt — een willekeurig tekstveld op een andere pagina laten we met rust.
            const paginaTekst = document.body ? document.body.innerText : '';
            if (/beveiligingscode|verificatiecode|security code|code de s.curit|one.?time|totp/i
                    .test(paginaTekst)) {
                const codeVeld = [...document.querySelectorAll(
                    'input[type=text], input[type=tel], input[type=number], input:not([type])')]
                    .find(zichtbaar);
                if (codeVeld) {
                    if (codeVeld.value === __CODE__) return 'totp-wacht';
                    zet(codeVeld, __CODE__);
                    submit();
                    return 'totp';
                }
            }
            return null;
        })()
        """;
}
