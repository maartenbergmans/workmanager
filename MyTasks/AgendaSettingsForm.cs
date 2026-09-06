namespace WorkManager;

/// <summary>
/// Instellingen voor de agenda-koppeling: de geheime iCal-adressen van Google Calendar,
/// één per regel, met een testknop die meteen het aantal afspraken van vandaag/morgen toont.
/// </summary>
public class AgendaSettingsForm : Form
{
    private readonly AgendaSettings _settings;
    private readonly TextBox _urls;
    private readonly TextBox _hilkeUrls;
    private readonly TextBox _kidsUrls;
    private readonly ModernButton _testButton;
    private readonly Label _statusLabel;
    private readonly CancellationTokenSource _cts = new();

    public AgendaSettingsForm()
    {
        Text = "Agenda-koppeling (Google Calendar)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(680, 420);
        MinimizeBox = false;

        _settings = AgendaSettings.Load();

        _urls = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoSmall,
            Text = string.Join(Environment.NewLine, _settings.Urls),
        };
        _hilkeUrls = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoSmall,
            Text = string.Join(Environment.NewLine, _settings.HilkeUrls),
        };
        var hilkeLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Agenda van Hilke (apart, lichter grijs in de cockpit) — zelfde soort iCal-adressen:",
        };
        _kidsUrls = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoSmall,
            Text = string.Join(Environment.NewLine, _settings.KidsUrls),
        };
        var kidsLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Agenda \"Lisa - Emilia\" (kinderen) — iCal-adres of caldav:<agenda-id>:",
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Eén geheim iCal-adres per regel. Zo vind je het: Google Calendar → tandwiel →\n" +
                   "Instellingen → klik links op je agenda → 'Agenda integreren' → 'Geheim adres in\n" +
                   "iCal-formaat' (eindigt op basic.ics). De adressen worden versleuteld opgeslagen.\n" +
                   "Let op: Google ververst deze feed met enige vertraging (minuten tot een paar uur).",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) =>
        {
            Opslaan();
            DialogResult = DialogResult.OK;
        };
        _testButton = new ModernButton { Text = "Testen", Width = 100, Glyph = Fluent.Kalender };
        _testButton.Click += async (_, _) => await TestAsync();
        _statusLabel = new Label { AutoSize = true, Padding = new Padding(0, 8, 12, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(_testButton);
        buttons.Controls.Add(_statusLabel);
        CancelButton = cancel;

        Controls.Add(_urls);
        Controls.Add(hint);
        Controls.Add(kidsLabel);
        Controls.Add(_kidsUrls);
        Controls.Add(hilkeLabel);
        Controls.Add(_hilkeUrls);
        Controls.Add(buttons);
        Size = new Size(680, 620);
        FormClosed += (_, _) => _cts.Cancel();
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        hilkeLabel.ForeColor = Theme.Muted;
        kidsLabel.ForeColor = Theme.Muted;
        _statusLabel.ForeColor = Theme.Muted;
    }

    /// <summary>
    /// Bruikbare regels uit een invoerveld: iCal-adressen én "caldav:&lt;agenda-id&gt;"-regels
    /// (die laatste gooide het opslaan vroeger per ongeluk weg).
    /// </summary>
    private static List<string> ParseUrls(IEnumerable<string> regels) => regels
        .Select(r => r.Trim())
        .Where(r => r.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    r.StartsWith("caldav:", StringComparison.OrdinalIgnoreCase))
        .ToList();

    private List<string> IngevuldeUrls => ParseUrls(_urls.Lines);

    private void Opslaan()
    {
        _settings.Urls = IngevuldeUrls;
        _settings.HilkeUrls = ParseUrls(_hilkeUrls.Lines);
        _settings.KidsUrls = ParseUrls(_kidsUrls.Lines);
        _settings.Save();
    }

    private async Task TestAsync()
    {
        var urls = IngevuldeUrls;
        if (urls.Count == 0)
        {
            MessageBox.Show(this, "Plak eerst minstens één geheim iCal-adres.", "Agenda-koppeling",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _testButton.Enabled = false;
        _testButton.Bezig = true;
        _statusLabel.Text = "";
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var items = await AgendaClient.OphalenAsync(urls, vandaag, vandaag.AddDays(1), _cts.Token);
            _statusLabel.Text = $"✔ verbonden — {items.Count} afspraken vandaag/morgen";
            _statusLabel.ForeColor = Theme.Success;
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens de test.
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "mislukt";
            _statusLabel.ForeColor = Theme.Warn;
            Toast.Fout(this, "Agenda ophalen mislukt", ex.Message);
        }
        finally
        {
            _testButton.Enabled = true;
            _testButton.Bezig = false;
        }
    }
}
