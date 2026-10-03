using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Korte openingsanimatie in de stijl van het kleurenschema, één keer per dag bij het
/// openen van de cockpit. 007 krijgt de gun barrel uit de filmintro (een cirkel die
/// horizontaal meeschuift en dan dichtloopt), Zomer een opkomende zon, Neon een scanlijn,
/// Espresso een oplossende koffiering. Middernacht en Daglicht slaan hem over: die thema's
/// zijn bewust rustig.
///
/// <para>Net als de confetti gebeurt dit op een klikdoorlatend overlay-venster, zodat de
/// cockpit er meteen onder bruikbaar is. Duur: ongeveer anderhalve seconde.</para>
/// </summary>
public static class ThemaIntro
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "thema-intro.json");

    /// <summary>Speelt de intro als die vandaag nog niet gespeeld is voor dit thema.</summary>
    public static void SpeelEenmaalPerDag(Form eigenaar)
    {
        var sleutel = $"{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}|{Theme.Palet.Naam}";
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) == sleutel)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(sleutel));
        }
        catch
        {
            // Zonder state hooguit één intro te veel; geen reden om te stoppen.
        }
        Speel(eigenaar);
    }

    /// <summary>Speelt de intro nu (voor tests en voor een themawissel).</summary>
    public static void Speel(Form eigenaar)
    {
        if (eigenaar.IsDisposed || NietStoren.Actief || Theme.Palet.Naam is "Middernacht" or "Daglicht")
        {
            return;
        }
        try
        {
            var overlay = new IntroVenster(eigenaar.RectangleToScreen(eigenaar.ClientRectangle));
            overlay.Show(eigenaar);
        }
        catch
        {
            // Een intro mag nooit het openen van de cockpit in de weg staan.
        }
    }

    private sealed class IntroVenster : Form
    {
        private const int Frames = 62; // ± 1 s bij 60 fps — kort en terughoudend

        /// <summary>Hoogste dekking van het hele overlay; bewust laag, het mag amper opvallen.</summary>
        private static double MaxDekking => Theme.Palet.Donker ? 0.22 : 0.13;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private int _frame;

        public IntroVenster(Rectangle schermRect)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = schermRect;
            // Bewust géén TransparencyKey: doorschijnend tekenen bovenop een magenta
            // sleutelkleur gaf een roze waas. Nu is het venster één effen vlak waarvan
            // alleen de dekking (Opacity) animeert, en tekenen we met volle kleuren.
            BackColor = Theme.Palet.Donker ? Color.FromArgb(6, 6, 8) : Color.White;
            Opacity = 0;
            DoubleBuffered = true;

            _timer.Tick += (_, _) =>
            {
                if (++_frame > Frames)
                {
                    _timer.Stop();
                    Close();
                    return;
                }
                // Snel op, even blijven, rustig weg.
                var t = _frame / (double)Frames;
                Opacity = MaxDekking * (t < 0.25 ? t / 0.25 : t < 0.55 ? 1 : 1 - (t - 0.55) / 0.45);
                Invalidate();
            };
            _timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                const int WsExTransparent = 0x20;
                const int WsExNoActivate = 0x8000000;
                cp.ExStyle |= WsExTransparent | WsExNoActivate;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var t = _frame / (float)Frames; // 0 → 1
            switch (Theme.Palet.Naam)
            {
                case "007":
                    TekenGunBarrel(g, t);
                    break;
                case "Zomer":
                    TekenZon(g, t);
                    break;
                case "Neon":
                    TekenScanlijn(g, t);
                    break;
                case "Espresso":
                    TekenKoffiering(g, t);
                    break;
                case "Godfather":
                    TekenMarionet(g, t);
                    break;
                case "Breaking Bad":
                    TekenElementen(g, t);
                    break;
                case "Zeilen":
                    TekenZeil(g, t);
                    break;
            }
        }

        /// <summary>
        /// De gun barrel, ingetogen: een dunne gouden ring die van links naar het midden
        /// schuift en dan samentrekt, met een fijn kruisdraadje. Geen zwart scherm en geen
        /// gevulde vlakken — de dekking van het hele overlay doet het werk.
        /// </summary>
        private void TekenGunBarrel(Graphics g, float t)
        {
            var midY = Height / 2f;
            var schuif = Math.Min(1f, t / 0.55f);
            var x = Width * (0.20f + 0.30f * Soepel(schuif));
            var basis = Math.Min(Width, Height) * 0.13f;
            var straal = t < 0.55f ? basis : basis * (1f - 0.75f * Soepel((t - 0.55f) / 0.45f));
            if (straal < 2)
            {
                return;
            }
            using (var pen = new Pen(Theme.Accent, 2.2f))
            {
                g.DrawEllipse(pen, x - straal, midY - straal, straal * 2, straal * 2);
            }
            using var draad = new Pen(Theme.AccentHover, 1f);
            g.DrawLine(draad, x - straal * 1.25f, midY, x - straal * 0.35f, midY);
            g.DrawLine(draad, x + straal * 0.35f, midY, x + straal * 1.25f, midY);
            g.DrawLine(draad, x, midY - straal * 1.25f, x, midY - straal * 0.35f);
            g.DrawLine(draad, x, midY + straal * 0.35f, x, midY + straal * 1.25f);
        }

        /// <summary>Zomer: een dunne zonneboog die onderaan even opkomt.</summary>
        private void TekenZon(Graphics g, float t)
        {
            var straal = Math.Min(Width, Height) * 0.14f;
            var y = Height * (1.02f - 0.28f * Soepel(Math.Min(1f, t / 0.7f)));
            var x = Width / 2f;
            using (var pen = new Pen(Theme.Accent, 2.2f))
            {
                g.DrawEllipse(pen, x - straal, y - straal, straal * 2, straal * 2);
            }
            // Een paar korte stralen, geen volle gloed.
            using var straaltje = new Pen(Theme.AccentHover, 1.4f);
            for (var hoek = 200; hoek <= 340; hoek += 20)
            {
                var rad = hoek * Math.PI / 180;
                var binnen = straal * 1.25f;
                var buiten = straal * 1.5f;
                g.DrawLine(straaltje,
                    x + (float)(Math.Cos(rad) * binnen), y + (float)(Math.Sin(rad) * binnen),
                    x + (float)(Math.Cos(rad) * buiten), y + (float)(Math.Sin(rad) * buiten));
            }
        }

        /// <summary>Neon: één dunne lichtlijn die over het scherm loopt.</summary>
        private void TekenScanlijn(Graphics g, float t)
        {
            var y = Height * Soepel(t);
            using var lijn = new Pen(Theme.Accent, 1.6f);
            g.DrawLine(lijn, 0, y, Width, y);
            using var echo = new Pen(Theme.AccentHover, 1f);
            g.DrawLine(echo, 0, y - 5, Width, y - 5);
        }

        /// <summary>Espresso: een koffiering die rustig opzwelt.</summary>
        private void TekenKoffiering(Graphics g, float t)
        {
            var straal = Math.Min(Width, Height) * (0.07f + 0.10f * Soepel(t));
            var x = Width / 2f;
            var y = Height / 2f;
            using var ring = new Pen(Theme.Accent, 3f);
            g.DrawEllipse(ring, x - straal, y - straal, straal * 2, straal * 2);
            using var binnen = new Pen(Theme.AccentHover, 1.2f);
            var k = straal * 0.72f;
            g.DrawEllipse(binnen, x - k, y - k, k * 2, k * 2);
        }

        /// <summary>
        /// De marionet uit het filmlogo, ingetogen: het handkruis met een paar draden die
        /// naar beneden zakken. Alleen dunne lijnen — net als de andere intro's doet de
        /// dekking van het overlay het werk, niet gevulde vlakken.
        /// </summary>
        private void TekenMarionet(Graphics g, float t)
        {
            var x = Width / 2f;
            var top = Height * 0.34f;
            var breedte = Math.Min(Width, Height) * 0.11f;
            using var balk = new Pen(Theme.Accent, 2.4f);
            // Het kruis komt eerst in beeld, daarna zakken de draden.
            var komOp = Soepel(Math.Min(1f, t / 0.4f));
            var halveBreedte = breedte * komOp;
            g.DrawLine(balk, x - halveBreedte, top, x + halveBreedte, top);
            g.DrawLine(balk, x, top - breedte * 0.45f * komOp, x, top + breedte * 0.18f * komOp);

            if (t <= 0.4f)
            {
                return;
            }
            using var draad = new Pen(Theme.AccentHover, 1.1f);
            var zak = Soepel((t - 0.4f) / 0.6f) * Height * 0.16f;
            foreach (var verhouding in new[] { -1f, -0.35f, 0.35f, 1f })
            {
                var dx = x + breedte * verhouding;
                g.DrawLine(draad, dx, top, dx, top + zak);
            }
        }

        /// <summary>
        /// Breaking Bad: de twee elementvakjes uit de serietitel (Br en Ba) schuiven van
        /// buiten naar het midden toe en komen naast elkaar tot stilstand. Alleen de contouren
        /// en de lettertekens — net als de andere intro's doet de dekking het werk.
        /// </summary>
        private void TekenElementen(Graphics g, float t)
        {
            var zijde = Math.Min(Width, Height) * 0.11f;
            var midY = Height / 2f;
            var rust = zijde * 0.62f; // halve tussenruimte in de eindstand
            var komBinnen = Soepel(Math.Min(1f, t / 0.6f));
            using var pen = new Pen(Theme.Accent, 2.2f);
            using var font = new Font(Theme.BaseFont.FontFamily, zijde * 0.42f, FontStyle.Bold);
            using var tekst = new SolidBrush(Theme.Accent);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            foreach (var (label, kant) in new[] { ("Br", -1f), ("Ba", 1f) })
            {
                // Van een halve schermbreedte buiten het midden naar zijn eindplek.
                var van = kant * Width * 0.42f;
                var naar = kant * rust;
                var x = Width / 2f + van + (naar - van) * komBinnen;
                var vak = new RectangleF(x - zijde / 2f, midY - zijde / 2f, zijde, zijde);
                g.DrawRectangle(pen, vak.X, vak.Y, vak.Width, vak.Height);
                g.DrawString(label, font, tekst, vak, sf);
            }
        }

        /// <summary>
        /// Zeilen: de horizon ligt er, en het zeil wordt gehesen — een driehoek die vanaf
        /// de giek omhoog groeit langs de mast.
        /// </summary>
        private void TekenZeil(Graphics g, float t)
        {
            var basisY = Height * 0.62f;
            var mastX = Width * 0.5f;
            var hoogte = Math.Min(Width, Height) * 0.3f;
            using var zee = new Pen(Theme.AccentHover, 1.2f);
            g.DrawLine(zee, Width * 0.2f, basisY, Width * 0.8f, basisY);
            var hijs = Soepel(Math.Min(1f, t / 0.7f));
            if (hijs <= 0.02f)
            {
                return;
            }
            using var pen = new Pen(Theme.Accent, 2.2f);
            var top = basisY - hoogte * hijs;
            var voet = mastX + hoogte * 0.62f * hijs;
            g.DrawLine(pen, mastX, basisY, mastX, top);          // mast
            g.DrawLine(pen, mastX, top, voet, basisY);            // achterlijk
            g.DrawLine(pen, mastX, basisY, voet, basisY);         // giek
        }

        /// <summary>Ease-out: snel starten, zacht uitlopen.</summary>
        private static float Soepel(float t) => 1f - (float)Math.Pow(1 - Math.Clamp(t, 0f, 1f), 3);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
