namespace WorkManager;

/// <summary>
/// Handmatige vakantie-invoer (los van SD Worx): periodes per persoon, ook voor Maarten
/// zelf. De weekmail slaat leden die de hele werkweek afwezig zijn over bij het toewijzen
/// van taken en vermeldt de afwezigheden bovenaan. Wijzigingen worden direct bewaard.
/// </summary>
public class TeamVakantiesForm : Form
{
    private readonly TeamTasksData _data;
    private readonly ModernListView _list;
    private readonly ComboBox _persoon;
    private readonly DatumKiezer _van;
    private readonly DatumKiezer _tot;

    public TeamVakantiesForm(TeamTasksData data)
    {
        _data = data;
        Text = "Vakanties ingeven";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 460);
        MinimizeBox = false;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _persoon = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDown };
        // Maarten zelf bovenaan; verder de teamleden. Vrij typen kan ook (bv. een externe).
        foreach (var naam in new[] { "Maarten" }.Concat(data.Leden)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _persoon.Items.Add(naam);
        }
        _persoon.SelectedIndex = 0;
        // Periode: van/tot met dezelfde kiezer als overal. "Tot" kan nooit vóór "van"
        // liggen (MinimumDatum schuift mee) en één dag vrij is één klik: tot = van.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        _van = new DatumKiezer
        {
            Width = 165, Waarde = vandaag, LeegToegestaan = false, Margin = new Padding(6, 6, 0, 0),
        };
        _tot = new DatumKiezer
        {
            Width = 165, Waarde = vandaag, LeegToegestaan = false, MinimumDatum = vandaag,
            Margin = new Padding(6, 6, 0, 0),
        };
        _van.WaardeGewijzigd += (_, _) => _tot.MinimumDatum = _van.Waarde;
        var eenDag = new ModernButton { Text = "1 dag", Margin = new Padding(6, 6, 0, 0) };
        eenDag.KrimpNaarInhoud();
        eenDag.Click += (_, _) => _tot.Waarde = _van.Waarde;
        var toevoegen = new ModernButton
        {
            Text = "Toevoegen", Width = 110, Kind = ButtonKind.Accent, Glyph = Fluent.Add,
            Margin = new Padding(6, 6, 0, 0),
        };
        toevoegen.Click += (_, _) => Toevoegen();
        toolbar.Controls.AddRange(new Control[] { _persoon, _van, _tot, eenDag, toevoegen });

        _list = new ModernListView { Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Clickable };
        _list.Columns.Add("Wie", 150);
        _list.Columns.Add("Van", 130);
        _list.Columns.Add("Tot en met", 130);
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                Verwijderen();
            }
        };
        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var verwijderItem = new ToolStripMenuItem("Verwijderen\tDel");
        verwijderItem.Click += (_, _) => Verwijderen();
        listMenu.Items.Add(verwijderItem);
        _list.ContextMenuStrip = listMenu;

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Een lid dat de hele werkweek afwezig is, krijgt in de weekmail geen taken.",
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluiten = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.OK, Width = 100 };
        buttons.Controls.Add(sluiten);
        CancelButton = sluiten;

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);
        Controls.Add(toolbar);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        VulLijst();
    }

    private void Toevoegen()
    {
        var persoon = _persoon.Text.Trim();
        if (persoon.Length == 0 || _van.Waarde is not { } van || _tot.Waarde is not { } tot)
        {
            return;
        }
        _data.Vakanties.Add(new VakantiePeriode
        {
            Persoon = persoon,
            Van = van,
            Tot = tot < van ? van : tot,
        });
        TeamTaskStore.Save(_data);
        VulLijst();
    }

    private void Verwijderen()
    {
        if (_list.SelectedItems.Count == 0 ||
            _list.SelectedItems[0].Tag is not VakantiePeriode periode)
        {
            return;
        }
        _data.Vakanties.Remove(periode);
        TeamTaskStore.Save(_data);
        VulLijst();
    }

    private void VulLijst()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var v in _data.Vakanties.OrderBy(v => v.Van).ThenBy(v => v.Persoon))
        {
            var item = new ListViewItem(v.Persoon) { Tag = v };
            item.SubItems.Add(v.Van.ToString("ddd d MMM yyyy"));
            item.SubItems.Add(v.Tot.ToString("ddd d MMM yyyy"));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }
}
