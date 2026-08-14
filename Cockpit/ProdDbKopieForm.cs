using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Haalt de productiedatabank van een applicatie naar de lokale dev-mysql, om aanpassingen
/// te kunnen testen met echte productiegegevens. Toont eerst de onderdelen (tabellen, of bij
/// een multi-tenant app de databanken) met hun grootte — oude rommel zoals _copy-tabellen
/// staat standaard uitgevinkt en de eigen selectie wordt onthouden — kopieert pas na een
/// klik en laat de voortgang zien, met meelopende klok. Grote historietabellen kunnen tot
/// het laatste jaar beperkt worden. De lokale databank wordt overschreven; dat is de
/// bedoeling, maar het staat er duidelijk bij.
/// </summary>
public sealed class ProdDbKopieForm : Form
{
    private const int FilterMaanden = 12;

    private readonly ProdDbDoel _doel;
    private readonly ProdDbKopie.DoelState _state;
    private readonly DataGridView _grid;
    private readonly Label _hint;
    private readonly Label _status;
    private readonly ProgressBar _balk;
    private readonly ModernButton _start;
    private readonly CheckBox _datumFilter;
    private readonly System.Windows.Forms.Timer _klok = new() { Interval = 1000 };
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ProdDbKopie.KopieItem> _items = new();
    private string _statusBasis = "";
    private DateTime _gestart;
    private bool _bezig;

