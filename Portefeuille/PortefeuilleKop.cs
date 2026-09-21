using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// De kop van het portefeuillevenster: de totale waarde groot, de dagbeweging als gekleurde
/// pil ernaast, en daaronder één staaf die toont hoe de waarde over de potjes verdeeld is.
/// Alles wat een bedrag is, verdwijnt achter bolletjes zodra de privacystand aan staat;
/// percentages en de vorm van de staaf blijven wel staan.
/// </summary>
public sealed class PortefeuilleKop : Control
{
    private static readonly Font GrootFont = Zoek("Segoe UI Variable Display", 27f);
    private static readonly Font PilFont = Zoek("Segoe UI Variable Text Semibold", 10.5f);

    private PortefeuilleStand? _stand;
    private string? _potje;

    public PortefeuilleKop()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BaseFont;
        HerberekenHoogte();
    }

    // Alle hoogtes volgen uit de gemeten tekst in plaats van uit vaste pixels: op een scherm
    // met 125 % schaling werd het bedrag anders onderaan afgesneden.
    private int TitelHoogte => TextRenderer.MeasureText("Xg", Theme.CaptionFont).Height;

    private int BedragHoogte => TextRenderer.MeasureText("€ 0", GrootFont).Height;

    private int RegelHoogte => TextRenderer.MeasureText("Xg", Theme.BaseFont).Height;

    private int StaafTop => 10 + TitelHoogte + 6 + BedragHoogte + 12;

    private int StaafHoogte => Math.Max(8, RegelHoogte / 2);

    private void HerberekenHoogte() => Height = StaafTop + StaafHoogte + 8 + RegelHoogte + 8;

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        HerberekenHoogte();
    }

    /// <summary>De stand die getoond wordt, en het eventueel gekozen potje (leeg = alles).</summary>
    public void Toon(PortefeuilleStand? stand, string? potje)
    {
        _stand = stand;
        _potje = potje;
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool BedragenVerborgen { get; set; }

    /// <summary>Regel in accentkleur rechtsboven: mijlpaal, hoogste stand, of leeg.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Bijzonder { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        var stand = _stand?.Filter(_potje);
        var titel = string.IsNullOrEmpty(_potje) ? "TOTALE WAARDE" : _potje.ToUpperInvariant();
        var titelVak = new Rectangle(2, 10, Width - 4, TitelHoogte);
        TextRenderer.DrawText(g, titel, Theme.CaptionFont, titelVak,
            Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        if (Bijzonder.Length > 0)
        {
            TextRenderer.DrawText(g, Bijzonder, Theme.CaptionFont,
                new Rectangle(Width / 2, titelVak.Y, Width / 2 - 4, TitelHoogte), Theme.Accent,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        var bedragVak = new Rectangle(0, titelVak.Bottom + 6, Width, BedragHoogte);
        if (stand is null || stand.Regels.Count == 0)
        {
            TextRenderer.DrawText(g, "—", GrootFont, bedragVak, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        var bedrag = Bedrag.Euro(stand.Totaal, BedragenVerborgen);
        var bedragMaat = TextRenderer.MeasureText(bedrag, GrootFont);
        TextRenderer.DrawText(g, bedrag, GrootFont, bedragVak, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var pilHoogte = RegelHoogte + 10;
        TekenPil(g, new Point(bedragMaat.Width + 14,
            bedragVak.Y + (bedragVak.Height - pilHoogte) / 2), stand);
        TekenVerdeling(g, stand);
    }

    /// <summary>De dagbeweging als afgeronde pil: pijl, bedrag en percentage.</summary>
    private void TekenPil(Graphics g, Point linksboven, PortefeuilleStand stand)
    {
        var kleur = Bedrag.Kleur(stand.DagVerschil);
        var tekst = BedragenVerborgen
            ? $"{Bedrag.Pijl(stand.DagVerschil)} {Bedrag.Procent(stand.DagProcent)}"
            : $"{Bedrag.Pijl(stand.DagVerschil)} {Bedrag.EuroDelta(stand.DagVerschil)}   " +
              Bedrag.Procent(stand.DagProcent);
        var maat = TextRenderer.MeasureText(tekst, PilFont);
        var pil = new Rectangle(linksboven.X, linksboven.Y, maat.Width + 22, maat.Height + 10);
        if (pil.Right > Width - 4)
        {
            return; // te smal venster: liever niets dan een afgekapte pil
        }
        using (var pad = Theme.RoundedPath(pil, pil.Height / 2))
        {
            using var vulling = new SolidBrush(Color.FromArgb(Theme.Palet.Donker ? 46 : 32, kleur));
            g.FillPath(vulling, pad);
        }
        TextRenderer.DrawText(g, tekst, PilFont, pil, kleur,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var achter = new Rectangle(pil.Right + 8, pil.Y, Width - pil.Right - 12, pil.Height);
        if (achter.Width > 60)
        {
            TextRenderer.DrawText(g, "vandaag", Theme.BaseFont, achter, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Eén staaf met een segment per potje, met de legende eronder.</summary>
    private void TekenVerdeling(Graphics g, PortefeuilleStand stand)
    {
        var delen = stand.PerPotje().Where(p => p.Waarde > 0).ToList();
        if (delen.Count == 0 || stand.Totaal <= 0)
        {
            return;
        }

        var staaf = new Rectangle(0, StaafTop, Math.Max(20, Width - 4), StaafHoogte);
        var x = (float)staaf.X;
        for (var i = 0; i < delen.Count; i++)
        {
            var breedte = (float)(delen[i].Waarde / stand.Totaal) * staaf.Width;
            // Laatste segment tot exact de rand, anders blijft er een haarlijn open.
            if (i == delen.Count - 1)
            {
                breedte = staaf.Right - x;
            }
            var deel = new RectangleF(x, staaf.Y, Math.Max(2, breedte - (i < delen.Count - 1 ? 3 : 0)), staaf.Height);
            using var vulling = new SolidBrush(PotjeKleur(delen[i].Potje, i));
            using var pad = Theme.RoundedPath(Rectangle.Round(deel), 5);
            g.FillPath(vulling, pad);
            x += breedte;
        }

        var legende = staaf.X;
        var legendeTop = staaf.Bottom + 8;
        for (var i = 0; i < delen.Count; i++)
        {
            var aandeel = delen[i].Waarde / stand.Totaal * 100;
            var tekst = BedragenVerborgen
                ? $"{delen[i].Potje}  {aandeel:N0} %"
                : $"{delen[i].Potje}  {Bedrag.Euro(delen[i].Waarde)}  ·  {aandeel:N0} %";
            var maat = TextRenderer.MeasureText(tekst, Theme.BaseFont);
            if (legende + maat.Width + 18 > Width)
            {
                break;
            }
            using (var stip = new SolidBrush(PotjeKleur(delen[i].Potje, i)))
            {
                g.FillEllipse(stip, legende + 1, legendeTop + (RegelHoogte - 8) / 2f, 8, 8);
            }
            TextRenderer.DrawText(g, tekst, Theme.BaseFont,
                new Rectangle(legende + 14, legendeTop, maat.Width + 6, RegelHoogte), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            legende += maat.Width + 32;
        }
    }

    /// <summary>
    /// Vaste kleur per potje: privé en de vennootschap krijgen hun klantkleur uit het thema,
    /// zelfverzonnen potjes een kleur uit het accentgamma.
    /// </summary>
    public static Color PotjeKleur(string potje, int index) => potje switch
    {
        Portefeuille.PrivePotje => Theme.KlantPrive,
        Portefeuille.BedrijfPotje => Theme.KlantUrbanIt,
        _ => (index % 3) switch
        {
            0 => Theme.Accent,
            1 => Theme.KlantAqurat,
            _ => Theme.KlantRadiology,
        },
    };

    private static Font Zoek(string naam, float grootte)
    {
        try
        {
            var font = new Font(naam, grootte);
            if (font.Name.Equals(naam, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }
            font.Dispose();
        }
        catch
        {
            // Onbekend lettertype: hieronder de huisstijlfamilie.
        }
        return new Font(Theme.BaseFont.FontFamily, grootte);
    }
}
