namespace WorkManager;

/// <summary>
/// Snel notitievenster (Ctrl+N in de cockpit): een kladblok dat automatisch bewaart in
/// %APPDATA%\WorkManager\scratchpad.txt, met een knop om van de selectie (of de eerste regel)
/// meteen een taak in "Mijn taken" te maken. Sluiten = verbergen; de tekst blijft staan.
/// </summary>
public sealed class ScratchpadForm : Form
{
    private static ScratchpadForm? _instantie;

    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "scratchpad.txt");

    private readonly TextBox _tekst;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };

    /// <summary>Toont het (gedeelde) scratchpad; bestaat het al, dan komt het naar voren.</summary>
    public static void Toon(Form eigenaar)
    {
        if (_instantie is { IsDisposed: false })
        {
            _instantie.Show();
            _instantie.BringToFront();
            _instantie.Activate();
            return;
        }
        _instantie = new ScratchpadForm();
        _instantie.Show(eigenaar);
    }

    private ScratchpadForm()
    {
        Text = "Kladblok (autosave)";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(430, 420);
        // Rechtsboven op het werkscherm, uit de weg van de lijsten.
        var scherm = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(scherm.Right - Width - 40, scherm.Top + 60);
        ShowInTaskbar = false;

        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.BaseFont,
        };
        try
        {
            if (File.Exists(Bestand))
            {
                _tekst.Text = File.ReadAllText(Bestand);
                _tekst.SelectionStart = _tekst.TextLength;
            }
        }
        catch
        {
            // Leeg beginnen.
        }
        _tekst.TextChanged += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start(); // pas bewaren als het typen even stilligt
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Bewaar();
        };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48,
            Padding = new Padding(8),
        };
        var taakKnop = new ModernButton
        {
            Text = "Taak maken", Width = 130, Kind = ButtonKind.Accent, Glyph = Fluent.Checkbox,
        };
        taakKnop.Click += (_, _) => MaakTaak();
        var wisKnop = new ModernButton { Text = "Leegmaken", Width = 115 };
        wisKnop.Click += (_, _) =>
        {
            _tekst.Clear();
            Bewaar();
        };
        knoppen.Controls.Add(taakKnop);
        knoppen.Controls.Add(wisKnop);

        Controls.Add(_tekst);
        Controls.Add(knoppen);
        // Sluiten bewaart en verbergt alleen (Ctrl+N haalt hem zo weer tevoorschijn).
        FormClosing += (_, e) =>
        {
            Bewaar();
            e.Cancel = true;
            Hide();
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        Theme.Apply(this);
        VensterGeheugen.Volg(this, "kladblok");
    }

    private void Bewaar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, _tekst.Text);
        }
        catch
        {
            // Best effort; de tekst staat nog in het venster.
        }
    }

    /// <summary>Maakt van de selectie (of anders de eerste niet-lege regel) een taak in "Mijn taken".</summary>
    private void MaakTaak()
    {
        var tekst = _tekst.SelectedText.Trim();
        if (tekst.Length == 0)
        {
            tekst = _tekst.Lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        }
        if (tekst.Length == 0)
        {
            Toast.Toon(this, "Niets om een taak van te maken", Fluent.Checkbox);
            return;
        }
        if (tekst.Length > 200)
        {
            tekst = tekst[..200];
        }
        var data = MijnTaakStore.Load();
        data.Taken.Add(new MijnTaak
        {
            Tekst = tekst,
            Categorie = "Werk",
            Prioriteit = 1,
        });
        MijnTaakStore.Save(data);
        Toast.Toon(this, $"Taak toegevoegd: {tekst[..Math.Min(40, tekst.Length)]}", Fluent.Check);
    }
}
