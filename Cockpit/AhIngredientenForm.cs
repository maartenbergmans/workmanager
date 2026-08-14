using System.Diagnostics;
using System.Text.Json;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Bewerkt de productlinks (en aantallen) van de AH-ingrediënten. Links staan alle groepen
/// (gerechten, suggesties, rubrieken zoals Fruit of Snacks), rechts de producten van de
/// gekozen groep met hun ah.be-link. Een link plak je in het veld onderaan, of je zoekt hem
/// via "Kies op ah.be…"; "Openen op ah.be" (of dubbelklik) toont het product in de browser.
/// De aanroeper bewaart het resultaat in ah-gerechten.json; ingrediënten zonder eigen link
/// blijven daar gewone strings — die krijgen hun product uit de lokale producttabel.
/// </summary>
public class AhIngredientenForm : Form
{
    /// <summary>Eén gerecht, suggestie of rubriek in de linkerlijst.</summary>
    private sealed class Groep
    {
        public required string Naam { get; init; }
        public required List<AhIngredient> Producten { get; init; }

        /// <summary>Gerecht of suggestie (heeft een recept), i.t.t. een rubriek zoals Fruit.</summary>
        public required bool Maaltijd { get; init; }
    }

    private readonly ModernListView _groepen;
    private readonly ModernListView _lijst;
    private readonly TextBox _url;
    private readonly TextBox _nieuw;
    private readonly TextBox _nieuwUrl;
    private readonly NumericUpDown _aantal;
    private readonly ModernButton _open;
    private readonly Panel _receptPaneel;
    private readonly TextBox _recept;
    private readonly NumericUpDown _minuten;
    private readonly NumericUpDown _personen;
    private readonly ModernButton _receptKnop;
    private bool _vullen; // velden worden programmatorisch gevuld: niet terugschrijven

    /// <summary>Bewerkte kopie van de gerechten; alleen bruikbaar na DialogResult.OK.</summary>
    public Dictionary<string, List<AhIngredient>> Gerechten { get; }

    /// <summary>Bewerkte kopie van de receptsuggesties; alleen bruikbaar na DialogResult.OK.</summary>
    public Dictionary<string, List<AhIngredient>> Suggesties { get; }

    /// <summary>Bewerkte kopie van de rubrieken; alleen bruikbaar na DialogResult.OK.</summary>
    public Dictionary<string, List<AhIngredient>> Rubrieken { get; }

    /// <summary>Bewerkte kopie van de recepten (gerecht → tekst + bereidingstijd); alleen geldig na OK.</summary>
    public Dictionary<string, Recept> Recepten { get; }

