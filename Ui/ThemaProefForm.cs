namespace WorkManager;

/// <summary>
/// Proefvenster voor de kleurenschema's: toont in één scherm alle bouwstenen die de app
/// gebruikt (knoppen, groepen, lijsten met kleurcodes, grid, invoervelden, datumkiezer,
/// statuslabels, menu). Bedoeld om na een themawijziging te controleren dat álles leesbaar
/// blijft — starten met: WorkManager.exe --venster thema [themanaam].
/// </summary>
public sealed class ThemaProefForm : Form
{
    public ThemaProefForm()
    {
        Text = $"Themaproef — {Theme.Palet.Naam}";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1180, 760);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        var gewoon = new ModernButton { Text = "Gewone knop", Glyph = Fluent.Refresh };
        gewoon.KrimpNaarInhoud();
        var accent = new ModernButton { Text = "Primaire actie", Kind = ButtonKind.Accent, Glyph = Fluent.Check };
        accent.KrimpNaarInhoud();
        var bezig = new ModernButton { Text = "Bezig…", Glyph = Fluent.Sync, Bezig = true };
        bezig.KrimpNaarInhoud();
        var uit = new ModernButton { Text = "Uitgeschakeld", Enabled = false };
        uit.KrimpNaarInhoud();
        var dropdown = new ModernButton { Text = "Menu ▾" };
        dropdown.KrimpNaarInhoud(dropdown: true);
        var menu = new ContextMenuStrip();
        Theme.Style(menu);
        menu.Items.Add(new ToolStripMenuItem("Gewoon item"));
        menu.Items.Add(new ToolStripMenuItem("Aangevinkt item") { Checked = true });
        // Submenu: dat is een aparte ToolStrip en viel vroeger terug op de Windows-renderer
        // (lichte tekst op wit). Hier staat het in de proef zodat dat opvalt.
        var subItem = new ToolStripMenuItem("Submenu met keuzes");
        subItem.DropDownItems.Add(new ToolStripMenuItem("Eerste keuze"));
        subItem.DropDownItems.Add(new ToolStripMenuItem("Tweede keuze") { Checked = true });
        subItem.DropDownItems.Add(new ToolStripSeparator());
        subItem.DropDownItems.Add(new ToolStripMenuItem("Uitgeschakelde keuze") { Enabled = false });
        menu.Items.Add(subItem);
        // Submenu dat pas bij het openen gevuld wordt (zoals de snooze-presets in de cockpit).
        var laatItem = new ToolStripMenuItem("Submenu, laat gevuld");
        laatItem.DropDownOpening += (_, _) =>
        {
            laatItem.DropDownItems.Clear();
            laatItem.DropDownItems.Add(new ToolStripMenuItem("Pas nu aangemaakt"));
            laatItem.DropDownItems.Add(new ToolStripMenuItem("En nog eentje"));
        };
        menu.Items.Add(laatItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Uitgeschakeld item") { Enabled = false });
        dropdown.Click += (_, _) => menu.Show(dropdown, new Point(0, dropdown.Height + 4));
        var status = new Label { AutoSize = true, Text = "Statuslabel · gedempte tekst" };
        // De stem van het thema hoort ook in de proef: zo zie je vóór het kiezen
        // welke toon je erbij krijgt.
        var citaat = new Label { AutoSize = true, Text = ThemaCitaat.Aangehaald() };
        Theme.AsStatus(status);
        Theme.AsStatus(citaat);
        toolbar.Controls.AddRange(new Control[] { gewoon, accent, bezig, uit, dropdown, status, citaat });

        // Lijst met alle betekeniskleuren die de app gebruikt.
        var lijst = new ModernListView { Dock = DockStyle.Fill };
        lijst.Columns.Add("Rol", 190);
        lijst.Columns.Add("Voorbeeldtekst", 330);
        lijst.Columns.Add("Extra", 150);
        void Rij(string rol, string tekst, Color kleur, string extra = "")
        {
            var item = new ListViewItem(rol) { UseItemStyleForSubItems = false, ForeColor = kleur };
            var sub = item.SubItems.Add(tekst);
            sub.ForeColor = kleur;
            var e = item.SubItems.Add(extra);
            e.ForeColor = Theme.Muted;
            lijst.Items.Add(item);
        }
        Rij("Tekst", "Gewone tekst in een lijstrij", Theme.Text, "Theme.Text");
        Rij("Gedempt", "Minder belangrijke informatie", Theme.Muted, "Theme.Muted");
        Rij("Accent", "Nadruk of link", Theme.Accent, "Theme.Accent");
        Rij("Accent hover", "Klok, actieve elementen", Theme.AccentHover, "Theme.AccentHover");
        Rij("Waarschuwing", "Deadline nadert", Theme.Warn, "Theme.Warn");
        Rij("Succes", "Afgewerkt", Theme.Success, "Theme.Success");
        Rij("Gevaar", "Urgent of te laat", Theme.Danger, "Theme.Danger");
        Rij("CED", "Klantkleur CED", Theme.KlantCed, "blauw");
        Rij("Aqurat", "Klantkleur Aqurat", Theme.KlantAqurat, "oranje");
        Rij("RadiologyPartners", "Klantkleur RP", Theme.KlantRadiology, "teal");
        Rij("UrbanIT", "Klantkleur UrbanIT", Theme.KlantUrbanIt, "lila");
        Rij("Privé", "Klantkleur privé", Theme.KlantPrive, "groen");
        Rij("Lauryssens", "Klantkleur Lauryssens", Theme.KlantLauryssens, "goud");
        Rij("Gmail", "Berichtbron Gmail", Theme.VoorBron("gmail"), "merkkleur");
        Rij("Chat", "Berichtbron Google Chat", Theme.VoorBron("chat"), "merkkleur");
        Rij("WhatsApp", "Berichtbron WhatsApp", Theme.VoorBron("whatsapp"), "merkkleur");
        Rij("Teams", "Berichtbron Teams", Theme.VoorBron("teams"), "merkkleur");
        var lijstGroep = new ModernGroupBox
        {
            Text = "Lijst met betekeniskleuren", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        lijstGroep.Controls.Add(lijst);
        lijstGroep.Accent = Theme.Accent;

        // Grid + invoervelden.
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
            RowHeadersVisible = false,
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Aan", Width = 50 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Omschrijving", Width = 260 });
        var combo = new DataGridViewComboBoxColumn { HeaderText = "Keuze", Width = 130, FlatStyle = FlatStyle.Flat };
        combo.Items.AddRange("CED", "Aqurat", "UrbanIT");
        grid.Columns.Add(combo);
        grid.Rows.Add(true, "Rij in een tabel", "CED");
        grid.Rows.Add(false, "Tweede rij, niet aangevinkt", "Aqurat");
        grid.Rows.Add(true, "Derde rij", "UrbanIT");

