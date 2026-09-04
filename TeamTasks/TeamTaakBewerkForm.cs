namespace WorkManager;

/// <summary>
/// Bewerkdialoog voor een teamtaak: tekst aanpassen en/of aan een ander teamlid toewijzen.
/// Te openen met dubbelklik, F2 of het contextmenu in het Taken team-venster.
/// </summary>
public class TeamTaakBewerkForm : Form
{
    private readonly TextBox _tekst;
    private readonly ComboBox _lid;
    private readonly ComboBox _prio;
    private readonly TextBox _subtaken;

    private readonly List<SubTaak> _bestaandeSubtaken;

    public string TaakTekst => _tekst.Text.Trim();
    public string Lid => _lid.SelectedItem as string ?? "";
    public int Prioriteit => _prio.SelectedIndex; // 0 = hoog, 1 = normaal, 2 = laag

    /// <summary>
    /// De ingegeven subtaken (één per regel). Bestond een subtaak met dezelfde tekst al, dan
    /// blijven zijn afvinkstatus en prioriteit behouden (die worden in de lijst zelf beheerd
    /// met een checkbox en sterren); nieuwe regels krijgen de standaardwaarden.
    /// </summary>
    public List<SubTaak> Subtaken => _subtaken.Lines
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .Select(l => _bestaandeSubtaken
            .FirstOrDefault(s => string.Equals(s.Tekst, l, StringComparison.OrdinalIgnoreCase))
            ?? new SubTaak { Tekst = l })
        .ToList();

    public TeamTaakBewerkForm(List<string> leden, TeamTaak taak)
    {
        _bestaandeSubtaken = taak.Subtaken;
        Text = "Taak bewerken";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(600, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 14, 12, 0),
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _tekst = new TextBox { Dock = DockStyle.Fill, Text = taak.Tekst };
        _lid = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var lid in leden)
        {
            _lid.Items.Add(lid);
        }
        if (!_lid.Items.Contains(taak.Lid) && taak.Lid.Length > 0)
        {
            _lid.Items.Add(taak.Lid); // lid dat niet (meer) in de ledenlijst staat
        }
        _lid.SelectedItem = taak.Lid.Length > 0 ? taak.Lid : (_lid.Items.Count > 0 ? _lid.Items[0] : null);

        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label
        {
            Text = "Taak:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
        }, 0, 0);
        grid.Controls.Add(_tekst, 1, 0);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label
        {
            Text = "Teamlid:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
        }, 0, 1);
        grid.Controls.Add(_lid, 1, 1);

        _prio = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        _prio.Items.AddRange(new object[]
        {
            "★★★  hoog (gemarkeerd in de weekmail)", "★★  normaal", "★  laag",
        });
        _prio.SelectedIndex = Math.Clamp(taak.Prioriteit, 0, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label
        {
            Text = "Prioriteit:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
        }, 0, 2);
        grid.Controls.Add(_prio, 1, 2);

        _subtaken = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, taak.Subtaken.Select(s => s.Tekst)),
            PlaceholderText = "Eén subtaak per regel — afvinken en sterren beheer je in de lijst.",
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label
        {
            Text = "Subtaken:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, 3);
        grid.Controls.Add(_subtaken, 1, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", Width = 110, Kind = ButtonKind.Accent, Glyph = Fluent.Check };
        ok.Click += (_, _) => DialogResult = DialogResult.OK;
        // Opslaan hoort bij het taakveld: pas actief met een omschrijving (zelfde
        // koppeling als de Toevoegen-knop in het Taken team-venster).
        ok.Enabled = TaakTekst.Length > 0;
        _tekst.TextChanged += (_, _) => ok.Enabled = TaakTekst.Length > 0;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        Theme.Apply(this);
        _tekst.Select(0, 0);
    }
}
