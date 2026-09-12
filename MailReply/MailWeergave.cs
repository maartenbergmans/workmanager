using System.Net;

namespace WorkManager;

/// <summary>
/// De html-weergave van een bericht (mail, chat, taak) in de ingebedde browsers van de
/// cockpit: witte kaart met kopregel, bijlagechips en eventueel een vertaalblok. Kwam uit het
/// vroegere mailvenster (MailReplyForm, verwijderd op 2026-09-12: de cockpit is de werkplek).
/// </summary>
internal static class MailWeergave
{
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
            {(mail.Van.Length == 0 && mail.VanAdres.Length == 0
                ? "" // taak zonder bronbericht: geen afzender, dus ook geen lege "<>"-regel
                : $"<div><b>{WebUtility.HtmlEncode(mail.Van)}</b> <span style=\"color:#5f6368\">&lt;{WebUtility.HtmlEncode(mail.VanAdres)}&gt;</span></div>")}
            {(mail.Aan.Count > 1
                ? $"<div style=\"color:#5f6368\">Aan: {WebUtility.HtmlEncode(string.Join("; ", mail.Aan))}</div>"
                : "")}
            {(mail.Cc.Count > 0
                ? $"<div style=\"color:#5f6368\">Cc: {WebUtility.HtmlEncode(string.Join("; ", mail.Cc))}</div>"
                : "")}
            {(mail.Datum == default
                ? "" // taak zonder bronbericht: "maandag 1 januari 0001" is alleen maar raar
                : $"<div style=\"color:#5f6368\">{mail.Datum.ToLocalTime():dddd d MMMM yyyy 'om' HH:mm}</div>")}
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
}
