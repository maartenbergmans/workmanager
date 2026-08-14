namespace WorkManager;

/// <summary>
/// Voorbeeldvenster voor de weekmail: ontvangers, onderwerp en de bewerkbare mailtekst.
/// Versturen gaat via de Gmail-SMTP-instellingen van de mailassistent; zonder
/// app-wachtwoord kan de tekst naar het klembord gekopieerd worden. Met het
/// feedbackveld herwerkt Claude de tekst (bv. "korter", "in het Frans").
/// </summary>
public class TeamMailForm : Form
{
    private readonly TeamTasksData _data;
    private readonly string _stijl;
    private readonly TextBox _aan;
    private readonly TextBox _onderwerp;
    private readonly TextBox _tekst;
    private readonly TextBox _feedback;
    private readonly ModernButton _feedbackButton;
    private readonly ModernButton _verstuurButton;
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly CancellationTokenSource _cts = new();

    public TeamMailForm(TeamTasksData data, string stijl, string onderwerp, string tekst)
    {
        _data = data;
        _stijl = stijl;

        Text = "Weekmail team";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 720);
        MinimizeBox = false;

        // Kopvelden: aan + onderwerp
        var kop = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 72,
            ColumnCount = 2,
            Padding = new Padding(8, 8, 8, 0),
        };
        kop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        kop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _aan = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = data.MailAan,
            PlaceholderText = "adres1@…; adres2@… (gescheiden door ; of ,)",
        };
        _onderwerp = new TextBox { Dock = DockStyle.Fill, Text = onderwerp };
        kop.Controls.Add(new Label { Text = "Aan:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        kop.Controls.Add(_aan, 1, 0);
        kop.Controls.Add(new Label { Text = "Onderwerp:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        kop.Controls.Add(_onderwerp, 1, 1);

        // Mailtekst met feedbackveld eronder
        var tekstGroup = new ModernGroupBox
        {
            Text = "Mailtekst (bewerkbaar)",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };
        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Text = tekst,
        };
        _feedback = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Feedback voor Claude (bv. \"korter\", \"aanhef in het Frans\")…",
        };
        _feedback.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await VerwerkFeedbackAsync();
            }
        };
        _feedbackButton = new ModernButton { Text = "Pas tekst aan", Width = 125, Dock = DockStyle.Right };
        _feedbackButton.Click += async (_, _) => await VerwerkFeedbackAsync();
        var feedbackPanel = new Panel { Dock = DockStyle.Bottom, Height = 39, Padding = new Padding(0, 8, 0, 0) };
        feedbackPanel.Controls.Add(_feedback);
        feedbackPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); // ruimte naast de knop
        feedbackPanel.Controls.Add(_feedbackButton);
        tekstGroup.Controls.Add(_tekst);
        tekstGroup.Controls.Add(feedbackPanel);

        // Knoppen
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        _status = new Label { AutoSize = true, Padding = new Padding(0, 8, 10, 0), Anchor = AnchorStyles.Right };
        var sluiten = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 95 };
        var kopieer = new ModernButton { Text = "Kopiëren", Width = 110, Glyph = Fluent.Copy };
        kopieer.Click += (_, _) =>
        {
            Clipboard.SetText(_tekst.Text);
            _status.Text = "Mailtekst gekopieerd naar het klembord.";
            Toast.Toon(this, "Gekopieerd naar het klembord", Fluent.Copy);
        };
        _verstuurButton = new ModernButton
        {
            Text = "Versturen", Width = 125, Kind = ButtonKind.Accent, Glyph = Fluent.Send,
        };
        _verstuurButton.Click += async (_, _) => await VerstuurAsync();
        buttons.Controls.Add(sluiten);
        buttons.Controls.Add(kopieer);
        buttons.Controls.Add(_verstuurButton);
        buttons.Controls.Add(_status);
        CancelButton = sluiten;

        Controls.Add(tekstGroup);
        Controls.Add(_pulse);
        Controls.Add(kop);
        Controls.Add(buttons);

        FormClosed += (_, _) =>
        {
            BewaarOntvangers();
            _cts.Cancel();
        };
        Theme.Apply(this);
        _status.ForeColor = Theme.Muted;
    }

    private void BewaarOntvangers()
    {
        if (_data.MailAan != _aan.Text.Trim())
        {
            _data.MailAan = _aan.Text.Trim();
            TeamTaskStore.Save(_data);
        }
    }

    private async Task VerwerkFeedbackAsync()
    {
        var feedback = _feedback.Text.Trim();
        if (feedback.Length == 0)
        {
            return;
        }

        _feedbackButton.Enabled = false;
        _feedbackButton.Bezig = true;
        _pulse.Actief = true;
        _status.Text = "Claude herwerkt de tekst…";
        try
        {
            _tekst.Text = await TeamMailBuilder.ReviseAsync(_tekst.Text, feedback, _stijl, _cts.Token);
            _feedback.Clear();
            _status.Text = "Tekst aangepast.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = "";
            Toast.Fout(this, "Herwerken mislukt", ex.Message);
        }
        finally
        {
            _feedbackButton.Enabled = true;
            _feedbackButton.Bezig = false;
            _pulse.Actief = false;
        }
    }

    private async Task VerstuurAsync()
    {
        var aan = _aan.Text.Trim();
        if (aan.Length == 0)
        {
            MessageBox.Show(this, "Vul eerst de ontvangers in.",
                "Weekmail team", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = MailReplySettings.Load();
        if (settings.AppWachtwoord.Length == 0)
        {
            MessageBox.Show(this,
                "Er is nog geen Gmail-app-wachtwoord ingesteld (zie \"Mail beantwoorden (Gmail)…\" → " +
                "Instellingen). Gebruik ondertussen \"Kopiëren\" om de tekst zelf te mailen.",
                "Weekmail team", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var bevestig = MessageBox.Show(this,
            $"Weekmail versturen naar:\n{aan}",
            "Weekmail team", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (bevestig != DialogResult.Yes)
        {
            return;
        }

        BewaarOntvangers();
        _verstuurButton.Enabled = false;
        _verstuurButton.Bezig = true;
        _pulse.Actief = true;
        _status.Text = "Versturen…";
        try
        {
            await TeamMailBuilder.VerstuurAsync(settings, aan, _onderwerp.Text.Trim(), _tekst.Text, _cts.Token);
            VasteTaken.VinkAf(VasteTaken.WeekmailTaak); // wekelijkse taak in Mijn taken afvinken
            MessageBox.Show(this, "De weekmail is verstuurd.",
                "Weekmail team", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = "";
            Toast.Fout(this, "Versturen mislukt", ex.Message);
        }
        finally
        {
            _verstuurButton.Enabled = true;
            _verstuurButton.Bezig = false;
            _pulse.Actief = false;
        }
    }
}
