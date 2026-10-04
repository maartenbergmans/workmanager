using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Eén waarheid over wat er in de berichtenlijst mag komen. Berichten die volgens een vaste
/// of zelfgemaakte regel toch meteen gearchiveerd worden, horen nooit in beeld te komen —
/// ook niet eventjes. Dat kon vroeger wel: de CED-regels werden pas ná het ophalen van
/// Outlook en Smartschool toegepast, dus die twee tussenstanden lieten zo'n mail nog
/// oplichten om er een paar seconden later weer uit te verdwijnen.
///
/// Daarom beslist deze klasse vóór élke weergave (elke tussenstand, de eindstand, de lijst
/// uit de cache bij het openen van de cockpit) of een bericht zichtbaar mag zijn — los van
/// de vraag of het archiveren zelf al gelukt is. Diezelfde regels worden door de pollronde
/// gebruikt om het echte archiveren te kiezen, zodat weergave en archivering niet uit elkaar
/// kunnen groeien.
/// </summary>
public static class BerichtFilter
{
    /// <summary>
    /// Vaste regels voor Gmail: routinemails die altijd meteen het archief in gaan —
    /// Netflix-bevestigingen, de e-Box-meldingsmail (die zet eerst de cockpitknop aan), de
    /// JAAN bv "SMS credits bijgeschreven"-meldingen (het aantal in het onderwerp varieert,
    /// dus op de vaste kern matchen), storingsmails van IT-support (die worden eerst als
    /// rode taak geregistreerd), de maandelijkse Apple-factuur van € 0,99 (één per jaar
    /// tonen, in januari), de Seety-bon van een gratis parkeersessie en de AH-bestel- en
    /// bezorgmails (die worden eerst tot taak en agenda-afspraak verwerkt).
    ///
    /// De Google Cloud-factuur van € 0,00 telt alleen mee als de pdf-bijlage al gelezen is:
    /// het bedrag staat niet in de mail zelf, en zonder oordeel blijft zo'n factuur juist
    /// staan. Dat lezen doet de pollronde (zie <see cref="GoogleCloudFactuur"/>).
    /// </summary>
    public static bool GmailVast(MailBericht m) =>
        m.VanAdres.Contains("account.netflix.com", StringComparison.OrdinalIgnoreCase) ||
        EboxForm.IsMeldingsmail(m) ||
        Regex.IsMatch(m.Onderwerp, @"SMS[\s-]*credits zijn bijgeschreven", RegexOptions.IgnoreCase) ||
        AlarmMails.Matcht(m) ||
        AppleFactuur.MoetArchiveren(m) ||
        SeetyBon.IsGratisBon(m) ||
        GoogleCloudFactuur.IsBekendeNulfactuur(m) ||
        AhLevering.Matcht(m);

    /// <summary>
    /// Vaste regels voor de CED-mailbox (Outlook): "Reactie(s) dagelijks overzicht", de
    /// telefoniestatistieken van NoReply Belgium ("…;Employee Group Performance by
    /// Employee;…"), het maandelijkse CyberVadis-rapport en de storingsmails.
    /// </summary>
    public static bool CedVast(MailBericht m) =>
        Regex.IsMatch(m.Onderwerp, @"reacties?\s+dagelijks\s+overzicht", RegexOptions.IgnoreCase) ||
        (m.Van.Contains("NoReply Belgium", StringComparison.OrdinalIgnoreCase) &&
         m.Onderwerp.Contains("Employee Group Performance", StringComparison.OrdinalIgnoreCase)) ||
        m.Onderwerp.Contains("monthly CyberVadis report", StringComparison.OrdinalIgnoreCase) ||
        AlarmMails.Matcht(m);

    /// <summary>
    /// Moet dit bericht uit de weergave blijven? Dat geldt voor alles wat een regel raakt
    /// (vast of zelfgemaakt) en voor wat al als afgehandeld gemarkeerd staat: een
    /// gearchiveerde chat of CED-mail (de vlag <see cref="MailBericht.Genegeerd"/> uit de
    /// conceptcache).
    ///
    /// Bewust ruimer dan "is het archiveren gelukt": mislukt dat (IMAP eruit, Outlook-sessie
    /// weg), dan blijft de mail gewoon in zijn eigen postvak staan en probeert de volgende
    /// pollronde het opnieuw — maar in de cockpit is hij dan nooit even zichtbaar geweest.
    /// </summary>
    public static bool Verbergen(MailBericht m, List<ArchiveerRegel> regels)
    {
        if (m.IsChat && m.Genegeerd)
        {
            return true; // gearchiveerd/afgehandeld: blijft weg tot er een nieuw bericht komt
        }
        if (m.OutlookMail.Length > 0)
        {
            return CedVast(m) || ArchiveerRegels.Matcht(m, regels);
        }
        if (m.IsChat)
        {
            return false; // chats (Teams, WhatsApp, Google Chat, Smartschool): geen mailregels
        }
        // Gmail. De CC-overzichtsrij is geen echte mail en heeft zijn eigen afhandeling.
        return m.VanAdres != "CC-map" && (GmailVast(m) || ArchiveerRegels.Matcht(m, regels));
    }

    /// <summary>Zelfde beslissing, met de regels uit de (gecachete) regelbestanden.</summary>
    public static bool Verbergen(MailBericht m) =>
        Verbergen(m, ArchiveerRegels.LoadGecached());

    /// <summary>
    /// Haalt alles wat niet in beeld mag uit de lijst. Dit draait kort vóór het vullen van
    /// de lijst — niet in de pollronde zelf, want daar is elk bericht nog nodig: de
    /// storingsmelder, de AH-verwerking, de knopsignalen (TopDesk, DevOps, verlof, e-Box) en
    /// het zelfherstel van niet-verplaatste Outlook-mails kijken juist naar wat er binnenkwam.
    /// </summary>
    public static void Verberg(List<MailBericht> berichten)
    {
        var regels = ArchiveerRegels.LoadGecached();
        berichten.RemoveAll(m => Verbergen(m, regels));
    }
}
