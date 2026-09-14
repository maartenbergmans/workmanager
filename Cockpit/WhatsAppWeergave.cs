using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Rendert een WhatsApp-gesprek als HTML dat er uitziet als WhatsApp Web zelf: kop met foto,
/// naam en deelnemers; datumchips ("Vandaag", "vrijdag", "4/9/2026"); bubbels met staartje
/// bij het eerste bericht van een reeks; in groepen een avatar en de gekleurde afzender
/// ("~ profielnaam" + nummer, groepslabel eronder); tijd en vinkjes rechtsonder; citaten,
/// linkvoorbeelden, albums, polls, oproepen en reacties. Volgt het kleurenschema van de app
/// (licht/donker, zoals WhatsApp Web zelf ook een dark mode heeft). Het detailpaneel draait
/// zonder scripts, dus alles is pure HTML/CSS (avatars één keer als CSS-klasse, niet per bericht).
/// </summary>
internal static class WhatsAppWeergave
{
    /// <summary>Diagnose: donkere weergave afdwingen, los van het actieve kleurenschema.</summary>
    internal static bool? DonkerOverride { get; set; }

    private static bool Donker => DonkerOverride ?? Theme.Palet.Donker;

    private const string Css =
        """
        .wa{--bg:#efeae2;--kop-bg:#f0f2f5;--kop-rand:#e9edef;--tekst:#111b21;--sub:#667781;
            --in:#fff;--uit:#d9fdd3;--chip-bg:#fff;--chip-tekst:#54656f;--cit-bg:rgba(11,20,26,.05);
            --link:#027eb5;--tint:rgba(11,20,26,.07);--reac-bg:#fff;--av-bg:#dfe5e7;--groen:#1daa61;
            --schaduw:0 1px .5px rgba(11,20,26,.13);
            font-family:"Segoe UI","Helvetica Neue",Helvetica,Arial,"Segoe UI Emoji",sans-serif;
            color:var(--tekst);background:var(--bg);margin:-16px;border-radius:0 0 12px 12px;overflow:hidden}
        .wa.donker{--bg:#0b141a;--kop-bg:#202c33;--kop-rand:#2a3942;--tekst:#e9edef;--sub:#8696a0;
            --in:#202c33;--uit:#005c4b;--chip-bg:#182229;--chip-tekst:#8696a0;--cit-bg:rgba(255,255,255,.06);
            --link:#53bdeb;--tint:rgba(255,255,255,.09);--reac-bg:#202c33;--av-bg:#2a3942;--groen:#00a884;
            --schaduw:0 1px .5px rgba(0,0,0,.35)}
        .wa-kop{background:var(--kop-bg);padding:10px 16px;display:flex;align-items:center;gap:13px;
            border-bottom:1px solid var(--kop-rand);flex:none}
        .wa-kop-foto{width:40px;height:40px;border-radius:50%;object-fit:cover;flex:none}
        .wa-kop-leeg{width:40px;height:40px;border-radius:50%;flex:none;display:flex;
            align-items:center;justify-content:center}
        .wa-kop-tekst{min-width:0}
        .wa-kop-naam{font-size:16px;line-height:21px;color:var(--tekst);white-space:nowrap;overflow:hidden;
            text-overflow:ellipsis}
        .wa-kop-sub{font-size:13px;line-height:19px;color:var(--sub);white-space:nowrap;overflow:hidden;
            text-overflow:ellipsis}
        .wa-scroll{background:var(--bg);display:flex;flex-direction:column-reverse;overflow-y:auto}
        .wa-scroll::-webkit-scrollbar{width:6px}
        .wa-scroll::-webkit-scrollbar-thumb{background:rgba(134,150,160,.4);border-radius:3px}
        .wa-lijst{padding:6px 5% 14px;display:flex;flex-direction:column}
        .wa-dag,.wa-sys{align-self:center;background:var(--chip-bg);color:var(--chip-tekst);font-size:12.5px;
            line-height:21px;padding:5px 12px 6px;border-radius:7.5px;box-shadow:var(--schaduw)}
        .wa-dag{margin:12px 0 8px}
        .wa-sys{margin:8px 0 4px;max-width:85%;text-align:center}
        .wa-sys.ongelezen{align-self:stretch;text-align:center;color:var(--groen);text-transform:uppercase;
            letter-spacing:.3px;font-size:12px;margin:10px 0 6px}
        .wa-rij{display:flex;align-items:flex-start;margin-top:2px}
        .wa-rij.eerste{margin-top:10px}
        .wa-rij.uit{justify-content:flex-end}
        .wa-av{width:28px;height:28px;border-radius:50%;flex:none;margin-right:8px;
            background:var(--av-bg) center/cover no-repeat;display:flex;align-items:center;
            justify-content:center;font-size:11.5px;font-weight:600;letter-spacing:.2px}
        .wa-av-plek{width:28px;flex:none;margin-right:8px}
        .wa-kol{display:flex;flex-direction:column;align-items:flex-start;max-width:76%;min-width:0}
        .uit .wa-kol{align-items:flex-end}
        .wa-b{position:relative;background:var(--in);border-radius:7.5px;padding:3px;
            box-shadow:var(--schaduw);font-size:14.2px;line-height:19px;
            max-width:100%;min-width:70px;box-sizing:border-box}
        .uit .wa-b{background:var(--uit)}
        .in .wa-b.staart{border-top-left-radius:0}
        .uit .wa-b.staart{border-top-right-radius:0}
        .wa-b.staart::before{content:"";position:absolute;top:0;width:8px;height:13px;background:inherit}
        .in .wa-b.staart::before{left:-8px;clip-path:polygon(0 0,100% 0,100% 100%)}
        .uit .wa-b.staart::before{right:-8px;clip-path:polygon(0 0,100% 0,0 100%)}
        .wa-s{padding:3px 5px 0 6px}
        .wa-naam{display:flex;align-items:baseline;font-size:12.8px;line-height:19px;font-weight:500;
            padding:3px 5px 0 6px}
        .wa-naam .n{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .wa-naam .nr{margin-left:auto;padding-left:16px;color:var(--sub);font-weight:400;white-space:nowrap}
        .wa-label{font-size:12px;line-height:16px;color:var(--sub);padding:0 5px 1px 6px}
        .wa-fwd{font-size:13px;line-height:18px;font-style:italic;color:var(--sub);padding:3px 5px 0 6px;
            display:flex;align-items:center;gap:4px}
        .wa-t{white-space:pre-wrap;overflow-wrap:anywhere;padding:3px 5px 6px 6px}
        .wa-t a{color:var(--link);text-decoration:underline}
        .wa-t code{font-family:Consolas,monospace;font-size:13px;background:var(--tint);
            border-radius:3px;padding:0 3px}
        .wa-groot{font-size:34px;line-height:42px}
        .wa-ruimte{display:inline-block;height:1px;vertical-align:bottom}
        .wa-voet{height:17px}
        .wa-meta{position:absolute;right:8px;bottom:5px;font-size:11px;line-height:15px;color:var(--sub);
            white-space:nowrap;display:flex;align-items:center;gap:3px;cursor:default}
        .wa-meta.op{color:#fff;right:9px;bottom:8px;text-shadow:0 1px 2px rgba(0,0,0,.5)}
        .wa-meta.pil{background:rgba(11,20,26,.45);color:#fff;border-radius:9px;padding:0 6px}
        .wa-meta svg{display:block}
        .wa-cit{display:flex;background:var(--cit-bg);border-radius:6px;border-left:4px solid #06cf9c;
            margin:3px 3px 1px;font-size:13.2px;line-height:18px;color:var(--sub);overflow:hidden}
        .wa-cit .ct{flex:1;min-width:0;padding:5px 8px 6px}
        .wa-cit .w{font-weight:500;font-size:12.8px;line-height:19px}
        .wa-cit .q{display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}
        .wa-cit img{width:58px;min-height:58px;object-fit:cover;flex:none}
        .wa-foto{display:block;width:330px;max-width:100%;max-height:360px;object-fit:cover;border-radius:5px}
        .wa-media{position:relative}
        .wa-media::after{content:"";position:absolute;left:0;right:0;bottom:0;height:34px;border-radius:0 0 5px 5px;
            background:linear-gradient(transparent,rgba(0,0,0,.28));pointer-events:none}
        .wa-album{display:grid;grid-template-columns:1fr 1fr;gap:3px;width:330px;max-width:100%}
        .wa-album div{position:relative}
        .wa-album img{display:block;width:100%;aspect-ratio:1;object-fit:cover;border-radius:4px}
        .wa-album .meer{position:absolute;inset:0;background:rgba(11,20,26,.45);color:#fff;border-radius:4px;
            display:flex;align-items:center;justify-content:center;font-size:26px}
        .wa-play{position:absolute;left:50%;top:50%;width:48px;height:48px;margin:-24px 0 0 -24px;
            border-radius:50%;background:rgba(11,20,26,.45);display:flex;align-items:center;justify-content:center}
        .wa-duur{position:absolute;left:9px;bottom:8px;color:#fff;font-size:11px;z-index:1;
            text-shadow:0 1px 2px rgba(0,0,0,.5)}
        .wa-sticker{background:transparent!important;box-shadow:none!important}
        .wa-sticker::before{display:none}
        .wa-sticker img{display:block;width:150px;height:150px;object-fit:contain}
        .wa-link{display:flex;background:var(--cit-bg);border-radius:6px;overflow:hidden;margin:0 0 2px}
        .wa-link.groot{flex-direction:column}
        .wa-link img{width:88px;min-height:88px;object-fit:cover;flex:none}
        .wa-link.groot img{width:100%;max-height:200px;min-height:0}
        .wa-link .lt{padding:6px 10px 8px;min-width:0}
        .wa-link .t{font-size:13.5px;line-height:19px;color:var(--tekst);font-weight:500;display:-webkit-box;
            -webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
        .wa-link .o{font-size:12.5px;line-height:17px;color:var(--sub);display:-webkit-box;
            -webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;margin-top:2px}
        .wa-link .d{font-size:12.5px;line-height:17px;color:#8696a0;margin-top:3px;white-space:nowrap;
            overflow:hidden;text-overflow:ellipsis}
        .wa-kaart{display:flex;align-items:center;gap:10px;background:var(--cit-bg);border-radius:6px;
            padding:9px 10px;margin:3px 3px 0;min-width:220px}
        .wa-kaart .ico{width:36px;height:36px;border-radius:50%;background:var(--tint);flex:none;
            display:flex;align-items:center;justify-content:center;font-size:17px}
        .wa-kaart .k1{font-size:14px;line-height:19px;color:var(--tekst)}
        .wa-kaart .k2{font-size:12.5px;line-height:17px;color:var(--sub)}
        .wa-golf{flex:1;height:18px;min-width:120px;opacity:.55;
            background:repeating-linear-gradient(90deg,#8696a0 0 2px,transparent 2px 5px);
            -webkit-mask:linear-gradient(0deg,transparent 20%,#000 20% 80%,transparent 80%)}
        .wa-verw{color:var(--sub);font-style:italic;display:flex;align-items:center;gap:5px}
        .wa-poll{padding:4px 7px 0 8px;min-width:250px}
        .wa-poll .vraag{font-weight:600;font-size:14.5px;line-height:20px}
        .wa-poll .sub{font-size:12.5px;color:var(--sub);margin:3px 0 6px}
        .wa-poll .opt{display:flex;gap:10px;align-items:flex-start;margin:9px 0 10px}
        .wa-poll .vink{width:17px;height:17px;border-radius:50%;border:2px solid #8696a0;flex:none;
            box-sizing:border-box;margin-top:1px;display:flex;align-items:center;justify-content:center}
        .wa-poll .vink.aan{background:var(--groen);border-color:var(--groen)}
        .wa-poll .optk{flex:1;min-width:0}
        .wa-poll .optr{display:flex;justify-content:space-between;gap:10px;font-size:14px;line-height:19px}
        .wa-poll .n{color:var(--sub)}
        .wa-poll .balk{height:6px;border-radius:3px;background:var(--tint);margin-top:6px;overflow:hidden}
        .wa-poll .balk div{height:100%;background:var(--groen);border-radius:3px}
        .wa-reac{position:relative;z-index:1;margin:-5px 0 2px 8px;background:var(--reac-bg);border-radius:12px;
            padding:1px 7px;box-shadow:0 1px 3px rgba(11,20,26,.18);font-size:13px;line-height:20px;
            white-space:nowrap;color:var(--tekst)}
        .uit .wa-reac{margin:-5px 8px 2px 0}
        """;

