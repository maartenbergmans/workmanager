using System.Diagnostics;

namespace WorkManager;

/// <summary>
/// Instellen en openen van de online git-tabel (git.php op de hosting): het adres, het token,
/// een QR-code en de link die je aan een collega geeft. Op die pagina staat per project wat er
/// ongecommit openstaat, elk uur bijgewerkt door de pc.
///
/// <para>De link is leesbaar voor wie hem heeft — bedoeld om te delen met een collega, maar
/// daarom ook met een eigen token: hij geeft nergens anders toegang tot, en je kunt hem
/// intrekken door het token op de hosting te wijzigen.</para>
/// </summary>
public sealed class GitWebForm : Form
{
    private readonly TextBox _url;
    private readonly TextBox _token;
    private readonly CheckBox _bestandsnamen;
    private readonly Label _status;
    private readonly PictureBox _qr;

    public GitWebForm()
    {
        Text = "Git online (om te laten meevolgen)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(790, 650);
        MinimumSize = new Size(620, 520);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "gitweb");

        var settings = GitWebSettings.Load();

        var uitleg = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 76,
            Padding = new Padding(2, 4, 2, 8),
            Text = "Deze pagina toont per project welke bestanden er nog ongecommit zijn, " +
                   "met hoe lang ze al openstaan. De pc zet de stand elk uur online.\r\n" +
                   "De link bevat het token en is bedoeld om te delen (bijvoorbeeld met een " +
                   "collega die meevolgt); wie hem heeft, kan de tabel lezen — niets meer.",
        };

        _url = new TextBox
        {
            Dock = DockStyle.Top,
            Text = settings.Url.Length > 0 ? settings.Url : "https://workmanager.urbanit.be/git.php",
        };
        _token = new TextBox { Dock = DockStyle.Top, Text = settings.Token, UseSystemPasswordChar = true };
        _bestandsnamen = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Bestandsnamen meesturen (uit = alleen aantallen per project)",
            Checked = settings.MetBestandsnamen,
        };
        _status = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26 };
        Theme.AsStatus(_status);

        _qr = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Theme.Card,
        };

        var toonToken = new ModernButton { Text = "Token tonen", Width = 130 };
        toonToken.Click += (_, _) =>
        {
            _token.UseSystemPasswordChar = !_token.UseSystemPasswordChar;
            toonToken.Text = _token.UseSystemPasswordChar ? "Token tonen" : "Token verbergen";
        };
        var bewaar = new ModernButton { Text = "Bewaren", Width = 120, Kind = ButtonKind.Accent };
        bewaar.Click += (_, _) => Bewaar();
        var nuKnop = new ModernButton { Text = "Nu online zetten", Width = 170, Glyph = Fluent.Sync };
        nuKnop.Click += async (_, _) => await NuAsync(nuKnop);
        var kopieer = new ModernButton { Text = "Link kopiëren", Width = 150, Glyph = Fluent.Copy };
        kopieer.Click += (_, _) =>
        {
            if (Link() is { Length: > 0 } link)
            {
                Clipboard.SetText(link);
                Toast.Toon(this, "Link op het klembord — klaar om te delen", Fluent.Globe);
            }
        };
        var openen = new ModernButton { Text = "Openen", Width = 110, Glyph = Fluent.Globe };
        openen.Click += (_, _) =>
        {
            if (Link() is { Length: > 0 } link)
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
        };

        var knoppen = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        Theme.AsToolbar(knoppen);
        knoppen.Controls.AddRange(new Control[] { bewaar, nuKnop, kopieer, openen, toonToken });

        // Dock=Top stapelt van onder naar boven: wie het eerst toegevoegd wordt, komt onderaan.
        var velden = new Panel { Dock = DockStyle.Top, Height = 300, Padding = new Padding(14, 10, 14, 4) };
        velden.Controls.Add(_status);
        velden.Controls.Add(knoppen);
        velden.Controls.Add(_bestandsnamen);
        velden.Controls.Add(_token);
        velden.Controls.Add(Label("Token (uit git_token in config.php op de hosting)"));
        velden.Controls.Add(_url);
        velden.Controls.Add(Label("Adres van de pagina"));
        velden.Controls.Add(uitleg);

        var qrPaneel = new ModernGroupBox { Dock = DockStyle.Fill, Text = "Scan of deel de link" };
        qrPaneel.Controls.Add(_qr);

        Controls.Add(qrPaneel);
        Controls.Add(velden);
        Padding = new Padding(14, 12, 14, 14);

        _url.TextChanged += (_, _) => TekenQr();
        _token.TextChanged += (_, _) => TekenQr();
        TekenQr();
        _status.Text = settings.Compleet
            ? "Koppeling staat aan — de pc zet de stand elk uur online." +
              (GitWebSync.LaatsteUpload is { } u ? $" Laatst: {u.LocalDateTime:HH:mm}." : "")
            : "Nog niet ingesteld: vul het adres en het token in en bewaar.";
    }

    private static Label Label(string tekst) => new()
    {
        Dock = DockStyle.Top, Text = tekst, AutoSize = false, Height = 24,
        Padding = new Padding(2, 5, 2, 0),
    };

    private string Link()
    {
        var url = _url.Text.Trim();
        var token = _token.Text.Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && token.Length > 0
            ? $"{url}?t={Uri.EscapeDataString(token)}"
            : "";
    }

    private void Bewaar()
    {
        var settings = GitWebSettings.Load();
        settings.Url = _url.Text.Trim();
        settings.Token = _token.Text.Trim();
        settings.MetBestandsnamen = _bestandsnamen.Checked;
        settings.Save();
        _status.Text = settings.Compleet
            ? "Bewaard — de stand gaat binnen het uur online (of nu, met de knop ernaast)."
            : "Bewaard, maar nog niet compleet (adres moet met http beginnen).";
        Toast.Toon(this, "Instellingen bewaard", Fluent.Check);
    }

    private async Task NuAsync(ModernButton knop)
    {
        Bewaar();
        if (!GitWebSettings.Load().Compleet)
        {
            return;
        }
        knop.Bezig = true;
        knop.Enabled = false;
        _status.Text = "Peilen en versturen…";
        try
        {
            await new GitWebSync().PollAsync(forceren: true);
            _status.Text = GitWebSync.LaatsteUpload is { } u
                ? $"Stand online gezet om {u.LocalDateTime:HH:mm}."
                : "Versturen mislukt — klopt het adres en het token?";
        }
        catch (Exception ex)
        {
            _status.Text = $"Versturen mislukt: {ex.Message}";
        }
        finally
        {
            knop.Bezig = false;
            knop.Enabled = true;
        }
    }

    /// <summary>Tekent de QR-code van de link (of niets zolang er nog geen link is).</summary>
    private void TekenQr()
    {
        _qr.Image?.Dispose();
        var link = Link();
        _qr.Image = link.Length == 0 ? null : QrCode.Teken(link, 300, Theme.Text, Theme.Card);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
