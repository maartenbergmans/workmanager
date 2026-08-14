namespace WorkManager;

/// <summary>
/// Bijlagen van een mail rechtstreeks in een Google Drive-map zetten. Wordt vanuit twee
/// plekken gebruikt (de berichtenlijst in de cockpit en het mailvenster), vandaar dat de
/// hele flow — map kiezen, bijlagen aanvinken, downloaden, uploaden — hier samen zit.
///
/// De bijlagen gaan via een tijdelijke map: de IMAP-download bestaat al en schrijft naar
/// schijf, en die code twee keer hebben is erger dan één keer een tempmap opruimen.
/// </summary>
public static class BijlagenNaarDrive
{
    /// <summary>Heeft deze mail iets om op te slaan?</summary>
    public static bool HeeftBijlagen(MailBericht? mail) =>
        mail is not null && (mail.Bijlagen.Count > 0 || mail.LinkBijlagen.Count > 0);

    /// <summary>
    /// Bouwt het submenu "Bijlagen opslaan in ▸": eerst de favorieten, dan de recent gebruikte
    /// mappen, dan "Andere map…". <paramref name="opslaan"/> krijgt id en naam van de keuze;
    /// een leeg id betekent: open eerst de mapkiezer.
    /// </summary>
    public static ToolStripMenuItem Submenu(Action<string, string> opslaan)
    {
        var menu = new ToolStripMenuItem("Bijlagen opslaan in");
        // Placeholder zodat het pijltje verschijnt; de echte inhoud komt bij elke opening vers,
        // want de recente mappen verschuiven na elke keer opslaan.
        menu.DropDownItems.Add(new ToolStripMenuItem("…"));
        menu.DropDownOpening += (_, _) => Vul(menu, opslaan);
        return menu;
    }

