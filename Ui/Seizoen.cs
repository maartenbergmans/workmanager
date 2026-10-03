using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Het seizoen in de cockpit. Eén keer per dag dwarrelt er bij het openen kort iets over het
/// venster: bloesem in de lente, pluisjes in de zomer, bladeren in de herfst, sneeuw in de
/// winter — en als het buiten écht regent of sneeuwt (dat weet <see cref="WeerCache"/> al voor
/// de kalender) dan wint het weer van de kalender. De eerste sneeuwdag van de winter krijgt
/// een langer moment en een melding; die komt één keer per winter.
///
/// <para>Techniek zoals de confetti en de thema-intro: een klikdoorlatend overlay-venster dat
/// zichzelf opruimt, dus de cockpit blijft er onder gewoon bruikbaar. De sleutelkleur is
/// bewust de achtergrondkleur van het thema: de zachte randen van de deeltjes mengen daar
/// mee weg in plaats van een roze waas te geven. State in %APPDATA%\WorkManager\seizoen.json.</para>
/// </summary>
public static class Seizoen
{
    /// <summary>Wat er naar beneden komt.</summary>
    public enum Soort
    {
        Bloesem,
        Pluis,
        Blad,
        Vlok,
        Druppel,
    }

    /// <summary>Het seizoensbeeld van vandaag.</summary>
    public sealed record Stand(string Naam, Soort Deeltje, string Emoji, string Regel);

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "seizoen.json");

    private sealed class State
    {
        /// <summary>De dag waarop het seizoensmoment voor het laatst gespeeld is.</summary>
        public string LaatsteDag { get; set; } = "";

        /// <summary>De winter (bv. "2026-2027") waarin de eerste sneeuw al gevierd is.</summary>
        public string EersteSneeuwWinter { get; set; } = "";
    }

    /// <summary>Sneeuwcodes van Open-Meteo (sneeuw, sneeuwkorrels, sneeuwbuien).</summary>
    private static bool IsSneeuw(int code) => code is 71 or 73 or 75 or 77 or 85 or 86;

    /// <summary>Regen, motregen, buien, ijzel en onweer — alles waar je nat van wordt.</summary>
    private static bool IsRegen(int code) =>
        code is 51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82
            or 95 or 96 or 99;

    /// <summary>
    /// Het seizoensbeeld van vandaag: de kalender bepaalt het seizoen, maar echt weer gaat
    /// voor. Zonder weergegevens (nog niets opgehaald, geen internet) blijft het bij de
    /// kalender — dan klopt het nog altijd voor de tijd van het jaar.
    /// </summary>
    public static Stand Nu()
    {
        var nu = DateTime.Now;
        var weer = WeerCache.Uit(DateOnly.FromDateTime(nu));
        if (weer is not null && IsSneeuw(weer.Code))
        {
            return new Stand("Sneeuw", Soort.Vlok, "❄️", "Het sneeuwt in Brasschaat");
        }
        if (weer is not null && IsRegen(weer.Code))
        {
            return new Stand("Regen", Soort.Druppel, "🌧️", "Regen op het dak");
        }
        return (nu.Month, nu.Day) switch
        {
            // Meteorologische seizoenen, met de lente vanaf de equinox: dat sluit beter aan
            // bij wanneer de bloesem hier echt staat.
            (12 or 1 or 2, _) => new Stand("Winter", Soort.Vlok, "❄️", "Winter"),
            (3, < 20) => new Stand("Winter", Soort.Vlok, "❄️", "Winter"),
            (3 or 4 or 5, _) => new Stand("Lente", Soort.Bloesem, "🌸", "Lente — bloesem"),
            (6 or 7 or 8, _) => new Stand("Zomer", Soort.Pluis, "🌼", "Zomer"),
            (9, < 21) => new Stand("Zomer", Soort.Pluis, "🌼", "Zomer"),
            _ => new Stand("Herfst", Soort.Blad, "🍂", "Herfst — bladeren"),
        };
    }

    /// <summary>
    /// Versiering voor het tray-icoon, of null. Alleen in de kerstperiode: een rood mutsje op
    /// het WorkManager-icoon. Bewust één enkel geval — een icoon dat elke maand verandert is
    /// geen icoon meer, en je zoekt je tray-pictogram op zijn vorm.
    /// </summary>
    public static bool Kerstmuts
    {
        get
        {
            var nu = DateTime.Now;
            return (nu.Month == 12 && nu.Day >= 1) || (nu.Month == 1 && nu.Day <= 6);
        }
    }

    /// <summary>
    /// Is dit de eerste sneeuwdag van deze winter? Zo ja, dan geeft dit de meldingstekst en
    /// wordt de winter afgevinkt — de volgende sneeuwdag is dan gewoon sneeuw. Een winter
    /// loopt van juli tot juli, zodat december en januari bij dezelfde winter horen.
    /// </summary>
    public static string? EersteSneeuw()
    {
        try
        {
            var weer = WeerCache.Uit(DateOnly.FromDateTime(DateTime.Now));
            if (weer is null || !IsSneeuw(weer.Code))
            {
                return null;
            }
            var nu = DateTime.Now;
            var winter = nu.Month >= 7 ? $"{nu.Year}-{nu.Year + 1}" : $"{nu.Year - 1}-{nu.Year}";
            var state = Laad();
            if (state.EersteSneeuwWinter == winter)
            {
                return null;
            }
            state.EersteSneeuwWinter = winter;
            Bewaar(state);
            return "De eerste sneeuw van de winter — kijk eens naar buiten.";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Speelt het seizoensmoment als dat vandaag nog niet gebeurd is.</summary>
    public static void SpeelEenmaalPerDag(Form eigenaar)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        try
        {
            var state = Laad();
            if (state.LaatsteDag == vandaag)
            {
                return;
            }
            state.LaatsteDag = vandaag;
            Bewaar(state);
        }
        catch
        {
            // Zonder state hooguit één moment te veel; geen reden om te stoppen.
        }
        Speel(eigenaar);
    }

    /// <summary>
    /// Laat het seizoen nu over het venster dwarrelen. <paramref name="seconden"/> langer
    /// meegeven mag: de eerste sneeuw van de winter verdient meer dan een paar tellen.
    /// </summary>
    public static void Speel(Form eigenaar, double seconden = 7)
    {
        if (eigenaar.IsDisposed || NietStoren.Actief)
        {
            return;
        }
        try
        {
            var overlay = new SeizoenVenster(eigenaar, Nu().Deeltje, seconden);
            overlay.Show(eigenaar);
        }
        catch
        {
            // Sfeer mag nooit het openen van de cockpit in de weg staan.
        }
    }

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: dan speelt het moment gewoon opnieuw.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SeizoenVenster : Form
    {
        private sealed class Deeltje
        {
            public float X, Y, Vx, Vy, Hoek, Draai, Grootte, Zwaai, Fase;
            public Color Kleur;
        }

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private readonly List<Deeltje> _deeltjes = new();
        private readonly Soort _soort;
        private readonly int _frames;
        private readonly Form _eigenaar;
        private int _frame;

        public SeizoenVenster(Form eigenaar, Soort soort, double seconden)
        {
            _eigenaar = eigenaar;
            _soort = soort;
            _frames = (int)(seconden * 60);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = eigenaar.RectangleToScreen(eigenaar.ClientRectangle);
            // De sleutelkleur is de achtergrond van het thema: de zachte (antialiased) randen
            // van elk deeltje mengen daarmee, en dat valt tegen de cockpit niet op. Een vaste
            // magenta key gaf een roze halo rond elke vlok.
            BackColor = Theme.Bg;
            TransparencyKey = Theme.Bg;
            Opacity = 0.8;
            DoubleBuffered = true;

            var willekeur = new Random();
            foreach (var deeltje in Maak(willekeur, Bounds.Size, soort))
            {
                _deeltjes.Add(deeltje);
            }

            // Met het venster meeschuiven en -groeien, zodat het beeld blijft kloppen als je
            // de cockpit tijdens het moment verplaatst of maximaliseert.
            void Volg(object? _, EventArgs __)
            {
                if (!IsDisposed && !eigenaar.IsDisposed)
                {
                    Bounds = eigenaar.RectangleToScreen(eigenaar.ClientRectangle);
                }
            }
            eigenaar.LocationChanged += Volg;
            eigenaar.SizeChanged += Volg;
            FormClosed += (_, _) =>
            {
                eigenaar.LocationChanged -= Volg;
                eigenaar.SizeChanged -= Volg;
            };

            _timer.Tick += (_, _) =>
            {
                if (_eigenaar.IsDisposed || ++_frame > _frames)
                {
                    _timer.Stop();
                    Close();
                    return;
                }
                Beweeg();
                // De laatste seconde wegfaden, zodat het niet midden in de val afbreekt.
                var over = _frames - _frame;
                if (over < 60)
                {
                    Opacity = 0.8 * over / 60.0;
                }
                Invalidate();
            };
            _timer.Start();
        }

        /// <summary>Hoeveel deeltjes en met welke eigenschappen — per soort anders.</summary>
        private static IEnumerable<Deeltje> Maak(Random r, Size vlak, Soort soort)
        {
            var (aantal, kleuren) = soort switch
            {
                Soort.Vlok => (130, new[]
                {
                    Color.White, Color.FromArgb(0xEA, 0xF4, 0xFF), Color.FromArgb(0xCF, 0xE6, 0xFA),
                }),
                Soort.Blad => (75, new[]
                {
                    Color.FromArgb(0xC4, 0x5A, 0x1B), Color.FromArgb(0xE0, 0x8A, 0x22),
                    Color.FromArgb(0x8C, 0x3A, 0x18), Color.FromArgb(0xD6, 0xB0, 0x3A),
                    Color.FromArgb(0xA5, 0x6B, 0x25),
                }),
                Soort.Bloesem => (95, new[]
                {
                    Color.FromArgb(0xFF, 0xD7, 0xE4), Color.FromArgb(0xFF, 0xBF, 0xD6),
                    Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xF7, 0xC9, 0xDE),
                }),
                Soort.Pluis => (60, new[]
                {
                    Color.White, Color.FromArgb(0xFF, 0xF8, 0xE0), Color.FromArgb(0xF0, 0xF2, 0xE6),
                }),
                _ => (170, new[]
                {
                    Color.FromArgb(0x9C, 0xC7, 0xE8), Color.FromArgb(0xBF, 0xDC, 0xF0),
                    Color.FromArgb(0x7F, 0xAF, 0xD6),
                }),
            };

            for (var i = 0; i < aantal; i++)
            {
                // Boven het venster beginnen, verspreid over een ruime aanloop: zo valt er
                // vanaf het eerste frame iets én blijft het de hele duur doorregenen.
                var deeltje = new Deeltje
                {
                    X = (float)(r.NextDouble() * vlak.Width),
                    Y = (float)(-r.NextDouble() * vlak.Height * 1.1 - 10),
                    Hoek = (float)(r.NextDouble() * 360),
                    Fase = (float)(r.NextDouble() * Math.PI * 2),
                    Kleur = kleuren[r.Next(kleuren.Length)],
                };
                switch (soort)
                {
                    case Soort.Vlok:
                        deeltje.Vy = (float)(r.NextDouble() * 0.9 + 0.6);
                        deeltje.Vx = (float)(r.NextDouble() * 0.5 - 0.25);
                        deeltje.Grootte = (float)(r.NextDouble() * 4 + 3);
                        deeltje.Zwaai = (float)(r.NextDouble() * 0.7 + 0.3);
                        deeltje.Draai = (float)(r.NextDouble() * 2 - 1);
                        break;
                    case Soort.Blad:
                        deeltje.Vy = (float)(r.NextDouble() * 1.2 + 1.0);
                        deeltje.Vx = (float)(r.NextDouble() * 1.2 - 0.3);
                        deeltje.Grootte = (float)(r.NextDouble() * 7 + 8);
                        deeltje.Zwaai = (float)(r.NextDouble() * 1.4 + 0.6);
                        deeltje.Draai = (float)(r.NextDouble() * 5 - 2.5);
                        break;
                    case Soort.Bloesem:
                        deeltje.Vy = (float)(r.NextDouble() * 0.9 + 0.7);
                        deeltje.Vx = (float)(r.NextDouble() * 1.0 - 0.2);
                        deeltje.Grootte = (float)(r.NextDouble() * 4 + 4);
                        deeltje.Zwaai = (float)(r.NextDouble() * 1.1 + 0.5);
                        deeltje.Draai = (float)(r.NextDouble() * 4 - 2);
                        break;
                    case Soort.Pluis:
                        deeltje.Vy = (float)(r.NextDouble() * 0.5 + 0.25);
                        deeltje.Vx = (float)(r.NextDouble() * 0.8 - 0.3);
                        deeltje.Grootte = (float)(r.NextDouble() * 3 + 3);
                        deeltje.Zwaai = (float)(r.NextDouble() * 1.6 + 0.8);
                        deeltje.Draai = (float)(r.NextDouble() * 2 - 1);
                        break;
                    default: // Druppel
                        deeltje.Vy = (float)(r.NextDouble() * 5 + 9);
                        deeltje.Vx = -1.2f;
                        deeltje.Grootte = (float)(r.NextDouble() * 6 + 7);
                        deeltje.Zwaai = 0;
                        deeltje.Draai = 0;
                        break;
                }
                yield return deeltje;
            }
        }

        private void Beweeg()
        {
            foreach (var d in _deeltjes)
            {
                d.Fase += 0.03f;
                // De zijwaartse zwaai is wat een vallend blad of een vlok levend maakt: een
                // trage sinus bovenop de eigen drift.
                d.X += d.Vx + (float)Math.Sin(d.Fase) * d.Zwaai;
                d.Y += d.Vy;
                d.Hoek += d.Draai;
                if (d.Y > Height + 20)
                {
                    // Opnieuw bovenaan laten beginnen: zo blijft het doorsneeuwen tot het eind.
                    d.Y = -20;
                    d.X = Random.Shared.Next(Math.Max(1, Width));
                }
                if (d.X < -30)
                {
                    d.X = Width + 20;
                }
                else if (d.X > Width + 30)
                {
                    d.X = -20;
                }
            }
        }

        // Niet activeerbaar en klikdoorlatend: de cockpit eronder blijft gewoon bedienbaar.
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
            foreach (var d in _deeltjes)
            {
                if (d.Y < -20 || d.Y > Height)
                {
                    continue;
                }
                using var brush = new SolidBrush(d.Kleur);
                using var pen = new Pen(d.Kleur, Math.Max(1f, d.Grootte / 7f));
                switch (_soort)
                {
                    case Soort.Druppel:
                        // Regen: een schuine streep in de valrichting, geen bolletje.
                        g.DrawLine(pen, d.X, d.Y, d.X - d.Grootte * 0.18f, d.Y - d.Grootte);
                        break;
                    case Soort.Vlok:
                        TekenVlok(g, pen, brush, d);
                        break;
                    case Soort.Blad:
                        TekenBlad(g, brush, pen, d);
                        break;
                    case Soort.Bloesem:
                        TekenBloesem(g, brush, d);
                        break;
                    default:
                        TekenPluis(g, brush, pen, d);
                        break;
                }
            }
        }

        /// <summary>Een vlok: kleine kern met zes armen, licht meedraaiend.</summary>
        private static void TekenVlok(Graphics g, Pen pen, Brush brush, Deeltje d)
        {
            var r = d.Grootte / 2f;
            if (d.Grootte < 4.5f)
            {
                g.FillEllipse(brush, d.X - r, d.Y - r, d.Grootte, d.Grootte);
                return;
            }
            g.TranslateTransform(d.X, d.Y);
            g.RotateTransform(d.Hoek);
            for (var i = 0; i < 3; i++)
            {
                var rad = i * 60f * MathF.PI / 180f;
                var dx = MathF.Cos(rad) * r;
                var dy = MathF.Sin(rad) * r;
                g.DrawLine(pen, -dx, -dy, dx, dy);
            }
            g.ResetTransform();
        }

        /// <summary>Een blad: een tuimelende ovaal met een nerf erin.</summary>
        private static void TekenBlad(Graphics g, Brush brush, Pen pen, Deeltje d)
        {
            g.TranslateTransform(d.X, d.Y);
            g.RotateTransform(d.Hoek);
            var b = d.Grootte * 0.55f;
            var h = d.Grootte;
            g.FillEllipse(brush, -b / 2f, -h / 2f, b, h);
            using var nerf = new Pen(Color.FromArgb(90, 60, 30), Math.Max(1f, d.Grootte / 12f));
            g.DrawLine(nerf, 0, -h / 2f, 0, h / 2f);
            g.ResetTransform();
            _ = pen;
        }

        /// <summary>Bloesem: vier blaadjes rond een hart, als een klein bloemetje.</summary>
        private static void TekenBloesem(Graphics g, Brush brush, Deeltje d)
        {
            g.TranslateTransform(d.X, d.Y);
            g.RotateTransform(d.Hoek);
            var blad = d.Grootte * 0.55f;
            for (var i = 0; i < 4; i++)
            {
                g.RotateTransform(90f);
                g.FillEllipse(brush, -blad / 2f, -d.Grootte * 0.75f, blad, blad * 1.4f);
            }
            g.ResetTransform();
        }

        /// <summary>Pluis: een bolletje met een paar haartjes — zomerse paardenbloemzaadjes.</summary>
        private static void TekenPluis(Graphics g, Brush brush, Pen pen, Deeltje d)
        {
            var r = d.Grootte / 2.5f;
            g.FillEllipse(brush, d.X - r, d.Y - r, r * 2, r * 2);
            g.TranslateTransform(d.X, d.Y);
            g.RotateTransform(d.Hoek);
            for (var i = 0; i < 5; i++)
            {
                var rad = (i * 72f) * MathF.PI / 180f;
                g.DrawLine(pen, 0, 0, MathF.Cos(rad) * d.Grootte, MathF.Sin(rad) * d.Grootte);
            }
            g.ResetTransform();
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
