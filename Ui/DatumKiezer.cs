using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace WorkManager;

/// <summary>
/// Datumveld in de huisstijl: toont de datum leesbaar ("vr 14 aug") en opent bij een klik
/// een eigen kalenderpopup met snelkeuzes (Vandaag, Morgen, Maandag, …). Vervangt de native
/// DateTimePicker, die in het donkere thema een witte blokkendoos bleef en waarbij een
/// datum leegmaken alleen via een vinkje ernaast kon.
///
/// <para>Leeg mag: <see cref="Waarde"/> is nullable en de popup heeft een "Geen datum".
/// Met <see cref="MinimumDatum"/> worden eerdere dagen grijs en niet klikbaar — zo kan een
/// deadline nooit vóór de startdatum landen.</para>
/// </summary>
public sealed class DatumKiezer : Control
{
    private DateOnly? _waarde;
    private DateOnly? _minimum;
    private bool _hover;
    private DatumPopup? _popup;

    /// <summary>Tekst als er geen datum gekozen is (bv. "geen deadline").</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LeegTekst { get; set; } = "geen";

    /// <summary>Mag de datum leeg zijn? Zo niet, dan verdwijnt "Geen datum" uit de popup.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LeegToegestaan { get; set; } = true;

    /// <summary>Vroegste kiesbare dag; eerdere dagen staan uit (bv. deadline ≥ startdatum).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateOnly? MinimumDatum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            // Een bestaande waarde die nu te vroeg is, schuift mee: het veld toont nooit
            // een datum die je niet meer zou mogen kiezen.
            if (value is { } min && _waarde is { } huidig && huidig < min)
            {
                Waarde = min;
            }
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateOnly? Waarde
    {
        get => _waarde;
        set
        {
            if (_waarde == value)
            {
                return;
            }
            _waarde = value;
            Invalidate();
            WaardeGewijzigd?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? WaardeGewijzigd;

    public DatumKiezer()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.BaseFont;
        Size = new Size(170, 30);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    /// <summary>De datum zoals hij in het veld staat ("vr 14 aug", of "vr 14 aug 2027").</summary>
    public static string Toon(DateOnly dag)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var tekst = dag.ToString(dag.Year == vandaag.Year ? "ddd d MMM" : "ddd d MMM yyyy",
            CultureInfo.CurrentCulture);
        return dag == vandaag ? $"vandaag ({tekst})"
            : dag == vandaag.AddDays(1) ? $"morgen ({tekst})"
            : tekst;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        OpenPopup();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Enter or Keys.Space or Keys.Down or Keys.Delete || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Toetsenbord: Enter/spatie/pijl-omlaag opent, Delete maakt leeg.
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
        {
            e.Handled = true;
            OpenPopup();
        }
        else if (e.KeyCode == Keys.Delete && LeegToegestaan)
        {
            e.Handled = true;
            Waarde = null;
        }
    }

    private void OpenPopup()
    {
        if (_popup is { Visible: true })
        {
            return;
        }
        _popup?.Dispose();
        _popup = new DatumPopup(_waarde, _minimum, LeegToegestaan, gekozen => Waarde = gekozen);
        _popup.Toon(this);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedPath(rect, 6))
        {
            using var vlak = new SolidBrush(_hover ? Theme.CardHover : Theme.Field);
            g.FillPath(vlak, path);
            using var pen = new Pen(Focused ? Theme.Accent : Theme.Border);
            g.DrawPath(pen, path);
        }
        // Kalendericoon links, tekst ernaast, chevron rechts.
        TextRenderer.DrawText(g, Fluent.Kalender, Theme.IconFont,
            new Rectangle(8, 0, 20, Height), _waarde is null ? Theme.Muted : Theme.AccentHover,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _waarde is { } d ? Toon(d) : LeegTekst, Font,
            new Rectangle(30, 0, Width - 48, Height), _waarde is null ? Theme.Muted : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "", Theme.IconFont, // ChevronDown
            new Rectangle(Width - 22, 0, 16, Height), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// De uitklapkalender van <see cref="DatumKiezer"/>: snelkeuzeknoppen bovenaan en daaronder
/// een zelfgetekende maandkalender (weeknummers weggelaten, weekend gedempt, vandaag met een
/// randje, gekozen dag accentgevuld). Sluit bij een keuze, bij Esc of zodra hij focus verliest.
/// </summary>
internal sealed class DatumPopup : Form
{
    private const int CelBreedte = 34;
    private const int CelHoogte = 30;
    private const int Marge = 10;
    private const int KopHoogte = 34;   // maandtitel met ◀ ▶
    private const int DagenHoogte = 22; // ma di wo …

    private readonly Action<DateOnly?> _kies;
    private readonly DateOnly? _minimum;
    private readonly Panel _kalender;
    private DateOnly _maand;
    private DateOnly? _gekozen;
    private DateOnly? _hoverDag;

    public DatumPopup(DateOnly? huidig, DateOnly? minimum, bool leegToegestaan, Action<DateOnly?> kies)
    {
        _kies = kies;
        _minimum = minimum;
        _gekozen = huidig;
        var start = huidig ?? DateOnly.FromDateTime(DateTime.Now);
        _maand = new DateOnly(start.Year, start.Month, 1);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        Font = Theme.BaseFont;

        var presets = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(Marge, Marge, Marge, 0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        foreach (var (label, dag) in Snelkeuzes())
        {
            var knop = new ModernButton { Text = label, Height = 27, Margin = new Padding(0, 0, 6, 6) };
            knop.KrimpNaarInhoud();
            knop.Click += (_, _) => Kies(dag);
            presets.Controls.Add(knop);
        }

        _kalender = new Panel
        {
            Dock = DockStyle.Fill,
            Height = KopHoogte + DagenHoogte + CelHoogte * 6,
        };
        _kalender.Paint += (_, e) => TekenKalender(e.Graphics);
        _kalender.MouseMove += (_, e) =>
        {
            var dag = DagOp(e.Location);
            if (dag != _hoverDag)
            {
                _hoverDag = dag;
                _kalender.Invalidate();
            }
        };
        _kalender.MouseLeave += (_, _) =>
        {
            _hoverDag = null;
            _kalender.Invalidate();
        };
        _kalender.MouseDown += (_, e) =>
        {
            // Maandnavigatie in de kop, anders een dag kiezen.
            if (e.Y < KopHoogte)
            {
                if (e.X < Marge + 34)
                {
                    _maand = _maand.AddMonths(-1);
                    _kalender.Invalidate();
                }
                else if (e.X > _kalender.Width - Marge - 34)
                {
                    _maand = _maand.AddMonths(1);
                    _kalender.Invalidate();
                }
                return;
            }
            if (DagOp(e.Location) is { } dag && Kiesbaar(dag))
            {
                Kies(dag);
            }
        };

        var onder = new Panel { Dock = DockStyle.Bottom, Height = leegToegestaan ? 44 : 10 };
        if (leegToegestaan)
        {
            var wis = new ModernButton
            {
                Text = "Geen datum", Height = 28, Location = new Point(Marge, 6),
            };
            wis.KrimpNaarInhoud();
            wis.Click += (_, _) => Kies(null);
            onder.Controls.Add(wis);
        }

        Controls.Add(_kalender);
        Controls.Add(presets);
        Controls.Add(onder);
        var breedte = Marge * 2 + CelBreedte * 7;
        ClientSize = new Size(Math.Max(breedte, 268),
            presets.Height + KopHoogte + DagenHoogte + CelHoogte * 6 + onder.Height);
        Theme.Apply(this, fade: false);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
        Deactivate += (_, _) => Close();
    }

    /// <summary>Toont de popup onder het veld, of erboven als hij anders van het scherm valt.</summary>
    public void Toon(Control anker)
    {
        var punt = anker.PointToScreen(new Point(0, anker.Height + 4));
        var scherm = Screen.FromControl(anker).WorkingArea;
        if (punt.Y + Height > scherm.Bottom)
        {
            punt.Y = anker.PointToScreen(Point.Empty).Y - Height - 4;
        }
        punt.X = Math.Min(punt.X, scherm.Right - Width - 8);
        punt.X = Math.Max(punt.X, scherm.Left + 8);
        Location = punt;
        Show(anker.FindForm());
        Activate();
    }

    private void Kies(DateOnly? dag)
    {
        _gekozen = dag;
        _kies(dag);
        Close();
    }

    private static IEnumerable<(string Label, DateOnly Dag)> Snelkeuzes()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        yield return ("Vandaag", vandaag);
        yield return ("Morgen", vandaag.AddDays(1));
        var maandag = vandaag.AddDays(((int)DayOfWeek.Monday - (int)vandaag.DayOfWeek + 7) % 7 is var d
            && d == 0 ? 7 : d);
        yield return ("Maandag", maandag);
        yield return ("+1 week", vandaag.AddDays(7));
        yield return ("Einde maand", new DateOnly(vandaag.Year, vandaag.Month,
            DateTime.DaysInMonth(vandaag.Year, vandaag.Month)));
    }

    private bool Kiesbaar(DateOnly dag) => _minimum is not { } min || dag >= min;

    /// <summary>De eerste cel linksboven: de maandag van de week waarin de 1e valt.</summary>
    private DateOnly Roosterstart() =>
        _maand.AddDays(-(((int)_maand.DayOfWeek + 6) % 7));

    private DateOnly? DagOp(Point p)
    {
        var kolom = (p.X - Marge) / CelBreedte;
        var rij = (p.Y - KopHoogte - DagenHoogte) / CelHoogte;
        if (kolom is < 0 or > 6 || rij is < 0 or > 5)
        {
            return null;
        }
        return Roosterstart().AddDays(rij * 7 + kolom);
    }

    private void TekenKalender(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var vandaag = DateOnly.FromDateTime(DateTime.Now);

        // Kop: ◀ maandnaam jaar ▶
        TextRenderer.DrawText(g, "", Theme.IconFont, // ChevronLeft
            new Rectangle(Marge, 0, 34, KopHoogte), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _maand.ToString("MMMM yyyy", CultureInfo.CurrentCulture), Theme.SemiBold,
            new Rectangle(Marge + 34, 0, _kalender.Width - (Marge + 34) * 2, KopHoogte), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "", Theme.IconFont, // ChevronRight
            new Rectangle(_kalender.Width - Marge - 34, 0, 34, KopHoogte), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

        // Weekdagkoppen.
        var dagNamen = new[] { "ma", "di", "wo", "do", "vr", "za", "zo" };
        for (var k = 0; k < 7; k++)
        {
            TextRenderer.DrawText(g, dagNamen[k], Theme.CaptionFont,
                new Rectangle(Marge + k * CelBreedte, KopHoogte, CelBreedte, DagenHoogte), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        var start = Roosterstart();
        for (var i = 0; i < 42; i++)
        {
            var dag = start.AddDays(i);
            var cel = new Rectangle(
                Marge + i % 7 * CelBreedte,
                KopHoogte + DagenHoogte + i / 7 * CelHoogte,
                CelBreedte, CelHoogte);
            var binnen = cel;
            binnen.Inflate(-2, -2);

            var kiesbaar = Kiesbaar(dag);
            var buitenMaand = dag.Month != _maand.Month;
            if (dag == _gekozen)
            {
                using var vul = new SolidBrush(Theme.Accent);
                using var pad = Theme.RoundedPath(binnen, 6);
                g.FillPath(vul, pad);
            }
            else if (dag == _hoverDag && kiesbaar)
            {
                using var vul = new SolidBrush(Theme.CardHover);
                using var pad = Theme.RoundedPath(binnen, 6);
                g.FillPath(vul, pad);
            }
            if (dag == vandaag && dag != _gekozen)
            {
                using var pen = new Pen(Theme.AccentHover);
                using var pad = Theme.RoundedPath(binnen, 6);
                g.DrawPath(pen, pad);
            }

            var kleur = !kiesbaar ? Theme.Mix(Theme.Bg, Theme.Muted, 0.45f)
                : dag == _gekozen ? Theme.OpAccent
                : buitenMaand ? Theme.Mix(Theme.Bg, Theme.Muted, 0.75f)
                : dag.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Theme.Muted
                : Theme.Text;
            TextRenderer.DrawText(g, dag.Day.ToString(), dag == _gekozen ? Theme.SemiBold : Font, cel, kleur,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
