using System.Text;

namespace WorkManager;

/// <summary>
/// Headless regressietests voor de kwetsbaarste tekstparsers, gestart met
/// "WorkManager.exe --parsertests". Geen testframework: gewoon een lijst controles met
/// het resultaat in %APPDATA%\WorkManager\parser-tests.txt; de exitcode is het aantal
/// fouten (0 = alles groen). Bedoeld om na een parserwijziging snel te zien of de
/// bestaande gevallen nog kloppen. De OWA-voorbeelden komen uit echte debugdumps.
/// </summary>
public static class ParserTests
{
    private static readonly StringBuilder Verslag = new();
    private static int _fouten;
    private static int _totaal;

    public static int Draai()
    {
        BedragTests();
        AfwezigheidTests();
        AfwezigheidsRegelTests();
        AhLinkTests();
        OwaAgendaTests();
        MailKopTests();
        O365DetailsTests();
        BerichtFilterTests();

        Verslag.AppendLine();
        Verslag.AppendLine($"{_totaal - _fouten}/{_totaal} geslaagd, {_fouten} fout(en).");
        try
        {
            var pad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "parser-tests.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(pad)!);
            File.WriteAllText(pad, Verslag.ToString());
        }
        catch
        {
            // Verslag is bijzaak; de exitcode telt.
        }
        return _fouten;
    }

    private static void Check(string naam, object? verwacht, object? echt)
    {
        _totaal++;
        var ok = Equals(verwacht?.ToString(), echt?.ToString());
        if (!ok)
        {
            _fouten++;
        }
        Verslag.AppendLine($"{(ok ? "OK  " : "FOUT")} {naam}: verwacht [{verwacht}], kreeg [{echt}]");
    }

    private static void CheckBevat(string naam, string deel, string echt, bool moetBevatten = true)
    {
        _totaal++;
        var ok = echt.Contains(deel, StringComparison.OrdinalIgnoreCase) == moetBevatten;
        if (!ok)
        {
            _fouten++;
        }
        Verslag.AppendLine($"{(ok ? "OK  " : "FOUT")} {naam}" +
            (ok ? "" : $": {(moetBevatten ? "mist" : "bevat onterecht")} [{deel}] in [{echt}]"));
    }

    /// <summary>Bedragen in Belgische en Engelse notatie (InvoiceApprovalForm.ParseBedrag).</summary>
    private static void BedragTests()
    {
        Verslag.AppendLine("— Bedragen —");
        Check("1.234,56", 1234.56m, InvoiceApprovalForm.ParseBedrag("1.234,56"));
        Check("1,234.56", 1234.56m, InvoiceApprovalForm.ParseBedrag("1,234.56"));
        Check("1.234 (duizendtal)", 1234m, InvoiceApprovalForm.ParseBedrag("1.234"));
        Check("1.234.567", 1234567m, InvoiceApprovalForm.ParseBedrag("1.234.567"));
        Check("12,5", 12.5m, InvoiceApprovalForm.ParseBedrag("12,5"));
        // 99.00m: ParseBedrag houdt de twee decimalen aan en Check vergelijkt als tekst.
        Check("€ 99,00", 99.00m, InvoiceApprovalForm.ParseBedrag("€ 99,00"));
        Check("leeg", null, InvoiceApprovalForm.ParseBedrag(""));
        Check("alleen tekst", null, InvoiceApprovalForm.ParseBedrag("n.v.t."));
    }

