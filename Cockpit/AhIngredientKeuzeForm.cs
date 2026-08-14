namespace WorkManager;

/// <summary>
/// Tussenstap tussen "gerechten aanvinken" en "mandje vullen": toont alle ingrediënten van
/// de gekozen gerechten, elk met het AH-product dat er in de lokale producttabel
/// (<see cref="AhProducten"/>) bij gevonden is. Vink af wat je écht nodig hebt — alleen de
/// aangevinkte regels gaan naar het winkelmandje. Per regel kun je het aantal bijstellen of
/// een ander product kiezen; die keuze wordt onthouden in ah-gerechten.json.
/// </summary>
public class AhIngredientKeuzeForm : Form
{
    /// <summary>Eén regel in de lijst: ingrediënt + waar het vandaan komt + het gevonden product.</summary>
    private sealed class Regel
    {
        public required AhIngredient Ingredient { get; init; }
        public required string Gerechten { get; init; }
        public AhMatch Zekerheid { get; set; }

        /// <summary>Handmatig gekozen link (of catalogusmatch) om terug te schrijven.</summary>
        public bool LinkGewijzigd { get; set; }

        /// <summary>Titel van een automatisch gezocht product (niet in de producttabel), voor de weergave.</summary>
        public string? ZoekTitel { get; set; }
    }

    /// <summary>Kopregel boven een gerecht of rubriek: klikken vinkt de hele groep aan of uit.</summary>
    private sealed class Kop
    {
        public required string Titel { get; init; }
    }

    private readonly ModernListView _lijst;
    private readonly NumericUpDown _aantal;
    private readonly Label _status;
    private readonly TextBox _filter;

    /// <summary>
    /// Álle regels (kopjes + ingrediënten) in vaste volgorde. De zichtbare lijst is hier een
    /// (gefilterde) weergave van; status, groepsklikken en bevestigen rekenen altijd op deze
    /// volledige lijst, zodat een aangevinkte regel niet verdwijnt door het filter.
    /// </summary>
    private readonly List<ListViewItem> _alleItems = new();

    private bool _vullen;

    /// <summary>Aangevinkte ingrediënten mét productlink; alleen geldig na DialogResult.OK.</summary>
    public List<AhIngredient> Producten { get; private set; } = new();

    /// <summary>Aangevinkte ingrediënten zonder link — die moet je zelf zoeken.</summary>
    public List<string> Handmatig { get; private set; } = new();

    /// <summary>Ingrediëntnaam → nieuw gekozen productlink, om in ah-gerechten.json te bewaren.</summary>
    public Dictionary<string, string> NieuweLinks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Het afvinkgedrag van álle regels (ook de afgevinkte): naam, de Standaard-vlag waarmee
    /// de regel getoond werd en of hij aangevinkt bleef. Voer voor <see cref="AhKeuzeLeer"/>;
    /// alleen gevuld na DialogResult.OK.
    /// </summary>
    public List<(string Naam, bool Standaard, bool Aangevinkt)> KeuzeGedrag { get; private set; } = new();

