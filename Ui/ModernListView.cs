using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Zelf getekende lijst (Details-weergave) in de huisstijl: donkere vlakke koppen,
/// ruimere rijen, hover-markering en een afgeronde accentselectie met accentbalkje links.
/// Vinkjes worden als moderne afgeronde checkboxes getekend. Drop-in vervanger voor ListView.
/// </summary>
public class ModernListView : ListView
{
    private readonly ImageList _rijHoogte = new() { ImageSize = new Size(1, 26) };
    private int _hot = -1;

    /// <summary>Tekst die gecentreerd getoond wordt zolang de lijst leeg is (leeg = niet tonen).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string LegeTekst { get; set; } = "";

    /// <summary>
    /// Soort lijst ("berichten", "taken", "meetings", "deadline"): bepaalt welk sfeersilhouet
    /// er achter de lege staat komt, zodat niet elk leeg paneel er hetzelfde uitziet.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string LeegSoort { get; set; } = "";

    /// <summary>Fluent-icoon boven de lege-staat-tekst.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string LeegGlyph { get; set; } = "";

    /// <summary>
    /// Bepaalt per rij of er een checkbox getekend wordt (null = altijd). Rijen zonder
    /// checkbox houden dezelfde tekstinspringing zodat de kolom uitgelijnd blijft.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<ListViewItem, bool>? HeeftCheckbox { get; set; }

    /// <summary>
    /// Kolomindex die prioriteitssterren toont (celtekst "★", "★★" of "★★★"). Die cel wordt
    /// als drie klikbare sterposities getekend (gevuld + hol); klikken op positie 1–3 vuurt
    /// <see cref="SterGeklikt"/> met het gekozen aantal sterren. -1 = uit.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SterrenKolom { get; set; } = -1;

    /// <summary>Klik op een sterpositie: (rij, aantal sterren 1–3).</summary>
    public event Action<ListViewItem, int>? SterGeklikt;

    /// <summary>
    /// Kolomindex die een aantal met klikbare − en + toont (celtekst = het getal). De cel
    /// wordt getekend als "− n +"; klikken op − of + vuurt <see cref="PlusMinGeklikt"/> met
    /// -1 of +1. Cellen zonder getal (bv. kopregels) blijven gewone tekst. -1 = uit.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int PlusMinKolom { get; set; } = -1;

    /// <summary>Klik op − of + in de <see cref="PlusMinKolom"/>: (rij, -1 of +1).</summary>
    public event Action<ListViewItem, int>? PlusMinGeklikt;

    /// <summary>Breedte (px) van de − en +-klikzones in de <see cref="PlusMinKolom"/>.</summary>
    private const int PlusMinZone = 22;

    /// <summary>Levert per rij een icoon voor in de eerste kolom (null = geen icoon). Zijde = <see cref="IcoonGrootte"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<ListViewItem, Image?>? RijIcoon { get; set; }

    /// <summary>Zijde (px) van het <see cref="RijIcoon"/> in de eerste kolom.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int IcoonGrootte { get; set; } = 18;

    /// <summary>Rijhoogte in pixels (via de onzichtbare hoogte-ImageList).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RijHoogte
    {
        get => _rijHoogte.ImageSize.Height;
        set => _rijHoogte.ImageSize = new Size(1, Math.Clamp(value, 8, 256));
    }

    public ModernListView()
    {
        View = View.Details;
        OwnerDraw = true;
        FullRowSelect = true;
        BorderStyle = BorderStyle.None;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        SmallImageList = _rijHoogte;

        DrawColumnHeader += TekenKop;
        DrawItem += TekenRij;
        DrawSubItem += TekenCel;

        // Donkere scrollbalken en groepskoppen via het Explorer-donkerthema.
        Theme.DarkScrollbars(this);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Native dubbele buffering van de ListView zelf: zonder deze stijl tekent hij elke
        // rij rechtstreeks op het scherm, wat bij hoveren flikkert of donkere vegen geeft.
        const int LvmSetExtendedStyle = 0x1036;
        const int LvsExDoubleBuffer = 0x00010000;
        SendMessage(Handle, LvmSetExtendedStyle, (IntPtr)LvsExDoubleBuffer, (IntPtr)LvsExDoubleBuffer);
    }

