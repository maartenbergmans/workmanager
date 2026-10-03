using System.Diagnostics;

namespace WorkManager;

/// <summary>
/// De bestelgeschiedenis van Albert Heijn: elke bestelling die ooit via de cockpit of de
/// webpagina is samengesteld, met de gerechten erbij en per product of het effectief in het
/// mandje lag. Twee weergaven — per bestelling (wat ging er die week mee) en per product (hoe
/// vaak en hoeveel) — plus een CSV-export om er buiten WorkManager mee verder te kunnen.
/// </summary>
public sealed class AhArchiefForm : Form
{
    private readonly ModernListView _bestellingen;
    private readonly ModernListView _detail;
    private readonly ModernListView _producten;
    private readonly TextBox _zoek;
    private readonly Label _voet;
    private readonly SplitContainer _split;
    private readonly ModernButton _knopBestellingen;
    private readonly ModernButton _knopProducten;
    private List<AhBesteldeRonde> _rondes = new();
    private bool _perProduct;

    public AhArchiefForm()
    {
        Text = "AH-bestelgeschiedenis";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1080, 680);
        MinimumSize = new Size(760, 440);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);

        _knopBestellingen = new ModernButton { Text = "Per bestelling", Width = 150 };
        _knopProducten = new ModernButton { Text = "Per product", Width = 140 };
        _knopBestellingen.Click += (_, _) => ZetWeergave(perProduct: false);
        _knopProducten.Click += (_, _) => ZetWeergave(perProduct: true);

        // Theme.Apply kleurt de velden mee; hier alleen de breedte en de hint.
        _zoek = new TextBox { Width = 240, PlaceholderText = "Zoek product of gerecht…" };
        _zoek.TextChanged += (_, _) => Vul();

        var exportKnop = new ModernButton { Text = "Exporteren (CSV)", Width = 180, Glyph = Fluent.Document };
        exportKnop.Click += (_, _) =>
        {
            try
            {
                var pad = AhBestelArchief.ExporteerCsv();
                Toast.ToonActie(this, $"Geëxporteerd naar {Path.GetFileName(pad)}", "openen",
                    () => Process.Start(new ProcessStartInfo(pad) { UseShellExecute = true }),
                    Fluent.Document);
            }
            catch (Exception ex)
            {
                Toast.Fout(this, "Exporteren lukte niet", ex.ToString());
            }
        };
        toolbar.Controls.AddRange(new Control[]
        {
            _knopBestellingen, _knopProducten, _zoek, exportKnop,
        });

        _bestellingen = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen bestellingen in het archief 🛒",
            LeegGlyph = Fluent.Winkelwagen,
        };
        _bestellingen.Columns.Add("Datum", 130);
        _bestellingen.Columns.Add("Producten", 90);
        _bestellingen.Columns.Add("Gerechten", 280);
        _bestellingen.Columns.Add("Bron", 80);
        _bestellingen.SelectedIndexChanged += (_, _) => VulDetail();

        _detail = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Kies een bestelling",
            LeegGlyph = Fluent.Lijst,
            RijHoogte = 42,
            IcoonGrootte = 32,
            RijIcoon = rij => rij.Tag is AhArchiefProduct p ? AhAfbeeldingen.Voor(p.Url) : null,
        };
        _detail.Columns.Add("Product", 260);
        _detail.Columns.Add("Aantal", 70);
        _detail.Columns.Add("Uit", 200);
        _detail.Columns.Add("Mandje", 80);
        // Dubbelklik opent het product op ah.be — handig om iets opnieuw te bestellen.
        _detail.DoubleClick += (_, _) =>
        {
            if (_detail.SelectedItems.Count > 0 &&
                _detail.SelectedItems[0].Tag is AhArchiefProduct { Url: { Length: > 0 } url })
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        };

        _producten = new ModernListView
        {
            Dock = DockStyle.Fill,
            Visible = false,
            LegeTekst = "Nog geen producten in het archief 🛒",
            LeegGlyph = Fluent.Winkelwagen,
        };
        _producten.Columns.Add("Product", 320);
        _producten.Columns.Add("Keren", 70);
        _producten.Columns.Add("Stuks", 70);
        _producten.Columns.Add("Laatst", 130);
        _producten.Columns.Add("Voor het eerst", 130);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 520,
        };
        _split.Panel1.Controls.Add(_bestellingen);
        _split.Panel2.Controls.Add(_detail);

        _voet = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 8, 12, 0) };
        Theme.AsStatus(_voet);

        Controls.Add(_split);
        Controls.Add(_producten);
        Controls.Add(toolbar);
        Controls.Add(_voet);

        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "aharchief");

        AhBestelArchief.MigreerUitHistoriek(); // eerste keer: de bestaande geschiedenis erin
        Herlaad();
        ZetWeergave(perProduct: false);
    }

    private void Herlaad()
    {
        _rondes = AhBestelArchief.Lees();
        _voet.Text = AhBestelArchief.Samenvatting();
        Vul();
    }

    private void ZetWeergave(bool perProduct)
    {
        _perProduct = perProduct;
        _split.Visible = !perProduct;
        _producten.Visible = perProduct;
        _knopBestellingen.Kind = perProduct ? ButtonKind.Normal : ButtonKind.Accent;
        _knopProducten.Kind = perProduct ? ButtonKind.Accent : ButtonKind.Normal;
        Vul();
    }

    private void Vul()
    {
        var zoek = _zoek.Text.Trim();
        if (_perProduct)
        {
            _producten.BeginUpdate();
            _producten.Items.Clear();
            foreach (var t in AhBestelArchief.PerProduct())
            {
                if (zoek.Length > 0 && !t.Naam.Contains(zoek, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rij = new ListViewItem(t.Naam);
                rij.SubItems.Add(t.Keren.ToString());
                rij.SubItems.Add(t.Stuks.ToString());
                rij.SubItems.Add($"{t.Laatste:d MMM yyyy} ({t.DagenGeleden} d)");
                rij.SubItems.Add($"{t.Eerste:d MMM yyyy}");
                _producten.Items.Add(rij);
            }
            _producten.EndUpdate();
            return;
        }

        _bestellingen.BeginUpdate();
        _bestellingen.Items.Clear();
        foreach (var ronde in _rondes)
        {
            if (zoek.Length > 0 &&
                !ronde.AlleNamen.Any(n => n.Contains(zoek, StringComparison.OrdinalIgnoreCase)) &&
                !ronde.Gerechten.Any(g => g.Contains(zoek, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var rij = new ListViewItem($"{ronde.Datum:ddd d MMM yyyy}") { Tag = ronde };
            var stuks = ronde.AantalStuks.ToString();
            rij.SubItems.Add(ronde.AantalInMandje is { } inMandje
                ? $"{stuks} ({inMandje} in mandje)"
                : stuks);
            rij.SubItems.Add(ronde.GerechtenKort);
            rij.SubItems.Add(ronde.Bron);
            _bestellingen.Items.Add(rij);
        }
        _bestellingen.EndUpdate();
        if (_bestellingen.Items.Count > 0)
        {
            _bestellingen.Items[0].Selected = true;
        }
        else
        {
            _detail.Items.Clear();
        }
    }

    private void VulDetail()
    {
        _detail.BeginUpdate();
        _detail.Items.Clear();
        if (_bestellingen.SelectedItems.Count > 0 &&
            _bestellingen.SelectedItems[0].Tag is AhBesteldeRonde ronde)
        {
            AhAfbeeldingen.Voorladen(ronde.Producten.Select(p => p.Url));
            foreach (var p in ronde.Producten)
            {
                var rij = new ListViewItem(p.Naam) { Tag = p };
                rij.SubItems.Add(Math.Max(1, p.Aantal).ToString());
                rij.SubItems.Add(p.Herkomst);
                rij.SubItems.Add(p.InMandje is null ? "" : p.InMandje == true ? "✓" : "⚠");
                _detail.Items.Add(rij);
            }
            foreach (var naam in ronde.Handmatig)
            {
                var rij = new ListViewItem(naam) { ForeColor = Theme.Muted };
                rij.SubItems.Add("1");
                rij.SubItems.Add("zelf zoeken");
                rij.SubItems.Add("");
                _detail.Items.Add(rij);
            }
        }
        _detail.EndUpdate();
    }

    /// <summary>De productfoto's komen asynchroon binnen; dan de rijen hertekenen.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // De verdeling pas nu zetten: in de constructor kent het venster zijn breedte nog
        // niet en viel de helft met de aantallen buiten beeld.
        _split.SplitterDistance = Math.Max(360, _split.ClientSize.Width / 2);
        AhAfbeeldingen.BeeldKlaar += OpBeeldKlaar;
        FormClosed += (_, _) => AhAfbeeldingen.BeeldKlaar -= OpBeeldKlaar;
    }

    private void OpBeeldKlaar()
    {
        if (!IsDisposed && IsHandleCreated)
        {
            BeginInvoke(() => _detail.Invalidate());
        }
    }
}
