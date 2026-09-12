using System.Globalization;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Tijdcapsule: na elke afgesloten werkweek schrijft Claude uit het weekoverzicht van het
/// werkjournaal een kort briefje aan jezelf (waar je mee bezig was, wat lastig was, wat
/// lukte, met een vraag aan je toekomstige zelf). Dat briefje blijft vier weken dicht en
/// verschijnt dan onverwacht: "📬 Een brief van vier weken geleden". Bij de eerste run
/// worden ook de voorbije weken uit het journaal ingepakt, zodat de eerste al snel opengaat.
/// <para>State: %APPDATA%\WorkManager\tijdcapsule.json.</para>
/// </summary>
public static class Tijdcapsule
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "tijdcapsule.json");

    private const int WekenDicht = 4;

    public sealed class Brief
    {
        public string Week { get; set; } = "";          // yyyy-Www
        public DateTimeOffset Geschreven { get; set; }
        public string OpenOp { get; set; } = "";        // yyyy-MM-dd
        public string Tekst { get; set; } = "";
        public bool Geopend { get; set; }
    }

    private sealed class State
    {
        public List<Brief> Brieven { get; set; } = new();
    }

    private static bool _bezig;

    /// <summary>
    /// Vanuit de 10-minutentik (UI-thread): pakt afgesloten weken in (vanaf zaterdag, of later
    /// als het gemist werd; hoogstens één Claude-run per tik) en opent een brief die aan de
    /// beurt is — alleen op een werkdag tussen 9 en 18 u, als je achter de pc zit.
    /// </summary>
    public static async Task ZorgVoorAsync(Action<Brief> open)
    {
        if (_bezig)
        {
            return;
        }
        _bezig = true;
        try
        {
            await PakInAsync();
            var nu = DateTime.Now;
            if (nu.Hour is < 9 or >= 18 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || NietStoren.Actief)
            {
                return;
            }
            var state = Laad();
            var vandaag = nu.ToString("yyyy-MM-dd");
            var brief = state.Brieven.Where(b => !b.Geopend && string.CompareOrdinal(b.OpenOp, vandaag) <= 0)
                .OrderBy(b => b.OpenOp).FirstOrDefault();
            if (brief is null)
            {
                return;
            }
            brief.Geopend = true;
            Bewaar(state);
            open(brief);
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

    /// <summary>Schrijft de brief voor de oudste afgesloten week zonder brief (hoogstens drie weken terug).</summary>
    private static async Task PakInAsync()
    {
        var state = Laad();
        var nu = DateTime.Now;
        // De lopende week is pas "af" vanaf zaterdag.
        var kandidaten = Enumerable.Range(0, 4)
            .Select(i => nu.AddDays(-7 * i))
            .Where(d => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).Date.AddDays(5) <= nu.Date) // zaterdag van die week voorbij/bereikt
            .Select(d => (Jaar: ISOWeek.GetYear(d), Week: ISOWeek.GetWeekOfYear(d)))
            .Select(w => $"{w.Jaar}-W{w.Week:00}")
            .Where(w => state.Brieven.All(b => b.Week != w))
            .OrderBy(w => w)
            .ToList();
        foreach (var week in kandidaten)
        {
            var md = Path.Combine(Werkjournaal.Map, "weken", $"{week}.md");
            if (!File.Exists(md))
            {
                continue;
            }
            var overzicht = await File.ReadAllTextAsync(md);
            if (overzicht.Length > 12000)
            {
                overzicht = overzicht[..12000];
            }
            var prompt = $$"""
                Hieronder het automatische weekoverzicht van Maarten (freelance IT'er bij UrbanIT,
                teamleider IT bij CED, klanten o.a. Aqurat, Vriesveem, Radiology Partners;
                getrouwd met Hilke, dochters Emilia en Lisa).

                Schrijf een kort briefje (4 à 6 zinnen, Nederlands, tweede persoon "je") dat
                Maarten over vier weken onverwacht te lezen krijgt: waar hij die week mee bezig
                was, wat zwaar of lastig leek, wat er lukte, en sluit af met één vraag aan zijn
                toekomstige zelf. Warm, concreet, met een vleugje humor — geen motivatiepraat,
                geen opsommingstekens, geen aanhef of ondertekening. Verzin niets dat niet in het
                overzicht staat.

                Antwoord uitsluitend met de tekst van het briefje.

                WEEKOVERZICHT {{week}}
                {{overzicht}}
                """;
            var tekst = (await ClaudeDrafter.RunClaudeAsync(prompt, CancellationToken.None)).Trim();
            if (tekst.Length < 40)
            {
                continue;
            }
            var maandag = ISOWeek.ToDateTime(int.Parse(week[..4]), int.Parse(week[6..]), DayOfWeek.Monday);
            var openOp = maandag.AddDays(7 * WekenDicht + 7); // vier weken na het einde van die week
            if (openOp.Date <= nu.Date)
            {
                openOp = nu.Date.AddDays(new Random().Next(1, 4)); // al "te laat": binnenkort, onverwacht
            }
            state = Laad();
            state.Brieven.Add(new Brief
            {
                Week = week,
                Geschreven = DateTimeOffset.Now,
                OpenOp = openOp.ToString("yyyy-MM-dd"),
                Tekst = tekst,
            });
            Bewaar(state);
            return; // hoogstens één Claude-run per tik
        }
    }

    public static List<Brief> Alle() => Laad().Brieven;

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>Het venstertje waarin een geopende tijdcapsule getoond wordt.</summary>
public sealed class TijdcapsuleForm : Form
{
    public TijdcapsuleForm(Tijdcapsule.Brief brief)
    {
        var maandag = ISOWeek.ToDateTime(int.Parse(brief.Week[..4]), int.Parse(brief.Week[6..]), DayOfWeek.Monday);
        Text = "📬 Tijdcapsule";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 420);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var kop = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 16, 18, 0),
            Font = new Font(Theme.BaseFont.FontFamily, Theme.BaseFont.Size + 4, FontStyle.Bold),
            Text = $"Een brief van jezelf, uit de week van {maandag.ToString("d MMMM", new CultureInfo("nl-BE"))}",
        };
        var tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Georgia", 12.5f),
            Text = brief.Tekst.Replace("\n", Environment.NewLine),
        };
        var rand = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 8, 22, 8) };
        rand.Controls.Add(tekst);
        var knoppen = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var ok = new ModernButton { Text = "Dank je, verleden ik", Width = 180, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => Close();
        knoppen.Controls.Add(ok);
        Controls.Add(rand);
        Controls.Add(kop);
        Controls.Add(knoppen);
        AcceptButton = ok;
        Theme.Apply(this);
        tekst.BackColor = BackColor;
        tekst.ForeColor = Theme.Text;
        kop.ForeColor = Theme.Accent;
        Theme.EscSluit(this);
    }
}
