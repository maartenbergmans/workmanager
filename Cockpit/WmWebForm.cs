using System.Diagnostics;

namespace WorkManager;

/// <summary>
/// Instellen en openen van de persoonlijke WorkManager-webpagina (wm.php op de hosting).
/// Toont de link met token erin, een QR-code om hem op de gsm te scannen, en een testknop
/// die controleert of de server antwoordt. De link is persoonlijk: hij geeft toegang tot
/// taken, agenda en de berichtenlijst, dus hij hoort niet bij de AH-link van thuis.
/// </summary>
public sealed class WmWebForm : Form
{
    private readonly TextBox _url;
    private readonly TextBox _token;
    private readonly TextBox _push;
    private readonly Label _status;
    private readonly PictureBox _qr;
    private readonly WmWebSync _sync = new();

    public WmWebForm()
    {
        Text = "WorkManager online (alleen voor mij)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(790, 660);
        MinimumSize = new Size(620, 520);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "wmweb");

        var settings = WmWebSettings.Load();

        var uitleg = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 62,
            Padding = new Padding(2, 4, 2, 8),
            Text = "Deze pagina toont onderweg je taken, agenda, wachtende berichten en de " +
                   "urenstand — en je kunt er taken afvinken, verzetten of bijmaken.\r\n" +
                   "De link bevat het token, dus hou hem privé: hij staat los van de " +
                   "AH-bestellink.",
        };

        _url = new TextBox { Dock = DockStyle.Top, Text = settings.Url };
        _token = new TextBox { Dock = DockStyle.Top, Text = settings.Token, UseSystemPasswordChar = true };
        _push = new TextBox
        {
            Dock = DockStyle.Top,
            Text = settings.PushTopic,
            PlaceholderText = "leeg = geen pushmeldingen",
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
        var testKnop = new ModernButton { Text = "Testen", Width = 110 };
        testKnop.Click += async (_, _) => await TestAsync();
        var kopieer = new ModernButton { Text = "Link kopiëren", Width = 145 };
        kopieer.Click += (_, _) =>
        {
            if (Link() is { Length: > 0 } link)
            {
                Clipboard.SetText(link);
                Toast.Toon(this, "Link op het klembord", Fluent.Globe);
            }
        };
        var openen = new ModernButton { Text = "Openen", Width = 110 };
        openen.Click += (_, _) =>
        {
            if (Link() is { Length: > 0 } link)
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
        };

        var pushTest = new ModernButton { Text = "Testmelding", Width = 140 };
        pushTest.Click += async (_, _) =>
        {
            Bewaar();
            if (_push.Text.Trim().Length == 0)
            {
                _status.Text = "Vul eerst een ntfy-topic in.";
                return;
            }
            await PushMelding.StuurAsync(
                "WorkManager", "Testmelding — als je dit ziet, werkt de push.");
            _status.Text = "Testmelding verstuurd naar ntfy.sh.";
        };
        var pushNieuw = new ModernButton { Text = "Topic verzinnen", Width = 165 };
        pushNieuw.Click += (_, _) =>
        {
            // Een topic is het enige geheim bij ntfy, dus lang en willekeurig.
            _push.Text = "wm-" + Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        };

        var knoppen = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        Theme.AsToolbar(knoppen);
        knoppen.Controls.AddRange(new Control[]
        {
            bewaar, testKnop, kopieer, openen, toonToken, pushTest, pushNieuw,
        });

        // Dock=Top stapelt van onder naar boven: wie het eerst toegevoegd wordt, komt onderaan.
        // Vandaar de omgekeerde volgorde hieronder.
        var velden = new Panel { Dock = DockStyle.Top, Height = 345, Padding = new Padding(14, 10, 14, 4) };
        velden.Controls.Add(_status);
        velden.Controls.Add(knoppen);
        velden.Controls.Add(_push);
        velden.Controls.Add(Label(
            "Push-topic (installeer de ntfy-app en abonneer je op dit topic; wie het kent, leest mee)"));
        velden.Controls.Add(_token);
        velden.Controls.Add(Label("Token (uit wm_token in config.php op de hosting)"));
        velden.Controls.Add(_url);
        velden.Controls.Add(Label("Adres van de pagina"));
        velden.Controls.Add(uitleg);

        var qrPaneel = new ModernGroupBox { Dock = DockStyle.Fill, Text = "Scan met de gsm" };
        qrPaneel.Controls.Add(_qr);

        Controls.Add(qrPaneel);
        Controls.Add(velden);
        Padding = new Padding(14, 12, 14, 14);

        _url.TextChanged += (_, _) => TekenQr();
        _token.TextChanged += (_, _) => TekenQr();
        TekenQr();
        _status.Text = settings.Compleet
            ? "Koppeling staat aan — de pc stuurt elke halve minuut bij."
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
        var settings = WmWebSettings.Load();
        settings.Url = _url.Text.Trim();
        settings.Token = _token.Text.Trim();
        settings.PushTopic = _push.Text.Trim();
        settings.Save();
        _status.Text = settings.Compleet
            ? "Bewaard — de pagina wordt binnen een halve minuut gevuld."
            : "Bewaard, maar nog niet compleet (adres moet met http beginnen).";
        Toast.Toon(this, "Instellingen bewaard", Fluent.Check);
    }

    private async Task TestAsync()
    {
        Bewaar();
        _status.Text = "Testen…";
        try
        {
            await _sync.PollAsync();
            _status.Text = WmWebSettings.Load().Compleet
                ? "Verbinding gelukt — het snapshot staat online."
                : "Vul eerst het adres en het token in.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Test mislukt: {ex.Message}";
        }
    }

    /// <summary>Tekent de QR-code van de link (of een uitleg als er nog geen link is).</summary>
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
