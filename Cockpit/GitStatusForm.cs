namespace WorkManager;

/// <summary>
/// Toont wat er in een projectmap nog ongecommit is: hoeveel bestanden, welke, hoe lang ze al
/// openstaan, en of de branch voor- of achterloopt op de remote. Dubbelklik toont de diff van
/// het bestand; "Committen…" zet de wijzigingen in een commit (eventueel meteen met push) en
/// met "Kopiëren" gaat de hele lijst naar het klembord.
/// </summary>
public class GitStatusForm : Form
{
    private readonly string _werkmap;
    private readonly string _projectNaam;
    private readonly ModernListView _lijst;
    private readonly Label _status;
    private readonly ModernButton _verversKnop;
    private readonly CancellationTokenSource _cts = new();

    public GitStatusForm(string werkmap, string projectNaam)
    {
        _werkmap = werkmap;
        _projectNaam = projectNaam;
        Text = $"Git-status — {projectNaam}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 520);
        MinimizeBox = false;

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Niets ongecommit 🎉",
            LeegGlyph = Fluent.Check,
        };
        _lijst.Columns.Add("Status", 150);
        _lijst.Columns.Add("Bestand", 430);
        _lijst.Columns.Add("Ligt er", 90);
        _lijst.DoubleClick += (_, _) => ToonDiff();

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Git-status ophalen…",
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
        _verversKnop = new ModernButton { Text = "Verversen", Width = 115, Glyph = Fluent.Sync };
        _verversKnop.Click += async (_, _) => await LaadAsync();
        var kopieer = new ModernButton { Text = "Kopiëren", Width = 115 };
        kopieer.Click += (_, _) => KopieerLijst();
        var commit = new ModernButton { Text = "Committen…", Width = 140, Kind = ButtonKind.Accent };
        commit.Click += async (_, _) =>
        {
            using (var form = new GitCommitForm(_werkmap, _projectNaam))
            {
                form.ShowDialog(this);
            }
            await LaadAsync();
        };
        var diff = new ModernButton { Text = "Diff…", Width = 100 };
        diff.Click += (_, _) => ToonDiff();
        var phpStorm = new ModernButton { Text = "Openen in PhpStorm", Width = 175 };
        phpStorm.Click += (_, _) =>
        {
            try
            {
                // Met een bestand geselecteerd opent PhpStorm meteen dát bestand; anders het
                // project. Het pad uit git is relatief aan de repo-root.
                ClientLauncher.StartPhpStorm(
                    _lijst.SelectedItems.Count > 0 &&
                    _lijst.SelectedItems[0].Tag is GitStatus.Wijziging gekozen
                        ? Path.Combine(_werkmap, gekozen.Pad.Replace('/', '\\'))
                        : _werkmap);
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Openen mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_verversKnop);
        knoppen.Controls.Add(commit);
        knoppen.Controls.Add(diff);
        knoppen.Controls.Add(kopieer);
        knoppen.Controls.Add(phpStorm);
        CancelButton = sluit;

        Controls.Add(_lijst);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "gitstatus");

        Shown += async (_, _) => await LaadAsync();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private async Task LaadAsync()
    {
        _verversKnop.Bezig = true;
        _verversKnop.Enabled = false;
        _status.Text = "Git-status ophalen…";
        try
        {
            var rapport = await GitStatus.OphalenAsync(_werkmap, _cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            Vul(rapport);
        }
        finally
        {
            _verversKnop.Bezig = false;
            _verversKnop.Enabled = true;
        }
    }

    private void Vul(GitStatus.Rapport rapport)
    {
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        // Gestagede wijzigingen eerst (die zitten al in de index), daarna de rest op pad.
        foreach (var w in rapport.Wijzigingen
                     .OrderByDescending(w => w.Gestaged)
                     .ThenBy(w => w.Pad, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(w.Omschrijving + (w.Gestaged ? " · staged" : ""))
            {
                Tag = w,
            };
            item.SubItems.Add(w.Pad);
            var dagen = GitRadar.DagenOud(_werkmap, w.Pad);
            item.SubItems.Add(dagen < 0 ? "" : dagen == 0 ? "vandaag" : $"{dagen} d");
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();

        if (rapport.Fout is { } fout)
        {
            _status.Text = $"Geen git-status: {fout}";
            return;
        }
        var sync = rapport.Sync.Length > 0 ? $" · {rapport.Sync} op de remote" : "";
        _status.Text = rapport.Aantal == 0
            ? $"Branch {rapport.Branch}: alles gecommit{sync}"
            : $"Branch {rapport.Branch}: {rapport.Aantal} ongecommit " +
              $"({rapport.Wijzigingen.Count(w => w.Gestaged)} staged){sync}";
    }

    /// <summary>De diff van het gekozen bestand — sneller dan er PhpStorm voor openen.</summary>
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

    private void KopieerLijst()
    {
        if (_lijst.Items.Count == 0)
        {
            Toast.Toon(this, "Niets te kopiëren", Fluent.Copy);
            return;
        }
        var tekst = string.Join(Environment.NewLine, _lijst.Items.Cast<ListViewItem>()
            .Select(i => $"{i.SubItems[1].Text}  ({i.Text})"));
        Clipboard.SetText(tekst);
        Toast.Toon(this, $"{_lijst.Items.Count} regels gekopieerd", Fluent.Copy);
    }
}
