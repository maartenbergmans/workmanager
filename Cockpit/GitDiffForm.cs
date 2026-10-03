namespace WorkManager;

/// <summary>
/// De diff van één bestand, zonder dat je er PhpStorm voor hoeft te openen: toegevoegde
/// regels groen, verwijderde rood, de rest grijs. Bedoeld om vlak voor een commit nog even
/// na te lezen wát je precies aan het committen bent. Een nieuw (untracked) bestand heeft
/// geen diff — dan toont het venster de inhoud zelf.
/// </summary>
public sealed class GitDiffForm : Form
{
    /// <summary>Boven dit aantal regels wordt niet meer per regel ingekleurd (anders duurt het te lang).</summary>
    private const int MaxGekleurd = 3000;

    private readonly string _werkmap;
    private readonly string _pad;
    private readonly RichTextBox _tekst;
    private readonly Label _status;
    private readonly CancellationTokenSource _cts = new();

    public GitDiffForm(string werkmap, string pad, string projectNaam)
    {
        _werkmap = werkmap;
        _pad = pad;
        Text = $"Diff — {projectNaam} · {pad}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(940, 680);
        MinimumSize = new Size(560, 360);

        _tekst = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = Theme.MonoSmall,
            WordWrap = false,
            DetectUrls = false,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
        };

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(10, 7, 10, 0),
            Text = "Diff ophalen…",
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
        var kopieer = new ModernButton { Text = "Kopiëren", Width = 120, Glyph = Fluent.Copy };
        kopieer.Click += (_, _) =>
        {
            if (_tekst.TextLength == 0)
            {
                return;
            }
            Clipboard.SetText(_tekst.Text);
            Toast.Toon(this, "Diff op het klembord", Fluent.Copy);
        };
        var phpStorm = new ModernButton { Text = "Openen in PhpStorm", Width = 180 };
        phpStorm.Click += (_, _) =>
        {
            try
            {
                ClientLauncher.StartPhpStorm(Path.Combine(_werkmap, _pad.Replace('/', '\\')));
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Openen mislukt: {ex.Message}", Fluent.Globe);
            }
        };
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(kopieer);
        knoppen.Controls.Add(phpStorm);
        CancelButton = sluit;

        Controls.Add(_tekst);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "gitdiff");

        Shown += async (_, _) => await LaadAsync();
        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private async Task LaadAsync()
    {
        try
        {
            var diff = await GitStatus.DiffAsync(_werkmap, _pad, _cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            Vul(diff);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            _status.Text = $"Diff mislukt: {ex.Message}";
        }
    }

    private void Vul(string diff)
    {
        var regels = diff.ReplaceLineEndings("\n").Split('\n');
        _tekst.SuspendLayout();
        _tekst.Clear();
        _tekst.Text = string.Join(Environment.NewLine, regels);
        if (regels.Length <= MaxGekleurd)
        {
            var positie = 0;
            foreach (var regel in regels)
            {
                var kleur = Kleur(regel);
                if (kleur is { } c)
                {
                    _tekst.Select(positie, regel.Length);
                    _tekst.SelectionColor = c;
                }
                positie += regel.Length + Environment.NewLine.Length;
            }
        }
        _tekst.Select(0, 0);
        _tekst.ResumeLayout();

        var bij = regels.Count(r => r.StartsWith('+') && !r.StartsWith("+++", StringComparison.Ordinal));
        var weg = regels.Count(r => r.StartsWith('-') && !r.StartsWith("---", StringComparison.Ordinal));
        _status.Text = diff.Trim().Length == 0
            ? "Geen verschil te zien."
            : $"+{bij} / −{weg} regels" +
              (regels.Length > MaxGekleurd ? $" · {regels.Length} regels (te lang om in te kleuren)" : "");
    }

    /// <summary>De kleur van één diffregel, of null voor gewone contextregels.</summary>
    private static Color? Kleur(string regel)
    {
        if (regel.StartsWith("+++", StringComparison.Ordinal) ||
            regel.StartsWith("---", StringComparison.Ordinal) ||
            regel.StartsWith("diff ", StringComparison.Ordinal) ||
            regel.StartsWith("index ", StringComparison.Ordinal) ||
            regel.StartsWith('#'))
        {
            return Theme.Muted;
        }
        if (regel.StartsWith("@@", StringComparison.Ordinal))
        {
            return Theme.Accent;
        }
        return regel.StartsWith('+') ? Theme.Success
            : regel.StartsWith('-') ? Theme.Danger
            : null;
    }
}