    /// <summary>SD Worx-afwezigheidsregels uit de weekmail-opmerking (teamtakenvenster).</summary>
    private static void AfwezigheidTests()
    {
        Verslag.AppendLine("— Afwezigheden uit de opmerking —");
        var vandaag = new DateOnly(2026, 8, 5);

        string Dump(string opmerking) => string.Join("; ",
            SdWorxVakanties.ParseAfwezigheden(opmerking, vandaag)
                .Select(a => $"{a.Naam} {a.Van:yyyy-MM-dd}..{a.Tot:yyyy-MM-dd}"));

        Check("is afwezig t.e.m.",
            "Christophe 2026-08-05..2026-08-14",
            Dump("Christophe is afwezig t.e.m. vr 14/8, terug op ma 17/8."));
        Check("afwezig van … t.e.m. …",
            "Wim 2026-08-10..2026-08-21",
            Dump("Wim afwezig van ma 10/8 t.e.m. vr 21/8, terug op ma 24/8."));
        Check("afwezig op (één dag)",
            "Alex 2026-08-14..2026-08-14",
            Dump("Alex afwezig op vr 14/8."));
        Check("langdurig afwezig",
            "Kris 2026-08-05..2026-09-30",
            Dump("Kris is langdurig afwezig, zeker t.e.m. wo 30/9."));
        Check("vanaf … langdurig afwezig",
            "Henny 2026-08-10..2026-09-30",
            Dump("Henny is vanaf ma 10/8 langdurig afwezig, zeker t.e.m. wo 30/9."));
        Check("feestdagregel telt niet mee", "", Dump("Feestdag op ma 21/7."));
        Check("jaarwissel",
            "Wim 2026-12-28..2027-01-08",
            string.Join("; ", SdWorxVakanties
                .ParseAfwezigheden("Wim afwezig van ma 28/12 t.e.m. vr 8/1, terug op ma 11/1.",
                    new DateOnly(2026, 12, 20))
                .Select(a => $"{a.Naam} {a.Van:yyyy-MM-dd}..{a.Tot:yyyy-MM-dd}")));
        Check("meerdere regels",
            "Christophe 2026-08-05..2026-08-14; Wim 2026-08-05..2026-08-13",
            Dump("Christophe is afwezig t.e.m. vr 14/8, terug op ma 17/8.\n" +
                 "Wim is afwezig t.e.m. do 13/8, terug op ma 17/8."));
    }

    /// <summary>Herkenning van afwezigheidsregels (vervangen i.p.v. opstapelen bij herladen).</summary>
    private static void AfwezigheidsRegelTests()
    {
        Verslag.AppendLine("— IsAfwezigheidsRegel —");
        Check("afwezig t.e.m.", true,
            SdWorxVakanties.IsAfwezigheidsRegel("Christophe is afwezig t.e.m. vr 14/8, terug op ma 17/8."));
        Check("feestdag", true, SdWorxVakanties.IsAfwezigheidsRegel("Feestdag op ma 21/7."));
        Check("vervangingsfeestdag", true,
            SdWorxVakanties.IsAfwezigheidsRegel("Vervangingsfeestdag op vr 25/7."));
        Check("gewone opmerking", false,
            SdWorxVakanties.IsAfwezigheidsRegel("Denk aan de releaseplanning van augustus."));
    }

    /// <summary>Webshop-id's uit AH-productlinks (AhApi.WebshopId).</summary>
    private static void AhLinkTests()
    {
        Verslag.AppendLine("— AH-productlinks —");
        Check("productlink", "4076",
            AhApi.WebshopId("https://www.ah.be/producten/product/wi4076/ah-winterpeen"));
        Check("hoofdletters", "159760",
            AhApi.WebshopId("https://www.ah.be/producten/product/WI159760/ah-spaghetti"));
        Check("geen productlink", null, AhApi.WebshopId("https://www.ah.be/bonus"));
        Check("lege url", null, AhApi.WebshopId(""));
    }

    /// <summary>OWA-agendalabels → schone meetingtitels (OutlookClient.SchoonAgendaTitel).</summary>
    private static void OwaAgendaTests()
    {
        Verslag.AppendLine("— OWA-agendatitels —");
        Check("NL Teams-meeting", "IT-meeting",
            OutlookClient.SchoonAgendaTitel(
                "IT-meeting, , Dinsdag, 4 Augustus, 2026, Microsoft Teams-vergadering, " +
                "Door Maarten Bergmans, Busy, Terugkerende gebeurtenis"));
        Check("fysieke meeting", "Workshop – Priorisation des Quick Wins Mobility & Roadmap",
            OutlookClient.SchoonAgendaTitel(
                "Workshop – Priorisation des Quick Wins Mobility & Roadmap, , Dinsdag, " +
                "4 Augustus, 2026, Meetingroom Vilvoorde (BE), Door Ludovic Leleu, Busy"));
        Check("EN Teams-meeting", "Follow-uw before go-live K4K FLEX",
            OutlookClient.SchoonAgendaTitel(
                "Follow-uw before go-live K4K FLEX, , Dinsdag, 4 Augustus, 2026, " +
                "Microsoft Teams Meeting, Door LONNOY Michael, Busy"));
        Check("titel met streep en slash", "Charlie - progress / testing",
            OutlookClient.SchoonAgendaTitel(
                "Charlie - progress / testing, , Donderdag, 6 Augustus, 2026, " +
                "Microsoft Teams-vergadering, Door Tiemen Schotsaert, Tentative"));
        Check("al schone titel blijft heel", "AI steerco",
            OutlookClient.SchoonAgendaTitel("AI steerco"));
    }

