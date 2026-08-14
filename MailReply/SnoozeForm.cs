namespace WorkManager;

/// <summary>
/// Dialoog voor het snoozen van mails: bovenaan het (lerende) voorstel, daaronder enkele
/// vaste alternatieven en een vrije datum/tijd-keuze.
/// </summary>
public class SnoozeForm : Form
{
    private readonly List<(RadioButton Knop, DateTimeOffset Waarde)> _opties = new();
    private readonly RadioButton _anders;
    private readonly DateTimePicker _picker;
    private readonly DatumKiezer _datum;
    private readonly RadioButton? _slimKnop;
    private DateTimeOffset _slimWaarde;
    private readonly CancellationTokenSource _slimCts = new();

    public DateTimeOffset Gekozen { get; private set; }

    /// <param name="slimVoorstel">
    /// Optioneel: levert asynchroon een inhoudelijk voorstel (Claude leest de mail). Het
    /// verschijnt als extra keuzerondje zodra het er is; de dialoog wacht er niet op.
    /// </param>
    public SnoozeForm(int aantal, DateTimeOffset voorstel, string enkelvoud = "Mail", string meervoud = "mails",
        Func<CancellationToken, Task<(DateTimeOffset Moment, string Reden)?>>? slimVoorstel = null)
    {
        Text = aantal == 1 ? $"{enkelvoud} snoozen" : $"{aantal} {meervoud} snoozen";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(440, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(14, 12, 14, 0),
        };

        void VoegOptieToe(string tekst, DateTimeOffset waarde, bool vet = false, bool geselecteerd = false)
        {
            var knop = new RadioButton
            {
                Text = tekst,
                AutoSize = true,
                Checked = geselecteerd,
                Margin = new Padding(0, 4, 0, 4),
            };
            if (vet)
            {
                knop.Font = new Font(Font, FontStyle.Bold);
            }
            _opties.Add((knop, waarde));
            panel.Controls.Add(knop);
        }

        var nu = DateTimeOffset.Now;
        VoegOptieToe($"Voorstel: {voorstel:dddd d MMMM 'om' HH:mm}", voorstel, vet: true, geselecteerd: true);

        // Het slimme voorstel druppelt asynchroon binnen: eerst een wachttekst, en zodra
        // Claude de mail gelezen heeft wordt het rondje een echte keuze met de reden erbij.
        if (slimVoorstel is not null)
        {
            _slimKnop = new RadioButton
            {
                Text = "🤖 Claude leest de mail…",
                AutoSize = true,
                Enabled = false,
                Margin = new Padding(0, 4, 0, 4),
                ForeColor = Theme.Muted,
            };
            panel.Controls.Add(_slimKnop);
            Shown += async (_, _) =>
            {
                try
                {
                    var slim = await slimVoorstel(_slimCts.Token);
                    if (IsDisposed || _slimKnop.IsDisposed)
                    {
                        return;
                    }
                    if (slim is { } s)
                    {
                        _slimWaarde = s.Moment;
                        _slimKnop.Text = $"🤖 {s.Moment:dddd d MMMM 'om' HH:mm}" +
                                         (s.Reden.Length > 0 ? $" — {s.Reden}" : "");
                        _slimKnop.Enabled = true;
                        _slimKnop.ForeColor = Theme.Text;
                    }
                    else
                    {
                        _slimKnop.Visible = false;
                    }
                }
                catch
                {
                    if (!IsDisposed && !_slimKnop.IsDisposed)
                    {
                        _slimKnop.Visible = false; // geen voorstel is geen ramp
                    }
                }
            };
            FormClosed += (_, _) => _slimCts.Cancel();
        }

        var vanavond = new DateTimeOffset(nu.Year, nu.Month, nu.Day, 18, 0, 0, nu.Offset);
        if (vanavond > nu && vanavond != voorstel)
        {
            VoegOptieToe($"Vanavond om 18:00", vanavond);
        }
        var morgen = new DateTimeOffset(nu.Year, nu.Month, nu.Day, 8, 0, 0, nu.Offset).AddDays(1);
        if (morgen != voorstel)
        {
            VoegOptieToe($"Morgen om 08:00 ({morgen:ddd d MMM})", morgen);
        }
        var dagenTotMaandag = ((int)DayOfWeek.Monday - (int)nu.DayOfWeek + 7 - 1) % 7 + 1;
        var maandag = new DateTimeOffset(nu.Year, nu.Month, nu.Day, 8, 0, 0, nu.Offset)
            .AddDays(dagenTotMaandag);
        if (maandag != voorstel)
        {
            VoegOptieToe($"Volgende week maandag om 08:00 ({maandag:d MMM})", maandag);
        }

        // Vrije keuze
        _anders = new RadioButton { Text = "Ander moment:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
        // Datum met de gedeelde kiezer (kalender + snelkeuzes), uur ernaast.
        _datum = new DatumKiezer
        {
            Width = 180, LeegToegestaan = false,
            Waarde = DateOnly.FromDateTime(voorstel.LocalDateTime),
        };
        _datum.WaardeGewijzigd += (_, _) => _anders.Checked = true;
        _picker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 80,
            Margin = new Padding(6, 4, 0, 0),
            Value = voorstel.LocalDateTime,
        };
        _picker.ValueChanged += (_, _) => _anders.Checked = true;
        var andersRij = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0),
            WrapContents = false,
        };
        andersRij.Controls.Add(_anders);
        andersRij.Controls.Add(_datum);
        andersRij.Controls.Add(_picker);
        panel.Controls.Add(andersRij);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Snoozen", Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => Bevestig();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(panel);
        Controls.Add(buttons);
        Theme.Apply(this);
    }

    private void Bevestig()
    {
        Gekozen = _slimKnop is { Checked: true }
            ? _slimWaarde
            : _anders.Checked
                ? new DateTimeOffset(
                    (_datum.Waarde ?? DateOnly.FromDateTime(DateTime.Today))
                        .ToDateTime(TimeOnly.FromDateTime(_picker.Value)))
                : _opties.First(o => o.Knop.Checked).Waarde;
        if (Gekozen <= DateTimeOffset.Now)
        {
            MessageBox.Show(this, "Kies een moment in de toekomst.", "WorkManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
