using System.Globalization;

namespace WorkManager;

/// <summary>
/// Vraagt per gekozen gerecht wanneer het gekookt wordt, zodat het in de agenda kan.
/// De dag kies je uit een leesbare lijst ("Morgen · wo 6 aug", …, twee weken vooruit —
/// vandaag koken heeft geen zin: de boodschappen moeten nog geleverd worden). Standaard
/// begint de afspraak 's avonds (instelbaar, 18:00); met het middag-vinkje per gerecht
/// wordt het 12:00 — voor wie 's middags warm eet. Het blok duurt altijd een uur; de
/// bereidingstijd staat er alleen ter info bij.
/// </summary>
public class AhAgendaForm : Form
{
    /// <summary>Middagtijd voor gerechten met het middag-vinkje aan.</summary>
    private static readonly TimeSpan Middag = TimeSpan.FromHours(12);

    private sealed class Rij
    {
        public required string Naam { get; init; }
        public required int Minuten { get; init; }
        public required CheckBox Aan { get; init; }
        public required ComboBox Datum { get; init; }
        public required CheckBox MiddagKeuze { get; init; }
        public required PictureBox Foto { get; init; }

        /// <summary>De dagindex die bij het openen voorgesteld werd; staat de keuze daar nog
        /// op, dan mag de bezet-check hem naar een vrije avond verschuiven.</summary>
        public required int StandaardIndex { get; init; }
    }

    /// <summary>Eén kiesbare dag in de datumlijst, met een leesbare Nederlandse naam.</summary>
    private sealed class DagKeuze
    {
        public required DateTime Datum { get; init; }
        public required string Tekst { get; init; }
        public override string ToString() => Tekst;
    }

    /// <summary>Vroegste dag die je mag kiezen: morgen.</summary>
    private static DateTime Vroegste => DateTime.Today.AddDays(1);

    /// <summary>Zoveel dagen vooruit kun je plannen; ruim genoeg voor een weekmenu.</summary>
    private const int DagenVooruit = 14;

    private static readonly CultureInfo NlBe = CultureInfo.GetCultureInfo("nl-BE");

    private readonly List<Rij> _rijen = new();
    private readonly DateTimePicker _tijd;

    /// <summary>De ingeplande gerechten (alleen geldig na DialogResult.OK).</summary>
    public List<(string Naam, DateTime Start, TimeSpan Duur)> Geplande { get; } = new();

    /// <summary>Een maaltijdafspraak duurt altijd een uur; de bereidingstijd staat in de tekst.</summary>
    public static readonly TimeSpan ReceptDuur = TimeSpan.FromHours(1);

    public AhAgendaForm(IReadOnlyList<(string Naam, int Minuten)> gerechten)
    {
        Text = "Gerechten inplannen";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        // Ruim genoeg: de controls schalen bij hogere DPI mee, het venster niet vanzelf.
        Size = new Size(900, Math.Min(720, 210 + gerechten.Count * 52));

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(12, 10, 12, 0),
            Text = "Kies per gerecht een dag (vanaf morgen). Standaard 's avonds; vink " +
                   "\"middag\" aan om 12:00 te eten. Vink uit wat je niet in de agenda wil.",
        };

