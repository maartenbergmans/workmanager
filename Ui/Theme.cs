using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WorkManager;

/// <summary>
/// Centrale donkere huisstijl voor alle WorkManager-vensters: kleurenpalet, typografie en
/// helpers die standaardcontrols (tekstvakken, grids, menu's) in die stijl zetten.
/// Eigen controls (ModernButton, ModernGroupBox, ModernListView) tekenen zichzelf.
/// </summary>
public static class Theme
{
    // ---------- Palet ----------
    // Alle kleuren komen uit het gekozen thema (zie Themas.cs). Ze zijn properties en geen
    // velden, zodat een ander thema meteen overal doorwerkt: elk zelfgetekend control leest
    // deze waarden op het moment dat het tekent.

    /// <summary>Het actieve kleurenschema; wisselen gaat via <see cref="ZetThema"/>.</summary>
    public static ThemaPalet Palet { get; private set; } = Themas.Laad();

    /// <summary>Wordt gemeld na een themawissel, zodat open vensters zich kunnen hertekenen.</summary>
    public static event Action? ThemaGewijzigd;

    /// <summary>
    /// Wisselt van kleurenschema, bewaart de keuze en hertekent alle open vensters. Kleuren
    /// die vensters bij hun opbouw hebben overgenomen (bv. een label dat ForeColor op Muted
    /// zette) volgen pas als dat venster opnieuw opent — vandaar ook de melding in het menu.
    /// </summary>
    public static void ZetThema(ThemaPalet palet)
    {
        var oud = Palet;
        Palet = palet;
        Themas.Bewaar(palet);

        // Vensters die al openstaan hebben kleuren uit het oude palet vastgelegd (een label
        // met ForeColor = Muted, een lijstrij in Warn, het accent van een paneel). Die
        // blijven anders staan — op een licht thema onleesbaar. Daarom zetten we elke
        // kleur die letterlijk uit het oude palet komt om naar zijn tegenhanger.
        var map = new Dictionary<Color, Color>();
        var oudeKleuren = Themas.Kleuren(oud);
        var nieuweKleuren = Themas.Kleuren(palet);
        for (var i = 0; i < oudeKleuren.Length; i++)
        {
            map[oudeKleuren[i]] = nieuweKleuren[i];
        }
        // Ook de afgeleide kleuren (selectie, hover) en de merkkleuren van berichtbronnen.
        map[Selectie(oud)] = SelectionFill;
        map[Mix(oud.Bg, oud.Donker ? Color.White : Color.Black, 0.05f)] = HoverFill;
        foreach (var merk in Merkkleuren)
        {
            map[oud.Donker ? merk : Mix(merk, Color.Black, 0.35f)] =
                palet.Donker ? merk : Mix(merk, Color.Black, 0.35f);
        }

        // Windows tekent zelf de scrollbalken, dropdownlijsten en titelbalken. Die volgen
        // de kleurmodus van de applicatie; die kantelt dus mee met licht/donker.
        if (oud.Donker != palet.Donker)
        {
            try
            {
#pragma warning disable WFO5001 // experimenteel, maar dit is precies waarvoor het dient
                Application.SetColorMode(palet.Donker ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
            }
            catch
            {
                // Lukt dit niet, dan klopt alles na een herstart alsnog.
            }
        }

        foreach (var form in Application.OpenForms.Cast<Form>().ToList())
        {
            try
            {
                Hervorm(form, map);
                Apply(form, fade: false);
                form.Invalidate(true);
            }
            catch
            {
                // Eén weerbarstig venster mag de rest niet tegenhouden.
            }
        }
        ThemaGewijzigd?.Invoke();
    }

    /// <summary>De vaste merkkleuren uit <see cref="VoorBron"/> (Gmail, Chat, WhatsApp, Teams).</summary>
    private static readonly Color[] Merkkleuren =
    {
        Color.FromArgb(240, 122, 122), Color.FromArgb(126, 217, 160),
        Color.FromArgb(103, 208, 133), Color.FromArgb(150, 143, 240),
    };

    /// <summary>
    /// Vervangt in een control (en al zijn kinderen, lijstrijen, gridcellen en menu-items)
    /// elke kleur uit het oude palet door die van het nieuwe. Alleen exacte treffers worden
    /// omgezet, dus eigen kleuren buiten het thema blijven ongemoeid.
    /// </summary>
    private static void Hervorm(Control control, Dictionary<Color, Color> map)
    {
        if (map.TryGetValue(control.ForeColor, out var voor))
        {
            control.ForeColor = voor;
        }
        if (map.TryGetValue(control.BackColor, out var achter))
        {
            control.BackColor = achter;
        }
        switch (control)
        {
            case ModernGroupBox groep when map.TryGetValue(groep.Accent, out var accent):
                groep.Accent = accent;
                break;
            case ListView lijst:
                foreach (ListViewItem item in lijst.Items)
                {
                    if (map.TryGetValue(item.ForeColor, out var itemVoor))
                    {
                        item.ForeColor = itemVoor;
                    }
                    if (map.TryGetValue(item.BackColor, out var itemAchter))
                    {
                        item.BackColor = itemAchter;
                    }
                    foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    {
                        if (map.TryGetValue(sub.ForeColor, out var subVoor))
                        {
                            sub.ForeColor = subVoor;
                        }
                        if (map.TryGetValue(sub.BackColor, out var subAchter))
                        {
                            sub.BackColor = subAchter;
                        }
                    }
                }
                break;
            case DataGridView grid:
                foreach (DataGridViewRow rij in grid.Rows)
                {
                    foreach (DataGridViewCell cel in rij.Cells)
                    {
                        if (map.TryGetValue(cel.Style.ForeColor, out var celVoor))
                        {
                            cel.Style.ForeColor = celVoor;
                        }
                        if (map.TryGetValue(cel.Style.BackColor, out var celAchter))
                        {
                            cel.Style.BackColor = celAchter;
                        }
                    }
                }
                break;
        }
        if (control.ContextMenuStrip is { } menu)
        {
            Style(menu);
        }
        foreach (Control kind in control.Controls)
        {
            Hervorm(kind, map);
        }
    }

    /// <summary>Een themakleur als CSS-hex ("#12131a"), voor de HTML-weergaves in WebView2.</summary>
    public static string Hex(Color kleur) => $"#{kleur.R:x2}{kleur.G:x2}{kleur.B:x2}";

    public static Color Bg => Palet.Bg;
    public static Color Surface => Palet.Surface;
    public static Color Card => Palet.Card;
    public static Color CardHover => Palet.CardHover;
    public static Color Field => Palet.Field;
    public static Color Border => Palet.Border;
    public static Color BorderLight => Palet.BorderLight;
    public static Color Text => Palet.Text;
    public static Color Muted => Palet.Muted;
    public static Color Accent => Palet.Accent;
    public static Color AccentHover => Palet.AccentHover;
    public static Color AccentPress => Palet.AccentPress;
    public static Color Warn => Palet.Warn;
    public static Color Success => Palet.Success;
    public static Color Danger => Palet.Danger;

    /// <summary>
    /// De tekstkleur die bovenop een accentvlak hoort (knoplabel, vinkje in een menu).
    /// Wit op een donker accent, bijna zwart op een licht accent zoals goud of turquoise —
    /// anders verdwijnt het label in de knop.
    /// </summary>
    public static Color OpAccent =>
        ThemaCheck.Contrast(Color.White, Accent) >= ThemaCheck.Contrast(Color.FromArgb(16, 16, 20), Accent)
            ? Color.White
            : Color.FromArgb(16, 16, 20);

    /// <summary>
    /// Selectiekleur voor lijsten en grids: het accent licht door de achtergrond gemengd.
    /// Bewust terughoudend — op een sterk gekleurde rij werd gekleurde tekst (klantkleuren,
    /// "urgent") onleesbaar. Het accentbalkje links markeert de selectie verder.
    /// </summary>
    public static Color SelectionFill => Selectie(Palet);

    /// <summary>De selectiekleur van een willekeurig palet (ook voor de contrastcontrole).</summary>
    public static Color Selectie(ThemaPalet p) =>
        Mix(p.Bg, p.Accent, p.Donker ? 0.17f : 0.10f);

    /// <summary>Hover-vulling voor lijstrijen: net iets van de achtergrond af.</summary>
    public static Color HoverFill => Mix(Bg, Palet.Donker ? Color.White : Color.Black, 0.05f);

    // ---------- Kleur met betekenis ----------
    // Elke klant en elke berichtbron heeft een eigen tint, zodat je in een lijst met één
    // oogopslag ziet waar iets bij hoort. Per thema afgestemd: op wit moeten ze dieper zijn
    // dan op zwart om leesbaar te blijven.

    public static Color KlantCed => Palet.KlantCed;
    public static Color KlantAqurat => Palet.KlantAqurat;
    public static Color KlantRadiology => Palet.KlantRadiology;
    public static Color KlantUrbanIt => Palet.KlantUrbanIt;
    public static Color KlantPrive => Palet.KlantPrive;
    public static Color KlantLauryssens => Palet.KlantLauryssens;

    /// <summary>De tint die bij een klant/categorie hoort; gedempt grijs als hij onbekend is.</summary>
    public static Color VoorKlant(string categorie) => categorie.Trim().ToLowerInvariant() switch
    {
        "ced" => KlantCed,
        "aqurat" => KlantAqurat,
        "radiologypartners" or "radiology partners" => KlantRadiology,
        "urban it" or "urbanit" => KlantUrbanIt,
        var l when l.StartsWith("lauryssens", StringComparison.Ordinal) => KlantLauryssens,
        "privé" or "prive" => KlantPrive,
        // Asana had een vaste roze merkkleur; die vloekte in een palet als 007. Nu een
        // tint uit het thema zelf, zodat hij overal past.
        "asana" => Mix(Palet.KlantAqurat, Palet.Accent, 0.4f),
        _ => Muted,
    };

    /// <summary>De herkenningskleur van een berichtbron (Gmail, Chat, WhatsApp, Teams, Outlook).</summary>
    public static Color VoorBron(string bron) => bron.Trim().ToLowerInvariant() switch
    {
        // Merkkleuren, op een licht thema een slag dieper zodat ze leesbaar blijven.
        "gmail" or "mail" => Bron(Color.FromArgb(240, 122, 122)),
        "chat" or "google chat" => Bron(Color.FromArgb(126, 217, 160)),
        "whatsapp" or "wa" => Bron(Color.FromArgb(103, 208, 133)),
        "teams" => Bron(Color.FromArgb(150, 143, 240)),
        "outlook" or "ced" => KlantCed,
        _ => Muted,
    };

    /// <summary>Een vaste merkkleur passend maken bij het thema (dieper op een licht palet).</summary>
    private static Color Bron(Color merk) => Palet.Donker ? merk : Mix(merk, Color.Black, 0.35f);

    public static readonly Font BaseFont = MaakFont(9.75f, "Segoe UI Variable Text", "Segoe UI");
    public static readonly Font SemiBold = MaakFont(9.75f, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly Font CaptionFont = MaakFont(8.25f, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly Font MonoFont = MaakFont(9.75f, "Cascadia Mono", "Consolas");
    public static readonly Font MonoSmall = MaakFont(9f, "Cascadia Mono", "Consolas");
    public static readonly Font IconFont = MaakFont(10f, "Segoe Fluent Icons", "Segoe MDL2 Assets");
    public static readonly Font IconLarge = MaakFont(26f, "Segoe Fluent Icons", "Segoe MDL2 Assets");

    /// <summary>Applicatie-icoon (meegebakken als resource); venstericoon voor alle forms.</summary>
    public static readonly Icon? AppIcon = LaadAppIcon();

    private static Icon? LaadAppIcon()
    {
        try
        {
            using var stream = typeof(Theme).Assembly
                .GetManifestResourceStream("WorkManager.Assets.workmanager.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            // Zonder icoon verder; vensters vallen terug op het standaardicoon.
            return null;
        }
    }

    private static Font MaakFont(float size, params string[] namen)
    {
        foreach (var naam in namen)
        {
            var font = new Font(naam, size);
            if (string.Equals(font.Name, naam, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }
            font.Dispose();
        }
        return new Font(FontFamily.GenericSansSerif, size);
    }

    // ---------- Toepassen op een venster ----------

    /// <summary>
    /// Zet het hele venster in de donkere stijl: achtergrond, lettertype, titelbalk en alle
    /// standaardcontrols (recursief). Aanroepen als laatste stap in de constructor.
    /// Vensters met een WebView2 geven <paramref name="fade"/> = false mee: een venster met
    /// Opacity-animatie wordt gelaagd en daar rendert WebView2 niet betrouwbaar in.
    /// </summary>
    public static void Apply(Form form, bool fade = true)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = BaseFont;
        if (AppIcon is not null)
        {
            form.Icon = AppIcon;
        }
        DonkereTitelbalk(form);
        StyleChildren(form.Controls);
        if (fade)
        {
            FadeIn(form);
        }
    }

    private static void StyleChildren(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            switch (control)
            {
                case ModernButton or ModernListView:
                    break; // tekenen zichzelf
                case TextBox tb:
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    if (tb.Multiline && tb.ReadOnly)
                    {
                        // Logvensters: rustig, monospaced, gedempt.
                        tb.BackColor = Bg;
                        tb.ForeColor = Muted;
                        tb.Font = MonoSmall;
                    }
                    else
                    {
                        tb.BackColor = Field;
                        tb.ForeColor = Text;
                    }
                    DarkScrollbars(tb);
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Field;
                    combo.ForeColor = Text;
                    DarkTheme(combo, Palet.Donker ? "DarkMode_CFD" : "CFD");
                    break;
                case NumericUpDown num:
                    num.BorderStyle = BorderStyle.FixedSingle;
                    num.BackColor = Field;
                    num.ForeColor = Text;
                    break;
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                case CheckBox cb:
                    cb.ForeColor = Text;
                    break;
                case RadioButton rb:
                    rb.ForeColor = Text;
                    break;
                case Label label:
                    if (label.ForeColor == SystemColors.ControlText || label.ForeColor.IsSystemColor)
                    {
                        label.ForeColor = Text;
                    }
                    break;
                case ListView lv:
                    lv.BackColor = Bg;
                    lv.ForeColor = Text;
                    DarkScrollbars(lv);
                    break;
                case Button knop:
                    // Vangnet voor een gewone Button die geen ModernButton werd.
                    knop.FlatStyle = FlatStyle.Flat;
                    knop.BackColor = Card;
                    knop.ForeColor = Text;
                    knop.FlatAppearance.BorderColor = Border;
                    knop.FlatAppearance.MouseOverBackColor = CardHover;
                    break;
            }
            if (control.HasChildren)
            {
                StyleChildren(control.Controls);
            }
        }
    }

    /// <summary>
    /// Laat Esc het venster sluiten — voor vensters zonder Annuleren-knop, zodat de
    /// Esc-conventie overal geldt. Niet gebruiken op vensters waar Esc al iets anders doet.
    /// </summary>
    public static void EscSluit(Form form)
    {
        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                form.Close();
            }
        };
    }

    // ---------- Bouwstenen ----------

    /// <summary>Stijlt een werkbalkpaneel: iets lichter vlak met een dunne rand eronder.</summary>
    public static void AsToolbar(FlowLayoutPanel bar)
    {
        bar.BackColor = Surface;
        bar.Height = 48;
        // Past de rij niet (extra knoppen zoals "Facturen goedkeuren…" op woensdag), dan
        // wikkelt de balk naar een tweede regel en groeit hij mee in plaats van dat de
        // knoppen over elkaar heen geklemd worden.
        bar.AutoSize = true;
        bar.MinimumSize = new Size(0, 48);
        bar.Padding = new Padding(10, 9, 10, 0);
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            // Het themamotief rechts in de werkbalk: één vast, gedempt beeldmerk dat elk
            // venster van de app meekleurt met het gekozen thema. Knoppen tekenen erover,
            // dus het valt alleen op waar de balk toch leeg is.
            var maat = Math.Min(bar.Height - 8, 56);
            if (maat >= 40 && bar.Width > 360)
            {
                ThemaEmbleem.Teken(e.Graphics,
                    new Rectangle(bar.Width - maat - 14, (bar.Height - maat) / 2, maat, maat), 0.30f, Surface);
            }
        };
        bar.Resize += (_, _) => bar.Invalidate();
    }

