namespace WorkManager;

/// <summary>
/// Dialoog om een Google-agenda-afspraak aan te maken of te bewerken (titel, datum, begintijd,
/// duur en omschrijving). Wordt gebruikt door de cockpit; het schrijven zelf gaat via
/// <see cref="CalendarClient"/>.
/// </summary>
public sealed class AgendaAfspraakForm : Form
{
    /// <summary>
    /// Markering in de omschrijving voor afspraken waar je gewoon kunt doorwerken (een
    /// verwachte levering, een was die draait): de dagplanner behandelt ze dan niet als
    /// blokkerend, alleen als geheugensteun.
    /// </summary>
    public const string WerkbaarMarker = "[werkbaar]";

    private readonly TextBox _titel;
    private readonly DatumKiezer _datum;
    private readonly DateTimePicker _tijd;
    private readonly NumericUpDown _duur;
    private readonly Label _eindeLabel;
    private readonly TextBox _locatie;
    private readonly TextBox _omschrijving;
    private readonly CheckBox _werkbaar;

    public string Titel => _titel.Text.Trim();

    public DateTime Start =>
        (_datum.Waarde ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue)
        + _tijd.Value.TimeOfDay;
    public TimeSpan Duur => TimeSpan.FromMinutes((double)_duur.Value);
    public string Locatie => _locatie.Text.Trim();

    /// <summary>De omschrijving, met de werkbaar-marker erin als dat vakje aanstaat.</summary>
    public string Omschrijving
    {
        get
        {
            var tekst = _omschrijving.Text.Trim();
            return _werkbaar.Checked
                ? (tekst.Length > 0 ? tekst + "\n" : "") + WerkbaarMarker
                : tekst;
        }
    }

    /// <param name="bestaand">Bestaande afspraak om voor te vullen (bewerken); null = nieuw.</param>
    /// <param name="alsNieuw">True = voorgevuld maar tóch een nieuwe afspraak (bv. vanuit een mail).</param>
    public AgendaAfspraakForm(AgendaClient.AgendaItem? bestaand = null, bool alsNieuw = false)
    {
        var bewerken = bestaand is not null && !alsNieuw;
        Text = bewerken ? "Afspraak bewerken" : "Nieuwe afspraak";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(460, 424);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var start = bestaand?.Start.LocalDateTime ?? RondAf(DateTime.Now.AddHours(1));

        Label Lbl(string t, int y) => new() { Text = t, AutoSize = true, Location = new Point(16, y + 4) };

        _titel = new TextBox
        {
            Text = bestaand?.Titel ?? "", Location = new Point(110, 16), Width = 316,
        };
        _datum = new DatumKiezer
        {
            Location = new Point(110, 50), Width = 190, LeegToegestaan = false,
            Waarde = DateOnly.FromDateTime(start.Date),
        };
        _tijd = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time, ShowUpDown = true,
            Location = new Point(308, 52), Width = 90, Value = start,
        };
        _duur = new NumericUpDown
        {
            Minimum = 5, Maximum = 1440, Increment = 15,
            Value = bestaand is { } b ? Math.Clamp((int)(b.Einde - b.Start).TotalMinutes, 5, 1440) : 60,
            Location = new Point(110, 88), Width = 80,
        };
        // Duur is handig om te typen, maar je denkt in "tot hoe laat": het eindtijdstip
        // staat er daarom live naast.
        _eindeLabel = new Label { AutoSize = true, Location = new Point(200, 92) };
        void WerkEindeBij() =>
            _eindeLabel.Text = $"→ tot {Start.AddMinutes((double)_duur.Value):HH:mm}";
        _duur.ValueChanged += (_, _) => WerkEindeBij();
        _tijd.ValueChanged += (_, _) => WerkEindeBij();
        _datum.WaardeGewijzigd += (_, _) => WerkEindeBij();
        WerkEindeBij();
        _locatie = new TextBox
        {
            Text = bestaand?.Locatie ?? "", Location = new Point(110, 124), Width = 316,
            PlaceholderText = "adres of plaats (leeg = geen)",
        };
        // De werkbaar-marker hoort niet zichtbaar in het tekstvak; het vinkje neemt het over.
        var omschrijving = (bestaand?.Omschrijving ?? "")
            .Replace(WerkbaarMarker, "", StringComparison.OrdinalIgnoreCase).Trim();
        _omschrijving = new TextBox
        {
            Text = omschrijving, Location = new Point(110, 160), Width = 316,
            Height = 96, Multiline = true, ScrollBars = ScrollBars.Vertical,
        };
        _werkbaar = new CheckBox
        {
            Text = "Blokkeert mijn agenda niet — ik kan ondertussen werken\n" +
                   "(bv. een verwachte levering)",
            AutoSize = false, Location = new Point(110, 264), Size = new Size(320, 40),
            // De vlag kan in de omschrijving zitten ([werkbaar]-marker) óf alleen lokaal
            // bewaard zijn (Gmail kent hem niet en weigert soms de omschrijving-wijziging).
            Checked = bestaand is { } wb &&
                (wb.Omschrijving.Contains(WerkbaarMarker, StringComparison.OrdinalIgnoreCase) ||
                 WerkbaarStore.Is(wb)),
        };

        var ok = new ModernButton
        {
            Text = bewerken ? "Opslaan" : "Aanmaken", Width = 120, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(306, 334), Glyph = Fluent.Check,
        };
        ok.Click += (_, _) =>
        {
            if (Titel.Length == 0)
            {
                MessageBox.Show(this, "Vul een titel in.", "Agenda",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(196, 334),
        };

        Controls.AddRange(new Control[]
        {
            Lbl("Titel", 16), _titel,
            Lbl("Datum", 52), _datum, _tijd,
            Lbl("Duur (min)", 88), _duur, _eindeLabel,
            Lbl("Locatie", 124), _locatie,
            Lbl("Notitie", 160), _omschrijving,
            _werkbaar,
            ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.Apply(this);
        _eindeLabel.ForeColor = Theme.Muted;
    }

    private static DateTime RondAf(DateTime dt) =>
        new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute < 30 ? 30 : 0, 0)
            .AddHours(dt.Minute < 30 ? 0 : 1);
}
