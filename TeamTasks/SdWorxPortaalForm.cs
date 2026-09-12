using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Opent het SD Worx-portaal (myworkandme.com) in een ingebedde browser en keurt de
/// openstaande verlofaanvragen automatisch goed: na de automatische login (zelfde
/// browserprofiel en login-assistent als de teamkalender) navigeert het venster zelf
/// naar "Te behandelen aanvragen", opent elke aanvraag en klikt op Goedkeuren, tot de
/// lijst leeg is. Strandt de flow ergens (MFA, onverwacht scherm), dan stopt hij met een
/// duidelijke status en werk je gewoon handmatig verder in het venster. Het openen dooft
/// het verlofsignaal van de cockpit (geopend = opgepakt).
/// </summary>
public class SdWorxPortaalForm : Form
{
    // De eBlox HR-app zelf (niet de publieke landingspagina): zonder sessie dwingt die
    // meteen de redirect naar auth.sdworx.com af, zodat de login-assistent kan invullen.
    private const string PortaalUrl = "https://www.myworkandme.com/ebloxhr/hrwwevo/#/";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly SdWorxSettings _settings = SdWorxSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _alleenInspecteren;
    private bool _bezig;
    private bool _ingelogd;

    public SdWorxPortaalForm(bool alleenInspecteren = false)
    {
        _alleenInspecteren = alleenInspecteren;
        Text = "Verlof goedkeuren – SD Worx";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1200, 800);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        _status.Padding = new Padding(4, 14, 0, 0);
        toolbar.Controls.Add(_status);