    /// <summary>Zet een statuslabel in gedempte kleur, netjes uitgelijnd met werkbalkknoppen.</summary>
    public static void AsStatus(Label label)
    {
        label.ForeColor = Muted;
        label.Padding = new Padding(12, 7, 0, 0);
    }

    /// <summary>Donkere stijl + eigen renderer (afgeronde hover, accentvinkjes) voor menu's.</summary>
    public static void Style(ContextMenuStrip menu)
    {
        StyleDropDown(menu);
    }

    /// <summary>
    /// Zet een menu én al zijn submenu's in de huisstijl. Submenu's zijn aparte ToolStrips:
    /// zonder deze recursie vielen ze terug op de Windows-renderer terwijl ze de tekstkleur
    /// van het hoofdmenu erfden — op een licht thema gaf dat lichte tekst op een witte
    /// achtergrond. Items die pas bij het openen worden bijgemaakt, worden opgevangen door
    /// de globale renderer (zie <see cref="ZetStandaardRenderer"/>).
    /// </summary>
    private static void StyleDropDown(ToolStripDropDown menu)
    {
        menu.Renderer = SleekMenuRenderer.Instance;
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = BaseFont;
        if (menu is ToolStripDropDownMenu dropdown)
        {
            dropdown.ShowImageMargin = true;
        }
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            item.ForeColor = Text;
            if (item.HasDropDownItems)
            {
                StyleDropDown(item.DropDown);
            }
            // Submenu's die pas bij het openen gevuld worden ook meepakken.
            item.DropDownOpening += (_, _) => StyleDropDown(item.DropDown);
        }
    }

    /// <summary>
    /// Maakt onze menurenderer de standaard voor élk menu in de applicatie, ook voor menu's
    /// die ergens los aangemaakt worden of pas na een themawissel opengaan.
    /// </summary>
    public static void ZetStandaardRenderer() =>
        ToolStripManager.Renderer = SleekMenuRenderer.Instance;

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Bg;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Border;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        g.RowHeadersDefaultCellStyle.BackColor = Surface;
        g.RowHeadersDefaultCellStyle.SelectionBackColor = SelectionFill;
        g.DefaultCellStyle.BackColor = Bg;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = SelectionFill;
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.RowTemplate.Height = 30;
        DarkScrollbars(g);
    }

    // ---------- Grafische helpers ----------

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width < 2 || r.Height < 2)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Sterrenweergave voor een prioriteit (0 = hoog ★★★, 1 = normaal ★★, 2 = laag ★),
    /// met bijpassende kleur voor in lijsten en menu's.
    /// </summary>
    public static (string Tekst, Color Kleur) PrioSterren(int prioriteit) => prioriteit switch
    {
        0 => ("★★★", Danger),
        2 => ("★", Color.FromArgb(105, 105, 128)),
        _ => ("★★", Muted),
    };

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    // ---------- Vensterchroom ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? subIdList);

    private const int ImmersiveDarkMode = 20;

    private static void DonkereTitelbalk(Form form)
    {
        void Zet()
        {
            // Volgt het thema: op een licht palet hoort ook de titelbalk licht te zijn.
            var aan = Palet.Donker ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, ImmersiveDarkMode, ref aan, sizeof(int));
        }
        if (form.IsHandleCreated)
        {
            Zet();
        }
        else
        {
            form.HandleCreated += (_, _) => Zet();
        }
    }

    /// <summary>Scrollbalken in de tint van het thema (donker of gewoon Explorer-licht).</summary>
    public static void DarkScrollbars(Control control) =>
        DarkTheme(control, Palet.Donker ? "DarkMode_Explorer" : "Explorer");

    private static void DarkTheme(Control control, string thema)
    {
        void Zet() => _ = SetWindowTheme(control.Handle, thema, null);
        if (control.IsHandleCreated)
        {
            Zet();
        }
        else
        {
            control.HandleCreated += (_, _) => Zet();
        }
    }

    private static void FadeIn(Form form)
    {
        form.Opacity = 0;
        form.Shown += (_, _) =>
        {
            // Fade in en schuif tegelijk een stukje omhoog naar de eindpositie.
            var doelTop = form.Top;
            var afstand = 18f;
            form.Top = doelTop + (int)afstand;
            var timer = new System.Windows.Forms.Timer { Interval = 12 };
            timer.Tick += (_, _) =>
            {
                form.Opacity = Math.Min(1.0, form.Opacity + 0.15);
                afstand *= 0.72f;
                form.Top = doelTop + (int)afstand;
                if (form.Opacity >= 1.0 && afstand < 1f)
                {
                    form.Top = doelTop;
                    timer.Stop();
                    timer.Dispose();
                }
            };
            timer.Start();
        };
    }
}

