namespace WorkManager;

/// <summary>
/// Instellingen van de reisassistent: vanwaar je vertrekt, hoeveel marge je wil en hoe vroeg
/// de melding komt. Bij het opslaan wordt het adres meteen opgezocht, zodat je ziet of het
/// gevonden wordt en de coördinaten daarna hergebruikt kunnen worden.
/// </summary>
public class ReisSettingsForm : Form
{
    private readonly TextBox _adres;
    private readonly NumericUpDown _buffer;
    private readonly NumericUpDown _waarschuw;
    private readonly NumericUpDown _minimum;
    private readonly CheckBox _aan;
    private readonly Label _status;
    private readonly ModernButton _opslaan;
    private readonly ReisSettings _settings;

    public ReisSettingsForm()
    {
        _settings = ReisSettings.Load();

        Text = "Reisassistent";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(560, 380);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(14, 12, 14, 0),
            Text = "Voor afspraken met een adres in de agenda berekent WorkManager de rijtijd met " +
                   "het verkeer van dat moment (via Waze) en waarschuwt hij wanneer je moet " +
                   "vertrekken. Online afspraken worden overgeslagen.",
        };

        var paneel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14, 4, 14, 4),
        };
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        paneel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _adres = new TextBox { Text = _settings.ThuisAdres, Width = 320, Margin = new Padding(0, 4, 0, 8) };
        _buffer = Getal(_settings.BufferMinuten, 0, 120);
        _waarschuw = Getal(_settings.WaarschuwMinuten, 0, 120);
        _minimum = Getal(_settings.MinimumRijMinuten, 0, 120);
        _aan = new CheckBox
        {
            Text = "Reiswaarschuwingen aan", Checked = _settings.Aan, AutoSize = true,
            Margin = new Padding(0, 6, 0, 6),
        };

        VoegRij(paneel, "Vertrekadres (thuis)", _adres);
        VoegRij(paneel, "Marge bovenop de rijtijd", Met(_buffer, "minuten"));
        VoegRij(paneel, "Waarschuwen vóór vertrek", Met(_waarschuw, "minuten"));
        VoegRij(paneel, "Negeer ritten korter dan", Met(_minimum, "minuten"));
        paneel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, paneel.RowCount);
        paneel.Controls.Add(_aan, 1, paneel.RowCount);
        paneel.RowCount++;

        _status = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 4, 14, 0), ForeColor = Theme.Muted,
            Text = _settings.HeeftThuis
                ? $"Adres gekend ({_settings.ThuisLat:0.####}, {_settings.ThuisLon:0.####})."
                : "Nog geen adres opgezocht.",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52,
            Padding = new Padding(10),
        };
        var annuleer = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 110 };
        _opslaan = new ModernButton
        {
            Text = "Opslaan", Width = 130, Kind = ButtonKind.Accent, Glyph = Fluent.Check,
        };
        _opslaan.Click += async (_, _) => await Opslaan();
        buttons.Controls.Add(annuleer);
        buttons.Controls.Add(_opslaan);
        CancelButton = annuleer;

        Controls.Add(paneel);
        Controls.Add(_status);
        Controls.Add(hint);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
    }

    private static NumericUpDown Getal(int waarde, int min, int max) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(waarde, min, max), Width = 70,
        Margin = new Padding(0, 4, 6, 6),
    };

    private static Control Met(NumericUpDown getal, string eenheid)
    {
        var rij = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rij.Controls.Add(getal);
        rij.Controls.Add(new Label
        {
            Text = eenheid, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 9, 0, 0),
        });
        return rij;
    }

    private static void VoegRij(TableLayoutPanel paneel, string label, Control control)
    {
        var rij = paneel.RowCount;
        paneel.Controls.Add(new Label
        {
            Text = label, AutoSize = true, Margin = new Padding(0, 8, 8, 6), ForeColor = Theme.Muted,
        }, 0, rij);
        paneel.Controls.Add(control, 1, rij);
        paneel.RowCount = rij + 1;
    }

    private async Task Opslaan()
    {
        var adres = _adres.Text.Trim();
        _settings.BufferMinuten = (int)_buffer.Value;
        _settings.WaarschuwMinuten = (int)_waarschuw.Value;
        _settings.MinimumRijMinuten = (int)_minimum.Value;
        _settings.Aan = _aan.Checked;

        if (adres.Length == 0)
        {
            _settings.ThuisAdres = "";
            _settings.ThuisLat = _settings.ThuisLon = 0;
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        // Adres opnieuw opzoeken zodra het gewijzigd is (of nog geen coördinaten heeft).
        if (adres != _settings.ThuisAdres || !_settings.HeeftThuis)
        {
            _opslaan.Bezig = true;
            _status.Text = "Adres opzoeken…";
            try
            {
                var punt = await Reistijd.GeocodeAsync(adres, CancellationToken.None);
                if (punt is null)
                {
                    _status.Text = "Adres niet gevonden — probeer het volledige adres met gemeente.";
                    _status.ForeColor = Theme.Danger;
                    return;
                }
                _settings.ThuisLat = punt.Lat;
                _settings.ThuisLon = punt.Lon;
            }
            finally
            {
                _opslaan.Bezig = false;
            }
        }

        _settings.ThuisAdres = adres;
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}