    /// <summary>
    /// Behang-tegel zoals WhatsApps doodle-achtergrond: losse lijntekeningetjes, heel licht,
    /// zodat de bubbels erbovenop liggen en niet op een vlakke plaat. {K} = lijnkleur.
    /// </summary>
    private const string Behang =
        "<svg xmlns='http://www.w3.org/2000/svg' width='240' height='240' viewBox='0 0 240 240' fill='none' " +
        "stroke='{K}' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'>" +
        "<circle cx='34' cy='36' r='10'/>" +
        "<path d='M96 22l3.7 7.5 8.3 1.2-6 5.8 1.4 8.2-7.4-3.9-7.4 3.9 1.4-8.2-6-5.8 8.3-1.2z'/>" +
        "<path d='M176 34c-3.5-7-13-5-13 2.5 0 6.5 13 14 13 14s13-7.5 13-14c0-7.5-9.5-9.5-13-2.5z'/>" +
        "<path d='M22 104q11-11 22 0t22 0'/>" +
        "<rect x='150' y='92' width='20' height='20' rx='5' transform='rotate(14 160 102)'/>" +
        "<path d='M110 90v14M103 97h14'/>" +
        "<path d='M60 168l11 20H49z'/>" +
        "<circle cx='200' cy='166' r='5.5'/>" +
        "<path d='M126 158a8 8 0 1 0 16 0a8 8 0 1 0-16 0M134 150v-6'/>" +
        "<path d='M28 214q9-7 18 0'/>" +
        "<path d='M186 212l6-6 6 6-6 6z'/>" +
        "<path d='M96 220c4-8 12-8 16 0'/>" +
        "</svg>";

