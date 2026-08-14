namespace WorkManager;

/// <summary>
/// Receptkaart in HelloFresh-stijl: grote gerechtfoto bovenaan, daaronder links de
/// ingrediënten (met productfoto en aantal) en rechts de genummerde bereidingsstappen.
/// Puur om te bekijken — bestellen en plannen gebeurt in de bestelflow zelf.
/// </summary>
public class AhReceptKaartForm : Form
{
    public AhReceptKaartForm(
        string naam, List<AhIngredient> ingredienten, Recept? recept, string extraInfo = "")
    {
        Text = $"Recept – {naam}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 760);
        MinimumSize = new Size(620, 520);
        MinimizeBox = false;

        // Grote foto bovenaan; wordt asynchroon geladen (Allerhande, terugval productfoto).
        var foto = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 280,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Bg,
        };
        _ = LaadFotoAsync(foto, naam);

        var titel = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(14, 8, 14, 0),
            Text = naam,
            Font = new Font("Segoe UI Semibold", 15f),
        };
        var subtitel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(14, 0, 14, 0),
            Text = string.Join("  ·  ", new[]
            {
                recept?.Minuten is > 0 and var m ? $"⏱ {m} min" : null,
                $"voor {Math.Max(1, recept?.Personen ?? 4)} personen",
                $"{ingredienten.Count} ingrediënten",
                extraInfo.Length > 0 ? extraInfo : null,
            }.OfType<string>()),
        };

        // Links de ingrediënten, rechts de stappen.
        var lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.None,
            HeeftCheckbox = _ => false,
            RijHoogte = 46,
            IcoonGrootte = 36,
            RijIcoon = rij => rij.Tag is AhIngredient ing ? AhAfbeeldingen.Voor(ing.Url) : null,
            LegeTekst = "Geen ingrediënten bekend.",
            LeegGlyph = Fluent.Lijst,
        };
        lijst.Columns.Add("", 250);
        lijst.Columns.Add("", 44, HorizontalAlignment.Right);
        lijst.Resize += (_, _) => lijst.Columns[0].Width =
            Math.Max(140, lijst.ClientSize.Width - lijst.Columns[1].Width - 4);
        foreach (var ing in ingredienten)
        {
            lijst.Items.Add(new ListViewItem(new[]
            {
                ing.Naam, ing.Aantal > 1 ? $"{ing.Aantal}×" : "",
            })
            {
                Tag = ing,
            });
        }
        AhAfbeeldingen.BeeldKlaar += OpBeeldKlaar;
        FormClosed += (_, _) => AhAfbeeldingen.BeeldKlaar -= OpBeeldKlaar;
        AhAfbeeldingen.Voorladen(ingredienten.Select(i => i.Url));
        _lijst = lijst;

        var stappen = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 10.5f),
            Text = StappenTekst(recept),
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 320,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 220,
        };
        split.Panel1.Padding = new Padding(8, 6, 4, 6);
        split.Panel2.Padding = new Padding(10, 6, 12, 6);
        split.Panel1.Controls.Add(lijst);
        split.Panel2.Controls.Add(stappen);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 110 };
        buttons.Controls.Add(sluit);
        CancelButton = sluit;

        Controls.Add(split);
        Controls.Add(subtitel);
        Controls.Add(titel);
        Controls.Add(foto);
        Controls.Add(buttons);
        Theme.Apply(this);
        subtitel.ForeColor = Theme.Muted;
        stappen.BackColor = Theme.Bg;
        stappen.ForeColor = Theme.Text;
        // Ná Theme.Apply: die zet zijn eigen lettertype, en de splitterafstand rekent pas
        // goed met een levend handle.
        stappen.Font = new Font("Segoe UI", 10.5f);
        Shown += (_, _) => split.SplitterDistance = 320;
    }

    private readonly ModernListView _lijst;

    /// <summary>De recepttekst als genummerde stappen; zonder recept een vriendelijke hint.</summary>
    private static string StappenTekst(Recept? recept)
    {
        var regels = (recept?.Tekst ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (regels.Length == 0)
        {
            return "Nog geen recept — gebruik \"Recept voorstellen\" in de ingrediëntbewerker.";
        }
        // Eén doorlopende tekst zonder \n wordt op zinnen geknipt, zodat er toch stappen staan.
        if (regels.Length == 1 && regels[0].Length > 120)
        {
            regels = regels[0]
                .Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(z => z.EndsWith('.') ? z : z + ".")
                .ToArray();
        }
        return string.Join("\r\n\r\n", regels.Select((r, i) => $"{i + 1}.  {r}"));
    }

    private async Task LaadFotoAsync(PictureBox doel, string naam)
    {
        try
        {
            var beeld = await GerechtFoto.GrootAsync(naam);
            if (beeld is not null && !IsDisposed && !doel.IsDisposed)
            {
                doel.Image = beeld;
            }
        }
        catch
        {
            // Geen foto: de kaart werkt ook zonder.
        }
    }

    private void OpBeeldKlaar()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() => _lijst.Invalidate());
            }
        }
        catch (InvalidOperationException)
        {
            // Venster net gesloten.
        }
    }
}
