using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace WorkManager;

/// <summary>
/// Het waardeverloop van de portefeuille als vlakgrafiek: één lijn met een verlopende vulling
/// eronder, groen bij winst over de periode en rood bij verlies. Met de muis erover verschijnt
/// een peillijn met de waarde van die dag. In privacystand staan er percentages op de as in
/// plaats van bedragen, zodat de vorm van de curve wél zichtbaar blijft.
/// </summary>
public sealed class WaardeGrafiek : Control
{
    // De marges volgen uit de gemeten tekst: met vaste pixels vielen de aslabels op een
    // scherm met 125 % schaling half buiten beeld.
    private int MargeLinks => TextRenderer.MeasureText("−188,8k", Theme.MonoSmall).Width + 12;

    private int MargeRechts => 14;

    private int MargeBoven => 14;

    private int MargeOnder => TextRenderer.MeasureText("Xg", Theme.MonoSmall).Height + 10;

    private IReadOnlyList<(DateOnly Dag, double Waarde)> _punten = Array.Empty<(DateOnly, double)>();
    private int _peil = -1;

    public WaardeGrafiek()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BaseFont;
    }

    /// <summary>De punten, oplopend in de tijd.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<(DateOnly Dag, double Waarde)> Punten
    {
        get => _punten;
        set
        {
            _punten = value;
            _peil = -1;
            Invalidate();
        }
    }

    /// <summary>Bedragen vervangen door percentages ten opzichte van het beginpunt.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool BedragenVerborgen { get; set; }

    /// <summary>Tekst als er (nog) geen verloop is.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LegeTekst { get; set; } = "Nog geen koershistoriek";

    /// <summary>De dag waar de muis boven hangt, voor de regel onder de grafiek.</summary>
    public event Action<(DateOnly Dag, double Waarde)?>? Gepeild;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_punten.Count < 2)
        {
            return;
        }
        var vlak = Tekenvlak();
        var deel = (e.X - vlak.Left) / (double)Math.Max(1, vlak.Width);
        var index = (int)Math.Round(Math.Clamp(deel, 0, 1) * (_punten.Count - 1));
        if (index == _peil)
        {
            return;
        }
        _peil = index;
        Gepeild?.Invoke(_punten[index]);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_peil < 0)
        {
            return;
        }
        _peil = -1;
        Gepeild?.Invoke(null);
        Invalidate();
    }

    private Rectangle Tekenvlak() => new(
        MargeLinks, MargeBoven,
        Math.Max(10, Width - MargeLinks - MargeRechts),
        Math.Max(10, Height - MargeBoven - MargeOnder));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        var kaart = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var pad = Theme.RoundedPath(kaart, 10))
        {
            using var vulling = new SolidBrush(Theme.Card);
            g.FillPath(vulling, pad);
            using var rand = new Pen(Theme.Border);
            g.DrawPath(rand, pad);
        }

        if (_punten.Count < 2)
        {
            TextRenderer.DrawText(g, LegeTekst, Theme.BaseFont, kaart, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var vlak = Tekenvlak();
        var min = _punten.Min(p => p.Waarde);
        var max = _punten.Max(p => p.Waarde);
        // Wat lucht boven en onder, en nooit een nulbereik (vlakke lijn).
        var marge = Math.Max((max - min) * 0.12, Math.Max(max * 0.001, 0.5));
        var onder = min - marge;
        var boven = max + marge;
        var start = _punten[0].Waarde;
        var stijgt = _punten[^1].Waarde >= start;
        var lijnKleur = stijgt ? Theme.Success : Theme.Danger;

        float X(int i) => vlak.Left + (float)i / (_punten.Count - 1) * vlak.Width;
        float Y(double waarde) => vlak.Bottom - (float)((waarde - onder) / (boven - onder)) * vlak.Height;

        TekenRaster(g, vlak, onder, boven, start);

        var lijn = new PointF[_punten.Count];
        for (var i = 0; i < _punten.Count; i++)
        {
            lijn[i] = new PointF(X(i), Y(_punten[i].Waarde));
        }

        // Vulling onder de lijn: van de lijnkleur naar niets, zodat de curve zelf het beeld blijft.
        using (var vlakPad = new GraphicsPath())
        {
            vlakPad.AddLines(lijn);
            vlakPad.AddLine(lijn[^1].X, vlak.Bottom, lijn[0].X, vlak.Bottom);
            vlakPad.CloseFigure();
            using var verloop = new LinearGradientBrush(
                new Rectangle(vlak.Left, vlak.Top, vlak.Width, vlak.Height + 1),
                Color.FromArgb(Theme.Palet.Donker ? 78 : 58, lijnKleur),
                Color.FromArgb(0, lijnKleur),
                LinearGradientMode.Vertical);
            g.FillPath(verloop, vlakPad);
        }

        using (var pen = new Pen(lijnKleur, 2f) { LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, lijn);
        }

        // Het laatste punt krijgt een dot met halo: dat is "nu".
        var laatste = lijn[^1];
        using (var halo = new SolidBrush(Color.FromArgb(70, lijnKleur)))
        {
            g.FillEllipse(halo, laatste.X - 6, laatste.Y - 6, 12, 12);
        }
        using (var dot = new SolidBrush(lijnKleur))
        {
            g.FillEllipse(dot, laatste.X - 3.2f, laatste.Y - 3.2f, 6.4f, 6.4f);
        }

        TekenDatums(g, vlak);

        if (_peil >= 0 && _peil < lijn.Length)
        {
            TekenPeil(g, vlak, lijn[_peil], _punten[_peil], lijnKleur);
        }
    }

    /// <summary>Horizontale hulplijnen met hun waarde, plus de streeplijn van het beginpunt.</summary>
    private void TekenRaster(Graphics g, Rectangle vlak, double onder, double boven, double start)
    {
        using var rasterPen = new Pen(Color.FromArgb(Theme.Palet.Donker ? 42 : 34, Theme.Text));
        const int lijnen = 4;
        for (var i = 0; i <= lijnen; i++)
        {
            var waarde = onder + (boven - onder) * i / lijnen;
            var y = vlak.Bottom - (float)i / lijnen * vlak.Height;
            g.DrawLine(rasterPen, vlak.Left, y, vlak.Right, y);
            var label = BedragenVerborgen
                ? (start > 0 ? Bedrag.Procent((waarde - start) / start * 100, 1) : "")
                : Kort(waarde);
            var hoogte = TextRenderer.MeasureText("Xg", Theme.MonoSmall).Height;
            TextRenderer.DrawText(g, label, Theme.MonoSmall,
                new Rectangle(2, (int)y - hoogte / 2, MargeLinks - 8, hoogte), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // Waar stond de portefeuille aan het begin van de periode? Alles boven die streep is winst.
        if (start > onder && start < boven)
        {
            using var startPen = new Pen(Color.FromArgb(90, Theme.Muted)) { DashStyle = DashStyle.Dash };
            var y = vlak.Bottom - (float)((start - onder) / (boven - onder)) * vlak.Height;
            g.DrawLine(startPen, vlak.Left, y, vlak.Right, y);
        }
    }

    private void TekenDatums(Graphics g, Rectangle vlak)
    {
        var eerste = _punten[0].Dag;
        var laatste = _punten[^1].Dag;
        var maanden = (laatste.Year - eerste.Year) * 12 + laatste.Month - eerste.Month;
        var opmaak = maanden >= 10 ? "MMM yy" : "d MMM";
        var nl = CultureInfo.GetCultureInfo("nl-BE");

        var breed = TextRenderer.MeasureText("00 mmm 00", Theme.MonoSmall).Width;
        var hoog = TextRenderer.MeasureText("Xg", Theme.MonoSmall).Height;

        void Datum(int index, TextFormatFlags uitlijning, int links)
        {
            var tekst = _punten[index].Dag.ToString(opmaak, nl);
            TextRenderer.DrawText(g, tekst, Theme.MonoSmall,
                new Rectangle(links, vlak.Bottom + 4, breed, hoog), Theme.Muted,
                uitlijning | TextFormatFlags.VerticalCenter);
        }

        Datum(0, TextFormatFlags.Left, vlak.Left);
        Datum(_punten.Count - 1, TextFormatFlags.Right, vlak.Right - breed);
        if (vlak.Width > breed * 4)
        {
            Datum(_punten.Count / 2, TextFormatFlags.HorizontalCenter,
                vlak.Left + vlak.Width / 2 - breed / 2);
        }
    }

    /// <summary>Peillijn met de waarde van de dag waar de muis boven hangt.</summary>
    private void TekenPeil(Graphics g, Rectangle vlak, PointF punt,
        (DateOnly Dag, double Waarde) meting, Color kleur)
    {
        using (var pen = new Pen(Color.FromArgb(120, Theme.Text)) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(pen, punt.X, vlak.Top, punt.X, vlak.Bottom);
        }
        using (var dot = new SolidBrush(kleur))
        {
            g.FillEllipse(dot, punt.X - 4, punt.Y - 4, 8, 8);
        }
        using (var ring = new Pen(Theme.Card, 2f))
        {
            g.DrawEllipse(ring, punt.X - 4, punt.Y - 4, 8, 8);
        }

        var tekst = BedragenVerborgen
            ? meting.Dag.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("nl-BE"))
            : $"{meting.Dag.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("nl-BE"))}   " +
              Bedrag.Euro(meting.Waarde);
        var maat = TextRenderer.MeasureText(tekst, Theme.MonoSmall);
        var breedte = maat.Width + 16;
        var links = (int)Math.Clamp(punt.X - breedte / 2f, vlak.Left, vlak.Right - breedte);
        var vak = new Rectangle(links, vlak.Top - 2, breedte, maat.Height + 8);
        using (var pad = Theme.RoundedPath(vak, 6))
        {
            using var vulling = new SolidBrush(Theme.Surface);
            g.FillPath(vulling, pad);
            using var rand = new Pen(Theme.Border);
            g.DrawPath(rand, pad);
        }
        TextRenderer.DrawText(g, tekst, Theme.MonoSmall, vak, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Astekst kort houden: 152.842 wordt "152,8k".</summary>
    private static string Kort(double waarde)
    {
        var nl = CultureInfo.GetCultureInfo("nl-BE");
        return Math.Abs(waarde) >= 10_000
            ? (waarde / 1000).ToString("N1", nl) + "k"
            : waarde.ToString("N0", nl);
    }
}