    public AhIngredientenForm(
        Dictionary<string, List<AhIngredient>> gerechten,
        Dictionary<string, List<AhIngredient>> suggesties,
        Dictionary<string, List<AhIngredient>> rubrieken,
        Dictionary<string, Recept>? recepten = null)
    {
        // Diepe kopie: annuleren mag niets aan de originele gegevens veranderen.
        Gerechten = Kopie(gerechten);
        Suggesties = Kopie(suggesties);
        Rubrieken = Kopie(rubrieken);
        Recepten = (recepten ?? new()).ToDictionary(
            r => r.Key, r => new Recept { Tekst = r.Value.Tekst, Minuten = r.Value.Minuten, Personen = r.Value.Personen });

        Text = "Albert Heijn – ingrediëntlinks";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1240, 720);
        MinimumSize = new Size(760, 460);
        MinimizeBox = false;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Kies links een gerecht of rubriek; rechts de producten. Onderaan voeg je een nieuw " +
                   "ingrediënt toe of verwijder je er een (Delete). \"Opslaan en terug\" brengt je naar de gerechtenlijst.",
        };

        _groepen = new ModernListView
        {
            Dock = DockStyle.Left,
            Width = 320,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.None,
            LegeTekst = "Geen gerechten gevonden.",
            LeegGlyph = Fluent.Winkelwagen,
            HeeftCheckbox = _ => false,
        };
        _groepen.Columns.Add("", 300);
        _groepen.Resize += (_, _) =>
            _groepen.Columns[0].Width = Math.Max(120, _groepen.ClientSize.Width - 4);
        VulGroepen("Gerechten", Gerechten, maaltijd: true);
        VulGroepen("Suggesties", Suggesties, maaltijd: true);
        VulGroepen("Rubrieken", Rubrieken, maaltijd: false);
        _groepen.SelectedIndexChanged += (_, _) => ToonGroep();

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            HideSelection = false,
            CheckBoxes = true, // vinkje = standaard aangevinkt in de keuzestap
            LegeTekst = "Kies links een gerecht of rubriek.",
            LeegGlyph = Fluent.Lijst,
        };
        _lijst.Columns.Add("Standaard mee", 280);
        _lijst.Columns.Add("Aantal", 86, HorizontalAlignment.Right);
        _lijst.Columns.Add("Productlink", 470);
        // Aantal direct in de rij aanpassen met − en +, net als in de keuzestap.
        _lijst.PlusMinKolom = 1;
        _lijst.PlusMinGeklikt += (rij, delta) =>
        {
            if (rij.Tag is not AhIngredient geklikt)
            {
                return;
            }
            geklikt.Aantal = Math.Clamp(geklikt.Aantal + delta, 1, 99);
            WerkLinkBij(rij, geklikt);
            if (rij.Selected)
            {
                VulVelden(); // het veld onderaan meteen laten meelopen
            }
        };
        _lijst.Resize += (_, _) => _lijst.Columns[2].Width = Math.Max(200,
            _lijst.ClientSize.Width - _lijst.Columns[0].Width - _lijst.Columns[1].Width - 4);
        _lijst.SelectedIndexChanged += (_, _) => VulVelden();
        _lijst.DoubleClick += (_, _) => OpenInBrowser();
        _lijst.ItemChecked += (_, e) =>
        {
            if (e.Item.Tag is AhIngredient ing)
            {
                ing.Standaard = e.Item.Checked;
            }
        };

        // Bewerkstrook voor het geselecteerde ingrediënt.
        var bewerk = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(10, 8, 10, 0),
            WrapContents = false,
        };
        var urlLabel = new Label { Text = "Link:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        _url = new TextBox { Width = 430, Enabled = false };
        var aantalLabel = new Label { Text = "Aantal:", AutoSize = true, Margin = new Padding(10, 7, 4, 0) };
        _aantal = new NumericUpDown { Width = 52, Minimum = 1, Maximum = 99, Enabled = false };
        var kies = new ModernButton
        {
            Text = "Kies op ah.be…", Width = 150, Glyph = Fluent.Zoek, Margin = new Padding(10, 0, 0, 0),
        };
        _open = new ModernButton
        {
            Text = "Openen op ah.be", Width = 160, Glyph = Fluent.Globe,
            Margin = new Padding(8, 0, 0, 0), Enabled = false,
        };
        _url.TextChanged += (_, _) => SchrijfVelden();
        _aantal.ValueChanged += (_, _) => SchrijfVelden();
        kies.Click += (_, _) => KiesOpAh();
        _open.Click += (_, _) => OpenInBrowser();
        bewerk.Controls.AddRange(new Control[] { urlLabel, _url, aantalLabel, _aantal, kies, _open });

        // Ingrediënt toevoegen aan / verwijderen uit de gekozen groep.
        var toevoegStrook = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(10, 6, 10, 0), WrapContents = false,
        };
        var nieuwLabel = new Label { Text = "Nieuw:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        _nieuw = new TextBox { Width = 200, PlaceholderText = "naam" };
        var nieuwUrlLabel = new Label { Text = "link:", AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        _nieuwUrl = new TextBox { Width = 300, PlaceholderText = "ah.be-link (optioneel)" };
        var toevoegKnop = new ModernButton { Text = "Toevoegen", Width = 120, Glyph = Fluent.Add, Margin = new Padding(8, 0, 0, 0) };
        var verwijderKnop = new ModernButton
        {
            Text = "Verwijderen", Width = 120, Glyph = Fluent.Delete, Margin = new Padding(8, 0, 0, 0),
        };
        // Enter in het naam- óf linkveld voegt meteen toe (geen dialoog sluiten).
        void EnterVoegtToe(object? _, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                VoegToe();
                e.SuppressKeyPress = true;
            }
        }
        _nieuw.KeyDown += EnterVoegtToe;
        _nieuwUrl.KeyDown += EnterVoegtToe;
        toevoegKnop.Click += (_, _) => VoegToe();
        verwijderKnop.Click += (_, _) => VerwijderGeselecteerd();
        _lijst.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                VerwijderGeselecteerd();
            }
        };
        toevoegStrook.Controls.AddRange(new Control[]
        {
            nieuwLabel, _nieuw, nieuwUrlLabel, _nieuwUrl, toevoegKnop, verwijderKnop,
        });

        // Recept + bereidingstijd, alleen zichtbaar bij een gerecht/suggestie.
        _receptPaneel = new Panel { Dock = DockStyle.Bottom, Height = 126, Padding = new Padding(10, 4, 10, 6), Visible = false };
        var receptLabel = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Theme.Muted,
            Text = "Recept (komt mee in de agenda-afspraak):",
        };
        _recept = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
        var tijdStrook = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var minLabel = new Label { Text = "Bereidingstijd:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = Theme.Muted };
        _minuten = new NumericUpDown { Width = 64, Minimum = 0, Maximum = 480, Increment = 5 };
        var minEenheid = new Label { Text = "min", AutoSize = true, Margin = new Padding(4, 6, 16, 0), ForeColor = Theme.Muted };
        var persLabel = new Label { Text = "voor", AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = Theme.Muted };
        _personen = new NumericUpDown { Width = 52, Minimum = 1, Maximum = 20, Value = 4 };
        var persEenheid = new Label { Text = "personen", AutoSize = true, Margin = new Padding(4, 6, 0, 0), ForeColor = Theme.Muted };
        _receptKnop = new ModernButton
        {
            Text = "Recept voorstellen", Width = 175, Glyph = Fluent.Ster, Margin = new Padding(20, 0, 0, 0),
        };
        _receptKnop.Click += async (_, _) => await StelReceptVoor();
        tijdStrook.Controls.AddRange(new Control[]
        {
            minLabel, _minuten, minEenheid, persLabel, _personen, persEenheid, _receptKnop,
        });
        var receptTabel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        receptTabel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        receptTabel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        receptTabel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        receptTabel.Controls.Add(receptLabel, 0, 0);
        receptTabel.Controls.Add(_recept, 0, 1);
        receptTabel.Controls.Add(tijdStrook, 0, 2);
        _receptPaneel.Controls.Add(receptTabel);
        _recept.TextChanged += (_, _) => SchrijfRecept();
        _minuten.ValueChanged += (_, _) => SchrijfRecept();
        _personen.ValueChanged += (_, _) => SchrijfRecept();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton
        {
            Text = "Terug zonder opslaan", DialogResult = DialogResult.Cancel, Width = 175,
        };
        var ok = new ModernButton
        {
            Text = "Opslaan en terug", DialogResult = DialogResult.OK, Width = 165,
            Kind = ButtonKind.Accent, Glyph = Fluent.Check,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;
        // Bewust géén AcceptButton: Enter in het "Nieuw"-veld voegt een ingrediënt toe i.p.v.
        // het venster te sluiten. Opslaan doe je met de knop "Opslaan en terug".

        Controls.Add(_lijst);
        Controls.Add(_groepen);
        Controls.Add(hint);
        Controls.Add(toevoegStrook);
        Controls.Add(_receptPaneel);
        Controls.Add(bewerk);
        Controls.Add(buttons);
        Theme.Apply(this);
        nieuwLabel.ForeColor = nieuwUrlLabel.ForeColor = Theme.Muted;
        hint.ForeColor = Theme.Muted;
        urlLabel.ForeColor = Theme.Muted;
        aantalLabel.ForeColor = Theme.Muted;
        foreach (ListViewItem rij in _groepen.Items)
        {
            if (rij.Tag is null)
            {
                rij.ForeColor = Theme.Muted;
                rij.Font = new Font(_groepen.Font, FontStyle.Bold);
            }
        }
        // Begin op het eerste gerecht, zodat de rechterlijst niet leeg opent.
        if (_groepen.Items.Cast<ListViewItem>().FirstOrDefault(r => r.Tag is Groep) is { } eerste)
        {
            eerste.Selected = true;
        }

        // Productgegevens (voor de glutenwaarschuwing) komen op de achtergrond binnen;
        // de zichtbare rijen bijwerken zodra er iets klaar is.
        AhDetails.Klaar += OpDetailsKlaar;
        FormClosed += (_, _) => AhDetails.Klaar -= OpDetailsKlaar;
    }

    /// <summary>Hertekent de rechterlijst (op de UI-thread) met de pas binnengekomen productdata.</summary>
    private void OpDetailsKlaar()
    {
        try
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }
            BeginInvoke(() =>
            {
                foreach (ListViewItem rij in _lijst.Items)
                {
                    if (rij.Tag is AhIngredient ing)
                    {
                        WerkLinkBij(rij, ing);
                    }
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Venster net gesloten: negeren.
        }
    }

    private static Dictionary<string, List<AhIngredient>> Kopie(
        Dictionary<string, List<AhIngredient>> bron) => bron.ToDictionary(
            g => g.Key,
            g => g.Value
                .Select(i => new AhIngredient { Naam = i.Naam, Url = i.Url, Aantal = i.Aantal, Standaard = i.Standaard })
                .ToList());

    /// <summary>Kopregel (Tag = null) met daaronder de groepen van die sectie.</summary>
    private void VulGroepen(string kop, Dictionary<string, List<AhIngredient>> groepen, bool maaltijd)
    {
        if (groepen.Count == 0)
        {
            return;
        }
        _groepen.Items.Add(new ListViewItem(kop.ToUpperInvariant()));
        foreach (var (naam, producten) in groepen)
        {
            _groepen.Items.Add(new ListViewItem("    " + naam)
            {
                Tag = new Groep { Naam = naam, Producten = producten, Maaltijd = maaltijd },
            });
        }
    }

    /// <summary>Vult de rechterlijst met de producten van de geselecteerde groep.</summary>
    private void ToonGroep()
    {
        var groep = _groepen.SelectedItems.Count > 0
            ? _groepen.SelectedItems[0].Tag as Groep
            : null;
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var ing in groep?.Producten ?? new List<AhIngredient>())
        {
            var rij = new ListViewItem(new[] { ing.Naam, ing.Aantal.ToString(), "" })
            {
                Tag = ing,
                Checked = ing.Standaard, // vinkje = standaard mee in de keuzestap
                UseItemStyleForSubItems = false,
            };
            _lijst.Items.Add(rij);
            WerkLinkBij(rij, ing);
        }
        _lijst.EndUpdate();
        VulVelden();
        VulRecept(groep);
    }

    /// <summary>De groep (gerecht/suggestie/rubriek) die links geselecteerd is.</summary>
    private Groep? HuidigeGroep =>
        _groepen.SelectedItems.Count > 0 ? _groepen.SelectedItems[0].Tag as Groep : null;

    /// <summary>
    /// Voegt een nieuw ingrediënt (naam + optionele link) toe aan de geselecteerde groep. Een
    /// meegegeven link is een bewuste keuze: die blijft altijd staan en wordt nooit door de
    /// automatische producttabel- of zoekmatch overschreven (die vullen alleen lege links).
    /// </summary>
    private void VoegToe()
    {
        if (HuidigeGroep is not { } groep)
        {
            Toast.Toon(this, "Kies links eerst een gerecht of rubriek", Fluent.Lijst);
            return;
        }
        var naam = _nieuw.Text.Trim();
        if (naam.Length == 0)
        {
            _nieuw.Focus();
            return;
        }
        var url = _nieuwUrl.Text.Trim();
        groep.Producten.Add(new AhIngredient { Naam = naam, Url = url.Length > 0 ? url : null });
        _nieuw.Clear();
        _nieuwUrl.Clear();
        ToonGroep();
        if (url.Length > 0)
        {
            _ = WaarschuwBijGlutenAsync(url);
        }
        // Selecteer de nieuwe (laatste) regel, klaar voor de volgende toevoeging.
        if (_lijst.Items.Count > 0)
        {
            var rij = _lijst.Items[^1];
            rij.Selected = true;
            rij.EnsureVisible();
        }
        _nieuw.Focus();
    }

    /// <summary>Verwijdert het geselecteerde ingrediënt uit de groep.</summary>
    private void VerwijderGeselecteerd()
    {
        if (Geselecteerd is not { } ing || HuidigeGroep is not { } groep)
        {
            return;
        }
        var index = _lijst.SelectedIndices.Count > 0 ? _lijst.SelectedIndices[0] : -1;
        groep.Producten.Remove(ing);
        ToonGroep();
        // Houd de selectie in de buurt van wat net verwijderd werd.
        if (_lijst.Items.Count > 0)
        {
            _lijst.Items[Math.Clamp(index, 0, _lijst.Items.Count - 1)].Selected = true;
        }
    }

    /// <summary>Toont het recept-paneel bij een gerecht/suggestie en laadt tekst + bereidingstijd.</summary>
    private void VulRecept(Groep? groep)
    {
        _vullen = true;
        try
        {
            _receptPaneel.Visible = groep is { Maaltijd: true };
            if (groep is { Maaltijd: true })
            {
                var recept = Recepten.GetValueOrDefault(groep.Naam);
                _recept.Text = recept?.Tekst ?? "";
                _minuten.Value = Math.Clamp(recept?.Minuten ?? 0, 0, 480);
                _personen.Value = Math.Clamp(recept?.Personen ?? 4, 1, 20);
            }
        }
        finally
        {
            _vullen = false;
        }
    }

    /// <summary>Schrijft het recept van de geselecteerde maaltijd terug (of verwijdert het als het leeg is).</summary>
    private void SchrijfRecept()
    {
        if (_vullen ||
            (_groepen.SelectedItems.Count > 0 ? _groepen.SelectedItems[0].Tag as Groep : null)
                is not { Maaltijd: true } groep)
        {
            return;
        }
        var tekst = _recept.Text.Trim();
        var minuten = (int)_minuten.Value;
        var personen = (int)_personen.Value;
        if (tekst.Length == 0 && minuten == 0)
        {
            Recepten.Remove(groep.Naam);
        }
        else
        {
            Recepten[groep.Naam] = new Recept { Tekst = tekst, Minuten = minuten, Personen = personen };
        }
    }

    /// <summary>
    /// Laat Claude ('claude -p' op het abonnement) een recept voorstellen op basis van de
    /// gerechtnaam en de ingrediënten van de groep, en vult tekst + bereidingstijd + personen in.
    /// </summary>
    private async Task StelReceptVoor()
    {
        if (HuidigeGroep is not { Maaltijd: true } groep)
        {
            Toast.Toon(this, "Kies links eerst een gerecht", Fluent.Lijst);
            return;
        }
        var ingredienten = groep.Producten.Select(p => p.Naam)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (ingredienten.Count == 0)
        {
            Toast.Toon(this, "Voeg eerst enkele ingrediënten toe", Fluent.Lijst);
            return;
        }

        var oudeTekst = _receptKnop.Text;
        _receptKnop.Enabled = false;
        _receptKnop.Text = "Claude denkt na…";
        try
        {
            var prompt = $$"""
                Je bent een ervaren kok. Stel een eenvoudig recept voor het gerecht "{{groep.Naam}}" op,
                op basis van deze ingrediënten:
                {{string.Join("\n", ingredienten.Select(i => "- " + i))}}

                Maarten eet glutenvrij: kies waar relevant de glutenvrije aanpak.

                Antwoord uitsluitend met JSON, exact in dit formaat (geen extra tekst):
                {"recept": "<bereidingswijze in het Nederlands, 3 tot 6 korte stappen, gescheiden door \n>", "minuten": <bereidingstijd in hele minuten>, "personen": <aantal personen als geheel getal>}
                """;

            var output = await ClaudeDrafter.RunClaudeAsync(prompt, CancellationToken.None);
            using var doc = ClaudeDrafter.ParseJson(output);
            var wortel = doc.RootElement;

            var recepttekst = wortel.TryGetProperty("recept", out var r) ? r.GetString() ?? "" : "";
            if (recepttekst.Length == 0)
            {
                Toast.Toon(this, "Claude gaf geen bruikbaar recept terug", Fluent.Ster);
                return;
            }
            _recept.Text = recepttekst.Replace("\\n", "\n").Trim();
            if (wortel.TryGetProperty("minuten", out var m) && m.TryGetInt32(out var minuten))
            {
                _minuten.Value = Math.Clamp(minuten, 0, 480);
            }
            if (wortel.TryGetProperty("personen", out var p) && p.TryGetInt32(out var personen))
            {
                _personen.Value = Math.Clamp(personen, 1, 20);
            }
            // De TextChanged/ValueChanged-handlers schrijven het recept al naar Recepten.
            Toast.Toon(this, "Recept voorgesteld door Claude — pas gerust aan", Fluent.Ster);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, "Recept voorstellen mislukt: " + ex.Message, Fluent.Ster);
        }
        finally
        {
            _receptKnop.Text = oudeTekst;
            _receptKnop.Enabled = true;
        }
    }

    /// <summary>
    /// Toont de eigen link van het ingrediënt, of anders die uit de producttabel — gedimd,
    /// want dat is een gevonden link en geen vastgelegde keuze. Bevat het product volgens de
    /// AH-gegevens gluten, dan kleurt de regel meteen rood met een ⚠ — zo zie je het al bij
    /// het toevoegen en niet pas in de keuzestap.
    /// </summary>
    private void WerkLinkBij(ListViewItem rij, AhIngredient ing)
    {
        rij.SubItems[1].Text = ing.Aantal.ToString();
        var url = ing.Url ?? AhProducten.Zoek(ing.Naam).Product?.Url;
        var bevatGluten = AhDetails.Voor(url)?.Gluten == AhApi.GlutenStatus.Bevat;
        rij.SubItems[2].Text = url is null ? "—" : (bevatGluten ? "⚠ bevat gluten — " : "") + url;
        rij.SubItems[2].ForeColor = bevatGluten
            ? Theme.Danger
            : ing.Url is null ? Theme.Muted : Theme.Text;
    }

    private AhIngredient? Geselecteerd =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as AhIngredient : null;

    /// <summary>De link die telt: de eigen link, of anders die uit de producttabel.</summary>
    private string? HuidigeUrl => Geselecteerd is { } ing
        ? ing.Url ?? AhProducten.Zoek(ing.Naam).Product?.Url
        : null;

    private void VulVelden()
    {
        _vullen = true;
        try
        {
            var ing = Geselecteerd;
            _url.Enabled = _aantal.Enabled = ing is not null;
            _url.Text = ing?.Url ?? "";
            _aantal.Value = Math.Clamp(ing?.Aantal ?? 1, 1, 99);
            _open.Enabled = HuidigeUrl is not null;
        }
        finally
        {
            _vullen = false;
        }
    }

    private void SchrijfVelden()
    {
        if (_vullen || Geselecteerd is not { } ing || _lijst.SelectedItems.Count == 0)
        {
            return;
        }
        var url = _url.Text.Trim();
        ing.Url = url.Length > 0 ? url : null;
        ing.Aantal = (int)_aantal.Value;
        WerkLinkBij(_lijst.SelectedItems[0], ing);
        _open.Enabled = HuidigeUrl is not null;
    }

    private void KiesOpAh()
    {
        if (Geselecteerd is not { } ing)
        {
            Toast.Toon(this, "Selecteer eerst een product in de lijst", Fluent.Lijst);
            return;
        }
        using var kiezer = new AhProductKiesForm(ing.Naam);
        if (kiezer.ShowDialog(this) == DialogResult.OK && kiezer.GekozenUrl is { } url)
        {
            _url.Text = url; // SchrijfVelden werkt het ingrediënt en de rij bij
            _ = WaarschuwBijGlutenAsync(url);
        }
    }

    /// <summary>
    /// Checkt een net gekozen/geplakt product meteen bij de AH-API en waarschuwt als het
    /// gluten bevat — Maarten eet glutenvrij, dus dat wil je horen vóór het in een gerecht zit.
    /// </summary>
    private async Task WaarschuwBijGlutenAsync(string url)
    {
        try
        {
            if (AhApi.WebshopId(url) is not { } id ||
                await AhApi.DetailAsync(id) is not { Gluten: AhApi.GlutenStatus.Bevat } info ||
                IsDisposed)
            {
                return;
            }
            Toast.Toon(this, $"⚠ {info.Titel} bevat gluten — kies eventueel een glutenvrij alternatief",
                Fluent.Winkelwagen);
        }
        catch
        {
            // Geen productdata: dan waarschuwt de keuzestap later alsnog.
        }
    }

    /// <summary>Opent de productpagina in de standaardbrowser (los van het WebView2-profiel).</summary>
    private void OpenInBrowser()
    {
        if (HuidigeUrl is not { } url)
        {
            Toast.Toon(this, "Voor dit product is nog geen ah.be-link bekend", Fluent.Globe);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            Clipboard.SetText(url);
            Toast.Toon(this, "Browser start niet; link staat op het klembord", Fluent.Copy);
        }
    }
}

