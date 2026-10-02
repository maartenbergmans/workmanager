namespace WorkManager;

/// <summary>
/// Beheervenster voor de Billit-regels: welke mails een leveranciersfactuur zijn en welke
/// bijlage daarvan naar Billit moet. Zelfde opzet als
/// <see cref="ArchiveerRegelsForm"/>, met één veld extra — het bijlagepatroon, want een
/// factuurmail bevat vaak ook een betalingsbewijs dat niet in de boekhouding hoort.
/// </summary>
public sealed class BillitRegelsForm : Form
{
    private readonly List<BillitRegel> _regels = BillitRegels.Load();
    private readonly ListBox _lijst;
    private readonly TextBox _afzender;
    private readonly TextBox _onderwerp;
    private readonly TextBox _bijlage;

    /// <param name="voorstelAfzender">Vooringevulde afzender (via "Billit-regel van dit bericht").</param>
    /// <param name="voorstelOnderwerp">Vooringevuld onderwerp.</param>
    /// <param name="voorstelBijlage">Vooringevuld bijlagepatroon.</param>
    public BillitRegelsForm(
        string voorstelAfzender = "", string voorstelOnderwerp = "", string voorstelBijlage = "")
    {
        Text = "Billit-regels (facturen)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var uitleg = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 8, 12, 0),
            Text = "Mails die aan een regel voldoen leveren een taak op. Dubbelklik die taak " +
                "en de bijlage gaat naar Billit, de mail wordt gearchiveerd.",
        };

        _lijst = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        HervulLijst();
        _lijst.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                VerwijderSelectie();
            }
        };
        var lijstGroep = new ModernGroupBox
        {
            Text = "Regels", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        lijstGroep.Controls.Add(_lijst);

        var nieuwGroep = new ModernGroupBox
        {
            Text = "Nieuwe regel (minstens afzender of onderwerp invullen)",
            Dock = DockStyle.Bottom, Height = 160, Padding = new Padding(10, 8, 10, 10),
        };
        _afzender = new TextBox
        {
            Text = voorstelAfzender, Location = new Point(140, 28), Width = 370,
            PlaceholderText = "bv. invoice+statements@mail.anthropic.com",
        };
        _onderwerp = new TextBox
        {
            Text = voorstelOnderwerp, Location = new Point(140, 60), Width = 336,
            PlaceholderText = "bv. \"receipt from\"",
        };
        // Het voorgestelde onderwerp bevat bijna altijd een factuurnummer: met één klik leeg
        // te maken, zodat de regel ook de volgende maand nog matcht.
        var wisOnderwerp = new ModernButton
        {
            Text = "✕", Width = 28, Height = _onderwerp.Height, Location = new Point(482, 59),
        };
        wisOnderwerp.Click += (_, _) =>
        {
            _onderwerp.Clear();
            _onderwerp.Focus();
        };
        _bijlage = new TextBox
        {
            Text = voorstelBijlage.Length > 0 ? voorstelBijlage : "*.pdf",
            Location = new Point(140, 92), Width = 370,
            PlaceholderText = "bv. Invoice-*.pdf",
        };
        var voegToe = new ModernButton
        {
            Text = "Regel toevoegen", Width = 155, Kind = ButtonKind.Accent, Glyph = Fluent.Add,
            Location = new Point(355, 124),
        };
        voegToe.Click += (_, _) => VoegToe();
        var verwijder = new ModernButton
        {
            Text = "Verwijderen", Width = 120, Glyph = Fluent.Delete, Location = new Point(12, 124),
        };
        verwijder.Click += (_, _) => VerwijderSelectie();
        nieuwGroep.Controls.AddRange(new Control[]
        {
            new Label { Text = "Afzender bevat:", AutoSize = true, Location = new Point(12, 32) },
            _afzender,
            new Label { Text = "Onderwerp bevat:", AutoSize = true, Location = new Point(12, 64) },
            _onderwerp, wisOnderwerp,
            new Label { Text = "Bijlage (* mag):", AutoSize = true, Location = new Point(12, 96) },
            _bijlage,
            voegToe, verwijder,
        });

        Controls.Add(lijstGroep);
        Controls.Add(nieuwGroep);
        Controls.Add(uitleg);
        Theme.Apply(this);
        Theme.EscSluit(this);
        uitleg.ForeColor = Theme.Muted;
    }

    private void HervulLijst()
    {
        _lijst.Items.Clear();
        foreach (var regel in _regels)
        {
            _lijst.Items.Add(regel.ToString());
        }
    }

    private void VoegToe()
    {
        var regel = new BillitRegel
        {
            Afzender = _afzender.Text.Trim(),
            Onderwerp = _onderwerp.Text.Trim(),
            Bijlage = _bijlage.Text.Trim().Length > 0 ? _bijlage.Text.Trim() : "*.pdf",
        };
        if (regel.Afzender.Length == 0 && regel.Onderwerp.Length == 0)
        {
            Toast.Toon(this, "Vul minstens afzender of onderwerp in", Fluent.Edit);
            return;
        }
        _regels.Add(regel);
        BillitRegels.Save(_regels);
        HervulLijst();
        _afzender.Clear();
        _onderwerp.Clear();
        _bijlage.Text = "*.pdf";
        Toast.Toon(this, "Billit-regel toegevoegd", Fluent.Check);
    }

    private void VerwijderSelectie()
    {
        if (_lijst.SelectedIndex is var i && i >= 0 && i < _regels.Count)
        {
            _regels.RemoveAt(i);
            BillitRegels.Save(_regels);
            HervulLijst();
        }
    }
}
