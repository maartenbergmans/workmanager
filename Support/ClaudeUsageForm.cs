namespace WorkManager;

/// <summary>
/// Klein overzicht van het Claude-abonnementsverbruik: per limiet (sessie, week, per model)
/// een balk met het percentage en wanneer hij reset. Zelfde bron als /usage in de CLI.
/// </summary>
public sealed class ClaudeUsageForm : Form
{
    private readonly ModernListView _lijst;
    private readonly Label _status;
    private readonly ModernButton _ververs;
    private readonly CancellationTokenSource _cts = new();

    public ClaudeUsageForm()
    {
        Text = "Claude-verbruik";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 320);
        MinimizeBox = false;

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Verbruik ophalen…",
            LeegGlyph = Fluent.Ster,
        };
        _lijst.Columns.Add("Limiet", 220);
        _lijst.Columns.Add("Gebruik", 220);
        _lijst.Columns.Add("Reset", 150);

        _status = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(12, 6, 12, 0) };
        Theme.AsStatus(_status);

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Height = 50, Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 100 };
        _ververs = new ModernButton { Text = "Verversen", Width = 115, Glyph = Fluent.Sync };
        _ververs.Click += async (_, _) => await LaadAsync();
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_ververs);
        CancelButton = sluit;

        Controls.Add(_lijst);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await LaadAsync();
    }

    private async Task LaadAsync()
    {
        _ververs.Bezig = true;
        _ververs.Enabled = false;
        try
        {
            var limieten = await ClaudeUsage.OphalenAsync(_cts.Token);
            if (IsDisposed)
            {
                return;
            }
            _lijst.BeginUpdate();
            _lijst.Items.Clear();
            foreach (var l in limieten)
            {
                var item = new ListViewItem(l.Naam) { UseItemStyleForSubItems = false };
                var balk = item.SubItems.Add($"{Balk(l.Percent)}  {l.Percent}%");
                balk.Font = Theme.MonoFont;
                balk.ForeColor = KleurVoor(l.Percent);
                item.SubItems.Add(l.ResetTekst);
                _lijst.Items.Add(item);
            }
            _lijst.EndUpdate();
            _status.Text = limieten.Any(l => l.Percent >= 50)
                ? "⚠ Eén of meer limieten boven de helft — grote klussen even doseren."
                : "Ruimte zat.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _ververs.Bezig = false;
            _ververs.Enabled = true;
        }
    }

    /// <summary>Tekstbalkje van tien blokjes (monospace) — leesbaarder dan alleen een getal.</summary>
    private static string Balk(int percent)
    {
        var vol = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        return new string('█', vol) + new string('░', 10 - vol);
    }

    private static Color KleurVoor(int percent) => percent switch
    {
        >= 80 => Theme.Danger,
        >= 50 => Theme.Warn,
        _ => Theme.Success,
    };
}
