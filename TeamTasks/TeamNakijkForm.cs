using System.Globalization;

namespace WorkManager;

/// <summary>
/// Nakijkstap vóór de weekmail: de ★★★-taken die al weken openstaan, al drie keer of vaker
/// gemaild zijn of over hun deadline zijn. Per taak kies je: afgevinkt (het is gebeurd),
/// deadline naar volgende vrijdag (concrete afspraak), naar ★★ (uit de mail, blijft in de
/// lijst) of bewerken. Zo blijft de weekmail een lijst van échte prioriteiten in plaats
/// van een steeds langer wordend herhaalrondje.
/// </summary>
public class TeamNakijkForm : Form
{
    private readonly TeamTasksData _data;
    private readonly ModernListView _lijst;
    private readonly Label _status;

    /// <summary>Er is iets aan de taken veranderd: de aanroeper moet zijn lijst hervullen.</summary>
    public bool Gewijzigd { get; private set; }

    private static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-BE");

    /// <summary>Open ★★★-taken die aandacht vragen: ≥ 6 weken oud, ≥ 3× gemaild of over de deadline.</summary>
    public static List<TeamTaak> Kandidaten(TeamTasksData data)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return data.Taken
            .Where(t => !t.Klaar && t.Prioriteit == 0 &&
                        (t.InMail >= 3 || t.Leeftijd >= 42 || (t.Deadline is { } d && d < vandaag)))
            .OrderBy(t => data.Leden.FindIndex(l => l.Equals(t.Lid, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(t => t.InMail)
            .ThenBy(t => t.Aangemaakt)
            .ToList();
    }

    public TeamNakijkForm(TeamTasksData data, bool vooraf)
    {
        _data = data;
        Text = "Weekmail nakijken";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 600);
        MinimizeBox = false;

        var uitleg = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(12, 10, 12, 0),
            ForeColor = Theme.Muted,
            Text = "Deze ★★★-taken slepen al even mee. Kies per taak wat ermee moet vóór ze weer de mail in gaan: " +
                   "afgevinkt, een concrete deadline, uit de mail (★★) of bewerken. Wat je laat staan, gaat gewoon mee.",
        };

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            MultiSelect = true,
            LegeTekst = "Niets meer na te kijken.",
            LeegGlyph = Fluent.Check,
        };
        _lijst.Columns.Add("Teamlid", 110);
        _lijst.Columns.Add("Taak", 470);
        _lijst.Columns.Add("Waarom", 340);
        _lijst.Resize += (_, _) => _lijst.Columns[1].Width = Math.Max(250,
            _lijst.ClientSize.Width - _lijst.Columns[0].Width - _lijst.Columns[2].Width - 4);
        _lijst.MouseDoubleClick += (_, _) => Bewerken();

        // Acties links, doorgaan/sluiten rechts.
        var acties = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10) };
        var afgevinkt = new ModernButton { Text = "✓ Afgevinkt", Width = 120, Glyph = Fluent.Check };
        afgevinkt.Click += (_, _) => PasToe(t =>
        {
            t.Klaar = true;
            t.KlaarOp = DateTimeOffset.Now;
        });
        var deadline = new ModernButton { Text = "Deadline: volgende vrijdag", Width = 205, Glyph = Fluent.Kalender };
        deadline.Click += (_, _) => PasToe(t => t.Deadline = VolgendeVrijdag());
        var uitMail = new ModernButton { Text = "Naar ★★ (uit de mail)", Width = 175 };
        uitMail.Click += (_, _) => PasToe(t => t.Prioriteit = 1);
        var bewerken = new ModernButton { Text = "Bewerken…", Width = 115, Glyph = Fluent.Edit };
        bewerken.Click += (_, _) => Bewerken();
        var latenStaan = new ModernButton { Text = "Laten staan", Width = 115 };
        latenStaan.Click += (_, _) => VerwijderSelectieUitLijst();
        _status = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 11, 0, 0) };
        acties.Controls.AddRange(new Control[] { afgevinkt, deadline, uitMail, bewerken, latenStaan, _status });

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(10),
        };
        var sluiten = new ModernButton
        {
            Text = vooraf ? "Annuleren" : "Sluiten", DialogResult = DialogResult.Cancel, Width = 110,
        };
        var verder = new ModernButton
        {
            Text = vooraf ? "Naar de mail" : "Klaar", Width = 140, Kind = ButtonKind.Accent,
            Glyph = vooraf ? Fluent.Mail : Fluent.Check, DialogResult = DialogResult.OK,
        };
        knoppen.Controls.Add(sluiten);
        knoppen.Controls.Add(verder);
        CancelButton = sluiten;
        AcceptButton = verder;

        Controls.Add(_lijst);
        Controls.Add(uitleg);
        Controls.Add(acties);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VulLijst();
    }

    private void VulLijst()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var taak in Kandidaten(_data))
        {
            var redenen = new List<string>();
            if (taak.Deadline is { } d && d < vandaag)
            {
                redenen.Add($"⚠ deadline {d.ToString("ddd d/M", Nl)} voorbij");
            }
            if (taak.InMail >= 3)
            {
                redenen.Add($"📧 al {taak.InMail}× gemaild");
            }
            if (taak.Leeftijd >= 42)
            {
                redenen.Add($"{taak.Leeftijd / 7} weken open (sinds {taak.Aangemaakt.LocalDateTime:d/M})");
            }
            var item = new ListViewItem(taak.Lid) { Tag = taak, UseItemStyleForSubItems = false };
            item.SubItems.Add(taak.Tekst);
            var waarom = item.SubItems.Add(string.Join(" · ", redenen));
            waarom.ForeColor = redenen[0].StartsWith('⚠') ? Theme.Warn : Theme.Muted;
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
        _status.Text = _lijst.Items.Count == 0 ? "Alles nagekeken."
            : $"{_lijst.Items.Count} na te kijken · selecteer één of meer taken";
    }

    private List<TeamTaak> Selectie() =>
        _lijst.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<TeamTaak>().ToList();

    private void PasToe(Action<TeamTaak> actie)
    {
        var taken = Selectie();
        if (taken.Count == 0)
        {
            Toast.Toon(this, "Selecteer eerst een taak", Fluent.Lijst);
            return;
        }
        foreach (var taak in taken)
        {
            actie(taak);
        }
        TeamTaskStore.Save(_data);
        Gewijzigd = true;
        VulLijst();
    }

    private void Bewerken()
    {
        if (Selectie().FirstOrDefault() is not { } taak)
        {
            return;
        }
        using var form = new TeamTaakBewerkForm(_data.Leden, taak);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        taak.Tekst = form.TaakTekst;
        taak.Lid = form.Lid;
        taak.Prioriteit = form.Prioriteit;
        taak.Deadline = form.Deadline;
        taak.Subtaken = form.Subtaken;
        TeamTaskStore.Save(_data);
        Gewijzigd = true;
        VulLijst();
    }

    /// <summary>"Laten staan": alleen uit deze nakijklijst, de taak zelf verandert niet.</summary>
    private void VerwijderSelectieUitLijst()
    {
        foreach (ListViewItem item in _lijst.SelectedItems.Cast<ListViewItem>().ToList())
        {
            _lijst.Items.Remove(item);
        }
        _status.Text = _lijst.Items.Count == 0 ? "Alles nagekeken."
            : $"{_lijst.Items.Count} na te kijken · selecteer één of meer taken";
    }

    private static DateOnly VolgendeVrijdag()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var naarVrijdag = ((int)DayOfWeek.Friday - (int)vandaag.DayOfWeek + 7) % 7;
        return vandaag.AddDays(naarVrijdag + 7);
    }
}