    /// <summary>Het exacte ontvangstmoment uit de OWA-mailkop (ParseVolledigMoment).</summary>
    private static void MailKopTests()
    {
        Verslag.AppendLine("— OWA-mailkop —");
        Check("cijferdatum", "2026-07-27 13:16",
            OutlookClient.ParseVolledigMoment("ma 27-7-2026 13:16")
                ?.ToString("yyyy-MM-dd HH:mm") ?? "(null)");
        Check("maandnaam", "2026-07-27 13:16",
            OutlookClient.ParseVolledigMoment("maandag 27 juli 2026 om 13:16")
                ?.ToString("yyyy-MM-dd HH:mm") ?? "(null)");
    }

    /// <summary>O365-afspraakdetails: kernsignalen overleven, UI-ruis verdwijnt.</summary>
    private static void O365DetailsTests()
    {
        Verslag.AppendLine("— O365-details —");
        var details = CockpitForm.SchoonO365Details(
            "IT-meeting\r\nDi 4-8-2026 13:00 - 13:45\r\nDeelnemen\r\nRSVP\r\n" +
            "Maarten Bergmans heeft u uitgenodigd\r\n" +
            "LONNOY Michael; Wim Peeters; Kris Van Leuffelen\r\n" +
            "Deelnemen: https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0\r\n" +
            "Copilot samenvatting\r\nAgendapunten: cijfers Q3 bespreken",
            "IT-meeting");
        CheckBevat("joinlink blijft staan", "https://teams.microsoft.com/l/meetup-join", details);
        CheckBevat("deelnemers blijven staan", "LONNOY Michael", details);
        CheckBevat("omschrijving blijft staan", "cijfers Q3", details);
        CheckBevat("Copilot-ruis verdwijnt", "Copilot", details, moetBevatten: false);
        CheckBevat("losse RSVP-knop verdwijnt", "RSVP", details, moetBevatten: false);
    }

