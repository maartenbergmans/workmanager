using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Venster voor de persoonlijke takenlijst: per categorie een groep met taken die af te
/// vinken zijn. Snel toevoegen met "!" voor hoge prioriteit en "@morgen" / "@vr" /
/// "@28-07" voor een deadline. Taken sorteren automatisch op prioriteit en deadline;
/// te late taken kleuren amber. Alles wordt meteen bewaard in my-tasks.json.
/// </summary>
public class MijnTakenForm : Form
{
    private MijnTakenData _data;
    private DateTime _geladenOp = MijnTaakStore.BestandTijd();
    private bool _labelBewerking;
    private AsanaSettings _asana = AsanaSettings.Load();
    private readonly List<AsanaClient.AsanaTaak> _asanaTaken = new();
    private bool _asanaLaden;
    private AgendaSettings _agenda = AgendaSettings.Load();
    private readonly List<AgendaClient.AgendaItem> _agendaItems = new();
    private bool _agendaLaden;
    private int _wekkerTeller;
    private readonly PulseBar _pulse = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ModernListView _list;
    private readonly ComboBox _categorieCombo;
    private readonly TextBox _nieuweTaak;
    private readonly TextBox _zoek;
    private readonly CheckBox _toonGesnoozed;
    private readonly Label _status;
    private readonly Font _klaarFont;
    private readonly System.Windows.Forms.Timer _wekker = new() { Interval = 60_000 };
    private bool _loading;

    public MijnTakenForm()
    {
        Text = "Mijn taken";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 740);

        _data = MijnTaakStore.Load();
        _klaarFont = new Font(Font, FontStyle.Strikeout);

