using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Kleine melding die rechtsonder in het venster omhoog schuift en na een paar seconden
/// vanzelf weer verdwijnt. Gebruik: Toast.Toon(form, "3 mails gearchiveerd", Fluent.Archive).
/// Met <see cref="ToonUndo"/> krijgt de melding een klikbare "Ongedaan maken" en blijft ze
/// wat langer staan. <see cref="ToonActie"/>-meldingen (bv. de Spotify-suggestie) blijven
/// staan tot er geklikt wordt — op de actie, of ernaast om weg te klikken; nieuwe meldingen
/// verschijnen er dan boven in plaats van ze te verdringen.
/// </summary>
public sealed class Toast : Control
{
    private const string UndoTekst = "Ongedaan maken";

    private readonly string _glyph;
    private readonly string _actieTekst;
    private readonly Action? _onUndo;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 14 };
    private float _y;
    private int _doelTop;
    private int _fase; // 0 = inschuiven, 1 = tonen, 2 = uitschuiven
    private int _wacht;
    private int _ticks; // totale levensduur; harde grens tegen blijven hangen
    private Rectangle _undoRect;
    private readonly int _toonTicks;
    private readonly bool _blijvend; // pas weg na een klik (met ruime noodgrens)
    private int _stapelOffset;       // hoogte van blijvende toasts waar deze boven hangt

    private Toast(string tekst, string glyph, Action? onUndo, string actieTekst, bool blijvend)
    {
        _glyph = glyph;
        _onUndo = onUndo;
        _actieTekst = actieTekst;
        _blijvend = blijvend;
        Text = tekst;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Font = Theme.BaseFont;
        var maat = TextRenderer.MeasureText(tekst, Theme.BaseFont);
        var undoBreedte = onUndo is not null
            ? TextRenderer.MeasureText(actieTekst, Theme.SemiBold).Width + 22
            : 0;
        Size = new Size(maat.Width + (glyph.Length > 0 ? 62 : 34) + undoBreedte, 44);
        // Met een undo-actie langer zichtbaar (± 5 s) zodat er tijd is om te klikken.
        _toonTicks = onUndo is not null ? 360 : 160;
        if (onUndo is not null)
        {
            Cursor = Cursors.Hand;
        }
    }

    // Meldingenlog: toasts zijn vluchtig; hier blijven de laatste ~30 bewaard zodat een
    // gemiste melding terug te vinden is (🔔 in de cockpit-werkbalk).
    private static readonly List<(DateTimeOffset Moment, string Tekst)> Log = new();

    /// <summary>De recentste meldingen, nieuwste eerst.</summary>
    public static IReadOnlyList<(DateTimeOffset Moment, string Tekst)> Recent
    {
        get { lock (Log) { return Log.ToList(); } }
    }

    private static void Registreer(string tekst)
    {
        lock (Log)
        {
            Log.Insert(0, (DateTimeOffset.Now, tekst));
            if (Log.Count > 30)
            {
                Log.RemoveAt(Log.Count - 1);
            }
        }
    }

    public static void Toon(Form eigenaar, string tekst, string glyph = "") =>
        Maak(eigenaar, tekst, glyph, null, UndoTekst, blijvend: false);

    /// <summary>
    /// Niet-blokkerende foutmelding: een toast met de korte boodschap en een klikbare
    /// "details…" die pas dan de volledige fout toont. Vervangt blokkerende MessageBoxen
    /// voor fouten waar je niet per se meteen iets mee moet.
    /// </summary>
    public static void Fout(Form eigenaar, string kort, string details)
    {
        // Zonder internetverbinding is de echte oorzaak meestal niet de fout zelf: dan
        // meldt de toast dát, met de oorspronkelijke fout achter "details…".
        if (Internet.Offline)
        {
            details = $"{kort}\r\n\r\n{details}";
            kort = "Geen internetverbinding";
        }
        Maak(eigenaar, $"⚠ {kort}", "", () => MessageBox.Show(eigenaar, details,
            "WorkManager", MessageBoxButtons.OK, MessageBoxIcon.Warning), "details…", blijvend: false);
    }

    /// <summary>Toont een melding met een klikbare "Ongedaan maken"-actie.</summary>
    public static void ToonUndo(Form eigenaar, string tekst, Action onUndo, string glyph = "") =>
        Maak(eigenaar, tekst, glyph, onUndo, UndoTekst, blijvend: false);

    /// <summary>
    /// Toont een melding met een klikbare actie onder je eigen naam (bv. "▶ Spotify").
    /// Blijft staan tot er geklikt wordt: op de actie, of ernaast om weg te klikken.
    /// </summary>
    public static void ToonActie(
        Form eigenaar, string tekst, string actieTekst, Action actie, string glyph = "") =>
        Maak(eigenaar, tekst, glyph, actie, actieTekst, blijvend: true);

    private static void Maak(
        Form eigenaar, string tekst, string glyph, Action? onUndo, string actieTekst, bool blijvend)
    {
        if (eigenaar.IsDisposed)
        {
            return;
        }
        // Vluchtige meldingen verdringen elkaar zoals altijd, maar een blijvende actie-toast
        // blijft staan: nieuwe meldingen komen er dan boven te hangen. Een nieuwe blijvende
        // toast vervangt wel alles (twee wachtende acties tegelijk wordt onoverzichtelijk).
        foreach (var oud in eigenaar.Controls.OfType<Toast>().ToList())
        {
            if (blijvend || !oud._blijvend)
            {
                oud.Verwijder();
            }
        }
        Registreer(tekst);
        var toast = new Toast(tekst, glyph, onUndo, actieTekst, blijvend)
        {
            _stapelOffset = eigenaar.Controls.OfType<Toast>().Sum(t => t.Height + 8),
        };
        eigenaar.Controls.Add(toast);
        toast.BringToFront();
        toast.Start(eigenaar);
    }

    private void Start(Form eigenaar)
    {
        const int marge = 18;
        Left = eigenaar.ClientSize.Width - Width - marge;
        _doelTop = eigenaar.ClientSize.Height - Height - marge;
        _y = eigenaar.ClientSize.Height + 8; // net onder de rand beginnen
        Top = (int)_y;
        // Bewust geen Anchor: de positie wordt per tik herrekend; een anker zou
        // tijdens de animatie met de handmatige Top-updates vechten.
        _timer.Tick += (_, _) => Stap();
        _timer.Start();
    }

    private void Stap()
    {
        // Harde grens (~10 s, blijvende toasts ~10 min): wat er ook gebeurt (resize,
        // DPI-wissel, vastgelopen animatie), de toast blijft nooit eeuwig hangen.
        if (Parent is null || ++_ticks > (_blijvend ? 43000 : 800))
        {
            Verwijder();
            return;
        }
        const int marge = 18;
        Left = Parent.ClientSize.Width - Width - marge;
        _doelTop = Parent.ClientSize.Height - Height - marge - _stapelOffset; // volgt het venster
        switch (_fase)
        {
            case 0:
                _y += (_doelTop - _y) * 0.28f;
                Top = (int)_y;
                if (Math.Abs(_y - _doelTop) < 1f)
                {
                    Top = _doelTop;
                    _fase = 1;
                }
                break;
            case 1:
                if (!_blijvend && ++_wacht > _toonTicks)
                {
                    _fase = 2;
                }
                break;
            case 2:
                _y += Math.Max(2f, (_y - _doelTop) * 0.35f + 2f);
                Top = (int)_y;
                if (Top > Parent.ClientSize.Height)
                {
                    Verwijder();
                }
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_onUndo is not null && _undoRect.Contains(e.Location))
        {
            var actie = _onUndo;
            Verwijder();
            actie();
            return;
        }
        // Klik naast de actie: wegklikken. Zo raak je een blijvende toast ook kwijt
        // zonder de actie uit te voeren.
        Verwijder();
    }

    private void Verwijder()
    {
        _timer.Stop();
        Parent?.Controls.Remove(this);
        Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedPath(rect, 9))
        {
            using var vlak = new SolidBrush(Theme.Card);
            g.FillPath(vlak, path);
            using var pen = new Pen(Theme.BorderLight);
            g.DrawPath(pen, path);
        }
        // Accentbalkje links
        using (var accent = new SolidBrush(Theme.Accent))
        using (var balk = Theme.RoundedPath(new Rectangle(6, 10, 3, Height - 20), 2))
        {
            g.FillPath(accent, balk);
        }

        var tekstLinks = 20;
        if (_glyph.Length > 0)
        {
            TextRenderer.DrawText(g, _glyph, Theme.IconFont,
                new Rectangle(18, 0, 26, Height), Theme.AccentHover,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            tekstLinks = 46;
        }
        var undoBreedte = 0;
        if (_onUndo is not null)
        {
            var uMaat = TextRenderer.MeasureText(_actieTekst, Theme.SemiBold);
            undoBreedte = uMaat.Width + 22;
            _undoRect = new Rectangle(Width - undoBreedte, 0, undoBreedte, Height);
            TextRenderer.DrawText(g, _actieTekst, Theme.SemiBold, _undoRect, Theme.AccentHover,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(tekstLinks, 0, Width - tekstLinks - 10 - undoBreedte, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