    /// <summary>Naamkleuren zoals WhatsApp ze toekent (terugval voor oude cache zonder kleur).</summary>
    private static readonly string[] Naamkleuren =
    [
        "rgb(184, 5, 49)", "rgb(2, 131, 119)", "rgb(27, 135, 85)", "rgb(157, 108, 44)",
        "rgb(94, 71, 222)", "rgb(2, 126, 181)", "rgb(0, 99, 203)", "rgb(133, 85, 56)",
        "rgb(212, 42, 102)",
    ];

    /// <summary>Pastel achtergrond + pictogramkleur voor een kop zonder profielfoto.</summary>
    private static readonly (string Bg, string Fg)[] KopKleuren =
    [
        ("#fde2e8", "#c2255c"), ("#ffefd6", "#b35c00"), ("#dcf5e7", "#1b8755"),
        ("#e1eaff", "#3b5bdb"), ("#efe3ff", "#7048e8"), ("#ffe6db", "#d0501d"),
    ];

    private const string GroepIcoon =
        "<svg viewBox=\"0 0 24 24\" width=\"22\" height=\"22\"><path fill=\"currentColor\" d=\"M16 11c1.66 0 " +
        "2.99-1.34 2.99-3S17.66 5 16 5s-3 1.34-3 3 1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5 5 " +
        "6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02" +
        "-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z\"/></svg>";