    public AhIngredientKeuzeForm(
        List<AhIngredient> ingredienten,
        Dictionary<string, string> herkomst)
    {
        Text = "Albert Heijn – boodschappen kiezen";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(940, 660);
        MinimizeBox = false;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Je voorkeuren staan al aangevinkt — pas aan wat je deze week (anders) nodig hebt. " +
                   "≈ = gok, ⚠ = bevat gluten, oranje prijs = bonus. Klik een kopregel om de groep om te zetten.",
        };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46 };
        Theme.AsToolbar(toolbar);
        var allesButton = new ModernButton { Text = "Alles aan/uit", Width = 130, Glyph = Fluent.Checkbox };
        allesButton.Click += (_, _) => WisselAlles();
        // Filter voor lange boodschappenlijsten; vinkjes op verborgen regels blijven tellen.
        _filter = new TextBox { Width = 170, PlaceholderText = "Filter…", Margin = new Padding(8, 8, 0, 0) };
        _filter.TextChanged += (_, _) => ToonGefilterd();
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[] { allesButton, _filter, _status });

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            MultiSelect = false,
            HideSelection = false,
            LegeTekst = "Geen ingrediënten gevonden bij deze gerechten.",
            LeegGlyph = Fluent.Winkelwagen,
            HeeftCheckbox = rij => rij.Tag is Regel,
            RijHoogte = 54,
            IcoonGrootte = 44,
            RijIcoon = rij => rij.Tag is Regel r ? AhAfbeeldingen.Voor(r.Ingredient.Url) : null,
        };
        _lijst.Columns.Add("Ingrediënt", 300);
        _lijst.Columns.Add("Aantal", 86, HorizontalAlignment.Right);
        _lijst.Columns.Add("Prijs", 75, HorizontalAlignment.Right);
        _lijst.Columns.Add("AH-product", 454);
        // Aantal direct in de rij aanpassen met − en + (sneller dan selecteren + veld onderaan).
        _lijst.PlusMinKolom = 1;
        _lijst.PlusMinGeklikt += (rij, delta) =>
        {
            if (rij.Tag is not Regel geklikt)
            {
                return;
            }
            geklikt.Ingredient.Aantal = Math.Clamp(geklikt.Ingredient.Aantal + delta, 1, 99);
            WerkRijBij(rij);
            if (rij.Selected)
            {
                VulVelden(); // het veld onderaan meteen laten meelopen
            }
            WerkStatusBij();
        };
        _lijst.Resize += (_, _) => _lijst.Columns[3].Width = Math.Max(180,
            _lijst.ClientSize.Width - _lijst.Columns[0].Width - _lijst.Columns[1].Width
            - _lijst.Columns[2].Width - 4);

        // Per gerecht/rubriek een kopregel, zodat een lange rubriek als Boterhambeleg in één
        // klik aan of uit gaat. De volgorde volgt die van de aangevinkte gerechten.
        foreach (var groep in ingredienten.GroupBy(
            i => herkomst.TryGetValue(i.Naam, out var g) ? g : ""))
        {
            if (groep.Key.Length > 0)
            {
                _alleItems.Add(new ListViewItem(new[] { groep.Key.ToUpperInvariant(), "", "", "" })
                {
                    Tag = new Kop { Titel = groep.Key },
                });
            }
            foreach (var ing in groep)
            {
                var regel = new Regel { Ingredient = ing, Gerechten = groep.Key };
                // Staat er al een link in ah-gerechten.json, dan is dat de bewuste keuze van
                // Maarten; anders zoeken we er een in de producttabel.
                if (ing.Url is null && AhProducten.Zoek(ing.Naam) is { Product: { } gevonden } match)
                {
                    ing.Url = gevonden.Url;
                    regel.Zekerheid = match.Zekerheid;
                }
                else if (ing.Url is not null)
                {
                    regel.Zekerheid = AhMatch.Zeker;
                }
                var rij = new ListViewItem(new[] { ing.Naam, "", "", "" })
                {
                    Tag = regel,
                    // Per product ingesteld (bv. appel aan, rozijn uit); wat in de
                    // voorraadkast zit heb je al in huis en staat dus uit.
                    Checked = ing.Standaard && !AhVoorraadkast.Bevat(ing.Naam),
                    UseItemStyleForSubItems = false,
                };
                _alleItems.Add(rij);
                WerkRijBij(rij);
                // Geen link én niets in de producttabel: op de achtergrond een product zoeken.
                if (ing.Url is null)
                {
                    ProbeerAutoMatch(rij);
                }
            }
        }
        ToonGefilterd();
        _lijst.SelectedIndexChanged += (_, _) => VulVelden();
        _lijst.ItemChecked += (_, _) => WerkStatusBij();
        _lijst.ItemCheck += (_, e) =>
        {
            if (_lijst.Items[e.Index].Tag is Kop)
            {
                e.NewValue = CheckState.Unchecked;
            }
        };
        _lijst.MouseClick += (_, e) =>
        {
            if (_lijst.GetItemAt(e.X, e.Y) is { Tag: Kop kop })
            {
                WisselGroep(kop.Titel);
            }
        };
        // Dubbelklik op een gerecht(kop): die groep boodschappen naar boven, zodat je het
        // gerecht waar je mee bezig bent bovenaan hebt staan.
        _lijst.MouseDoubleClick += (_, e) =>
        {
            var titel = _lijst.GetItemAt(e.X, e.Y)?.Tag switch
            {
                Kop kop => kop.Titel,
                Regel regel => regel.Gerechten,
                _ => null,
            };
            if (string.IsNullOrEmpty(titel))
            {
                return;
            }
            var groep = _alleItems
                .Where(i => i.Tag is Kop k && k.Titel == titel ||
                            i.Tag is Regel r && r.Gerechten == titel)
                .ToList();
            foreach (var item in groep)
            {
                _alleItems.Remove(item);
            }
            _alleItems.InsertRange(0, groep);
            ToonGefilterd();
        };

        // Bewerkstrook voor de geselecteerde regel.
        var bewerk = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(10, 8, 10, 0),
            WrapContents = false,
        };
        var aantalLabel = new Label { Text = "Aantal:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        _aantal = new NumericUpDown { Width = 56, Minimum = 1, Maximum = 99, Enabled = false };
        _aantal.ValueChanged += (_, _) => SchrijfAantal();
        var kies = new ModernButton
        {
            Text = "Ander product…", Width = 155, Glyph = Fluent.Zoek, Margin = new Padding(12, 0, 0, 0),
        };
        kies.Click += (_, _) => KiesProduct();
        var wis = new ModernButton
        {
            Text = "Zonder product", Width = 145, Glyph = Fluent.Delete, Margin = new Padding(6, 0, 0, 0),
        };
        wis.Click += (_, _) => WisProduct();
        var voorraadkast = new ModernButton
        {
            Text = "Voorraadkast", Width = 140, Glyph = Fluent.Huis, Margin = new Padding(6, 0, 0, 0),
        };
        voorraadkast.Click += (_, _) => WisselVoorraadkast();
        bewerk.Controls.AddRange(new Control[] { aantalLabel, _aantal, kies, wis, voorraadkast });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 110 };
        var ok = new ModernButton
        {
            Text = "In winkelmandje leggen", Width = 200, Kind = ButtonKind.Accent,
            Glyph = Fluent.Winkelwagen,
        };
        ok.Click += (_, _) => Bevestig();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;

        Controls.Add(_lijst);
        Controls.Add(toolbar);
        Controls.Add(hint);
        Controls.Add(new AhStappenBalk(2));
        Controls.Add(bewerk);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
        aantalLabel.ForeColor = Theme.Muted;
        foreach (var rij in _alleItems.Where(r => r.Tag is Kop))
        {
            rij.ForeColor = Theme.Muted;
            rij.Font = new Font(_lijst.Font, FontStyle.Bold);
        }
        WerkStatusBij();

        // Productfoto's, prijzen en automatische matches komen op de achtergrond binnen; telkens
        // hertekenen zodra er iets klaar is.
        AhAfbeeldingen.BeeldKlaar += OpBeeldKlaar;
        AhDetails.Klaar += OpDetailsKlaar;
        AhZoeker.MatchKlaar += OpMatchKlaar;
        FormClosed += (_, _) =>
        {
            AhAfbeeldingen.BeeldKlaar -= OpBeeldKlaar;
            AhDetails.Klaar -= OpDetailsKlaar;
            AhZoeker.MatchKlaar -= OpMatchKlaar;
        };
        AhAfbeeldingen.Voorladen(Rijen.Select(r => ((Regel)r.Tag!).Ingredient.Url));
    }

    /// <summary>Voert een UI-actie veilig uit op de UI-thread (achtergrond-events).</summary>
    private void OpUiThread(Action actie)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(actie);
            }
        }
        catch (InvalidOperationException)
        {
            // Venster net gesloten: negeren.
        }
    }

    private void OpBeeldKlaar() => OpUiThread(() => _lijst.Invalidate());

    private void OpDetailsKlaar() => OpUiThread(() =>
    {
        foreach (var rij in Rijen)
        {
            WerkRijBij(rij);
        }
        WerkStatusBij();
    });

    private void OpMatchKlaar() => OpUiThread(() =>
    {
        foreach (var rij in Rijen)
        {
            if (rij.Tag is Regel { Ingredient.Url: null })
            {
                ProbeerAutoMatch(rij);
            }
        }
        WerkStatusBij();
    });

    private Regel? Geselecteerd =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as Regel : null;

    /// <summary>Alle aanvinkbare rijen (dus zonder de kopregels), óók de weggefilterde.</summary>
    private IEnumerable<ListViewItem> Rijen => _alleItems.Where(r => r.Tag is Regel);

    /// <summary>
    /// Vult de zichtbare lijst vanuit <see cref="_alleItems"/>, beperkt tot de regels die het
    /// filter halen (op ingrediëntnaam of AH-productnaam). Kopjes verschijnen alleen als er
    /// onder hen iets te zien valt.
    /// </summary>
    private void ToonGefilterd()
    {
        var f = _filter.Text.Trim();
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        ListViewItem? kop = null;
        foreach (var item in _alleItems)
        {
            if (item.Tag is Kop)
            {
                kop = item;
                continue;
            }
            if (f.Length > 0 && item.Tag is Regel regel &&
                !regel.Ingredient.Naam.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !item.SubItems[3].Text.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (kop is not null)
            {
                _lijst.Items.Add(kop);
                kop = null;
            }
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
    }

    private void WerkRijBij(ListViewItem rij)
    {
        var regel = (Regel)rij.Tag!;
        var voorraad = AhVoorraadkast.Bevat(regel.Ingredient.Naam);
        rij.SubItems[0].Text = regel.Ingredient.Naam + (voorraad ? "   🏠 voorraadkast" : "");
        rij.SubItems[0].ForeColor = voorraad ? Theme.Muted : Theme.Text;
        rij.SubItems[1].Text = regel.Ingredient.Aantal.ToString();

        var info = AhDetails.Voor(regel.Ingredient.Url);

        // Prijs (per stuk); bonusprijs in oranje. Leeg zolang hij nog opgehaald wordt.
        rij.SubItems[2].Text = info?.Prijs is { } p ? Euro(p) : "";
        rij.SubItems[2].ForeColor = info?.Bonus == true ? Theme.Warn : Theme.Muted;

        var product = regel.Ingredient.Url is null ? null : AhProducten.Alles
            .FirstOrDefault(pr => pr.Url.Equals(regel.Ingredient.Url, StringComparison.OrdinalIgnoreCase));
        var naam = product?.Naam ?? regel.ZoekTitel ?? info?.Titel ?? regel.Ingredient.Url;
        var bevatGluten = info?.Gluten == AhApi.GlutenStatus.Bevat;

        rij.SubItems[3].Text = regel.Ingredient.Url is null
            ? "— zelf zoeken in de winkel"
            : (bevatGluten ? "⚠ " : "") + (regel.Zekerheid == AhMatch.Gok ? "≈ " : "") + naam +
              Bonussuffix(info) +
              (info?.Nutri is { } nutri ? $"  · Nutri {nutri}" : "");
        rij.SubItems[3].ForeColor = bevatGluten
            ? Theme.Danger
            : regel.Ingredient.Url is null || regel.Zekerheid == AhMatch.Gok
                ? Theme.Muted
                : Theme.Text;
    }

    private static readonly System.Globalization.CultureInfo NlBe =
        System.Globalization.CultureInfo.GetCultureInfo("nl-BE");

    private static string Euro(decimal bedrag) => "€ " + bedrag.ToString("0.00", NlBe);

    /// <summary>Bonuslabel achter de productnaam: "(was €X)" alleen bij een echte prijsverlaging.</summary>
    private static string Bonussuffix(AhApi.ProductInfo? info)
    {
        if (info is not { Bonus: true })
        {
            return "";
        }
        return info is { PrijsVoorBonus: { } vb, Prijs: { } p } && p < vb
            ? $"  · bonus (was {Euro(vb)})"
            : "  · bonus";
    }

    /// <summary>Zoekt op de achtergrond een AH-product voor een ingrediënt zonder link/producttabelmatch.</summary>
    private void ProbeerAutoMatch(ListViewItem rij)
    {
        var regel = (Regel)rij.Tag!;
        if (regel.Ingredient.Url is not null)
        {
            return;
        }
        if (AhZoeker.Voor(regel.Ingredient.Naam) is { } info)
        {
            regel.Ingredient.Url = info.Url;
            regel.ZoekTitel = info.Titel;
            regel.Zekerheid = AhMatch.Gok; // automatische gok: laat staan of vervang met "Ander product…"
            WerkRijBij(rij);
        }
    }

    private void VulVelden()
    {
        _vullen = true;
        try
        {
            var regel = Geselecteerd;
            _aantal.Enabled = regel is not null;
            _aantal.Value = Math.Clamp(regel?.Ingredient.Aantal ?? 1, 1, 99);
        }
        finally
        {
            _vullen = false;
        }
    }

    private void SchrijfAantal()
    {
        if (_vullen || Geselecteerd is not { } regel)
        {
            return;
        }
        regel.Ingredient.Aantal = (int)_aantal.Value;
        WerkRijBij(_lijst.SelectedItems[0]);
        WerkStatusBij();
    }

    private void KiesProduct()
    {
        if (Geselecteerd is not { } regel)
        {
            Toast.Toon(this, "Selecteer eerst een ingrediënt in de lijst", Fluent.Lijst);
            return;
        }
        using var kiezer = new AhProductKiesForm(regel.Ingredient.Naam);
        if (kiezer.ShowDialog(this) == DialogResult.OK && kiezer.GekozenUrl is { } url)
        {
            regel.Ingredient.Url = url;
            regel.Zekerheid = AhMatch.Zeker;
            regel.LinkGewijzigd = true;
            _lijst.SelectedItems[0].Checked = true;
            WerkRijBij(_lijst.SelectedItems[0]);
            WerkStatusBij();
        }
    }

    /// <summary>
    /// Zet het geselecteerde ingrediënt in of uit de voorraadkast. In de kast = je hebt het
    /// al in huis: de regel gaat uit en toont het 🏠-label; eruit halen zet hem terug op
    /// zijn Standaard-vlag.
    /// </summary>
    private void WisselVoorraadkast()
    {
        if (Geselecteerd is not { } regel)
        {
            Toast.Toon(this, "Selecteer eerst een ingrediënt in de lijst", Fluent.Lijst);
            return;
        }
        var erin = AhVoorraadkast.Wissel(regel.Ingredient.Naam);
        var rij = _lijst.SelectedItems[0];
        rij.Checked = !erin && regel.Ingredient.Standaard;
        WerkRijBij(rij);
        WerkStatusBij();
        Toast.Toon(this,
            erin
                ? $"{regel.Ingredient.Naam} zit nu in de voorraadkast (standaard niet bestellen)"
                : $"{regel.Ingredient.Naam} weer uit de voorraadkast gehaald",
            Fluent.Huis);
    }

    private void WisProduct()
    {
        if (Geselecteerd is not { } regel)
        {
            return;
        }
        regel.Ingredient.Url = null;
        regel.Zekerheid = AhMatch.Geen;
        regel.LinkGewijzigd = false;
        WerkRijBij(_lijst.SelectedItems[0]);
        WerkStatusBij();
    }

    private void WisselAlles()
    {
        var rijen = Rijen.ToList();
        var aan = rijen.Count(r => r.Checked) < rijen.Count;
        foreach (var rij in rijen)
        {
            rij.Checked = aan;
        }
    }

    /// <summary>Zet alle regels van één gerecht/rubriek in één keer aan of uit.</summary>
    private void WisselGroep(string titel)
    {
        var rijen = Rijen.Where(r => ((Regel)r.Tag!).Gerechten == titel).ToList();
        var aan = rijen.Count(r => r.Checked) < rijen.Count;
        foreach (var rij in rijen)
        {
            rij.Checked = aan;
        }
    }

    private void WerkStatusBij()
    {
        var aangevinkt = Rijen.Where(r => r.Checked).Select(r => (Regel)r.Tag!).ToList();
        var metLink = aangevinkt.Count(r => r.Ingredient.Url is not null);

        // Geschat totaal, bonusbesparing en het aantal aangevinkte producten met gluten.
        decimal totaal = 0, besparing = 0;
        var metPrijs = 0;
        var glutenAantal = 0;
        foreach (var r in aangevinkt)
        {
            if (AhDetails.Voor(r.Ingredient.Url) is not { } info)
            {
                continue;
            }
            var n = Math.Max(1, r.Ingredient.Aantal);
            if (info.Prijs is { } p)
            {
                totaal += p * n;
                metPrijs++;
            }
            if (info is { Bonus: true, PrijsVoorBonus: { } vb, Prijs: { } bp })
            {
                besparing += (vb - bp) * n;
            }
            if (info.Gluten == AhApi.GlutenStatus.Bevat)
            {
                glutenAantal++;
            }
        }

        _status.Text = $"{aangevinkt.Count} van {Rijen.Count()} aangevinkt — " +
            $"{metLink} automatisch, {aangevinkt.Count - metLink} zelf zoeken" +
            (metPrijs > 0 ? $" — ≈ {Euro(totaal)} geschat" : "") +
            (besparing > 0 ? $" ({Euro(besparing)} bonusvoordeel)" : "") +
            (glutenAantal > 0 ? $" · ⚠ {glutenAantal} met gluten" : "") + ".";
    }

    private void Bevestig()
    {
        var aangevinkt = Rijen.Where(r => r.Checked)
            .Select(r => (Regel)r.Tag!)
            .ToList();
        if (aangevinkt.Count == 0)
        {
            Toast.Toon(this, "Vink eerst minstens één ingrediënt aan", Fluent.Checkbox);
            return;
        }
        // Twee ingrediënten kunnen naar hetzelfde product wijzen ("wortel" bij het ene
        // gerecht, "wortelen" bij het andere). Die aantallen optellen, anders legt het
        // mandje-script er maar één in: bij de tweede staat het gevraagde aantal er al.
        Producten = aangevinkt
            .Where(r => r.Ingredient.Url is not null)
            .GroupBy(r => r.Ingredient.Url!, StringComparer.OrdinalIgnoreCase)
            .Select(groep => new AhIngredient
            {
                Naam = string.Join(" / ", groep.Select(r => r.Ingredient.Naam).Distinct()),
                Url = groep.Key,
                Aantal = groep.Sum(r => Math.Max(1, r.Ingredient.Aantal)),
            })
            .ToList();
        Handmatig = aangevinkt.Where(r => r.Ingredient.Url is null).Select(r => r.Ingredient.Naam).ToList();
        KeuzeGedrag = Rijen
            .Select(r => (((Regel)r.Tag!).Ingredient.Naam, ((Regel)r.Tag!).Ingredient.Standaard, r.Checked))
            .ToList();
        foreach (var regel in aangevinkt.Where(r => r.LinkGewijzigd && r.Ingredient.Url is not null))
        {
            NieuweLinks[regel.Ingredient.Naam] = regel.Ingredient.Url!;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