/// <summary>
/// Ingebed browservenster om een AH-product op te zoeken: start op de zoekresultaten voor
/// het ingrediënt, en zodra een productpagina (…/producten/product/…) open staat kan die
/// met "Dit product gebruiken" gekozen worden. Deelt het browserprofiel met het
/// winkelmandje-venster, dus login en cookiekeuze gelden hier ook.
/// </summary>
public class AhProductKiesForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ModernButton _kies;
    private readonly Label _status;
    private readonly string _zoekterm;

    /// <summary>De gekozen productpagina; alleen gevuld na DialogResult.OK.</summary>
    public string? GekozenUrl { get; private set; }

    public AhProductKiesForm(string zoekterm)
    {
        _zoekterm = zoekterm;

        Text = $"AH-product kiezen – {zoekterm}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1150, 780);
        MinimizeBox = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 110 };
        _kies = new ModernButton
        {
            Text = "Dit product gebruiken", Width = 190, Kind = ButtonKind.Accent,
            Glyph = Fluent.Winkelwagen, Enabled = false,
        };
        _kies.Click += (_, _) =>
        {
            GekozenUrl = _web.CoreWebView2?.Source;
            DialogResult = DialogResult.OK;
            Close();
        };
        _status = new Label { AutoSize = true, Margin = new Padding(10, 14, 10, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_kies);
        buttons.Controls.Add(_status);
        CancelButton = cancel;

        Controls.Add(_web);
        Controls.Add(buttons);
        Shown += async (_, _) => await InitWebViewAsync();
        Theme.Apply(this, fade: false); // fade niet: WebView2 rendert niet in een gelaagd venster
        _status.ForeColor = Theme.Muted;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Zelfde profiel als het winkelmandje-venster: login en cookiekeuze gedeeld.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ah"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.SourceChanged += (_, _) => BronGewijzigd();
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                BronGewijzigd();
                await AccepteerCookiesAsync();
            };
            _web.CoreWebView2.Navigate(
                "https://www.ah.be/zoeken?query=" + Uri.EscapeDataString(_zoekterm));
        }
        catch (Exception ex)
        {
            _status.Text = $"Browser kon niet starten: {ex.Message}";
        }
    }

    private void BronGewijzigd()
    {
        if (IsDisposed)
        {
            return;
        }
        var bron = _web.CoreWebView2?.Source ?? "";
        var isProduct = bron.Contains("/producten/product/", StringComparison.OrdinalIgnoreCase);
        _kies.Enabled = isProduct;
        _status.Text = isProduct
            ? "Product: " + Uri.UnescapeDataString(bron[(bron.LastIndexOf('/') + 1)..]).Replace('-', ' ')
            : "Klik een product open; dan kun je hem hieronder kiezen.";
    }

    private async Task AccepteerCookiesAsync()
    {
        for (var poging = 0; poging < 6 && !IsDisposed && _web.CoreWebView2 is not null; poging++)
        {
            try
            {
                var r = await _web.CoreWebView2.ExecuteScriptAsync($$"""
                    (function () {
                        {{AhWinkelForm.CookieJs}}
                        return 'geen';
                    })()
                    """);
                if (r == "\"cookies\"")
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            await Task.Delay(500);
        }
    }
}
