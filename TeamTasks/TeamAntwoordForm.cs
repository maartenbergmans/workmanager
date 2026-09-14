namespace WorkManager;

/// <summary>
/// Bevestigingsdialoog voor <see cref="ClaudeTeamAntwoord"/>: de taken die volgens het
/// antwoord van het teamlid af zijn (aangevinkt = echt afvinken) en de nieuwe taken die
/// het lid noemt (aangevinkt = toevoegen). Niets gebeurt zonder "Toepassen".
/// </summary>
public class TeamAntwoordForm : Form
{
    private readonly ModernListView _lijst;

    public List<TeamTaak> AfTeVinken { get; } = new();
    public List<ClaudeTeamTaken.Voorstel> ToeTeVoegen { get; } = new();

    public TeamAntwoordForm(string afzender, ClaudeTeamAntwoord.Uitkomst uitkomst)
    {
        Text = $"Teamtaken bijwerken uit antwoord van {afzender}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 560);
        MinimizeBox = false;

        var kop = new Label
        {
            Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 10, 12, 0), ForeColor = Theme.Muted,
            Text = uitkomst.Samenvatting.Length > 0
                ? uitkomst.Samenvatting
                : "Claude las het antwoord; vink aan wat je wilt toepassen.",
        };

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill, CheckBoxes = true, FullRowSelect = true,
            LegeTekst = "Claude vond in dit antwoord geen afgewerkte of nieuwe taken.",
            LeegGlyph = Fluent.Mail,
        };
        _lijst.Columns.Add("Wat", 130);
        _lijst.Columns.Add("Taak", 520);
        _lijst.Columns.Add("Teamlid", 120);
        _lijst.Resize += (_, _) => _lijst.Columns[1].Width = Math.Max(250,
            _lijst.ClientSize.Width - _lijst.Columns[0].Width - _lijst.Columns[2].Width - 4);

        foreach (var taak in uitkomst.Klaar)
        {
            var item = new ListViewItem("✓ afvinken") { Tag = taak, Checked = true, UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.Success;
            item.SubItems.Add(taak.Tekst);
            item.SubItems.Add(taak.Lid).ForeColor = Theme.AccentHover;
            _lijst.Items.Add(item);
        }
        foreach (var voorstel in uitkomst.Nieuw)
        {
            var item = new ListViewItem("+ nieuwe taak") { Tag = voorstel, Checked = true, UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.Accent;
            item.SubItems.Add(voorstel.Tekst);
            item.SubItems.Add(voorstel.Lid).ForeColor = Theme.AccentHover;
            _lijst.Items.Add(item);
        }

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(10),
        };
        var annuleer = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton
        {
            Text = "Toepassen", Width = 130, Kind = ButtonKind.Accent, Glyph = Fluent.Check,
            Enabled = _lijst.Items.Count > 0,
        };
        ok.Click += (_, _) =>
        {
            foreach (ListViewItem item in _lijst.CheckedItems)
            {
                switch (item.Tag)
                {
                    case TeamTaak t: AfTeVinken.Add(t); break;
                    case ClaudeTeamTaken.Voorstel v: ToeTeVoegen.Add(v); break;
                }
            }
            DialogResult = DialogResult.OK;
        };
        knoppen.Controls.Add(annuleer);
        knoppen.Controls.Add(ok);
        CancelButton = annuleer;
        AcceptButton = ok;

        Controls.Add(_lijst);
        Controls.Add(kop);
        Controls.Add(knoppen);
        Theme.Apply(this);
    }
}