/// <summary>
/// Donkere menu-renderer: vlak oppervlak, afgeronde hover-markering en accentkleurige vinkjes.
/// Gebruikt voor alle contextmenu's en het traymenu.
/// </summary>
public sealed class SleekMenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly SleekMenuRenderer Instance = new();

    private SleekMenuRenderer() : base(new SleekColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        if (e.Item.Selected && e.Item.Enabled)
        {
            using var brush = new SolidBrush(Theme.CardHover);
            using var path = Theme.RoundedPath(rect, 5);
            g.FillPath(brush, path);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        r.Inflate(-1, -1);
        using var path = Theme.RoundedPath(r, 4);
        if (e.Item is ToolStripMenuItem { Image: not null })
        {
            // Item met eigen icoon: alleen een accentrand als "actief"-markering.
            using var pen = new Pen(Theme.Accent, 1.6f);
            g.DrawPath(pen, path);
            return;
        }
        using var fill = new SolidBrush(Theme.Accent);
        g.FillPath(fill, path);
        using var check = new Pen(Theme.OpAccent, 1.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var m = r;
        g.DrawLines(check, new[]
        {
            new PointF(m.X + m.Width * 0.26f, m.Y + m.Height * 0.53f),
            new PointF(m.X + m.Width * 0.44f, m.Y + m.Height * 0.72f),
            new PointF(m.X + m.Width * 0.75f, m.Y + m.Height * 0.30f),
        });
    }

    private sealed class SleekColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
