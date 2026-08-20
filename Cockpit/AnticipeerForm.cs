namespace WorkManager;

/// <summary>
/// Vooruitblik op de takenlijst: de vijf taken die als eerste op je afkomen — inclusief de
/// taken die nú nog verborgen zijn omdat hun startdatum in de toekomst ligt of omdat ze
/// gesnoozed zijn. Zo zie je wat er aankomt zonder filters om te zetten.
///
/// <para>Per taak staat er wanneer hij opduikt en waarom (startdatum, snooze of deadline).
/// Vanuit dit venster kun je een taak meteen naar voren halen ("Nu oppakken") of naar een
/// andere dag verzetten.</para>
/// </summary>
public sealed class AnticipeerForm : Form
{
    private readonly ModernListView _lijst;
    private readonly Label _uitleg;
    private readonly ModernButton _modusKnop;
    private MijnTakenData _data;

    /// <summary>Opent de volwaardige bewerkdialoog van de cockpit (null bij losse start).</summary>
    private readonly Func<MijnTaak, Task>? _bewerk;

    /// <summary>Alle geplande taken tonen in plaats van alleen de eerstvolgende vijf.</summary>
    private bool _allesTonen;

    /// <summary>Hoeveel taken de vooruitblik toont.</summary>
    private const int Aantal = 5;

    public AnticipeerForm(Func<MijnTaak, Task>? bewerk = null)
    {
        _bewerk = bewerk;
        Text = "Wat komt eraan — volgende 5 taken";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 380);
        MinimizeBox = false;

        _data = MijnTaakStore.Load();

