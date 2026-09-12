namespace WorkManager;

/// <summary>
/// Toont het door Claude opgestelde dagvoorstel (uit de activiteitenlog en de andere sporen)
/// vóórdat er iets geboekt wordt: regels uitvinken, tijden/minuten/klant/omschrijving
/// aanpassen, en pas bij "Timesheets aanmaken" gaat het naar de wachtrij. Zelfde gedachte
/// als <see cref="CedDagForm"/>, maar met een klantkolom omdat de dag over meerdere klanten
/// kan lopen. Ook hier is de dag te wisselen (gisteren vergeten te boeken?): het venster
/// sluit dan met DialogResult.Retry en <see cref="GewisseldNaar"/> gezet, waarna de
/// aanroeper het voorstel voor die dag laadt.
/// </summary>
public sealed class TimesheetVoorstelForm : Form
{
    private readonly DataGridView _grid;
    private readonly Label _totaal;
    private readonly DateOnly _dag;

    /// <summary>De aangevinkte regels, klaar om als timesheet weggeschreven te worden.</summary>
    public List<TimesheetRegel> Gekozen { get; } = new();

    /// <summary>Bij DialogResult.Retry: de dag waarnaar gewisseld is (null = gewoon vernieuwen).</summary>
    public DateOnly? GewisseldNaar { get; private set; }

    /// <param name="gemaaktOp">
    /// Tijdstip waarop dit voorstel op de achtergrond klaargezet is (16:30-voorbereiding of
    /// gsm-run); null bij een verse run. Bij een klaargezet voorstel komt er een
    /// "Vernieuwen"-knop bij die met DialogResult.Retry om een verse Claude-run vraagt.
    /// </param>
    public TimesheetVoorstelForm(
        DateOnly dag, List<TimesheetRegel> voorstel, string toelichting = "",
        DateTimeOffset? gemaaktOp = null)
    {
        _dag = dag;

        Text = $"Dagvoorstel timesheets – {dag:dddd d MMMM yyyy}" +
            (gemaaktOp is { } klaargezet ? $" (klaargezet om {klaargezet:HH:mm})" : "");
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, toelichting.Length > 0 ? 540 : 480);
        MinimizeBox = false;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Boeken", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Van", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Min", Width = 55 });
        var klantKolom = new DataGridViewComboBoxColumn
        {
            HeaderText = "Klant", Width = 140, FlatStyle = FlatStyle.Flat,
        };
        klantKolom.Items.AddRange(TimesheetStore.Klanten.Cast<object>().ToArray());
        _grid.Columns.Add(klantKolom);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Omschrijving",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });
        _grid.CellEndEdit += (_, _) => WerkTotaalBij();
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
        // Een tikfout in de klantcel mag geen foutdialoog opleveren.
        _grid.DataError += (_, e) => e.ThrowException = false;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 6, 10, 0),
            Text = "Voorstel op basis van de activiteitenlog, agenda, Claude-opdrachten en " +
                   "verzonden mails. Vink uit wat niet geboekt moet worden; alles is aanpasbaar.",
        };

        // Dagwissel (bv. gisteren vergeten te boeken): het voorstel voor die dag moet
        // opnieuw geladen worden (cache of verse Claude-run), dus dat doet de aanroeper.
        var dagRij = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            WrapContents = false,
        };
        var dagKiezer = new DatumKiezer { Width = 185, LeegToegestaan = false, Waarde = dag };
        dagKiezer.WaardeGewijzigd += (_, _) =>
        {
            if (dagKiezer.Waarde is { } nieuw && nieuw != _dag)
            {
                GewisseldNaar = nieuw;
                DialogResult = DialogResult.Retry;
            }
        };
        dagRij.Controls.Add(new Label { Text = "Dag", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        dagRij.Controls.Add(dagKiezer);

        // De uitleg van Claude bij het voorstel (keuzes, aannames) — informatief, komt
        // nooit in de timesheets zelf terecht.
        TextBox? uitleg = null;
        Panel? uitlegPaneel = null;
        if (toelichting.Length > 0)
        {
            uitleg = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                Text = toelichting,
            };
            uitlegPaneel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 74,
                Padding = new Padding(10, 4, 10, 8),
            };
            uitlegPaneel.Controls.Add(uitleg);
        }

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
        if (gemaaktOp is not null)
        {
            // Alleen bij een klaargezet voorstel: een verse run duurt minuten, dus dat
            // doe je bewust — bv. omdat er na het klaarzetten nog doorgewerkt is.
            var vernieuw = new ModernButton
            {
                Text = "Vernieuwen", Width = 115, DialogResult = DialogResult.Retry,
            };
            knoppen.Controls.Add(vernieuw);
        }
        AcceptButton = ok;
        CancelButton = annuleer;

        Controls.Add(_grid);
        if (uitlegPaneel is not null)
        {
            Controls.Add(uitlegPaneel); // tussen hint en grid (docking loopt achterstevoren)
        }
        Controls.Add(hint);
        Controls.Add(dagRij);
        Controls.Add(_totaal);
        Controls.Add(knoppen);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        _totaal.ForeColor = Theme.Muted;
        if (uitleg is not null)
        {
            uitleg.BackColor = BackColor;
            uitleg.ForeColor = Theme.Muted;
        }

        foreach (var regel in voorstel)
        {
            _grid.Rows.Add(
                true, regel.Van?.ToString("HH:mm") ?? "", regel.Minuten, regel.Klant, regel.Omschrijving);
        }
        WerkTotaalBij();
    }

    /// <summary>Leest één rij uit; null als de regel onbruikbaar is.</summary>
    private TimesheetRegel? Lees(DataGridViewRow rij)
    {
        if (!int.TryParse(rij.Cells[2].Value?.ToString(), out var minuten) || minuten <= 0)
        {
            return null;
        }
        var omschrijving = rij.Cells[4].Value?.ToString()?.Trim() ?? "";
        if (omschrijving.Length == 0)
        {
            return null;
        }
        return new TimesheetRegel
        {
            Datum = _dag,
            Van = TimeOnly.TryParse(rij.Cells[1].Value?.ToString(), out var van) ? van : null,
            Klant = rij.Cells[3].Value?.ToString() is { Length: > 0 } klant ? klant : "Niet factureerbaar",
            Minuten = minuten,
            Omschrijving = omschrijving,
            Bron = "dagvoorstel",
        };
    }

    private void WerkTotaalBij()
    {
        var perKlant = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true)
            .Select(Lees)
            .OfType<TimesheetRegel>()
            .GroupBy(r => r.Klant)
            .Select(g => $"{g.Key} {g.Sum(r => r.Minuten) / 60.0:0.##} u")
            .ToList();
        var totaal = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true)
            .Sum(r => Lees(r)?.Minuten ?? 0);
        _totaal.Text = $"Totaal aangevinkt: {totaal / 60}u{totaal % 60:00}" +
            (perKlant.Count > 0 ? "  ·  " + string.Join("  ·  ", perKlant) : "");
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
            if (Lees(rij) is not { } regel)
            {
                MessageBox.Show(this,
                    $"Regel {rij.Index + 1} is onvolledig (minuten > 0 en een omschrijving nodig).",
                    "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Gekozen.Add(regel);
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
