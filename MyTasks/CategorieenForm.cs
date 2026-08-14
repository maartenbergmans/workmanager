namespace WorkManager;

/// <summary>
/// Beheerscherm voor de categorieën van de persoonlijke takenlijst: één naam per regel,
/// in de volgorde waarin ze in het venster komen. Taken van een verwijderde categorie
/// blijven zichtbaar zolang ze bestaan.
/// </summary>
public class CategorieenForm : Form
{
    private readonly TextBox _tekst;

    public List<string> Categorieen { get; private set; } = new();

    public CategorieenForm(List<string> categorieen)
    {
        Text = "Categorieën";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(360, 420);
        MinimizeBox = false;

        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoFont,
            Text = string.Join(Environment.NewLine, categorieen),
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Eén categorie per regel, in de gewenste volgorde.",
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
        ok.Click += (_, _) => Categorieen = _tekst.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;

        Controls.Add(_tekst);
        Controls.Add(hint);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
    }
}