    private void TekenKop(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics!;
        using (var vlak = new SolidBrush(Theme.Surface))
        {
            g.FillRectangle(vlak, e.Bounds);
        }
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        var tekstRect = e.Bounds;
        tekstRect.Inflate(-8, 0);
        var vlaggen = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
            (e.Header?.TextAlign switch
            {
                HorizontalAlignment.Right => TextFormatFlags.Right,
                HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                _ => TextFormatFlags.Left,
            });
        TextRenderer.DrawText(g, e.Header?.Text.ToUpperInvariant(), Theme.CaptionFont, tekstRect, Theme.Muted, vlaggen);
    }

    /// <summary>
    /// Zebra-kleur voor oneven rijen: net iets van de achtergrond af. Bewust een property en
    /// geen static readonly-veld: dat werd één keer bij het opstarten berekend en bleef bij
    /// een themawissel op de oude (donkere) waarde staan.
    /// </summary>
    private static Color RijAltKleur => Theme.Palet.Donker
        ? Color.FromArgb(Math.Min(255, Theme.Bg.R + 6), Math.Min(255, Theme.Bg.G + 6),
            Math.Min(255, Theme.Bg.B + 9))
        : Color.FromArgb(Math.Max(0, Theme.Bg.R - 5), Math.Max(0, Theme.Bg.G - 5),
            Math.Max(0, Theme.Bg.B - 4));

    private void TekenRij(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rij = e.Bounds;
        // Subtiele zebrastrepen: oneven rijen een tikje lichter voor rustiger scannen.
        using (var achtergrond = new SolidBrush(e.ItemIndex % 2 == 1 ? RijAltKleur : Theme.Bg))
        {
            g.FillRectangle(achtergrond, rij);
        }

        var binnen = new Rectangle(rij.X + 2, rij.Y + 1, Math.Max(4, rij.Width - 5), rij.Height - 2);
        if (e.Item.Selected)
        {
            using var vlak = new SolidBrush(Theme.SelectionFill);
            using var path = Theme.RoundedPath(binnen, 6);
            g.FillPath(vlak, path);

            // Accentbalkje links als selectiemarkering.
            var balk = new Rectangle(binnen.X + 1, binnen.Y + 4, 3, binnen.Height - 8);
            using var accent = new SolidBrush(Theme.Accent);
            using var balkPath = Theme.RoundedPath(balk, 2);
            g.FillPath(accent, balkPath);
        }
        else if (e.ItemIndex == _hot)
        {
            using var vlak = new SolidBrush(Theme.HoverFill);
            using var path = Theme.RoundedPath(binnen, 6);
            g.FillPath(vlak, path);
        }

        // Subtiele scheidingslijn tussen rijen.
        using (var lijn = new Pen(Color.FromArgb(14, Theme.Palet.Donker ? Color.White : Color.Black)))
        {
            g.DrawLine(lijn, rij.X + 8, rij.Bottom - 1, rij.Right - 8, rij.Bottom - 1);
        }

        // Álle kolommen hier meteen meetekenen: bij hover- en tooltip-hertekeningen slaat de
        // native ListView het DrawSubItem-event soms over — niet alleen voor kolom 0, maar ook
        // voor bv. de onderwerp-kolom, waardoor die cel leeg (achtergrondkleur) bleef en het
        // onderwerp enkel nog in de tooltip te lezen was. DrawItem vuurt wél betrouwbaar één
        // keer per rij, dus tekenen we hier elke cel; DrawSubItem blijft als extra ververser.
        var celX = rij.X;
        for (var k = 0; k < Columns.Count; k++)
        {
            TekenCelInhoud(g, e.Item, k, new Rectangle(celX, rij.Y, Columns[k].Width, rij.Height));
            celX += Columns[k].Width;
        }
    }

    private void TekenCel(object? sender, DrawListViewSubItemEventArgs e)
    {
        // Alle cellen worden in TekenRij (DrawItem) getekend, dat betrouwbaar per rij vuurt.
        // Dit event laten we bewust leeg: het blijft geabonneerd zodat de native ListView geen
        // standaard (systeemkleur) subitem tekent, maar we tekenen hier niets dubbel.
    }

    private void TekenCelInhoud(Graphics g, ListViewItem item, int columnIndex, Rectangle rect)
    {
        if (columnIndex >= item.SubItems.Count)
        {
            return;
        }
        var sub = item.SubItems[columnIndex];
        var links = rect.X + 8;

        if (columnIndex == 0 && CheckBoxes)
        {
            if (HeeftCheckbox?.Invoke(item) != false)
            {
                TekenCheckbox(g, new Rectangle(rect.X + 8, rect.Y + (rect.Height - 15) / 2, 15, 15), item.Checked);
            }
            links = rect.X + 31;
        }
        if (columnIndex == 0 && RijIcoon?.Invoke(item) is { } icoon)
        {
            var maat = IcoonGrootte;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icoon, new Rectangle(links, rect.Y + (rect.Height - maat) / 2, maat, maat));
            links += maat + 6;
        }

