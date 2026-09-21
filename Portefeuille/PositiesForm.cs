using System.Globalization;

namespace WorkManager;

/// <summary>
/// Bewerkvenster voor de posities: per regel een fonds, het aantal stuks, het potje waar het
/// in zit en (optioneel) de gemiddelde aankoopkoers — die laatste alleen nodig als je het
/// rendement sinds aankoop wilt zien. De ticker is die van Yahoo Finance, mét beurssuffix:
/// IWDA.AS voor Euronext Amsterdam, IS3Q.DE voor Xetra.
/// </summary>
public sealed class PositiesForm : Form
{
    private const int KolTicker = 0;
    private const int KolNaam = 1;
    private const int KolAantal = 2;
    private const int KolPotje = 3;
    private const int KolAankoop = 4;

    private readonly Portefeuille _portefeuille;
    private readonly DataGridView _grid = new();
    private readonly Label _status;
    private readonly ModernButton _controleerKnop;
    private readonly CancellationTokenSource _cts = new();

    public PositiesForm(Portefeuille portefeuille)
    {
        _portefeuille = portefeuille;
        Text = "Posities";
        Size = new Size(860, 480);
        MinimumSize = new Size(700, 360);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Icon = Theme.AppIcon;

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = true;
        _grid.AllowUserToDeleteRows = true;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ticker", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Naam",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Aantal",
            Width = 90,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Potje", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Aankoopkoers",
            Width = 150,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
            ToolTipText = "Gemiddelde aankoopkoers per stuk; leeg laten als je geen rendement wilt zien.",
        });
        Theme.StyleGrid(_grid);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(10, 7, 10, 0),
            Text = "Ticker mét beurssuffix (.AS = Amsterdam, .DE = Xetra, .PA = Parijs, .L = Londen).",
        };
        Theme.AsStatus(_status);

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10, 8, 10, 6),
        };
        var annuleer = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 110 };
        annuleer.Click += (_, _) => Close();
        var bewaar = new ModernButton { Text = "Bewaren", Kind = ButtonKind.Accent, Width = 110 };
        bewaar.Click += async (_, _) => await BewaarAsync();
        _controleerKnop = new ModernButton { Text = "Namen ophalen", Glyph = Fluent.Sync };
        _controleerKnop.Click += async (_, _) => await ControleerAsync();
        var verwijder = new ModernButton { Text = "Regel verwijderen", Glyph = Fluent.Delete };
        verwijder.Click += (_, _) => VerwijderRegel();
        var toevoegen = new ModernButton { Text = "Regel toevoegen", Glyph = Fluent.Add };
        toevoegen.Click += (_, _) => VoegRegelToe();
        foreach (var knop in new[] { _controleerKnop, verwijder, toevoegen })
        {
            knop.KrimpNaarInhoud();
        }
        knoppen.Controls.Add(annuleer);
        knoppen.Controls.Add(bewaar);
        knoppen.Controls.Add(_controleerKnop);
        knoppen.Controls.Add(verwijder);
        knoppen.Controls.Add(toevoegen);
        CancelButton = annuleer;
        AcceptButton = bewaar;

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);

        Vul();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private void Vul()
    {
        var nl = CultureInfo.GetCultureInfo("nl-BE");
        foreach (var positie in _portefeuille.Posities)
        {
            _grid.Rows.Add(
                positie.Ticker,
                positie.Naam,
                positie.Aantal % 1 == 0 ? positie.Aantal.ToString("0", nl) : positie.Aantal.ToString("0.####", nl),
                positie.Potje,
                positie.AankoopKoers is { } koers ? koers.ToString("0.##", nl) : "");
        }
    }

    /// <summary>
    /// Nieuwe regel onderaan, met de cursor meteen in het tickerveld. Het potje wordt gevuld
    /// met dat van de laatste regel — meestal voeg je meerdere fondsen in hetzelfde potje toe.
    /// </summary>
    private void VoegRegelToe()
    {
        var vorigPotje = _grid.Rows.Cast<DataGridViewRow>()
            .LastOrDefault(r => !r.IsNewRow && Cel(r, KolPotje).Length > 0) is { } laatste
            ? Cel(laatste, KolPotje)
            : Portefeuille.PrivePotje;
        var index = _grid.Rows.Add("", "", "", vorigPotje, "");
        _grid.CurrentCell = _grid.Rows[index].Cells[KolTicker];
        _grid.BeginEdit(selectAll: true);
    }

    private void VerwijderRegel()
    {
        if (_grid.CurrentRow is { IsNewRow: false } rij)
        {
            _grid.Rows.Remove(rij);
        }
    }

    /// <summary>Vult de naam van elk fonds aan met wat de beurs ervan zegt — meteen een tickercontrole.</summary>
    private async Task ControleerAsync()
    {
        _controleerKnop.Bezig = true;
        _controleerKnop.Enabled = false;
        try
        {
            var onbekend = new List<string>();
            foreach (DataGridViewRow rij in _grid.Rows)
            {
                if (rij.IsNewRow || Cel(rij, KolTicker) is not { Length: > 0 } ticker)
                {
                    continue;
                }
                var koers = await Koersen.HaalAsync(ticker, _cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    return;
                }
                if (koers is null)
                {
                    onbekend.Add(ticker);
                    rij.Cells[KolTicker].Style.ForeColor = Theme.Danger;
                    continue;
                }
                rij.Cells[KolTicker].Style.ForeColor = Theme.Text;
                rij.Cells[KolNaam].Value = koers.Naam;
            }
            _status.Text = onbekend.Count == 0
                ? "Alle tickers gevonden op de beurs."
                : $"Niet gevonden: {string.Join(", ", onbekend)} — controleer het beurssuffix.";
            _status.ForeColor = onbekend.Count == 0 ? Theme.Success : Theme.Warn;
        }
        finally
        {
            if (!IsDisposed)
            {
                _controleerKnop.Bezig = false;
                _controleerKnop.Enabled = true;
            }
        }
    }

    private async Task BewaarAsync()
    {
        var posities = new List<Positie>();
        foreach (DataGridViewRow rij in _grid.Rows)
        {
            if (rij.IsNewRow)
            {
                continue;
            }
            var ticker = Cel(rij, KolTicker);
            if (ticker.Length == 0)
            {
                continue;
            }
            if (!Getal(Cel(rij, KolAantal), out var aantal) || aantal <= 0)
            {
                Toast.Toon(this, $"{ticker}: vul een aantal in dat groter is dan nul.", Fluent.Lijst);
                return;
            }
            var potje = Cel(rij, KolPotje);
            posities.Add(new Positie
            {
                Ticker = ticker.ToUpperInvariant(),
                Naam = Cel(rij, KolNaam),
                Aantal = aantal,
                Potje = potje.Length == 0 ? Portefeuille.PrivePotje : potje,
                AankoopKoers = Getal(Cel(rij, KolAankoop), out var koers) && koers > 0 ? koers : null,
            });
        }

        // Een typfout in een ticker geeft anders stil een verkeerd totaal: even vragen.
        var onbekend = new List<string>();
        foreach (var ticker in posities.Select(p => p.Ticker).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await Koersen.HaalAsync(ticker, _cts.Token) is null)
            {
                onbekend.Add(ticker);
            }
        }
        if (_cts.IsCancellationRequested)
        {
            return;
        }
        if (onbekend.Count > 0 &&
            MessageBox.Show(this,
                $"Geen koers gevonden voor: {string.Join(", ", onbekend)}.\n\n" +
                "Dat kan aan een typfout in de ticker liggen, of aan de verbinding.\nToch bewaren?",
                "Portefeuille", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _portefeuille.Posities = posities;
        PortefeuilleStore.Bewaar(_portefeuille);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string Cel(DataGridViewRow rij, int kolom) =>
        (rij.Cells[kolom].Value as string ?? rij.Cells[kolom].Value?.ToString() ?? "").Trim();

    /// <summary>Getal met komma of punt: beide invoerwijzen komen in de praktijk voor.</summary>
    private static bool Getal(string tekst, out double waarde)
    {
        tekst = tekst.Replace(" ", "").Replace(".", ",");
        return double.TryParse(tekst, NumberStyles.Float,
            CultureInfo.GetCultureInfo("nl-BE"), out waarde);
    }
}
