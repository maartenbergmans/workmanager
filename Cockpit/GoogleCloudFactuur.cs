using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// De maandelijkse factuurmail van Google Cloud ("uw factuur voor 01817E-… is beschikbaar")
/// automatisch archiveren zolang er niets te betalen is. Het bedrag staat niet in de mail —
/// alleen in de pdf-bijlage — dus die wordt gedownload en met <see cref="PdfTekst"/> gelezen.
/// Staan er alleen nulbedragen op, dan is het pure routine en verdwijnt de mail uit de
/// cockpit; staat er wél een bedrag op (of valt de pdf niet te lezen), dan blijft de mail
/// gewoon staan. Twijfel betekent dus altijd: laten staan.
///
/// <para>Het oordeel per factuur wordt bewaard in <c>gcp-facturen.json</c>, zodat de pdf niet
/// bij elke poll opnieuw gedownload wordt.</para>
/// </summary>
public static class GoogleCloudFactuur
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "gcp-facturen.json");

    private sealed class Oordeel
    {
        /// <summary>Het hoogste bedrag dat op de factuur gevonden is (null = niet kunnen lezen).</summary>
        public decimal? Bedrag { get; set; }

        public bool Nulfactuur { get; set; }
        public string Factuurnummer { get; set; } = "";
        public DateTimeOffset Gelezen { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Is dit de factuurmail van Google Cloud (met pdf-bijlage)?</summary>
    public static bool IsFactuurmail(MailBericht m) =>
        !m.IsChat && m.Uid > 0 &&
        m.VanAdres.Contains("payments-noreply@google.com", StringComparison.OrdinalIgnoreCase) &&
        m.Onderwerp.Contains("Google Cloud Platform", StringComparison.OrdinalIgnoreCase) &&
        (m.Onderwerp.Contains("factuur", StringComparison.OrdinalIgnoreCase) ||
         m.Onderwerp.Contains("invoice", StringComparison.OrdinalIgnoreCase)) &&
        m.Bijlagen.Any(b => b.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bekijkt de Google Cloud-factuurmails in de lijst en geeft de uid's terug van de
    /// facturen waarop niets te betalen staat — die mogen gearchiveerd worden. Alles wat niet
    /// met zekerheid nul is (bedrag erop, pdf onleesbaar, download mislukt) blijft eruit.
    /// </summary>
    public static async Task<HashSet<uint>> NulFacturenAsync(
        IEnumerable<MailBericht> berichten, MailReplySettings settings, CancellationToken ct)
    {
        var nul = new HashSet<uint>();
        var kandidaten = berichten.Where(IsFactuurmail).ToList();
        if (kandidaten.Count == 0)
        {
            return nul;
        }
        var state = Laad();
        var gewijzigd = false;
        foreach (var mail in kandidaten)
        {
            var sleutel = Sleutel(mail);
            if (!state.TryGetValue(sleutel, out var oordeel))
            {
                oordeel = await BeoordeelAsync(mail, settings, ct);
                if (oordeel is null)
                {
                    continue; // download mislukt: volgende poll opnieuw proberen
                }
                state[sleutel] = oordeel;
                gewijzigd = true;
            }
            if (oordeel.Nulfactuur)
            {
                nul.Add(mail.Uid);
            }
        }
        if (gewijzigd)
        {
            Bewaar(state);
        }
        return nul;
    }

    /// <summary>
    /// Downloadt de pdf en leest eruit of er iets te betalen valt. Null = niet gelukt (dan
    /// wordt er niets bewaard en probeert de volgende poll opnieuw).
    /// </summary>
    private static async Task<Oordeel?> BeoordeelAsync(
        MailBericht mail, MailReplySettings settings, CancellationToken ct)
    {
        var map = Path.Combine(Path.GetTempPath(), "WorkManager-gcp");
        try
        {
            Directory.CreateDirectory(map);
            var index = mail.Bijlagen.FindIndex(b =>
                b.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }
            var paden = await GmailClient.DownloadBijlagenAsync(
                settings, mail, map, new[] { (index, mail.Bijlagen[index]) }, ct);
            if (paden.Count == 0)
            {
                return null;
            }
            try
            {
                var tekst = PdfTekst.Lees(paden[0]);
                var (bedrag, nul) = Beoordeel(tekst);
                return new Oordeel
                {
                    Bedrag = bedrag,
                    Nulfactuur = nul,
                    Factuurnummer = Factuurnummer(mail),
                    Gelezen = DateTimeOffset.Now,
                };
            }
            finally
            {
                try
                {
                    File.Delete(paden[0]); // de factuur zelf blijft in Gmail staan
                }
                catch
                {
                    // Een achtergebleven tijdelijk bestand is geen probleem.
                }
            }
        }
        catch
        {
            return null; // geen netwerk, IMAP-fout, …: dan gewoon nog niets besluiten
        }
    }

    /// <summary>
    /// Het oordeel over de factuurtekst: het hoogste bedrag dat erop staat, en of dit met
    /// zekerheid een nulfactuur is. Zekerheid vraagt drie dingen: er zijn bedragen gevonden,
    /// ze zijn allemaal nul, en er staat een totaal-achtig label op de factuur. Anders zou
    /// een half gelukte tekstextractie (één btw-regel van 0,00) al kunnen doorslaan.
    /// </summary>
    public static (decimal? Hoogste, bool Nulfactuur) Beoordeel(string pdfTekst)
    {
        if (pdfTekst.Trim().Length == 0)
        {
            return (null, false);
        }
        var bedragen = new List<decimal>();
        // Bedragen met een valuta-aanduiding ervoor of erachter: € 12,34 / €12.34 / 12,34 EUR.
        // Twee decimalen zijn verplicht: zonder die eis leest "Page 2 of 2 €0.00" als het
        // bedrag 2, en dan zou een nulfactuur nooit als nul doorkomen.
        const string Bedrag = @"-?\d{1,3}(?:[.,]\d{3})*[.,]\d{2}";
        foreach (Match m in Regex.Matches(pdfTekst,
                     $@"(?:(?:€|EUR|\$|USD)\s?(?<voor>{Bedrag})|(?<na>{Bedrag})\s?(?:€|EUR|\$|USD))",
                     RegexOptions.IgnoreCase))
        {
            var ruw = m.Groups["voor"].Success ? m.Groups["voor"].Value : m.Groups["na"].Value;
            if (Ontleed(ruw) is { } waarde)
            {
                bedragen.Add(Math.Abs(waarde));
            }
        }
        // Zoeken in een kopie zonder witruimte: pdf-tekst komt er met kerningspaties uit
        // ("T otal in EUR"), en dan vindt een gewone zoekopdracht het label niet.
        var compact = Regex.Replace(pdfTekst, @"\s", "");
        var heeftTotaal = Regex.IsMatch(compact,
            @"totaal|total|tebetalen|amountdue|verschuldigd|subtotaal|subtotal",
            RegexOptions.IgnoreCase);
        if (bedragen.Count == 0)
        {
            return (null, false);
        }
        var hoogste = bedragen.Max();
        return (hoogste, hoogste == 0m && heeftTotaal);
    }

    /// <summary>
    /// Een bedrag uit een factuur ontleden. Zowel 1.234,56 (nl) als 1,234.56 (en) komen voor;
    /// het laatste scheidingsteken bepaalt wat de decimalen zijn.
    /// </summary>
    private static decimal? Ontleed(string ruw)
    {
        var schoon = Regex.Replace(ruw, @"\s", "");
        if (schoon.Length == 0)
        {
            return null;
        }
        var laatsteKomma = schoon.LastIndexOf(',');
        var laatstePunt = schoon.LastIndexOf('.');
        string genormaliseerd;
        if (laatsteKomma > laatstePunt)
        {
            genormaliseerd = schoon.Replace(".", "").Replace(',', '.');
        }
        else if (laatstePunt > laatsteKomma)
        {
            genormaliseerd = schoon.Replace(",", "");
        }
        else
        {
            genormaliseerd = schoon;
        }
        return decimal.TryParse(genormaliseerd, NumberStyles.Any, CultureInfo.InvariantCulture,
            out var waarde)
            ? waarde
            : null;
    }

    /// <summary>Het factuurnummer uit de mailtekst ("Factuurnummer 5710566237"), voor het logboek.</summary>
    private static string Factuurnummer(MailBericht mail)
    {
        var m = Regex.Match(mail.Tekst.Length > 0 ? mail.Tekst : mail.Html,
            @"(?:Factuurnummer|Invoice number)\s*:?\s*(\d{6,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Sleutel per factuur: het factuurnummer als dat te vinden is, anders de message-id.
    /// Niet de uid — die verandert als de mail verplaatst wordt.
    /// </summary>
    private static string Sleutel(MailBericht mail)
    {
        var nummer = Factuurnummer(mail);
        return nummer.Length > 0 ? nummer
            : mail.MessageId.Length > 0 ? mail.MessageId
            : $"uid-{mail.Uid}";
    }

    private static Dictionary<string, Oordeel> Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Dictionary<string, Oordeel>>(
                    File.ReadAllText(StateFile), JsonOpts) is { } data)
            {
                return data;
            }
        }
        catch
        {
            // Onleesbaar: dan beoordelen we de facturen opnieuw.
        }
        return new Dictionary<string, Oordeel>(StringComparer.Ordinal);
    }

    private static void Bewaar(Dictionary<string, Oordeel> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // Zonder geheugen wordt de pdf elke poll opnieuw gelezen; hinderlijk, niet fout.
        }
    }
}
