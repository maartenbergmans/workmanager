using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Eén gerechtkaart in HelloFresh-stijl: grote foto, titel, ondertitel met de kern­-
/// ingrediënten en een inforegel (tijd, Nutri, prijs, bonus). Klikken vinkt de kaart aan of
/// uit (accentrand + vinkbadge), de sterren rechtsonder zijn direct klikbaar en dubbelklik
/// opent de receptkaart. Rubrieken en voorraadregels gebruiken dezelfde kaart in de compacte
/// variant zonder foto.
/// </summary>
public sealed class AhGerechtKaart : Control
{
    /// <summary>Vaste kaartbreedte; de hoogte hangt af van <see cref="MetFoto"/>.</summary>
    public const int KaartBreedte = 252;

    public const int HoogteMetFoto = 248;
    public const int HoogteCompact = 66;

    private const int FotoHoogte = 140;

    public string Naam { get; }

    /// <summary>Ondertitel ("met patatjes en sperziebonen"); leeg = niet tonen.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Subtitel { get; set; } = "";

    /// <summary>Inforegel onderaan ("⏱ 30 min · Nutri B · ≈ € 7,76"); leeg = niet tonen.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Info { get; set; } = "";

    /// <summary>🏷-badge linksboven op de foto als er bonusproducten in zitten (0 = geen).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Bonus { get; set; }

    public bool MetFoto { get; }

    /// <summary>0 = geen sterren (rubrieken); 1–3 = klikbare waardering.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Sterren { get; set; }

    private bool _aangevinkt;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Aangevinkt
    {
        get => _aangevinkt;
        set
        {
            if (_aangevinkt != value)
            {
                _aangevinkt = value;
                Invalidate();
            }
        }
    }

    /// <summary>Vinkje gewisseld door een klik van de gebruiker.</summary>
    public event Action<AhGerechtKaart>? VinkGewisseld;

    /// <summary>Sterren aangeklikt (nieuwe waarde staat dan al in <see cref="Sterren"/>).</summary>
    public event Action<AhGerechtKaart>? SterrenGewijzigd;

    /// <summary>Dubbelklik: toon het recept.</summary>
    public event Action<AhGerechtKaart>? ReceptGevraagd;

    public AhGerechtKaart(string naam, bool metFoto)
    {
        Naam = naam;
        MetFoto = metFoto;
        Size = new Size(KaartBreedte, metFoto ? HoogteMetFoto : HoogteCompact);
        Margin = new Padding(6);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    /// <summary>De rechthoek van de drie sterren, rechtsonder op de kaart.</summary>
    private Rectangle SterrenZone => new(Width - 74, Height - 30, 66, 24);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (Sterren > 0 && SterrenZone.Contains(e.Location))
        {
            Sterren = Math.Clamp((e.X - SterrenZone.X) / (SterrenZone.Width / 3) + 1, 1, 3);
            Invalidate();
            SterrenGewijzigd?.Invoke(this);
            return;
        }
        _aangevinkt = !_aangevinkt;
        Invalidate();
        VinkGewisseld?.Invoke(this);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        ReceptGevraagd?.Invoke(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var kader = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var pad = Theme.RoundedPath(kader, 10))
        {
            using var vlak = new SolidBrush(Theme.Surface);
            g.FillPath(vlak, pad);
            using var rand = new Pen(_aangevinkt ? Theme.Accent : Theme.Border, _aangevinkt ? 2f : 1f);
            g.DrawPath(rand, pad);
        }

        var y = 8;
        if (MetFoto)
        {
            var fotoRect = new Rectangle(1, 1, Width - 2, FotoHoogte);
            using (var clip = Theme.RoundedPath(new Rectangle(1, 1, Width - 3, FotoHoogte + 10), 10))
            {
                var oud = g.Clip;
                g.SetClip(clip);
                g.SetClip(fotoRect, CombineMode.Intersect);
                if (GerechtFoto.Kaart(Naam) is { } foto)
                {
                    // Cover-crop: vul de hele fotozone, snij bij wat niet past.
                    var schaal = Math.Max((float)fotoRect.Width / foto.Width,
                        (float)fotoRect.Height / foto.Height);
                    var w = foto.Width * schaal;
                    var h = foto.Height * schaal;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(foto, fotoRect.X + (fotoRect.Width - w) / 2f,
                        fotoRect.Y + (fotoRect.Height - h) / 2f, w, h);
                }
                else
                {
                    using var donker = new SolidBrush(Theme.Surface);
                    g.FillRectangle(donker, fotoRect);
                    TextRenderer.DrawText(g, Fluent.EtenDrinken, Theme.IconLarge, fotoRect,
                        Theme.Mix(Theme.Bg, Theme.Muted, 0.55f),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                g.Clip = oud;
            }
            if (Bonus > 0)
            {
                var badge = new Rectangle(8, 8, 74, 22);
                using var badgePad = Theme.RoundedPath(badge, 11);
                using var badgeVlak = new SolidBrush(Theme.Warn);
                g.FillPath(badgeVlak, badgePad);
                TextRenderer.DrawText(g, $"🏷 {Bonus} bonus", Theme.CaptionFont, badge, Theme.OpAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            y = FotoHoogte + 8;
        }

        // Vinkbadge rechtsboven (op de foto of naast de titel).
        var vink = new Rectangle(Width - 30, 8, 20, 20);
        using (var cirkel = new GraphicsPath())
        {
            cirkel.AddEllipse(vink);
            using var vlak = new SolidBrush(_aangevinkt ? Theme.Accent : Color.FromArgb(140, Theme.Surface));
            g.FillPath(vlak, cirkel);
            using var rand = new Pen(_aangevinkt ? Theme.Accent : Theme.Border);
            g.DrawPath(rand, cirkel);
        }
        if (_aangevinkt)
        {
            using var pen = new Pen(Theme.OpAccent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new Point(vink.X + 5, vink.Y + 10),
                new Point(vink.X + 8, vink.Y + 14),
                new Point(vink.X + 15, vink.Y + 6),
            });
        }

        var tekstBreedte = Width - 16 - (MetFoto ? 0 : 26);
        using var titelFont = new Font(Font, FontStyle.Bold);
        var titelRect = new Rectangle(10, y, tekstBreedte, MetFoto ? 38 : Height - 16);
        TextRenderer.DrawText(g, Naam, titelFont, titelRect, Theme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
            (MetFoto ? TextFormatFlags.Top : TextFormatFlags.VerticalCenter));
        if (!MetFoto)
        {
            return; // compacte kaart: alleen titel + vinkje
        }
        y += 40;
        if (Subtitel.Length > 0)
        {
            TextRenderer.DrawText(g, Subtitel, Font, new Rectangle(10, y, tekstBreedte, 34),
                Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (Info.Length > 0)
        {
            TextRenderer.DrawText(g, Info, Font,
                new Rectangle(10, Height - 28, Width - (Sterren > 0 ? 84 : 16), 22), Theme.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
        if (Sterren > 0)
        {
            var zone = SterrenZone;
            var sterBreedte = zone.Width / 3;
            for (var i = 0; i < 3; i++)
            {
                TextRenderer.DrawText(g, i < Sterren ? "★" : "☆", Font,
                    new Rectangle(zone.X + i * sterBreedte, zone.Y, sterBreedte, zone.Height),
                    i < Sterren ? Theme.Accent : Theme.Mix(Theme.Bg, Theme.Muted, 0.55f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