        var kleur = item.UseItemStyleForSubItems ? item.ForeColor : sub.ForeColor;
        if (kleur.IsSystemColor || kleur.ToArgb() == Color.Black.ToArgb() || kleur.IsEmpty)
        {
            kleur = Theme.Text;
        }
        var font = item.UseItemStyleForSubItems ? item.Font : sub.Font;

        // Aantal-kolom: "− n +" met klikbare mini-knoppen, zodat je een aantal aanpast
        // zonder eerst de rij te selecteren en naar het veld onderaan te gaan.
        if (columnIndex == PlusMinKolom && int.TryParse(sub.Text, out _))
        {
            var midden = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            var knopHoogte = Math.Min(20, rect.Height - 6);
            var knopY = rect.Y + (rect.Height - knopHoogte) / 2;
            foreach (var (x, glyph) in new[]
            {
                (rect.X + 4, "−"),
                (rect.Right - PlusMinZone - 4, "+"),
            })
            {
                var knop = new Rectangle(x, knopY, PlusMinZone, knopHoogte);
                using (var vlak = new SolidBrush(Theme.Surface))
                using (var rand = new Pen(Theme.Border))
                using (var path = Theme.RoundedPath(knop, 5))
                {
                    g.FillPath(vlak, path);
                    g.DrawPath(rand, path);
                }
                TextRenderer.DrawText(g, glyph, Font, knop, Theme.Muted, midden);
            }
            TextRenderer.DrawText(g, sub.Text, font ?? Font,
                Rectangle.FromLTRB(rect.X + 4 + PlusMinZone, rect.Y, rect.Right - PlusMinZone - 4, rect.Bottom),
                kleur, midden);
            return;
        }

