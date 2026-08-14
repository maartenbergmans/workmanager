using System.ComponentModel;
using System.Globalization;

namespace WorkManager;

/// <summary>
/// Beheerscherm voor de auto-goedkeuringsregels: leverancier + maximumbedrag,
/// met toevoegen/wijzigen/verwijderen via een grid.
/// </summary>
public class RulesForm : Form
{
    private readonly BindingList<ApprovalRule> _rules;
    private readonly DataGridView _grid;

    public RulesForm(string? nieuweLeverancier = null)
    {
        Text = "Auto-goedkeuringsregels ISPnext";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 640);
        MinimizeBox = false;

        _rules = new BindingList<ApprovalRule>(
            ApprovalRules.Load().OrderByDescending(r => r.MaxBedrag).ThenBy(r => r.Leverancier).ToList());

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersWidth = 24,
            DataSource = _rules,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ApprovalRule.Leverancier),
            HeaderText = "Leverancier (exacte naam in ISPnext)",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });
        var bedragCol = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ApprovalRule.MaxBedrag),
            HeaderText = "Max. bedrag (€)",
            Width = 130,
        };
        bedragCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        bedragCol.DefaultCellStyle.Format = "N2";
        bedragCol.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("nl-BE");
        _grid.Columns.Add(bedragCol);

        // Ongeldig bedrag: melding tonen i.p.v. de standaard exception-dialoog.
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            MessageBox.Show(this, "Ongeldig bedrag.", "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Facturen van deze leveranciers worden tot het maximumbedrag automatisch geselecteerd.\n" +
                   "De naam moet exact overeenkomen met de leveranciersnaam in ISPnext (hoofdletterongevoelig).",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", DialogResult = DialogResult.OK, Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => SaveRules();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(hint);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;

        if (!string.IsNullOrWhiteSpace(nieuweLeverancier) &&
            ApprovalRules.Match(_rules, nieuweLeverancier) is null)
        {
            _rules.Add(new ApprovalRule { Leverancier = nieuweLeverancier.Trim() });
            Shown += (_, _) =>
            {
                var row = _grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(r => r.DataBoundItem is ApprovalRule a && a.Leverancier == nieuweLeverancier.Trim());
                if (row is not null)
                {
                    _grid.CurrentCell = row.Cells[1];
                    _grid.BeginEdit(true);
                }
            };
        }
    }

    private void SaveRules()
    {
        _grid.EndEdit();
        ApprovalRules.Save(_rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Leverancier) && r.MaxBedrag > 0)
            .ToList());
    }
}
