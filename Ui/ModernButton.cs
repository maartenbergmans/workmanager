using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WorkManager;

public enum ButtonKind
{
    /// <summary>Gewone knop: donker vlak met rand.</summary>
    Normal,
    /// <summary>Primaire actie: gevuld in de accentkleur.</summary>
    Accent,
    /// <summary>Alarmactie die nú aandacht vraagt: gevuld in de dangerkleur (rood).</summary>
    Danger,
}

/// <summary>
/// Zelf getekende knop in de huisstijl: afgeronde hoeken, vloeiende hover-animatie
/// (kleur schuift geleidelijk naar de hoverstaat) en een accentvariant voor primaire acties.
/// Stamt bewust af van Control (niet Button): de dark-modus van WinForms tekent Buttons
/// native en zou onze eigen tekening overslaan. IButtonControl zorgt voor het gewone
/// dialooggedrag (AcceptButton/CancelButton/DialogResult).
/// </summary>
public class ModernButton : Control, IButtonControl
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 16 };
    private float _hover;      // 0 = rust, 1 = volledig hover
    private float _hoverDoel;
    private float _spinHoek;
    private bool _ingedrukt;
    private bool _isDefault;
    private bool _bezig;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; } = ButtonKind.Normal;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 7;

    /// <summary>Optioneel Fluent-icoon (Segoe Fluent Icons-teken) links van de tekst.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    /// <summary>
    /// Toont een draaiende spinner in plaats van het icoon zolang de actie loopt. De knop
    /// blijft in zijn "actieve" kleuren, ook al is hij intussen uitgeschakeld.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Bezig
    {
        get => _bezig;
        set
        {
            if (_bezig == value)
            {
                return;
            }
            _bezig = value;
            if (value)
            {
                _spinTimer.Start();
            }
            else
            {
                _spinTimer.Stop();
                _spinHoek = 0;
            }
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable | ControlStyles.StandardClick, true);
        Size = new Size(120, 31);
        Cursor = Cursors.Hand;
        TabStop = true;
        _timer.Tick += (_, _) =>
        {
            // Exponentieel naar het doel toe; stoppen zodra we er (bijna) zijn.
            _hover += (_hoverDoel - _hover) * 0.28f;
            if (Math.Abs(_hoverDoel - _hover) < 0.02f)
            {
                _hover = _hoverDoel;
                _timer.Stop();
            }
            Invalidate();
        };
        _spinTimer.Tick += (_, _) =>
        {
            _spinHoek = (_spinHoek + 9f) % 360f;
            Invalidate();
        };
    }

    /// <summary>
    /// Zet de breedte snug rond de inhoud (tekst + eventueel icoon), zodat de knop niet
    /// breder is dan nodig. <paramref name="dropdown"/> reserveert wat ruimte voor een ▾.
    /// </summary>
    public void KrimpNaarInhoud(bool dropdown = false)
    {
        var tekst = TextRenderer.MeasureText(Text, Theme.BaseFont).Width;
        var icoon = Glyph.Length > 0 ? TextRenderer.MeasureText(Glyph, Theme.IconFont).Width + 6 : 0;
        Width = tekst + icoon + (dropdown ? 26 : 22);
    }

    // ---------- IButtonControl ----------

    public void NotifyDefault(bool value)
    {
        if (_isDefault != value)
        {
            _isDefault = value;
            Invalidate();
        }
    }

    public void PerformClick()
    {
        if (Enabled && Visible)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        // Zelfde gedrag als Button: DialogResult doorzetten naar het venster.
        if (DialogResult != DialogResult.None && FindForm() is { } form)
        {
            form.DialogResult = DialogResult;
        }
        base.OnClick(e);
    }

    // ---------- Interactie ----------

    private void AnimeerNaar(float doel)
    {
        _hoverDoel = doel;
        if (!_timer.Enabled && !IsDisposed)
        {
            _timer.Start();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        AnimeerNaar(1f);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _ingedrukt = false;
        AnimeerNaar(0f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _ingedrukt = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _ingedrukt = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            PerformClick();
        }
        base.OnKeyDown(e);
    }

    protected override bool ProcessMnemonic(char charCode)
    {
        if (Enabled && Visible && IsMnemonic(charCode, Text))
        {
            PerformClick();
            return true;
        }
        return base.ProcessMnemonic(charCode);
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

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    // ---------- Tekenen ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        Color vlak, rand, tekst;
        if (!Enabled && !Bezig)
        {
            vlak = Theme.Surface;
            rand = Theme.Border;
            tekst = Theme.Mix(Theme.Bg, Theme.Muted, 0.55f);
        }
        else if (Kind == ButtonKind.Accent)
        {
            vlak = _ingedrukt ? Theme.AccentPress : Theme.Mix(Theme.Accent, Theme.AccentHover, _hover);
            rand = Color.Transparent;
            // Niet altijd wit: op een licht accent (goud, turquoise) leest bijna-zwart beter.
            tekst = Theme.OpAccent;
        }
        else if (Kind == ButtonKind.Danger)
        {
            vlak = _ingedrukt
                ? Theme.Mix(Theme.Danger, Color.Black, 0.25f)
                : Theme.Mix(Theme.Danger, Color.White, 0.15f * _hover);
            rand = Color.Transparent;
            tekst = Color.White;
        }
        else
        {
            vlak = _ingedrukt ? Theme.Surface : Theme.Mix(Theme.Card, Theme.CardHover, _hover);
            rand = _isDefault
                ? Theme.Mix(Theme.Accent, Theme.AccentHover, _hover)
                : Theme.Mix(Theme.Border, Theme.BorderLight, _hover);
            tekst = Theme.Text;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedPath(rect, CornerRadius))
        {
            if ((Enabled || Bezig) && Kind == ButtonKind.Accent)
            {
                // Zachte gloed rond de knop + subtiel verticaal verloop in het vlak.
                using var gloed = new Pen(Color.FromArgb((int)(50 + 40 * _hover), Theme.AccentHover), 3f);
                g.DrawPath(gloed, path);
                using var verloop = new LinearGradientBrush(
                    rect, Theme.Mix(vlak, Theme.Palet.Donker ? Color.White : Color.Black, 0.10f),
                    vlak, 90f);
                g.FillPath(verloop, path);
            }
            else
            {
                using var brush = new SolidBrush(vlak);
                g.FillPath(brush, path);
            }
            if (rand != Color.Transparent)
            {
                using var pen = new Pen(rand);
                g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(Kind == ButtonKind.Accent
                    ? Color.FromArgb(160, Theme.OpAccent) : Theme.Accent);
                var binnen = rect;
                binnen.Inflate(-2, -2);
                using var focusPath = Theme.RoundedPath(binnen, Math.Max(2, CornerRadius - 2));
                g.DrawPath(focus, focusPath);
            }
        }

        const TextFormatFlags basis = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var toonGlyph = Bezig ? Fluent.Sync : Glyph;
        if (toonGlyph.Length > 0)
        {
            // Icoon + tekst samen gecentreerd; icoon in accenttint op gewone knoppen.
            var glyphMaat = TextRenderer.MeasureText(g, toonGlyph, Theme.IconFont);
            var tekstMaat = TextRenderer.MeasureText(g, Text, Font);
            var totaal = glyphMaat.Width + 2 + tekstMaat.Width;
            var x = Math.Max(4, (Width - totaal) / 2);
            var glyphKleur = !Enabled && !Bezig ? tekst
                : Kind == ButtonKind.Accent ? Theme.OpAccent
                : Kind == ButtonKind.Danger ? Color.White
                : Theme.Mix(Theme.AccentHover, Theme.Text, 0.25f);
            if (Bezig)
            {
                // Spinner: het sync-icoon rond zijn middelpunt draaien.
                g.TranslateTransform(x + glyphMaat.Width / 2f, Height / 2f);
                g.RotateTransform(_spinHoek);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                using var brush = new SolidBrush(glyphKleur);
                g.DrawString(toonGlyph, Theme.IconFont, brush, 0f, 1f, sf);
                g.ResetTransform();
            }
            else
            {
                TextRenderer.DrawText(g, toonGlyph, Theme.IconFont,
                    new Rectangle(x, 1, glyphMaat.Width, Height - 1), glyphKleur, basis);
            }
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(x + glyphMaat.Width + 2, 0, tekstMaat.Width + 4, Height), tekst, basis);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, rect, tekst, basis | TextFormatFlags.HorizontalCenter);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _spinTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
