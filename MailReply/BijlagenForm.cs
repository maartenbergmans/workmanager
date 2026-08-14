namespace WorkManager;

/// <summary>
/// Dialoog voor het opslaan of doorsturen van bijlagen: per bijlage aanvinken en de
/// bestandsnaam kiezen (bij opslaan vooraf ingevuld met "yyMMdd " + originele naam),
/// bij opslaan ook de doelmap. In doorstuur-modus (Billit) vervalt de mapkeuze en
/// staat standaard niets aangevinkt, tenzij er maar één bijlage is.
///
/// Bij opslaan naar Google Drive is de doelmap al gekozen in het menu; dan vervalt de
/// mapkeuze eveneens, maar blijft de rest (datumprefix, alles aangevinkt) zoals bij opslaan.
/// </summary>
public class BijlagenForm : Form
{
    private readonly DataGridView _grid;
    private readonly TextBox _map;
    private readonly bool _doorsturen;
    private readonly bool _naarDrive;

    public string Doelmap => _map.Text.Trim();
    public List<(int Index, string Naam)> Selectie { get; } = new();
    public List<(string Url, string Naam)> LinkSelectie { get; } = new();

    public BijlagenForm(MailBericht mail, string standaardMap, bool doorsturen = false,
        string? driveMap = null)
    {
        _doorsturen = doorsturen;
        _naarDrive = driveMap is { Length: > 0 };
        Text = doorsturen
            ? $"Bijlagen doorsturen naar Billit – {mail.Onderwerp}"
            : _naarDrive
                ? $"Bijlagen opslaan in Drive: {driveMap} – {mail.Onderwerp}"
                : $"Bijlagen opslaan – {mail.Onderwerp}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(680, 420);
        MinimizeBox = false;

        // Doelmap-rij
        var mapPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(8, 6, 8, 0),
            ColumnCount = 3,
        };
        mapPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        mapPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mapPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        _map = new TextBox { Dock = DockStyle.Fill, Text = standaardMap };
        var bladeren = new ModernButton { Text = "Bladeren…", Dock = DockStyle.Fill };
        bladeren.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(Doelmap) ? Doelmap : standaardMap,
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _map.Text = dialog.SelectedPath;
            }
        };
        mapPanel.Controls.Add(new Label { Text = "Map:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        mapPanel.Controls.Add(_map, 1, 0);
        mapPanel.Controls.Add(bladeren, 2, 0);

        // Bijlagenlijst met vinkje + bewerkbare naam
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Opslaan", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Bestandsnaam",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Bron", Width = 80, ReadOnly = true });

        var datumPrefix = doorsturen ? "" : DateTime.Now.ToString("yyMMdd") + " ";
        var totaal = mail.Bijlagen.Count + mail.LinkBijlagen.Count;
        var standaardAan = !doorsturen || totaal == 1;
        for (var i = 0; i < mail.Bijlagen.Count; i++)
        {
            var rij = _grid.Rows.Add(standaardAan, datumPrefix + mail.Bijlagen[i], "bijlage");
            _grid.Rows[rij].Tag = i;
        }
        foreach (var link in mail.LinkBijlagen)
        {
            var rij = _grid.Rows.Add(standaardAan, datumPrefix + link.Naam, "link");
            _grid.Rows[rij].Tag = link.Url;
        }

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(10, 8, 10, 0),
            Text = doorsturen
                ? "Vink aan welke bijlage(n) naar Billit doorgestuurd worden."
                : _naarDrive
                    ? $"Vink aan wat naar \"{driveMap}\" in Google Drive gaat; namen zijn aanpasbaar."
                    : "Vink aan wat je wil opslaan en pas de bestandsnamen aan waar nodig.",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton
        {
            Text = doorsturen ? "Doorsturen" : "Opslaan", Width = 110, Kind = ButtonKind.Accent,
        };
        ok.Click += (_, _) => Opslaan();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(hint);
        if (!doorsturen && !_naarDrive)
        {
            Controls.Add(mapPanel);
        }
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
    }

    private void Opslaan()
    {
        _grid.EndEdit();

        if (!_doorsturen && !_naarDrive && Doelmap.Length == 0)
        {
            MessageBox.Show(this, "Kies een doelmap.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Selectie.Clear();
        LinkSelectie.Clear();
        foreach (DataGridViewRow rij in _grid.Rows)
        {
            if (rij.Cells[0].Value is not true ||
                rij.Cells[1].Value?.ToString()?.Trim() is not { Length: > 0 } naam)
            {
                continue;
            }
            switch (rij.Tag)
            {
                case int index:
                    Selectie.Add((index, naam));
                    break;
                case string url:
                    LinkSelectie.Add((url, naam));
                    break;
            }
        }
        if (Selectie.Count == 0 && LinkSelectie.Count == 0)
        {
            MessageBox.Show(this, "Vink minstens één bijlage aan.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
    }
}
