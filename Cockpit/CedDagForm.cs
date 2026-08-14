namespace WorkManager;

/// <summary>
/// Toont de timesheets die voor een CED-dag aangemaakt zouden worden, vóórdat er iets geboekt
/// wordt. Regels kunnen uitgevinkt worden, tijden en omschrijvingen zijn aanpasbaar, en de
/// werkdag zelf (standaard 8:00–17:00) kan bijgesteld worden met een herberekening. Ook de dag
/// is te wisselen (vergeten te boeken? kies gisteren) — de meetings laden dan opnieuw.
/// </summary>
public sealed class CedDagForm : Form
{
    private readonly DataGridView _grid;
    private readonly DatumKiezer _dagKiezer;
    private readonly DateTimePicker _start;
    private readonly DateTimePicker _einde;
    private readonly Label _totaal;
    private readonly Func<DateOnly, Task<List<AgendaClient.AgendaItem>>>? _meetingsLader;
    private DateOnly _dag;
    private List<AgendaClient.AgendaItem> _meetings;

    /// <summary>De aangevinkte regels, klaar om als timesheet weggeschreven te worden.</summary>
    public List<CedBlok> Gekozen { get; } = new();

    /// <summary>De dag waarop geboekt wordt; kan in het venster gewisseld zijn.</summary>
    public DateOnly Dag => _dag;

    public CedDagForm(
        DateOnly dag,
        List<AgendaClient.AgendaItem> meetings,
        Func<DateOnly, Task<List<AgendaClient.AgendaItem>>>? meetingsLader = null)
    {
        _dag = dag;
        _meetings = meetings;
        _meetingsLader = meetingsLader;

        Text = $"CED-dag – {dag:dddd d MMMM yyyy}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 480);
        MinimizeBox = false;

        var dagRij = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            WrapContents = false,
        };
        _dagKiezer = new DatumKiezer { Width = 185, LeegToegestaan = false, Waarde = dag };
        _dagKiezer.WaardeGewijzigd += async (_, _) => await WisselDagAsync();
        _start = Tijdkiezer(CedDagPlanner.StandaardStart);
        _einde = Tijdkiezer(CedDagPlanner.StandaardEinde);
        var herbereken = new ModernButton { Text = "Herberekenen", Width = 120 };
        herbereken.Click += (_, _) => Vul();
        dagRij.Controls.Add(new Label { Text = "Dag", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        dagRij.Controls.Add(_dagKiezer);
        dagRij.Controls.Add(new Label { Text = "van", AutoSize = true, Padding = new Padding(10, 6, 6, 0) });
        dagRij.Controls.Add(_start);
        dagRij.Controls.Add(new Label { Text = "tot", AutoSize = true, Padding = new Padding(8, 6, 6, 0) });
        dagRij.Controls.Add(_einde);
        dagRij.Controls.Add(herbereken);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Boeken", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Van", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tot", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Min", Width = 55, ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Omschrijving",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });
        _grid.CellEndEdit += (_, e) =>
        {
            if (e.ColumnIndex is 1 or 2)
            {
                WerkMinutenBij(_grid.Rows[e.RowIndex]);
            }
            WerkTotaalBij();
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == 0)
            {
                WerkTotaalBij();
            }
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 6, 10, 0),
            Text = "Vink uit wat niet geboekt moet worden; tijden en omschrijvingen zijn aanpasbaar. "
                 + "Er wordt pas iets weggeschreven als je op \"Timesheets aanmaken\" klikt.",
        };

        _totaal = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 2, 10, 0) };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var annuleer = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Timesheets aanmaken", Width = 165, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => Bevestig();
        knoppen.Controls.Add(annuleer);
        knoppen.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = annuleer;

        Controls.Add(_grid);
        Controls.Add(hint);
        Controls.Add(dagRij);
        Controls.Add(_totaal);
        Controls.Add(knoppen);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        _totaal.ForeColor = Theme.Muted;

        Vul();
    }

    private static DateTimePicker Tijdkiezer(TimeOnly tijd) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 70,
        Value = DateTime.Today + tijd.ToTimeSpan(),
    };

    /// <summary>
    /// Wisselt naar de gekozen dag: meetings van die dag ophalen en de blokken herrekenen.
    /// Wisselt de gebruiker tijdens het laden nógmaals, dan wint de laatste keuze.
    /// </summary>
    private async Task WisselDagAsync()
    {
        if (_dagKiezer.Waarde is not { } nieuw || nieuw == _dag)
        {
            return;
        }
        _dag = nieuw;
        Text = $"CED-dag – {nieuw:dddd d MMMM yyyy}";

        if (_meetingsLader is not null)
        {
            UseWaitCursor = true;
            try
            {
                var meetings = await _meetingsLader(nieuw);
                if (_dagKiezer.Waarde != nieuw)
                {
                    return; // intussen alweer verder gewisseld; die aanroep vult zelf
                }
                _meetings = meetings;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        Vul();
    }

    private void Vul()
    {
        var blokken = CedDagPlanner.Maak(
            _dag,
            TimeOnly.FromDateTime(_start.Value),
            TimeOnly.FromDateTime(_einde.Value),
            _meetings);

        _grid.Rows.Clear();
        foreach (var blok in blokken)
        {
            var rij = _grid.Rows.Add(
                true, blok.Van.ToString("HH:mm"), blok.Tot.ToString("HH:mm"),
                blok.Minuten, blok.Omschrijving);
            _grid.Rows[rij].Tag = blok.IsMeeting;
            if (blok.IsMeeting)
            {
                _grid.Rows[rij].DefaultCellStyle.ForeColor = Theme.Accent;
            }
        }
        WerkTotaalBij();
    }

    private void WerkMinutenBij(DataGridViewRow rij)
    {
        rij.Cells[3].Value = Lees(rij) is { } blok ? blok.Minuten : 0;
    }

    /// <summary>Leest één rij uit; null als de tijden onleesbaar zijn of het blok leeg is.</summary>
    private static CedBlok? Lees(DataGridViewRow rij)
    {
        if (!TimeOnly.TryParse(rij.Cells[1].Value?.ToString(), out var van) ||
            !TimeOnly.TryParse(rij.Cells[2].Value?.ToString(), out var tot) ||
            tot <= van)
        {
            return null;
        }
        return new CedBlok
        {
            Van = van,
            Tot = tot,
            Omschrijving = rij.Cells[4].Value?.ToString()?.Trim() ?? "",
            IsMeeting = rij.Tag is true,
        };
    }

    private void WerkTotaalBij()
    {
        var minuten = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true)
            .Sum(r => Lees(r)?.Minuten ?? 0);
        _totaal.Text = $"Totaal aangevinkt: {minuten / 60}u{minuten % 60:00} ({minuten} minuten)";
    }

    private void Bevestig()
    {
        _grid.EndEdit();
        Gekozen.Clear();

        foreach (DataGridViewRow rij in _grid.Rows)
        {
            if (rij.Cells[0].Value is not true)
            {
                continue;
            }
            if (Lees(rij) is not { } blok)
            {
                MessageBox.Show(this,
                    $"Regel {rij.Index + 1} heeft een ongeldige tijd (gebruik HH:mm, en tot ná van).",
                    "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Gekozen.Add(blok);
        }

        if (Gekozen.Count == 0)
        {
            MessageBox.Show(this, "Vink minstens één regel aan.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