    private const string PersoonIcoon =
        "<svg viewBox=\"0 0 24 24\" width=\"22\" height=\"22\"><path fill=\"currentColor\" d=\"M12 12c2.21 0 " +
        "4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z\"/></svg>";

    private const string DoorstuurIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"14\" height=\"14\"><path fill=\"currentColor\" d=\"M9.2 3v2.6C4.6 " +
        "6 2.5 9.1 2 13c1.4-2.5 3.6-3.7 7.2-3.7v2.6L14 7.5 9.2 3z\"/></svg>";

    private const string PlayIcoon =
        "<svg viewBox=\"0 0 24 24\" width=\"24\" height=\"24\"><path fill=\"#fff\" d=\"M8 5v14l11-7z\"/></svg>";

    private const string VerwijderdIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"15\" height=\"15\"><circle cx=\"8\" cy=\"8\" r=\"6.2\" " +
        "fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\"/><path d=\"M3.8 12.2l8.4-8.4\" " +
        "stroke=\"currentColor\" stroke-width=\"1.4\"/></svg>";

    private const string VinkWit =
        "<svg viewBox=\"0 0 12 12\" width=\"11\" height=\"11\"><path d=\"M2.3 6.3l2.4 2.3 5-5.1\" fill=\"none\" " +
        "stroke=\"#fff\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

    private static readonly Regex OngelezenRegex = new(
        @"^\d+\s+(ongelezen bericht|unread message|message[s]? non lu)", RegexOptions.IgnoreCase);

    private static string Vinkjes(string status, bool opBeeld)
    {
        if (status == "wachtend")
        {
            return "<svg viewBox=\"0 0 16 15\" width=\"15\" height=\"14\"><circle cx=\"8\" cy=\"7.5\" r=\"5.4\" " +
                "fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M8 4.6v3.2l2 1.3\" " +
                "fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linecap=\"round\"/></svg>";
        }
        var kleur = status == "gelezen" ? "#53bdeb" : opBeeld ? "#ffffff" : "#8696a0";
        var tweede = status == "verzonden" ? "" :
            "<path d=\"M7.4 8.6l.9.8 6.4-7.1\" fill=\"none\" stroke=\"" + kleur + "\" stroke-width=\"1.5\" " +
            "stroke-linecap=\"round\" stroke-linejoin=\"round\"/>";
        return "<svg viewBox=\"0 0 16 11\" width=\"16\" height=\"11\"><path d=\"M1.3 5.9l3 2.9 6.4-7.1\" " +
            "fill=\"none\" stroke=\"" + kleur + "\" stroke-width=\"1.5\" stroke-linecap=\"round\" " +
            "stroke-linejoin=\"round\"/>" + tweede + "</svg>";
    }

    private static string E(string tekst) => WebUtility.HtmlEncode(tekst);

