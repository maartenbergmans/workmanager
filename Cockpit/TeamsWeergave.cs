using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Rendert een Teams-chat als HTML dat er uitziet als Teams zelf (Fluent): kop met foto en
/// naam; datumscheidingen als losse grijze regel; per reeks één kopregel "Naam  tijd" met
/// avatar links (groepen); grijze bubbels voor de ander, lichtpaarse rechts voor eigen
/// berichten; opmaak, lijsten, links en @vermeldingen; citaat-/doorstuurkaart; bijlagen;
/// reactiepilletjes; leesstatus onder het laatste eigen bericht. Volgt het kleurenschema
/// van de app (licht/donker). Zonder scripts: pure HTML/CSS.
/// </summary>
internal static class TeamsWeergave
{
    /// <summary>Diagnose: donkere weergave afdwingen, los van het actieve kleurenschema.</summary>
    internal static bool? DonkerOverride { get; set; }

    private static bool Donker => DonkerOverride ?? Theme.Palet.Donker;

    private const string Css =
        """
        .tm{--bg:#fff;--kop-bg:#f5f5f5;--rand:#e0e0e0;--tekst:#242424;--sub:#616161;--in:#f5f5f5;
            --uit:#e8ebfa;--link:#5b5fc7;--kaart:#fff;--tint:rgba(0,0,0,.06);--eigen:#e8ebfa;--accent:#c4314b;
            font-family:"Segoe UI","Segoe UI Web (West European)",-apple-system,"Helvetica Neue",Arial,
            "Segoe UI Emoji",sans-serif;color:var(--tekst);background:var(--bg);margin:-16px;
            border-radius:0 0 12px 12px;overflow:hidden}
        .tm.donker{--bg:#1f1f1f;--kop-bg:#292929;--rand:#3d3d3d;--tekst:#fff;--sub:#adadad;--in:#292929;
            --uit:#3b3b5b;--link:#a6a7dc;--kaart:#292929;--tint:rgba(255,255,255,.08);--eigen:#3b3b5b}
        .tm-kop{background:var(--kop-bg);padding:10px 16px;display:flex;align-items:center;gap:12px;
            border-bottom:1px solid var(--rand);flex:none}
        .tm-kop-foto{width:40px;height:40px;border-radius:50%;object-fit:cover;flex:none}
        .tm-kop-leeg{width:40px;height:40px;border-radius:50%;flex:none;display:flex;align-items:center;
            justify-content:center;font-size:15px;font-weight:600;color:#fff;background:#5b5fc7}
        .tm-kop-tekst{min-width:0}
        .tm-kop-naam{font-size:16px;line-height:22px;font-weight:700;white-space:nowrap;overflow:hidden;
            text-overflow:ellipsis}
        .tm-kop-sub{font-size:12px;line-height:16px;color:var(--sub);white-space:nowrap;overflow:hidden;
            text-overflow:ellipsis}
        .tm-scroll{background:var(--bg);display:flex;flex-direction:column-reverse;overflow-y:auto}
        .tm-scroll::-webkit-scrollbar{width:8px}
        .tm-scroll::-webkit-scrollbar-thumb{background:rgba(128,128,128,.4);border-radius:4px}
        .tm-lijst{padding:8px 16px 14px;display:flex;flex-direction:column}
        .tm-dag{align-self:center;font-size:12px;line-height:16px;color:var(--sub);margin:14px 0 4px}
        .tm-sys{align-self:center;font-size:12px;line-height:16px;color:var(--sub);font-style:italic;
            margin:8px 0 2px;text-align:center;max-width:90%}
        .tm-rij{display:flex;align-items:flex-start;margin-top:2px}
        .tm-rij.eerste{margin-top:12px}
        .tm-rij.uit{justify-content:flex-end}
        .tm-av{width:32px;height:32px;border-radius:50%;flex:none;margin:22px 8px 0 0;background:var(--in)
            center/cover no-repeat;display:flex;align-items:center;justify-content:center;font-size:12px;
            font-weight:600;color:#fff}
        .tm-av-plek{width:32px;flex:none;margin-right:8px}
        .tm-kol{display:flex;flex-direction:column;align-items:flex-start;max-width:82%;min-width:0}
        .uit .tm-kol{align-items:flex-end}
        .tm-hd{display:flex;align-items:baseline;gap:8px;font-size:12px;line-height:16px;color:var(--sub);
            margin:0 0 4px 2px;white-space:nowrap}
        .tm-hd .n{color:var(--tekst);opacity:.85}
        .tm-hd .at{color:var(--accent);font-weight:700}
        .tm-b{background:var(--in);border-radius:8px;padding:8px 16px;font-size:14px;line-height:20px;
            max-width:100%;box-sizing:border-box;overflow-wrap:anywhere;position:relative}
        .uit .tm-b{background:var(--uit)}
        .tm-b.vermeld{box-shadow:inset 3px 0 0 var(--accent)}
        .tm-t div{min-height:20px}
        .tm-t a{color:var(--link);text-decoration:none}
        .tm-t a:hover{text-decoration:underline}
        .tm-m{color:var(--link);font-weight:600}
        .tm-t ul,.tm-t ol{margin:2px 0;padding-left:24px}
        .tm-t blockquote{border-left:3px solid var(--rand);margin:4px 0;padding:0 0 0 10px;color:var(--sub)}
        .tm-t pre,.tm-t code{font-family:Consolas,"Cascadia Code",monospace;font-size:13px;background:var(--tint);
            border-radius:4px}
        .tm-t pre{padding:8px 10px;white-space:pre-wrap;margin:4px 0}
        .tm-t code{padding:0 4px}
        .tm-t table{border-collapse:collapse;margin:4px 0;max-width:100%}
        .tm-t td,.tm-t th{border:1px solid var(--rand);padding:3px 8px;vertical-align:top}
        .tm-cit{background:var(--kaart);border:1px solid var(--rand);border-radius:4px;padding:8px 12px 10px;
            margin:2px 0 8px;font-size:14px;line-height:20px;max-width:100%;box-sizing:border-box}
        .tm-cit .h{display:flex;align-items:center;gap:8px;font-size:12px;line-height:16px;color:var(--sub);
            margin-bottom:4px;white-space:nowrap;overflow:hidden}
        .tm-cit .h .w{color:var(--sub)}
        .tm-cit .h .d{color:var(--link);text-decoration:underline}
        .tm-cit .q{display:-webkit-box;-webkit-line-clamp:6;-webkit-box-orient:vertical;overflow:hidden;
            white-space:pre-wrap;color:var(--tekst)}
        .tm-cit .av{width:20px;height:20px;border-radius:50%;background:#c7c7f5;color:#3d3d8c;font-size:9px;
            font-weight:700;display:inline-flex;align-items:center;justify-content:center;flex:none}
        .tm-foto{display:block;max-width:100%;max-height:340px;border-radius:4px;margin:6px 0 2px}
        .tm-foto-leeg{background:var(--tint);border:1px dashed var(--rand);border-radius:4px;padding:20px 14px;
            text-align:center;color:var(--sub);font-size:12.5px;margin:6px 0 2px}
        .tm-file{display:flex;align-items:center;gap:10px;background:var(--kaart);border:1px solid var(--rand);
            border-radius:4px;padding:8px 12px;margin:6px 0 2px;font-size:13px;line-height:18px;min-width:200px;
            max-width:100%;box-sizing:border-box}
        .tm-file .i{font-size:20px;flex:none}
        .tm-file .n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:600}
        .tm-reac{display:flex;flex-wrap:wrap;gap:4px;margin:4px 0 0 2px}
        .uit .tm-reac{justify-content:flex-end;margin-right:2px}
        .tm-pil{background:var(--kaart);border:1px solid var(--rand);border-radius:16px;padding:1px 8px;
            font-size:12px;line-height:18px;white-space:nowrap;display:inline-flex;align-items:center;gap:4px}
        .tm-pil.eigen{border-color:var(--link);background:var(--eigen)}
        .tm-status{display:flex;align-items:center;gap:4px;font-size:11px;line-height:14px;color:var(--sub);
            margin:3px 2px 0 0}
        .tm-status svg{display:block}
        """;