        var tijdStrook = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 4, 0, 0) };
        var tijdLabel = new Label { Text = "Avond om:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
        _tijd = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 70,
            Value = DateTime.Today.AddHours(18),
        };
        var middagUitleg = new Label
        {
            Text = "· middag = 12:00", AutoSize = true, Margin = new Padding(10, 7, 0, 0),
        };
        tijdStrook.Controls.AddRange(new Control[] { tijdLabel, _tijd, middagUitleg });

        var paneel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            AutoScroll = true,
            Padding = new Padding(12, 4, 12, 4),
        };
        // Alles AutoSize (de naamkolom heeft zelf een vaste breedte): een percent-kolom
        // rekent bij AutoScroll met de volle breedte en duwt de rechterkolommen buiten beeld.
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // foto
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // naam
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // dag
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // middag
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // duur

        var i = 0;
        foreach (var (naam, minuten) in gerechten)
        {
            // Foto van het gerecht vóór de naam — zelfde bron als de bestellijst; laadt hij
            // pas later, dan vult BeeldKlaar hem alsnog in.
            var foto = new PictureBox
            {
                Size = new Size(40, 40),
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 3, 8, 3),
                Image = GerechtFoto.Voor(naam),
            };
            // Vaste breedte met ellipsis: een lange gerechtnaam mag de dag- en middagkolom
            // niet uit het venster duwen.
            var aan = new CheckBox
            {
                Text = naam, Checked = true, AutoSize = false, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Width = 285, Height = 28,
                Margin = new Padding(0, 9, 12, 6),
            };
            // Leesbare daglijst in één klik, in plaats van de priegelige datumspinner.
            var datum = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 188,
                Margin = new Padding(0, 9, 8, 4),
            };
            foreach (var dag in DagKeuzes())
            {
                datum.Items.Add(dag);
            }
            // Elk volgend gerecht standaard een dag later, zodat een weekmenu vanzelf spreidt.
            datum.SelectedIndex = Math.Min(i, datum.Items.Count - 1);
            var middag = new CheckBox
            {
                Text = "middag", AutoSize = true, Margin = new Padding(0, 12, 10, 6),
            };
            var duur = new Label
            {
                Text = minuten > 0 ? $"{minuten} min" : "duur onbekend",
                AutoSize = true,
                Margin = new Padding(0, 12, 0, 6),
                ForeColor = Theme.Muted,
            };
            paneel.Controls.Add(foto, 0, i);
            paneel.Controls.Add(aan, 1, i);
            paneel.Controls.Add(datum, 2, i);
            paneel.Controls.Add(middag, 3, i);
            paneel.Controls.Add(duur, 4, i);
            _rijen.Add(new Rij
            {
                Naam = naam, Minuten = minuten, Aan = aan, Datum = datum,
                MiddagKeuze = middag, Foto = foto, StandaardIndex = datum.SelectedIndex,
            });
            i++;
        }
        // Foto's die nog gedownload worden zodra ze binnen zijn tonen (event komt van een
        // achtergrondthread, dus via BeginInvoke naar de UI-thread).
        GerechtFoto.BeeldKlaar += OpFotoKlaar;
        FormClosed += (_, _) => GerechtFoto.BeeldKlaar -= OpFotoKlaar;

        // Op de achtergrond de agenda raadplegen en dagen met een avondafspraak markeren.
        Shown += async (_, _) => await MarkeerBezetteAvondenAsync();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var overslaan = new ModernButton { Text = "Overslaan", DialogResult = DialogResult.Cancel, Width = 110 };
        var ok = new ModernButton
        {
            Text = "In agenda zetten", Width = 160, Kind = ButtonKind.Accent, Glyph = Fluent.Kalender,
        };
        ok.Click += (_, _) => Bevestig();
        buttons.Controls.Add(overslaan);
        buttons.Controls.Add(ok);
        CancelButton = overslaan;

        Controls.Add(paneel);
        Controls.Add(tijdStrook);
        Controls.Add(hint);
        Controls.Add(new AhStappenBalk(3));
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        tijdLabel.ForeColor = middagUitleg.ForeColor = Theme.Muted;
    }

    /// <summary>
    /// De kiesbare dagen: "Morgen · wo 6 aug", "Overmorgen · do 7 aug", daarna gewoon
    /// "vrijdag 8 aug" — herkenbaarder dan een kale datum in een spinnertje. Met
    /// <paramref name="bezet"/> krijgen dagen waarop 's avonds al iets gepland staat een ⚠.
    /// </summary>
    private static IEnumerable<DagKeuze> DagKeuzes(HashSet<DateOnly>? bezet = null)
    {
        for (var d = 0; d < DagenVooruit; d++)
        {
            var datum = Vroegste.AddDays(d);
            var kort = datum.ToString("ddd d MMM", NlBe);
            var tekst = d switch
            {
                0 => $"Morgen · {kort}",
                1 => $"Overmorgen · {kort}",
                _ => Hoofdletter(datum.ToString("dddd d MMM", NlBe)),
            };
            if (bezet?.Contains(DateOnly.FromDateTime(datum)) == true)
            {
                tekst += "  ⚠ bezet";
            }
            yield return new DagKeuze { Datum = datum, Tekst = tekst };
        }
    }

    /// <summary>
    /// Kijkt in Google Agenda welke van de kiesbare dagen 's avonds (17–21 u) al een
    /// afspraak hebben en zet daar een ⚠ bij in de daglijsten — zo plan je niet per ongeluk
    /// een kookavond op een avond dat je weg bent. De gekozen indexen blijven staan.
    /// </summary>
    private async Task MarkeerBezetteAvondenAsync()
    {
        if (!CalendarClient.Beschikbaar)
        {
            return;
        }
        HashSet<DateOnly> bezet;
        try
        {
            var van = DateOnly.FromDateTime(Vroegste);
            var afspraken = await CalendarClient.ZoekInPeriodeAsync(
                van, van.AddDays(DagenVooruit - 1), CancellationToken.None);
            bezet = afspraken
                .Where(a => a.Einde.TimeOfDay > TimeSpan.FromHours(17) &&
                            a.Start.TimeOfDay < TimeSpan.FromHours(21))
                .Select(a => DateOnly.FromDateTime(a.Start))
                .ToHashSet();
        }
        catch
        {
            return; // geen agenda te lezen: dan gewoon geen markering
        }
        if (bezet.Count == 0 || IsDisposed)
        {
            return;
        }
        var keuzes = DagKeuzes(bezet).ToList();
        // De eerste vrije avonden, om de standaardkeuzes naartoe te schuiven (HelloFresh-idee:
        // het voorstel houdt al rekening met je agenda; wie zelf al koos blijft staan).
        var vrij = Enumerable.Range(0, keuzes.Count)
            .Where(i => !bezet.Contains(DateOnly.FromDateTime(keuzes[i].Datum)))
            .ToList();
        var volgendeVrije = 0;
        foreach (var rij in _rijen)
        {
            var gekozen = rij.Datum.SelectedIndex;
            rij.Datum.BeginUpdate();
            rij.Datum.Items.Clear();
            foreach (var dag in keuzes)
            {
                rij.Datum.Items.Add(dag);
            }
            if (gekozen == rij.StandaardIndex && vrij.Count > 0)
            {
                gekozen = vrij[Math.Min(volgendeVrije++, vrij.Count - 1)];
            }
            rij.Datum.SelectedIndex = gekozen;
            rij.Datum.EndUpdate();
        }
    }

    private static string Hoofdletter(string tekst) =>
        tekst.Length > 0 ? char.ToUpper(tekst[0], NlBe) + tekst[1..] : tekst;

    private void OpFotoKlaar()
    {
        if (IsDisposed)
        {
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                foreach (var rij in _rijen.Where(r => r.Foto.Image is null))
                {
                    rij.Foto.Image = GerechtFoto.Voor(rij.Naam);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Venster net gesloten: niets meer te tonen.
        }
    }

    private void Bevestig()
    {
        foreach (var rij in _rijen.Where(r => r.Aan.Checked))
        {
            if (rij.Datum.SelectedItem is not DagKeuze dag)
            {
                continue;
            }
            var tijd = rij.MiddagKeuze.Checked ? Middag : _tijd.Value.TimeOfDay;
            var start = dag.Datum.Date + tijd;
            // Altijd een uur blokkeren, ongeacht de bereidingstijd: een blok van 20 of 45
            // minuten in de agenda dekt het aan tafel gaan niet, en die blokjes van
            // wisselende lengte maakten de dag alleen maar rommelig.
            Geplande.Add((rij.Naam, start, ReceptDuur));
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
