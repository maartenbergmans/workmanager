using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Kaart met een titel, een alinea en/of een opsomming, waarbij de tekst netjes terugloopt en
/// de kaart zelf de juiste hoogte krijgt. Gebruikt in het dagstartvenster; de ouder roept
/// <see cref="ZetBreedte"/> aan bij elke resize en de kaart rekent zijn hoogte zelf uit.
/// </summary>
public sealed class BriefingKaart : Control
{
    private const int Rand = 16;
    private const int TitelRuimte = 26;
    private const int PuntInspring = 18;

    private readonly List<string> _punten = new();

    public BriefingKaart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BaseFont;
        Margin = new Padding(0, 0, 0, 10);
    }

    /// <summary>Kop van de kaart (in accentkleur, met eventueel een icoon ervoor).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Titel { get; set; } = "";

    /// <summary>Icoontje uit <see cref="Fluent"/>; leeg = geen icoon.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    /// <summary>Vrije alinea boven de opsomming.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Tekst { get; set; } = "";

    /// <summary>Kleur van de titel en de opsommingstekens.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Theme.Accent;

    /// <summary>De opsommingsregels van deze kaart.</summary>
    public IList<string> Punten => _punten;

    /// <summary>
    /// Tekent het themamotief (zie <see cref="ThemaEmbleem"/>) gedempt rechts in de kaart.
    /// Bedoeld voor één kaart per venster — de kopkaart — anders wordt het behang.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MetEmbleem { get; set; }

    /// <summary>Toont "(niets)" in plaats van een lege kaart.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LegeTekst { get; set; } = "";

    /// <summary>Zet de breedte en bereken de bijbehorende hoogte.</summary>
    public void ZetBreedte(int breedte)
    {
        breedte = Math.Max(160, breedte);
        Width = breedte;
        Height = Hoogte(breedte);
    }

    private bool Leeg => Tekst.Length == 0 && _punten.Count == 0;

    private int Hoogte(int breedte)
    {
        var binnen = breedte - Rand * 2;
        var hoogte = Rand + (Titel.Length > 0 ? TitelRuimte : 0);

        if (Leeg)
        {
            return hoogte + Meet(LegeTekst.Length > 0 ? LegeTekst : "(niets)", Theme.BaseFont, binnen) + Rand;
        }
        if (Tekst.Length > 0)
        {
            hoogte += Meet(Tekst, Theme.BaseFont, binnen) + (_punten.Count > 0 ? 10 : 0);
        }
        foreach (var punt in _punten)
        {
            hoogte += Meet(punt, Theme.BaseFont, binnen - PuntInspring) + 7;
        }
        return hoogte + Rand;
    }

    private static int Meet(string tekst, Font font, int breedte) =>
        TextRenderer.MeasureText(tekst, font, new Size(Math.Max(40, breedte), int.MaxValue),
            TextFormatFlags.WordBreak).Height;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        var vlak = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var pad = Theme.RoundedPath(vlak, 10))
        {
            using var vulling = new SolidBrush(Theme.Card);
            g.FillPath(vulling, pad);
            using var rand = new Pen(Theme.Border);
            g.DrawPath(rand, pad);
        }
        // Het themamotief eerst, zodat alle tekst er overheen komt te staan. Rechts in de
        // kaart, waar de tekst zelden komt, en zo gedempt dat het niet met de inhoud vecht.
        if (MetEmbleem && Height > 60)
        {
            var maat = Math.Min(Height - 14, 132);
            var oudeClip = g.Clip;
            using (var pad = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            {
                g.SetClip(pad);
                ThemaEmbleem.Teken(g, new Rectangle(Width - maat - 18, (Height - maat) / 2, maat, maat),
                    0.22f, Theme.Card);
            }
            g.Clip = oudeClip;
        }
        // Accentstreepje links: maakt in één oogopslag duidelijk welk soort kaart dit is.
        using (var streep = new SolidBrush(Accent))
        using (var pad = Theme.RoundedPath(new Rectangle(0, 6, 3, Height - 13), 2))
        {
            g.FillPath(streep, pad);
        }

        var binnen = Width - Rand * 2;
        var y = Rand;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

        if (Titel.Length > 0)
        {
            var kop = Titel.ToUpperInvariant();
            var x = Rand;
            if (Glyph.Length > 0)
            {
                TextRenderer.DrawText(g, Glyph, Theme.IconFont, new Point(x, y - 1), Accent);
                x += 22;
            }
            TextRenderer.DrawText(g, kop, Theme.CaptionFont,
                new Rectangle(x, y, Width - x - Rand, TitelRuimte), Accent, Wrap);
            y += TitelRuimte;
        }

        if (Leeg)
        {
            TextRenderer.DrawText(g, LegeTekst.Length > 0 ? LegeTekst : "(niets)", Theme.BaseFont,
                new Rectangle(Rand, y, binnen, Height - y), Theme.Muted, Wrap);
            return;
        }

        if (Tekst.Length > 0)
        {
            var hoogte = Meet(Tekst, Theme.BaseFont, binnen);
            TextRenderer.DrawText(g, Tekst, Theme.BaseFont,
                new Rectangle(Rand, y, binnen, hoogte), Theme.Text, Wrap);
            y += hoogte + (_punten.Count > 0 ? 10 : 0);
        }

        foreach (var punt in _punten)
        {
            var hoogte = Meet(punt, Theme.BaseFont, binnen - PuntInspring);
            using (var stip = new SolidBrush(Accent))
            {
                g.FillEllipse(stip, Rand + 3, y + 6, 5, 5);
            }
            TextRenderer.DrawText(g, punt, Theme.BaseFont,
                new Rectangle(Rand + PuntInspring, y, binnen - PuntInspring, hoogte), Theme.Text, Wrap);
            y += hoogte + 7;
        }
    }
}
