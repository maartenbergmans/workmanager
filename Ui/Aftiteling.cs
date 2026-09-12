using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// De eindgeneriek van je werkdag: een filmische aftiteling die over de cockpit rolt, in de
/// stijl van het thema — "In de hoofdrol: Maarten Bergmans · Met Claude, als zichzelf (84
/// opdrachten) · Gastrollen: Aqurat, CED · Stunts: de afgevinkte taken · Gefilmd op locatie:
/// Thuis". Gevoed door het werkjournaal van vandaag. Klik, Esc of spatie sluit hem.
/// Wie de aftiteling gezien heeft, klapt makkelijker de laptop dicht.
/// </summary>
public static class Aftiteling
{
    private enum Stijl { Titel, Ondertitel, Kop, Naam, Ruimte, Einde }

    /// <summary>Rolt de aftiteling van vandaag over het scherm van <paramref name="eigenaar"/>.</summary>
    public static async Task SpeelAsync(Form eigenaar)
    {
        if (eigenaar.IsDisposed || NietStoren.Actief)
        {
            return;
        }
        // Eerst de dagsamenvatting verversen: het journaal loopt anders tot een uur achter.
        await Task.Run(Werkjournaal.WerkBij);
        if (eigenaar.IsDisposed)
        {
            return;
        }
        var regels = Regels(DateOnly.FromDateTime(DateTime.Now));
        var scherm = Screen.FromControl(eigenaar).Bounds;
        var venster = new AftitelingVenster(scherm, regels);
        venster.Show(eigenaar);
        venster.Activate();
    }

    private static List<(string Tekst, Stijl Stijl)> Regels(DateOnly dag)
    {
        var d = Werkjournaal.Dag(dag);
        var nl = new CultureInfo("nl-BE");
        var weekdag = dag.ToString("dddd", nl);
        var r = new List<(string, Stijl)>();
        void Kop(string tekst) { r.Add(("", Stijl.Ruimte)); r.Add((tekst, Stijl.Kop)); }
        void Naam(string tekst) => r.Add((tekst, Stijl.Naam));

        var (titel, onder, disclaimer) = Theme.Palet.Naam switch
        {
            "Godfather" => ("IL PADRINO", $"een {weekdag} uit het leven van de familie Bergmans",
                "Er belandde geen enkel paardenhoofd in een bed tijdens de opnames."),
            "007" => ("MAARTEN BERGMANS IS 007", $"in  {Kapitaal(weekdag)} Never Dies",
                "Er werd geen enkele Aston Martin beschadigd tijdens de opnames."),
            "Espresso" => ("RISTRETTO", $"een {weekdag} in één kort, sterk shot",
                "Er werd geen enkele espresso koud tijdens de opnames."),
            "Neon" => ("NEON NIGHTS", $"a {weekdag} in the city",
                "No pixels were harmed during this production."),
            "Zomer" => ("ZOMERSE ZAKEN", $"een {weekdag} met zand tussen de tenen",
                "Er smolt geen enkel ijsje tijdens de opnames."),
            _ => ("EEN WORKMANAGER-PRODUCTIE", $"{Kapitaal(weekdag)} {dag.ToString("d MMMM yyyy", nl)}",
                "Er raakte geen enkele mail gewond tijdens de opnames."),
        };
        r.Add((titel, Stijl.Titel));
        r.Add((onder, Stijl.Ondertitel));

        Kop("In de hoofdrol");
        Naam("Maarten Bergmans");
        if (d is { Claude.Opdrachten: > 0 })
        {
            Kop("Met");
            Naam($"Claude, als zichzelf ({d.Claude.Opdrachten} opdrachten)");
            foreach (var (map, n) in d.Claude.PerMap.Take(3))
            {
                Naam($"{map}  ·  {n} scènes");
            }
        }
        var gasten = d?.PerContext
            .Where(kv => !kv.Key.StartsWith("Overig") && !kv.Key.Contains("onbekend") && !kv.Key.StartsWith("Browser") &&
                         kv.Value >= 15)
            .Take(4).ToList() ?? new();
        if (gasten.Count > 0)
        {
            Kop("Gastrollen");
            gasten.ForEach(g => Naam($"{g.Key}  ({g.Value / 60.0:0.#} u)"));
        }
        if (d is { Meetings.Count: > 0 })
        {
            Kop("Scènes in de vergaderzaal");
            foreach (var m in d.Meetings.Take(6))
            {
                Naam(Regex.Replace(m, @"^\d\d:\d\d-\d\d:\d\d ", ""));
            }
        }
        if (d is { TakenAfgevinkt.Count: > 0 })
        {
            Kop("Stunts");
            foreach (var t in d.TakenAfgevinkt.Take(7))
            {
                Naam(Kort(Regex.Replace(t, @"^\[[^\]]*\] ", "").Replace("❓ ", "").Replace("🤝 ", ""), 60));
            }
        }
        Kop("Boekhouding");
        Naam(d is { Timesheets.Minuten: > 0 }
            ? $"{d.Timesheets.Minuten / 60.0:0.#} uur geboekt op {d.Timesheets.PerProject.Count} project{(d.Timesheets.PerProject.Count == 1 ? "" : "en")}"
            : "Nog niets geboekt — de producent kijkt streng");
        if (d is { Eerste: not null })
        {
            Kop("Draaitijd");
            Naam($"{d.Eerste} – {d.Laatste}  ·  {d.ActiefMinuten / 60.0:0.#} uur achter de camera");
        }
        Kop("Gefilmd op locatie");
        Naam(d is { Plekken.Count: > 0 } ? string.Join(", ", d.Plekken.Where(p => p != "onbekende plek").DefaultIfEmpty("Onbekend terrein")) : "Thuis");
        if (Soundtrack() is { } nummer)
        {
            Kop("Soundtrack");
            Naam(nummer);
        }
        if (MorgenEerste() is { } morgen)
        {
            Kop("Volgende aflevering");
            Naam(morgen);
        }
        r.Add(("", Stijl.Ruimte));
        r.Add(("", Stijl.Ruimte));
        Naam(disclaimer);
        r.Add(("", Stijl.Ruimte));
        r.Add(("", Stijl.Ruimte));
        r.Add(("THE END", Stijl.Einde));
        r.Add(("tot morgen", Stijl.Ondertitel));
        return r;
    }

