namespace WorkManager;

/// <summary>
/// Kiest een doelmap in Google Drive. Opent op de snellijst — favorieten en recent gebruikte
/// mappen — want negen van de tien keer staat het doel daar al tussen. Van daaruit kun je de
/// mappenboom in bladeren of over heel Drive zoeken.
/// </summary>
public sealed class DriveMapKiezerForm : Form
{
    private readonly GoogleChatSettings _settings;
    private readonly ModernListView _lijst;
    private readonly TextBox _zoek;
    private readonly Label _pad;
    private readonly ModernButton _omhoog;
    private readonly ModernButton _kies;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Waar we nu staan; leeg = de snellijst met favorieten en recent.</summary>
    private readonly List<DriveMap> _kruimels = new();

    public string GekozenId { get; private set; } = "";
    public string GekozenNaam { get; private set; } = "";

    public DriveMapKiezerForm(GoogleChatSettings settings)
    {
        _settings = settings;
        Text = "Map kiezen in Google Drive";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 520);
        MinimizeBox = false;
        MaximizeBox = false;

        _pad = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(10, 6, 10, 0),
            Text = "Favorieten en recent",
        };

        var zoekRij = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 4, 8, 4),
            ColumnCount = 3,
        };
        zoekRij.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        zoekRij.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zoekRij.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _omhoog = new ModernButton { Text = "◀ Terug", Dock = DockStyle.Fill, Enabled = false };
        _omhoog.Click += async (_, _) => await OmhoogAsync();

        _zoek = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Zoek een map in Drive…" };
        _zoek.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await ZoekAsync();
            }
        };
        var zoekKnop = new ModernButton { Text = "Zoeken", Dock = DockStyle.Fill };
        zoekKnop.Click += async (_, _) => await ZoekAsync();

        zoekRij.Controls.Add(_omhoog, 0, 0);
        zoekRij.Controls.Add(_zoek, 1, 0);
        zoekRij.Controls.Add(zoekKnop, 2, 0);

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.None,
            LegeTekst = "Geen mappen gevonden.",
        };
        _lijst.Columns.Add("Map", 500);
        _lijst.DoubleClick += async (_, _) => await OpenSelectieAsync();
        _lijst.SelectedIndexChanged += (_, _) => WerkKnoppenBij();

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var annuleer = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        _kies = new ModernButton { Text = "Kies deze map", Width = 130, Kind = ButtonKind.Accent, Enabled = false };
        _kies.Click += (_, _) => Kies();
        var openKnop = new ModernButton { Text = "Openen ▸", Width = 100 };
        openKnop.Click += async (_, _) => await OpenSelectieAsync();
        knoppen.Controls.Add(annuleer);
        knoppen.Controls.Add(_kies);
        knoppen.Controls.Add(openKnop);
        AcceptButton = _kies;
        CancelButton = annuleer;

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Padding = new Padding(10, 2, 10, 0),
            Text = "Dubbelklik om een map te openen; \"Kies deze map\" bevestigt de selectie.",
        };

        Controls.Add(_lijst);
        Controls.Add(zoekRij);
        Controls.Add(_pad);
        Controls.Add(hint);
        Controls.Add(knoppen);
        Theme.Apply(this);
        _pad.ForeColor = Theme.Muted;
        hint.ForeColor = Theme.Muted;

        FormClosed += (_, _) => _cts.Cancel();
        ToonSnellijst();
    }

    /// <summary>Het startscherm: favorieten, recent en een ingang naar de wortel van Drive.</summary>
    private void ToonSnellijst()
    {
        _kruimels.Clear();
        _pad.Text = "Favorieten en recent";
        _omhoog.Enabled = false;

        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var (naam, id) in DriveMappen.Favorieten)
        {
            if (id.Length > 0)
            {
                Voegtoe(new DriveMap(id, naam), "★ ");
            }
        }
        foreach (var recent in DriveMappen.Recent())
        {
            Voegtoe(new DriveMap(recent.Id, recent.Naam), "↻ ");
        }
        Voegtoe(new DriveMap("root", "Mijn Drive"), "▸ ");
        _lijst.EndUpdate();
        WerkKnoppenBij();
    }

    private void Voegtoe(DriveMap map, string prefix = "")
    {
        _lijst.Items.Add(new ListViewItem(prefix + map.Naam) { Tag = map });
    }

    private async Task OpenSelectieAsync()
    {
        if (Geselecteerd() is not { } map)
        {
            return;
        }
        _kruimels.Add(map);
        await ToonSubmappenAsync();
    }

    private async Task OmhoogAsync()
    {
        if (_kruimels.Count == 0)
        {
            return;
        }
        _kruimels.RemoveAt(_kruimels.Count - 1);
        if (_kruimels.Count == 0)
        {
            ToonSnellijst();
        }
        else
        {
            await ToonSubmappenAsync();
        }
    }

    private async Task ToonSubmappenAsync()
    {
        var huidig = _kruimels[^1];
        _pad.Text = string.Join(" › ", _kruimels.Select(k => k.Naam));
        _omhoog.Enabled = true;
        await VulAsync(() => GoogleDriveClient.SubmappenAsync(_settings, huidig.Id, _cts.Token),
            "Deze map heeft geen submappen — kies ze met \"Kies deze map\".");
    }

    private async Task ZoekAsync()
    {
        var tekst = _zoek.Text.Trim();
        if (tekst.Length < 2)
        {
            return;
        }
        // Zoeken staat los van de boom: we tonen treffers, maar houden de kruimels leeg zodat
        // "Terug" je netjes naar de snellijst brengt in plaats van naar een half pad.
        _kruimels.Clear();
        _pad.Text = $"Zoekresultaten voor \"{tekst}\"";
        _omhoog.Enabled = true;
        await VulAsync(() => GoogleDriveClient.ZoekMappenAsync(_settings, tekst, _cts.Token),
            "Geen map met die naam gevonden.");
    }

    private async Task VulAsync(Func<Task<List<DriveMap>>> ophalen, string legeTekst)
    {
        _lijst.Items.Clear();
        _lijst.LegeTekst = "Bezig met laden…";
        _lijst.Invalidate();
        UseWaitCursor = true;
        try
        {
            var mappen = await ophalen();
            _lijst.BeginUpdate();
            _lijst.Items.Clear();
            foreach (var map in mappen)
            {
                Voegtoe(map);
            }
            _lijst.EndUpdate();
            _lijst.LegeTekst = legeTekst;
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het laden.
        }
        catch (Exception ex)
        {
            _lijst.LegeTekst = ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
            _lijst.Invalidate();
            WerkKnoppenBij();
        }
    }

    private DriveMap? Geselecteerd() =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as DriveMap : null;

    private void WerkKnoppenBij()
    {
        // Op "Mijn Drive" zelf iets droppen is zelden de bedoeling; dat is een vergissing die
        // je pas veel later merkt. Openen mag, kiezen niet.
        var map = Geselecteerd();
        _kies.Enabled = map is not null && map.Id != "root";
    }

    private void Kies()
    {
        if (Geselecteerd() is not { } map || map.Id == "root")
        {
            return;
        }
        GekozenId = map.Id;
        GekozenNaam = map.Naam;
        DialogResult = DialogResult.OK;
    }
}
