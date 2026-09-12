namespace WorkManager;

/// <summary>
/// Toont de openstaande <see cref="KennisVoorstellen"/>: per repo wat Claude voorstelt om aan
/// CLAUDE.md toe te voegen. Aanvinken + "Toevoegen aan CLAUDE.md" schrijft ze onderaan het
/// bestand (ongecommit), "Negeren" haalt ze weg; "Nu genereren" analyseert de voorbije week
/// meteen in plaats van vrijdag af te wachten.
/// </summary>
public sealed class KennisVoorstellenForm : Form
{
    private readonly DataGridView _grid;
    private readonly Label _status;
    private readonly ModernButton _genereer;

    public KennisVoorstellenForm()
    {
        Text = "Kennisvoorstellen voor CLAUDE.md";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 620);
        MinimizeBox = false;

        var uitleg = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Elke vrijdag leest WorkManager je Claude-opdrachten van de voorbije week en stelt per repo " +
                   "voor wat er in CLAUDE.md bij hoort. Vink aan wat erin mag: het komt onderaan het bestand " +
                   "(ongecommit — zelf nakijken en committen).",
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "✓", Width = 36 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Repo", Width = 150, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Voorstel", Width = 200, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Tekst voor CLAUDE.md",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 60,
            DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Waarom",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 40,
            ReadOnly = true,
            DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
        });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 2, 10, 0) };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", Width = 100, DialogResult = DialogResult.Cancel };
        sluit.Click += (_, _) => Close();
        var voegToe = new ModernButton { Text = "Toevoegen aan CLAUDE.md", Width = 200, Kind = ButtonKind.Accent };
        voegToe.Click += (_, _) => VoegToe();
        var negeer = new ModernButton { Text = "Negeren", Width = 100 };
        negeer.Click += (_, _) =>
        {
            var ids = Gekozen();
            if (ids.Count == 0)
            {
                return;
            }
            KennisVoorstellen.Negeer(ids);
            Vul($"{ids.Count} voorstel(len) genegeerd.");
        };
        _genereer = new ModernButton { Text = "Nu genereren", Width = 130 };
        _genereer.Click += async (_, _) => await GenereerAsync();
        knoppen.Controls.AddRange(new Control[] { sluit, voegToe, negeer, _genereer });
        CancelButton = sluit;

        Controls.Add(_grid);
        Controls.Add(uitleg);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.StyleGrid(_grid);
        uitleg.ForeColor = Theme.Muted;
        _status.ForeColor = Theme.Muted;
        VensterGeheugen.Volg(this, "kennisvoorstellen");
        Vul();
    }

    private void Vul(string melding = "")
    {
        _grid.Rows.Clear();
        foreach (var v in KennisVoorstellen.Open().OrderBy(v => v.RepoNaam).ThenBy(v => v.Titel))
        {
            var i = _grid.Rows.Add(false, v.RepoNaam, v.Titel, v.Tekst, v.Waarom);
            _grid.Rows[i].Tag = v.Id;
        }
        _status.Text = melding.Length > 0
            ? melding
            : _grid.Rows.Count == 0
                ? "Geen openstaande voorstellen. \"Nu genereren\" analyseert de voorbije week."
                : $"{_grid.Rows.Count} openstaande voorstel(len). De tekst is aan te passen vóór het toevoegen.";
    }

    private List<Guid> Gekozen()
    {
        _grid.EndEdit();
        return _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true && r.Tag is Guid)
            .Select(r => (Guid)r.Tag!)
            .ToList();
    }

    private void VoegToe()
    {
        var ids = Gekozen();
        if (ids.Count == 0)
        {
            _status.Text = "Vink eerst aan wat erin mag.";
            return;
        }
        try
        {
            // Aangepaste tekst in het grid meenemen.
            foreach (DataGridViewRow rij in _grid.Rows)
            {
                if (rij.Tag is Guid id && ids.Contains(id))
                {
                    KennisVoorstellen.ZetTekst(id, rij.Cells[3].Value?.ToString() ?? "");
                }
            }
            var n = KennisVoorstellen.VoegToe(ids);
            Vul($"{n} voorstel(len) toegevoegd aan CLAUDE.md — nakijken en committen in de repo.");
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Toevoegen mislukt", ex.Message);
        }
    }

    private async Task GenereerAsync()
    {
        _genereer.Enabled = false;
        try
        {
            var voortgang = new Progress<string>(t => _status.Text = t);
            _status.Text = "Claude-opdrachten van de voorbije week verzamelen…";
            var n = await KennisVoorstellen.GenereerAsync(CancellationToken.None, voortgang);
            Vul(n > 0 ? $"{n} nieuwe voorstel(len)." : "Geen nieuwe voorstellen (of er liep al een analyse).");
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Genereren mislukt", ex.Message);
        }
        finally
        {
            _genereer.Enabled = true;
        }
    }
}
