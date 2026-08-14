namespace WorkManager;

/// <summary>
/// Dialoog die met Claude teamtaken haalt uit ruwe input en over de teamleden verdeelt:
/// bovenaan de invoer, daaronder de voorstellen met vinkjes ter controle. Per voorstel kan
/// het teamlid nog gewisseld worden via het contextmenu. De aangevinkte voorstellen komen
/// via <see cref="Gekozen"/> terug bij de aanroeper.
/// </summary>
public class TeamUitTekstForm : Form
{
    private readonly List<string> _leden;
    private readonly string _standaardLid;
    private readonly TextBox _invoer;
    private readonly ModernButton _genereerButton;
    private readonly ModernButton _okButton;
    private readonly ModernListView _lijst;
    private readonly PulseBar _pulse = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    public List<ClaudeTeamTaken.Voorstel> Gekozen { get; } = new();

    public TeamUitTekstForm(List<string> leden, string standaardLid)
    {
        _leden = leden;
        _standaardLid = standaardLid;

        Text = "Teamtaken uit tekst (Claude)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 700);
        MinimizeBox = false;

        // Invoerkaart bovenaan
        var invoerGroup = new ModernGroupBox
        {
            Text = "Ruwe input (notities, mail, verslag, opsomming…)",
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
            Text = "Claude splitst dit in korte taken en wijst ze toe aan " +
                   $"het genoemde teamlid (anders aan {standaardLid}).",
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
            LeegGlyph = Fluent.People,
        };
        _lijst.Columns.Add("Taak", 550);
        _lijst.Columns.Add("Teamlid", 170);
        _lijst.Columns.Add("Prio", 70);
        _lijst.Resize += (_, _) => _lijst.Columns[0].Width = Math.Max(300,
            _lijst.ClientSize.Width - _lijst.Columns[1].Width - _lijst.Columns[2].Width - 4);
        _lijst.SterrenKolom = 2;
        _lijst.SterGeklikt += (item, aantal) =>
        {
            if (item.Tag is ClaudeTeamTaken.Voorstel voorstel)
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

        // Rechtsklik: taak aan een ander teamlid toewijzen.
        var lidMenu = new ContextMenuStrip();
        Theme.Style(lidMenu);
        lidMenu.Opening += (_, e) =>
        {
            if (_lijst.SelectedItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            lidMenu.Items.Clear();
            foreach (var lid in _leden)
            {
                var doel = new ToolStripMenuItem($"Toewijzen aan {lid}");
                doel.Click += (_, _) => SelectieToewijzen(lid);
                lidMenu.Items.Add(doel);
            }
        };
        _lijst.ContextMenuStrip = lidMenu;

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
            MessageBox.Show(this, "Plak of typ eerst wat ruwe input.", "Teamtaken uit tekst",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _genereerButton.Enabled = false;
        _genereerButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            var voorstellen = await ClaudeTeamTaken.GenereerAsync(tekst, _leden, _standaardLid, _cts.Token);
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

    private void VulLijst(List<ClaudeTeamTaken.Voorstel> voorstellen)
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
            item.SubItems.Add(voorstel.Lid).ForeColor = Theme.AccentHover;
            var prio = item.SubItems.Add("");
            (prio.Text, prio.ForeColor) = Theme.PrioSterren(voorstel.Prioriteit);
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
        _loading = false;
        UpdateOkKnop();
    }

    private void SelectieToewijzen(string lid)
    {
        foreach (ListViewItem item in _lijst.SelectedItems)
        {
            if (item.Tag is ClaudeTeamTaken.Voorstel voorstel)
            {
                item.Tag = voorstel with { Lid = lid };
                item.SubItems[1].Text = lid;
            }
        }
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
            .Select(i => i.Tag).OfType<ClaudeTeamTaken.Voorstel>());
        if (Gekozen.Count == 0)
        {
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