    private const string AntwoordIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"14\" height=\"14\"><path fill=\"currentColor\" d=\"M6.8 3.2 2.5 7.5l4.3 4.3.7-.7L4.4 8h4.1c2.5 0 4 1.3 4 3.5v.5h1v-.5C13.5 8.7 11.5 7 8.5 7H4.4l3.1-3.1z\"/></svg>";

    private const string DoorstuurIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"14\" height=\"14\"><path fill=\"currentColor\" d=\"M9.2 3.2 13.5 7.5l-4.3 4.3-.7-.7L11.6 8H7.5c-2.5 0-4 1.3-4 3.5v.5h-1v-.5C2.5 8.7 4.5 7 7.5 7h4.1L8.5 3.9z\"/></svg>";

    private const string GezienIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"14\" height=\"14\"><path fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.3\" " +
        "d=\"M1.5 8s2.5-4.5 6.5-4.5S14.5 8 14.5 8s-2.5 4.5-6.5 4.5S1.5 8 1.5 8z\"/><circle cx=\"8\" cy=\"8\" r=\"2.2\" fill=\"currentColor\"/></svg>";

    private const string VerzondenIcoon =
        "<svg viewBox=\"0 0 16 16\" width=\"14\" height=\"14\"><circle cx=\"8\" cy=\"8\" r=\"6.2\" fill=\"none\" " +
        "stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M5 8.2l2 2 4-4.4\" fill=\"none\" stroke=\"currentColor\" " +
        "stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