        _uitleg = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 8, 10, 0),
            Text = "De eerstvolgende taken, ook de taken die nu nog verborgen zijn " +
                   "(latere startdatum of gesnoozed).",
        };

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Niets in aantocht — de lijst is helemaal bij.",
            LeegSoort = "taken",
            LeegGlyph = Fluent.Checkbox,
        };
        _lijst.Columns.Add("Wanneer", 150);
        _lijst.Columns.Add("Waarom", 110);
        _lijst.Columns.Add("Taak", 330);
        _lijst.Columns.Add("Categorie", 120);
        _lijst.DoubleClick += (_, _) => NuOppakken();

        var menu = new ContextMenuStrip();
        Theme.Style(menu);
        var afvinkItem = new ToolStripMenuItem("Afvinken (klaar)");
        afvinkItem.Click += (_, _) => Afvinken();
        menu.Items.Add(afvinkItem);
        var nuItem = new ToolStripMenuItem("Nu oppakken (meteen tonen)");
        nuItem.Click += (_, _) => NuOppakken();
        menu.Items.Add(nuItem);
        var verzetItem = new ToolStripMenuItem("Verzetten naar…");
        verzetItem.Click += (_, _) => Verzetten();
        menu.Items.Add(verzetItem);
        if (_bewerk is not null)
        {
            var bewerkItem = new ToolStripMenuItem("Taak bewerken…");
            bewerkItem.Click += async (_, _) => await BewerkenAsync();
            menu.Items.Add(bewerkItem);
        }
        _lijst.ContextMenuStrip = menu;

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", Width = 100 };
        sluit.Click += (_, _) => Close();
        // Schakelaar tussen de korte vooruitblik en de volledige lijst met geplande taken
        // (alles met een startdatum in de toekomst of een lopende snooze).
        _modusKnop = new ModernButton { Text = "Alle geplande tonen", Width = 185, Glyph = Fluent.Lijst };
        _modusKnop.Click += (_, _) =>
        {
            _allesTonen = !_allesTonen;
            _modusKnop.Text = _allesTonen ? "Alleen volgende 5" : "Alle geplande tonen";
            Text = _allesTonen
                ? "Geplande taken — startdatum in de toekomst"
                : "Wat komt eraan — volgende 5 taken";
            Vul();
        };
        var afvinkKnop = new ModernButton
        {
            Text = "Afvinken", Width = 120, Glyph = Fluent.Checkbox,
        };
        afvinkKnop.Click += (_, _) => Afvinken();
        var nuKnop = new ModernButton
        {
            Text = "Nu oppakken", Width = 150, Kind = ButtonKind.Accent, Glyph = Fluent.Check,
        };
        nuKnop.Click += (_, _) => NuOppakken();
        var verzetKnop = new ModernButton { Text = "Verzetten…", Width = 130, Glyph = Fluent.Kalender };
        verzetKnop.Click += (_, _) => Verzetten();
        var bewerkKnop = new ModernButton
        {
            Text = "Bewerken…", Width = 125, Glyph = Fluent.Edit, Visible = _bewerk is not null,
        };
        bewerkKnop.Click += async (_, _) => await BewerkenAsync();
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(nuKnop);
        knoppen.Controls.Add(afvinkKnop);
        knoppen.Controls.Add(verzetKnop);
        knoppen.Controls.Add(bewerkKnop);
        knoppen.Controls.Add(_modusKnop);
        CancelButton = sluit;

        Controls.Add(_lijst);
        Controls.Add(_uitleg);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "anticiperen");
        _uitleg.ForeColor = Theme.Muted;
        Vul();
    }

    /// <summary>
    /// Het moment waarop een taak op je bord komt: de dag dat hij weer zichtbaar wordt
    /// (startdatum of snooze — de laatste van de twee), en anders zijn deadline.
    /// Null = geen datum; die taken komen achteraan.
    /// </summary>
    private static (DateOnly? Moment, string Reden) Aankomst(MijnTaak taak)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        DateOnly? start = taak.Startdatum is { } s && s > vandaag ? s : null;
        DateOnly? snooze = taak.SnoozeTot is { } sn && sn > DateTimeOffset.Now
            ? DateOnly.FromDateTime(sn.LocalDateTime)
            : null;
        // Allebei mogelijk: de taak duikt pas op als de laatste van de twee gepasseerd is.
        var verborgenTot = start is null ? snooze : snooze is null ? start
            : start > snooze ? start : snooze;
        if (verborgenTot is { } dag)
        {
            return (dag, snooze is not null && (start is null || snooze >= start)
                ? "snooze loopt af" : "startdatum");
        }
        return taak.Deadline is { } deadline ? (deadline, "deadline") : (null, "geen datum");
    }

    private static string Wanneer(DateOnly? moment)
    {
        if (moment is not { } dag)
        {
            return "geen datum";
        }
        var dagen = dag.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        var omschrijving = dagen switch
        {
            < 0 => "achterstallig",
            0 => "vandaag",
            1 => "morgen",
            < 7 => $"over {dagen} dagen",
            < 14 => "volgende week",
            _ => $"over {dagen} dagen",
        };
        return $"{omschrijving} · {dag:ddd d MMM}";
    }

    private void Vul()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        // Twee modi: de korte vooruitblik (alles wat er als eerste aankomt) of álle taken
        // die nu nog wachten op hun startdatum of op het aflopen van een snooze.
        var basis = _data.Taken
            .Where(t => !t.Klaar && (!_allesTonen || t.NogNietGestart || t.Gesnoozed))
            .Select(t => (Taak: t, Aankomst: Aankomst(t)))
            // Taken met een datum eerst (oplopend), daarna de dateloze op prioriteit.
            .OrderBy(x => x.Aankomst.Moment is null)
            .ThenBy(x => x.Aankomst.Moment ?? DateOnly.MaxValue)
            .ThenBy(x => x.Taak.Prioriteit);
        var kandidaten = (_allesTonen ? basis : basis.Take(Aantal)).ToList();

        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var (taak, aankomst) in kandidaten)
        {
            var item = new ListViewItem(Wanneer(aankomst.Moment))
            {
                Tag = taak, UseItemStyleForSubItems = false,
            };
            // Achterstallig valt op, vandaag/morgen krijgt nadruk, de rest blijft rustig.
            var dagen = aankomst.Moment is { } m ? m.DayNumber - vandaag.DayNumber : int.MaxValue;
            item.SubItems[0].ForeColor = dagen switch
            {
                < 0 => Theme.Danger,
                <= 1 => Theme.Warn,
                _ => Theme.Text,
            };
            var reden = item.SubItems.Add(aankomst.Reden);
            reden.ForeColor = Theme.Muted;
            item.SubItems.Add(taak.Tekst);
            var categorie = item.SubItems.Add(taak.Categorie);
            categorie.ForeColor = Theme.VoorKlant(taak.Categorie);
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
        if (_lijst.Items.Count > 0)
        {
            _lijst.Items[0].Selected = true;
        }
        var verborgen = _data.Taken.Count(t => !t.Klaar && (t.NogNietGestart || t.Gesnoozed));
        _uitleg.Text = _allesTonen
            ? $"Alle {kandidaten.Count} taken die nu nog wachten: startdatum in de toekomst of " +
              "een lopende snooze. Ze staan pas in je gewone lijst vanaf de dag hieronder."
            : verborgen > 0
                ? $"De eerstvolgende taken. {verborgen} {(verborgen == 1 ? "taak staat" : "taken staan")} " +
                  "nu nog verborgen — klik \"Alle geplande tonen\" om ze allemaal te zien."
                : "De eerstvolgende taken. Er staat op dit moment niets in de wacht.";
    }

    private MijnTaak? Geselecteerd() =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as MijnTaak : null;

    /// <summary>Vinkt de geselecteerde taak meteen af (met undo), zonder de cockpit nodig te hebben.</summary>
    private void Afvinken()
    {
        if (Geselecteerd() is not { } taak)
        {
            return;
        }
        taak.Klaar = true;
        taak.KlaarOp = DateTimeOffset.Now;
        Bewaar();
        Toast.ToonUndo(this, $"Afgevinkt: {Kort(taak.Tekst)}", () =>
        {
            taak.Klaar = false;
            taak.KlaarOp = null;
            Bewaar();
        }, Fluent.Check);
    }

    /// <summary>Haalt de taak naar vandaag: startdatum en snooze eraf, zodat hij meteen meedoet.</summary>
    private void NuOppakken()
    {
        if (Geselecteerd() is not { } taak)
        {
            return;
        }
        var oudeStart = taak.Startdatum;
        var oudeSnooze = taak.SnoozeTot;
        if (oudeStart is null && oudeSnooze is null)
        {
            Toast.Toon(this, "Deze taak staat al gewoon in je lijst", Fluent.Checkbox);
            return;
        }
        taak.Startdatum = null;
        taak.SnoozeTot = null;
        Bewaar();
        Toast.ToonUndo(this, $"Staat nu in je lijst: {Kort(taak.Tekst)}", () =>
        {
            taak.Startdatum = oudeStart;
            taak.SnoozeTot = oudeSnooze;
            Bewaar();
        }, Fluent.Check);
    }

    /// <summary>Verzet de startdatum (of, bij een taak zonder start, de deadline).</summary>
    private void Verzetten()
    {
        if (Geselecteerd() is not { } taak)
        {
            return;
        }
        var startdatumTaak = taak.Startdatum is not null || taak.SnoozeTot is not null;
        var huidig = startdatumTaak ? taak.Startdatum : taak.Deadline;

        using var dialog = new Form
        {
            Text = startdatumTaak ? "Startdatum verzetten" : "Deadline verzetten",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(340, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var kiezer = new DatumKiezer
        {
            Waarde = huidig,
            LeegTekst = startdatumTaak ? "meteen zichtbaar" : "geen deadline",
            Location = new Point(16, 18),
            Width = 200,
        };
        var ok = new ModernButton
        {
            Text = "Verzetten", Width = 115, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(196, 70),
        };
        var annuleer = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(86, 70),
        };
        dialog.Controls.AddRange(new Control[] { kiezer, ok, annuleer });
        dialog.AcceptButton = ok;
        dialog.CancelButton = annuleer;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        if (startdatumTaak)
        {
            taak.Startdatum = kiezer.Waarde;
            taak.SnoozeTot = null; // de gekozen dag is nu leidend
        }
        else
        {
            taak.Deadline = kiezer.Waarde;
        }
        Bewaar();
        Toast.Toon(this, $"Verzet: {Kort(taak.Tekst)}", Fluent.Kalender);
    }

    /// <summary>Opent de volwaardige bewerkdialoog van de cockpit voor de geselecteerde taak.</summary>
    private async Task BewerkenAsync()
    {
        if (_bewerk is null || Geselecteerd() is not { } taak)
        {
            return;
        }
        await _bewerk(taak);
        // De dialoog schrijft zelf naar de store: vers laden zodat de lijst het meteen toont.
        _data = MijnTaakStore.Load();
        Vul();
    }

    private static string Kort(string tekst) => tekst.Length > 45 ? tekst[..45] + "…" : tekst;

    private void Bewaar()
    {
        // Vers laden en de wijziging op het bewaarde exemplaar zetten: het takenbestand kan
        // intussen door een ander venster geschreven zijn.
        var opSchijf = MijnTaakStore.Load();
        foreach (var taak in _data.Taken)
        {
            if (opSchijf.Taken.FirstOrDefault(t => t.Id == taak.Id) is { } doel)
            {
                doel.Startdatum = taak.Startdatum;
                doel.SnoozeTot = taak.SnoozeTot;
                doel.Deadline = taak.Deadline;
                doel.Klaar = taak.Klaar;
                doel.KlaarOp = taak.KlaarOp;
            }
        }
        MijnTaakStore.Save(opSchijf);
        _data = opSchijf;
        Vul();
    }
}
