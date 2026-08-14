namespace WorkManager;

/// <summary>
/// Instellingenscherm voor de Gmail-mailassistent: e-mailadres, Gmail-app-wachtwoord,
/// Anthropic API-key en ophaalopties. Wachtwoord en key worden DPAPI-versleuteld opgeslagen.
/// </summary>
public class MailSettingsForm : Form
{
    private readonly MailReplySettings _settings;
    private readonly GoogleChatSettings _chatSettings;
    private readonly TextBox _email;
    private readonly TextBox _wachtwoord;
    private readonly TextBox _billit;
    private readonly NumericUpDown _maxMails;
    private readonly CheckBox _alleenOngelezen;
    private readonly TextBox _chatClientId;
    private readonly TextBox _chatClientSecret;
    private readonly Label _chatStatus;

    public MailSettingsForm()
    {
        Text = "Instellingen mailassistent (Gmail)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(590, 540);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        _settings = MailReplySettings.Load();
        _chatSettings = GoogleChatSettings.Load();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 9,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _email = new TextBox { Dock = DockStyle.Fill, Text = _settings.Email };
        _wachtwoord = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = _settings.AppWachtwoord.Length > 0 ? "(ongewijzigd laten)" : "",
        };
        _billit = new TextBox { Dock = DockStyle.Fill, Text = _settings.BillitAdres };
        _maxMails = new NumericUpDown
        {
            Minimum = 1, Maximum = 100, Value = Math.Clamp(_settings.MaxMails, 1, 100), Width = 80,
        };
        _alleenOngelezen = new CheckBox
        {
            Text = "Alleen ongelezen mails ophalen", Checked = _settings.AlleenOngelezen, AutoSize = true,
        };

        _chatClientId = new TextBox { Dock = DockStyle.Fill, Text = _chatSettings.ClientId };
        _chatClientSecret = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = _chatSettings.ClientSecretVersleuteld.Length > 0 ? "(ongewijzigd laten)" : "",
        };
        var koppelKnop = new ModernButton { Text = "Google Chat koppelen…", Width = 190 };
        _chatStatus = new Label
        {
            AutoSize = true,
            Padding = new Padding(10, 8, 0, 0),
            Text = _chatSettings.Gekoppeld ? "✔ gekoppeld" : "nog niet gekoppeld",
        };
        var koppelPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        koppelPanel.Controls.Add(koppelKnop);
        koppelPanel.Controls.Add(_chatStatus);
        koppelKnop.Click += async (_, _) =>
        {
            _chatSettings.ClientId = _chatClientId.Text.Trim();
            if (_chatClientSecret.Text.Length > 0)
            {
                _chatSettings.ClientSecret = _chatClientSecret.Text.Trim();
            }
            if (_chatSettings.ClientId.Length == 0 || _chatSettings.ClientSecretVersleuteld.Length == 0)
            {
                _chatStatus.Text = "vul eerst client-ID en secret in";
                return;
            }
            _chatSettings.Save();
            koppelKnop.Enabled = false;
            _chatStatus.Text = "browser geopend, wachten op toestemming…";
            try
            {
                await GoogleChatClient.KoppelAsync(_chatSettings, CancellationToken.None);
                _chatStatus.Text = "✔ gekoppeld";
            }
            catch (Exception ex)
            {
                _chatStatus.Text = "koppelen mislukt";
                Toast.Fout(this, "Koppelen mislukt", ex.Message);
            }
            finally
            {
                koppelKnop.Enabled = true;
            }
        };

        AddRow(grid, 0, "E-mailadres (Gmail):", _email);
        AddRow(grid, 1, "App-wachtwoord:", _wachtwoord);
        AddRow(grid, 2, "Billit-adres:", _billit);
        AddRow(grid, 3, "Max. aantal mails:", _maxMails);
        AddRow(grid, 4, "", _alleenOngelezen);
        AddRow(grid, 5, "Chat client-ID:", _chatClientId);
        AddRow(grid, 6, "Chat client-secret:", _chatClientSecret);
        AddRow(grid, 7, "", koppelPanel);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "Het app-wachtwoord maak je aan op myaccount.google.com/apppasswords " +
                   "(vereist tweestapsverificatie op je Google-account) en wordt versleuteld " +
                   "opgeslagen in %APPDATA%\\WorkManager. De concepten draaien via de Claude Code CLI " +
                   "op je bestaande abonnement — geen API-key nodig. Voor Google Chat maak je in de " +
                   "Google Cloud Console een OAuth-client (type desktop-app) aan met de Chat API " +
                   "ingeschakeld; plak hier de client-ID en het secret en klik op koppelen.",
        };
        grid.Controls.Add(hint, 0, 8);
        grid.SetColumnSpan(hint, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", DialogResult = DialogResult.OK, Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        if (label.Length > 0)
        {
            grid.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
            }, 0, row);
        }
        grid.Controls.Add(control, 1, row);
    }

    private void SaveSettings()
    {
        _settings.Email = _email.Text.Trim();
        if (_wachtwoord.Text.Length > 0)
        {
            _settings.AppWachtwoord = _wachtwoord.Text.Replace(" ", ""); // Google toont het met spaties
        }
        _settings.BillitAdres = _billit.Text.Trim();
        _settings.MaxMails = (int)_maxMails.Value;
        _settings.AlleenOngelezen = _alleenOngelezen.Checked;
        _settings.Save();

        _chatSettings.ClientId = _chatClientId.Text.Trim();
        if (_chatClientSecret.Text.Length > 0)
        {
            _chatSettings.ClientSecret = _chatClientSecret.Text.Trim();
        }
        _chatSettings.Save();
    }
}
