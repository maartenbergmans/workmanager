using System.Globalization;

namespace WorkManager;

/// <summary>
/// Weekadvies: elke maandag vanaf 9 u leest Claude de weekoverzichten van de voorbije twee
/// weken uit het werkjournaal en geeft drie concrete, uitvoerbare tips plus één compliment —
/// gebaseerd op de cijfers, niet op algemeenheden. Het advies komt als melding (de eerste tip)
/// en staat volledig in journaal\advies\yyyy-Www.md; klikken opent het.
/// </summary>
public static class Weekadvies
{
    private static readonly string Map = Path.Combine(Werkjournaal.Map, "advies");
    private static bool _bezig;

    /// <summary>Vanuit de 10-minutentik: op maandag (of later in de week als het gemist werd) één keer.</summary>
    public static async Task ZorgVoorAsync(Action<string, string> toon)
    {
        var nu = DateTime.Now;
        if (_bezig || nu.Hour < 9 || nu.Hour >= 18 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || NietStoren.Actief)
        {
            return;
        }
        var week = $"{ISOWeek.GetYear(nu)}-W{ISOWeek.GetWeekOfYear(nu):00}";
        var pad = Path.Combine(Map, $"{week}.md");
        if (File.Exists(pad))
        {
            return;
        }
        _bezig = true;
        try
        {
            var vorige = nu.AddDays(-7);
            var tweeTerug = nu.AddDays(-14);
            var overzichten = new[] { tweeTerug, vorige }
                .Select(d => Path.Combine(Werkjournaal.Map, "weken", $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}.md"))
                .Where(File.Exists)
                .Select(f => File.ReadAllText(f))
                .Select(t => t.Length > 9000 ? t[..9000] : t)
                .ToList();
            if (overzichten.Count == 0)
            {
                return;
            }
            var prompt = $$"""
                Je bent de persoonlijke werkcoach van Maarten: freelance IT'er (UrbanIT), teamleider
                IT bij CED, ontwikkelt voor Aqurat, Vriesveem, Radiology Partners en Lauryssens,
                werkt intensief met meerdere Claude Code-sessies tegelijk; getrouwd met Hilke,
                dochters Emilia en Lisa. Hieronder de automatische weekoverzichten uit zijn
                werkjournaal (de laatste is de afgelopen week).

                Geef:
                1. Drie concrete, uitvoerbare tips voor deze week, elk met het cijfer uit het
                   overzicht waarop ze steunen (bv. uren actief vs geboekt, werk na 19 u,
                   contextwissels, Claude-opdrachten per project, WorkManager-gebruik). Geen
                   algemeenheden, geen preek; kort en direct, zoals een goede collega het zegt.
                2. Eén oprecht compliment over iets wat goed ging, ook met een cijfer.

                Schrijf in het Nederlands, in markdown, met als eerste regel een pakkende titel
                van hoogstens 60 tekens (zonder #). Verzin niets dat niet in de overzichten staat.

                {{string.Join("\n\n---\n\n", overzichten)}}
                """;
            var advies = (await ClaudeDrafter.RunClaudeAsync(prompt, CancellationToken.None)).Trim();
            if (advies.Length < 80)
            {
                return;
            }
            Directory.CreateDirectory(Map);
            await File.WriteAllTextAsync(pad, $"# Weekadvies {week}\n\n{advies}\n");
            var titel = advies.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim('#', ' ', '*');
            toon($"💡 Weekadvies: {Kort(titel, 60)}", pad);
        }
        catch
        {
            // Volgende tik opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    private static string Kort(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Leesvenster voor een markdownbestand uit het journaal (weekadvies).</summary>
public sealed class LeesVenster : Form
{
    public LeesVenster(string titel, string pad)
    {
        Text = titel;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 560);
        MinimizeBox = false;
        var tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 11f),
            Text = (File.Exists(pad) ? File.ReadAllText(pad) : "(niet gevonden)")
                .Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        };
        var rand = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        rand.Controls.Add(tekst);
        var knoppen = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var sluit = new ModernButton { Text = "Sluiten", Width = 100 };
        sluit.Click += (_, _) => Close();
        var map = new ModernButton { Text = "Map openen", Width = 120 };
        map.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(pad)!) { UseShellExecute = true });
            }
            catch
            {
                // Map niet te openen.
            }
        };
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(map);
        Controls.Add(rand);
        Controls.Add(knoppen);
        Theme.Apply(this);
        tekst.BackColor = BackColor;
        tekst.ForeColor = Theme.Text;
        Theme.EscSluit(this);
        tekst.SelectionStart = 0;
    }
}
