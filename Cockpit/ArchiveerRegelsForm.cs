namespace WorkManager;

/// <summary>
/// Beheervenster voor de zelfgemaakte auto-archiveerregels: lijst met bestaande regels,
/// velden om een nieuwe toe te voegen (afzender en/of onderwerp bevat) en verwijderen met
/// Delete of de knop. Wijzigingen worden meteen bewaard.
/// </summary>
public sealed class ArchiveerRegelsForm : Form
{
    private readonly List<ArchiveerRegel> _regels = ArchiveerRegels.Load();
    private readonly ListBox _lijst;
    private readonly TextBox _afzender;
    private readonly TextBox _onderwerp;

    /// <param name="voorstelAfzender">Vooringevulde afzender (via "Regel maken van dit bericht").</param>
    /// <param name="voorstelOnderwerp">Vooringevuld onderwerp.</param>
    public ArchiveerRegelsForm(string voorstelAfzender = "", string voorstelOnderwerp = "")
    {
        Text = "Auto-archiveerregels";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var uitleg = new Label
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 8, 12, 0),
            Text = "Mails die aan een regel voldoen worden automatisch gearchiveerd " +
                "en niet in de cockpit getoond.",
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

        // Invoer voor een nieuwe regel.
        var nieuwGroep = new ModernGroupBox
        {
            Text = "Nieuwe regel (minstens één veld invullen)", Dock = DockStyle.Bottom,
            Height = 128, Padding = new Padding(10, 8, 10, 10),
        };
        _afzender = new TextBox
        {
            Text = voorstelAfzender, Location = new Point(140, 28), Width = 370,
            PlaceholderText = "bv. noreply@… of een naam",
        };
        _onderwerp = new TextBox
        {
            Text = voorstelOnderwerp, Location = new Point(140, 60), Width = 336,
            PlaceholderText = "bv. \"dagelijks overzicht\"",
        };
        // Het voorgestelde onderwerp bevat vaak variabele delen (datum, nummer): met één
        // klik leeg te maken zodat de regel alleen op de afzender matcht.
        var wisOnderwerp = new ModernButton
        {
            Text = "✕", Width = 28, Height = _onderwerp.Height,
            Location = new Point(482, 59),
        };
        wisOnderwerp.Click += (_, _) =>
        {
            _onderwerp.Clear();
            _onderwerp.Focus();
        };
        var voegToe = new ModernButton
        {
            Text = "Regel toevoegen", Width = 155, Kind = ButtonKind.Accent, Glyph = Fluent.Add,
            Location = new Point(355, 92),
        };
        voegToe.Click += (_, _) => VoegToe();
        var verwijder = new ModernButton
        {
            Text = "Verwijderen", Width = 120, Glyph = Fluent.Delete, Location = new Point(12, 92),
        };
        verwijder.Click += (_, _) => VerwijderSelectie();
        nieuwGroep.Controls.AddRange(new Control[]
        {
            new Label { Text = "Afzender bevat:", AutoSize = true, Location = new Point(12, 32) },
            _afzender,
            new Label { Text = "Onderwerp bevat:", AutoSize = true, Location = new Point(12, 64) },
            _onderwerp, wisOnderwerp,
            voegToe, verwijder,
        });

        Controls.Add(lijstGroep);
        Controls.Add(nieuwGroep);
        Controls.Add(uitleg);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
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
        var regel = new ArchiveerRegel
        {
            Afzender = _afzender.Text.Trim(),
            Onderwerp = _onderwerp.Text.Trim(),
        };
        if (regel.Afzender.Length == 0 && regel.Onderwerp.Length == 0)
        {
            Toast.Toon(this, "Vul minstens één veld in", Fluent.Edit);
            return;
        }
        _regels.Add(regel);
        ArchiveerRegels.Save(_regels);
        HervulLijst();
        _afzender.Clear();
        _onderwerp.Clear();
        Toast.Toon(this, "Regel toegevoegd", Fluent.Check);
    }

    private void VerwijderSelectie()
    {
        if (_lijst.SelectedIndex is var i && i >= 0 && i < _regels.Count)
        {
            _regels.RemoveAt(i);
            ArchiveerRegels.Save(_regels);
            HervulLijst();
        }
    }
}
