namespace WorkManager;

/// <summary>
/// Dialoog die met Claude taken haalt uit ruwe input (notities, mail, braindump):
/// bovenaan de invoer, daaronder de voorgestelde taken met vinkjes ter controle.
/// De aangevinkte voorstellen komen via <see cref="Gekozen"/> terug bij de aanroeper.
/// </summary>
public class TakenUitTekstForm : Form
{
    private readonly List<string> _categorieen;
    private readonly TextBox _invoer;
    private readonly ModernButton _genereerButton;
    private readonly ModernButton _okButton;
    private readonly ModernListView _lijst;
    private readonly PulseBar _pulse = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    public List<ClaudeTaken.Voorstel> Gekozen { get; } = new();

    public TakenUitTekstForm(List<string> categorieen)
    {
        _categorieen = categorieen;

        Text = "Taken uit tekst (Claude)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 700);
        MinimizeBox = false;

        // Invoerkaart bovenaan
        var invoerGroup = new ModernGroupBox
        {
            Text = "Ruwe input (notities, mail, braindump, verslag…)",
            Dock = DockStyle.Top,
            Height = 240,
            Padding = new Padding(10, 8, 10, 10),
        };
        _invoer = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        _genereerButton = new ModernButton
        {
            Text = "Taken voorstellen", Width = 170, Kind = ButtonKind.Accent, Glyph = Fluent.Ster,
            Dock = DockStyle.Right,
        };
        _genereerButton.Click += async (_, _) => await GenereerAsync();
        var knopRij = new Panel { Dock = DockStyle.Bottom, Height = 39, Padding = new Padding(0, 8, 0, 0) };
        knopRij.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Claude splitst dit in korte taken met categorie, prioriteit en eventuele deadline.",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
        });
        knopRij.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        knopRij.Controls.Add(_genereerButton);
        invoerGroup.Controls.Add(_invoer);
        invoerGroup.Controls.Add(knopRij);

        // Voorstellenlijst
        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LegeTekst = "Nog geen voorstellen — plak hierboven tekst en klik 'Taken voorstellen'.",
            LeegGlyph = Fluent.Ster,
        };
        _lijst.Columns.Add("Taak", 470);
        _lijst.Columns.Add("Categorie", 150);
        _lijst.Columns.Add("Prio", 60);
        _lijst.Columns.Add("Deadline", 110);
        _lijst.Resize += (_, _) => _lijst.Columns[0].Width = Math.Max(250,
            _lijst.ClientSize.Width - _lijst.Columns[1].Width - _lijst.Columns[2].Width -
            _lijst.Columns[3].Width - 4);
        _lijst.SterrenKolom = 2;
        _lijst.SterGeklikt += (item, aantal) =>
        {
            if (item.Tag is ClaudeTaken.Voorstel voorstel)
            {
                var prio = 3 - aantal;
                item.Tag = voorstel with { Prioriteit = prio };
                var sub = item.SubItems[2];
                (sub.Text, sub.ForeColor) = Theme.PrioSterren(prio);
            }
        };
        _lijst.ItemChecked += (_, _) =>
        {
            if (!_loading)
            {
                UpdateOkKnop();
            }
        };

        // Knoppen onderaan
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        _okButton = new ModernButton
        {
            Text = "Toevoegen", Width = 170, Kind = ButtonKind.Accent, Glyph = Fluent.Add, Enabled = false,
        };
        _okButton.Click += (_, _) => Bevestig();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_okButton);
        CancelButton = cancel;

        Controls.Add(_lijst);
        Controls.Add(_pulse);
        Controls.Add(invoerGroup);
        Controls.Add(buttons);

        FormClosed += (_, _) => _cts.Cancel();
        Theme.Apply(this);
    }

    private async Task GenereerAsync()
    {
        var tekst = _invoer.Text.Trim();
        if (tekst.Length == 0)
        {
            MessageBox.Show(this, "Plak of typ eerst wat ruwe input.", "Taken uit tekst",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _genereerButton.Enabled = false;
        _genereerButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            var voorstellen = await ClaudeTaken.GenereerAsync(tekst, _categorieen, _cts.Token);
            VulLijst(voorstellen);
            if (voorstellen.Count == 0)
            {
                Toast.Toon(this, "Claude vond geen concrete taken in de tekst", Fluent.Ster);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het genereren.
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Taken voorstellen mislukt", ex.Message);
        }
        finally
        {
            _genereerButton.Enabled = true;
            _genereerButton.Bezig = false;
            _pulse.Actief = false;
        }
    }

    private void VulLijst(List<ClaudeTaken.Voorstel> voorstellen)
    {
        _loading = true;
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var voorstel in voorstellen)
        {
            var item = new ListViewItem(voorstel.Tekst)
            {
                Tag = voorstel, Checked = true, UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(voorstel.Categorie).ForeColor = Theme.Muted;
            var prio = item.SubItems.Add("");
            (prio.Text, prio.ForeColor) = Theme.PrioSterren(voorstel.Prioriteit);
            var deadline = item.SubItems.Add(
                voorstel.Deadline is { } d ? d.ToString("ddd d MMM") : "");
            deadline.ForeColor = Theme.Muted;
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
        _loading = false;
        UpdateOkKnop();
    }

    private void UpdateOkKnop()
    {
        var aantal = _lijst.CheckedItems.Count;
        _okButton.Enabled = aantal > 0;
        _okButton.Text = aantal switch
        {
            0 => "Toevoegen",
            1 => "1 taak toevoegen",
            _ => $"{aantal} taken toevoegen",
        };
    }

    private void Bevestig()
    {
        Gekozen.Clear();
        Gekozen.AddRange(_lijst.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<ClaudeTaken.Voorstel>());
        if (Gekozen.Count == 0)
        {
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