        // Werkbalk
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _categorieCombo = new ComboBox
        {
            Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3),
        };
        _nieuweTaak = new TextBox
        {
            Width = 300,
            PlaceholderText = "Nieuwe taak…   (! = hoog, @morgen of @28-07 = deadline)",
            Margin = new Padding(3, 5, 3, 3),
        };
        _nieuweTaak.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                TaakToevoegen();
            }
        };
        var addButton = new ModernButton
        {
            Text = "Toevoegen", Width = 115, Kind = ButtonKind.Accent, Glyph = Fluent.Add,
        };
        addButton.Click += (_, _) => TaakToevoegen();
        var claudeButton = new ModernButton { Text = "Uit tekst (Claude)…", Width = 165, Glyph = Fluent.Ster };
        claudeButton.Click += (_, _) => TakenUitTekst();

        // Beheer-acties in één dropdown-knop om de werkbalk compact te houden.
        var beheerMenu = new ContextMenuStrip();
        Theme.Style(beheerMenu);
        var opruimItem = new ToolStripMenuItem("Afgevinkte opruimen");
        opruimItem.Click += (_, _) => AfgevinkteOpruimen();
        beheerMenu.Items.Add(opruimItem);
        var categorieItem = new ToolStripMenuItem("Categorieën…");
        categorieItem.Click += (_, _) => CategorieenBeheren();
        beheerMenu.Items.Add(categorieItem);
        beheerMenu.Items.Add(new ToolStripSeparator());
        var asanaKoppelItem = new ToolStripMenuItem("Asana-koppeling…");
        asanaKoppelItem.Click += async (_, _) =>
        {
            using var form = new AsanaSettingsForm();
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                await AsanaVernieuwenAsync();
            }
        };
        beheerMenu.Items.Add(asanaKoppelItem);
        var agendaKoppelItem = new ToolStripMenuItem("Agenda-koppeling (Google)…");
        agendaKoppelItem.Click += async (_, _) =>
        {
            using var form = new AgendaSettingsForm();
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                await AgendaVernieuwenAsync();
            }
        };
        beheerMenu.Items.Add(agendaKoppelItem);
        var vernieuwItem = new ToolStripMenuItem("Koppelingen vernieuwen");
        vernieuwItem.Click += async (_, _) =>
        {
            var agendaTaak = AgendaVernieuwenAsync();
            await AsanaVernieuwenAsync();
            await agendaTaak;
        };
        beheerMenu.Items.Add(vernieuwItem);
        beheerMenu.Opening += (_, _) => vernieuwItem.Enabled =
            AsanaSettings.Load().Compleet || AgendaSettings.Load().Compleet;
        var beheerButton = new ModernButton { Text = "Beheren", Width = 110, Glyph = Fluent.Settings };
        beheerButton.Click += (_, _) => beheerMenu.Show(beheerButton, new Point(0, beheerButton.Height + 4));
        _zoek = new TextBox { Width = 140, PlaceholderText = "Zoeken…", Margin = new Padding(12, 5, 3, 3) };
        _zoek.TextChanged += (_, _) => VulLijst();
        _zoek.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _zoek.Clear();
            }
        };
        _toonGesnoozed = new CheckBox
        {
            Text = "Gesnoozed/gepland tonen", AutoSize = true, Margin = new Padding(8, 8, 3, 3),
        };
        _toonGesnoozed.CheckedChanged += (_, _) => VulLijst();
        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[]
        {
            _categorieCombo, _nieuweTaak, addButton, claudeButton, beheerButton,
            _zoek, _toonGesnoozed, _status,
        });

        // Takenlijst: één groep per categorie
        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LabelEdit = true,
            LegeTekst = "Nog geen taken — typ hierboven je eerste taak.",
            LeegSoort = "taken",
            LeegGlyph = Fluent.Checkbox,
            // Volledige omschrijving bij het zweven over een rij die niet past.
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Taak", 640);
        _list.Columns.Add("Prio", 70);
        _list.Columns.Add("Deadline", 150);
        _list.Resize += (_, _) =>
            _list.Columns[0].Width = Math.Max(300, _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - 4);
        _list.SterrenKolom = 1;
        _list.SterGeklikt += (item, aantal) =>
        {
            if (item.Tag is not MijnTaak taak || taak.Klaar)
            {
                return;
            }
            taak.Prioriteit = 3 - aantal; // 3 sterren = hoog (0), 1 ster = laag (2)
            Bewaar();
            VulLijst(taak.Id); // hersorteert meteen op de nieuwe prioriteit
        };
        _list.HeeftCheckbox = item => item.Tag is not AgendaClient.AgendaItem;
        _list.ItemCheck += (_, e) =>
        {
            // Agenda-afspraken zijn niet afvinkbaar.
            if (!_loading && _list.Items[e.Index].Tag is AgendaClient.AgendaItem)
            {
                e.NewValue = e.CurrentValue;
            }
        };
        _list.ItemChecked += (_, e) =>
        {
            if (_loading)
            {
                return;
            }
            if (e.Item.Tag is AsanaClient.AsanaTaak asanaTaak)
            {
                if (e.Item.Checked)
                {
                    _ = AsanaVoltooiAsync(e.Item, asanaTaak);
                }
                return;
            }
            if (e.Item.Tag is not MijnTaak taak)
            {
                return;
            }
            taak.Klaar = e.Item.Checked;
            taak.KlaarOp = taak.Klaar ? DateTimeOffset.Now : null;
            Bewaar();
            MaakItemOp(e.Item, taak);
            UpdateStatus();
            if (taak.Klaar && _data.Taken.Count > 0 && _data.Taken.All(t => t.Klaar))
            {
                Confetti.Vier(this);
                Toast.Toon(this, $"Alles afgevinkt! {ThemaStem.Gevierd()}", Fluent.Check);
            }
        };
        _list.BeforeLabelEdit += (_, e) =>
        {
            // Asana-taken zijn hier alleen-lezen; bewerken gebeurt in Asana zelf.
            if (_list.Items[e.Item].Tag is not MijnTaak)
            {
                e.CancelEdit = true;
                return;
            }
            _labelBewerking = true;
        };
        _list.MouseDoubleClick += (_, e) =>
        {
            var geraakt = _list.HitTest(e.Location).Item;
            if (geraakt is { Tag: AsanaClient.AsanaTaak asanaTaak } && asanaTaak.Url.Length > 0)
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(asanaTaak.Url) { UseShellExecute = true });
                }
                catch
                {
                    // Geen standaardbrowser gevonden; klik stilletjes negeren.
                }
            }
            else if (geraakt is { Tag: MijnTaak })
            {
                geraakt.BeginEdit();
            }
        };
        _list.AfterLabelEdit += (_, e) =>
        {
            _labelBewerking = false;
            if (e.Label is null || _list.Items[e.Item].Tag is not MijnTaak taak)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(e.Label))
            {
                e.CancelEdit = true;
                return;
            }
            taak.Tekst = e.Label.Trim();
            Bewaar();
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                SelectieVerwijderen();
            }
            else if (e.KeyCode == Keys.F2 && _list.SelectedItems.Count > 0)
            {
                _list.SelectedItems[0].BeginEdit();
            }
        };

        _list.ContextMenuStrip = BouwContextMenu();

        Controls.Add(_list);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        Shown += async (_, _) =>
        {
            var agendaTaak = AgendaVernieuwenAsync();
            await AsanaVernieuwenAsync();
            await agendaTaak;
        };
        FormClosed += (_, _) => _cts.Cancel();

        // Gesnoozde taken automatisch terug laten verschijnen zodra hun tijd verstrijkt;
        // de koppelingen (Asana/agenda) elk kwartier stil vernieuwen.
        _wekker.Tick += (_, _) =>
        {
            if (++_wekkerTeller % 15 == 0)
            {
                _ = AsanaVernieuwenAsync();
                _ = AgendaVernieuwenAsync();
            }
            var ontwaakt = _data.Taken
                .Where(t => !t.Klaar && t.SnoozeTot is { } tot && tot <= DateTimeOffset.Now)
                .ToList();
            if (ontwaakt.Count > 0)
            {
                foreach (var taak in ontwaakt)
                {
                    taak.SnoozeTot = null;
                }
                Bewaar();
                VulLijst();
                Toast.Toon(this, ontwaakt.Count == 1
                    ? $"Snooze afgelopen: {ontwaakt[0].Tekst}"
                    : $"{ontwaakt.Count} snoozes afgelopen", Fluent.Klok);
            }
        };
        _wekker.Start();
        FormClosed += (_, _) => _wekker.Dispose();

        // Vensters zoals "Mail beantwoorden" kunnen taken toevoegen terwijl dit venster
        // open staat; bij terugkeer naar dit venster de lijst verversen als het bestand
        // intussen gewijzigd is.
        Activated += (_, _) =>
        {
            if (!_labelBewerking && MijnTaakStore.BestandTijd() > _geladenOp)
            {
                _data = MijnTaakStore.Load();
                _geladenOp = MijnTaakStore.BestandTijd();
                VulCategorieCombo();
                VulLijst();
            }
        };

        Theme.Apply(this);
        VensterGeheugen.Volg(this, "mijn-taken");
        VulCategorieCombo();
        VulLijst();
    }

    /// <summary>Bewaart en onthoudt de bestandtijd, zodat de eigen save geen herlaad-lus triggert.</summary>
    private void Bewaar()
    {
        MijnTaakStore.Save(_data);
        _geladenOp = MijnTaakStore.BestandTijd();
    }

    // ---------- Contextmenu ----------

    private ContextMenuStrip BouwContextMenu()
    {
        var menu = new ContextMenuStrip();
        Theme.Style(menu);

        var asanaOpenItem = new ToolStripMenuItem("In Asana openen");
        asanaOpenItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 &&
                _list.SelectedItems[0].Tag is AsanaClient.AsanaTaak { Url.Length: > 0 } asanaTaak)
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(asanaTaak.Url) { UseShellExecute = true });
                }
                catch
                {
                    // Geen standaardbrowser gevonden.
                }
            }
        };
        menu.Items.Add(asanaOpenItem);

        var bewerkItem = new ToolStripMenuItem("Bewerken\tF2");
        bewerkItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0)
            {
                _list.SelectedItems[0].BeginEdit();
            }
        };
        menu.Items.Add(bewerkItem);

        // De bron: het bericht, bestand, de map of de webpagina waar de taak vandaan komt.
        // Zelfde venster en zelfde regels als in de cockpit (zie TaakBron).
        var bronItem = new ToolStripMenuItem("Bron…");
        bronItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is MijnTaak taak &&
                TaakBron.Bewerk(this, taak))
            {
                Bewaar();
                VulLijst(taak.Id);
            }
        };
        menu.Items.Add(bronItem);
        var bronOpenItem = new ToolStripMenuItem("Bron openen");
        bronOpenItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is MijnTaak { Mail.Link: { } link })
            {
                TaakBron.Open(link);
            }
        };
        menu.Items.Add(bronOpenItem);

        var prioItem = new ToolStripMenuItem("Prioriteit");
        foreach (var (naam, waarde) in new[] { ("★★★   hoog", 0), ("★★   normaal", 1), ("★   laag", 2) })
        {
            var keuze = new ToolStripMenuItem(naam) { Tag = waarde };
            keuze.Click += (_, _) => SelectieAanpassen(t => t.Prioriteit = waarde);
            prioItem.DropDownItems.Add(keuze);
        }
        menu.Items.Add(prioItem);

        // Eerst "vanaf" (startdatum), dan "uiterlijk" (deadline) — dezelfde volgorde en
        // woordkeuze als in de bewerkdialoog, zodat de twee datums niet door elkaar lopen.
        var startItem = new ToolStripMenuItem("Vanaf (startdatum)");
        var startVandaag = new ToolStripMenuItem("Vandaag");
        startVandaag.Click += (_, _) => SelectieAanpassen(t => t.Startdatum = Vandaag());
        startItem.DropDownItems.Add(startVandaag);
        var startMorgen = new ToolStripMenuItem("Morgen");
        startMorgen.Click += (_, _) => SelectieAanpassen(t => t.Startdatum = Vandaag().AddDays(1));
        startItem.DropDownItems.Add(startMorgen);
        var startWeek = new ToolStripMenuItem("Volgende week maandag");
        startWeek.Click += (_, _) => SelectieAanpassen(t => t.Startdatum = VolgendeMaandag());
        startItem.DropDownItems.Add(startWeek);
        var startKies = new ToolStripMenuItem("Kiezen…");
        startKies.Click += (_, _) =>
        {
            var huidig = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is MijnTaak t
                ? t.Startdatum : null;
            if (VraagDatum(huidig, "Startdatum kiezen") is { } gekozen)
            {
                SelectieAanpassen(taak => taak.Startdatum = gekozen.Datum);
            }
        };
        startItem.DropDownItems.Add(startKies);
        startItem.DropDownItems.Add(new ToolStripSeparator());
        var startGeen = new ToolStripMenuItem("Geen startdatum");
        startGeen.Click += (_, _) => SelectieAanpassen(t => t.Startdatum = null);
        startItem.DropDownItems.Add(startGeen);
        menu.Items.Add(startItem);

        var deadlineItem = new ToolStripMenuItem("Uiterlijk (deadline)");
        var vandaagKeuze = new ToolStripMenuItem("Vandaag");
        vandaagKeuze.Click += (_, _) => SelectieAanpassen(t => t.Deadline = Vandaag());
        deadlineItem.DropDownItems.Add(vandaagKeuze);
        var morgenKeuze = new ToolStripMenuItem("Morgen");
        morgenKeuze.Click += (_, _) => SelectieAanpassen(t => t.Deadline = Vandaag().AddDays(1));
        deadlineItem.DropDownItems.Add(morgenKeuze);
        var weekKeuze = new ToolStripMenuItem("Volgende week maandag");
        weekKeuze.Click += (_, _) => SelectieAanpassen(t => t.Deadline = VolgendeMaandag());
        deadlineItem.DropDownItems.Add(weekKeuze);
        // Datum overnemen van het andere veld: de meest gevraagde "dezelfde dag"-actie.
        var zelfdeAlsStart = new ToolStripMenuItem("Zelfde als startdatum");
        zelfdeAlsStart.Click += (_, _) =>
            SelectieAanpassen(t => t.Deadline = t.Startdatum ?? t.Deadline);
        deadlineItem.DropDownItems.Add(zelfdeAlsStart);
        var kiesKeuze = new ToolStripMenuItem("Kiezen…");
        kiesKeuze.Click += (_, _) =>
        {
            var taak = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MijnTaak : null;
            if (VraagDatum(taak?.Deadline, "Deadline kiezen", taak?.Startdatum) is { } gekozen)
            {
                SelectieAanpassen(t => t.Deadline = gekozen.Datum);
            }
        };
        deadlineItem.DropDownItems.Add(kiesKeuze);
        deadlineItem.DropDownItems.Add(new ToolStripSeparator());
        var geenKeuze = new ToolStripMenuItem("Geen deadline");
        geenKeuze.Click += (_, _) => SelectieAanpassen(t => t.Deadline = null);
        deadlineItem.DropDownItems.Add(geenKeuze);
        deadlineItem.DropDownOpening += (_, _) =>
            zelfdeAlsStart.Enabled = _list.SelectedItems.Cast<ListViewItem>()
                .Any(i => i.Tag is MijnTaak { Startdatum: not null });
        menu.Items.Add(deadlineItem);

        var verplaatsItem = new ToolStripMenuItem("Verplaatsen naar");
        menu.Items.Add(verplaatsItem);

        // Snoozen met één-klik-presets (zelfde momenten als in de cockpit); de dialoog
        // alleen nog voor een afwijkende datum.
        var snoozeItem = new ToolStripMenuItem("Snoozen");
        var snoozeVanavond = new ToolStripMenuItem("Vanavond (18:00)");
        snoozeVanavond.Click += (_, _) => SelectieSnoozen(VandaagOm(18));
        snoozeItem.DropDownItems.Add(snoozeVanavond);
        var snoozeMorgen = new ToolStripMenuItem("Morgenvroeg (08:00)");
        snoozeMorgen.Click += (_, _) => SelectieSnoozen(VandaagOm(8).AddDays(1));
        snoozeItem.DropDownItems.Add(snoozeMorgen);
        var snoozeMaandag = new ToolStripMenuItem("Maandag (08:00)");
        snoozeMaandag.Click += (_, _) => SelectieSnoozen(
            new DateTimeOffset(VolgendeMaandag().ToDateTime(new TimeOnly(8, 0))));
        snoozeItem.DropDownItems.Add(snoozeMaandag);
        snoozeItem.DropDownItems.Add(new ToolStripSeparator());
        var snoozeKies = new ToolStripMenuItem("Kies datum…");
        snoozeKies.Click += (_, _) => SelectieSnoozen();
        snoozeItem.DropDownItems.Add(snoozeKies);
        menu.Items.Add(snoozeItem);
        var ontsnoozeItem = new ToolStripMenuItem("Snooze opheffen");
        ontsnoozeItem.Click += (_, _) => SelectieAanpassen(t => t.SnoozeTot = null);
        menu.Items.Add(ontsnoozeItem);

        menu.Items.Add(new ToolStripSeparator());
        var verwijderItem = new ToolStripMenuItem("Verwijderen\tDel");
        verwijderItem.Click += (_, _) => SelectieVerwijderen();
        menu.Items.Add(verwijderItem);

        menu.Opening += (_, e) =>
        {
            if (_list.SelectedItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            var selectie = _list.SelectedItems.Cast<ListViewItem>().ToList();
            var alleenEigen = selectie.All(i => i.Tag is MijnTaak);
            asanaOpenItem.Visible = selectie.Count == 1 && selectie[0].Tag is AsanaClient.AsanaTaak;
            foreach (var eigen in new ToolStripMenuItem[]
                     {
                         bewerkItem, prioItem, deadlineItem, startItem, verplaatsItem, snoozeItem, verwijderItem,
                     })
            {
                eigen.Enabled = alleenEigen;
            }
            ontsnoozeItem.Visible = selectie.Any(i => i.Tag is MijnTaak { Gesnoozed: true });
            // Bron hoort bij één taak tegelijk; openen alleen als er ook echt een link is.
            bronItem.Enabled = selectie.Count == 1 && alleenEigen;
            bronOpenItem.Visible = selectie.Count == 1 &&
                selectie[0].Tag is MijnTaak { Mail.Link.Length: > 0 };
            verplaatsItem.DropDownItems.Clear();
            foreach (var categorie in AlleCategorieen())
            {
                var doel = new ToolStripMenuItem(categorie);
                doel.Click += (_, _) => SelectieAanpassen(t => t.Categorie = categorie);
                verplaatsItem.DropDownItems.Add(doel);
            }
        };
        return menu;
    }

    private static DateTimeOffset VandaagOm(int uur)
    {
        var nu = DateTimeOffset.Now;
        return new DateTimeOffset(nu.Year, nu.Month, nu.Day, uur, 0, 0, nu.Offset);
    }

    /// <summary>
    /// Snoozet de geselecteerde taken: tijdelijk uit de lijst tot het gekozen moment.
    /// Met <paramref name="preset"/> zonder dialoog (één-klik uit het menu), en altijd
    /// met een "Ongedaan maken" in de toast.
    /// </summary>
    private void SelectieSnoozen(DateTimeOffset? preset = null)
    {
        var taken = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MijnTaak>().Where(t => !t.Klaar).ToList();
        if (taken.Count == 0)
        {
            return;
        }

        DateTimeOffset gekozen;
        if (preset is { } p)
        {
            gekozen = p;
        }
        else
        {
            using var dialog = new SnoozeForm(taken.Count, VandaagOm(8).AddDays(1), "Taak", "taken");
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            gekozen = dialog.Gekozen;
        }

        var vorige = taken.Select(t => (Taak: t, Oud: t.SnoozeTot)).ToList();
        foreach (var taak in taken)
        {
            taak.SnoozeTot = gekozen;
        }
        Bewaar();
        VulLijst();
        Toast.ToonUndo(this, $"Gesnoozed tot {gekozen:ddd d MMM HH:mm}", () =>
        {
            foreach (var (taak, oud) in vorige)
            {
                taak.SnoozeTot = oud;
            }
            Bewaar();
            VulLijst();
        }, Fluent.Klok);
    }

    // ---------- Lijst vullen ----------

    private List<string> AlleCategorieen() =>
        _data.Categorieen
            .Concat(_data.Taken.Select(t => t.Categorie))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void VulCategorieCombo()
    {
        var huidig = _categorieCombo.SelectedItem as string;
        _categorieCombo.Items.Clear();
        foreach (var categorie in AlleCategorieen())
        {
            _categorieCombo.Items.Add(categorie);
        }
        if (_categorieCombo.Items.Count > 0)
        {
            var index = huidig is null ? 0 : _categorieCombo.Items.IndexOf(huidig);
            _categorieCombo.SelectedIndex = index < 0 ? 0 : index;
        }
    }

    private void VulLijst(Guid? selecteer = null)
    {
        _loading = true;
        var filter = _zoek.Text.Trim();
        // Lege lijst: bij een filter zakelijk, anders in de toon van het kleurenschema —
        // maar alleen als er écht niets meer openstaat (anders is "typ je eerste taak" beter).
        _list.LegeTekst = filter.Length > 0
            ? "Geen taken gevonden voor je zoekopdracht."
            : _data.Taken.Count > 0 && _data.Taken.All(t => t.Klaar)
                ? ThemaStem.GeenTaken()
                : "Nog geen taken — typ hierboven je eerste taak.";
        bool Zichtbaar(MijnTaak t) =>
            (_toonGesnoozed.Checked || (!t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt)) &&
            (filter.Length == 0 ||
             t.Tekst.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
             t.Categorie.Contains(filter, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();

        // Agenda van vandaag en morgen bovenaan (alleen-lezen, geen checkbox).
        var agendaZichtbaar = _agendaItems
            .Where(a => filter.Length == 0 || a.Titel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (agendaZichtbaar.Count > 0)
        {
            var vandaagAantal = agendaZichtbaar.Count(a => a.Start.Date <= DateTime.Now.Date);
            var agendaGroep = new ListViewGroup($"Agenda  ({vandaagAantal} vandaag)") { Tag = "agenda" };
            _list.Groups.Add(agendaGroep);
            foreach (var afspraak in agendaZichtbaar)
            {
                var item = new ListViewItem(afspraak.Titel, agendaGroep)
                {
                    Tag = afspraak, UseItemStyleForSubItems = false,
                };
                item.SubItems.Add("");
                var wanneer = item.SubItems.Add("");
                (wanneer.Text, wanneer.ForeColor) = AfspraakWeergave(afspraak);
                _list.Items.Add(item);
            }
        }

        foreach (var categorie in AlleCategorieen())
        {
            var taken = _data.Taken
                .Where(t => string.Equals(t.Categorie, categorie, StringComparison.OrdinalIgnoreCase))
                .Where(Zichtbaar)
                .OrderBy(t => t.Klaar)
                .ThenBy(t => t.Prioriteit)
                .ThenBy(t => t.Deadline ?? DateOnly.MaxValue)
                .ThenBy(t => t.AangemaaktOp)
                .ToList();
            if (taken.Count == 0 && (filter.Length > 0 || !_data.Categorieen.Contains(categorie)))
            {
                continue;
            }

            var open = taken.Count(t => !t.Klaar);
            var group = new ListViewGroup($"{categorie}  ({open} open)") { Tag = categorie };
            _list.Groups.Add(group);

            foreach (var taak in taken)
            {
                var item = new ListViewItem(group) { Tag = taak, Checked = taak.Klaar };
                ZetTaakTekst(item, taak.Tekst);
                item.SubItems.Add("");
                item.SubItems.Add("");
                MaakItemOp(item, taak);
                _list.Items.Add(item);
                if (taak.Id == selecteer)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }
        }

        // Asana-taken als aparte groep onderaan (alleen-lezen; afvinken = voltooien in Asana).
        var asanaZichtbaar = _asanaTaken
            .Where(t => filter.Length == 0 || t.Naam.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Deadline ?? DateOnly.MaxValue)
            .ToList();
        if (asanaZichtbaar.Count > 0)
        {
            var groep = new ListViewGroup($"Asana – {_asana.WorkspaceNaam}  ({asanaZichtbaar.Count} open)")
            {
                Tag = "asana",
            };
            _list.Groups.Add(groep);
            foreach (var asanaTaak in asanaZichtbaar)
            {
                var item = new ListViewItem(asanaTaak.Naam, groep)
                {
                    Tag = asanaTaak, UseItemStyleForSubItems = false,
                };
                item.SubItems.Add("");
                var deadline = item.SubItems.Add("");
                if (asanaTaak.Deadline is { } d)
                {
                    (deadline.Text, deadline.ForeColor) = DeadlineWeergave(d);
                }
                _list.Items.Add(item);
            }
        }

        _list.EndUpdate();
        _loading = false;
        UpdateStatus();
    }

    /// <summary>Weergavetekst en kleur voor een agenda-afspraak (vandaag/morgen).</summary>
    private static (string Tekst, Color Kleur) AfspraakWeergave(AgendaClient.AgendaItem afspraak)
    {
        var nu = DateTime.Now;
        var vandaag = afspraak.Start.Date <= nu.Date && afspraak.Einde.DateTime > nu.Date;
        var bezigNu = !afspraak.HeleDag && afspraak.Start <= nu && nu < afspraak.Einde;
        if (afspraak.HeleDag)
        {
            return (vandaag ? "vandaag" : "morgen", vandaag ? Theme.AccentHover : Theme.Muted);
        }
        var tijd = $"{afspraak.Start:HH:mm}–{afspraak.Einde:HH:mm}";
        if (bezigNu)
        {
            return ($"nu · {tijd}", Theme.Success);
        }
        return vandaag ? (tijd, Theme.AccentHover) : ($"morgen {afspraak.Start:HH:mm}", Theme.Muted);
    }

    /// <summary>Haalt de afspraken van vandaag en morgen opnieuw op (stil zonder koppeling).</summary>
    private async Task AgendaVernieuwenAsync()
    {
        _agenda = AgendaSettings.Load();
        if (!_agenda.Compleet)
        {
            if (_agendaItems.Count > 0)
            {
                _agendaItems.Clear();
                VulLijst();
            }
            return;
        }
        if (_agendaLaden)
        {
            return;
        }

        _agendaLaden = true;
        _pulse.Actief = true;
        try
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var items = await AgendaClient.OphalenAsync(_agenda.Urls, vandaag, vandaag.AddDays(1), _cts.Token);
            _agendaItems.Clear();
            _agendaItems.AddRange(items);
            VulLijst();
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Agenda: {ex.Message}", Fluent.Kalender);
        }
        finally
        {
            _agendaLaden = false;
            _pulse.Actief = false;
        }
    }

    // ---------- Asana ----------

    /// <summary>Haalt de open Asana-taken opnieuw op (stil als er geen koppeling is).</summary>
    private async Task AsanaVernieuwenAsync()
    {
        _asana = AsanaSettings.Load();
        if (!_asana.Compleet)
        {
            if (_asanaTaken.Count > 0)
            {
                _asanaTaken.Clear();
                VulLijst();
            }
            return;
        }
        if (_asanaLaden)
        {
            return;
        }

        _asanaLaden = true;
        _pulse.Actief = true;
        try
        {
            var taken = await AsanaClient.OpenTakenAsync(_asana, _cts.Token);
            _asanaTaken.Clear();
            _asanaTaken.AddRange(taken);
            VulLijst();
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het ophalen.
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Asana: {ex.Message}", Fluent.Globe);
        }
        finally
        {
            _asanaLaden = false;
            _pulse.Actief = false;
            UpdateStatus();
        }
    }

    /// <summary>Zet een aangevinkte Asana-taak in Asana op voltooid; bij een fout gaat het vinkje terug.</summary>
    private async Task AsanaVoltooiAsync(ListViewItem item, AsanaClient.AsanaTaak taak)
    {
        _pulse.Actief = true;
        try
        {
            await AsanaClient.VoltooiAsync(_asana, taak.Gid, _cts.Token);
            _asanaTaken.RemoveAll(t => t.Gid == taak.Gid);
            VulLijst();
            Toast.Toon(this, $"Voltooid in Asana: {taak.Naam}", Fluent.Check);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !item.ListView!.IsDisposed)
            {
                _loading = true;
                item.Checked = false;
                _loading = false;
            }
            Toast.Toon(this, $"Voltooien in Asana mislukt: {ex.Message}", Fluent.Globe);
        }
        finally
        {
            _pulse.Actief = false;
        }
    }

    /// <summary>Zet tekst, kleuren en doorstreping van een rij volgens de taakstatus.</summary>
    /// <summary>
    /// De taaktekst op een rij: meerdere regels worden met " · " samengevoegd, verder niets
    /// afgekapt — de lijst ellipsiseert zelf op kolombreedte. De volledige tekst zit in de
    /// tooltip zodra er iets wegvalt. Zelfde gedrag als in de cockpit.
    /// </summary>
    private void ZetTaakTekst(ListViewItem item, string tekst)
    {
        var regels = tekst.ReplaceLineEndings("\n").Split('\n')
            .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        item.Text = string.Join(" · ", regels);
        var breedte = _list.Columns.Count > 0 ? _list.Columns[0].Width : 0;
        var nodig = TextRenderer.MeasureText(item.Text, _list.Font).Width + 28;
        item.ToolTipText = breedte > 0 && nodig > breedte ? tekst.Trim() : "";
    }

    private void MaakItemOp(ListViewItem item, MijnTaak taak)
    {
        item.UseItemStyleForSubItems = false;
        item.Font = taak.Klaar ? _klaarFont : _list.Font;
        item.SubItems[0].ForeColor = taak.Klaar ? Theme.Muted : Theme.Text;
        item.SubItems[0].Font = item.Font;

        var prio = item.SubItems[1];
        (prio.Text, prio.ForeColor) = taak.Klaar ? ("", Theme.Muted) : Theme.PrioSterren(taak.Prioriteit);

        var deadline = item.SubItems[2];
        if (taak.Gesnoozed && taak.SnoozeTot is { } tot)
        {
            // Alleen zichtbaar met de toggle aan; de hele rij gedempt.
            deadline.Text = $"tot {tot:ddd d MMM HH:mm}";
            deadline.ForeColor = Theme.Muted;
            item.SubItems[0].ForeColor = Theme.Muted;
        }
        else if (taak.NogNietGestart && taak.Startdatum is { } start)
        {
            // Nog niet gestart: alleen zichtbaar met de toggle aan; gedempt tonen wanneer hij begint.
            deadline.Text = $"vanaf {start:ddd d MMM}";
            deadline.ForeColor = Theme.Muted;
            item.SubItems[0].ForeColor = Theme.Muted;
        }
        else if (taak.NogNietAanDeBeurt && taak.StartUur is { } uur)
        {
            // Startuur vandaag nog niet bereikt: alleen zichtbaar met de toggle aan.
            deadline.Text = $"vanaf ⏰{uur:HH\\:mm}";
            deadline.ForeColor = Theme.Muted;
            item.SubItems[0].ForeColor = Theme.Muted;
        }
        else if (taak.Deadline is not { } d || taak.Klaar)
        {
            deadline.Text = "";
        }
        else
        {
            (deadline.Text, deadline.ForeColor) = DeadlineWeergave(d);
        }
    }

    private static (string Tekst, Color Kleur) DeadlineWeergave(DateOnly d)
    {
        var vandaag = Vandaag();
        if (d < vandaag)
        {
            return ($"{d:dd-MM} · te laat", Theme.Warn);
        }
        if (d == vandaag)
        {
            return ("vandaag", Theme.AccentHover);
        }
        if (d == vandaag.AddDays(1))
        {
            return ("morgen", Theme.Text);
        }
        var cultuur = CultureInfo.GetCultureInfo("nl-BE");
        return (d.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM", cultuur), Theme.Muted);
    }

    private void UpdateStatus()
    {
        var open = _data.Taken.Count(t =>
            !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt);
        var aandacht = _data.Taken.Count(t =>
            !t.Klaar && !t.Gesnoozed && !t.NogNietGestart && !t.NogNietAanDeBeurt &&
            t.Deadline is { } d && d <= Vandaag());
        var gesnoozed = _data.Taken.Count(t => t.Gesnoozed);
        var gepland = _data.Taken.Count(t => t.NogNietGestart || t.NogNietAanDeBeurt);
        var klaar = _data.Taken.Count(t => t.Klaar);
        _status.Text = $"{open} open" +
                       (aandacht > 0 ? $" · {aandacht} voor vandaag" : "") +
                       (gepland > 0 ? $" · {gepland} gepland" : "") +
                       (gesnoozed > 0 ? $" · {gesnoozed} gesnoozed" : "") +
                       $" · {klaar} afgevinkt" +
                       (_asanaTaken.Count > 0 ? $" · {_asanaTaken.Count} Asana" : "");
    }

    // ---------- Acties ----------

    private void TaakToevoegen()
    {
        var invoer = _nieuweTaak.Text.Trim();
        if (invoer.Length == 0 || _categorieCombo.SelectedItem is not string categorie)
        {
            return;
        }

        var (tekst, prio, deadline) = ParseSnel(invoer);
        if (tekst.Length == 0)
        {
            return;
        }
        var taak = new MijnTaak { Tekst = tekst, Categorie = categorie, Prioriteit = prio, Deadline = deadline };
        _data.Taken.Add(taak);
        Bewaar();
        _nieuweTaak.Clear();
        _nieuweTaak.Focus();
        VulLijst(taak.Id);

        // Feedback dat de snelle invoer (! en @…) herkend werd.
        if (prio == 0 || deadline is not null)
        {
            var delen = new List<string>();
            if (prio == 0)
            {
                delen.Add("hoge prioriteit");
            }
            if (deadline is { } d)
            {
                delen.Add($"deadline {DeadlineWeergave(d).Tekst}");
            }
            Toast.Toon(this, $"Toegevoegd met {string.Join(" en ", delen)}", Fluent.Check);
        }
    }

    /// <summary>
    /// Snelle invoer: "!" vooraan = hoge prioriteit; "@…" achteraan = deadline
    /// (vandaag/morgen/overmorgen, weekdag zoals "vr" of "vrijdag", of dd-MM[-jjjj]).
    /// </summary>
    internal static (string Tekst, int Prio, DateOnly? Deadline) ParseSnel(string invoer)
    {
        var tekst = invoer.Trim();
        var prio = 1;
        if (tekst.StartsWith('!'))
        {
            prio = 0;
            tekst = tekst.TrimStart('!').Trim();
        }

        DateOnly? deadline = null;
        var match = Regex.Match(tekst, @"\s@([\w\-/]+)$");
        if (match.Success && ProbeerDatum(match.Groups[1].Value, out var datum))
        {
            deadline = datum;
            tekst = tekst[..match.Index].Trim();
        }
        return (tekst, prio, deadline);
    }

    private static bool ProbeerDatum(string tekst, out DateOnly datum)
    {
        var vandaag = Vandaag();
        datum = vandaag;
        tekst = tekst.ToLowerInvariant();

        switch (tekst)
        {
            case "vandaag":
                return true;
            case "morgen":
                datum = vandaag.AddDays(1);
                return true;
            case "overmorgen":
                datum = vandaag.AddDays(2);
                return true;
        }

        var dagen = new Dictionary<string, DayOfWeek>
        {
            ["ma"] = DayOfWeek.Monday, ["maandag"] = DayOfWeek.Monday,
            ["di"] = DayOfWeek.Tuesday, ["dinsdag"] = DayOfWeek.Tuesday,
            ["wo"] = DayOfWeek.Wednesday, ["woensdag"] = DayOfWeek.Wednesday,
            ["do"] = DayOfWeek.Thursday, ["donderdag"] = DayOfWeek.Thursday,
            ["vr"] = DayOfWeek.Friday, ["vrijdag"] = DayOfWeek.Friday,
            ["za"] = DayOfWeek.Saturday, ["zaterdag"] = DayOfWeek.Saturday,
            ["zo"] = DayOfWeek.Sunday, ["zondag"] = DayOfWeek.Sunday,
        };
        if (dagen.TryGetValue(tekst, out var dag))
        {
            var dagenVooruit = ((int)dag - (int)vandaag.DayOfWeek + 6) % 7 + 1; // altijd in de toekomst
            datum = vandaag.AddDays(dagenVooruit);
            return true;
        }

        // dd-MM of dd/MM, optioneel met jaartal; zonder jaartal: eerstvolgende keer.
        var m = Regex.Match(tekst, @"^(\d{1,2})[-/](\d{1,2})(?:[-/](\d{4}))?$");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var d) && int.TryParse(m.Groups[2].Value, out var mnd))
        {
            var jaar = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : vandaag.Year;
            try
            {
                datum = new DateOnly(jaar, mnd, d);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            if (!m.Groups[3].Success && datum < vandaag)
            {
                datum = datum.AddYears(1);
            }
            return true;
        }
        return false;
    }

    private void SelectieAanpassen(Action<MijnTaak> wijziging)
    {
        var taken = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MijnTaak>().ToList();
        if (taken.Count == 0)
        {
            return;
        }
        foreach (var taak in taken)
        {
            wijziging(taak);
            // Start en deadline logisch houden: een deadline vóór de startdatum bestaat niet.
            // Wat je zonet zette wint; het andere veld schuift mee.
            if (taak is { Startdatum: { } start, Deadline: { } deadline } && deadline < start)
            {
                taak.Deadline = start;
            }
        }
        Bewaar();
        VulCategorieCombo();
        VulLijst(taken[0].Id);
    }

    private void SelectieVerwijderen()
    {
        var taken = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<MijnTaak>().ToList();
        if (taken.Count == 0)
        {
            return;
        }
        _data.Taken.RemoveAll(t => taken.Any(s => s.Id == t.Id));
        Bewaar();
        VulLijst();
        // Verwijderen is de enige echt destructieve actie hier: altijd een ongedaan-maken.
        var eerste = taken[0].Tekst.Length > 40 ? taken[0].Tekst[..40] + "…" : taken[0].Tekst;
        Toast.ToonUndo(this,
            taken.Count == 1 ? $"Verwijderd: {eerste}" : $"{taken.Count} taken verwijderd",
            () =>
            {
                _data.Taken.AddRange(taken);
                Bewaar();
                VulLijst();
            }, Fluent.Delete);
    }

    private void AfgevinkteOpruimen()
    {
        var klaar = _data.Taken.Count(t => t.Klaar);
        if (klaar == 0)
        {
            return;
        }
        var result = MessageBox.Show(this,
            $"{klaar} afgevinkte {(klaar == 1 ? "taak" : "taken")} verwijderen?",
            "Mijn taken", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }
        _data.Taken.RemoveAll(t => t.Klaar);
        Bewaar();
        VulLijst();
        Toast.Toon(this, klaar == 1 ? "1 taak opgeruimd" : $"{klaar} taken opgeruimd", Fluent.Delete);
    }

    /// <summary>Laat Claude taken voorstellen uit ruwe tekst en voegt de aangevinkte toe.</summary>
    private void TakenUitTekst()
    {
        using var form = new TakenUitTekstForm(AlleCategorieen());
        if (form.ShowDialog(this) != DialogResult.OK || form.Gekozen.Count == 0)
        {
            return;
        }

        foreach (var voorstel in form.Gekozen)
        {
            _data.Taken.Add(new MijnTaak
            {
                Tekst = voorstel.Tekst,
                Categorie = voorstel.Categorie,
                Prioriteit = voorstel.Prioriteit,
                Deadline = voorstel.Deadline,
            });
        }
        Bewaar();
        VulCategorieCombo();
        VulLijst();
        Toast.Toon(this, form.Gekozen.Count == 1
            ? "1 taak toegevoegd" : $"{form.Gekozen.Count} taken toegevoegd", Fluent.Ster);
    }

    private void CategorieenBeheren()
    {
        using var form = new CategorieenForm(_data.Categorieen);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _data.Categorieen = form.Categorieen;
        Bewaar();
        VulCategorieCombo();
        VulLijst();
    }

    /// <summary>
    /// Klein dialoogje met de datumkiezer; null = geannuleerd. Met <paramref name="minimum"/>
    /// zijn eerdere dagen niet kiesbaar (een deadline mag niet vóór de startdatum liggen).
    /// </summary>
    private (DateOnly Datum, bool _)? VraagDatum(
        DateOnly? huidig, string titel = "Datum kiezen", DateOnly? minimum = null)
    {
        using var dialog = new Form
        {
            Text = titel,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(320, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var picker = new DatumKiezer
        {
            Location = new Point(16, 18),
            Width = 270,
            LeegToegestaan = false,
            MinimumDatum = minimum,
            Waarde = huidig ?? Vandaag(),
        };
        var ok = new ModernButton
        {
            Text = "Instellen", Width = 110, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(176, 70),
        };
        var cancel = new ModernButton
        {
            Text = "Annuleren", Width = 100,
            DialogResult = DialogResult.Cancel, Location = new Point(66, 70),
        };
        dialog.Controls.AddRange(new Control[] { picker, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Theme.Apply(dialog);

        return dialog.ShowDialog(this) == DialogResult.OK && picker.Waarde is { } gekozen
            ? (gekozen, true)
            : null;
    }

    private static DateOnly Vandaag() => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly VolgendeMaandag()
    {
        var vandaag = Vandaag();
        var dagen = ((int)DayOfWeek.Monday - (int)vandaag.DayOfWeek + 6) % 7 + 1;
        return vandaag.AddDays(dagen);
    }
}
