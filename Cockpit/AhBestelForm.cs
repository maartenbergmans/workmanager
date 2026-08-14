using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Eén ingrediënt van een AH-gerecht. In ah-gerechten.json mag een ingrediënt een kale
/// string zijn ("wortelen") of een object met een ah.be-productlink en optioneel aantal
/// ({ "naam": "wortelen", "url": "https://www.ah.be/producten/product/wi4076/...", "aantal": 2 }).
/// Ingrediënten mét link worden automatisch in het winkelmandje gelegd.
/// </summary>
[JsonConverter(typeof(AhIngredientConverter))]
public sealed class AhIngredient
{
    public string Naam { get; set; } = "";
    public string? Url { get; set; }
    public int Aantal { get; set; } = 1;

    /// <summary>
    /// Of dit ingrediënt in de keuzestap standaard aangevinkt staat. Zo kun je per product
    /// aangeven of het normaal mee moet (appel: ja) of niet (rozijn: nee). Standaard aan.
    /// </summary>
    public bool Standaard { get; set; } = true;
}

/// <summary>Recept bij een gerecht: vrije tekst of link, met bereidingstijd en basis-porties.</summary>
public sealed class Recept
{
    public string Tekst { get; set; } = "";

    /// <summary>Bereidingstijd in minuten (0 = onbekend); bepaalt ook de lengte van de agenda-afspraak.</summary>
    public int Minuten { get; set; }

    /// <summary>Voor hoeveel personen het recept bedoeld is; basis om de hoeveelheden op te schalen.</summary>
    public int Personen { get; set; } = 4;
}