        Controls.Add(_web);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) =>
        {
            if (!_alleenInspecteren)
            {
                WerkSignaal.Zet("sdworx", false);
            }
            await InitWebViewAsync();
        };
        Theme.Apply(this, fade: false); // WebView2 rendert niet in een gelaagd venster
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "sdworx-portaal");
        _web.DefaultBackgroundColor = Theme.Bg;
    }

    private void Status(string tekst)
    {
        if (!IsDisposed)
        {
            _status.Text = tekst;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _pulse.Actief = true;
            Status("Browser starten…");
            // Zelfde profielmap als de teamkalender: één blijvende SD Worx-sessie voor beide.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-sdworx"));
            await _web.EnsureCoreWebView2Async(env);
            try
            {
                _web.CoreWebView2.IsMuted = true; // WorkManager is stil
            }
            catch
            {
                // Oudere runtime zonder IsMuted: dan blijft alles werken zoals voorheen.
            }

            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    // Toch (opnieuw) op de loginpagina beland — bv. sessie net verlopen:
                    // de assistent weer aan het werk zetten.
                    if (SdWorxLogin.IsLoginUrl(_web.Source?.ToString() ?? ""))
                    {
                        _ingelogd = false;
                    }
                    await ProbeerLoginAsync();
                }
            };

            Status("Naar het SD Worx-portaal…");
            _web.CoreWebView2.Navigate(PortaalUrl);
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Browser starten mislukt: {ex.Message}");
        }
    }

    /// <summary>
    /// Drijft de loginflow met de gedeelde SD Worx-assistent, in een lus omdat de login
    /// een SPA zonder navigatie-events tussen de stappen is. Stopt zodra de browser terug
    /// op myworkandme.com staat; MFA blijft handwerk in het venster.
    /// </summary>
    private async Task ProbeerLoginAsync()
    {
        if (IsDisposed || _bezig || _ingelogd)
        {
            return;
        }
        _bezig = true;
        try
        {
            var script = _settings.Gebruiker.Length > 0 && _settings.Wachtwoord.Length > 0
                ? SdWorxLogin.Script(_settings.Gebruiker, _settings.Wachtwoord)
                : null;
            var wachtwoordWachtrondes = 0;
            var stabielOpPortaal = 0;
            for (var poging = 0; poging < 75; poging++)
            {
                var url = _web.Source?.ToString() ?? "";
                if (url.Contains("myworkandme.com", StringComparison.OrdinalIgnoreCase) &&
                    !SdWorxLogin.IsLoginUrl(url))
                {
                    // Pas na een paar stabiele rondes "ingelogd" concluderen: zonder sessie
                    // stuurt de eBlox-app pas ná het laden door naar auth.sdworx.com.
                    if (++stabielOpPortaal >= 3)
                    {
                        _ingelogd = true;
                        await NaIngelogdAsync();
                        return;
                    }
                    await Task.Delay(1200, _cts.Token);
                    continue;
                }
                stabielOpPortaal = 0;
                if (SdWorxLogin.IsLoginUrl(url))
                {
                    if (script is null)
                    {
                        _pulse.Actief = false;
                        Status("Geen SD Worx-inloggegevens gevonden — log handmatig in.");
                        return;
                    }
                    var resultaat = await RunScriptAsync(script);
                    Status(SdWorxLogin.StatusTekst(resultaat));
                    if (resultaat is "\"wachtwoord-wacht\"" && ++wachtwoordWachtrondes > 15)
                    {
                        // Wachtwoord is één keer gesubmit maar het portaal komt niet: MFA of
                        // een foutmelding. Bewust niet opnieuw (accountblokkering vermijden).
                        _pulse.Actief = false;
                        Status("Aangemeld maar het portaal verschijnt niet (MFA of foutmelding?) — " +
                               "werk handmatig verder in het venster.");
                        return;
                    }
                }
                await Task.Delay(1200, _cts.Token);
            }
            _pulse.Actief = false;
            Status("Automatisch inloggen lukte niet — log handmatig in.");
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _pulse.Actief = false;
            Status($"Inloggen mislukt: {ex.Message}");
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Na een geslaagde login: inspecteren (diagnosemodus) of goedkeuren.</summary>
    private async Task NaIngelogdAsync()
    {
        if (_alleenInspecteren)
        {
            await InspecteerAsync();
            return;
        }
        await KeurAanvragenGoedAsync();
    }

    // ---------- Automatisch goedkeuren ----------

    /// <summary>
    /// Behandelt alle openstaande verlofaanvragen: naar "Te behandelen aanvragen", per
    /// aanvraag het detail openen en op Goedkeuren klikken, tot de lijst leeg is. Strandt
    /// de flow ergens (onverwacht scherm, knop ontbreekt), dan stopt hij met een duidelijke
    /// status en werk je gewoon handmatig verder in het venster.
    /// </summary>
    private async Task KeurAanvragenGoedAsync()
    {
        var goedgekeurd = new List<string>();
        var vorigeRijen = int.MaxValue;
        for (var ronde = 0; ronde < 15; ronde++)
        {
            Status("Naar de te behandelen aanvragen…");
            await RunScriptAsync(KlikNaarAanvragenScript);

            int? rijen = null;
            for (var i = 0; i < 20 && rijen is null; i++)
            {
                await Task.Delay(1000, _cts.Token);
                var raw = await RunScriptAsync(TelAanvragenScript);
                rijen = raw is null or "null" ? null : int.Parse(raw);
            }
            if (rijen is null)
            {
                Strand("De aanvragenlijst kwam niet tevoorschijn", goedgekeurd);
                return;
            }
            if (rijen == 0)
            {
                Klaar(goedgekeurd);
                return;
            }
            if (rijen >= vorigeRijen)
            {
                // De lijst wordt niet korter: het goedkeuren komt blijkbaar niet door.
                // Stoppen in plaats van dezelfde aanvraag te blijven aanklikken.
                Strand("De lijst wordt niet korter na het goedkeuren", goedgekeurd);
                return;
            }
            vorigeRijen = rijen.Value;

            Status(rijen == 1
                ? "Nog 1 aanvraag — openen…"
                : $"Nog {rijen} aanvragen — de eerste openen…");
            await RunScriptAsync(KlikOpEersteAanvraagScript);

            string? naam = null;
            var knop = false;
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(1000, _cts.Token);
                var raw = await RunScriptAsync(LeesDetailScript);
                if (raw is not null and not "null")
                {
                    using var doc = JsonDocument.Parse(raw);
                    naam = doc.RootElement.GetProperty("naam").GetString();
                    knop = doc.RootElement.GetProperty("knop").GetBoolean();
                    break;
                }
            }
            if (naam is null)
            {
                Strand("De aanvraag opende niet", goedgekeurd);
                return;
            }
            if (!knop)
            {
                Strand($"Geen Goedkeuren-knop bij de aanvraag van {naam}", goedgekeurd);
                return;
            }

            Status($"Aanvraag van {naam} goedkeuren…");
            if (await RunScriptAsync(KlikGoedkeurenScript) is null or "null")
            {
                Strand($"Goedkeuren van {naam} lukte niet", goedgekeurd);
                return;
            }
            // Wachten tot het portaal de goedkeuring verwerkt heeft (de knop verdwijnt);
            // een eventuele bevestigingsvraag wordt onderweg beantwoord.
            var verwerkt = false;
            for (var i = 0; i < 15 && !verwerkt; i++)
            {
                await Task.Delay(1000, _cts.Token);
                await RunScriptAsync(BevestigScript);
                verwerkt = await RunScriptAsync(DetailAfgehandeldScript) is "true";
            }
            if (!verwerkt)
            {
                Strand($"De goedkeuring van {naam} lijkt niet door te komen", goedgekeurd);
                return;
            }
            goedgekeurd.Add(naam);
        }
        Klaar(goedgekeurd);
    }

    private void Klaar(List<string> goedgekeurd)
    {
        _pulse.Actief = false;
        if (goedgekeurd.Count == 0)
        {
            Status("Geen aanvragen om te behandelen.");
            Log("klaar: geen aanvragen om te behandelen");
            return;
        }
        Status(goedgekeurd.Count == 1
            ? $"Klaar — aanvraag van {goedgekeurd[0]} goedgekeurd."
            : $"Klaar — {goedgekeurd.Count} aanvragen goedgekeurd: {string.Join(", ", goedgekeurd)}.");
        Log($"klaar: {goedgekeurd.Count} goedgekeurd ({string.Join(", ", goedgekeurd)})");
        Toast.Toon(this, goedgekeurd.Count == 1
            ? $"Verlof van {goedgekeurd[0]} goedgekeurd ✔"
            : $"{goedgekeurd.Count} verlofaanvragen goedgekeurd ✔", Fluent.Check);
    }

    private void Strand(string reden, List<string> goedgekeurd)
    {
        _pulse.Actief = false;
        var al = goedgekeurd.Count > 0
            ? $" (al goedgekeurd: {string.Join(", ", goedgekeurd)})"
            : "";
        Status($"{reden} — werk handmatig verder in het venster{al}.");
        Log($"gestrand: {reden}{al}");
    }

    // Kort logboek van elke goedkeurronde, om achteraf te zien wat er gebeurd is.
    private static void Log(string tekst)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "sdworx-goedkeur-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {tekst}\r\n");
        }
        catch
        {
            // Logboek is nice-to-have; nooit de flow laten stranden op een schrijffout.
        }
    }

    // ---------- Diagnosemodus ----------

    /// <summary>
    /// Diagnosemodus (--venster verlofdump): legt de structuur van het portaal vast om de
    /// automatische goedkeuring op af te stemmen. Dumpt elke DOM-wijziging naar
    /// %APPDATA%\WorkManager\sdworx-portaal-dump-N.json en probeert na een paar stabiele
    /// dumps zelf naar het scherm met te behandelen aanvragen door te klikken.
    /// </summary>
    private async Task InspecteerAsync()
    {
        Status("Inspectiemodus — structuur van het portaal vastleggen…");
        var vorige = "";
        var nummer = 0;
        var stabiel = 0;
        var klikfase = 0;
        for (var ronde = 0; ronde < 60; ronde++)
        {
            var dump = await RunScriptAsync(DumpScript);
            if (!string.IsNullOrEmpty(dump) && dump != "null")
            {
                if (dump != vorige)
                {
                    vorige = dump;
                    stabiel = 0;
                    File.WriteAllText(
                        Path.Combine(DataDir, $"sdworx-portaal-dump-{++nummer}.json"),
                        JsonSerializer.Deserialize<JsonElement>(dump).GetRawText());
                    Status($"Inspectie — snapshot {nummer} vastgelegd…");
                }
                else if (++stabiel == 3 && klikfase < 2)
                {
                    // De pagina staat stil: doorklikken naar de volgende stap — eerst het
                    // scherm met te behandelen aanvragen, daarna de eerste aanvraag zelf —
                    // zodat de volgende snapshots dat scherm laten zien.
                    var script = ++klikfase == 1 ? KlikNaarAanvragenScript : KlikOpEersteAanvraagScript;
                    var klik = await RunScriptAsync(script);
                    File.AppendAllText(
                        Path.Combine(DataDir, "sdworx-portaal-klik.txt"),
                        $"fase {klikfase}: {klik}\r\n");
                    Status($"Inspectie — doorgeklikt (fase {klikfase}): {klik}");
                }
            }
            await Task.Delay(1500, _cts.Token);
        }
        _pulse.Actief = false;
        Status($"Inspectie klaar — {nummer} snapshots weggeschreven.");
    }

    // Beschrijft de pagina zonder iets te wijzigen: alle links (met hash-route), knoppen
    // en klikbare elementen (knockout data-bind), plus de zichtbare tekst — genoeg om de
    // route naar de aanvragen en de goedkeurknoppen te vinden. Kijkt ook in (same-origin)
    // iframes: de nieuwe schermen (r=ng) laden als sub-app in een iframe.
    private const string DumpScript =
        """
        (() => {
            const tekst = e => ((e.innerText || e.value || '') + '').trim()
                .replace(/\s+/g, ' ').slice(0, 80);
            const uit = { url: location.href, frames: [], links: [], klikbaar: [], body: '' };
            const loop = (doc, frame) => {
                doc.querySelectorAll('a[href]').forEach(a => uit.links.push({
                    frame, tekst: tekst(a), href: a.getAttribute('href') || '',
                    id: a.id || '', zichtbaar: a.offsetParent !== null,
                }));
                doc.querySelectorAll(
                    'button, input[type=submit], [role=button], [data-bind*="click"]'
                ).forEach(e => uit.klikbaar.push({
                    frame, tag: e.tagName, tekst: tekst(e), id: e.id || '',
                    bind: (e.getAttribute('data-bind') || '').slice(0, 120),
                    cls: (e.className || '') + '', zichtbaar: e.offsetParent !== null,
                    disabled: !!e.disabled,
                }));
                uit.body += (frame ? '\n===== frame ' + frame + ' =====\n' : '') +
                    (doc.body.innerText || '').replace(/\n{2,}/g, '\n').slice(0, 4000);
                doc.querySelectorAll('iframe').forEach((fr, i) => {
                    const naam = (frame ? frame + '/' : '') + (fr.id || fr.name || i);
                    try {
                        if (fr.contentDocument) {
                            uit.frames.push({ naam, src: fr.src || '', bereikbaar: true });
                            loop(fr.contentDocument, naam);
                        } else {
                            uit.frames.push({ naam, src: fr.src || '', bereikbaar: false });
                        }
                    } catch (e) {
                        uit.frames.push({ naam, src: fr.src || '', bereikbaar: false });
                    }
                });
            };
            loop(document, '');
            return uit;
        })()
        """;

    // Klikt op het dashboard door naar de te behandelen aanvragen: eerst de widget-link
    // "N te behandelen afwezigheidsaanvraag/-aanvragen" (knockout RedirectToPage), anders
    // het menu-item "Te behandelen aanvragen". Geeft null terug als er niets te doen is.
    private const string KlikNaarAanvragenScript =
        """
        (() => {
            const tekst = e => ((e.innerText || '') + '').trim().replace(/\s+/g, ' ');
            const widget = [...document.querySelectorAll('a')]
                .find(a => a.offsetParent !== null &&
                    /te behandelen .*aanvra/i.test(tekst(a)));
            if (widget) {
                widget.click();
                return 'widget: "' + tekst(widget).slice(0, 60) + '"';
            }
            const menu = [...document.querySelectorAll('a')]
                .find(a => /^te behandelen aanvragen$/i.test(tekst(a)));
            if (menu) {
                menu.click();
                return 'menu: "' + tekst(menu).slice(0, 60) + '"';
            }
            return null;
        })()
        """;

    // Telt de zichtbare aanvraagrijen in het aanvragen-iframe (de periode-links in het
    // grid). Geeft null zolang de lijst nog niet geladen/zichtbaar is; 0 zodra het grid er
    // staat zonder rijen. Let op: een verborgen iframe telt niet mee (innerText van
    // verborgen elementen bevat ook stylesheet-tekst en is onbruikbaar).
    private const string TelAanvragenScript =
        """
        (() => {
            for (const fr of document.querySelectorAll('iframe')) {
                if (fr.offsetParent === null) continue;
                let doc = null;
                try { doc = fr.contentDocument; } catch (e) { }
                if (!doc || !doc.body) continue;
                const rijen = [...doc.querySelectorAll('a')].filter(a =>
                    a.offsetParent !== null &&
                    /^[a-z]{2}\s+\d{1,2}\s+\w+\.?\s+\d{4}/i.test((a.innerText || '').trim()));
                if (rijen.length > 0) return rijen.length;
                const body = doc.body.innerText || '';
                if (/loading/i.test(body)) continue;
                // Lege lijst: "Geen aanvragen beschikbaar" (de kolomkoppen verdwijnen dan).
                if (/NAAM|PERIODE|geen rijen|geen aanvragen|no rows|no requests/i.test(body)) return 0;
            }
            return null;
        })()
        """;

    // Leest de geopende aanvraag: de naam uit de kop "Afwezigheidsaanvraag voor …" en of
    // de Goedkeuren-knop (knockout approveRequest) zichtbaar en bruikbaar is.
    private const string LeesDetailScript =
        """
        (() => {
            const kop = [...document.querySelectorAll('h1, h2, h3, h4, legend, span, div')]
                .filter(e => e.offsetParent !== null)
                .map(e => ((e.innerText || '') + '').trim())
                .map(t => t.match(/^Afwezigheidsaanvraag voor (.{2,60})$/i))
                .find(m => m);
            if (!kop) return null;
            const knop = [...document.querySelectorAll('button')].find(b =>
                b.offsetParent !== null && !b.disabled &&
                /goedkeuren/i.test((b.innerText || '').trim()) &&
                (b.getAttribute('data-bind') || '').includes('approveRequest'));
            return { naam: kop[1].trim(), knop: !!knop };
        })()
        """;

    // Klikt de Goedkeuren-knop van de geopende aanvraag aan.
    private const string KlikGoedkeurenScript =
        """
        (() => {
            const knop = [...document.querySelectorAll('button')].find(b =>
                b.offsetParent !== null && !b.disabled &&
                /goedkeuren/i.test((b.innerText || '').trim()) &&
                (b.getAttribute('data-bind') || '').includes('approveRequest'));
            if (!knop) return null;
            knop.click();
            return 'geklikt';
        })()
        """;

    // Beantwoordt een eventuele bevestigingsvraag na het goedkeuren: alleen in een
    // zichtbare dialoog die over goedkeuren/bevestigen gaat, de primaire knop aanklikken.
    private const string BevestigScript =
        """
        (() => {
            const dialoog = [...document.querySelectorAll('.modal, [role=dialog]')]
                .find(d => d.offsetParent !== null &&
                    /goedkeur|bevestig/i.test((d.innerText || '')));
            if (!dialoog) return null;
            const knop = [...dialoog.querySelectorAll('button, a.btn')].find(b =>
                b.offsetParent !== null && !b.disabled &&
                (/^(ok|ja|bevestigen|goedkeuren)$/i.test((b.innerText || '').trim()) ||
                 b.classList.contains('btn-primary')));
            if (!knop) return null;
            knop.click();
            return 'bevestigd';
        })()
        """;

    // Is de goedkeuring verwerkt? Zodra de Goedkeuren-knop niet meer zichtbaar is
    // (showApprove wordt false, of het portaal is terug naar de lijst), zijn we klaar.
    private const string DetailAfgehandeldScript =
        """
        (() => {
            const knop = [...document.querySelectorAll('button')].find(b =>
                b.offsetParent !== null &&
                /goedkeuren/i.test((b.innerText || '').trim()) &&
                (b.getAttribute('data-bind') || '').includes('approveRequest'));
            return !knop;
        })()
        """;

    // Opent in het aanvragen-iframe de eerste aanvraagrij (de periode-link in het grid).
    private const string KlikOpEersteAanvraagScript =
        """
        (() => {
            for (const fr of document.querySelectorAll('iframe')) {
                let doc = null;
                try { doc = fr.contentDocument; } catch (e) { }
                if (!doc) continue;
                const link = [...doc.querySelectorAll('a')].find(a =>
                    a.offsetParent !== null &&
                    /^[a-z]{2}\s+\d{1,2}\s+\w+\.?\s+\d{4}/i.test((a.innerText || '').trim()));
                if (link) {
                    link.click();
                    return 'aanvraag: "' + (link.innerText || '').trim().slice(0, 60) + '"';
                }
            }
            return null;
        })()
        """;

    private async Task<string?> RunScriptAsync(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null)
        {
            return null;
        }
        try
        {
            return await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            return null;
        }
    }
}