    /// <summary>Kleuren voor initialen-avatars, zoals Teams ze per persoon toekent.</summary>
    private static readonly string[] AvatarKleuren =
    [
        "#5b5fc7", "#c4314b", "#0078d4", "#8764b8", "#038387", "#ca5010", "#498205", "#b4009e", "#4f6bed",
    ];

    private static string E(string tekst) => WebUtility.HtmlEncode(tekst);

    public static string Bouw(List<TeamsClient.TeamsChatBericht> berichten, string chatNaam)
    {
        var donker = Donker;
        var echte = berichten.Where(b => b.Soort.Length == 0).ToList();
        var afzenders = echte.Where(b => !b.Uitgaand).Select(b => b.Auteur)
            .Where(a => a.Length > 0).Distinct().ToList();
        // Groep: Teams toont dan avatars naast de bubbels; in een 1-op-1-chat niet.
        var groep = afzenders.Count > 1 || chatNaam.Contains(',') ||
            (afzenders.Count == 1 && !chatNaam.Contains(afzenders[0], StringComparison.OrdinalIgnoreCase));
        var avatarKlassen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in echte.Select(b => b.AvatarUrl).Where(u => u.StartsWith("data:image")))
        {
            avatarKlassen.TryAdd(url, $"tm-a{avatarKlassen.Count}");
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"wm-chat tm").Append(donker ? " donker" : "").Append("\"><style>").Append(Css);
        foreach (var (url, klasse) in avatarKlassen)
        {
            sb.Append('.').Append(klasse).Append("{background-image:url(").Append(url).Append(")}");
        }
        sb.Append("</style>");

        if (chatNaam.Length > 0)
        {
            // Kop: in een 1-op-1-chat de foto van de ander, anders initialen in Teams-paars.
            var kopFoto = groep ? "" : echte.FirstOrDefault(b => !b.Uitgaand && b.AvatarUrl.Length > 0)?.AvatarUrl ?? "";
            sb.Append("<div class=\"tm-kop\">");
            if (kopFoto.Length > 0)
            {
                sb.Append($"<img class=\"tm-kop-foto\" src=\"{kopFoto}\">");
            }
            else
            {
                sb.Append("<div class=\"tm-kop-leeg\" style=\"background:").Append(Kleur(chatNaam)).Append("\">")
                  .Append(E(Initialen(chatNaam))).Append("</div>");
            }
            sb.Append("<div class=\"tm-kop-tekst\"><div class=\"tm-kop-naam\">").Append(E(chatNaam)).Append("</div>");
            var sub = groep && afzenders.Count > 0 ? string.Join(", ", afzenders) : "Chat";
            sb.Append("<div class=\"tm-kop-sub\" title=\"").Append(E(sub)).Append("\">").Append(E(sub)).Append("</div>");
            sb.Append("</div></div>");
        }

