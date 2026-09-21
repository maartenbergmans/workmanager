using System.Diagnostics;
using System.Globalization;

namespace WorkManager;

/// <summary>
/// Het portefeuillevenster: bovenaan de totale waarde met de beweging van vandaag, daaronder
/// het waardeverloop en per positie de details. De koersen komen van de beurs (Yahoo Finance)
/// en verversen zichzelf zolang het venster openstaat — elke minuut terwijl de beurs open is,
/// daarbuiten om de vijf minuten.
///
/// Discretie is een uitgangspunt: met het oogje gaan alle bedragen achter bolletjes en blijven
/// alleen percentages en de vorm van de grafiek over. Die keuze wordt onthouden, dus ook het
/// knopje in de cockpit blijft dan zwijgen over bedragen.
/// </summary>
public sealed class PortefeuilleForm : Form
{
    private readonly PortefeuilleKop _kop = new() { Dock = DockStyle.Top };
    private readonly WaardeGrafiek _grafiek = new()
    {
        Dock = DockStyle.Top,
        Height = 232,
        LegeTekst = "Koershistoriek ophalen…",
    };
    private readonly ModernListView _lijst;
    private readonly FlowLayoutPanel _potjeBalk;
    private readonly FlowLayoutPanel _periodeBalk;
    private readonly Label _periodeLabel;
    private readonly Label _status;
    private readonly ModernButton _verversKnop;
    private readonly ModernButton _oogKnop;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private readonly CancellationTokenSource _cts = new();

    private Portefeuille _portefeuille = PortefeuilleStore.Laad();
    private PortefeuilleStand? _stand;
    private bool _bezig;

    public PortefeuilleForm()
    {
        Text = "Portefeuille";
        Size = new Size(1180, 820);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Theme.AppIcon;

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            RijHoogte = 30,
            LegeTekst = "Nog geen posities — voeg ze toe met “Posities…”",
            LeegGlyph = Fluent.Lijst,
        };
        _lijst.Columns.Add("Fonds", 300);
        _lijst.Columns.Add("Ticker", 100);
        _lijst.Columns.Add("Potje", 110);
        _lijst.Columns.Add("Aantal", 80, HorizontalAlignment.Right);
        _lijst.Columns.Add("Koers", 100, HorizontalAlignment.Right);
        _lijst.Columns.Add("Vandaag", 120, HorizontalAlignment.Right);
        _lijst.Columns.Add("Waarde", 120, HorizontalAlignment.Right);
        _lijst.Columns.Add("Aandeel", 80, HorizontalAlignment.Right);
        _lijst.Columns.Add("Rendement", 130, HorizontalAlignment.Right);
        _lijst.Resize += (_, _) => SchaalKolommen();
        _lijst.SelectedIndexChanged += (_, _) => ToonStatus();
        _lijst.DoubleClick += (_, _) => OpenFondspagina();