    public ProdDbKopieForm(ProdDbDoel doel)
    {
        _doel = doel;
        _state = ProdDbKopie.LaadState(doel);

        Text = ThemaStem.ProdDbTitel(doel.Naam);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(660, 580);
        MinimizeBox = false;

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(10, 6, 10, 0),
            Text = HintTekst(),
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Kopieer ✓✗", Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = doel.ViaSsh ? "Databank" : "Tabel",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true,
        });
        var mbKolom = new DataGridViewTextBoxColumn { HeaderText = "MB", Width = 80, ReadOnly = true };
        mbKolom.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns.Add(mbKolom);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, _) => { ToonSelectie(); BewaarSelectie(); };
        // Klik op de kop van de vinkjeskolom: alles aan/uit tegelijk.
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex != 0 || _bezig)
            {
                return;
            }
            var allesAan = _grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[0].Value is true);
            foreach (DataGridViewRow rij in _grid.Rows)
            {
                rij.Cells[0].Value = !allesAan;
            }
        };

        _datumFilter = new CheckBox
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 0, 10, 0),
            Text = $"Historietabellen beperken tot de laatste {FilterMaanden} maanden " +
                   "(ServiceDate/Date) — veel kleiner en sneller",
            Checked = _state.DatumFilterAan,
            Visible = doel.DatumFilters.Length > 0,
        };
        _datumFilter.CheckedChanged += (_, _) =>
        {
            _state.DatumFilterAan = _datumFilter.Checked;
            ProdDbKopie.BewaarState(_doel, _state);
        };

        _status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 2, 10, 0) };
        _balk = new ProgressBar { Dock = DockStyle.Bottom, Height = 14, Maximum = 1000, Visible = false };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", Width = 100 };
        sluit.Click += (_, _) => Close();
        _start = new ModernButton
        {
            Text = "Kopieer naar localhost", Width = 185, Kind = ButtonKind.Accent, Enabled = false,
        };
        _start.Click += async (_, _) => await StartKopieAsync();
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_start);
        CancelButton = sluit;

        Controls.Add(_grid);
        Controls.Add(_hint);
        Controls.Add(_datumFilter);
        Controls.Add(_status);
        Controls.Add(_balk);
        Controls.Add(knoppen);
        Theme.Apply(this);
        VensterGeheugen.Volg(this, "prod-db-kopie");
        _hint.ForeColor = Theme.Muted;
        _status.ForeColor = Theme.Muted;

        // Meelopende klok tijdens de kopie: zelfde status, verse tijdsaanduiding.
        _klok.Tick += (_, _) =>
        {
            if (_bezig && _statusBasis.Length > 0)
            {
                var t = DateTime.Now - _gestart;
                _status.Text = $"{_statusBasis}   ({(int)t.TotalMinutes}m{t.Seconds:00})";
            }
        };

        Shown += async (_, _) => await LaadItemsAsync();
        FormClosing += (_, e) =>
        {
            if (_bezig && MessageBox.Show(this,
                    "De kopie loopt nog. Stoppen? (Alleen al afgeronde onderdelen zijn dan " +
                    "vervangen; een tijdelijke importdatabank kan achterblijven.)",
                    "WorkManager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }
            _cts.Cancel();
        };
    }

    private string HintTekst()
    {
        var vers = _state.LaatsteKopie is { } l
            ? $"Laatste kopie: {l.LocalDateTime:ddd d MMM HH:mm} " +
              $"({Math.Max(0, (int)(DateTime.Now - l.LocalDateTime).TotalDays)} dagen geleden)."
            : "Nog nooit gekopieerd.";
        return $"Bron: {_doel.BronOmschrijving}. Bestaande lokale gegevens worden " +
               $"overschreven. {vers}";
    }

    private async Task LaadItemsAsync()
    {
        _status.Text = "Productiedatabank verkennen…";
        try
        {
            var items = await ProdDbKopie.ItemsAsync(_doel, _cts.Token);
            _items.Clear();
            _items.AddRange(items);
            _grid.Rows.Clear();
            foreach (var item in items)
            {
                var aan = !Regex.IsMatch(item.Naam, _doel.SkipPatroon, RegexOptions.IgnoreCase) &&
                          !_state.Uitgevinkt.Contains(item.Naam);
                _grid.Rows.Add(aan, item.Naam, item.Mb);
            }
            _start.Enabled = items.Count > 0;
            ToonSelectie();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.Text = "Verkennen mislukte.";
            Toast.Fout(this, "Kon de productiedatabank niet lezen", ex.Message);
        }
    }

    private List<ProdDbKopie.KopieItem> Gekozen() =>
        _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true && r.Index < _items.Count)
            .Select(r => _items[r.Index])
            .ToList();

    /// <summary>Bewaart wat je zelf hebt uitgevinkt (het skip-patroon staat sowieso al uit).</summary>
    private void BewaarSelectie()
    {
        if (_bezig || _items.Count == 0)
        {
            return;
        }
        _state.Uitgevinkt = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is not true && r.Index < _items.Count)
            .Select(r => _items[r.Index].Naam)
            .Where(naam => !Regex.IsMatch(naam, _doel.SkipPatroon, RegexOptions.IgnoreCase))
            .ToList();
        ProdDbKopie.BewaarState(_doel, _state);
    }

    private void ToonSelectie()
    {
        if (_bezig)
        {
            return;
        }
        var keuze = Gekozen();
        _status.Text = keuze.Count == 0
            ? "Niets aangevinkt.  (Tip: klik op de kolomkop voor alles aan/uit.)"
            : $"Selectie: {keuze.Count} {(_doel.ViaSsh ? "databank(en)" : "tabellen")}, " +
              $"~{keuze.Sum(t => t.Mb)} MB.";
    }

    private async Task StartKopieAsync()
    {
        var keuze = Gekozen();
        if (keuze.Count == 0)
        {
            MessageBox.Show(this, "Vink minstens één onderdeel aan.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _bezig = true;
        _start.Enabled = false;
        _grid.Enabled = false;
        _datumFilter.Enabled = false;
        _balk.Visible = true;
        _balk.Value = 0;
        _gestart = DateTime.Now;
        _klok.Start();
        var voortgang = new Progress<(string Status, double Fractie)>(v =>
        {
            _statusBasis = v.Status;
            _status.Text = v.Status;
            _balk.Value = Math.Clamp((int)(v.Fractie * 1000), 0, 1000);
        });
        try
        {
            var filter = _datumFilter.Visible && _datumFilter.Checked ? FilterMaanden : (int?)null;
            await ProdDbKopie.KopieerAsync(_doel, keuze, filter, voortgang, _cts.Token);
            var duur = DateTime.Now - _gestart;
            _statusBasis = "";
            _status.Text = $"Klaar: {keuze.Count} onderde(e)l(en) gekopieerd " +
                $"in {(int)duur.TotalMinutes}m{duur.Seconds:00}.";
            _state.LaatsteKopie = DateTimeOffset.Now;
            ProdDbKopie.BewaarState(_doel, _state);
            _hint.Text = HintTekst();
            if (_doel.DataGripProject.Length > 0)
            {
                var project = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "DataGripProjects", _doel.DataGripProject);
                Toast.ToonActie(this, $"Productie-DB van {_doel.Naam} staat op localhost",
                    "DataGrip openen", () => ClientLauncher.StartDataGrip(project), Fluent.Sync);
            }
            else
            {
                Toast.Toon(this, $"Productie-DB van {_doel.Naam} staat op localhost", Fluent.Sync);
            }
        }
        catch (OperationCanceledException)
        {
            _statusBasis = "";
            _status.Text = "Kopie gestopt.";
        }
        catch (Exception ex)
        {
            _statusBasis = "";
            _status.Text = "Kopie mislukte.";
            Toast.Fout(this, "Kopiëren mislukte", ex.Message);
        }
        finally
        {
            _klok.Stop();
            _bezig = false;
            _grid.Enabled = true;
            _datumFilter.Enabled = true;
            _start.Enabled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _klok.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
