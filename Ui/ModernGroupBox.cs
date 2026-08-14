using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Groepsvak in kaartstijl: afgerond, iets lichter vlak dan de vensterachtergrond,
/// met het opschrift als klein kopje op de rand. Drop-in vervanger voor GroupBox.
/// </summary>
public class ModernGroupBox : GroupBox
{
    private Color _accent = Color.Empty;

    /// <summary>
    /// Eigen kleur voor dit paneel: het kopje krijgt die tint en er loopt een fijn gekleurd
    /// randje langs de bovenkant. Leeg (standaard) = het neutrale grijs van vroeger.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Accent
    {
        get => _accent;
        set
        {
            _accent = value;
            Invalidate();
        }
    }

    public ModernGroupBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Theme.Muted;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var achter = Parent?.BackColor ?? Theme.Bg;
        g.Clear(achter);

        var kopHoogte = TextRenderer.MeasureText("Ag", Theme.CaptionFont).Height;
        var rect = new Rectangle(0, kopHoogte / 2, Width - 1, Height - kopHoogte / 2 - 1);
        using (var path = Theme.RoundedPath(rect, 8))
        {
            using var vlak = new SolidBrush(Theme.Surface);
            g.FillPath(vlak, path);
            using var pen = new Pen(_accent.IsEmpty ? Theme.Border : Color.FromArgb(90, _accent));
            g.DrawPath(pen, path);
            // Subtiele lichtlijn aan de binnenkant van de bovenrand geeft de kaart diepte;
            // met een accentkleur wordt dat een fijne gekleurde streep.
            // Bij een accentkleur loopt die streep nu uit in het niets in plaats van hard te
            // stoppen: hetzelfde signaal, maar het oog blijft er niet aan haken.
            var lijnVan = rect.X + 9;
            var lijnTot = rect.Right - 9;
            if (_accent.IsEmpty)
            {
                using var licht = new Pen(Color.FromArgb(16, Theme.Palet.Donker ? Color.White : Color.Black));
                g.DrawLine(licht, lijnVan, rect.Y + 1, lijnTot, rect.Y + 1);
            }
            else if (lijnTot > lijnVan)
            {
                using var verloop = new LinearGradientBrush(
                    new Rectangle(lijnVan, rect.Y, lijnTot - lijnVan, 3),
                    Color.FromArgb(150, _accent), Color.FromArgb(0, _accent), LinearGradientMode.Horizontal);
                using var licht = new Pen(verloop, 2f);
                g.DrawLine(licht, lijnVan, rect.Y + 1, lijnTot, rect.Y + 1);
            }
        }

        if (Text.Length > 0)
        {
            var maat = TextRenderer.MeasureText(g, Text, Theme.CaptionFont);
            var kopRect = new Rectangle(14, 0, Math.Min(maat.Width + 10, Width - 28), kopHoogte);
            using var bg = new SolidBrush(achter);
            g.FillRectangle(bg, kopRect);
            TextRenderer.DrawText(g, Text, Theme.CaptionFont,
                new Point(kopRect.X + 5, 0), _accent.IsEmpty ? Theme.Muted : _accent,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