        _potjeBalk = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(0, 8, 0, 4),
            WrapContents = false,
            AutoScroll = false,
        };
        _potjeBalk.BackColor = Theme.Bg;

        _periodeBalk = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(0, 8, 0, 6),
            WrapContents = false,
        };
        _periodeBalk.BackColor = Theme.Bg;
        foreach (var (label, bereik, _) in PortefeuilleMeting.Periodes)
        {
            var knop = new ModernButton { Text = label, Width = 52, Tag = label };
            knop.Click += async (_, _) =>
            {
                _portefeuille.Periode = label;
                PortefeuilleStore.Bewaar(_portefeuille);
                MarkeerPeriodeKnoppen();
                await VerversGrafiekAsync(bereik);
            };
            _periodeBalk.Controls.Add(knop);
        }
        _periodeLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(14, 8, 0, 0),
            ForeColor = Theme.Muted,
            Text = "",
        };
        _periodeBalk.Controls.Add(_periodeLabel);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(2, 7, 2, 0),
            Text = "Koersen ophalen…",
        };
        Theme.AsStatus(_status);

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(0, 8, 0, 6),
        };
        var sluit = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 100 };
        sluit.Click += (_, _) => Close();
        _verversKnop = new ModernButton { Text = "Verversen", Width = 120, Glyph = Fluent.Sync };
        _verversKnop.Click += async (_, _) => await VerversAsync(metGrafiek: true);
        var positiesKnop = new ModernButton { Text = "Posities…", Width = 120, Glyph = Fluent.Edit };
        positiesKnop.Click += (_, _) => OpenPosities();
        _oogKnop = new ModernButton { Width = 132 };
        _oogKnop.Click += (_, _) => WisselVerbergen();
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_verversKnop);
        knoppen.Controls.Add(positiesKnop);
        knoppen.Controls.Add(_oogKnop);
        CancelButton = sluit;

        var inhoud = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 6) };
        inhoud.Controls.Add(_lijst);
        inhoud.Controls.Add(_periodeBalk);
        inhoud.Controls.Add(_grafiek);
        inhoud.Controls.Add(_kop);
        inhoud.Controls.Add(_potjeBalk);
        Controls.Add(inhoud);
        Controls.Add(_status);
        Controls.Add(knoppen);

        _grafiek.Gepeild += punt => ToonPeiling(punt);

        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "portefeuille");

        void Herkleur()
        {
            if (IsDisposed)
            {
                return;
            }
            _potjeBalk.BackColor = Theme.Bg;
            _periodeBalk.BackColor = Theme.Bg;
            _periodeLabel.ForeColor = Theme.Muted;
            ToonStand(); // rijkleuren komen uit het palet
            _kop.Invalidate();
            _grafiek.Invalidate();
        }
        Theme.ThemaGewijzigd += Herkleur;

        _timer.Tick += async (_, _) => await VerversAsync(metGrafiek: false);
        Shown += async (_, _) =>
        {
            BouwPotjeKnoppen();
            MarkeerPeriodeKnoppen();
            WerkOogKnopBij();
            // Meteen iets op het scherm uit de koerscache — ook de grafiek, die zijn punten
            // uit dezelfde cache haalt. Pas daarna het (soms trage) netwerk.
            _stand = PortefeuilleMeting.UitCache(_portefeuille);
            ToonStand();
            await VerversGrafiekAsync(Bereik);
            _timer.Start();
            await VerversAsync(metGrafiek: true);
        };
        FormClosed += (_, _) =>
        {
            Theme.ThemaGewijzigd -= Herkleur;
            _timer.Stop();
            _cts.Cancel();
        };
    }

    private string Potje => _portefeuille.Filter;

    private string Bereik
    {
        get
        {
            foreach (var periode in PortefeuilleMeting.Periodes)
            {
                if (periode.Label == _portefeuille.Periode)
                {
                    return periode.Bereik;
                }
            }
            return "1y";
        }
    }

    // ---------- Verversen ----------

    private async Task VerversAsync(bool metGrafiek)
    {
        if (_bezig)
        {
            return;
        }
        _bezig = true;
        _verversKnop.Bezig = true;
        try
        {
            var stand = await PortefeuilleMeting.MeetAsync(_portefeuille, _cts.Token);
            if (_cts.IsCancellationRequested || IsDisposed)
            {
                return;
            }
            _stand = stand;
            if (!stand.UitCache && !stand.Onvolledig)
            {
                PortefeuilleHistoriek.Noteer(stand.Totaal);
                WerkMijlpalenBij(stand);
            }
            ToonStand();
            // Zolang de beurs open is loont het om de minuut te kijken; daarbuiten beweegt er
            // niets meer en hoeft Yahoo niet elke minuut een verzoek te krijgen.
            _timer.Interval = stand.BeursOpen ? 60_000 : 300_000;
            if (metGrafiek)
            {
                await VerversGrafiekAsync(Bereik);
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _status.Text = $"Koersen ophalen mislukt: {ex.Message}";
        }
        finally
        {
            _bezig = false;
            if (!IsDisposed)
            {
                _verversKnop.Bezig = false;
            }
        }
    }

    private async Task VerversGrafiekAsync(string bereik)
    {
        try
        {
            var verloop = await PortefeuilleMeting.VerloopAsync(
                _portefeuille, bereik, Potje, _stand, _cts.Token);
            if (_cts.IsCancellationRequested || IsDisposed)
            {
                return;
            }
            _grafiek.BedragenVerborgen = _portefeuille.BedragenVerborgen;
            _grafiek.Punten = verloop.Punten;
            var uitleg = "de periode";
            foreach (var periode in PortefeuilleMeting.Periodes)
            {
                if (periode.Bereik == bereik)
                {
                    uitleg = periode.Uitleg;
                }
            }
            if (!verloop.Bruikbaar)
            {
                _grafiek.LegeTekst = "Nog geen koershistoriek voor deze periode";
                _grafiek.Invalidate();
                _periodeLabel.Text = "";
                return;
            }
            _periodeLabel.Text = _portefeuille.BedragenVerborgen
                ? $"{Bedrag.Procent(verloop.Procent)} over {uitleg}"
                : $"{Bedrag.Procent(verloop.Procent)}  ·  {Bedrag.EuroDelta(verloop.Verschil)} " +
                  $"over {uitleg}";
            _periodeLabel.ForeColor = Bedrag.Kleur(verloop.Verschil);
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            _periodeLabel.Text = "Koershistoriek niet beschikbaar";
            _periodeLabel.ForeColor = Theme.Muted;
        }
    }

    // ---------- Tonen ----------

    private void ToonStand()
    {
        _kop.BedragenVerborgen = _portefeuille.BedragenVerborgen;
        _kop.Toon(_stand, Potje);
        VulLijst();
        ToonStatus();
    }

    private void VulLijst()
    {
        var zichtbaar = _stand is null
            ? new List<PositieWaarde>()
            : _stand.Filter(Potje).Regels
                .OrderBy(r => r.Positie.Potje, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Waarde)
                .ToList();
        var totaal = zichtbaar.Sum(r => r.Waarde);
        var verbergen = _portefeuille.BedragenVerborgen;
        var geselecteerd = _lijst.SelectedItems.Count > 0
            ? (_lijst.SelectedItems[0].Tag as PositieWaarde)?.Positie.Sleutel
            : null;

        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var regel in zichtbaar)
        {
            var naam = regel.Koers?.Naam is { Length: > 0 } officieel && regel.Positie.Naam.Length == 0
                ? officieel
                : regel.Positie.Naam;
            var item = new ListViewItem(KorteNaam(naam))
            {
                Tag = regel,
                UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, regel.Positie.Ticker)
            {
                ForeColor = Theme.Muted,
                Font = Theme.MonoSmall,
            });
            item.SubItems.Add(regel.Positie.Potje, PortefeuilleKop.PotjeKleur(regel.Positie.Potje, 0),
                Theme.Bg, Theme.BaseFont);
            item.SubItems.Add(Aantal(regel.Positie.Aantal));
            item.SubItems.Add(regel.Koers is null ? "—" : Bedrag.EuroPrecies(regel.Prijs));
            var dag = new ListViewItem.ListViewSubItem(item,
                regel.Koers is null
                    ? "—"
                    : $"{Bedrag.EuroDelta(regel.DagVerschil, verbergen)}  {Bedrag.Procent(regel.DagProcent)}")
            {
                ForeColor = regel.Koers is null ? Theme.Muted : Bedrag.Kleur(regel.DagVerschil),
            };
            item.SubItems.Add(dag);
            item.SubItems.Add(Bedrag.Euro(regel.Waarde, verbergen));
            item.SubItems.Add(totaal > 0 ? (regel.Waarde / totaal * 100).ToString("N1",
                CultureInfo.GetCultureInfo("nl-BE")) + " %" : "—");
            var rendement = new ListViewItem.ListViewSubItem(item,
                regel.RendementProcent is { } rp
                    ? $"{Bedrag.EuroDelta(regel.Rendement!.Value, verbergen)}  {Bedrag.Procent(rp, 1)}"
                    : "—")
            {
                ForeColor = regel.Rendement is { } r ? Bedrag.Kleur(r) : Theme.Muted,
            };
            item.SubItems.Add(rendement);
            _lijst.Items.Add(item);
            if (regel.Positie.Sleutel == geselecteerd)
            {
                item.Selected = true;
            }
        }
        _lijst.EndUpdate();
        SchaalKolommen();
    }

    private void ToonStatus()
    {
        if (_stand is null)
        {
            return;
        }
        // Staat er een positie geselecteerd, dan vertelt de statusregel over dát fonds.
        if (_lijst.SelectedItems.Count > 0 &&
            _lijst.SelectedItems[0].Tag is PositieWaarde { Koers: { } koers } regel)
        {
            var jaar = double.IsNaN(koers.JaarPositie)
                ? ""
                : $" · 52 weken {Bedrag.EuroPrecies(koers.JaarLaag)}–{Bedrag.EuroPrecies(koers.JaarHoog)} " +
                  $"(nu op {koers.JaarPositie * 100:N0} % van dat bereik)";
            _status.Text = $"{koers.Naam} · {koers.Ticker} op {koers.Beurs} · dagbereik " +
                           $"{Bedrag.EuroPrecies(koers.DagLaag)}–{Bedrag.EuroPrecies(koers.DagHoog)}{jaar}" +
                           $" · {regel.Positie.Aantal:N0} stuks";
            _status.ForeColor = Theme.Muted;
            return;
        }

        var tijd = _stand.Moment.ToString("HH:mm", CultureInfo.GetCultureInfo("nl-BE"));
        var vandaag = _stand.Moment.Date == DateTime.Today ? "" : $" ({_stand.Moment:d MMM})";
        if (_stand.UitCache)
        {
            _status.Text = $"Geen verbinding met de koersbron — laatst bekende koersen van {tijd}{vandaag}.";
            _status.ForeColor = Theme.Warn;
            return;
        }
        var beurs = _stand.BeursOpen ? "beurs open, ververst elke minuut" : "beurs gesloten";
        var inleg = _stand.RendementProcent is { } rp
            ? $" · sinds aankoop {Bedrag.Procent(rp, 1)}"
            : "";
        _status.Text = $"Koersen van {tijd}{vandaag} · {beurs}{inleg}" +
                       (_stand.Onvolledig ? " · let op: van minstens één fonds ontbreekt de koers" : "");
        _status.ForeColor = _stand.Onvolledig ? Theme.Warn : Theme.Muted;
    }

    private void ToonPeiling((DateOnly Dag, double Waarde)? punt)
    {
        if (punt is null)
        {
            ToonStatus();
            return;
        }
        var nl = CultureInfo.GetCultureInfo("nl-BE");
        var verschil = _stand is null ? 0 : _stand.Filter(Potje).Totaal - punt.Value.Waarde;
        _status.Text = _portefeuille.BedragenVerborgen
            ? $"{punt.Value.Dag.ToString("dddd d MMMM yyyy", nl)} · sindsdien " +
              $"{Bedrag.Procent(punt.Value.Waarde > 0 ? verschil / punt.Value.Waarde * 100 : 0)}"
            : $"{punt.Value.Dag.ToString("dddd d MMMM yyyy", nl)} · {Bedrag.Euro(punt.Value.Waarde)} " +
              $"· sindsdien {Bedrag.EuroDelta(verschil)}";
        _status.ForeColor = Theme.Muted;
    }

    // ---------- Knoppen ----------

    private void BouwPotjeKnoppen()
    {
        _potjeBalk.Controls.Clear();
        var potjes = new List<string> { "" };
        potjes.AddRange(_portefeuille.Potjes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        // Een filter op een potje dat niet meer bestaat: terug naar alles.
        if (Potje.Length > 0 && !potjes.Contains(Potje, StringComparer.OrdinalIgnoreCase))
        {
            _portefeuille.Filter = "";
        }
        foreach (var potje in potjes)
        {
            var knop = new ModernButton
            {
                Text = potje.Length == 0 ? "Alles" : potje,
                Tag = potje,
                Kind = potje.Equals(Potje, StringComparison.OrdinalIgnoreCase)
                    ? ButtonKind.Accent
                    : ButtonKind.Normal,
            };
            knop.KrimpNaarInhoud();
            knop.Click += async (_, _) =>
            {
                _portefeuille.Filter = potje;
                PortefeuilleStore.Bewaar(_portefeuille);
                MarkeerPotjeKnoppen();
                ToonStand();
                await VerversGrafiekAsync(Bereik);
            };
            _potjeBalk.Controls.Add(knop);
        }
    }

    private void MarkeerPotjeKnoppen()
    {
        foreach (var knop in _potjeBalk.Controls.OfType<ModernButton>())
        {
            knop.Kind = (knop.Tag as string ?? "").Equals(Potje, StringComparison.OrdinalIgnoreCase)
                ? ButtonKind.Accent
                : ButtonKind.Normal;
            knop.Invalidate();
        }
    }

    private void MarkeerPeriodeKnoppen()
    {
        foreach (var knop in _periodeBalk.Controls.OfType<ModernButton>())
        {
            knop.Kind = (knop.Tag as string) == _portefeuille.Periode
                ? ButtonKind.Accent
                : ButtonKind.Normal;
            knop.Invalidate();
        }
    }

    private void WerkOogKnopBij()
    {
        _oogKnop.Text = _portefeuille.BedragenVerborgen ? "🙈 Bedragen tonen" : "👁 Bedragen verbergen";
        _oogKnop.KrimpNaarInhoud();
    }

    private void WisselVerbergen()
    {
        _portefeuille.BedragenVerborgen = !_portefeuille.BedragenVerborgen;
        PortefeuilleStore.Bewaar(_portefeuille);
        WerkOogKnopBij();
        ToonStand();
        _ = VerversGrafiekAsync(Bereik);
    }

    /// <summary>
    /// "iShares Core MSCI World UCITS ETF USD (Acc)" wordt "iShares Core MSCI World": het
    /// juridische staartje zegt niets in een overzicht en duwt de rest uit beeld. De volledige
    /// naam blijft in de statusregel en op de fondspagina staan.
    /// </summary>
    private static string KorteNaam(string naam)
    {
        foreach (var staart in new[] { " UCITS", " ETF", " Fund", " USD", " EUR", " (Acc" })
        {
            var index = naam.IndexOf(staart, StringComparison.OrdinalIgnoreCase);
            if (index > 8)
            {
                naam = naam[..index];
            }
        }
        return naam.Trim();
    }

    /// <summary>Hele stukken zonder decimalen, fracties met vier — sommige brokers verkopen die.</summary>
    private static string Aantal(double aantal) =>
        aantal % 1 == 0
            ? aantal.ToString("N0", CultureInfo.GetCultureInfo("nl-BE"))
            : aantal.ToString("N4", CultureInfo.GetCultureInfo("nl-BE"));

    private void OpenPosities()
    {
        using var form = new PositiesForm(_portefeuille);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _portefeuille = PortefeuilleStore.Laad();
        BouwPotjeKnoppen();
        _ = VerversAsync(metGrafiek: true);
    }

    private void OpenFondspagina()
    {
        if (_lijst.SelectedItems.Count == 0 || _lijst.SelectedItems[0].Tag is not PositieWaarde regel)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(
                $"https://finance.yahoo.com/quote/{Uri.EscapeDataString(regel.Positie.Ticker)}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Openen mislukt: {ex.Message}", Fluent.Globe);
        }
    }

    /// <summary>
    /// Kolombreedtes uit de breedste inhoud die erin kan staan (zo blijven ze kloppen op een
    /// scherm met schaling), en de fondsnaam krijgt wat overblijft. De rendementkolom valt
    /// weg zolang er nergens een aankoopkoers ingevuld is: anders staat er alleen "—".
    /// </summary>
    private void SchaalKolommen()
    {
        if (_lijst.Columns.Count < 9)
        {
            return;
        }
        int Breedte(int kolom, string breedste) => Math.Max(
            TextRenderer.MeasureText(breedste, Theme.BaseFont).Width + 22,
            TextRenderer.MeasureText(_lijst.Columns[kolom].Text.ToUpperInvariant(),
                Theme.CaptionFont).Width + 20);

        _lijst.Columns[1].Width = Breedte(1, "IWDA.AS");
        _lijst.Columns[2].Width = Breedte(2, "Vennootschap");
        _lijst.Columns[3].Width = Breedte(3, "88.888");
        _lijst.Columns[4].Width = Breedte(4, "€ 8.888,88");
        _lijst.Columns[5].Width = Breedte(5, "−€ 8.888  −8,88 %");
        _lijst.Columns[6].Width = Breedte(6, "€ 888.888");
        _lijst.Columns[7].Width = Breedte(7, "88,8 %");
        _lijst.Columns[8].Width = _portefeuille.Posities.Any(p => p.AankoopKoers is > 0)
            ? Breedte(8, "+€ 88.888  +88,8 %")
            : 0;

        var vast = 0;
        for (var i = 1; i < _lijst.Columns.Count; i++)
        {
            vast += _lijst.Columns[i].Width;
        }
        // Ruimte voor de verticale scrollbalk alleen reserveren als er ook echt gescrold moet
        // worden; anders bleef er bij een korte lijst een lege strook naast de laatste kolom.
        var scrolt = _lijst.Items.Count * (_lijst.RijHoogte + 1) > _lijst.ClientSize.Height - 30;
        _lijst.Columns[0].Width = Math.Max(200, _lijst.ClientSize.Width - vast - 4 -
            (scrolt ? SystemInformation.VerticalScrollBarWidth : 0));
    }

    /// <summary>
    /// Houdt de hoogste stand ooit bij en viert elke mijlpaal van € 25.000 één keer — in het
    /// venster zelf, niet als melding op het bureaublad: dit hoort niemand anders te zien.
    /// </summary>
    private void WerkMijlpalenBij(PortefeuilleStand stand)
    {
        const double stap = 25_000;
        var totaal = stand.Totaal;
        var bijzonder = "";

        var mijlpaal = Math.Floor(totaal / stap) * stap;
        if (mijlpaal > _portefeuille.GevierdeMijlpaal && mijlpaal > 0)
        {
            var eerste = _portefeuille.GevierdeMijlpaal <= 0;
            _portefeuille.GevierdeMijlpaal = mijlpaal;
            if (!eerste)
            {
                bijzonder = _portefeuille.BedragenVerborgen
                    ? "🎉 nieuwe mijlpaal"
                    : $"🎉 {Bedrag.Euro(mijlpaal)} voorbij";
            }
        }
        if (totaal > _portefeuille.HoogsteStand + 0.5)
        {
            var eerste = _portefeuille.HoogsteStand <= 0;
            _portefeuille.HoogsteStand = totaal;
            _portefeuille.HoogsteStandOp = DateTimeOffset.Now;
            if (!eerste && bijzonder.Length == 0)
            {
                bijzonder = "🏔 hoogste stand ooit";
            }
        }
        else if (bijzonder.Length == 0 && _portefeuille.HoogsteStandOp is { } top)
        {
            var dagen = (DateTime.Today - top.LocalDateTime.Date).Days;
            if (dagen >= 30)
            {
                bijzonder = $"top stond {dagen} dagen geleden";
            }
        }
        PortefeuilleStore.Bewaar(_portefeuille);
        _kop.Bijzonder = bijzonder;
    }
}