        sb.Append("<div class=\"wm-chat-scroll tm-scroll\" style=\"max-height:520px\"><div class=\"tm-lijst\">");
        var laatsteUit = echte.LastOrDefault(b => b.Uitgaand);
        TeamsClient.TeamsChatBericht? vorige = null;
        foreach (var b in berichten)
        {
            if (b.Soort == "divider")
            {
                sb.Append("<div class=\"tm-dag\">").Append(E(b.Tekst)).Append("</div>");
                vorige = null;
                continue;
            }
            if (b.Soort == "systeem")
            {
                sb.Append("<div class=\"tm-sys\">").Append(E(b.Tekst)).Append("</div>");
                vorige = null;
                continue;
            }
            // Reeks: zelfde kant en afzender, binnen vijf minuten en zonder scheiding ertussen —
            // dan laat Teams de kopregel en de avatar weg.
            var vervolg = vorige is not null && vorige.Uitgaand == b.Uitgaand &&
                (b.Uitgaand || vorige.Auteur == b.Auteur) &&
                vorige.Tijdstip is { } vt && b.Tijdstip is { } bt && (bt - vt).TotalMinutes is >= 0 and <= 5;
            vorige = b;
            BouwBericht(sb, b, groep, !vervolg, avatarKlassen, ReferenceEquals(b, laatsteUit));
        }
        sb.Append("</div></div></div>");
        return sb.ToString();
    }

    private static void BouwBericht(StringBuilder sb, TeamsClient.TeamsChatBericht b, bool groep, bool eerste,
        Dictionary<string, string> avatarKlassen, bool laatsteEigen)
    {
        var kant = b.Uitgaand ? "uit" : "in";
        sb.Append($"<div class=\"tm-rij {kant}{(eerste ? " eerste" : "")}\">");
        if (groep && !b.Uitgaand)
        {
            if (!eerste)
            {
                sb.Append("<div class=\"tm-av-plek\"></div>");
            }
            else if (avatarKlassen.TryGetValue(b.AvatarUrl, out var klasse))
            {
                sb.Append($"<div class=\"tm-av {klasse}\"></div>");
            }
            else
            {
                sb.Append("<div class=\"tm-av\" style=\"background:").Append(Kleur(b.Auteur)).Append("\">")
                  .Append(E(Initialen(b.Auteur))).Append("</div>");
            }
        }
        sb.Append("<div class=\"tm-kol\">");
        if (eerste)
        {
            sb.Append("<div class=\"tm-hd\">");
            if (!b.Uitgaand && b.Auteur.Length > 0)
            {
                sb.Append("<span class=\"n\">").Append(E(b.Auteur)).Append("</span>");
            }
            if (b.Tijd.Length > 0)
            {
                sb.Append("<span title=\"").Append(E(b.TijdVol)).Append("\">").Append(E(b.Tijd)).Append("</span>");
            }
            if (b.Bewerkt)
            {
                sb.Append("<span>Bewerkt</span>");
            }
            if (b.Vermeld)
            {
                sb.Append("<span class=\"at\" title=\"Je bent vermeld\">@</span>");
            }
            sb.Append("</div>");
        }
        sb.Append($"<div class=\"tm-b{(b.Vermeld ? " vermeld" : "")}\">");

        if (b.Citaat is { } c)
        {
            sb.Append("<div class=\"tm-cit\"><div class=\"h\">").Append(c.Doorgestuurd ? DoorstuurIcoon : AntwoordIcoon);
            if (c.Wie.Length > 0)
            {
                sb.Append("<span class=\"av\">").Append(E(Initialen(c.Wie))).Append("</span>")
                  .Append("<span class=\"w\">").Append(E(c.Wie)).Append("</span>");
            }
            if (c.Wanneer.Length > 0)
            {
                sb.Append("<span class=\"d\">").Append(E(c.Wanneer)).Append("</span>");
            }
            sb.Append("</div><div class=\"q\">").Append(E(c.Wat)).Append("</div></div>");
        }

        // Inhoud: opgeschoonde HTML uit Teams (nieuwe cache) of platte tekst (oude cache).
        if (b.Html.Length > 0)
        {
            sb.Append("<div class=\"tm-t\">").Append(b.Html).Append("</div>");
        }
        else if (b.Tekst.Length > 0)
        {
            sb.Append("<div class=\"tm-t\" style=\"white-space:pre-wrap\">").Append(MailWeergave.EncodeMetLinks(b.Tekst)).Append("</div>");
        }
        if (b.Beeld.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"<img class=\"tm-foto\" src=\"{b.Beeld}\">");
        }
        else if (b.Foto)
        {
            // De foto kon niet opgehaald worden (bv. nog aan het uploaden tijdens de
            // scrape): placeholder tonen in plaats van het bericht te verzwijgen.
            sb.Append("<div class=\"tm-foto-leeg\">📷 Afbeelding — kon niet opgehaald worden, bekijk ze in Teams</div>");
        }
        foreach (var bijlage in b.Bijlagen)
        {
            sb.Append("<div class=\"tm-file\"><span class=\"i\">").Append(BestandIcoon(bijlage))
              .Append("</span><span class=\"n\" title=\"").Append(E(bijlage)).Append("\">").Append(E(bijlage))
              .Append("</span></div>");
        }
        sb.Append("</div>");

        if (b.Reacties.Length > 0)
        {
            sb.Append("<div class=\"tm-reac\">");
            foreach (var deel in ReactieRegex.Matches(b.Reacties).Select(m => m.Value))
            {
                sb.Append("<span class=\"tm-pil").Append(b.EigenReactie ? " eigen" : "").Append("\">")
                  .Append(E(deel)).Append("</span>");
            }
            sb.Append("</div>");
        }
        if (laatsteEigen && b.Status.Length > 0)
        {
            var gezien = b.Status.Contains("gezien", StringComparison.OrdinalIgnoreCase) ||
                b.Status.Contains("seen", StringComparison.OrdinalIgnoreCase) ||
                b.Status.Contains("read", StringComparison.OrdinalIgnoreCase);
            sb.Append("<div class=\"tm-status\">").Append(gezien ? GezienIcoon : VerzondenIcoon)
              .Append(E(b.Status)).Append("</div>");
        }
        sb.Append("</div></div>");
    }

    /// <summary>"👍 😂 2 ❤️" → losse pilletjes ("👍", "😂 2", "❤️").</summary>
    private static readonly Regex ReactieRegex = new(@"\S+(?:\s\d+)?");

    private static string BestandIcoon(string naam)
    {
        var ext = Path.GetExtension(naam).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "📕",
            ".xlsx" or ".xls" or ".csv" => "📗",
            ".docx" or ".doc" => "📘",
            ".pptx" or ".ppt" => "📙",
            ".zip" or ".7z" or ".rar" => "🗜️",
            ".png" or ".jpg" or ".jpeg" or ".gif" => "🖼️",
            ".mp4" or ".mov" => "🎞️",
            _ => "📎",
        };
    }

    private static string Initialen(string naam)
    {
        var woorden = naam.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (woorden.Count == 0)
        {
            return naam.Length > 0 ? naam[..1] : "?";
        }
        return (woorden.Count == 1 ? woorden[0][..1] : woorden[0][..1] + woorden[^1][..1]).ToUpperInvariant();
    }

    private static string Kleur(string naam) => AvatarKleuren[(int)((uint)Hash(naam) % AvatarKleuren.Length)];

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
