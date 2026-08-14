using System.Runtime.InteropServices;

namespace WorkManager;

/// <summary>
/// Minimalistische, altijd-bovenop focusbalk: één strookje bovenaan het scherm met alleen wat
/// je nú doet volgens de dagplanning, een vinkje en een overslaan-knop. Cockpit dicht, focus
/// aan. Slepen mag (vastpakken op de tekst); nogmaals de Focus-knop of ✕ sluit hem weer.
/// </summary>
public sealed class FocusBarForm : Form
{
    private static FocusBarForm? _open;

    private readonly Label _tekst;
    private readonly ModernButton _klaarKnop;
    private readonly System.Windows.Forms.Timer _ververs = new() { Interval = 30_000 };

    /// <summary>Toont de balk, of sluit hem als hij al open staat (toggle).</summary>
    public static void Toggle()
    {
        if (_open is { IsDisposed: false })
        {
            _open.Close();
            return;
        }
        _open = new FocusBarForm();
        _open.Show();
    }

    /// <summary>Staat de balk momenteel open?</summary>
    public static bool Actief => _open is { IsDisposed: false };

    private FocusBarForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Height = 46;
        Width = 640;
        BackColor = Theme.Surface;
        var scherm = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
        Location = new Point(scherm.X + (scherm.Width - Width) / 2, scherm.Y + 6);

        _tekst = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 8, 0),
            Font = Theme.SemiBold,
            ForeColor = Theme.Text,
            Cursor = Cursors.SizeAll,
        };
        // Slepen: de standaard versleep-truc (alsof je de titelbalk vastpakt).
        _tekst.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
            }
        };

        _klaarKnop = new ModernButton
        {
            Text = "✓", Width = 44, Dock = DockStyle.Right, Kind = ButtonKind.Accent,
        };
        _klaarKnop.Click += (_, _) => HandelAf(klaar: true);
        var skipKnop = new ModernButton { Text = "⏭", Width = 44, Dock = DockStyle.Right };
        skipKnop.Click += (_, _) => HandelAf(klaar: false);
        var sluitKnop = new ModernButton { Text = "✕", Width = 38, Dock = DockStyle.Right };
        sluitKnop.Click += (_, _) => Close();

        var knoppen = new Panel { Dock = DockStyle.Right, Width = 138, Padding = new Padding(3, 6, 6, 6) };
        knoppen.Controls.Add(sluitKnop);
        knoppen.Controls.Add(skipKnop);
        knoppen.Controls.Add(_klaarKnop);

        Controls.Add(_tekst);
        Controls.Add(knoppen);

        // Dun accentrandje zodat de balk zich aftekent tegen lichte vensters eronder.
        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Accent, 2);
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
        };

        _ververs.Tick += (_, _) => Vernieuw();
        FormClosed += (_, _) =>
        {
            _ververs.Stop();
            _ververs.Dispose();
            _open = null;
        };
        Shown += (_, _) =>
        {
            Vernieuw();
            _ververs.Start();
        };
    }

    /// <summary>Het eerstvolgende vrije planitem (afspraken vink je niet af).</summary>
    private static PlanItem? Volgende() =>
        DagPlan.LaadVandaag() is { } plan
            ? DagPlan.Tijdlijn(plan).FirstOrDefault(r => !r.Item.VastBlok).Item
            : null;

    private void Vernieuw()
    {
        if (IsDisposed)
        {
            return;
        }
        if (DagPlan.LaadVandaag() is not { } plan)
        {
            _tekst.Text = "Geen dagplanning — maak er één via de cockpit";
            _klaarKnop.Enabled = false;
            return;
        }
        var item = Volgende();
        if (item is null)
        {
            _tekst.Text = "Alles afgewerkt 🎉";
            _klaarKnop.Enabled = false;
            return;
        }
        _klaarKnop.Enabled = true;
        var duur = item.Minuten >= 60 ? $"{item.Minuten / 60}u{item.Minuten % 60:00}" : $"{item.Minuten} min";
        // Komt er binnen twee uur een afspraak, dan telt de balk ernaartoe af.
        var afspraak = DagPlan.Tijdlijn(plan)
            .FirstOrDefault(r => r.Item is { Soort: "afspraak", VastStart: not null } &&
                                 r.Item.VastStart > DateTimeOffset.Now).Item;
        var staart = "";
        if (afspraak?.VastStart is { } begin && begin - DateTimeOffset.Now < TimeSpan.FromHours(2))
        {
            staart = $"   ·   📅 over {(int)(begin - DateTimeOffset.Now).TotalMinutes} min";
        }
        _tekst.Text = $"▶ {Kort(item.Tekst, 55)}  (~{duur}){staart}";
    }

    private void HandelAf(bool klaar)
    {
        if (DagPlan.LaadVandaag() is not { } plan || Volgende() is not { } item)
        {
            return;
        }
        var echte = plan.Items.FirstOrDefault(i => i.Id == item.Id);
        if (echte is null)
        {
            return;
        }
        echte.Klaar = klaar;
        echte.Overgeslagen = !klaar;
        if (klaar && echte.TaakId is { } id)
        {
            var data = MijnTaakStore.Load();
            if (data.Taken.FirstOrDefault(t => t.Id == id) is { } taak && !taak.Klaar)
            {
                taak.Klaar = true;
                taak.KlaarOp = DateTimeOffset.Now;
                MijnTaakStore.Save(data);
            }
        }
        DagPlan.Bewaar(plan);
        Vernieuw();
        if (klaar && Volgende() is null)
        {
            Confetti.Vier(this);
        }
    }

    private static string Kort(string tekst, int max) =>
        tekst.Length <= max ? tekst : tekst[..max] + "…";

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
