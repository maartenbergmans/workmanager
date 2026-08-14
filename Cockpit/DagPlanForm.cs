namespace WorkManager;

/// <summary>
/// De dagplanning: bovenaan groot wat je nú het best doet, daaronder de volgorde met per item
/// een geschatte duur, en onderaan of je rond geraakt voor het einde van je werkdag. Afvinken
/// werkt door: een taak uit het plan wordt ook in "Mijn taken" afgevinkt.
/// </summary>
public sealed class DagPlanForm : Form
{
    private readonly List<AgendaClient.AgendaItem> _meetings;
    private readonly ModernListView _lijst;
    private readonly Label _nuTitel;
    private readonly Label _nuDetail;
    private readonly Label _balans;
    private readonly ModernButton _planKnop;
    private readonly ModernButton _klaarKnop;
    private readonly ComboBox _eindUur;
    private readonly CancellationTokenSource _cts = new();
    private DagPlanData _plan;

    public DagPlanForm(List<AgendaClient.AgendaItem> meetings)
    {
        _meetings = meetings;
        _plan = DagPlan.LaadVandaag() ?? new DagPlanData
        {
            Dag = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"),
        };

        Text = "Dagplanning";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 720);
        MinimumSize = new Size(720, 520);

        // ---- Bovenaan: wat doe je nu ----
        var nuKaart = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(16, 12, 16, 12) };
        _nuTitel = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 44,
            Font = new Font(Theme.SemiBold.FontFamily, 15f, FontStyle.Bold),
            Text = "Nog geen planning voor vandaag",
        };
        _nuDetail = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26 };
        Theme.AsStatus(_nuDetail);
        nuKaart.Controls.Add(_nuDetail);
        nuKaart.Controls.Add(_nuTitel);

        // ---- Werkbalk ----
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _planKnop = new ModernButton
        {
            Text = "Plan mijn dag", Kind = ButtonKind.Accent, Glyph = Fluent.Ster,
        };
        _planKnop.KrimpNaarInhoud();
        _planKnop.Click += async (_, _) => await PlanAsync();
        _klaarKnop = new ModernButton { Text = "✓ Afvinken", Glyph = Fluent.Check };
        _klaarKnop.KrimpNaarInhoud();
        _klaarKnop.Click += (_, _) => VinkAf(klaar: true);
        var overslaanKnop = new ModernButton { Text = "Overslaan" };
        overslaanKnop.KrimpNaarInhoud();
        overslaanKnop.Click += (_, _) => VinkAf(klaar: false);
        var langerKnop = new ModernButton { Text = "+15 min" };
        langerKnop.KrimpNaarInhoud();
        langerKnop.Click += (_, _) => PasDuurAan(15);
        var korterKnop = new ModernButton { Text = "−15 min" };
        korterKnop.KrimpNaarInhoud();
        korterKnop.Click += (_, _) => PasDuurAan(-15);
        // Zelf de volgorde bepalen: ▲/▼ verschuift het geselecteerde item (slepen kan ook).
        var omhoogKnop = new ModernButton { Text = "▲", Width = 44 };
        omhoogKnop.Click += (_, _) => Verschuif(-1);
        var omlaagKnop = new ModernButton { Text = "▼", Width = 44 };
        omlaagKnop.Click += (_, _) => Verschuif(+1);
        // Afgewerkte/overgeslagen items standaard verbergen; met de knop haal je ze terug
        // in beeld (bv. om iets per ongeluk afgevinkts terug te vinden).
        var afgewerkteKnop = new ModernButton { Text = "Afgewerkte" };
        afgewerkteKnop.KrimpNaarInhoud();
        afgewerkteKnop.Click += (_, _) =>
        {
            _toonAfgewerkte = !_toonAfgewerkte;
            afgewerkteKnop.Text = _toonAfgewerkte ? "Afgewerkte ✓" : "Afgewerkte";
            afgewerkteKnop.KrimpNaarInhoud();
            Toon();
        };
        var eindLabel = new Label { Text = "Klaar om", AutoSize = true, Padding = new Padding(12, 10, 4, 0) };
        _eindUur = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 6, 3, 3) };
        _eindUur.Items.AddRange(new object[] { "16:00", "16:30", "17:00", "17:30", "18:00", "18:30", "19:00" });
        _eindUur.SelectedItem = _plan.EindeWerkdag;
        if (_eindUur.SelectedIndex < 0)
        {
            _eindUur.SelectedItem = "17:30";
        }
        _eindUur.SelectedIndexChanged += (_, _) =>
        {
            _plan.EindeWerkdag = _eindUur.SelectedItem as string ?? "17:30";
            DagPlan.Bewaar(_plan);
            Toon();
        };
        toolbar.Controls.Add(_planKnop);
        toolbar.Controls.Add(_klaarKnop);
        toolbar.Controls.Add(overslaanKnop);
        toolbar.Controls.Add(langerKnop);
        toolbar.Controls.Add(korterKnop);
        toolbar.Controls.Add(omhoogKnop);
        toolbar.Controls.Add(omlaagKnop);
        toolbar.Controls.Add(afgewerkteKnop);
        toolbar.Controls.Add(eindLabel);
        toolbar.Controls.Add(_eindUur);

        // ---- De volgorde ----
        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Klik op \"Plan mijn dag\" — Claude zet je taken, mails en afspraken op volgorde",
            LeegGlyph = Fluent.Ster,
            CheckBoxes = true, // expliciet aanvinken = afwerken (geen dubbelklik-verrassingen)
        };
        // Alleen echt werk krijgt een checkbox; afspraken lopen vanzelf af en afgehandelde
        // rijen zijn al geweest.
        _lijst.HeeftCheckbox = item =>
            item.Tag is PlanItem { VastBlok: false, Afgehandeld: false };
        _lijst.Columns.Add("Wanneer", 110);
        _lijst.Columns.Add("Duur", 70);
        _lijst.Columns.Add("Wat", 470);
        _lijst.Columns.Add("Waarom", 200);
        _lijst.ItemCheck += (_, e) =>
        {
            if (_laden || (_lijst.Items.Count > e.Index &&
                _lijst.Items[e.Index].Tag is not PlanItem { VastBlok: false, Afgehandeld: false }))
            {
                e.NewValue = e.CurrentValue; // geen toggle op afspraken/afgehandelde rijen
            }
        };
        _lijst.ItemChecked += (_, e) =>
        {
            if (!_laden && e.Item.Checked && e.Item.Tag is PlanItem geklikt)
            {
                HandelItemAf(geklikt, klaar: true);
            }
        };
        // Slepen om te herordenen: pak een rij vast en laat hem op de gewenste plek los.
        _lijst.AllowDrop = true;
        _lijst.ItemDrag += (_, e) =>
        {
            if (e.Item is ListViewItem { Tag: PlanItem { VastBlok: false, Afgehandeld: false } } rij)
            {
                _lijst.DoDragDrop(rij, DragDropEffects.Move);
            }
        };
        _lijst.DragOver += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(typeof(ListViewItem)) == true
                ? DragDropEffects.Move : DragDropEffects.None;
        _lijst.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(ListViewItem)) is not ListViewItem bron ||
                bron.Tag is not PlanItem versleept)
            {
                return;
            }
            var punt = _lijst.PointToClient(new Point(e.X, e.Y));
            VerplaatsNaar(versleept, _lijst.GetItemAt(punt.X, punt.Y)?.Tag as PlanItem);
        };

        _balans = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(16, 8, 16, 0), AutoSize = false };

        Controls.Add(_lijst);
        Controls.Add(_balans);
        Controls.Add(toolbar);
        Controls.Add(nuKaart);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster

        // Terwijl het venster openstaat blijft de planning meebewegen: nieuwe mails en taken
        // schuiven erin, en de tijdlijn verschuift met de klok mee.
        _ververs = new System.Windows.Forms.Timer { Interval = 60_000 };
        _ververs.Tick += (_, _) => Synchroniseer();
        FormClosed += (_, _) =>
        {
            _ververs.Stop();
            _ververs.Dispose();
            _cts.Cancel();
        };
        Shown += (_, _) =>
        {
            Synchroniseer();
            _ververs.Start();
        };
    }

    private readonly System.Windows.Forms.Timer _ververs;

    /// <summary>Haalt nieuwe mails/taken binnen het bestaande plan en hertekent de tijdlijn.</summary>
    private void Synchroniseer()
    {
        if (_plan.Items.Count == 0)
        {
            Toon(); // nog geen plan: alleen de klok laten meelopen
            return;
        }
        List<MailBericht> mails;
        try
        {
            mails = CockpitCache.Load();
        }
        catch
        {
            mails = new List<MailBericht>();
        }
        var (bij, _) = DagPlan.VulAan(_plan, mails, _meetings);
        Toon();
        if (bij > 0)
        {
            Toast.Toon(this, bij == 1
                ? "Er kwam 1 item bij in je planning"
                : $"Er kwamen {bij} items bij in je planning", Fluent.Ster);
        }
    }

    private async Task PlanAsync()
    {
        _planKnop.Bezig = true;
        _planKnop.Enabled = false;
        _nuDetail.Text = "Claude zet je dag op volgorde…";
        try
        {
            _plan = await DagPlan.MaakAsync(
                _meetings, _eindUur.SelectedItem as string ?? "17:30", _cts.Token);
            Toon();
            Toast.Toon(this, "Dagplanning klaar", Fluent.Ster);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het plannen.
        }
        catch (Exception ex)
        {
            _nuDetail.Text = "";
            Toast.Toon(this, $"Plannen mislukt: {ex.Message}", Fluent.Ster);
        }
        finally
        {
            _planKnop.Bezig = false;
            _planKnop.Enabled = true;
        }
    }

    /// <summary>Vinkt het geselecteerde item af (of slaat het over) — de werkbalkknoppen.</summary>
    private void VinkAf(bool klaar)
    {
        if (Geselecteerd() is not { } item)
        {
            return;
        }
        if (item.VastBlok)
        {
            Toast.Toon(this, item.Soort == "info"
                ? "Dit is ter info — het blokkeert je agenda toch al niet"
                : "Een afspraak vink je niet af — die loopt vanzelf af", Fluent.Kalender);
            return;
        }
        HandelItemAf(item, klaar);
    }

    /// <summary>Werkt een planitem af (of slaat het over) en werkt de brontaak mee bij.</summary>
    private void HandelItemAf(PlanItem item, bool klaar)
    {
        if (item is { VastBlok: true } or { Afgehandeld: true })
        {
            return;
        }
        item.Klaar = klaar;
        item.Overgeslagen = !klaar;
        // Een taak uit het plan is dezelfde taak als in "Mijn taken": daar ook afvinken.
        if (klaar && item.TaakId is { } id)
        {
            var data = MijnTaakStore.Load();
            if (data.Taken.FirstOrDefault(t => t.Id == id) is { } taak && !taak.Klaar)
            {
                taak.Klaar = true;
                taak.KlaarOp = DateTimeOffset.Now;
                MijnTaakStore.Save(data);
            }
        }
        DagPlan.Bewaar(_plan);
        Toon();
        if (klaar && _plan.Items.All(i => i.VastBlok || i.Afgehandeld))
        {
            Confetti.Vier(this);
            Toast.Toon(this, "Dagplanning afgewerkt 🎉", Fluent.Check);
        }
    }

    /// <summary>Verschuift het geselecteerde item één plek omhoog (-1) of omlaag (+1).</summary>
    private void Verschuif(int richting)
    {
        if (Geselecteerd() is not { VastBlok: false, Afgehandeld: false } item)
        {
            return;
        }
        // De volgorde leeft in plan.Items; afspraken en info-regels staan vast en doen niet mee.
        var vrij = _plan.Items.Where(i => !i.VastBlok && !i.Afgehandeld).ToList();
        var pos = vrij.FindIndex(i => i.Id == item.Id);
        var doel = pos + richting;
        if (pos < 0 || doel < 0 || doel >= vrij.Count)
        {
            return;
        }
        var a = _plan.Items.IndexOf(vrij[pos]);
        var b = _plan.Items.IndexOf(vrij[doel]);
        (_plan.Items[a], _plan.Items[b]) = (_plan.Items[b], _plan.Items[a]);
        item.Waarom = "zelf gekozen volgorde";
        DagPlan.Bewaar(_plan);
        Toon(); // selectie blijft op het item staan (Toon herstelt op Id)
    }

    /// <summary>Zet een versleept item vlak vóór het doel (of achteraan bij een lege plek).</summary>
    private void VerplaatsNaar(PlanItem versleept, PlanItem? doel)
    {
        if (versleept is not { VastBlok: false, Afgehandeld: false } ||
            ReferenceEquals(versleept, doel))
        {
            return;
        }
        _plan.Items.Remove(versleept);
        if (doel is null || doel.Afgehandeld)
        {
            _plan.Items.Add(versleept); // onder de lijst gedropt: achteraan
        }
        else
        {
            // Op een afspraak/info-regel gedropt = er net na; op een gewoon item = er net vóór.
            var idx = _plan.Items.IndexOf(doel);
            _plan.Items.Insert(
                Math.Clamp(doel.VastBlok ? idx + 1 : idx, 0, _plan.Items.Count),
                versleept);
        }
        versleept.Waarom = "zelf gekozen volgorde";
        DagPlan.Bewaar(_plan);
        Toon();
    }

    private void PasDuurAan(int minuten)
    {
        if (Geselecteerd() is not { } item || item.VastBlok)
        {
            return;
        }
        item.Minuten = Math.Clamp(item.Minuten + minuten, 5, 240);
        DagPlan.Bewaar(_plan);
        Toon();
    }

    private PlanItem? Geselecteerd() =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as PlanItem : null;

    private bool _laden; // ItemChecked negeren terwijl de lijst gevuld wordt
    private bool _toonAfgewerkte; // afgewerkte/overgeslagen items onderaan meetonen

    private void Toon()
    {
        _laden = true;
        var tijdlijn = DagPlan.Tijdlijn(_plan);
        var vorigeSelectie = Geselecteerd()?.Id;

        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var (item, start) in tijdlijn)
        {
            var rij = new ListViewItem(start.ToLocalTime().ToString("HH:mm"))
            {
                Tag = item,
                UseItemStyleForSubItems = false,
            };
            rij.SubItems.Add(Duur(item.Minuten));
            rij.SubItems.Add((item.Soort switch
            {
                "afspraak" => "📅 ",
                "info" => "🔔 ",
                "mail" => "✉ ",
                _ => "▸ ",
            }) + item.Tekst);
            rij.SubItems.Add(item.Waarom);
            if (item.VastBlok)
            {
                rij.ForeColor = Theme.Muted; // vast blok / ter info, geen keuze
            }
            _lijst.Items.Add(rij);
            if (item.Id == vorigeSelectie)
            {
                rij.Selected = true;
            }
        }
        // Afgehandelde items alleen op verzoek (knop "Afgewerkte"): gedempt en met een vinkje.
        foreach (var item in _toonAfgewerkte
                     ? _plan.Items.Where(i => i.Afgehandeld)
                     : Enumerable.Empty<PlanItem>())
        {
            var rij = new ListViewItem(item.Klaar ? "✓" : "—")
            {
                Tag = item, UseItemStyleForSubItems = false, ForeColor = Theme.Muted,
            };
            rij.SubItems.Add(Duur(item.Minuten));
            rij.SubItems.Add(item.Tekst);
            rij.SubItems.Add(item.Klaar ? "afgewerkt" : "overgeslagen");
            _lijst.Items.Add(rij);
        }
        _lijst.EndUpdate();
        _laden = false;

        ToonNu(tijdlijn);
        ToonBalans();
    }

    private void ToonNu(List<(PlanItem Item, DateTimeOffset Start)> tijdlijn)
    {
        var eerste = tijdlijn.FirstOrDefault(r => !r.Item.VastBlok);
        var eerstvolgendeAfspraak = tijdlijn.FirstOrDefault(r => r.Item.Soort == "afspraak");
        if (eerste.Item is null)
        {
            _nuTitel.Text = _plan.Items.Count == 0
                ? "Nog geen planning voor vandaag"
                : "Alles afgewerkt 🎉";
            _nuDetail.Text = _plan.Items.Count == 0
                ? "Klik op \"Plan mijn dag\"."
                : "Niets meer op de lijst.";
            return;
        }
        _nuTitel.Text = "Nu: " + eerste.Item.Tekst;
        var detail = $"~{Duur(eerste.Item.Minuten)}";
        if (eerste.Item.Waarom.Length > 0)
        {
            detail += $"  ·  {eerste.Item.Waarom}";
        }
        if (eerstvolgendeAfspraak.Item is { } afspraak && afspraak.VastStart is { } begin)
        {
            var over = begin - DateTimeOffset.Now;
            if (over > TimeSpan.Zero && over < TimeSpan.FromHours(4))
            {
                detail += $"  ·  over {Duur((int)over.TotalMinutes)}: {afspraak.Tekst}";
            }
        }
        _nuDetail.Text = detail;
    }

    private void ToonBalans()
    {
        if (_plan.Items.Count == 0)
        {
            _balans.Text = "";
            return;
        }
        var (klaar, verschil, werk) = DagPlan.Haalbaarheid(_plan);
        var einde = DagPlan.EindeMoment(_plan);
        if (werk == 0)
        {
            _balans.Text = "Alles afgewerkt — de rest van de dag is van jou 🎉";
            _balans.ForeColor = Theme.Success;
            return;
        }
        if (verschil >= TimeSpan.Zero)
        {
            _balans.Text = $"✅ Je geraakt rond: klaar om {klaar.ToLocalTime():HH:mm}, " +
                           $"{Duur((int)verschil.TotalMinutes)} speling tot {einde.ToLocalTime():HH:mm} " +
                           $"({Duur(werk)} werk te gaan)";
            _balans.ForeColor = Theme.Success;
        }
        else
        {
            _balans.Text = $"⚠ Te veel voor vandaag: klaar om {klaar.ToLocalTime():HH:mm}, " +
                           $"{Duur((int)-verschil.TotalMinutes)} over {einde.ToLocalTime():HH:mm} heen " +
                           $"— sla iets over of verzet het";
            _balans.ForeColor = Theme.Warn;
        }
    }

    private static string Duur(int minuten) =>
        minuten >= 60
            ? (minuten % 60 == 0 ? $"{minuten / 60}u" : $"{minuten / 60}u{minuten % 60:00}")
            : $"{minuten} min";
}