        // Sterrenkolom: drie klikbare posities, gevuld tot het huidige niveau.
        if (columnIndex == SterrenKolom && sub.Text.Length > 0 && sub.Text[0] == '★')
        {
            var gevuld = sub.Text.Length;
            var breedte = SterBreedte();
            for (var i = 0; i < 3; i++)
            {
                TextRenderer.DrawText(g, i < gevuld ? "★" : "☆", Font,
                    new Rectangle(links + i * breedte, rect.Y, breedte + 4, rect.Height),
                    i < gevuld ? kleur : Theme.Mix(Theme.Bg, Theme.Muted, 0.45f),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            return;
        }

        var tekstRect = Rectangle.FromLTRB(links, rect.Y, rect.Right - 6, rect.Bottom);
        var vlaggen = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            (Columns.Count > columnIndex && Columns[columnIndex].TextAlign == HorizontalAlignment.Right
                ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(g, sub.Text, font ?? Font, tekstRect, kleur, vlaggen);
    }

    private static void TekenCheckbox(Graphics g, Rectangle r, bool aan)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(r, 4);
        if (aan)
        {
            using var vlak = new SolidBrush(Theme.Accent);
            g.FillPath(vlak, path);
            using var pen = new Pen(Theme.OpAccent, 1.7f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLines(pen, new[]
            {
                new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.55f),
                new PointF(r.X + r.Width * 0.44f, r.Y + r.Height * 0.74f),
                new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.28f),
            });
        }
        else
        {
            using var vlak = new SolidBrush(Theme.Field);
            g.FillPath(vlak, path);
            using var pen = new Pen(Theme.BorderLight);
            g.DrawPath(pen, path);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = VeiligeHitTest(e.Location)?.Item?.Index ?? -1;
        if (index != _hot)
        {
            WisselHot(index);
        }
        Cursor = SterOnderMuis(e.Location) is not null ? Cursors.Hand : Cursors.Default;
    }

    /// <summary>
    /// HitTest die niet crasht: de ingebouwde ListView.HitTest gooit een
    /// ArgumentOutOfRangeException (index -1) wanneer de muis beweegt terwijl er net
    /// rijen verwijderd zijn — precies wat er na archiveren gebeurt.
    /// </summary>
    private ListViewHitTestInfo? VeiligeHitTest(Point locatie)
    {
        try
        {
            return HitTest(locatie);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (SterOnderMuis(e.Location) is { } ster)
        {
            SterGeklikt?.Invoke(ster.Item, ster.Aantal);
        }
        if (PlusMinOnderMuis(e.Location) is { } plusmin)
        {
            PlusMinGeklikt?.Invoke(plusmin.Item, plusmin.Delta);
        }
    }

    /// <summary>Bepaalt of de muis boven de − of + van de aantal-kolom staat.</summary>
    private (ListViewItem Item, int Delta)? PlusMinOnderMuis(Point locatie)
    {
        if (PlusMinKolom < 0)
        {
            return null;
        }
        if (VeiligeHitTest(locatie) is not { } hit ||
            hit.Item is not { } item || hit.SubItem is not { } sub ||
            item.SubItems.IndexOf(sub) != PlusMinKolom || !int.TryParse(sub.Text, out _))
        {
            return null;
        }
        var cel = sub.Bounds;
        if (locatie.X >= cel.X + 4 && locatie.X < cel.X + 4 + PlusMinZone)
        {
            return (item, -1);
        }
        if (locatie.X >= cel.Right - PlusMinZone - 4 && locatie.X < cel.Right - 4)
        {
            return (item, +1);
        }
        return null;
    }

    /// <summary>Bepaalt of de muis boven een sterpositie in de sterrenkolom staat.</summary>
    private (ListViewItem Item, int Aantal)? SterOnderMuis(Point locatie)
    {
        if (SterrenKolom < 0)
        {
            return null;
        }
        if (VeiligeHitTest(locatie) is not { } hit ||
            hit.Item is not { } item || hit.SubItem is not { } sub ||
            item.SubItems.IndexOf(sub) != SterrenKolom ||
            sub.Text.Length == 0 || sub.Text[0] != '★')
        {
            return null;
        }
        var breedte = SterBreedte();
        var offset = locatie.X - sub.Bounds.X - 8;
        if (offset < 0 || offset >= breedte * 3)
        {
            return null;
        }
        return (item, Math.Clamp(offset / breedte + 1, 1, 3));
    }

    private int SterBreedte() => TextRenderer.MeasureText("★", Font).Width - 4;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        WisselHot(-1);
    }

    private void WisselHot(int index)
    {
        var vorige = _hot;
        _hot = index;
        InvalidateRij(vorige);
        InvalidateRij(_hot);
        Update(); // direct hertekenen: anders blijft bij snelle muisbewegingen een halve rij donker staan
    }

    /// <summary>
    /// Hertekent één rij volledig via RedrawItems (zodat álle cellen — ook de eerste kolom
    /// met de afzender — opnieuw getekend worden) plus de volle rijbreedte.
    /// </summary>
    private void InvalidateRij(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            RedrawItems(index, index, invalidateOnly: true);
            var b = Items[index].Bounds;
            Invalidate(new Rectangle(0, b.Y, ClientSize.Width, b.Height));
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Lege-staat na de gewone paint tekenen (ListView kent zelf geen Paint-event).
        const int WmPaint = 0x000F;
        if (m.Msg == WmPaint && Items.Count == 0 && LegeTekst.Length > 0 && !DesignMode)
        {
            using var g = Graphics.FromHwnd(Handle);
            // Sfeersilhouet van het thema, heel gedempt achter de tekst (007 een martiniglas,
            // Zomer een palmboom). Alleen als de lijst er ruimte voor heeft.
            if (ThemaStem.LeegSilhouet(LeegSoort) is { Length: > 0 } silhouet &&
                ClientSize.Height > 90 && ClientSize.Width > 200)
            {
                using var groot = new Font("Segoe UI Emoji", Math.Min(84f, ClientSize.Height * 0.5f));
                TextRenderer.DrawText(g, silhouet, groot,
                    new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                    Theme.Mix(Theme.Bg, Theme.Muted, 0.18f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix);
            }
            // Het hele blok (icoon + tekst) als geheel verticaal centreren; met de tekst
            // óp het midden en het icoon erboven hing alles zichtbaar te hoog, zeker in
            // lage panelen zoals de takenlijst.
            var blokHoogte = LeegGlyph.Length > 0 ? 74 : 30;
            var top = Math.Max(0, (ClientSize.Height - blokHoogte) / 2);
            if (LeegGlyph.Length > 0)
            {
                TextRenderer.DrawText(g, LeegGlyph, Theme.IconLarge,
                    new Rectangle(0, top, ClientSize.Width, 44),
                    // Gedempt maar mee met het thema (op wit moet dit donkerder zijn).
                    Theme.Mix(Theme.Bg, Theme.Muted, 0.55f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            TextRenderer.DrawText(g, LegeTekst, Theme.BaseFont,
                new Rectangle(0, top + blokHoogte - 30, ClientSize.Width, 30), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rijHoogte.Dispose();
        }
        base.Dispose(disposing);
    }
}
