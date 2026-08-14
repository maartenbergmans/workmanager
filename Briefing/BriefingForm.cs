using System.Globalization;

namespace WorkManager;

/// <summary>
/// Het dagstartvenster: links de briefing van vandaag (samenvatting, focus, aandachtspunten
/// en de voorbereiding van de eerstvolgende afspraak), rechts de agenda van vandaag. De
/// briefing komt uit <see cref="DagBriefing"/> — bij het openen wordt de bewaarde versie van
/// vandaag getoond, of meteen een nieuwe gemaakt als die er nog niet is.
/// </summary>
public class BriefingForm : Form
{
    private readonly FlowLayoutPanel _kaarten;
    private readonly ModernListView _agenda;
    private readonly Label _status;
    private readonly ModernButton _vernieuw;
    private readonly ModernButton _prepKnop;
    private readonly CancellationTokenSource _cts = new();
    private bool _herschalen;

    public BriefingForm()
    {
        Text = "Dagstart";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 760);
        MinimumSize = new Size(760, 520);

        var werkbalk = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = false };
        Theme.AsToolbar(werkbalk);

        _vernieuw = new ModernButton
        {
            Text = "Briefing vernieuwen", Width = 180, Kind = ButtonKind.Accent, Glyph = Fluent.Sync,
        };
        _vernieuw.Click += async (_, _) => await LaadAsync(forceer: true);

        _prepKnop = new ModernButton
        {
            Text = "Volgende afspraak voorbereiden", Width = 250, Glyph = Fluent.Kalender,
        };
        _prepKnop.Click += async (_, _) => await BereidVolgendeVoor();

        var reisKnop = new ModernButton { Text = "Reisassistent…", Width = 150, Glyph = Fluent.Globe };
        reisKnop.Click += (_, _) =>
        {
            using var venster = new ReisSettingsForm();
            if (venster.ShowDialog(this) == DialogResult.OK)
            {
                Toast.Toon(this, "Reisinstellingen bewaard", Fluent.Check);
            }
        };

        _status = new Label { AutoSize = true, Text = "" };
        Theme.AsStatus(_status);

        werkbalk.Controls.AddRange(new Control[] { _vernieuw, _prepKnop, reisKnop, _status });

        _kaarten = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(14, 12, 14, 12),
            BackColor = Theme.Bg,
        };
        _kaarten.Resize += (_, _) => Herschaal();
        Theme.DarkScrollbars(_kaarten);

        _agenda = new ModernListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            LegeTekst = "Geen afspraken vandaag.",
            LeegGlyph = Fluent.Kalender,
            HeeftCheckbox = _ => false,
        };
        _agenda.Columns.Add("Wanneer", 110);
        _agenda.Columns.Add("Afspraak", 230);
        _agenda.Columns.Add("Waar", 150);
        _agenda.Resize += (_, _) => _agenda.Columns[1].Width = Math.Max(120,
            _agenda.ClientSize.Width - _agenda.Columns[0].Width - _agenda.Columns[2].Width - 4);

        var rechts = new Panel { Dock = DockStyle.Right, Width = 430, Padding = new Padding(0, 0, 0, 0) };
        var agendaKop = new Label
        {
            Dock = DockStyle.Top, Height = 30, Text = "AGENDA VANDAAG", Font = Theme.CaptionFont,
            ForeColor = Theme.Muted, Padding = new Padding(12, 9, 0, 0), BackColor = Theme.Surface,
        };
        rechts.Controls.Add(_agenda);
        rechts.Controls.Add(agendaKop);
        rechts.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, rechts.Height);
        };

        Controls.Add(_kaarten);
        Controls.Add(rechts);
        Controls.Add(werkbalk);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "briefing");

        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
        Shown += async (_, _) => await LaadAsync(forceer: false);
    }

    /// <summary>
    /// Toont de briefing van vandaag; maakt er een als die er nog niet is, of opnieuw bij
    /// <paramref name="forceer"/>.
    /// </summary>
    private async Task LaadAsync(bool forceer)
    {
        var bestaand = DagBriefing.VanVandaag();
        if (bestaand is not null && !forceer)
        {
            Toon(bestaand);
        }
        else
        {
            Toon(null);
        }

        if (bestaand is not null && !forceer)
        {
            return;
        }
        if (DagBriefing.Bezig)
        {
            _status.Text = "De briefing wordt al samengesteld…";
            return;
        }

        _vernieuw.Bezig = true;
        _status.Text = "Claude stelt de briefing samen…";
        try
        {
            var briefing = await DagBriefing.MaakAsync(_cts.Token, forceer);
            if (!IsDisposed)
            {
                Toon(briefing);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            _status.Text = $"Briefing mislukt: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _vernieuw.Bezig = false;
            }
        }
    }

    private async Task BereidVolgendeVoor()
    {
        _prepKnop.Bezig = true;
        _status.Text = "Afspraak voorbereiden…";
        try
        {
            var agenda = await DagBriefing.AgendaVanVandaagAsync(_cts.Token);
            var volgende = agenda.FirstOrDefault(a => !a.HeleDag && a.Einde > DateTimeOffset.Now);
            if (volgende is null)
            {
                _status.Text = "Geen afspraak meer vandaag.";
                return;
            }
            await MeetingPrep.MaakPrepAsync(volgende, null, _cts.Token, forceer: true);
            if (DagBriefing.VanVandaag() is { } briefing)
            {
                Toon(briefing);
            }
            _status.Text = $"Voorbereiding klaar voor \"{volgende.Titel}\".";
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _status.Text = $"Voorbereiden mislukt: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _prepKnop.Bezig = false;
            }
        }
    }

    /// <summary>Bouwt de kaarten en de agendalijst op; null = nog geen briefing beschikbaar.</summary>
    private void Toon(DagBriefingData? briefing)
    {
        _kaarten.SuspendLayout();
        foreach (Control oud in _kaarten.Controls)
        {
            oud.Dispose();
        }
        _kaarten.Controls.Clear();

        var vandaag = DateTime.Today;
        var kop = new BriefingKaart
        {
            Titel = vandaag.ToString("dddd d MMMM", new CultureInfo("nl-BE")),
            Glyph = Fluent.Ster,
            Accent = Theme.Accent,
            Tekst = briefing?.Samenvatting.Length > 0
                ? briefing.Samenvatting
                : "Nog geen briefing voor vandaag — Claude stelt er een samen.",
            // Alleen de kopkaart krijgt het themamotief; verder blijft het venster sober.
            MetEmbleem = true,
        };
        _kaarten.Controls.Add(kop);

        if (briefing is not null)
        {
            var praktisch = new BriefingKaart
            {
                Titel = "Praktisch", Glyph = Fluent.Globe, Accent = Theme.Success,
                LegeTekst = "Geen weer- of reisinfo (stel je thuisadres in bij Reisassistent).",
            };
            if (briefing.Weer.Length > 0)
            {
                praktisch.Punten.Add("Weer: " + briefing.Weer);
            }
            if (briefing.Reis.Length > 0)
            {
                praktisch.Punten.Add("Onderweg: " + briefing.Reis);
            }
            praktisch.Punten.Add(
                $"{briefing.Afspraken} afspraken · {briefing.OpenTaken} open taken · " +
                $"{briefing.WachtendeBerichten} berichten wachten");
            _kaarten.Controls.Add(praktisch);

            var focus = new BriefingKaart
            {
                Titel = "Focus vandaag", Glyph = Fluent.Checkbox, Accent = Theme.Accent,
                LegeTekst = "Geen focuspunten.",
            };
            foreach (var punt in briefing.Focus)
            {
                focus.Punten.Add(punt);
            }
            _kaarten.Controls.Add(focus);

            if (briefing.Attentie.Count > 0)
            {
                var attentie = new BriefingKaart
                {
                    Titel = "Let op", Glyph = Fluent.Klok, Accent = Theme.Warn,
                };
                foreach (var punt in briefing.Attentie)
                {
                    attentie.Punten.Add(punt);
                }
                _kaarten.Controls.Add(attentie);
            }
        }

        if (MeetingPrep.Volgende() is { } prep)
        {
            var kaart = new BriefingKaart
            {
                Titel = $"Voorbereiding · {prep.Start:HH:mm} {prep.Titel}",
                Glyph = Fluent.People,
                Accent = Theme.Warn,
                Tekst = prep.Samenvatting,
            };
            if (prep.Reis.Length > 0)
            {
                kaart.Punten.Add("Onderweg: " + prep.Reis);
            }
            if (prep.Deelnemers.Count > 0)
            {
                kaart.Punten.Add("Met: " + string.Join(", ", prep.Deelnemers.Take(8)));
            }
            foreach (var punt in prep.Punten)
            {
                kaart.Punten.Add("Bespreken: " + punt);
            }
            foreach (var vraag in prep.Vragen)
            {
                kaart.Punten.Add("Vragen: " + vraag);
            }
            _kaarten.Controls.Add(kaart);
        }

        // Afsluiter: het citaat van de dag, in de toon van het thema. Eén regel, gedempt,
        // en de hele dag hetzelfde — het hoort bij de dagstart, niet bij het werk zelf.
        _kaarten.Controls.Add(new BriefingKaart
        {
            Titel = "Voor onderweg",
            Glyph = Fluent.Ster,
            Accent = Theme.Muted,
            Tekst = ThemaCitaat.Aangehaald(),
        });
        _kaarten.ResumeLayout();
        Herschaal();

        _agenda.BeginUpdate();
        _agenda.Items.Clear();
        foreach (var regel in briefing?.Agenda ?? new List<string>())
        {
            // De bewaarde regel heeft de vorm "09:00–10:00 · Titel (Locatie)".
            var delen = regel.Split(" · ", 2);
            var wanneer = delen[0];
            var rest = delen.Length > 1 ? delen[1] : regel;
            var locatie = "";
            var haakje = rest.LastIndexOf(" (", StringComparison.Ordinal);
            if (haakje > 0 && rest.EndsWith(')'))
            {
                locatie = rest[(haakje + 2)..^1];
                rest = rest[..haakje];
            }
            var item = new ListViewItem(new[] { wanneer, rest, locatie });
            if (DateTime.TryParse(wanneer.Split('–')[0], out var start) && start < DateTime.Now)
            {
                item.ForeColor = Theme.Muted; // al voorbij
            }
            _agenda.Items.Add(item);
        }
        _agenda.EndUpdate();

        _status.Text = briefing is null
            ? ""
            : $"Bijgewerkt om {briefing.GemaaktOp:HH:mm}";
    }

    /// <summary>Geeft elke kaart de breedte van de kolom (de kaart rekent zijn hoogte zelf uit).</summary>
    private void Herschaal()
    {
        if (_herschalen)
        {
            return;
        }
        _herschalen = true;
        try
        {
            var breedte = _kaarten.ClientSize.Width - _kaarten.Padding.Horizontal -
                          SystemInformation.VerticalScrollBarWidth;
            foreach (Control control in _kaarten.Controls)
            {
                if (control is BriefingKaart kaart)
                {
                    kaart.ZetBreedte(breedte);
                }
            }
        }
        finally
        {
            _herschalen = false;
        }
    }
}
