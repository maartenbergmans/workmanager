namespace WorkManager;

/// <summary>
/// Committen zonder naar de terminal te gaan: vink de bestanden aan, typ een bericht en
/// commit — eventueel meteen met een push erachter. Dubbelklik op een regel toont de diff,
/// zodat je nog kunt nalezen wat er precies in de commit belandt.
///
/// <para>Nieuwe (untracked) bestanden staan bewust uitgevinkt: dat zijn vaker logs, dumps en
/// kladbestanden dan werk dat de repo in moet. Alles wat git al kent, staat aangevinkt.</para>
/// </summary>
public sealed class GitCommitForm : Form
{
    private readonly string _werkmap;
    private readonly string _projectNaam;
    private readonly ModernListView _lijst;
    private readonly TextBox _bericht;
    private readonly Label _status;
    private readonly ModernButton _commitKnop;
    private readonly ModernButton _pushKnop;
    private readonly CancellationTokenSource _cts = new();
    private string _branch = "";

    public GitCommitForm(string werkmap, string projectNaam)
    {
        _werkmap = werkmap;
        _projectNaam = projectNaam;
        Text = $"Committen — {projectNaam}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(880, 640);
        MinimumSize = new Size(620, 460);

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LegeTekst = "Niets te committen 🎉",
            LeegGlyph = Fluent.Check,
        };
        _lijst.Columns.Add("Status", 150);
        _lijst.Columns.Add("Bestand", 470);
        _lijst.Columns.Add("Ligt er", 90);
        _lijst.DoubleClick += (_, _) => ToonDiff();

        _bericht = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "Commitbericht — eerste regel kort, uitleg eronder",
        };
        // Ctrl+Enter is in elk commitvenster de snelste weg naar "doe het maar".
        _bericht.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _commitKnop.PerformClick();
            }
        };
        var berichtVak = new ModernGroupBox { Dock = DockStyle.Bottom, Height = 120, Text = "Commitbericht" };
        berichtVak.Controls.Add(_bericht);

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Wijzigingen ophalen…",
        };
        Theme.AsStatus(_status);

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 100 };
        _commitKnop = new ModernButton { Text = "Committen", Width = 130, Kind = ButtonKind.Accent };
        _commitKnop.Click += async (_, _) => await CommitAsync(pushen: false);
        _pushKnop = new ModernButton { Text = "Committen + pushen", Width = 190 };
        _pushKnop.Click += async (_, _) => await CommitAsync(pushen: true);
        var diffKnop = new ModernButton { Text = "Diff…", Width = 100 };
        diffKnop.Click += (_, _) => ToonDiff();
        var allesKnop = new ModernButton { Text = "Alles aan/uit", Width = 140 };
        allesKnop.Click += (_, _) =>
        {
            var aan = _lijst.CheckedItems.Count < _lijst.Items.Count;
            foreach (ListViewItem item in _lijst.Items)
            {
                item.Checked = aan;
            }
        };
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_commitKnop);
        knoppen.Controls.Add(_pushKnop);
        knoppen.Controls.Add(diffKnop);
        knoppen.Controls.Add(allesKnop);
        CancelButton = sluit;

        Controls.Add(_lijst);
        Controls.Add(_status);
        Controls.Add(berichtVak);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "gitcommit");

        _lijst.ItemChecked += (_, _) => WerkStatusBij();
        Shown += async (_, _) => await LaadAsync();
        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private async Task LaadAsync()
    {
        _status.Text = "Wijzigingen ophalen…";
        try
        {
            // Geen fetch: hier gaat het om wat er lokaal openstaat, niet om de remote-stand.
            var rapport = await GitStatus.OphalenAsync(_werkmap, _cts.Token, fetchen: false);
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            _branch = rapport.Branch;
            _lijst.BeginUpdate();
            _lijst.Items.Clear();
            foreach (var w in rapport.Wijzigingen
                         .OrderByDescending(w => w.Gestaged)
                         .ThenBy(w => w.Pad, StringComparer.OrdinalIgnoreCase))
            {
                var dagen = GitRadar.DagenOud(_werkmap, w.Pad);
                var item = new ListViewItem(w.Omschrijving + (w.Gestaged ? " · staged" : ""))
                {
                    Tag = w,
                    // Untracked staat uit (zie de klassendoc); de rest aan.
                    Checked = w.Code.Trim() != "??",
                };
                item.SubItems.Add(w.Pad);
                item.SubItems.Add(dagen < 0 ? "" : dagen == 0 ? "vandaag" : $"{dagen} d");
                _lijst.Items.Add(item);
            }
            _lijst.EndUpdate();
            if (rapport.Fout is { } fout)
            {
                _status.Text = $"Geen git-status: {fout}";
                _commitKnop.Enabled = false;
                _pushKnop.Enabled = false;
                return;
            }
            WerkStatusBij();
            if (_lijst.Items.Count > 0)
            {
                _bericht.Focus();
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
    }

    private void WerkStatusBij()
    {
        var aan = _lijst.CheckedItems.Count;
        var totaal = _lijst.Items.Count;
        _status.Text = totaal == 0
            ? $"Branch {_branch}: niets te committen"
            : $"Branch {_branch}: {aan} van {totaal} bestand(en) aangevinkt";
        _commitKnop.Enabled = aan > 0;
        _pushKnop.Enabled = aan > 0;
    }

    private void ToonDiff()
    {
        if (_lijst.SelectedItems.Count == 0 || _lijst.SelectedItems[0].Tag is not GitStatus.Wijziging w)
        {
            Toast.Toon(this, "Kies eerst een bestand", Fluent.Document);
            return;
        }
        using var diff = new GitDiffForm(_werkmap, w.Pad, _projectNaam);
        diff.ShowDialog(this);
    }

    private async Task CommitAsync(bool pushen)
    {
        var paden = _lijst.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as GitStatus.Wijziging)
            .Where(w => w is not null)
            .Select(w => w!.Pad)
            .ToList();
        if (paden.Count == 0)
        {
            Toast.Toon(this, "Niets aangevinkt", Fluent.Checkbox);
            return;
        }
        if (_bericht.Text.Trim().Length == 0)
        {
            Toast.Toon(this, "Typ eerst een commitbericht", Fluent.Edit);
            _bericht.Focus();
            return;
        }

        _commitKnop.Bezig = true;
        _commitKnop.Enabled = false;
        _pushKnop.Enabled = false;
        _status.Text = pushen ? "Committen en pushen…" : "Committen…";
        try
        {
            var (ok, melding) = await GitStatus.CommitAsync(
                _werkmap, paden, _bericht.Text, pushen, _cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            _status.Text = melding;
            if (ok)
            {
                _bericht.Clear();
                Toast.Toon(this, melding, Fluent.Check);
                // De radar (en dus ook het overzicht en de online tabel) meteen bijwerken.
                await GitRadar.VerversAsync(_werkmap, _cts.Token);
                await LaadAsync();
            }
            else
            {
                Toast.Fout(this, "Commit niet gelukt", melding);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het committen.
        }
        catch (Exception ex)
        {
            _status.Text = $"Commit mislukt: {ex.Message}";
            Toast.Fout(this, "Commit mislukt", ex.Message);
        }
        finally
        {
            _commitKnop.Bezig = false;
            WerkStatusBij();
        }
    }
}