    private static void Vul(ToolStripMenuItem menu, Action<string, string> opslaan)
    {
        menu.DropDownItems.Clear();

        foreach (var (naam, id) in DriveMappen.Favorieten)
        {
            if (id.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                continue;
            }
            var item = new ToolStripMenuItem(naam);
            item.Click += (_, _) => opslaan(id, naam);
            menu.DropDownItems.Add(item);
        }

        var recent = DriveMappen.Recent();
        if (recent.Count > 0)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var map in recent)
            {
                var item = new ToolStripMenuItem("↻ " + map.Naam);
                var id = map.Id;
                var naam = map.Naam;
                item.Click += (_, _) => opslaan(id, naam);
                menu.DropDownItems.Add(item);
            }
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        var ander = new ToolStripMenuItem("Andere map…");
        ander.Click += (_, _) => opslaan("", "");
        menu.DropDownItems.Add(ander);
    }

    /// <summary>
    /// Zonder vensters: álle bijlagen (en downloadlinks) van een mail naar de opgegeven map,
    /// of naar de laatst gebruikte map als er geen meegegeven is. Nodig voor de webversie —
    /// daar staat niemand achter de pc om een mapkiezer weg te klikken. Geeft de melding voor
    /// op de gsm terug.
    /// </summary>
    public static async Task<string> StilNaarDriveAsync(
        MailReplySettings mailSettings, MailBericht mail, string mapId, CancellationToken ct)
    {
        var drive = GoogleChatSettings.Load();
        if (!drive.Gekoppeld)
        {
            return "Google is nog niet gekoppeld op de pc.";
        }
        var mapNaam = "Drive";
        if (mapId.Length == 0)
        {
            // De map waar je het laatst iets in zette is verreweg de beste gok; anders de
            // eerste favoriet.
            var recent = DriveMappen.Recent().FirstOrDefault();
            if (recent is not null)
            {
                (mapId, mapNaam) = (recent.Id, recent.Naam);
            }
            else if (DriveMappen.Favorieten.Length > 0)
            {
                (mapId, mapNaam) = (DriveMappen.Favorieten[0].Id, DriveMappen.Favorieten[0].Naam);
            }
            else
            {
                return "Nog geen Drive-map bekend — sla er op de pc één keer iets in op.";
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), "WorkManager-drive-" + Guid.NewGuid().ToString("N")[..8]);
        var geupload = new List<string>();
        var mislukt = 0;
        try
        {
            Directory.CreateDirectory(temp);
            var paden = new List<string>();
            if (mail.Bijlagen.Count > 0)
            {
                paden.AddRange(await GmailClient.DownloadBijlagenAsync(
                    mailSettings, mail, temp,
                    mail.Bijlagen.Select((naam, i) => (Index: i, Naam: naam)).ToList(), ct));
            }
            foreach (var link in mail.LinkBijlagen)
            {
                try
                {
                    paden.Add(await GmailClient.DownloadLinkAsync(link.Url, temp, link.Naam, ct));
                }
                catch
                {
                    mislukt++;
                }
            }
            foreach (var pad in paden)
            {
                try
                {
                    geupload.Add(await GoogleDriveClient.UploadAsync(drive, mapId, pad, ct));
                }
                catch
                {
                    mislukt++;
                }
            }
            if (geupload.Count > 0)
            {
                DriveMappen.OnthoudGebruik(mapId, mapNaam);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Tempmap blijft staan; Windows ruimt die zelf op.
            }
        }

        var staart = mislukt > 0 ? $" ({mislukt} niet gelukt)" : "";
        return geupload.Count == 0
            ? $"Geen bijlagen opgeslagen{staart}."
            : $"In Drive-map \"{mapNaam}\": {string.Join(", ", geupload)}{staart}";
    }

    /// <summary>
    /// De volledige flow. Met een leeg <paramref name="mapId"/> opent eerst de mapkiezer.
    /// Geeft via <paramref name="log"/> voortgang door en retourneert de geüploade namen.
    /// </summary>
    public static async Task<List<string>> UitvoerenAsync(
        IWin32Window eigenaar, MailReplySettings mailSettings, MailBericht mail,
        string mapId, string mapNaam, Action<string> log, CancellationToken ct)
    {
        var drive = GoogleChatSettings.Load();
        if (!drive.Gekoppeld)
        {
            log("Google is nog niet gekoppeld (zie Instellingen) — bijlagen niet opgeslagen.");
            return new List<string>();
        }

        if (mapId.Length == 0)
        {
            using var kiezer = new DriveMapKiezerForm(drive);
            if (kiezer.ShowDialog(eigenaar) != DialogResult.OK)
            {
                return new List<string>();
            }
            mapId = kiezer.GekozenId;
            mapNaam = kiezer.GekozenNaam;
        }

        using var dialog = new BijlagenForm(mail, "", doorsturen: false, driveMap: mapNaam);
        if (dialog.ShowDialog(eigenaar) != DialogResult.OK)
        {
            return new List<string>();
        }

        var temp = Path.Combine(Path.GetTempPath(), "WorkManager-drive-" + Guid.NewGuid().ToString("N")[..8]);
        var geupload = new List<string>();
        try
        {
            Directory.CreateDirectory(temp);
            log($"Bijlagen van \"{mail.Onderwerp}\" naar Drive-map \"{mapNaam}\"…");

            var paden = new List<string>();
            if (dialog.Selectie.Count > 0)
            {
                paden.AddRange(await GmailClient.DownloadBijlagenAsync(
                    mailSettings, mail, temp, dialog.Selectie, ct));
            }
            foreach (var (url, naam) in dialog.LinkSelectie)
            {
                try
                {
                    paden.Add(await GmailClient.DownloadLinkAsync(url, temp, naam, ct));
                }
                catch (Exception ex)
                {
                    log($"Downloaden van link \"{naam}\" mislukt: {ex.Message}");
                }
            }

            foreach (var pad in paden)
            {
                try
                {
                    geupload.Add(await GoogleDriveClient.UploadAsync(drive, mapId, pad, ct));
                }
                catch (Exception ex)
                {
                    log($"Uploaden van \"{Path.GetFileName(pad)}\" mislukt: {ex.Message}");
                }
            }

            if (geupload.Count > 0)
            {
                DriveMappen.OnthoudGebruik(mapId, mapNaam);
            }
            log(geupload.Count == 0
                ? "Geen bijlagen naar Drive opgeslagen."
                : $"Naar Drive-map \"{mapNaam}\": {string.Join(", ", geupload)}");
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het downloaden of uploaden.
        }
        catch (Exception ex)
        {
            log($"Bijlagen naar Drive opslaan mislukt: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Tempmap blijft staan; Windows ruimt die zelf op.
            }
        }
        return geupload;
    }
}
