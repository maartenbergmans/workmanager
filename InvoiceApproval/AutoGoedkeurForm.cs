using System.Globalization;

namespace WorkManager;

/// <summary>
/// De goedkeuringsvraag van de dagelijkse ISPnext-ronde: dit zijn de facturen die volgens de
/// regels automatisch mogen, klaar om in één klik weg te zijn. Goedkeuren gebeurt nooit zonder
/// dat deze vraag beantwoord is — op uitdrukkelijke vraag van Maarten.
///
/// <para>Bij "Goedkeuren" leest een verborgen WebView2 de lijst opnieuw en keurt dan precies de
/// facturen goed die hier aangevinkt staan (op leverancier + factuurnummer). Is er intussen
/// iets bijgekomen, dan blijft dat liggen tot de volgende ronde: wat je niet gezien hebt,
/// wordt niet goedgekeurd. Vinkjes kun je zelf uitzetten; het grote venster blijft er voor
/// alles wat aandacht vraagt.</para>
/// </summary>
public sealed class AutoGoedkeurForm : Form
{
    private static readonly CultureInfo Cultuur = CultureInfo.GetCultureInfo("nl-BE");

    private static AutoGoedkeurForm? _open;

    private readonly ModernListView _lijst;
    private readonly Label _status;
    private readonly ModernButton _keurKnop;
    private readonly CancellationTokenSource _cts = new();
    private bool _bezig;

    /// <summary>
    /// Toont de vraag voor deze peiling; staat ze al open, dan komt dat venster naar voren
    /// (de dagmelding kan twee keer aangeklikt worden).
    /// </summary>
    public static void Toon(IspPeiling peiling)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        _open = new AutoGoedkeurForm(peiling);
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
    }

    private AutoGoedkeurForm(IspPeiling peiling)
    {
        Text = "Facturen goedkeuren (ISPnext)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 420);
        MinimumSize = new Size(640, 320);
        ShowInTaskbar = true;

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LegeTekst = "Niets dat de regels automatisch goedkeuren",
            LeegGlyph = Fluent.Check,
        };
        _lijst.Columns.Add("Leverancier", 240);
        _lijst.Columns.Add("Factuurnummer", 160);
        _lijst.Columns.Add("Vervaldatum", 110);
        _lijst.Columns.Add("Bedrag", 110);
        _lijst.Columns.Add("Regel", 230);
        foreach (var f in peiling.Facturen.Where(f => f.Auto))
        {
            var item = new ListViewItem(f.Leverancier) { Tag = f, Checked = true };
            item.SubItems.Add(f.Factuurnummer);
            var verval = item.SubItems.Add(f.Vervaldatum + (f.Vervallen ? " ⚠" : ""));
            if (f.Vervallen)
            {
                verval.ForeColor = Theme.Warn;
            }
            item.SubItems.Add(f.Bedrag is { } b ? string.Create(Cultuur, $"€ {b:N2}") : f.BedragText);
            item.SubItems.Add(f.Reden);
            _lijst.Items.Add(item);
        }

        _status = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 10, 0) };
        Theme.AsStatus(_status);

        // Wat niet automatisch mag, hoort hier niet thuis — maar wel vermeld worden, anders
        // lijkt het alsof er niets meer wacht.
        var rest = peiling.Facturen.Count(f => !f.Auto);
        var uitleg = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(10, 6, 10, 0),
            Text = rest == 0
                ? "Alles in de lijst valt onder de regels."
                : $"{rest} factuur/facturen vallen buiten de regels en blijven wachten — " +
                  "die bekijk je in het volledige venster.",
        };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var later = new ModernButton { Text = "Later", DialogResult = DialogResult.Cancel, Width = 100 };
        _keurKnop = new ModernButton { Text = "Goedkeuren", Width = 150, Kind = ButtonKind.Accent };
        _keurKnop.Click += async (_, _) => await KeurAsync();
        var openKnop = new ModernButton { Text = "Volledig venster…", Width = 170 };
        openKnop.Click += (_, _) =>
        {
            new InvoiceApprovalForm().Show();
            Close();
        };
        knoppen.Controls.Add(later);
        knoppen.Controls.Add(_keurKnop);
        knoppen.Controls.Add(openKnop);
        CancelButton = later;

        Controls.Add(_lijst);
        Controls.Add(_status);
        Controls.Add(uitleg);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        uitleg.ForeColor = Theme.Muted;
        VensterGeheugen.Volg(this, "autogoedkeur");

        _lijst.ItemChecked += (_, _) => WerkStatusBij();
        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
        WerkStatusBij();
    }

    private List<IspFactuur> Aangevinkt() =>
        _lijst.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<IspFactuur>().ToList();

    private void WerkStatusBij()
    {
        if (_bezig)
        {
            return;
        }
        var gekozen = Aangevinkt();
        var totaal = gekozen.Sum(f => f.Bedrag ?? 0);
        _status.Text = gekozen.Count == 0
            ? "Niets aangevinkt."
            : string.Create(Cultuur, $"{gekozen.Count} van {_lijst.Items.Count} aangevinkt — € {totaal:N2}");
        _keurKnop.Enabled = gekozen.Count > 0;
    }

    private async Task KeurAsync()
    {
        var gekozen = Aangevinkt();
        if (_bezig || gekozen.Count == 0)
        {
            return;
        }
        _bezig = true;
        _keurKnop.Enabled = false;
        _keurKnop.Bezig = true;
        _lijst.Enabled = false;
        var regels = new List<string>();
        try
        {
            _status.Text = "Facturenlijst opnieuw ophalen en goedkeuren…";
            var sleutels = gekozen.Select(IspNextClient.Sleutel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (peiling, goedgekeurd) = await IspNextClient.Instance.PeilEnGoedkeurAsync(
                _cts.Token, r => regels.Add(r), proef: false, alleen: sleutels);
            if (IsDisposed)
            {
                return;
            }
            if (goedgekeurd.Count > 0)
            {
                var totaal = goedgekeurd.Sum(f => f.Bedrag ?? 0);
                _status.Text = string.Create(Cultuur,
                    $"{goedgekeurd.Count} facturen goedgekeurd — € {totaal:N2}");
                Toast.Toon(this, _status.Text, Fluent.Check);
                IspRadar.NaGoedkeuring(peiling, goedgekeurd);
                foreach (var item in _lijst.Items.Cast<ListViewItem>()
                             .Where(i => i.Tag is IspFactuur f && goedgekeurd.Contains(f)).ToList())
                {
                    _lijst.Items.Remove(item);
                }
                if (_lijst.Items.Count == 0)
                {
                    Close();
                    return;
                }
            }
            else
            {
                // Niets verstuurd: de reden staat in de logregels van de ronde.
                _status.Text = peiling is null
                    ? "Niet gelukt: de facturenpagina was niet te lezen. Probeer het volledige venster."
                    : "Niets goedgekeurd — " + (regels.LastOrDefault() ?? "onbekende reden");
                Toast.Fout(this, "Niets goedgekeurd", string.Join(Environment.NewLine, regels));
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens de ronde.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Toast.Fout(this, "Goedkeuren mislukt", ex.Message);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _bezig = false;
                _keurKnop.Bezig = false;
                _lijst.Enabled = true;
                WerkStatusBij();
            }
        }
    }
}