    public static string Bouw(List<WhatsAppClient.WaBericht> berichten, string chatNaam,
        string avatarDataUrl, string ondertitel)
    {
        var donker = Donker;
        // Groep: WhatsApp toont dan per bericht de afzender en avatar, in 1-op-1 niet.
        var groep = berichten.Any(b => !b.Uitgaand && b.Kleur.Length > 0) || ondertitel.Contains(',') ||
            berichten.Where(b => !b.Uitgaand && b.Media != "systeem")
                .Select(b => b.Afzender).Distinct().Count() > 1;
        var nieuweData = berichten.Any(b => b.Klok.Length > 0);

        // Profielfoto's één keer als CSS-klasse: in een groep komt dezelfde foto vaak terug.
        var avatarKlassen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in berichten.Select(b => b.AvatarUrl).Where(u => u.StartsWith("data:image")))
        {
            avatarKlassen.TryAdd(url, $"wa-a{avatarKlassen.Count}");
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"wm-chat wa").Append(donker ? " donker" : "").Append("\"><style>").Append(Css);
        var behang = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Behang.Replace("{K}", donker ? "rgba(255,255,255,0.045)" : "rgba(0,0,0,0.045)")));
        sb.Append(".wa-scroll{background-image:url(data:image/svg+xml;base64,").Append(behang).Append(")}");
        foreach (var (url, klasse) in avatarKlassen)
        {
            sb.Append('.').Append(klasse).Append("{background-image:url(").Append(url).Append(")}");
        }
        sb.Append("</style>");

        // Kop: foto (of pictogram), naam en de deelnemers / "laatst gezien".
        if (chatNaam.Length > 0)
        {
            sb.Append("<div class=\"wa-kop\">");
            if (avatarDataUrl.Length > 0)
            {
                sb.Append($"<img class=\"wa-kop-foto\" src=\"{avatarDataUrl}\">");
            }
            else
            {
                var (bg, fg) = KopKleuren[(int)((uint)Hash(chatNaam) % KopKleuren.Length)];
                sb.Append($"<div class=\"wa-kop-leeg\" style=\"background:{bg};color:{fg}\">")
                  .Append(groep ? GroepIcoon : PersoonIcoon).Append("</div>");
            }
            sb.Append("<div class=\"wa-kop-tekst\"><div class=\"wa-kop-naam\">").Append(E(chatNaam)).Append("</div>");
            if (ondertitel.Length > 0)
            {
                sb.Append("<div class=\"wa-kop-sub\" title=\"").Append(E(ondertitel)).Append("\">")
                  .Append(E(ondertitel)).Append("</div>");
            }
            sb.Append("</div></div>");
        }

        sb.Append("<div class=\"wm-chat-scroll wa-scroll\" style=\"max-height:520px\"><div class=\"wa-lijst\">");
        var vorigeDag = "";
        WhatsAppClient.WaBericht? vorige = null;
        foreach (var b in berichten)
        {
            var dag = b.Datum.Length > 0 ? b.Datum : DatumUitTijd(b.Tijd);
            var nieuweDag = dag.Length > 0 && dag != vorigeDag;
            if (nieuweDag)
            {
                sb.Append("<div class=\"wa-dag\">").Append(E(DagLabel(dag))).Append("</div>");
                vorigeDag = dag;
            }
            if (b.Media == "systeem")
            {
                // "3 ongelezen berichten" is in WhatsApp een groene scheidingsregel, geen chip.
                var ongelezen = OngelezenRegex.IsMatch(b.Tekst);
                sb.Append(ongelezen ? "<div class=\"wa-sys ongelezen\">" : "<div class=\"wa-sys\">")
                  .Append(E(b.Tekst)).Append("</div>");
                vorige = null;
                continue;
            }
            // Eerste van een reeks: staartje, naam en avatar. Nieuwe data weet dat uit de DOM
            // (staartje aanwezig); oude cache: zelfde richting + afzender = vervolg.
            var vervolg = !nieuweDag && vorige is not null && (nieuweData
                ? b.Vervolg
                : vorige.Uitgaand == b.Uitgaand && (b.Uitgaand || vorige.Afzender == b.Afzender));
            vorige = b;
            BouwBericht(sb, b, groep, !vervolg, avatarKlassen, donker);
        }
        sb.Append("</div></div></div>");
        return sb.ToString();
    }

    private static void BouwBericht(StringBuilder sb, WhatsAppClient.WaBericht b, bool groep, bool eerste,
        Dictionary<string, string> avatarKlassen, bool donker)
    {
        var kant = b.Uitgaand ? "uit" : "in";
        var kleur = b.Kleur.Length > 0 ? b.Kleur : Naamkleuren[(int)((uint)Hash(b.Afzender) % Naamkleuren.Length)];
        if (donker)
        {
            kleur = Helder(kleur); // de lichte-modus-kleuren zijn te donker op een donkere bubbel
        }
        sb.Append($"<div class=\"wa-rij {kant}{(eerste ? " eerste" : "")}\">");
        if (groep && !b.Uitgaand)
        {
            if (!eerste)
            {
                sb.Append("<div class=\"wa-av-plek\"></div>");
            }
            else if (avatarKlassen.TryGetValue(b.AvatarUrl, out var klasse))
            {
                sb.Append($"<div class=\"wa-av {klasse}\"></div>");
            }
            else
            {
                // Geen foto: initialen in de naamkleur op een lichte tint ervan, zoals WhatsApp
                // (alleen een nummer bekend: een persoonsicoontje).
                var initialen = Initialen(b.Afzender);
                sb.Append($"<div class=\"wa-av\" style=\"background:{Tint(kleur, donker)};color:{kleur}\">")
                  .Append(initialen.Length > 0 ? E(initialen) : PersoonIcoon.Replace("22", "18")).Append("</div>");
            }
        }
        sb.Append("<div class=\"wa-kol\">");

        var sticker = b.Media == "sticker" && b.Beeld.StartsWith("data:image");
        sb.Append($"<div class=\"wa-b{(eerste ? " staart" : "")}{(sticker ? " wa-sticker" : "")}\">");

        // Afzender (alleen in groepen, alleen boven het eerste bericht van een reeks).
        if (groep && !b.Uitgaand && eerste && !sticker)
        {
            sb.Append($"<div class=\"wa-naam\"><span class=\"n\" style=\"color:{kleur}\">")
              .Append(b.Profielnaam ? "~ " : "").Append(E(b.Afzender)).Append("</span>");
            if (b.Nummer.Length > 0 && b.Nummer != b.Afzender)
            {
                sb.Append("<span class=\"nr\">").Append(E(b.Nummer)).Append("</span>");
            }
            sb.Append("</div>");
            if (b.Label.Length > 0)
            {
                sb.Append("<div class=\"wa-label\">").Append(E(b.Label)).Append("</div>");
            }
        }
        if (b.Doorgestuurd.Length > 0)
        {
            sb.Append("<div class=\"wa-fwd\">").Append(DoorstuurIcoon).Append(E(b.Doorgestuurd)).Append("</div>");
        }

        // Citaat bovenaan (staat als "↪ antwoord op wie: “wat”" vooraan in de tekst).
        var tekst = b.Tekst;
        if (CitaatRegex.Match(tekst) is { Success: true } citaat)
        {
            var wie = citaat.Groups["wie"].Value;
            var citKleur = b.CitaatKleur.Length > 0 ? b.CitaatKleur
                : wie is "jou" or "" ? "#06cf9c" : Naamkleuren[(int)((uint)Hash(wie) % Naamkleuren.Length)];
            if (donker)
            {
                citKleur = Helder(citKleur);
            }
            sb.Append($"<div class=\"wa-cit\" style=\"border-left-color:{citKleur}\"><div class=\"ct\">");
            if (wie.Length > 0)
            {
                sb.Append($"<div class=\"w\" style=\"color:{citKleur}\">")
                  .Append(wie == "jou" ? "Jij" : E(wie)).Append("</div>");
            }
            sb.Append("<div class=\"q\">").Append(Opmaak(citaat.Groups["wat"].Value)).Append("</div></div>");
            if (b.CitaatBeeld.StartsWith("data:image"))
            {
                sb.Append($"<img src=\"{b.CitaatBeeld}\">");
            }
            sb.Append("</div>");
            tekst = tekst[citaat.Length..];
        }

        // Media en bijzondere soorten.
        var metaOpBeeld = false;
        var heeftBeeld = b.Beeld.StartsWith("data:image");
        if (b.Link is { } link)
        {
            sb.Append($"<div class=\"wa-s\" style=\"padding:0\"><div class=\"wa-link{(link.Groot ? " groot" : "")}\">");
            if (link.Beeld.StartsWith("data:image"))
            {
                sb.Append($"<img src=\"{link.Beeld}\">");
            }
            sb.Append("<div class=\"lt\">");
            if (link.Titel.Length > 0) sb.Append("<div class=\"t\">").Append(E(link.Titel)).Append("</div>");
            if (link.Omschrijving.Length > 0) sb.Append("<div class=\"o\">").Append(E(link.Omschrijving)).Append("</div>");
            if (link.Domein.Length > 0) sb.Append("<div class=\"d\">").Append(E(link.Domein)).Append("</div>");
            sb.Append("</div></div></div>");
        }
        if (b.Media == "poll" && b.Poll is { } poll)
        {
            BouwPoll(sb, poll);
            tekst = "";
        }
        else if (sticker)
        {
            sb.Append($"<img src=\"{b.Beeld}\">");
            metaOpBeeld = true;
        }
        else if (heeftBeeld && b.Album.Count > 0)
        {
            sb.Append("<div class=\"wa-album\">");
            var beelden = new[] { b.Beeld }.Concat(b.Album).Take(4).ToList();
            for (var i = 0; i < beelden.Count; i++)
            {
                sb.Append($"<div><img src=\"{beelden[i]}\">");
                if (i == beelden.Count - 1 && b.AlbumMeer > 0)
                {
                    sb.Append($"<div class=\"meer\">+{b.AlbumMeer}</div>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");
            metaOpBeeld = tekst.Trim().Length == 0;
        }
        else if (heeftBeeld)
        {
            metaOpBeeld = tekst.Trim().Length == 0;
            sb.Append($"<div class=\"{(metaOpBeeld ? "wa-media" : "")}\" style=\"position:relative\">")
              .Append($"<img class=\"wa-foto\" src=\"{b.Beeld}\">");
            if (b.Media == "video")
            {
                sb.Append("<div class=\"wa-play\">").Append(PlayIcoon).Append("</div>");
                if (b.MediaInfo.Length > 0)
                {
                    sb.Append("<div class=\"wa-duur\">🎥 ").Append(E(b.MediaInfo)).Append("</div>");
                }
            }
            sb.Append("</div>");
        }
        else if (b.Media == "video")
        {
            Kaart(sb, "🎥", "Video", b.MediaInfo);
        }
        else if (b.Media == "audio")
        {
            sb.Append("<div class=\"wa-kaart\" style=\"background:none;padding:6px 6px 0\">" +
                "<div class=\"ico\" style=\"background:#00a884;color:#fff\">▶</div><div class=\"wa-golf\"></div>")
              .Append("<div class=\"k2\">").Append(E(b.MediaInfo.Length > 0 ? b.MediaInfo : "🎤")).Append("</div></div>");
        }
        else if (b.Media == "document")
        {
            var delen = b.MediaInfo.Split(" · ", 2);
            Kaart(sb, "📄", delen[0].Length > 0 ? delen[0] : "Document", delen.Length > 1 ? delen[1] : "");
        }
        else if (b.Media == "oproep")
        {
            var delen = b.MediaInfo.Split(" · ", 2);
            var gemist = b.MediaInfo.Contains("gemist", StringComparison.OrdinalIgnoreCase) ||
                b.MediaInfo.Contains("missed", StringComparison.OrdinalIgnoreCase);
            Kaart(sb, gemist ? "📵" : "📞", delen[0].Length > 0 ? delen[0] : "Oproep",
                delen.Length > 1 ? delen[1] : "");
        }
        else if (b.Media == "verwijderd")
        {
            sb.Append("<div class=\"wa-t\"><span class=\"wa-verw\">").Append(VerwijderdIcoon)
              .Append(E(tekst.Trim().Length > 0 ? tekst.Trim() : "Dit bericht is verwijderd"))
              .Append(Ruimte(b)).Append("</span></div>");
            sb.Append(Meta(b, false, b.Uitgaand)).Append("</div>");
            SluitAf(sb, b);
            return;
        }
        else if (b.Media == "overig" && b.MediaInfo.Length > 0)
        {
            sb.Append("<div class=\"wa-t\" style=\"color:var(--sub);font-style:italic\">")
              .Append(E(b.MediaInfo)).Append(Ruimte(b)).Append("</div>");
            sb.Append(Meta(b, false, b.Uitgaand)).Append("</div>");
            SluitAf(sb, b);
            return;
        }

        // De tekst zelf, met ruimte achteraan voor tijd + vinkjes (zoals WhatsApp).
        tekst = tekst.Trim();
        if (tekst.Length > 0)
        {
            var groot = AlleenEmoji(tekst) && !heeftBeeld;
            sb.Append("<div class=\"wa-t\">")
              .Append(groot ? $"<span class=\"wa-groot\">{E(tekst)}</span>" : Opmaak(tekst))
              .Append(Ruimte(b)).Append("</div>");
        }
        else if (!metaOpBeeld)
        {
            sb.Append("<div class=\"wa-voet\"></div>");
        }
        sb.Append(Meta(b, metaOpBeeld, b.Uitgaand, sticker)).Append("</div>");
        SluitAf(sb, b);
    }

    /// <summary>Reacties onder de bubbel en de rij afsluiten.</summary>
    private static void SluitAf(StringBuilder sb, WhatsAppClient.WaBericht b)
    {
        if (b.Reacties.Length > 0)
        {
            sb.Append("<div class=\"wa-reac\">").Append(E(b.Reacties)).Append("</div>");
        }
        sb.Append("</div></div>");
    }

    private static void Kaart(StringBuilder sb, string icoon, string titel, string info)
    {
        sb.Append("<div class=\"wa-kaart\"><div class=\"ico\">").Append(icoon).Append("</div><div style=\"min-width:0\">")
          .Append("<div class=\"k1\">").Append(E(titel)).Append("</div>");
        if (info.Length > 0)
        {
            sb.Append("<div class=\"k2\">").Append(E(info)).Append("</div>");
        }
        sb.Append("</div></div>");
    }

    private static void BouwPoll(StringBuilder sb, WhatsAppClient.WaPoll poll)
    {
        var max = Math.Max(1, poll.Opties.Count == 0 ? 1 : poll.Opties.Max(o => o.Stemmen));
        sb.Append("<div class=\"wa-poll\"><div class=\"vraag\">").Append(Opmaak(poll.Vraag)).Append("</div>")
          .Append("<div class=\"sub\">").Append(poll.Meerdere ? "Selecteer één of meer" : "Selecteer één").Append("</div>");
        foreach (var o in poll.Opties)
        {
            sb.Append("<div class=\"opt\"><div class=\"vink").Append(o.Gekozen ? " aan\">" + VinkWit : "\">").Append("</div>")
              .Append("<div class=\"optk\"><div class=\"optr\"><span>").Append(E(o.Tekst)).Append("</span>")
              .Append("<span class=\"n\">").Append(o.Stemmen).Append("</span></div>")
              .Append("<div class=\"balk\"><div style=\"width:")
              .Append((100 * o.Stemmen / max).ToString(CultureInfo.InvariantCulture)).Append("%\"></div></div></div></div>");
        }
        sb.Append("</div><div class=\"wa-voet\" style=\"height:14px\"></div>");
    }

    /// <summary>Onzichtbare ruimte op de laatste tekstregel, zodat de tijd er niet overheen valt.</summary>
    private static string Ruimte(WhatsAppClient.WaBericht b)
    {
        var breedte = 12 + 30 + (b.Bewerkt ? 50 : 0) + (b.Uitgaand ? 20 : 0);
        return $"<span class=\"wa-ruimte\" style=\"width:{breedte}px\"></span>";
    }

    private static string Meta(WhatsAppClient.WaBericht b, bool opBeeld, bool uitgaand, bool pil = false)
    {
        var klok = b.Klok.Length > 0 ? b.Klok : b.Tijd.Split(',')[0].Trim();
        // Volledige datum + tijd als tooltip (WhatsApp toont die ook bij hover).
        var sb = new StringBuilder($"<span class=\"wa-meta{(pil ? " pil" : opBeeld ? " op" : "")}\" title=\"")
            .Append(E(b.Tijd)).Append("\">");
        if (b.Bewerkt)
        {
            sb.Append("Bewerkt ");
        }
        sb.Append(E(klok));
        if (uitgaand)
        {
            // Oude cache kent geen status: dan (zoals voorheen) blauwe vinkjes.
            sb.Append(Vinkjes(b.Status.Length > 0 ? b.Status : "gelezen", opBeeld || pil));
        }
        return sb.Append("</span>").ToString();
    }

    // ---------- Tekstopmaak ----------

    private static readonly Regex CitaatRegex = new(
        @"^↪ antwoord op (?:(?<wie>[^:“\n]+): )?“(?<wat>[^\n]*)”\n?");
    private static readonly Regex UrlRegex = new(
        @"\b(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase);

    private static readonly (Regex Patroon, string Html)[] MarkupRegels =
    [
        (new(@"`([^`\n]+)`"), "<code>$1</code>"),
        (new(@"(?<![\w*])\*(?=\S)([^*\n]*?\S)\*(?![\w*])"), "<b>$1</b>"),
        (new(@"(?<![\w_])_(?=\S)([^_\n]*?\S)_(?![\w_])"), "<i>$1</i>"),
        (new(@"(?<![\w~])~(?=\S)([^~\n]*?\S)~(?![\w~])"), "<s>$1</s>"),
    ];

    /// <summary>HTML-codeert en zet links en WhatsApp-markup (*vet* _cursief_ ~door~ `code`) om.</summary>
    internal static string Opmaak(string tekst)
    {
        var sb = new StringBuilder();
        var vanaf = 0;
        foreach (Match m in UrlRegex.Matches(tekst))
        {
            // Leestekens achter een link horen er niet bij ("zie https://x.be.").
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '’', '”', '*', '~');
            sb.Append(Markup(tekst[vanaf..m.Index]));
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            sb.Append("<a href=\"").Append(E(href)).Append("\">").Append(E(url)).Append("</a>");
            vanaf = m.Index + url.Length;
        }
        sb.Append(Markup(tekst[vanaf..]));
        return sb.ToString();
    }

    private static string Markup(string tekst)
    {
        var html = E(tekst);
        foreach (var (patroon, vervanging) in MarkupRegels)
        {
            html = patroon.Replace(html, vervanging);
        }
        return html;
    }

    /// <summary>Hooguit drie emoji's zonder andere tekst: WhatsApp toont ze dan groot.</summary>
    private static bool AlleenEmoji(string tekst)
    {
        var aantal = 0;
        var e = StringInfo.GetTextElementEnumerator(tekst.Trim());
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (string.IsNullOrWhiteSpace(element))
            {
                continue;
            }
            if (Rune.GetUnicodeCategory(Rune.GetRuneAt(element, 0)) != UnicodeCategory.OtherSymbol ||
                ++aantal > 3)
            {
                return false;
            }
        }
        return aantal > 0;
    }

    // ---------- Hulpjes ----------

    /// <summary>"12:30, 13/9/2026" → "13/9/2026" (oude cache zonder aparte datum).</summary>
    private static string DatumUitTijd(string tijd)
    {
        var i = tijd.IndexOf(',');
        return i < 0 ? "" : tijd[(i + 1)..].Trim();
    }

    /// <summary>Datumchip zoals WhatsApp: Vandaag, Gisteren, weekdag (deze week), anders d/M/jjjj.</summary>
    internal static string DagLabel(string datum)
    {
        if (!DateOnly.TryParseExact(datum, ["d/M/yyyy", "dd/MM/yyyy", "M/d/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return datum;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var verschil = vandaag.DayNumber - d.DayNumber;
        return verschil switch
        {
            0 => "Vandaag",
            1 => "Gisteren",
            > 1 and < 7 => d.ToString("dddd", new CultureInfo("nl-BE")),
            _ => datum,
        };
    }

    private static string Initialen(string naam)
    {
        var woorden = naam.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0])).ToList();
        if (woorden.Count == 0)
        {
            return naam.Length > 0 && !char.IsDigit(naam.TrimStart('+')[0]) ? naam[..1] : "";
        }
        return (woorden.Count == 1 ? woorden[0][..1] : woorden[0][..1] + woorden[1][..1]).ToUpperInvariant();
    }

    private static readonly Regex RgbRegex = new(@"rgba?\((\d+),\s*(\d+),\s*(\d+)");

    /// <summary>"rgb(184, 5, 49)" → dezelfde kleur op 15% (achtergrond voor initialen).</summary>
    private static string Tint(string kleur, bool donker)
    {
        var m = RgbRegex.Match(kleur);
        return m.Success
            ? $"rgba({m.Groups[1].Value},{m.Groups[2].Value},{m.Groups[3].Value},{(donker ? ".28" : ".15")})"
            : "var(--av-bg)";
    }

    /// <summary>
    /// Lichtere variant van een naamkleur voor de donkere weergave: WhatsApp gebruikt daar
    /// dezelfde tinten met meer helderheid, anders is bv. donkerblauw onleesbaar op grijs.
    /// </summary>
    private static string Helder(string kleur)
    {
        var m = RgbRegex.Match(kleur);
        if (!m.Success)
        {
            return kleur;
        }
        double r = int.Parse(m.Groups[1].Value) / 255.0, g = int.Parse(m.Groups[2].Value) / 255.0,
            bl = int.Parse(m.Groups[3].Value) / 255.0;
        double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max > min)
        {
            var d = max - min;
            s = l > .5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - bl) / d + (g < bl ? 6 : 0) : max == g ? (bl - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        l = Math.Max(l, .68);
        s = Math.Min(s, .8);
        double Kanaal(double t)
        {
            t = (t % 1 + 1) % 1;
            var q = l < .5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            var v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < .5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            return Math.Round(v * 255);
        }
        return $"rgb({Kanaal(h + 1.0 / 3)}, {Kanaal(h)}, {Kanaal(h - 1.0 / 3)})";
    }

    /// <summary>Stabiele hash (string.GetHashCode verschilt per proces).</summary>
    private static int Hash(string tekst)
    {
        var h = 17;
        foreach (var c in tekst)
        {
            h = unchecked(h * 31 + c);
        }
        return h;
    }
}
