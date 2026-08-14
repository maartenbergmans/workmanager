using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Stille vervanger van de Windows-ballonmeldingen (die spelen altijd het systeemgeluid,
/// en dat is via NotifyIcon niet uit te schakelen): een klein donker venster rechtsonder
/// op het scherm dat vanzelf verdwijnt. Steelt geen focus; klikken voert de meegegeven
/// actie uit (zoals BalloonTipClicked dat deed), het kruisje sluit alleen.
/// </summary>
public sealed class TrayMelding : Form
{
    private static TrayMelding? _huidig; // één melding tegelijk, zoals de balloon

    private readonly string _titel;
    private readonly Action? _onKlik;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private int _restMs;
    private Rectangle _sluitRect;

    public static void Toon(string titel, string tekst, Action? onKlik = null, int duurMs = 8000)
    {
        _huidig?.Sluit();
        _huidig = new TrayMelding(titel, tekst, onKlik, duurMs);
        _huidig.Show();
    }

    private TrayMelding(string titel, string tekst, Action? onKlik, int duurMs)
    {
        _titel = titel;
        _onKlik = onKlik;
        _restMs = Math.Max(2000, duurMs);
        Text = tekst;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Card;
        Font = Theme.BaseFont;
        Cursor = onKlik is not null ? Cursors.Hand : Cursors.Default;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        // Formaat op basis van de (gewrapte) tekst; ballonnen konden meerdere regels aan.
        const int breedte = 380;
        var tekstMaat = TextRenderer.MeasureText(tekst, Theme.BaseFont,
            new Size(breedte - 56, 400), TextFormatFlags.WordBreak);
        Size = new Size(breedte, Math.Min(260, 52 + tekstMaat.Height + 14));
        var werk = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(werk.Right - Width - 16, werk.Bottom - Height - 16);

        _timer.Tick += (_, _) =>
        {
            _restMs -= _timer.Interval;
            if (_restMs <= 0)
            {
                Sluit();
            }
        };
        _timer.Start();
    }

    // Geen focus stelen: de melding mag nooit het typwerk van dat moment onderbreken.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_sluitRect.Contains(e.Location))
        {
            Sluit();
            return;
        }
        var actie = _onKlik;
        Sluit();
        actie?.Invoke();
    }

    private void Sluit()
    {
        _timer.Stop();
        if (_huidig == this)
        {
            _huidig = null;
        }
        if (!IsDisposed)
        {
            Close();
            Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var vlak = new SolidBrush(Theme.Card))
        using (var path = Theme.RoundedPath(rect, 9))
        {
            g.FillPath(vlak, path);
            using var pen = new Pen(Theme.BorderLight);
            g.DrawPath(pen, path);
        }
        using (var accent = new SolidBrush(Theme.Accent))
        using (var balk = Theme.RoundedPath(new Rectangle(6, 10, 3, Height - 20), 2))
        {
            g.FillPath(accent, balk);
        }
        TextRenderer.DrawText(g, _titel, Theme.SemiBold,
            new Rectangle(20, 10, Width - 56, 22), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Text, Theme.BaseFont,
            new Rectangle(20, 34, Width - 40, Height - 44), Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        _sluitRect = new Rectangle(Width - 30, 8, 22, 22);
        TextRenderer.DrawText(g, "✕", Theme.BaseFont, _sluitRect, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
