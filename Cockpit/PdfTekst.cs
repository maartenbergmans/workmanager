using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Haalt de leesbare tekst uit een PDF, zonder externe bibliotheek. Genoeg voor wat
/// WorkManager ervan moet weten: staat er een bedrag op deze factuur, en welk?
///
/// <para>Hoe: de inhoudsstromen van de PDF worden uitgepakt (FlateDecode) en daaruit worden de
/// tekenopdrachten geplukt. Moderne PDF's — ook de facturen van Google Cloud, die door
/// Chromium gemaakt worden — zetten hun tekst niet als leesbare letters neer maar als
/// glyph-nummers van een ingebed font (Identity-H). De omzetting naar echte tekens staat in de
/// /ToUnicode-tabel van dat font, en die tabellen worden hier gelezen en toegepast. Welke
/// tabel bij welke stroom hoort, wordt niet uit de objectstructuur opgezocht (dat zou een
/// volledige PDF-parser vragen): per stroom wordt elke tabel geprobeerd en de leesbaarste
/// uitkomst gehouden.</para>
///
/// <para>Komt er niets leesbaars uit, dan is dat geen fout: de aanroeper behandelt "niets
/// gevonden" als "ik weet het niet" en doet dan niets.</para>
/// </summary>
public static class PdfTekst
{
    /// <summary>De tekst van een PDF-bestand; leeg als er niets leesbaars uit komt.</summary>
    public static string Lees(string pad)
    {
        try
        {
            return Uit(File.ReadAllBytes(pad));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>De tekst uit de ruwe bytes van een PDF.</summary>
    public static string Uit(byte[] pdf)
    {
        var stromen = Stromen(pdf).ToList();
        // De /ToUnicode-tabellen staan zelf ook in (gecomprimeerde) stromen.
        var tabellen = stromen
            .Where(s => s.Contains("beginbfchar", StringComparison.Ordinal) ||
                        s.Contains("beginbfrange", StringComparison.Ordinal))
            .Select(LeesTabel)
            .Where(t => t.Count > 0)
            .ToList();

        var tekst = new StringBuilder();
        foreach (var stroom in stromen)
        {
            // Alleen stromen met tekenopdrachten zijn pagina-inhoud.
            if (!stroom.Contains("Tj", StringComparison.Ordinal) &&
                !stroom.Contains("TJ", StringComparison.Ordinal))
            {
                continue;
            }
            tekst.AppendLine(BesteVertaling(stroom, tabellen));
        }
        return tekst.ToString();
    }

    /// <summary>
    /// Een /ToUnicode-CMap inlezen: losse toewijzingen (beginbfchar: glyph → teken) en
    /// reeksen (beginbfrange: een reeks glyphs die aansluitend op een teken begint).
    /// </summary>
    private static Dictionary<int, char> LeesTabel(string cmap)
    {
        var tabel = new Dictionary<int, char>();
        foreach (Match blok in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
        {
            foreach (Match paar in Regex.Matches(blok.Groups[1].Value,
                         @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
            {
                if (Hex(paar.Groups[1].Value) is { } glyph && Hex(paar.Groups[2].Value) is { } teken)
                {
                    tabel[glyph] = (char)teken;
                }
            }
        }
        foreach (Match blok in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        {
            foreach (Match reeks in Regex.Matches(blok.Groups[1].Value,
                         @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
            {
                if (Hex(reeks.Groups[1].Value) is not { } van ||
                    Hex(reeks.Groups[2].Value) is not { } tot ||
                    Hex(reeks.Groups[3].Value) is not { } start ||
                    tot < van || tot - van > 65535)
                {
                    continue;
                }
                for (var glyph = van; glyph <= tot; glyph++)
                {
                    tabel[glyph] = (char)(start + glyph - van);
                }
            }
        }
        return tabel;

        // Alleen de eerste twee bytes: een bfchar-doel kan meerdere tekens bevatten
        // (ligaturen), en daarvan is het eerste teken voor ons voldoende.
        static int? Hex(string ruw) =>
            int.TryParse(ruw.Length > 4 ? ruw[..4] : ruw,
                System.Globalization.NumberStyles.HexNumber, null, out var w)
                ? w
                : null;
    }

    /// <summary>
    /// De leesbaarste vertaling van één inhoudsstroom: zonder tabel (gewone PDF's) of met
    /// elk van de gevonden /ToUnicode-tabellen. De score beloont echte factuurwoorden zwaar
    /// en gewone letters en cijfers licht, zodat een tabel van een ander font het niet haalt.
    /// </summary>
    private static string BesteVertaling(string stroom, List<Dictionary<int, char>> tabellen)
    {
        var beste = Vertaal(stroom, null);
        var besteScore = Score(beste);
        foreach (var tabel in tabellen)
        {
            var kandidaat = Vertaal(stroom, tabel);
            var score = Score(kandidaat);
            if (score > besteScore)
            {
                beste = kandidaat;
                besteScore = score;
            }
        }
        return beste;
    }

    /// <summary>Woorden die op een factuur horen; hun aanwezigheid verraadt de juiste tabel.</summary>
    private static readonly string[] Ankers =
    {
        "Total", "Totaal", "EUR", "Invoice", "Factuur", "Subtotal", "Subtotaal",
        "Google", "Amount", "Bedrag", "VAT", "BTW", "Due", "betalen", "Page", "Pagina",
    };

    private static int Score(string tekst)
    {
        var score = 0;
        foreach (var anker in Ankers)
        {
            var pos = 0;
            while ((pos = tekst.IndexOf(anker, pos, StringComparison.Ordinal)) >= 0)
            {
                score += 25;
                pos += anker.Length;
            }
        }
        foreach (var c in tekst)
        {
            if (char.IsLetterOrDigit(c))
            {
                score++;
            }
        }
        return score;
    }

    /// <summary>
    /// De tekenopdrachten uit één stroom omzetten naar tekst. Letterlijke strings komen er
    /// rechtstreeks uit; hexstrings gaan door de tabel (of, zonder tabel, door een
    /// eenvoudige byte- of UTF-16-lezing). Positioneringen worden witruimte, zodat losse
    /// stukken tekst niet aan elkaar plakken.
    /// </summary>
    private static string Vertaal(string stroom, Dictionary<int, char>? tabel)
    {
        var uit = new StringBuilder();
        foreach (Match m in Regex.Matches(stroom,
                     @"\((?<lit>(?:\\.|[^\\()])*)\)|<(?<hex>[0-9A-Fa-f\s]+)>|(?<pos>T[djDJ*]|'|"")",
                     RegexOptions.Singleline))
        {
            if (m.Groups["lit"].Success)
            {
                uit.Append(Ontsnap(m.Groups["lit"].Value));
            }
            else if (m.Groups["hex"].Success)
            {
                var hex = Regex.Replace(m.Groups["hex"].Value, @"\s", "");
                uit.Append(tabel is null ? UitHex(hex) : ViaTabel(hex, tabel));
            }
            else
            {
                uit.Append(' ');
            }
        }
        return uit.ToString();
    }

    /// <summary>Glyph-nummers (twee bytes per teken) via de /ToUnicode-tabel naar tekst.</summary>
    private static string ViaTabel(string hex, Dictionary<int, char> tabel)
    {
        var sb = new StringBuilder();
        for (var i = 0; i + 4 <= hex.Length; i += 4)
        {
            if (int.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber,
                    null, out var glyph) &&
                tabel.TryGetValue(glyph, out var teken))
            {
                sb.Append(teken);
            }
        }
        return sb.ToString();
    }

    /// <summary>PDF-escapes in een letterlijke string omzetten.</summary>
    private static string Ontsnap(string ruw)
    {
        var sb = new StringBuilder(ruw.Length);
        for (var i = 0; i < ruw.Length; i++)
        {
            if (ruw[i] != '\\')
            {
                sb.Append(ruw[i]);
                continue;
            }
            if (++i >= ruw.Length)
            {
                break;
            }
            switch (ruw[i])
            {
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'b' or 'f':
                    sb.Append(' ');
                    break;
                case >= '0' and <= '7':
                    // Octaal: \101 → 'A'.
                    var octaal = 0;
                    var cijfers = 0;
                    while (cijfers < 3 && i < ruw.Length && ruw[i] is >= '0' and <= '7')
                    {
                        octaal = octaal * 8 + (ruw[i] - '0');
                        i++;
                        cijfers++;
                    }
                    i--;
                    sb.Append((char)octaal);
                    break;
                default:
                    sb.Append(ruw[i]); // \( \) \\ en al de rest: letterlijk
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Een hexstring zonder tabel: twee hexcijfers per teken is de gewone vorm, vier per
    /// teken komt voor als UTF-16BE. Onleesbare (stuur)tekens worden overgeslagen.
    /// </summary>
    private static string UitHex(string hex)
    {
        var schoon = hex.Length % 2 == 0 ? hex : hex + "0";
        // Vier cijfers per teken als elk eerste bytepaar 00 is (UTF-16BE met Latijns schrift).
        var vier = schoon.Length % 4 == 0 && schoon.Length >= 4 &&
                   Enumerable.Range(0, schoon.Length / 4)
                       .All(i => schoon.Substring(i * 4, 2) == "00");
        var stap = vier ? 4 : 2;
        var sb = new StringBuilder();
        for (var i = 0; i + stap <= schoon.Length; i += stap)
        {
            if (!int.TryParse(schoon.AsSpan(i, stap), System.Globalization.NumberStyles.HexNumber,
                    null, out var code))
            {
                continue;
            }
            var teken = (char)code;
            if (!char.IsControl(teken))
            {
                sb.Append(teken);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Alle stromen uit de PDF, al uitgepakt. Een PDF bewaart zijn inhoud (en bij nieuwere
    /// versies ook de objectdefinities) tussen "stream" en "endstream", vrijwel altijd
    /// zlib-gecomprimeerd.
    /// </summary>
    private static IEnumerable<string> Stromen(byte[] pdf)
    {
        var start = 0;
        while (true)
        {
            var streamPos = Vind(pdf, "stream", start);
            if (streamPos < 0)
            {
                break;
            }
            var inhoudStart = streamPos + 6;
            // Na het sleutelwoord staat CR, LF of CRLF.
            if (inhoudStart < pdf.Length && pdf[inhoudStart] == (byte)'\r')
            {
                inhoudStart++;
            }
            if (inhoudStart < pdf.Length && pdf[inhoudStart] == (byte)'\n')
            {
                inhoudStart++;
            }
            var einde = Vind(pdf, "endstream", inhoudStart);
            if (einde < 0)
            {
                break;
            }
            var lengte = einde - inhoudStart;
            start = einde + 9;
            if (lengte <= 0)
            {
                continue;
            }
            var blok = new byte[lengte];
            Array.Copy(pdf, inhoudStart, blok, 0, lengte);
            if (Uitpakken(blok) is { Length: > 0 } uitgepakt)
            {
                yield return uitgepakt;
            }
        }
    }

    /// <summary>Zlib uitpakken; lukt dat niet, dan was de stroom niet gecomprimeerd.</summary>
    private static string Uitpakken(byte[] blok)
    {
        try
        {
            using var bron = new MemoryStream(blok);
            using var zlib = new ZLibStream(bron, CompressionMode.Decompress);
            using var doel = new MemoryStream();
            zlib.CopyTo(doel);
            return Encoding.Latin1.GetString(doel.ToArray());
        }
        catch
        {
            // Geen (geldige) zlib-stroom: misschien gewone tekst, misschien een afbeelding.
            // Latin1 houdt elke byte heel, dus de tekstpatronen vinden hem alsnog.
            return Encoding.Latin1.GetString(blok);
        }
    }

    /// <summary>Positie van een ASCII-sleutelwoord in de bytes, of -1.</summary>
    private static int Vind(byte[] data, string woord, int vanaf)
    {
        var naald = Encoding.ASCII.GetBytes(woord);
        for (var i = Math.Max(0, vanaf); i <= data.Length - naald.Length; i++)
        {
            var gelijk = true;
            for (var j = 0; j < naald.Length; j++)
            {
                if (data[i + j] != naald[j])
                {
                    gelijk = false;
                    break;
                }
            }
            if (gelijk)
            {
                return i;
            }
        }
        return -1;
    }
}
