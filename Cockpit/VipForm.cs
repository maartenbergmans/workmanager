namespace WorkManager;

/// <summary>
/// Beheervenster voor de VIP-lijst: afzenders en gesprekken die in de berichtencockpit voorrang
/// krijgen. Toevoegen kan hier met de hand (handig voor iemand die nog niets gestuurd heeft) of
/// via rechtsklikken op een bericht in de cockpit zelf.
/// </summary>
public class VipForm : Form
{
    private readonly ModernListView _lijst;
    private readonly TextBox _sleutel;
    private readonly TextBox _naam;
    private readonly ComboBox _soort;
    private List<VipItem> _items;

    public VipForm()
    {
        Text = "VIP-lijst";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(620, 520);
        MinimumSize = new Size(480, 360);

        _items = VipLijst.Laad();

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            LegeTekst = "Nog geen VIP's. Voeg hieronder iemand toe, of rechtsklik op een bericht "
                        + "in de cockpit en kies “Als VIP markeren”.",
            LeegGlyph = "⭐",
        };
        _lijst.Columns.Add("Naam", 190);
        _lijst.Columns.Add("Adres of chat", 290);
        _lijst.Columns.Add("Soort", 80);

        var invoer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(10, 8, 10, 4),
            ColumnCount = 4,
            RowCount = 2,
        };
        invoer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        invoer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        invoer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        invoer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        _sleutel = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "e-mailadres of chatnaam" };
        _naam = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "naam (optioneel)" };
        _soort = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _soort.Items.AddRange(new object[] { "mail", "chat" });
        _soort.SelectedIndex = 0;
        var toevoegen = new ModernButton { Text = "Toevoegen", Dock = DockStyle.Fill, Kind = ButtonKind.Accent };
        toevoegen.Click += (_, _) => Toevoegen();

        invoer.Controls.Add(new Label { Text = "Adres of chatnaam", AutoSize = true }, 0, 0);
        invoer.Controls.Add(new Label { Text = "Naam", AutoSize = true }, 1, 0);
        invoer.Controls.Add(new Label { Text = "Soort", AutoSize = true }, 2, 0);
        invoer.Controls.Add(_sleutel, 0, 1);
        invoer.Controls.Add(_naam, 1, 1);
        invoer.Controls.Add(_soort, 2, 1);
        invoer.Controls.Add(toevoegen, 3, 1);

        var onderaan = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluiten = new ModernButton { Text = "Sluiten", Width = 100 };
        sluiten.Click += (_, _) => Close();
        var verwijderen = new ModernButton { Text = "Verwijderen", Width = 120 };
        verwijderen.Click += (_, _) => Verwijderen();
        onderaan.Controls.Add(sluiten);
        onderaan.Controls.Add(verwijderen);

        var uitleg = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 46,
            Padding = new Padding(12, 4, 12, 0),
            Text = "Berichten van een VIP komen bovenaan in de cockpit te staan, krijgen een ster "
                   + "en leveren een tray-melding op zodra er een nieuw bericht van binnenkomt.",
        };

        Controls.Add(_lijst);
        Controls.Add(uitleg);
        Controls.Add(onderaan);
        Controls.Add(invoer);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        uitleg.ForeColor = Theme.Muted;

        _lijst.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                Verwijderen();
            }
        };
        _sleutel.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Toevoegen();
            }
        };

        Vul();
    }

    private void Vul()
    {
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var item in _items.OrderBy(v => v.Naam.Length > 0 ? v.Naam : v.Sleutel,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            _lijst.Items.Add(new ListViewItem(new[]
            {
                item.Naam.Length > 0 ? item.Naam : "—", item.Sleutel, item.Soort,
            })
            { Tag = item });
        }
        _lijst.EndUpdate();
    }

    private void Toevoegen()
    {
        var sleutel = _sleutel.Text.Trim();
        if (sleutel.Length == 0)
        {
            return;
        }
        _items.RemoveAll(v => string.Equals(v.Sleutel, sleutel, StringComparison.OrdinalIgnoreCase));
        _items.Add(new VipItem
        {
            Sleutel = sleutel,
            Naam = _naam.Text.Trim(),
            Soort = _soort.SelectedItem as string ?? "mail",
        });
        VipLijst.Bewaar(_items);
        _sleutel.Clear();
        _naam.Clear();
        Vul();
    }

    private void Verwijderen()
    {
        var weg = _lijst.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<VipItem>().ToList();
        if (weg.Count == 0)
        {
            return;
        }
        _items = _items.Where(v => !weg.Contains(v)).ToList();
        VipLijst.Bewaar(_items);
        Vul();
    }
}
