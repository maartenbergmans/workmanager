namespace WorkManager;

/// <summary>
/// Dialoog om een taak in "Mijn taken" te maken: vanuit een mail (voorgestelde taaktekst,
/// optioneel de mail meteen archiveren) of los vanuit de cockpit (mail = null). De snelle
/// invoer met "!" en "@…" werkt hier ook.
/// </summary>
public class MailTaakForm : Form
{
    private readonly TextBox _tekst;
    private readonly ComboBox _categorie;
    private readonly DatumKiezer _deadline;
    private readonly DatumKiezer _start;
    private readonly DateTimePicker _startUur;
    private readonly CheckBox _urgent;
    private readonly CheckBox _archiveer;

    public string TaakTekst { get; private set; } = "";
    public string Categorie { get; private set; } = "";
    public int Prioriteit { get; private set; } = 1;
    public DateOnly? Deadline { get; private set; }
    /// <summary>Dag waarop de taak pas moet opduiken; null = meteen zichtbaar.</summary>
    public DateOnly? Startdatum { get; private set; }
    /// <summary>Vroegste uur voor de dagplanning; null = geen beperking.</summary>
    public TimeOnly? StartUur { get; private set; }
    public bool Archiveren => _archiveer.Checked;

    public MailTaakForm(MailBericht? mail = null)
    {
        Text = mail is null ? "Nieuwe taak" : "Taak maken van mail";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 0),
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = mail is null ? "" : $"Opvolgen: {mail.Onderwerp} ({mail.Van})",
        };
        _categorie = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var categorie in MijnTaakStore.Load().Categorieen)
        {
            _categorie.Items.Add(categorie);
        }
        if (_categorie.Items.Count > 0)
        {
            _categorie.SelectedIndex = 0;
        }
        // Deadline standaard vandaag, zodat de taak meteen zichtbaar is in het cockpit-filter
        // "Deadline ≤ 2 dagen"; hij kan nooit vóór de startdatum liggen.
        _deadline = new DatumKiezer
        {
            Waarde = DateOnly.FromDateTime(DateTime.Today),
            LeegTekst = "geen deadline",
            Width = 190,
        };
        _urgent = new CheckBox
        {
            Text = "Urgent (hoge prioriteit)",
            AutoSize = true,
            Margin = new Padding(16, 5, 0, 0),
        };
        var zelfdeDag = new ModernButton { Text = "= start", Height = 28, Margin = new Padding(8, 1, 0, 0) };
        zelfdeDag.KrimpNaarInhoud();
        zelfdeDag.Visible = false;
        zelfdeDag.Click += (_, _) => _deadline.Waarde = _start.Waarde;
        var deadlinePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0),
        };
        deadlinePanel.Controls.Add(_deadline);
        deadlinePanel.Controls.Add(zelfdeDag);
        deadlinePanel.Controls.Add(_urgent);
        // Startdatum: vóór die dag blijft de taak uit de lijsten (leeg = meteen zichtbaar).
        _start = new DatumKiezer
        {
            Waarde = null,
            LeegTekst = "meteen zichtbaar",
            Width = 190,
        };
        _start.WaardeGewijzigd += (_, _) =>
        {
            _deadline.MinimumDatum = _start.Waarde; // deadline schuift mee als hij te vroeg lag
            zelfdeDag.Visible = _start.Waarde is not null;
        };
        // Vroegste uur: de dagplanning plant de taak dan niet eerder (bv. bellen vanaf 9 u).
        _startUur = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            ShowCheckBox = true,
            Checked = false,
            Value = DateTime.Today.AddHours(9),
            Width = 95,
            CustomFormat = "HH:mm",
            Margin = new Padding(8, 4, 0, 0),
        };
        var startPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0),
        };
        startPanel.Controls.Add(_start);
        startPanel.Controls.Add(new Label
        {
            Text = "niet vóór", AutoSize = true, ForeColor = Theme.Muted,
            Margin = new Padding(12, 8, 4, 0),
        });
        startPanel.Controls.Add(_startUur);
        startPanel.Controls.Add(new Label
        {
            Text = "uur",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = new Padding(6, 8, 0, 0),
        });
        _archiveer = new CheckBox
        {
            Text = "Mail meteen archiveren (blijft in Gmail onder 'Alle berichten')",
            AutoSize = true,
            Checked = mail is not null,
            Visible = mail is not null,
        };

        // Volgorde zoals je erover denkt: eerst vanaf wanneer, dan tegen wanneer.
        AddRow(grid, 0, "Taak:", _tekst);
        AddRow(grid, 1, "Categorie:", _categorie);
        AddRow(grid, 2, "Vanaf:", startPanel);
        AddRow(grid, 3, "Uiterlijk:", deadlinePanel);
        AddRow(grid, 4, "", _archiveer);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
            Text = "Taak maken", Width = 125, Kind = ButtonKind.Accent, Glyph = Fluent.Checkbox,
        };
        ok.Click += (_, _) => Bevestig();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        Theme.Apply(this);
        _tekst.Select(0, 0);
    }

    private static void AddRow(TableLayoutPanel grid, int rij, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        if (label.Length > 0)
        {
            grid.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
            }, 0, rij);
        }
        grid.Controls.Add(control, 1, rij);
    }

    private void Bevestig()
    {
        // De snelle codes ("!", "@…") blijven werken voor wie ze typt, maar de velden
        // hieronder zijn leidend.
        var (tekst, prio, deadline) = MijnTakenForm.ParseSnel(_tekst.Text);
        if (tekst.Length == 0)
        {
            MessageBox.Show(this, "Vul een taakomschrijving in.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        TaakTekst = tekst;
        Prioriteit = _urgent.Checked ? 0 : prio;
        Deadline = _deadline.Waarde ?? deadline;
        Startdatum = _start.Waarde;
        StartUur = _startUur.Checked ? TimeOnly.FromDateTime(_startUur.Value) : null;
        Categorie = _categorie.SelectedItem as string ?? "";
        DialogResult = DialogResult.OK;
    }
}