    /// <summary>
    /// De weergavefilter: wat onder een archiveerregel valt mag nooit in de lijst komen —
    /// ook niet even. Hier wordt met expliciete regels getest (niet met archiveer-regels.json),
    /// zodat de uitkomst niet van de eigen regels van de gebruiker afhangt.
    /// </summary>
    private static void BerichtFilterTests()
    {
        Verslag.AppendLine("— Weergavefilter berichten —");
        var regels = new List<ArchiveerRegel>
        {
            new() { Afzender = "SharePoint Online", Onderwerp = "Nieuws dat je mogelijk hebt gemist" },
            new() { Afzender = "nieuwsbrief@voorbeeld.be" },
            new() { Onderwerp = "Weekoverzicht van je saldo" },
            new() { }, // lege regel: mag niets raken
        };

        static MailBericht Mail(string van, string onderwerp, string vanAdres = "") => new()
        {
            Uid = 42,
            Van = van,
            VanAdres = vanAdres.Length > 0 ? vanAdres : van,
            Onderwerp = onderwerp,
            MessageId = $"<{van}/{onderwerp}>",
        };

        static MailBericht Ced(string van, string onderwerp) => new()
        {
            Van = van,
            VanAdres = "CED Outlook",
            Onderwerp = onderwerp,
            OutlookMail = $"{van}|{onderwerp}",
            MessageId = $"owa:{van}/{onderwerp}",
        };

        bool Verborgen(MailBericht m) => BerichtFilter.Verbergen(m, regels);

        // Vaste Gmail-regels.
        Check("Netflix-bevestiging", true,
            Verborgen(Mail("Netflix", "Je abonnement", "info@account.netflix.com")));
        Check("SMS-credits bijgeschreven", true,
            Verborgen(Mail("JAAN bv", "500 SMS credits zijn bijgeschreven")));
        Check("Storingsmail MailMobility", true,
            Verborgen(Mail("IT Support", "MailMobility onbereikbaar", "it-support@ced.group")));
        Check("Gewone mail blijft staan", false,
            Verborgen(Mail("Jan Peeters", "Vraag over het dossier", "jan@klant.be")));

        // Zelfgemaakte regels, op afzender én onderwerp / alleen afzender / alleen onderwerp.
        Check("Eigen regel: afzender + onderwerp", true,
            Verborgen(Mail("SharePoint Online", "Nieuws dat je mogelijk hebt gemist")));
        Check("Eigen regel: zelfde afzender, ander onderwerp", false,
            Verborgen(Mail("SharePoint Online", "Je bent toegevoegd aan een site")));
        Check("Eigen regel: alleen afzender", true,
            Verborgen(Mail("Voorbeeld bv", "Wat dan ook", "nieuwsbrief@voorbeeld.be")));
        Check("Eigen regel: alleen onderwerp", true,
            Verborgen(Mail("Wie dan ook", "JAAN bv - Weekoverzicht van je saldo")));
        Check("Lege regel raakt niets", false,
            Verborgen(Mail("Onbekend", "Zonder trefwoord")));

        // CED-mails (Outlook): vaste regels én dezelfde zelfgemaakte regels.
        Check("CED: reacties dagelijks overzicht", true,
            Verborgen(Ced("CED Belgium", "Reacties dagelijks overzicht 3/10")));
        Check("CED: telefoniestatistiek", true,
            Verborgen(Ced("NoReply Belgium", "Rapport;Employee Group Performance by Employee;okt")));
        Check("CED: NoReply met ander onderwerp", false,
            Verborgen(Ced("NoReply Belgium", "Wachtwoord verloopt binnenkort")));
        Check("CED: CyberVadis-rapport", true,
            Verborgen(Ced("CyberVadis", "Your monthly CyberVadis report")));
        Check("CED: eigen regel geldt ook hier", true,
            Verborgen(Ced("SharePoint Online", "Nieuws dat je mogelijk hebt gemist")));
        Check("CED: gewone mail blijft staan", false,
            Verborgen(Ced("Collega", "Planning volgende week")));

        // Afgehandelde chats blijven weg; een openstaande chat niet. En de CC-overzichtsrij
        // is geen echte mail: die heeft zijn eigen afhandeling en mag nooit wegvallen.
        var chat = new MailBericht
        {
            TeamsChat = "Collega",
            Van = "Collega",
            Onderwerp = "ok dan",
            MessageId = "teams:Collega|ok dan",
        };
        Check("Chat openstaand", false, Verborgen(chat));
        chat.Genegeerd = true;
        Check("Chat afgehandeld", true, Verborgen(chat));
        Check("CC-overzichtsrij blijft staan", false,
            Verborgen(Mail("SharePoint Online", "Nieuws dat je mogelijk hebt gemist", "CC-map")));

        // En de lijstvariant: alleen de zichtbare rijen blijven over.
        var lijst = new List<MailBericht>
        {
            Mail("Jan Peeters", "Vraag over het dossier", "jan@klant.be"),
            Mail("Netflix", "Je abonnement", "info@account.netflix.com"),
            Ced("CED Belgium", "Reacties dagelijks overzicht 3/10"),
            Ced("Collega", "Planning volgende week"),
        };
        lijst.RemoveAll(m => BerichtFilter.Verbergen(m, regels));
        Check("Lijst: aantal zichtbaar", 2, lijst.Count);
        CheckBevat("Lijst: klantmail blijft", "Jan Peeters", string.Join(", ", lijst.Select(m => m.Van)));
        CheckBevat("Lijst: Netflix weg", "Netflix",
            string.Join(", ", lijst.Select(m => m.Van)), moetBevatten: false);
    }
}
