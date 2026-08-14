using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Dun animerend activiteitsbalkje (zoals in moderne web-apps): een accentkleurig segment
/// dat van links naar rechts blijft vegen zolang <see cref="Actief"/> aan staat.
/// Onzichtbaar (achtergrondkleur) wanneer inactief. Docken onder de werkbalk.
/// </summary>
public class PulseBar : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private float _positie; // -0.3 .. 1.3 (segment start buiten beeld)
    private bool _actief;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Actief
    {
        get => _actief;
        set
        {
            if (_actief == value)
            {
                return;
            }
            _actief = value;
            if (value)
            {
                _positie = -0.3f;
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
            Invalidate();
        }
    }

    public PulseBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 3;
        Enabled = false; // puur decoratief, geen muisinteractie
        _timer.Tick += (_, _) =>
        {
            _positie += 0.014f;
            if (_positie > 1.3f)
            {
                _positie = -0.3f;
            }
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        if (!_actief || Width < 20)
        {
            return;
        }

        var segment = (int)(Width * 0.28f);
        var x = (int)(_positie * (Width + segment)) - segment;
        var rect = new Rectangle(x, 0, segment, Height);
        using var brush = new LinearGradientBrush(
            new Rectangle(rect.X - 1, 0, rect.Width + 2, Height),
            Color.FromArgb(0, Theme.Accent), Color.FromArgb(0, Theme.Accent), 0f);
        var blend = new ColorBlend
        {
            Colors = new[] { Color.FromArgb(0, Theme.Accent), Theme.AccentHover, Color.FromArgb(0, Theme.Accent) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        brush.InterpolationColors = blend;
        g.FillRectangle(brush, rect);
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
