namespace WorkManager;

/// <summary>
/// Dunne stappenindicator bovenin de AH-bestelflow: "Gerechten › Boodschappen › Agenda ›
/// Mandje", met de huidige stap in accentkleur. Puur oriëntatie — klikken doet niets, de
/// navigatie loopt via de knoppen van de vensters zelf.
/// </summary>
public sealed class AhStappenBalk : Control
{
    private static readonly string[] Stappen = { "Gerechten", "Boodschappen", "Agenda", "Mandje" };

    private readonly int _stap;

    /// <param name="stap">Huidige stap, 1 t/m 4.</param>
    public AhStappenBalk(int stap)
    {
        _stap = Math.Clamp(stap, 1, Stappen.Length);
        Dock = DockStyle.Top;
        Height = 30;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var x = 12;
        var vlaggen = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine;
        for (var i = 0; i < Stappen.Length; i++)
        {
            var huidig = i + 1 == _stap;
            var tekst = $"{i + 1} · {Stappen[i]}";
            var font = huidig ? new Font(Font, FontStyle.Bold) : Font;
            var kleur = huidig ? Theme.Accent : i + 1 < _stap ? Theme.Text : Theme.Muted;
            var breedte = TextRenderer.MeasureText(g, tekst, font).Width;
            TextRenderer.DrawText(g, tekst, font, new Rectangle(x, 0, breedte + 4, Height), kleur, vlaggen);
            x += breedte + 8;
            if (huidig)
            {
                font.Dispose();
            }
            if (i < Stappen.Length - 1)
            {
                var pijlBreedte = TextRenderer.MeasureText(g, "›", Font).Width;
                TextRenderer.DrawText(g, "›", Font, new Rectangle(x, 0, pijlBreedte + 4, Height),
                    Theme.Muted, vlaggen);
                x += pijlBreedte + 8;
            }
        }
    }
}
