namespace WorkManager;

/// <summary>
/// Instellingen voor de Asana-koppeling: Personal Access Token plakken, workspaces ophalen
/// en er één kiezen. Het token wordt DPAPI-versleuteld opgeslagen.
/// </summary>
public class AsanaSettingsForm : Form
{
    private readonly AsanaSettings _settings;
    private readonly TextBox _token;
    private readonly ComboBox _workspace;
    private readonly ModernButton _ophaalButton;
    private readonly Label _statusLabel;
    private readonly CancellationTokenSource _cts = new();
    private List<AsanaClient.Workspace> _workspaces = new();

    public AsanaSettingsForm()
    {
        Text = "Asana-koppeling";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        _settings = AsanaSettings.Load();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 0),
            ColumnCount = 3,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));

        _token = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = _settings.Token.Length > 0 ? "(ongewijzigd laten)" : "",
        };
        _ophaalButton = new ModernButton
        {
            Text = "Workspaces ophalen", Width = 160, Glyph = Fluent.Refresh, Dock = DockStyle.Fill,
        };
        _ophaalButton.Click += async (_, _) => await WorkspacesOphalenAsync();
        _workspace = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        if (_settings.WorkspaceNaam.Length > 0)
        {
            _workspace.Items.Add(_settings.WorkspaceNaam);
            _workspace.SelectedIndex = 0;
        }

        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label
        {
            Text = "Token:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
        }, 0, 0);
        grid.Controls.Add(_token, 1, 0);
        grid.Controls.Add(_ophaalButton, 2, 0);

        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label
        {
            Text = "Workspace:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
        }, 0, 1);
        grid.Controls.Add(_workspace, 1, 1);
        _statusLabel = new Label
        {
            AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 6, 0, 0),
        };
        grid.Controls.Add(_statusLabel, 2, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "Maak een Personal Access Token aan via Asana → instellingen → Apps → " +
                   "'Manage developer apps' → 'Create new token' (of app.asana.com/0/my-apps). " +
                   "Plak het token hierboven en klik 'Workspaces ophalen' om de koppeling te testen " +
                   "en je workspace te kiezen. Het token wordt versleuteld opgeslagen in " +
                   "%APPDATA%\\WorkManager.",
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(hint, 0, 2);
        grid.SetColumnSpan(hint, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => Opslaan();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        FormClosed += (_, _) => _cts.Cancel();
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        _statusLabel.ForeColor = Theme.Muted;
    }

    private string HuidigToken => _token.Text.Trim().Length > 0 ? _token.Text.Trim() : _settings.Token;

    private async Task WorkspacesOphalenAsync()
    {
        if (HuidigToken.Length == 0)
        {
            MessageBox.Show(this, "Plak eerst een Personal Access Token.", "Asana-koppeling",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _ophaalButton.Enabled = false;
        _ophaalButton.Bezig = true;
        _statusLabel.Text = "";
        try
        {
            _workspaces = await AsanaClient.WorkspacesAsync(HuidigToken, _cts.Token);
            _workspace.Items.Clear();
            foreach (var ws in _workspaces)
            {
                _workspace.Items.Add(ws.Naam);
            }
            if (_workspace.Items.Count > 0)
            {
                var index = _workspaces.FindIndex(w => w.Gid == _settings.WorkspaceGid);
                _workspace.SelectedIndex = index < 0 ? 0 : index;
                _statusLabel.Text = "✔ verbonden";
                _statusLabel.ForeColor = Theme.Success;
            }
            else
            {
                _statusLabel.Text = "geen workspaces";
                _statusLabel.ForeColor = Theme.Warn;
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "mislukt";
            _statusLabel.ForeColor = Theme.Warn;
            Toast.Fout(this, "Workspaces ophalen mislukt", ex.Message);
        }
        finally
        {
            _ophaalButton.Enabled = true;
            _ophaalButton.Bezig = false;
        }
    }

    private void Opslaan()
    {
        if (_token.Text.Trim().Length > 0)
        {
            _settings.Token = _token.Text.Trim();
        }
        if (_workspace.SelectedIndex >= 0 && _workspace.SelectedIndex < _workspaces.Count)
        {
            _settings.WorkspaceGid = _workspaces[_workspace.SelectedIndex].Gid;
            _settings.WorkspaceNaam = _workspaces[_workspace.SelectedIndex].Naam;
        }
        _settings.Save();
        DialogResult = DialogResult.OK;
    }
}