    private static string? Soundtrack()
    {
        try
        {
            var pad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkManager", "inbox-zero-muziek.json");
            if (!File.Exists(pad))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(pad));
            return doc.RootElement.TryGetProperty("LaatsteDag", out var dag) &&
                   dag.GetString() == DateTime.Now.ToString("yyyy-MM-dd") &&
                   doc.RootElement.TryGetProperty("LaatsteNummer", out var n)
                ? n.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? MorgenEerste()
    {
        try
        {
            var morgen = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
            if (MeetingsCache.Load() is not { } cache)
            {
                return null;
            }
            var ced = cache.Ced.TryGetValue(morgen.ToString("yyyy-MM-dd"), out var c) ? c : new();
            var eerste = cache.Eigen.Concat(ced)
                .Where(m => !m.HeleDag && DateOnly.FromDateTime(m.Start.LocalDateTime) == morgen && !GeenWerktijd.Is(m.Titel))
                .OrderBy(m => m.Start).FirstOrDefault();
            return eerste is null ? null : $"morgen om {eerste.Start.LocalDateTime:HH:mm}: {Kort(eerste.Titel, 50)}";
        }
        catch
        {
            return null;
        }
    }

    private static string Kapitaal(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static string Kort(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class AftitelingVenster : Form
    {
        private readonly List<(string Tekst, Stijl Stijl)> _regels;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private readonly Font _titelFont, _onderFont, _kopFont, _naamFont, _eindFont;
        private float _offset;
        private float _totaleHoogte;
        private int _frame;

        public AftitelingVenster(Rectangle scherm, List<(string, Stijl)> regels)
        {
            _regels = regels;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = scherm;
            TopMost = true;
            BackColor = Color.FromArgb(8, 8, 10);
            Opacity = 0;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            KeyPreview = true;

            var familie = Theme.Palet.Naam is "Godfather" or "Espresso" ? "Georgia" : "Segoe UI Light";
            _titelFont = new Font(familie, 40, FontStyle.Bold);
            _onderFont = new Font(familie, 18, FontStyle.Italic);
            _kopFont = new Font("Segoe UI", 12, FontStyle.Bold);
            _naamFont = new Font(familie, 20);
            _eindFont = new Font(familie, 48, FontStyle.Bold);
            _offset = scherm.Height;

            Click += (_, _) => Sluit();
            KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Escape or Keys.Space or Keys.Enter)
                {
                    Sluit();
                }
            };
            _timer.Tick += (_, _) =>
            {
                _frame++;
                if (Opacity < 0.94)
                {
                    Opacity = Math.Min(0.94, Opacity + 0.05);
                }
                _offset -= 1.6f; // ± 100 px per seconde
                if (_totaleHoogte > 0 && _offset + _totaleHoogte < Height * 0.35f)
                {
                    // Einde bereikt: rustig weg.
                    Opacity -= 0.02;
                    if (Opacity <= 0.05)
                    {
                        Sluit();
                        return;
                    }
                }
                Invalidate();
            };
            _timer.Start();
        }

        private void Sluit()
        {
            _timer.Stop();
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Dispose();
            foreach (var f in new[] { _titelFont, _onderFont, _kopFont, _naamFont, _eindFont })
            {
                f.Dispose();
            }
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var accent = Theme.Accent;
            var tekstKleur = Color.FromArgb(235, 232, 226);
            var gedimd = Color.FromArgb(150, 150, 150);
            using var opmaak = new StringFormat { Alignment = StringAlignment.Center };
            var y = _offset;
            foreach (var (tekst, stijl) in _regels)
            {
                var (font, kleur, hoogte) = stijl switch
                {
                    Stijl.Titel => (_titelFont, accent, 72f),
                    Stijl.Ondertitel => (_onderFont, gedimd, 40f),
                    Stijl.Kop => (_kopFont, accent, 30f),
                    Stijl.Einde => (_eindFont, accent, 80f),
                    Stijl.Ruimte => (_naamFont, tekstKleur, 26f),
                    _ => (_naamFont, tekstKleur, 36f),
                };
                if (tekst.Length > 0 && y > -hoogte && y < Height)
                {
                    var weer = stijl == Stijl.Kop ? tekst.ToUpperInvariant() : tekst;
                    using var kwast = new SolidBrush(kleur);
                    g.DrawString(weer, font, kwast, new RectangleF(40, y, Width - 80, hoogte), opmaak);
                }
                y += hoogte;
            }
            _totaleHoogte = y - _offset;
            using var hint = new SolidBrush(Color.FromArgb(90, 90, 90));
            g.DrawString("klik of Esc om te sluiten", _kopFont, hint, Width - 260, Height - 40);
        }
    }
}