        var velden = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 150, ColumnCount = 4, Padding = new Padding(10, 8, 10, 8),
        };
        velden.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        velden.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        velden.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        velden.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var tekstvak = new TextBox { Dock = DockStyle.Fill, Text = "Ingevulde tekst" };
        var leegvak = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "placeholder-tekst" };
        var keuze = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        keuze.Items.AddRange(new object[] { "Eerste keuze", "Tweede keuze" });
        keuze.SelectedIndex = 0;
        var datum = new DatumKiezer { Waarde = DateOnly.FromDateTime(DateTime.Today), Width = 190 };
        var leegDatum = new DatumKiezer { LeegTekst = "geen deadline", Width = 190 };
        var vinkje = new CheckBox { Text = "Een vinkje", AutoSize = true, Checked = true };
        var nummer = new NumericUpDown { Width = 90, Value = 42 };
        Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left };
        velden.Controls.Add(L("Tekst"), 0, 0);
        velden.Controls.Add(tekstvak, 1, 0);
        velden.Controls.Add(L("Leeg"), 2, 0);
        velden.Controls.Add(leegvak, 3, 0);
        velden.Controls.Add(L("Keuze"), 0, 1);
        velden.Controls.Add(keuze, 1, 1);
        velden.Controls.Add(L("Datum"), 2, 1);
        velden.Controls.Add(datum, 3, 1);
        velden.Controls.Add(L("Vinkje"), 0, 2);
        velden.Controls.Add(vinkje, 1, 2);
        velden.Controls.Add(L("Getal"), 2, 2);
        velden.Controls.Add(leegDatum, 3, 2);
        velden.Controls.Add(nummer, 1, 3);
        velden.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        velden.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        velden.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        velden.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var rechtsGroep = new ModernGroupBox
        {
            Text = "Tabel en invoervelden", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        rechtsGroep.Controls.Add(grid);
        rechtsGroep.Controls.Add(velden);
        rechtsGroep.Accent = Theme.Success;

        var leegGroep = new ModernGroupBox
        {
            Text = "Lege lijst", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(10, 8, 10, 10),
        };
        // Silhouet per soort: hier de meetings-variant, zodat de proef laat zien dat niet
        // elk leeg paneel hetzelfde beeld krijgt.
        var leeg = new ModernListView
        {
            Dock = DockStyle.Fill, LegeTekst = ThemaStem.GeenMeetings(vandaag: true),
            LeegSoort = "meetings", LeegGlyph = Fluent.Kalender,
        };
        leeg.Columns.Add("Kolom", 200);
        leegGroep.Controls.Add(leeg);
        leegGroep.Accent = Theme.Warn;

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
        split.Panel1.Controls.Add(lijstGroep);
        split.Panel2.Controls.Add(rechtsGroep);
        split.Panel2.Controls.Add(leegGroep);

        Controls.Add(split);
        Controls.Add(toolbar);
        Theme.Apply(this, fade: false);
        Theme.EscSluit(this);
        Shown += (_, _) =>
        {
            split.SplitterDistance = 560;
            // Ook de openingsanimatie van het thema meetesten (gun barrel, zon, scanlijn).
            ThemaIntro.Speel(this);
            Toast.Toon(this, ThemaStem.Dagdeel(), Fluent.Ster);
        };
    }
}
