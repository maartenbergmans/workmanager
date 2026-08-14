using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Kort confetti-moment over een venster (bv. wanneer de laatste taak afgevinkt wordt).
/// Tekent op een doorzichtig, klikdoorlatend overlay-venster en ruimt zichzelf op.
/// </summary>
public static class Confetti
{
    public static void Vier(Form eigenaar)
    {
        if (eigenaar.IsDisposed)
        {
            return;
        }
        var overlay = new ConfettiVenster(eigenaar.RectangleToScreen(eigenaar.ClientRectangle));
        overlay.Show(eigenaar);
    }

    private sealed class ConfettiVenster : Form
    {
        private sealed class Snipper
        {
            public float X, Y, Vx, Vy, Hoek, Draai, Grootte;
            public Color Kleur;
        }

        /// <summary>
        /// De snippers in de kleuren van het thema. 007 krijgt bewust alleen goudtinten
        /// (gouden vonken in plaats van bonte confetti), Zomer warme bloemblaadjes.
        /// Property en geen veld: anders staat de kleurkeuze vast op het thema dat bij het
        /// opstarten actief was.
        /// </summary>
        private static Color[] Kleuren => Theme.Palet.Naam switch
        {
            "007" => new[]
            {
                Theme.Accent, Theme.AccentHover, Theme.Palet.KlantLauryssens,
                Theme.Mix(Theme.Accent, Color.White, 0.35f), Theme.Warn,
            },
            "Zomer" => new[]
            {
                Theme.Accent, Theme.AccentHover, Theme.Warn, Theme.KlantAqurat,
                Theme.Success, Theme.Danger,
            },
            _ => new[]
            {
                Theme.Accent, Theme.AccentHover, Theme.KlantCed,
                Theme.Success, Theme.Warn, Theme.Danger,
                Theme.Palet.Donker ? Color.White : Theme.Text,
            },
        };

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private readonly List<Snipper> _snippers = new();
        private int _leeftijd;

        public ConfettiVenster(Rectangle schermRect)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = schermRect;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            var willekeur = new Random();
            for (var i = 0; i < 130; i++)
            {
                _snippers.Add(new Snipper
                {
                    X = (float)(willekeur.NextDouble() * schermRect.Width),
                    Y = (float)(-willekeur.NextDouble() * schermRect.Height * 0.4 - 10),
                    Vx = (float)(willekeur.NextDouble() * 4 - 2),
                    Vy = (float)(willekeur.NextDouble() * 3.5 + 2.5),
                    Hoek = (float)(willekeur.NextDouble() * 360),
                    Draai = (float)(willekeur.NextDouble() * 12 - 6),
                    Grootte = (float)(willekeur.NextDouble() * 5 + 5),
                    Kleur = Kleuren[willekeur.Next(Kleuren.Length)],
                });
            }

            _timer.Tick += (_, _) =>
            {
                foreach (var s in _snippers)
                {
                    s.X += s.Vx;
                    s.Y += s.Vy;
                    s.Vy += 0.16f; // zwaartekracht
                    s.Hoek += s.Draai;
                }
                if (++_leeftijd > 120) // ± 2 s
                {
                    _timer.Stop();
                    Close();
                    return;
                }
                Invalidate();
            };
            _timer.Start();
        }

        // Niet activeerbaar en klikdoorlatend: het venster eronder blijft gewoon bedienbaar.
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
            foreach (var s in _snippers)
            {
                if (s.Y > Height)
                {
                    continue;
                }
                g.TranslateTransform(s.X, s.Y);
                g.RotateTransform(s.Hoek);
                using var brush = new SolidBrush(s.Kleur);
                g.FillRectangle(brush, -s.Grootte / 2, -s.Grootte / 4, s.Grootte, s.Grootte / 2);
                g.ResetTransform();
            }
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
}