/// <summary>Leest een ingrediënt als string óf object, zodat bestaande bestanden blijven werken.</summary>
public sealed class AhIngredientConverter : JsonConverter<AhIngredient>
{
    public override AhIngredient Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new AhIngredient { Naam = reader.GetString() ?? "" };
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var ing = new AhIngredient();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Equals("naam", StringComparison.OrdinalIgnoreCase))
            {
                ing.Naam = prop.Value.GetString() ?? "";
            }
            else if (prop.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                ing.Url = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
            }
            else if (prop.Name.Equals("aantal", StringComparison.OrdinalIgnoreCase) &&
                prop.Value.TryGetInt32(out var aantal) && aantal > 0)
            {
                ing.Aantal = aantal;
            }
            else if (prop.Name.Equals("standaard", StringComparison.OrdinalIgnoreCase) &&
                (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False))
            {
                ing.Standaard = prop.Value.GetBoolean();
            }
        }
        return ing;
    }

    public override void Write(Utf8JsonWriter writer, AhIngredient value, JsonSerializerOptions options)
    {
        // Een kale string volstaat alleen als er niets bijzonders te bewaren valt.
        if (value.Url is null && value.Aantal <= 1 && value.Standaard)
        {
            writer.WriteStringValue(value.Naam);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("naam", value.Naam);
        if (value.Url is not null)
        {
            writer.WriteString("url", value.Url);
        }
        if (value.Aantal > 1)
        {
            writer.WriteNumber("aantal", value.Aantal);
        }
        if (!value.Standaard)
        {
            writer.WriteBoolean("standaard", false);
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Albert Heijn-bestelling: vink de gerechten voor deze week aan (uit
/// %APPDATA%\WorkManager\ah-gerechten.json — daar kun je gerechten bijzetten of aanpassen).
/// Bij "In winkelmandje leggen" worden alle ingrediënten met een productlink automatisch in
/// het mandje op ah.be gelegd (ingebed browservenster, login blijft bewaard); de rest komt
/// op het klembord om handmatig te zoeken. De keuze wordt bewaard in ah-bestelling.json.
/// </summary>
public class AhBestelForm : Form
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string GerechtenFile = Path.Combine(DataDir, "ah-gerechten.json");
    private static readonly string BestellingFile = Path.Combine(DataDir, "ah-bestelling.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class GerechtenData
    {
        public Dictionary<string, List<AhIngredient>> Gerechten { get; set; } = new();

        /// <summary>
        /// Voorraad receptsuggesties: hieruit toont het venster er elke week vijf andere
        /// (zie <see cref="SuggestiesVanDeWeek"/>). Allemaal met producten die al eens
        /// besteld zijn.
        /// </summary>
        public Dictionary<string, List<AhIngredient>> Suggesties { get; set; } = new();

        /// <summary>
        /// Vaste boodschappenrubrieken (Boterhambeleg, Groenten, Fruit, Snacks, Koekjes,
        /// Non-food). Per product bepaalt <see cref="AhIngredient.Standaard"/> of het in de
        /// keuzestap al aangevinkt staat.
        /// </summary>
        public Dictionary<string, List<AhIngredient>> Rubrieken { get; set; } = new();

        /// <summary>
        /// Optioneel recept per gerecht/suggestie (gerecht­naam → tekst/link + bereidingstijd).
        /// Wordt mee in de agenda-afspraak gezet als je een gerecht op een avond plant; de
        /// bereidingstijd bepaalt ook de lengte van die afspraak.
        /// </summary>
        public Dictionary<string, Recept> Recepten { get; set; } = new();

        /// <summary>
        /// Sterrenwaardering per gerecht/suggestie (1–3; afwezig = 2, neutraal). Favorieten
        /// (★★★) krijgen voorrang in het weekmenu-voorstel, afvallers (★) juist niet.
        /// </summary>
        public Dictionary<string, int> Sterren { get; set; } = new();
    }

    private readonly GerechtenData _data;

    /// <summary>De suggesties die nu getoond worden (standaard vijf; "meer tonen" telt op).</summary>
    private Dictionary<string, List<AhIngredient>> _weekSuggesties;

    /// <summary>Hoeveel suggesties er getoond worden; "Meer suggesties tonen" telt er 5 bij.</summary>
    private int _suggestieAantal = 5;

    /// <summary>
    /// Producten die volgens de bestelgeschiedenis aan een nabestelling toe zijn. De sleutel is
    /// de regel zoals ze in de lijst staat ("melk — om de 7 d, 9 d geleden"), de waarde is het
    /// product zelf. Zo passen ze in dezelfde machinerie als gerechten en rubrieken.
    /// </summary>
    private Dictionary<string, List<AhIngredient>> _voorraad;

    private readonly Panel _scroller;
    private readonly FlowLayoutPanel _grid;
    private readonly List<AhGerechtKaart> _kaarten = new();
    private readonly List<Label> _sectieLabels = new();

    /// <summary>De laatst aangeklikte kaart — daarop werkt de knop "Recept bekijken".</summary>
    private string? _laatsteKaart;

    private readonly NumericUpDown _personen;
    private readonly ComboBox _filterKeuze;

    /// <summary>
    /// De aangevinkte namen, los van wat er nu zichtbaar is: het tagfilter herbouwt de lijst,
    /// en een vinkje op een weggefilterd gerecht mag daarbij niet verloren gaan.
    /// </summary>
    private readonly HashSet<string> _vinkjes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _totaalLabel = new()
    {
        AutoSize = true, Margin = new Padding(24, 6, 0, 0),
    };

    public AhBestelForm()
    {
        Text = "Albert Heijn – bestelling voorbereiden";
        StartPosition = FormStartPosition.CenterParent;
        // Breed genoeg voor drie fotokaarten naast elkaar (HelloFresh-gevoel).
        Size = new Size(860, 860);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(600, 480);
        MinimizeBox = false;
        MaximizeBox = false;

        _data = LaadGerechten();
        _weekSuggesties = SuggestiesVanDeWeek(_data.Suggesties, _suggestieAantal);
        _voorraad = VoorraadVanNu();
        // De voorraadsuggesties meteen aangevinkt: ze zijn afgeleid van wat je zelf al
        // besteld hebt, dus meestal wil je ze gewoon mee.
        foreach (var naam in _voorraad.Keys)
        {
            _vinkjes.Add(naam);
        }

        // FlowLayoutPanel met AutoScroll wil nog weleens op de virtuele breedte wrappen;
        // daarom zit hij in een scroller-paneel en dwingt Min/MaximumSize de wrap op de
        // zichtbare breedte af (het standaardrecept voor een verticaal scrollend grid).
        _scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _grid = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(8, 0, 8, 8),
            Location = new Point(0, 0),
        };
        _scroller.Controls.Add(_grid);
        _scroller.Resize += (_, _) => HerschikGrid();
        GerechtFoto.BeeldKlaar += OpGerechtFotoKlaar;
        FormClosed += (_, _) => GerechtFoto.BeeldKlaar -= OpGerechtFotoKlaar;

        // Weersvooruitzicht alvast ophalen: bij zomers weer krijgen BBQ- en koude gerechten
        // een streepje voor in het weekmenu-voorstel.
        _ = AhWeer.VerversAsync();

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Klik de gerechten aan die deze week mee moeten; dubbelklik toont het recept.\n" +
                   "De suggesties wisselen elke week.",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 110 };
        var bestel = new ModernButton
        {
            Text = "In winkelmandje leggen", Width = 200, Kind = ButtonKind.Accent, Glyph = Fluent.Winkelwagen,
        };
        bestel.Click += async (_, _) => await Bestel();
        var links = new ModernButton { Text = "Ingrediënten…", Width = 140, Glyph = Fluent.Edit };
        links.Click += (_, _) => BewerkIngredienten();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(bestel);
        buttons.Controls.Add(links);
        CancelButton = cancel;

        // Werkbalk bovenaan (HelloFresh-gebaar vooraan in beeld): weekmenu-voorstel en
        // receptkaart — onderaan pasten deze knoppen niet meer naast de bestelknoppen.
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46 };
        Theme.AsToolbar(toolbar);
        var weekmenu = new ModernButton { Text = "Stel weekmenu voor", Width = 195, Glyph = Fluent.Ster };
        weekmenu.Click += (_, _) => StelWeekmenuVoor();
        var recept = new ModernButton { Text = "Recept bekijken", Width = 155, Glyph = Fluent.EtenDrinken };
        recept.Click += (_, _) => ToonReceptKaart();
        var webversie = new ModernButton { Text = "Webversie", Width = 125, Glyph = Fluent.Globe };
        webversie.Click += (_, _) => OpenWebversie();
        // Tagfilter: toon alleen maaltijden met dit label (rubrieken verdwijnen dan even mee).
        var filterLabel = new Label { Text = "Toon:", AutoSize = true, Margin = new Padding(12, 12, 4, 0) };
        _filterKeuze = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Margin = new Padding(0, 8, 0, 0),
        };
        _filterKeuze.Items.AddRange(new object[] { "alles", "snel", "vega", "vis", "vlees", "nutri A-B" });
        _filterKeuze.SelectedIndex = 0;
        _filterKeuze.SelectedIndexChanged += (_, _) => HerbouwLijst();
        toolbar.Controls.AddRange(new Control[] { weekmenu, recept, webversie, filterLabel, _filterKeuze });
        filterLabel.ForeColor = Theme.Muted;

        // Aantal eters: schaalt de hoeveelheden van de gerechten (niet van de rubrieken).
        var porties = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 0, 0, 0), WrapContents = false,
        };
        var portiesLabel = new Label { Text = "Koken voor:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
        _personen = new NumericUpDown { Width = 52, Minimum = 1, Maximum = 20, Value = 4 };
        var portiesEenheid = new Label
        {
            Text = "personen — schaalt de hoeveelheden", AutoSize = true, Margin = new Padding(4, 6, 0, 0),
        };
        porties.Controls.AddRange(new Control[] { portiesLabel, _personen, portiesEenheid, _totaalLabel });

        // Prijsindicatie: productgegevens van alle ingrediënten alvast ophalen; zodra er
        // prijzen binnenkomen de rijen + het totaal bijwerken.
        AhDetails.Voor(null); // no-op, initialiseert de cache-klasse
        foreach (var url in _data.Gerechten.Values
                     .Concat(_data.Suggesties.Values).Concat(_data.Rubrieken.Values)
                     .Concat(_voorraad.Values).SelectMany(l => l).Select(i => i.Url))
        {
            AhDetails.Voor(url);
        }
        AhDetails.Klaar += OpPrijsKlaar;
        _personen.ValueChanged += (_, _) => WerkKaartenBij();
        FormClosed += (_, _) => AhDetails.Klaar -= OpPrijsKlaar;

        Controls.Add(_scroller);
        Controls.Add(toolbar);
        Controls.Add(porties);
        Controls.Add(hint);
        Controls.Add(new AhStappenBalk(1));
        Controls.Add(buttons);
        Theme.Apply(this);
        VensterGeheugen.Volg(this, "ah-bestellen");
        hint.ForeColor = Theme.Muted;
        portiesLabel.ForeColor = portiesEenheid.ForeColor = Theme.Muted;
        _totaalLabel.ForeColor = Theme.Accent;
        _grid.BackColor = _scroller.BackColor = Theme.Bg;
        HerschikGrid();
        HerbouwLijst(); // bouwt de kaarten en toont wat al in de prijscache zit
    }

    /// <summary>
    /// Opent de linkbewerker rechtstreeks met de opgeslagen gegevens — voor de visuele test
    /// (WorkManager.exe --venster ahlinks).
    /// </summary>
    public static AhIngredientenForm LinkEditor()
    {
        var data = LaadGerechten();
        return new AhIngredientenForm(data.Gerechten, data.Suggesties, data.Rubrieken, data.Recepten);
    }

    /// <summary>Receptkaart met echte data — voor de visuele test (--venster ahrecept).</summary>
    public static AhReceptKaartForm ReceptKaartTest()
    {
        var data = LaadGerechten();
        var naam = data.Recepten.Keys.FirstOrDefault(data.Gerechten.ContainsKey)
            ?? data.Gerechten.Keys.First();
        return new AhReceptKaartForm(
            naam, data.Gerechten[naam], data.Recepten.GetValueOrDefault(naam));
    }

    /// <summary>Breedte van een sectiekop: de volle rijbreedte, zodat de kaarten eronder beginnen.</summary>
    private int SectieBreedte() => Math.Max(300, _scroller.ClientSize.Width - 24);

    /// <summary>
    /// Sectiekop + kaarten. Maaltijden krijgen de grote fotokaart met subtitel en sterren;
    /// rubrieken en voorraadregels de compacte variant zonder foto.
    /// </summary>
    private void VulSectie(string kop, IEnumerable<string> namen, bool metFoto)
    {
        var items = namen.ToList();
        if (items.Count == 0)
        {
            return;
        }
        var label = new Label
        {
            Text = kop.ToUpperInvariant(),
            AutoSize = false,
            Height = 34,
            Width = SectieBreedte(),
            ForeColor = Theme.Muted,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(6, 8, 6, 2),
        };
        _grid.Controls.Add(label);
        _grid.SetFlowBreak(label, true);
        _sectieLabels.Add(label);
        foreach (var naam in items)
        {
            var kaart = new AhGerechtKaart(naam, metFoto)
            {
                Aangevinkt = _vinkjes.Contains(naam),
                Sterren = metFoto ? RatingVoor(naam) : 0,
                Subtitel = metFoto ? SubtitelVoor(naam) : "",
            };
            kaart.VinkGewisseld += k =>
            {
                _laatsteKaart = k.Naam;
                if (k.Aangevinkt)
                {
                    _vinkjes.Add(k.Naam);
                }
                else
                {
                    _vinkjes.Remove(k.Naam);
                }
                WerkTotaalBij();
            };
            kaart.SterrenGewijzigd += k =>
            {
                _data.Sterren[k.Naam] = k.Sterren;
                BewaarGerechten();
            };
            kaart.ReceptGevraagd += k => ToonReceptKaart(k.Naam);
            _grid.Controls.Add(kaart);
            _kaarten.Add(kaart);
        }
    }

    /// <summary>HelloFresh-achtige ondertitel: "met winterpeen, aardappelen en melk".</summary>
    private string SubtitelVoor(string naam)
    {
        var delen = IngredientenVoor(naam)
            .Skip(1) // het hoofdbestanddeel staat meestal al in de titel
            .Select(i => KorteNaam(i.Naam))
            .Where(n => n.Length > 0)
            .Take(3)
            .ToList();
        return delen.Count switch
        {
            0 => "",
            1 => "met " + delen[0],
            _ => "met " + string.Join(", ", delen[..^1]) + " en " + delen[^1],
        };
    }

    /// <summary>"AH Terra Gepelde edamame boontjes" → "gepelde edamame boontjes".</summary>
    private static string KorteNaam(string productNaam)
    {
        var naam = productNaam.Trim();
        foreach (var merk in new[] { "AH Terra ", "AH Biologisch ", "AH Excellent ", "AH " })
        {
            if (naam.StartsWith(merk, StringComparison.OrdinalIgnoreCase))
            {
                naam = naam[merk.Length..];
                break;
            }
        }
        return naam.ToLowerInvariant();
    }

    private void OpPrijsKlaar()
    {
        if (IsDisposed)
        {
            return;
        }
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(WerkKaartenBij);
            }
            else
            {
                WerkKaartenBij();
            }
        }
        catch
        {
            // Venster net gesloten.
        }
    }

    /// <summary>De ingrediënten die bij een gerecht/suggestie/rubriek/voorraadregel horen.</summary>
    private List<AhIngredient> IngredientenVoor(string naam) =>
        _data.Gerechten.TryGetValue(naam, out var g) ? g
        : _weekSuggesties.TryGetValue(naam, out var s) ? s
        : _data.Rubrieken.TryGetValue(naam, out var r) ? r
        : _voorraad.TryGetValue(naam, out var v) ? v
        : new List<AhIngredient>();

    /// <summary>Geschatte prijs van een gerecht (som van de bekende ingrediëntprijzen × porties),
    /// hoeveel producten in de Bonus staan en de gemiddelde Nutri-Score-letter.</summary>
    private (decimal Prijs, bool Volledig, int Bonus, string? Nutri) PrijsVanGerecht(string naam)
    {
        var ingredienten = IngredientenVoor(naam);
        if (ingredienten.Count == 0)
        {
            return (0m, false, 0, null);
        }
        decimal totaal = 0m;
        var volledig = true;
        var bonus = 0;
        var nutriSom = 0;
        var nutriAantal = 0;
        var factor = FactorVoor(naam);
        foreach (var ing in ingredienten)
        {
            var info = AhDetails.Voor(ing.Url);
            if (info?.Prijs is { } p)
            {
                totaal += p * factor * Math.Max(1, ing.Aantal);
            }
            else if (!string.IsNullOrWhiteSpace(ing.Url))
            {
                volledig = false; // wel een product, maar de prijs is nog niet binnen
            }
            if (info?.Bonus == true)
            {
                bonus++;
            }
            if (info?.Nutri is [>= 'A' and <= 'E' and var letter])
            {
                nutriSom += letter - 'A' + 1;
                nutriAantal++;
            }
        }
        // Gemiddelde letter (A=1 … E=5), afgerond — een grove indicatie zoals op de receptkaart.
        var nutri = nutriAantal > 0
            ? ((char)('A' + Math.Clamp((int)Math.Round((double)nutriSom / nutriAantal), 1, 5) - 1)).ToString()
            : null;
        return (totaal, volledig, bonus, nutri);
    }

    /// <summary>Sterrenwaardering van een gerecht (1–3; 2 = neutraal/standaard).</summary>
    private int RatingVoor(string naam) =>
        Math.Clamp(_data.Sterren.GetValueOrDefault(naam, 2), 1, 3);

    /// <summary>
    /// Grove HelloFresh-achtige labels bij een maaltijd, afgeleid uit de ingrediëntnamen en de
    /// bereidingstijd: "snel" (≤ 25 min) plus vis / vlees / vega.
    /// </summary>
    private string TagsVoor(string naam)
    {
        if (!_data.Gerechten.ContainsKey(naam) && !_weekSuggesties.ContainsKey(naam))
        {
            return ""; // rubrieken en voorraadregels zijn geen maaltijden
        }
        var tekst = string.Join(" ", IngredientenVoor(naam).Select(i => i.Naam.ToLowerInvariant()));
        var tags = new List<string>();
        if (_data.Recepten.GetValueOrDefault(naam)?.Minuten is > 0 and <= 25)
        {
            tags.Add("snel");
        }
        var vis = System.Text.RegularExpressions.Regex.IsMatch(
            tekst, @"zalm|tonijn|vis|garna|mossel|surimi|fish");
        var vlees = !tekst.Contains("plantaardig") && System.Text.RegularExpressions.Regex.IsMatch(
            tekst, @"gehakt|kip|spek|worst|ham |salami|chipolata|bbq");
        tags.Add(vis ? "vis" : vlees ? "vlees" : "vega");
        return string.Join(" · ", tags);
    }

    /// <summary>
    /// Dwingt de wrap-breedte van het grid op de zichtbare breedte af. Wordt ook bij elke
    /// verversing aangeroepen: de DPI-migratie van WinForms schaalt een gezette
    /// Min/MaximumSize anders nog eens op, waardoor de kaarten buiten beeld wrappen.
    /// </summary>
    private void HerschikGrid()
    {
        var breedte = Math.Max(320, _scroller.ClientSize.Width);
        if (_grid.MaximumSize.Width != breedte)
        {
            _grid.MinimumSize = new Size(breedte, 0);
            _grid.MaximumSize = new Size(breedte, 0);
        }
        foreach (var label in _sectieLabels)
        {
            label.Width = SectieBreedte();
        }
    }

    /// <summary>Werkt de inforegel en bonusbadge van elke kaart bij, plus het totaal.</summary>
    private void WerkKaartenBij()
    {
        if (IsDisposed)
        {
            return;
        }
        HerschikGrid();
        foreach (var kaart in _kaarten.Where(k => k.MetFoto))
        {
            var (prijs, volledig, bonusVanGerecht, nutri) = PrijsVanGerecht(kaart.Naam);
            var minuten = _data.Recepten.GetValueOrDefault(kaart.Naam)?.Minuten ?? 0;
            // Compact op de kaart ("snel"-tag bewust niet — de tijd staat er al); de
            // volledige info staat op de receptkaart.
            kaart.Info = string.Join(" · ", new[]
            {
                minuten > 0 ? $"⏱ {minuten} min" : null,
                nutri is not null ? $"Nutri {nutri}" : null,
                prijs > 0 ? $"≈ {Euro(prijs)}{(volledig ? "" : "…")}" : null,
            }.OfType<string>());
            kaart.Bonus = bonusVanGerecht;
            kaart.Invalidate();
        }
        WerkTotaalBij();
    }

    /// <summary>Geschat totaal van alles wat aangevinkt is (ook wat nu weggefilterd is).</summary>
    private void WerkTotaalBij()
    {
        decimal totaal = 0m;
        var alleVolledig = true;
        foreach (var naam in _vinkjes)
        {
            var (prijs, volledig, _, _) = PrijsVanGerecht(naam);
            totaal += prijs;
            alleVolledig &= volledig || prijs == 0;
        }
        // Bonusradar: hoeveel gelinkte producten (over alle gerechten) staan nu in de Bonus?
        var bonus = _data.Gerechten.Values
            .Concat(_data.Suggesties.Values).Concat(_data.Rubrieken.Values)
            .Concat(_voorraad.Values).SelectMany(l => l)
            .Select(i => i.Url).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct()
            .Count(u => AhDetails.Voor(u)?.Bonus == true);
        _totaalLabel.Text =
            (totaal > 0 ? $"Geschat totaal: {Euro(totaal)}{(alleVolledig ? "" : " (deels)")}" : "") +
            (bonus > 0 ? $"   ·   🏷 {bonus} in bonus" : "");
    }

    private static string Euro(decimal bedrag) =>
        "€ " + bedrag.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-BE"));

    /// <summary>
    /// Zet de nabestelsuggesties uit het voorraadgeheugen om in dezelfde vorm als een gerecht:
    /// één regel met één ingrediënt. De productlink komt uit de gerechten en rubrieken, zodat
    /// de keuzestap meteen het juiste product voorstelt.
    /// </summary>
    private Dictionary<string, List<AhIngredient>> VoorraadVanNu()
    {
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ingredient in _data.Gerechten.Values
                     .Concat(_data.Suggesties.Values).Concat(_data.Rubrieken.Values)
                     .SelectMany(l => l))
        {
            if (!string.IsNullOrWhiteSpace(ingredient.Url))
            {
                links[ingredient.Naam] = ingredient.Url;
            }
        }

        var map = new Dictionary<string, List<AhIngredient>>();
        foreach (var ritme in AhHistoriek.Nabestellen())
        {
            map[ritme.Regel] = new List<AhIngredient>
            {
                new() { Naam = ritme.Naam, Url = links.GetValueOrDefault(ritme.Naam), Standaard = true },
            };
        }
        return map;
    }

    private static AhIngredient I(string naam, string? url = null) => new() { Naam = naam, Url = url };

    internal static GerechtenData LaadGerechten()
    {
        try
        {
            if (File.Exists(GerechtenFile) &&
                JsonSerializer.Deserialize<GerechtenData>(File.ReadAllText(GerechtenFile), JsonOpts)
                    is { } data && data.Gerechten.Count > 0)
            {
                if (data.Rubrieken.Count == 0)
                {
                    data.Rubrieken = StandaardRubrieken();
                }
                if (data.Suggesties.Count == 0)
                {
                    data.Suggesties = StandaardSuggesties();
                }
                return data;
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op de standaardgerechten hieronder.
        }
        return new GerechtenData
        {
            Gerechten = new Dictionary<string, List<AhIngredient>>
            {
                ["Fishsticks met wortelpuree"] = new()
                {
                    I("fishsticks"),
                    I("wortelen", "https://www.ah.be/producten/product/wi4076/ah-winterpeen"),
                    I("aardappelen"), I("melk"), I("boter"),
                },
                ["Kip met appelmoes en frietjes"] = new()
                {
                    I("kipfilet"), I("appelmoes"), I("frieten"), I("mayonaise"),
                },
                ["Pasta pesto"] = new()
                {
                    I("pasta"), I("groene pesto"), I("parmezaan"), I("pijnboompitten"),
                },
                ["Pasta bolognese"] = new()
                {
                    I("spaghetti"), I("gehakt"), I("passata"), I("ui"), I("knoflook"),
                    I("wortel", "https://www.ah.be/producten/product/wi4076/ah-winterpeen"),
                },
                ["Pasta tonijn"] = new()
                {
                    I("pasta"), I("tonijn in blik"), I("tomatenblokjes"), I("ui"), I("kappertjes"),
                },
            },
            Suggesties = StandaardSuggesties(),
            Rubrieken = StandaardRubrieken(),
        };
    }

    /// <summary>
    /// De suggesties van deze week. De volgorde van de voorraad ligt per jaar vast en
    /// schuift elke week vijf plaatsen op: zo zie je pas terug hetzelfde gerecht als de hele
    /// voorraad geweest is, en blijft de lijst de hele week hetzelfde. Met
    /// <paramref name="aantal"/> haalt "Meer suggesties tonen" er extra uit de voorraad.
    /// </summary>
    internal static Dictionary<string, List<AhIngredient>> SuggestiesVanDeWeek(
        Dictionary<string, List<AhIngredient>> voorraad, int aantal = 5)
    {
        if (voorraad.Count == 0)
        {
            return new Dictionary<string, List<AhIngredient>>();
        }
        var vandaag = DateTime.Today;
        var volgorde = voorraad.Keys.OrderBy(naam => Hash(naam + ISOWeek.GetYear(vandaag))).ToList();
        var start = ISOWeek.GetWeekOfYear(vandaag) * 5 % volgorde.Count;
        var suggesties = Enumerable.Range(0, Math.Min(aantal, volgorde.Count))
            .Select(i => volgorde[(start + i) % volgorde.Count])
            .ToDictionary(naam => naam, naam => voorraad[naam]);

        // Verjaardagstraditie: rond de verjaardagen van Emilia (13/9) en Lisa (23/5) staan de
        // pannenkoeken gegarandeerd tussen de suggesties, wat de weekrotatie ook zegt.
        if (RondEenVerjaardag(DateOnly.FromDateTime(vandaag)) &&
            voorraad.Keys.FirstOrDefault(
                n => n.Contains("pannenkoek", StringComparison.OrdinalIgnoreCase)) is { } pannenkoeken &&
            !suggesties.ContainsKey(pannenkoeken))
        {
            suggesties[pannenkoeken] = voorraad[pannenkoeken];
        }
        return suggesties;
    }

    /// <summary>Vanaf een week vóór tot en met de dag na de verjaardag van Emilia (13/9) of Lisa (23/5).</summary>
    private static bool RondEenVerjaardag(DateOnly vandaag)
    {
        foreach (var (maand, dag) in new[] { (9, 13), (5, 23) })
        {
            var verjaardag = new DateOnly(vandaag.Year, maand, dag);
            if (vandaag >= verjaardag.AddDays(-7) && vandaag <= verjaardag.AddDays(1))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Stabiele hash (FNV-1a). Bewust niet string.GetHashCode(): die verschilt per run,
    /// waardoor de suggesties bij elke start van de app zouden veranderen.
    /// </summary>
    private static uint Hash(string tekst)
    {
        uint h = 2166136261;
        foreach (var teken in tekst)
        {
            h = (h ^ teken) * 16777619;
        }
        return h;
    }

    /// <summary>
    /// Twintig recepten, opgebouwd uit producten die al eens besteld zijn (Assets/ah-producten.json),
    /// met telkens een glutenvrij te maken basis. Vier weken lang vijf andere.
    /// </summary>
    private static Dictionary<string, List<AhIngredient>> StandaardSuggesties() => new()
    {
        ["Taco's met kipgehakt"] = Rij(
            "AH Kipgehakt gekruid", "Santa Maria Kruidenmix taco",
            "Santa Maria Tortilla wraps corn & wheat 8x medium",
            "AH Maiskorrels fris en knapperig 3-pack", "AH Terra Kidneybonen", "AH Cherrytomaten",
            "AH Komkommer", "AH Geraspte Franse emmentaler"),
        ["Tortelloni met romige champignonsaus"] = Rij(
            "AH Tortelloni verdi ricotta spinaci", "AH Witte champignons", "AH Kookroom",
            "AH Gele uien", "AH Knoflook", "AH Italiaanse grana padano 32+ flakes"),
        ["Kipschnitzel met frietjes en salade"] = Rij(
            "AH Terra Plantaardige krokante kipschnitzel", "Belviva Klassieke oven airfryer frieten M",
            "AH Belgische mayonaise", "AH Biologisch Veldsla", "AH Tasty Tom trostomaten",
            "AH Komkommer"),
        ["Zalm uit de oven met aardappel en spinazie"] = Rij(
            "AH Zalmfilet", "AH Kruimige aardappelen", "AH Spinazie kleinverpakking", "AH Kookroom",
            "AH Knoflook", "AH Roomboter ongezouten"),
        ["Pizza met ham, mozzarella en ananas"] = Rij(
            "AH Pizzadeeg tomatensaus", "AH Mozzarella mini", "AH Gerookte slagersachterham",
            "AH Ananasschijven op sap", "AH Rucola"),
        ["Wraps met kip en avocado"] = Rij(
            "AH Tortilla naturel wraps large 6 stuks", "AH Scharrel kipfilet 2 stuks", "AH Avocado",
            "AH Komkommer", "AH Cherrytomaten", "De Zaanse Hoeve Halfvolle yoghurt",
            "AH Biologisch Veldsla"),
        ["Ravioli met tomaat-roomsaus"] = Rij(
            "AH Verse ravioli formaggio e pomodoro", "AH Tomaten passata gezeefd", "AH Kookroom",
            "AH Gele uien", "AH Knoflook", "AH Italiaanse grana padano 32+ flakes"),
        ["Kipworstjes met puree en appelmoes"] = Rij(
            "AH Scharrel kipbraadworst 4 stuks", "AH Kruimige aardappelen",
            "AH Houdbare halfvolle melk", "AH Roomboter ongezouten",
            "AH Appelmoes 0% suiker toegevoegd"),
        ["Roerei met spek op tijgerbrood"] = Rij(
            "AH Verse scharreleieren M L", "AH Spekreepjes gerookt",
            "AH Extra lang lekker tijger bruin half", "AH Roomboter ongezouten", "AH Cherrytomaten"),
        ["Pasta carbonara"] = Rij(
            "AH Farfalle", "AH Spekreepjes gerookt", "AH Verse scharreleieren M L",
            "AH Parmigiano reggiano 32+ flakes", "AH Knoflook", "AH Kookroom"),
        ["Ovenschotel met kipgehakt en courgette"] = Rij(
            "AH Kipgehakt naturel", "AH Courgette", "AH Tomatenblokjes", "AH Gele uien",
            "AH Knoflook", "AH Geraspte Franse emmentaler", "AH Kruimige aardappelen"),
        ["Mosselen met frietjes"] = Rij(
            "AH Verse mosselen", "Belviva Belgische frieten", "AH Bleekselderij", "AH Gele uien",
            "AH Belgische mayonaise"),
        ["Garnalen in knoflookolie met stokbrood"] = Rij(
            "AH Grote roerbak garnalen knoflook", "Schär Mini baguette glutenvrij", "AH Knoflook",
            "AH Rucola", "AH Cherrytomaten"),
        ["Gehaktballen in tomatensaus met pasta"] = Rij(
            "AH Gehakt varken gekruid", "AH Tomaten passata gezeefd", "Barilla Penne Rigate glutenvrij",
            "AH Gele uien", "AH Knoflook", "AH Parmigiano reggiano 32+ flakes"),
        ["Vissticks met frietjes en appelmoes"] = Rij(
            "AH Vissticks", "Belviva Klassieke oven airfryer frieten M",
            "AH Appelmoes 0% suiker toegevoegd", "AH Belgische mayonaise"),
        ["Pasta met spinazie, feta en tomaat"] = Rij(
            "AH Farfalle", "AH Spinazie kleinverpakking", "Dodoni Feta",
            "AH Zongedroogde tomaten jullienne", "AH Pijnboompitten", "AH Knoflook"),
        ["BBQ-schotel met frietjes"] = Rij(
            "AH BBQ schotel", "AH Chipolata mix", "Belviva Belgische frieten", "AH Biologisch Veldsla",
            "AH Belgische mayonaise"),
        ["Groenteschnitzel met puree en wortel"] = Rij(
            "AH Terra Groenteschnitzel", "AH Kruimige aardappelen", "AH Winterpeen",
            "AH Houdbare halfvolle melk", "AH Roomboter ongezouten"),
        ["Pannenkoeken"] = Rij(
            "Freee Plain white flour", "AH Verse scharreleieren M L", "AH Houdbare halfvolle melk",
            "AH Roomboter ongezouten", "AH Speculoos crunchy pasta"),
    };

    /// <summary>
    /// Vaste rubrieken met producten die Maarten zelf al eens besteld heeft. De namen zijn
    /// exact die uit de producttabel (Assets/ah-producten.json), zodat de keuzestap er meteen
    /// de juiste ah.be-link bij vindt.
    /// </summary>
    private static Dictionary<string, List<AhIngredient>> StandaardRubrieken() => new()
    {
        ["Boterhambeleg"] = Rij(
            "AH Extra lang lekker tijger bruin heel", "AH Extra lang lekker tijger bruin half",
            "AH Tijger volkoren heel", "AH Liefde & Passie OerDesem zonne rogge gesneden",
            "Schär Meesterbakker vital glutenvrij", "AH Glutenvrij Madeleine brood half",
            "Schär Mini baguette glutenvrij", "Schär Crackers glutenvrij",
            "BFree Meergranen wraps glutenvrij", "AH Gerookte slagersachterham", "AH Salami",
            "AH Kipgrillworst", "AH Goudse belegen 48+ plakken",
            "AH Goudse extra belegen 48+ plakken", "Old Amsterdam Original 48+ plakken",
            "Beemster Belegen 48+ plakken", "AH Geraspte Franse emmentaler",
            "AH Franse roombrie 60+", "AH Camembert", "Petrus Abdijkaas 50+", "AH Tonijn salade",
            "AH Surimi-krab salade", "AH Excellent Forel salade",
            "AH Excellent Vitello tonnato salade", "Olav's Gerookte zalmfilet",
            "AH Verse scharreleieren S M", "AH Pindakaas naturel", "AH Speculoos crunchy pasta",
            "AH Margarine", "Meggle Kruidenboter original"),
        // Alle groenten die al eens besteld zijn; de vier zonder producttabelnaam hebben een
        // eigen link (die komen uit de gerechten, niet uit /producten/eerder-gekocht).
        ["Groenten"] = new()
        {
            I("AH Gele uien"), I("AH Knoflook"), I("AH Cherrytomaten"),
            I("AH Tasty Tom trostomaten"), I("AH Komkommer"), I("AH Avocado"),
            I("AH Witte champignons"), I("AH Courgette"), I("AH Spinazie kleinverpakking"),
            I("AH Biologisch Veldsla"), I("AH Rucola"), I("AH Bleekselderij"),
            I("AH Winterpeen"), I("AH Kruimige aardappelen"),
            I("AH Maiskorrels fris en knapperig 3-pack"),
            I("AH Sperziebonen", "https://www.ah.be/producten/product/wi4102/ah-sperziebonen"),
            I("AH Paprika rood", "https://www.ah.be/producten/product/wi4117/ah-paprika-rood"),
            I("AH Wokgroente Chinees", "https://www.ah.be/producten/product/wi41069/ah-wokgroente-chinees"),
            I("AH Terra Gepelde edamame boontjes",
                "https://www.ah.be/producten/product/wi514806/ah-terra-edamame"),
        },
        ["Fruit"] = new()
        {
            I("Pink Lady appels schaal"), I("AH Conference peren bak"), I("AH Mandarijnen"),
            I("AH Blauwe bessen"), I("AH Mango"), I("AH Mini watermeloen"),
            // Kiwi en appelsienen staan niet in de producttabel (niet via de app besteld),
            // dus met een eigen ah.be-link.
            I("Zespri Kiwi sungold", "https://www.ah.be/producten/product/wi523724/zespri-kiwi-sungold"),
            I("AH Handsinaasappelen", "https://www.ah.be/producten/product/wi67896/ah-handsinaasappelen"),
            I("AH Ananasschijven op sap"), I("AH Biologisch Mango gedroogd"),
            I("AH Rozijnen zongedroogd"),
        },
        ["Snacks"] = Rij(
            "AH Ribbelchips paprika", "AH Tortilla chips naturel", "AH Gezouten pinda's",
            "AH Terra Ongebrande notenmix", "AH Protein maiswafels", "Frisia Twistermallows",
            "Bear Fruit rolls aardbei", "AH Zaans huisje pure chocolade",
            "Tony's Chocolonely Reep puur amandel zeezout", "AH Reep witte chocolade",
            "AH Havercracker chocolade"),
        ["Koekjes"] = Rij(
            "Schär Butterkeks petit beurre glutenvrij", "Schär Madeleines glutenvrij",
            "AH Glutenvrij Koekjes citroen", "AH Eierwafels met poedersuiker", "AH Luikse wafels",
            "AH Koffiewafels", "Lotus Mini frangipane", "AH Muffin vanille", "AH Muffin choco",
            "AH Roomboter cupcake naturel"),
        // Non-food staat standaard uít (Standaard=false): dit heb je niet elke week nodig.
        // De links zijn echte AH-producten (pas ze gerust aan via de ingrediëntbewerker).
        ["Non-food"] = new()
        {
            NF("toiletpapier", "610441", "page-toiletpapier"),
            NF("keukenrol", "584125", "ah-keukenpapier"),
            NF("vuilniszakken", "618348", "ah-vuilnisemmerzakken"),
            NF("afwasmiddel", "570826", "dreft-afwasmiddel"),
            NF("vaatwastabletten", "607517", "sun-vaatwascapsules"),
            NF("aluminiumfolie", "213910", "ah-aluminiumfolie"),
            NF("vershoudfolie", "213912", "ah-vershoudfolie"),
            NF("handzeep", "177757", "unicura-handzeep"),
            NF("wasmiddel", "622939", "ariel-wasmiddel"),
        },
    };

    private static List<AhIngredient> Rij(params string[] namen) => namen.Select(n => I(n)).ToList();

    /// <summary>Non-foodproduct: eigen ah.be-link en standaard uít.</summary>
    private static AhIngredient NF(string naam, string id, string slug) => new()
    {
        Naam = naam,
        Url = $"https://www.ah.be/producten/product/wi{id}/{slug}",
        Standaard = false,
    };

    private void BewerkIngredienten()
    {
        using var editor = new AhIngredientenForm(
            _data.Gerechten, _data.Suggesties, _data.Rubrieken, _data.Recepten);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _data.Gerechten = editor.Gerechten;
        _data.Suggesties = editor.Suggesties;
        _data.Rubrieken = editor.Rubrieken;
        _data.Recepten = editor.Recepten;
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(GerechtenFile, JsonSerializer.Serialize(_data, JsonOpts));

        // De bewerkte lijsten zijn nieuwe objecten: de keuzelijst opnieuw opbouwen; de
        // vinkjes-administratie zet terug wat er aangevinkt stond.
        _weekSuggesties = SuggestiesVanDeWeek(_data.Suggesties);
        _voorraad = VoorraadVanNu();
        HerbouwLijst();
    }

    /// <summary>Hertekent de kaarten zodra er een nieuwe gerechtfoto binnen is (op de UI-thread).</summary>
    private void OpGerechtFotoKlaar()
    {
        if (IsDisposed)
        {
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                foreach (var kaart in _kaarten)
                {
                    kaart.Invalidate();
                }
            });
        }
        catch
        {
            // Venster net gesloten: niets aan de hand.
        }
    }

    /// <summary>
    /// Herbouwt het kaarten-grid volgens het tagfilter; de vinkjes komen uit
    /// <see cref="_vinkjes"/> zodat ook weggefilterde keuzes blijven staan. Bij een actief
    /// filter blijven de voorraad- en rubriekensecties weg — dat zijn geen maaltijden.
    /// </summary>
    private void HerbouwLijst()
    {
        var filter = _filterKeuze.SelectedItem as string ?? "alles";
        bool Mag(string naam) => filter switch
        {
            "alles" => true,
            "nutri A-B" => PrijsVanGerecht(naam).Nutri is "A" or "B",
            _ => TagsVoor(naam).Split(" · ", StringSplitOptions.RemoveEmptyEntries).Contains(filter),
        };
        _grid.SuspendLayout();
        _grid.Controls.Clear();
        _kaarten.Clear();
        _sectieLabels.Clear();
        if (filter == "alles")
        {
            VulSectie("Waarschijnlijk op", _voorraad.Keys, metFoto: false);
        }
        VulSectie("Gerechten", _data.Gerechten.Keys.Where(Mag), metFoto: true);
        VulSectie($"Suggesties – week {ISOWeek.GetWeekOfYear(DateTime.Today)}",
            _weekSuggesties.Keys.Where(Mag), metFoto: true);
        // "Meer suggesties tonen": haalt telkens vijf extra recepten uit de voorraad.
        if (_weekSuggesties.Count < _data.Suggesties.Count)
        {
            var meer = new ModernButton
            {
                Text = "Meer suggesties tonen", Width = AhGerechtKaart.KaartBreedte,
                Height = 40, Glyph = Fluent.Add, Margin = new Padding(6, 10, 6, 6),
            };
            meer.Click += (_, _) =>
            {
                _suggestieAantal += 5;
                _weekSuggesties = SuggestiesVanDeWeek(_data.Suggesties, _suggestieAantal);
                HerbouwLijst();
            };
            _grid.Controls.Add(meer);
        }
        if (filter == "alles")
        {
            VulSectie("Rubrieken", _data.Rubrieken.Keys, metFoto: false);
        }
        _grid.ResumeLayout();
        WerkKaartenBij();
    }

    /// <summary>Opent de receptkaart van een gerecht (HelloFresh-stijl), met de volledige
    /// info (tags, Nutri, prijs, bonus) die op de kaart in het grid is ingekort.</summary>
    private void ToonReceptKaart(string? naam = null)
    {
        naam ??= _laatsteKaart;
        if (naam is null ||
            (!_data.Gerechten.ContainsKey(naam) && !_weekSuggesties.ContainsKey(naam)))
        {
            Toast.Toon(this, "Klik eerst een gerecht of suggestie aan", Fluent.EtenDrinken);
            return;
        }
        var (prijs, _, bonus, nutri) = PrijsVanGerecht(naam);
        var extra = string.Join(" · ", new[]
        {
            TagsVoor(naam) is { Length: > 0 } tags ? tags : null,
            nutri is not null ? $"Nutri {nutri}" : null,
            prijs > 0 ? $"≈ {Euro(prijs)}" : null,
            bonus > 0 ? $"🏷 {bonus} in bonus" : null,
        }.OfType<string>());
        using var kaart = new AhReceptKaartForm(
            naam, IngredientenVoor(naam), _data.Recepten.GetValueOrDefault(naam), extra);
        kaart.ShowDialog(this);
    }

    /// <summary>Schrijft de gerechten (incl. sterren en standaard-vlaggen) naar ah-gerechten.json.</summary>
    private void BewaarGerechten()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(GerechtenFile, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch
        {
            // Niet kunnen bewaren mag de flow niet tegenhouden.
        }
    }

    /// <summary>
    /// Het HelloFresh-gebaar: één klik en er ligt een weekmenu van drie gerechten klaar.
    /// Favorieten (★★★) en gerechten met bonusproducten scoren hoger, wat je vorige
    /// bestelling al zat juist lager; een week-hash zorgt voor variatie bij gelijke stand.
    /// Alleen een voorstel — de vinkjes pas je gewoon aan.
    /// </summary>
    private void StelWeekmenuVoor()
    {
        // Eerst het tagfilter uitzetten: het voorstel kan gerechten kiezen die nu
        // weggefilterd zijn, en die moeten wel zichtbaar aangevinkt worden.
        if (_filterKeuze.SelectedIndex != 0)
        {
            _filterKeuze.SelectedIndex = 0; // triggert HerbouwLijst
        }
        var vorige = VorigeBestelling();
        var kandidaten = _data.Gerechten.Keys.Concat(_weekSuggesties.Keys).Distinct().ToList();
        if (kandidaten.Count == 0)
        {
            return;
        }
        // Bij zomerse vooruitzichten (Open-Meteo) krijgen BBQ- en koude gerechten zoals een
        // pokébowl een streepje voor — niet exclusief, gewoon een duwtje.
        var zomers = AhWeer.Zomers;
        var top = kandidaten
            .OrderByDescending(naam =>
                RatingVoor(naam) * 2.0 +
                Math.Min(3, PrijsVanGerecht(naam).Bonus) +
                (vorige.Contains(naam) ? -4 : 0) +
                (zomers && IsZomerGerecht(naam) ? 1.5 : 0) +
                Hash(naam + ISOWeek.GetWeekOfYear(DateTime.Today)) % 100 / 100.0)
            .Take(3)
            .ToHashSet();
        foreach (var kaart in _kaarten.Where(k => k.MetFoto))
        {
            var aan = top.Contains(kaart.Naam);
            kaart.Aangevinkt = aan;
            if (aan)
            {
                _vinkjes.Add(kaart.Naam);
            }
            else
            {
                _vinkjes.Remove(kaart.Naam);
            }
        }
        WerkTotaalBij();
        Toast.Toon(this, "Weekmenu: " + string.Join(", ", top) + " — pas gerust aan" +
            (zomers ? " (zomers weer meegewogen ☀)" : ""), Fluent.Ster);
    }

    /// <summary>BBQ of eerder koud eten (pokébowl, salade) — geschikt voor zomerse dagen.</summary>
    private bool IsZomerGerecht(string naam)
    {
        var tekst = naam + " " + string.Join(" ", IngredientenVoor(naam).Select(i => i.Naam));
        return System.Text.RegularExpressions.Regex.IsMatch(
            tekst, @"bbq|barbecue|pok[eé]|salade|bowl", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>De gerechten van de vorige bestelling (uit ah-bestelling.json), voor variatie.</summary>
    private static HashSet<string> VorigeBestelling()
    {
        try
        {
            if (File.Exists(BestellingFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(BestellingFile));
                if (doc.RootElement.TryGetProperty("gerechten", out var lijst) &&
                    lijst.ValueKind == JsonValueKind.Array)
                {
                    return lijst.EnumerateArray()
                        .Select(el => el.GetString())
                        .OfType<string>()
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Geen vorige bestelling bekend: geen strafpunten.
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Leert van het afvinkgedrag: wijkt een product twee bestellingen op rij van zijn
    /// Standaard-vlag af, dan wordt voorgesteld die vlag om te zetten. Akkoord = de vlag gaat
    /// om in alle gerechten, suggesties en rubrieken en ah-gerechten.json wordt bewaard.
    /// </summary>
    private void VerwerkKeuzeLeer(List<(string Naam, bool Standaard, bool Aangevinkt)> gedrag)
    {
        var voorstellen = AhKeuzeLeer.Verwerk(gedrag);
        if (voorstellen.Count == 0)
        {
            return;
        }
        var uit = voorstellen.Where(v => !v.NieuwStandaard).Select(v => v.Naam).ToList();
        var aan = voorstellen.Where(v => v.NieuwStandaard).Select(v => v.Naam).ToList();
        var tekst = "Ik zie een vast patroon in je laatste twee bestellingen.\n\n" +
            (uit.Count > 0
                ? "Telkens afgevinkt — voortaan standaard uitzetten?\n" +
                  string.Join("\n", uit.Select(n => "  • " + n)) + "\n\n"
                : "") +
            (aan.Count > 0
                ? "Telkens tóch aangevinkt — voortaan standaard aanzetten?\n" +
                  string.Join("\n", aan.Select(n => "  • " + n)) + "\n\n"
                : "") +
            "Zal ik dat zo instellen?";
        if (MessageBox.Show(this, tekst, "Albert Heijn – voorkeuren bijwerken",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        foreach (var voorstel in voorstellen)
        {
            foreach (var ingredient in _data.Gerechten.Values
                         .Concat(_data.Suggesties.Values).Concat(_data.Rubrieken.Values)
                         .SelectMany(l => l)
                         .Where(i => i.Naam.Equals(voorstel.Naam, StringComparison.OrdinalIgnoreCase)))
            {
                ingredient.Standaard = voorstel.NieuwStandaard;
            }
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(GerechtenFile, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch
        {
            // Niet kunnen bewaren mag de bestelling niet tegenhouden.
        }
    }

    /// <summary>
    /// Bewaart de productlinks die in de keuzestap handmatig gekozen zijn, zodat hetzelfde
    /// ingrediënt de volgende keer meteen goed staat.
    /// </summary>
    private void BewaarNieuweLinks(Dictionary<string, string> nieuw)
    {
        if (nieuw.Count == 0)
        {
            return;
        }
        foreach (var ingredient in _data.Gerechten.Values.Concat(_data.Suggesties.Values)
            .SelectMany(l => l))
        {
            if (nieuw.TryGetValue(ingredient.Naam, out var url))
            {
                ingredient.Url = url;
            }
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(GerechtenFile, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch
        {
            // Niet kunnen bewaren mag de bestelling niet tegenhouden.
        }
    }

    /// <summary>
    /// Opent de gsm-bestelpagina (zelfde link als bij de echtgenote) in de standaardbrowser —
    /// handig om de webversie snel naast de pc-versie te leggen.
    /// </summary>
    private void OpenWebversie()
    {
        var settings = AhWebSettings.Load();
        if (!settings.Compleet)
        {
            Toast.Toon(this, "Webversie niet ingesteld (ah-web-settings.json ontbreekt)", Fluent.Globe);
            return;
        }
        var link = $"{settings.Url}?t={Uri.EscapeDataString(settings.Token)}";
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch
        {
            Clipboard.SetText(link);
            Toast.Toon(this, "Kon de browser niet openen — de link staat op het klembord", Fluent.Globe);
        }
    }

    private async Task Bestel()
    {
        // Uit de vinkjes-administratie, niet uit de zichtbare lijst: met een actief tagfilter
        // tellen ook de weggefilterde keuzes mee. Namen van verdwenen weeksuggesties vallen af.
        var gekozen = _vinkjes
            .Where(n => _data.Gerechten.ContainsKey(n) || _weekSuggesties.ContainsKey(n) ||
                        _data.Rubrieken.ContainsKey(n) || _voorraad.ContainsKey(n))
            .ToList();
        if (gekozen.Count == 0)
        {
            Toast.Toon(this, "Vink eerst minstens één gerecht of rubriek aan", Fluent.Checkbox);
            return;
        }

        // Zelfde ingrediënt in meerdere gerechten: aantallen optellen (twee pastagerechten
        // betekent ook twee pakken pasta). Het is bewust een nieuwe AhIngredient per regel:
        // de keuzestap hieronder mag de opgeslagen gerechten niet aanpassen.
        var perGerecht = gekozen
            .SelectMany(g => (_data.Gerechten.TryGetValue(g, out var lijst) ? lijst
                    : _weekSuggesties.TryGetValue(g, out var sug) ? sug
                    : _data.Rubrieken.TryGetValue(g, out var rub) ? rub
                    : _voorraad.TryGetValue(g, out var vrd) ? vrd
                    : new List<AhIngredient>())
                .Select(i => (Gerecht: g, Ingredient: i)))
            .ToList();
        var ingredienten = perGerecht
            .GroupBy(x => x.Ingredient.Naam, StringComparer.OrdinalIgnoreCase)
            .Select(groep => new AhIngredient
            {
                Naam = groep.Key,
                Url = groep.Select(x => x.Ingredient.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
                // Hoeveelheden schalen naar het aantal eters (rubrieken schalen niet mee).
                Aantal = groep.Sum(x => FactorVoor(x.Gerecht) * Math.Max(1, x.Ingredient.Aantal)),
                // Standaard mee als minstens één bron dat wil (rozijn blijft uit, appel gaat aan).
                Standaard = groep.Any(x => x.Ingredient.Standaard),
            })
            .ToList();
        var herkomst = perGerecht
            .GroupBy(x => x.Ingredient.Naam, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groep => groep.Key,
                groep => string.Join(", ", groep.Select(x => x.Gerecht).Distinct()),
                StringComparer.OrdinalIgnoreCase);

        // Pescotariër aan tafel: bij elk vleesgerecht automatisch een plantaardige vervanger
        // voor het vleesbestanddeel meenemen — zelfde gerecht, één bord anders.
        var vervangerNamen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vervangerGerechten = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var gerecht in gekozen.Where(g =>
                     _data.Gerechten.ContainsKey(g) || _weekSuggesties.ContainsKey(g)))
        {
            if (AhVleesvervanger.Voor(IngredientenVoor(gerecht)) is not { } vervanger)
            {
                continue;
            }
            vervangerNamen[vervanger.Url] = vervanger.Naam;
            if (!vervangerGerechten.TryGetValue(vervanger.Url, out var lijst))
            {
                vervangerGerechten[vervanger.Url] = lijst = new List<string>();
            }
            lijst.Add(gerecht);
        }
        foreach (var (url, gerechtNamen) in vervangerGerechten)
        {
            if (ingredienten.Any(i => url.Equals(i.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // vervanger zit al in de boodschappen
            }
            var naam = vervangerNamen[url] + " (vleesvervanger)";
            ingredienten.Add(new AhIngredient { Naam = naam, Url = url, Aantal = 1 });
            herkomst[naam] = string.Join(", ", gerechtNamen.Distinct());
        }

        // Tussenstap: alles staat aangevinkt, met het product dat de lokale producttabel
        // erbij vindt. Alleen wat aangevinkt blijft gaat naar het mandje.
        using var keuze = new AhIngredientKeuzeForm(ingredienten, herkomst);
        if (keuze.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var producten = keuze.Producten;
        var handmatig = keuze.Handmatig;
        BewaarNieuweLinks(keuze.NieuweLinks);
        VerwerkKeuzeLeer(keuze.KeuzeGedrag);

        // Gekozen gerechten/suggesties (geen rubrieken) mogen op een avond in de agenda.
        await PlanGerechtenInAgenda(gekozen);

        ingredienten = producten.Concat(handmatig.Select(n => new AhIngredient { Naam = n })).ToList();

        // Keuze bewaren en de volledige lijst op het klembord zetten (handig voor het
        // handmatige deel, en als reserve wanneer de browser niet meewerkt).
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(BestellingFile, JsonSerializer.Serialize(new
        {
            datum = DateTimeOffset.Now,
            gerechten = gekozen,
            ingredienten = ingredienten.Select(i => i.Naam).ToList(),
            producten,
        }, JsonOpts));

        // Het voorraadgeheugen bijwerken: hieruit leidt de app volgende keer af wat er
        // waarschijnlijk weer op is.
        AhHistoriek.Registreer(ingredienten.Select(i => i.Naam));
        Clipboard.SetText("AH-boodschappen:\n" +
            string.Join("\n", ingredienten.Select(i => "- " + i.Naam + (i.Url is null ? "" : " (automatisch)"))));

        if (producten.Count > 0)
        {
            using var winkel = new AhWinkelForm(producten, handmatig);
            winkel.ShowDialog(this);
            if (winkel.TerugGevraagd)
            {
                // Terug naar de bestellijst om andere gerechten te kiezen; de vinkjes staan er
                // nog en wat al in het mandje ligt blijft staan (vullen is idempotent).
                return;
            }
        }
        else
        {
            // Geen enkele productlink: oude gedrag, ah.be openen met de lijst op het klembord.
            try
            {
                Process.Start(new ProcessStartInfo("https://www.ah.be/producten") { UseShellExecute = true });
            }
            catch
            {
                // Geen standaardbrowser gevonden; de lijst staat in elk geval op het klembord.
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Laat de gekozen gerechten/suggesties op een avond inplannen (datumkiezer) en zet elke
    /// geplande maaltijd als afspraak in Google Calendar, met de ingrediënten en het recept
    /// (incl. bereidingstijd) in de omschrijving.
    /// </summary>
    private async Task PlanGerechtenInAgenda(List<string> gekozen)
    {
        // Alleen echte maaltijden (gerechten + suggesties van deze week), geen rubrieken.
        var maaltijden = gekozen
            .Where(n => _data.Gerechten.ContainsKey(n) || _weekSuggesties.ContainsKey(n))
            .ToList();
        if (maaltijden.Count == 0)
        {
            return;
        }
        if (!CalendarClient.Beschikbaar)
        {
            Toast.Toon(this, "Stel eerst je Gmail-koppeling in om in de agenda te schrijven", Fluent.Kalender);
            return;
        }

        var voorKiezer = maaltijden
            .Select(n => (Naam: n, Minuten: _data.Recepten.GetValueOrDefault(n)?.Minuten ?? 0))
            .ToList();
        using var kiezer = new AhAgendaForm(voorKiezer);
        if (kiezer.ShowDialog(this) != DialogResult.OK || kiezer.Geplande.Count == 0)
        {
            return;
        }

        var gelukt = 0;
        foreach (var (naam, start, duur) in kiezer.Geplande)
        {
            try
            {
                // Mes-en-vork in de titel: zo is het avondeten ook in Google Agenda zelf in
                // één oogopslag te herkennen (zoals de 🛒 bij de AH-levering).
                if (await CalendarClient.MaakAfspraakAsync(
                        "🍴 " + naam, start, duur, AgendaOmschrijving(naam), CancellationToken.None))
                {
                    gelukt++;
                }
            }
            catch
            {
                // Eén mislukte afspraak mag de rest niet tegenhouden.
            }
        }
        Toast.Toon(this,
            gelukt == kiezer.Geplande.Count
                ? $"{gelukt} gerecht(en) in de agenda gezet"
                : $"{gelukt} van {kiezer.Geplande.Count} in de agenda gezet",
            Fluent.Kalender);
    }

    /// <summary>Opschaalfactor voor een gerecht op basis van het aantal eters (rubrieken schalen niet).</summary>
    private int FactorVoor(string gerecht)
    {
        // Rubrieken en nabestellingen staan los van het aantal eters: één pak wasmiddel blijft
        // één pak wasmiddel.
        if (_data.Rubrieken.ContainsKey(gerecht) || _voorraad.ContainsKey(gerecht))
        {
            return 1;
        }
        var basis = Math.Max(1, _data.Recepten.GetValueOrDefault(gerecht)?.Personen ?? 4);
        return Math.Max(1, (int)Math.Ceiling((double)(int)_personen.Value / basis));
    }

    /// <summary>Bouwt de tekst van de agenda-afspraak: recept + bereidingstijd + porties + boodschappenlijst.</summary>
    private string AgendaOmschrijving(string maaltijd)
    {
        var regels = new List<string>();
        if (_data.Recepten.GetValueOrDefault(maaltijd) is { } recept)
        {
            if (recept.Tekst.Length > 0)
            {
                regels.Add(recept.Tekst);
            }
            if (recept.Minuten > 0)
            {
                regels.Add($"Bereidingstijd: {recept.Minuten} min");
            }
        }
        regels.Add($"Voor {(int)_personen.Value} personen");
        var ingredienten = _data.Gerechten.TryGetValue(maaltijd, out var g) ? g
            : _weekSuggesties.TryGetValue(maaltijd, out var s) ? s
            : new List<AhIngredient>();
        if (ingredienten.Count > 0)
        {
            regels.Add(regels.Count > 0 ? "\nBoodschappen:" : "Boodschappen:");
            regels.AddRange(ingredienten.Select(i =>
                "- " + i.Naam + (i.Aantal > 1 ? $" ({i.Aantal}x)" : "")));
        }
        return string.Join("\n", regels);
    }
}
