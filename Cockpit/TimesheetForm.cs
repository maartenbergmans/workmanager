namespace WorkManager;

/// <summary>
/// Dialoog om een timesheetregel te maken vanuit een meeting of mail: klantkeuze
/// (CED vooringevuld bij CED-bronnen), datum, duur in minuten en omschrijving.
/// </summary>
public class TimesheetForm : Form
{
    private readonly ComboBox _klant;
    private readonly DatumKiezer _datum;
    private readonly NumericUpDown _minuten;
    private readonly TextBox _omschrijving;

    public string Klant => _klant.SelectedItem as string ?? "";
    public DateOnly Datum => _datum.Waarde ?? DateOnly.FromDateTime(DateTime.Today);
    public int Minuten => (int)_minuten.Value;
    public string Omschrijving => _omschrijving.Text.Trim();

    public TimesheetForm(string? klantVoorstel, DateOnly datum, int minuten, string omschrijving)
    {
        Text = "Timesheet maken";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 268);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 14, 12, 0),
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _klant = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var klant in TimesheetStore.Klanten)
        {
            _klant.Items.Add(klant);
        }
        _klant.SelectedItem = klantVoorstel is { Length: > 0 } ? klantVoorstel : null;

        _datum = new DatumKiezer { Waarde = datum, LeegToegestaan = false, Width = 190 };
        _minuten = new NumericUpDown
        {
            Minimum = 5, Maximum = 720, Increment = 15,
            Value = Math.Clamp(minuten, 5, 720), Width = 90,
        };
        _omschrijving = new TextBox { Dock = DockStyle.Fill, Text = omschrijving };

        AddRow(grid, 0, "Klant:", _klant);
        AddRow(grid, 1, "Datum:", _datum);
        AddRow(grid, 2, "Minuten:", _minuten);
        AddRow(grid, 3, "Omschrijving:", _omschrijving);

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
            Text = "Timesheet maken", Width = 150, Kind = ButtonKind.Accent, Glyph = Fluent.Klok,
        };
        ok.Click += (_, _) =>
        {
            if (_klant.SelectedItem is null)
            {
                MessageBox.Show(this, "Kies een klant.", "WorkManager",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Omschrijving.Length == 0)
            {
                MessageBox.Show(this, "Vul een omschrijving in.", "WorkManager",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        Theme.Apply(this);
    }

    private static void AddRow(TableLayoutPanel grid, int rij, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.Controls.Add(new Label
        {
            Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 0, 0),
        }, 0, rij);
        grid.Controls.Add(control, 1, rij);
    }
}
