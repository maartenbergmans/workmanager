using System.Diagnostics;

namespace WorkManager;

/// <summary>
/// De bron van een taak: het bericht, bestand, de map of de webpagina waar de taak vandaan
/// komt. Zowel de cockpit als "Mijn taken" laten die bewerken, dus staan de regels hier
/// één keer.
/// </summary>
public static class TaakBron
{
    /// <summary>
    /// Maakt van wat er in het bronveld staat de bron van de taak. Een leeg veld haalt de
    /// koppeling weg, maar laat een écht bronbericht (met tekst of message-id) staan: anders
    /// zou je het antwoordblok van die mail kwijtspelen. Bij een taak zonder bronbericht
    /// levert een link een nieuwe, minimale bron op met een leesbare naam.
    /// </summary>
    public static TaakMail? UitLink(string link, TaakMail? huidig)
    {
        if (huidig is { } h && (h.Tekst.Length > 0 || h.MessageId.Length > 0))
        {
            h.Link = link;
            return h;
        }
        if (link.Length == 0)
        {
            return null;
        }
        var isUrl = Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile;
        return new TaakMail
        {
            Link = link,
            Van = isUrl ? uri!.Host : (Directory.Exists(link) ? "Map" : "Bestand"),
            Onderwerp = isUrl ? Kort(uri!.AbsolutePath.Trim('/'), 80) : Path.GetFileName(link.TrimEnd('\\', '/')),
            Datum = DateTimeOffset.Now,
        };
    }

    /// <summary>Omschrijving van de huidige bron voor onder het invoerveld.</summary>
    public static string Omschrijving(TaakMail? bron) =>
        bron is { } b && (b.Van.Length > 0 || b.Onderwerp.Length > 0)
            ? $"bericht van {Kort(b.Van, 24)} — {Kort(b.Onderwerp, 34)}"
            : "link, bestand of map (slepen mag) — leeg = geen bron";

    /// <summary>Opent de bron met de standaardtoepassing van Windows (browser, Verkenner, …).</summary>
    public static void Open(string link)
    {
        if (link.Trim().Length == 0)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(link.Trim()) { UseShellExecute = true });
        }
        catch
        {
            // Onbekend protocol of verdwenen pad: stilletjes negeren.
        }
    }

    /// <summary>
    /// Klein venster om alleen de bron van één taak te bewerken. Past de taak aan en geeft
    /// true als er iets veranderd is; bewaren doet de aanroeper (die weet in welke lijst de
    /// taak zit).
    /// </summary>
    public static bool Bewerk(IWin32Window eigenaar, MijnTaak taak)
    {
        using var dialog = new Form
        {
            Text = "Bron van de taak",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(462, 176),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var taakLabel = new Label
        {
            Text = Kort(taak.Tekst.ReplaceLineEndings(" · "), 70),
            AutoSize = true,
            Location = new Point(16, 14),
        };
        var veldLabel = new Label { Text = "Bron", AutoSize = true, Location = new Point(16, 46) };
        var bronBox = new TextBox
        {
            Text = taak.Mail?.Link ?? "",
            Location = new Point(16, 68),
            Width = 360,
            AllowDrop = true,
        };
        // Slepen vanuit de Verkenner is de snelste manier om een map of bestand te koppelen.
        bronBox.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        bronBox.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paden)
            {
                bronBox.Text = paden[0];
            }
        };
        var openKnop = new ModernButton
        {
            Text = "Openen", Height = 28, Width = 66, Location = new Point(380, 67),
        };
        openKnop.Click += (_, _) => Open(bronBox.Text);
        var hint = new Label
        {
            Text = Omschrijving(taak.Mail),
            AutoSize = true,
            Location = new Point(16, 100),
        };
        bronBox.TextChanged += (_, _) => openKnop.Enabled = bronBox.Text.Trim().Length > 0;
        openKnop.Enabled = bronBox.Text.Trim().Length > 0;
        var ok = new ModernButton
        {
            Text = "Opslaan", Width = 115, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(331, 130),
        };
        var annuleer = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(221, 130),
        };
        dialog.Controls.AddRange(new Control[] { taakLabel, veldLabel, bronBox, openKnop, hint, ok, annuleer });
        dialog.AcceptButton = ok;
        dialog.CancelButton = annuleer;
        Theme.Apply(dialog);
        taakLabel.ForeColor = Theme.Muted;
        hint.ForeColor = Theme.Muted;
        if (dialog.ShowDialog(eigenaar) != DialogResult.OK)
        {
            return false;
        }
        var nieuw = bronBox.Text.Trim();
        if (string.Equals(nieuw, taak.Mail?.Link ?? "", StringComparison.Ordinal))
        {
            return false;
        }
        taak.Mail = UitLink(nieuw, taak.Mail);
        return true;
    }

    private static string Kort(string tekst, int max) =>
        tekst.Length <= max ? tekst : tekst[..max] + "…";
}
