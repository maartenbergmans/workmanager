namespace WorkManager;

/// <summary>
/// Venster voor de teamtaken: per teamlid een groep met taken die af te vinken zijn
/// (vinkje = klaar, valt weg uit de weekmail). Bovenaan taken toevoegen en de knoppen
/// voor de weekmail; onderaan de opmerking die bovenaan de mail komt (bv. wie afwezig is).
/// </summary>
public class TeamTasksForm : Form
{
    private readonly TeamTasksData _data;
    private readonly ModernListView _list;
    private readonly ComboBox _lidCombo;
    private readonly TextBox _nieuweTaak;
    private readonly TextBox _opmerking;
    private readonly ModernButton _mailButton;
    private readonly PulseBar _pulse = new();
    private readonly Label _status;
    private readonly CancellationTokenSource _cts = new();
    private readonly Font _klaarFont;
    private TextBox _preview = null!; // live weekmail-preview
    private bool _loading;
    private bool _negeerCheck; // dubbelklik mag de checkbox niet omzetten
    private bool _sorteerOpPrio; // weergave op ★★★ eerst; de eigen (sleep)volgorde blijft bewaard

    /// <summary>Inspring-/bulletprefix waarmee subtaakrijen onder hun hoofdtaak verschijnen.</summary>
    private const string SubPrefix = "        ◦  ";

    public TeamTasksForm()
    {
        Text = "Taken team";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 780);

        _data = TeamTaskStore.Load();
        _klaarFont = new Font(Font, FontStyle.Strikeout);

        // Werkbalk
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _lidCombo = new ComboBox
        {
            Width = 135, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3),
        };
        _nieuweTaak = new TextBox
        {
            Width = 280, PlaceholderText = "Nieuwe taak…  (! = hoog)", Margin = new Padding(3, 5, 3, 3),
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
            Text = "Toevoegen", Width = 110, Glyph = Fluent.Add, Enabled = false,
        };
        addButton.Click += (_, _) => TaakToevoegen();
        // De knop hoort bij het invoerveld ernaast: pas actief zodra er tekst staat,
        // zodat die samenhang zichtbaar is.
        _nieuweTaak.TextChanged += (_, _) =>
            addButton.Enabled = _nieuweTaak.Text.Trim().Length > 0;
        var claudeButton = new ModernButton { Text = "Uit tekst (Claude)…", Width = 165, Glyph = Fluent.Ster };
        claudeButton.Click += (_, _) => TakenUitTekst();
        _mailButton = new ModernButton
        {
            Text = "Mail opstellen…", Width = 150, Kind = ButtonKind.Accent, Glyph = Fluent.Mail,
        };
        _mailButton.Click += (_, _) => MailOpstellen();
        var vakantiesButton = new ModernButton { Text = "Vakanties…", Width = 125, Glyph = Fluent.Kalender };
        var vakantiesMenu = new ContextMenuStrip();
        Theme.Style(vakantiesMenu);
        var sdworxItem = new ToolStripMenuItem("Ophalen uit SD Worx…");
        sdworxItem.Click += (_, _) => VakantiesOphalen();
        vakantiesMenu.Items.Add(sdworxItem);
        var ingevenItem = new ToolStripMenuItem("Zelf ingeven (Maarten/team)…");
        ingevenItem.Click += (_, _) =>
        {
            using var form = new TeamVakantiesForm(_data);
            form.ShowDialog(this);
        };
        vakantiesMenu.Items.Add(ingevenItem);
        vakantiesButton.Click += (_, _) =>
            vakantiesMenu.Show(vakantiesButton, new Point(0, vakantiesButton.Height + 4));

        // Beheer-acties in één dropdown-knop om de werkbalk compact te houden.
        var beheerMenu = new ContextMenuStrip();
        Theme.Style(beheerMenu);
        var opruimItem = new ToolStripMenuItem("Afgevinkte opruimen");
        opruimItem.Click += (_, _) => AfgevinkteOpruimen();
        beheerMenu.Items.Add(opruimItem);
        beheerMenu.Items.Add(new ToolStripSeparator());
        var ledenItem = new ToolStripMenuItem("Leden beheren…");
        ledenItem.Click += (_, _) => LedenBeheren();
        beheerMenu.Items.Add(ledenItem);
        var stijlItem = new ToolStripMenuItem("Stijl weekmail…");
        stijlItem.Click += (_, _) =>
        {
            using var form = new TeamStijlForm();
            form.ShowDialog(this);
        };
        beheerMenu.Items.Add(stijlItem);
        var beheerButton = new ModernButton { Text = "Beheren", Width = 110, Glyph = Fluent.Settings };
        beheerButton.Click += (_, _) => beheerMenu.Show(beheerButton, new Point(0, beheerButton.Height + 4));

        // Weergavesortering: hoge prioriteit (★★★) bovenaan binnen elk teamlid. De
        // opgeslagen (sleep)volgorde blijft ongemoeid — uitvinken zet alles terug.
        var prioSort = new CheckBox
        {
            Text = "Op ★ sorteren", AutoSize = true, Margin = new Padding(8, 8, 3, 3),
        };
        prioSort.CheckedChanged += (_, _) =>
        {
            _sorteerOpPrio = prioSort.Checked;
            VulLijst();
        };

        _status = new Label { AutoSize = true };
        Theme.AsStatus(_status);
        toolbar.Controls.AddRange(new Control[]
        {
            _lidCombo, _nieuweTaak, addButton, claudeButton, _mailButton, vakantiesButton,
            beheerButton, prioSort, _status,
        });

        // Takenlijst: één groep per teamlid, vinkje = klaar
        _list = new ModernListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            LabelEdit = true,
            HeaderStyle = ColumnHeaderStyle.None,
            ShowItemToolTips = true, // subtaken verschijnen als tooltip op de taak
        };
        _list.Columns.Add("Taak", 800);
        _list.Columns.Add("Prio", 70);
        _list.Resize += (_, _) =>
            _list.Columns[0].Width = Math.Max(200, _list.ClientSize.Width - _list.Columns[1].Width - 4);
        _list.SterrenKolom = 1;
        _list.SterGeklikt += (item, aantal) =>
        {
            var prio = 3 - aantal; // 3 sterren = hoog (0), 1 ster = laag (2)
            if (item.Tag is TeamTaak taak && !taak.Klaar)
            {
                taak.Prioriteit = prio;
                TeamTaskStore.Save(_data);
                MaakItemOp(item, taak);
            }
            else if (item.Tag is SubTaak sub && !sub.Klaar)
            {
                sub.Prioriteit = prio;
                TeamTaskStore.Save(_data);
                MaakSubItemOp(item, sub);
            }
        };
        // Dubbelklik op een rij mag de checkbox niet omzetten (dat is dan "afvinken" terwijl
        // de gebruiker wil bewerken): een dubbelklik markeren en de eerstvolgende check
        // annuleren. Een gewone (enkele) klik op het vinkje werkt normaal.
        _list.MouseDown += (_, e) => _negeerCheck = e.Clicks > 1;
        _list.ItemCheck += (_, e) =>
        {
            if (_negeerCheck)
            {
                e.NewValue = e.CurrentValue;
                _negeerCheck = false;
            }
        };
        // Onthouden welke teamleden de gebruiker open- of dichtklapt, zodat een hervulling
        // van de lijst (na elke wijziging) de stand niet terugzet.
        _list.GroupCollapsedStateChanged += (_, e) =>
        {
            if (_loading || e.GroupIndex < 0 || e.GroupIndex >= _list.Groups.Count ||
                _list.Groups[e.GroupIndex].Tag is not string lid)
            {
                return;
            }
            if (_list.Groups[e.GroupIndex].CollapsedState == ListViewGroupCollapsedState.Expanded)
            {
                _uitgeklapt.Add(lid);
            }
            else
            {
                _uitgeklapt.Remove(lid);
            }
        };
        _list.ItemChecked += (_, e) =>
        {
            if (_loading)
            {
                return;
            }
            if (e.Item.Tag is TeamTaak taak)
            {
                taak.Klaar = e.Item.Checked;
                taak.KlaarOp = taak.Klaar ? DateTimeOffset.Now : null;
                MaakItemOp(e.Item, taak);
                TeamTaskStore.Save(_data);
                if (taak.Klaar)
                {
                    // Afgevinkt: rij verdwijnt meteen; de toast biedt kort "Ongedaan maken"
                    // voor het geval het per ongeluk was.
                    BeginInvoke(() =>
                    {
                        VulLijst();
                        var kort = taak.Tekst.Length > 40 ? taak.Tekst[..40] + "…" : taak.Tekst;
                        Toast.ToonUndo(this, $"Afgevinkt: {kort}", () =>
                        {
                            taak.Klaar = false;
                            taak.KlaarOp = null;
                            TeamTaskStore.Save(_data);
                            VulLijst(taak.Id);
                        }, Fluent.Check);
                    });
                }
                else if (taak.Subtaken.Count > 0)
                {
                    // Weer open gezet: lijst hertekenen zodat de subtaken uitklappen.
                    BeginInvoke(() => VulLijst(taak.Id));
                }
            }
            else if (e.Item.Tag is SubTaak sub)
            {
                sub.Klaar = e.Item.Checked;
                MaakSubItemOp(e.Item, sub);
                TeamTaskStore.Save(_data);
            }
        };
        _list.AfterLabelEdit += (_, e) =>
        {
            if (e.Label is null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(e.Label))
            {
                e.CancelEdit = true;
                return;
            }
            var tag = _list.Items[e.Item].Tag;
            if (tag is TeamTaak taak)
            {
                taak.Tekst = e.Label.Trim();
                TeamTaskStore.Save(_data);
            }
            else if (tag is SubTaak sub)
            {
                // De inspring-/bulletprefix van de subtaakrij is weergave, geen taaktekst.
                sub.Tekst = System.Text.RegularExpressions.Regex
                    .Replace(e.Label, @"^[\s◦•\-]*", "").Trim();
                TeamTaskStore.Save(_data);
                // De prefix weer terugzetten zodat de rij ingesprongen blijft.
                _list.BeginInvoke(() => { if (e.Item < _list.Items.Count) _list.Items[e.Item].Text = SubPrefix + sub.Tekst; });
            }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                SelectieVerwijderen();
            }
            else if (e.KeyCode == Keys.F2)
            {
                TaakBewerken();
            }
        };
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.HitTest(e.Location).Item is { Tag: TeamTaak })
            {
                TaakBewerken();
            }
        };

        // Volgorde (en teamlid) wijzigen door taken te verslepen.
        _list.AllowDrop = true;
        _list.ItemDrag += (_, e) =>
        {
            // Zowel hoofdtaken (volgorde/teamlid) als subtaken (naar een andere hoofdtaak) slepen.
            if (e.Item is ListViewItem { Tag: TeamTaak or SubTaak })
            {
                _list.DoDragDrop(e.Item, DragDropEffects.Move);
            }
        };
        _list.DragOver += (_, e) => e.Effect = DragDropEffects.Move;
        _list.DragDrop += (_, e) => TaakVerslepen(e);

        var listMenu = new ContextMenuStrip();
        Theme.Style(listMenu);
        var bewerkItem = new ToolStripMenuItem("Bewerken…\tF2");
        bewerkItem.Click += (_, _) => TaakBewerken();
        listMenu.Items.Add(bewerkItem);
        var hernoemItem = new ToolStripMenuItem("Tekst ter plekke aanpassen");
        hernoemItem.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0)
            {
                _list.SelectedItems[0].BeginEdit();
            }
        };
        listMenu.Items.Add(hernoemItem);
        var prioMenuItem = new ToolStripMenuItem("Prioriteit");
        foreach (var (naam, waarde) in new[] { ("★★★   hoog", 0), ("★★   normaal", 1), ("★   laag", 2) })
        {
            var keuze = new ToolStripMenuItem(naam);
            keuze.Click += (_, _) => SelectiePrioriteit(waarde);
            prioMenuItem.DropDownItems.Add(keuze);
        }
        listMenu.Items.Add(prioMenuItem);
        var verwijderItem = new ToolStripMenuItem("Verwijderen\tDel");
        verwijderItem.Click += (_, _) => SelectieVerwijderen();
        listMenu.Items.Add(verwijderItem);
        var verplaatsItem = new ToolStripMenuItem("Verplaatsen naar");
        listMenu.Items.Add(verplaatsItem);
        listMenu.Items.Add(new ToolStripSeparator());
        var omhoogItem = new ToolStripMenuItem("Omhoog");
        omhoogItem.Click += (_, _) => VerplaatsInVolgorde(-1);
        listMenu.Items.Add(omhoogItem);
        var omlaagItem = new ToolStripMenuItem("Omlaag");
        omlaagItem.Click += (_, _) => VerplaatsInVolgorde(+1);
        listMenu.Items.Add(omlaagItem);
        listMenu.Opening += (_, e) =>
        {
            if (_list.SelectedItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            verplaatsItem.DropDownItems.Clear();
            foreach (var lid in _data.Leden)
            {
                var doel = new ToolStripMenuItem(lid);
                doel.Click += (_, _) => SelectieVerplaatsenNaar(lid);
                verplaatsItem.DropDownItems.Add(doel);
            }
        };
        _list.ContextMenuStrip = listMenu;

        // Opmerking voor bovenaan de weekmail
        var opmerkingGroup = new ModernGroupBox
        {
            Text = "Opmerking bovenaan de weekmail (bv. wie afwezig is)",
            Dock = DockStyle.Bottom,
            Height = 100,
            Padding = new Padding(10, 8, 10, 10),
        };
        _opmerking = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Text = _data.Opmerking,
        };
        _opmerking.TextChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }
            _data.Opmerking = _opmerking.Text;
            TeamTaskStore.Save(_data);
            WerkPreviewBij();
        };
        opmerkingGroup.Controls.Add(_opmerking);

        // Live preview van de weekmail (rechts): werkt mee terwijl je taken aan/uit vinkt.
        var previewGroup = new ModernGroupBox
        {
            Text = "Weekmail (live preview)",
            Dock = DockStyle.Right,
            Width = 420,
            Padding = new Padding(10, 8, 10, 10),
        };
        _preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoSmall,
        };
        previewGroup.Controls.Add(_preview);

        Controls.Add(_list);
        Controls.Add(previewGroup);
        Controls.Add(opmerkingGroup);
        Controls.Add(_pulse);
        Controls.Add(toolbar);

        FormClosed += (_, _) => _cts.Cancel();

        Theme.Apply(this);
        VensterGeheugen.Volg(this, "teamtaken");
        VulLidCombo();
        VulLijst();
    }

    private void VulLidCombo()
    {
        var huidig = _lidCombo.SelectedItem as string;
        _lidCombo.Items.Clear();
        foreach (var lid in _data.Leden)
        {
            _lidCombo.Items.Add(lid);
        }
        if (_lidCombo.Items.Count > 0)
        {
            var index = huidig is null ? 0 : _lidCombo.Items.IndexOf(huidig);
            _lidCombo.SelectedIndex = index < 0 ? 0 : index;
        }
    }

    /// <summary>Leden waarvan de groep open staat (handmatige toggles blijven bewaard).</summary>
    private readonly HashSet<string> _uitgeklapt = new(StringComparer.OrdinalIgnoreCase);

    private bool _startStandGezet;

    /// <summary>
    /// Is dit lid vandaag met vakantie? Kijkt naar de handmatig ingegeven periodes én naar
    /// de SD Worx-afwezigheidsregels in de weekmail-opmerking — dat is de enige plek waar
    /// de opgehaalde teamvakanties bewaard blijven.
    /// </summary>
    private bool AfwezigVandaag(string lid)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        return _data.Vakanties.Any(v =>
                   string.Equals(v.Persoon, lid, StringComparison.OrdinalIgnoreCase) &&
                   v.Van <= vandaag && vandaag <= v.Tot) ||
               SdWorxVakanties.ParseAfwezigheden(_data.Opmerking, vandaag).Any(a =>
                   string.Equals(a.Naam, lid, StringComparison.OrdinalIgnoreCase) &&
                   a.Van <= vandaag && vandaag <= a.Tot);
    }

    private void VulLijst(Guid? selecteer = null)
    {
        _loading = true;
        // Startstand: aanwezige leden open, wie vandaag met vakantie is dichtgeklapt.
        // Eén keer bepalen — daarna is wat de gebruiker zelf open/dicht klikt leidend.
        if (!_startStandGezet)
        {
            _startStandGezet = true;
            foreach (var lid in _data.Leden.Where(l => !AfwezigVandaag(l)))
            {
                _uitgeklapt.Add(lid);
            }
        }
        // Positie en selectie onthouden: na een wijziging of verwijdering hoor je op (de buurt
        // van) dezelfde rij te blijven staan, niet terug bovenaan de lijst.
        var vorigeSelectie = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var vorigeTop = _list.Items.Count > 0 ? _list.TopItem?.Index ?? 0 : 0;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();

        // Ledenvolgorde aanhouden; taken van (verwijderde) leden buiten de lijst blijven zichtbaar.
        var leden = _data.Leden
            .Concat(_data.Taken.Select(t => t.Lid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // De groep van de te selecteren taak moet sowieso open, anders is de selectie onzichtbaar.
        if (selecteer is { } sel &&
            _data.Taken.FirstOrDefault(t => t.Id == sel) is { } selectieTaak)
        {
            _uitgeklapt.Add(selectieTaak.Lid);
        }

        foreach (var lid in leden)
        {
            var open = _data.Taken.Count(t =>
                !t.Klaar && string.Equals(t.Lid, lid, StringComparison.OrdinalIgnoreCase));
            var group = new ListViewGroup(
                $"{lid}  ({open} open)" + (AfwezigVandaag(lid) ? "  ·  🏖 vandaag afwezig" : ""))
            {
                Tag = lid,
            };
            // Inklapbaar; standaard dichtgeklapt. Wat de gebruiker openzet blijft open
            // (bijgehouden in _uitgeklapt, ook over hervullingen heen).
            group.CollapsedState = _uitgeklapt.Contains(lid)
                ? ListViewGroupCollapsedState.Expanded
                : ListViewGroupCollapsedState.Collapsed;
            _list.Groups.Add(group);

            // Afgevinkte taken niet meer tonen (per ongeluk afvinken is terug te draaien via
            // de undo-toast direct na het afvinken; opruimen gebeurt via "Afgevinkte opruimen").
            var taken = _data.Taken.Where(t => !t.Klaar &&
                string.Equals(t.Lid, lid, StringComparison.OrdinalIgnoreCase));
            if (_sorteerOpPrio)
            {
                // OrderBy is stabiel: binnen dezelfde prioriteit blijft de eigen volgorde staan.
                taken = taken.OrderBy(t => t.Prioriteit);
            }
            foreach (var taak in taken)
            {
                var item = new ListViewItem(taak.Tekst, group)
                {
                    Tag = taak,
                    Checked = taak.Klaar,
                };
                item.SubItems.Add("");
                MaakItemOp(item, taak);
                _list.Items.Add(item);
                if (taak.Id == selecteer)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }

                // Subtaken als ingesprongen rijen eronder, elk met eigen checkbox en sterren.
                // Een afgevinkte hoofdtaak klapt zijn subtaken in (overzicht bij de weekmail).
                foreach (var sub in taak.Klaar ? Enumerable.Empty<SubTaak>()
                             : _sorteerOpPrio ? taak.Subtaken.OrderBy(s => s.Prioriteit)
                             : taak.Subtaken)
                {
                    var subItem = new ListViewItem(SubPrefix + sub.Tekst, group)
                    {
                        Tag = sub,
                        Checked = sub.Klaar,
                    };
                    subItem.SubItems.Add("");
                    MaakSubItemOp(subItem, sub);
                    _list.Items.Add(subItem);
                }
            }
        }

        _list.EndUpdate();
        // Geen expliciete selectie meegekregen? Dan de vorige plek herstellen: dezelfde rij-index
        // (of de buur als de rij net verwijderd is) en dezelfde scrollpositie.
        if (selecteer is null && _list.Items.Count > 0 && vorigeSelectie >= 0)
        {
            var idx = Math.Min(vorigeSelectie, _list.Items.Count - 1);
            _list.Items[idx].Selected = true;
            _list.Items[idx].EnsureVisible();
        }
        else if (selecteer is null && _list.Items.Count > 0 && vorigeTop > 0)
        {
            try
            {
                _list.TopItem = _list.Items[Math.Min(vorigeTop, _list.Items.Count - 1)];
            }
            catch
            {
                // TopItem is met groepen soms koppig; dan maar bovenaan.
            }
        }
        _loading = false;
        UpdateStatus();
    }

    private void MaakItemOp(ListViewItem item, TeamTaak taak)
    {
        item.UseItemStyleForSubItems = false;
        item.Font = taak.Klaar ? _klaarFont : _list.Font;
        item.SubItems[0].ForeColor = taak.Klaar ? Theme.Muted : Theme.Text;
        item.SubItems[0].Font = item.Font;

        var prio = item.SubItems[1];
        (prio.Text, prio.ForeColor) = taak.Klaar ? ("", Theme.Muted) : Theme.PrioSterren(taak.Prioriteit);
    }

    private void MaakSubItemOp(ListViewItem item, SubTaak sub)
    {
        item.UseItemStyleForSubItems = false;
        item.Font = sub.Klaar ? _klaarFont : _list.Font;
        // Subtaken iets gedempt, zodat het onderscheid met de hoofdtaak duidelijk blijft.
        item.SubItems[0].ForeColor = sub.Klaar ? Theme.Muted : Theme.Mix(Theme.Text, Theme.Muted, 0.5f);
        item.SubItems[0].Font = item.Font;

        var prio = item.SubItems[1];
        (prio.Text, prio.ForeColor) = sub.Klaar ? ("", Theme.Muted) : Theme.PrioSterren(sub.Prioriteit);
    }

    private void UpdateStatus()
    {
        var open = _data.Taken.Count(t => !t.Klaar);
        var klaar = _data.Taken.Count(t => t.Klaar);
        // Ook tonen wanneer de teamvakanties het laatst (op donderdag) opgehaald zijn.
        var vak = TeamVakantieCheck.LaatsteSucces is { } d
            ? $"   ·   🌴 vakanties gecheckt {d:ddd d/M}"
            : "   ·   🌴 vakanties nog niet gecheckt";
        _status.Text = $"{open} open, {klaar} afgevinkt{vak}";
        WerkPreviewBij();
    }

    /// <summary>Herbouwt de live preview van de weekmail (met de huidige taken/opmerking).</summary>
    private void WerkPreviewBij()
    {
        if (_preview is null)
        {
            return;
        }
        try
        {
            _preview.Text = TeamMailBuilder.BouwZelf(_data).Tekst.ReplaceLineEndings("\r\n");
        }
        catch
        {
            // Preview mag nooit het venster breken.
        }
    }

    private void TaakToevoegen()
    {
        var tekst = _nieuweTaak.Text.Trim();
        if (tekst.Length == 0 || _lidCombo.SelectedItem is not string lid)
        {
            return;
        }

        // "!" vooraan = hoge prioriteit (zelfde snelle invoer als bij Mijn taken).
        var prio = 1;
        if (tekst.StartsWith('!'))
        {
            prio = 0;
            tekst = tekst.TrimStart('!').Trim();
            if (tekst.Length == 0)
            {
                return;
            }
        }

        var taak = new TeamTaak { Lid = lid, Tekst = tekst, Prioriteit = prio };
        _data.Taken.Add(taak);
        TeamTaskStore.Save(_data);
        _nieuweTaak.Clear();
        _nieuweTaak.Focus();
        VulLijst(taak.Id);
    }

    /// <summary>
    /// Taak toevoegen vanuit een ander venster (de cockpitknop "Nieuwe teamtaak") terwijl
    /// dit venster openstaat: via de geheugenkopie, want rechtstreeks in team-tasks.json
    /// schrijven zou bij de eerstvolgende save van dit venster weer overschreven worden.
    /// </summary>
    public void VoegTaakToe(TeamTaak taak)
    {
        _data.Taken.Add(taak);
        TeamTaskStore.Save(_data);
        VulLijst(taak.Id);
    }

    /// <summary>Zet de prioriteit van de geselecteerde taken.</summary>
    private void SelectiePrioriteit(int prioriteit)
    {
        var taken = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<TeamTaak>().ToList();
        if (taken.Count == 0)
        {
            return;
        }
        foreach (var taak in taken)
        {
            taak.Prioriteit = prioriteit;
        }
        TeamTaskStore.Save(_data);
        VulLijst(taken[0].Id);
    }

    /// <summary>Opent de bewerkdialoog voor de geselecteerde taak (tekst + teamlid).</summary>
    private void TaakBewerken()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not TeamTaak taak)
        {
            return;
        }

        using var form = new TeamTaakBewerkForm(_data.Leden, taak);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var verplaatst = !string.Equals(taak.Lid, form.Lid, StringComparison.OrdinalIgnoreCase);
        taak.Tekst = form.TaakTekst;
        taak.Lid = form.Lid;
        taak.Prioriteit = form.Prioriteit;
        taak.Subtaken = form.Subtaken;
        TeamTaskStore.Save(_data);
        VulLijst(taak.Id);
        if (verplaatst)
        {
            Toast.Toon(this, $"Toegewezen aan {form.Lid}", Fluent.People);
        }
    }

    /// <summary>
    /// Haalt de teamvakanties op uit SD Worx en zet de samenvatting in de opmerking
    /// die bovenaan de weekmail komt.
    /// </summary>
    private void VakantiesOphalen()
    {
        using var form = new VakantiesForm();
        if (form.ShowDialog(this) != DialogResult.OK || form.VakantieTekst.Length == 0)
        {
            return;
        }

        // Eerder ingevoegde afwezigheidsregels eerst weghalen: opnieuw ophalen vervángt
        // ze — anders stapelen dezelfde regels bij elke run op in de opmerking.
        var huidig = string.Join(Environment.NewLine, _opmerking.Text
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !SdWorxVakanties.IsAfwezigheidsRegel(l))).Trim();
        _opmerking.Text = huidig.Length == 0
            ? form.VakantieTekst
            : huidig + Environment.NewLine + form.VakantieTekst;
        // De TextChanged-handler van de opmerking bewaart meteen.
        Toast.Toon(this, "Afwezigheden in de weekmail-opmerking gezet", Fluent.Kalender);
    }

    /// <summary>Laat Claude taken uit ruwe tekst halen, verdeeld over de teamleden.</summary>
    private void TakenUitTekst()
    {
        if (_data.Leden.Count == 0)
        {
            MessageBox.Show(this, "Voeg eerst teamleden toe via 'Leden beheren…'.",
                "Taken team", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var standaardLid = _lidCombo.SelectedItem as string ?? _data.Leden[0];
        using var form = new TeamUitTekstForm(_data.Leden, standaardLid);
        if (form.ShowDialog(this) != DialogResult.OK || form.Gekozen.Count == 0)
        {
            return;
        }

        Guid? eerste = null;
        foreach (var voorstel in form.Gekozen)
        {
            var taak = new TeamTaak
            {
                Lid = voorstel.Lid, Tekst = voorstel.Tekst, Prioriteit = voorstel.Prioriteit,
            };
            eerste ??= taak.Id;
            _data.Taken.Add(taak);
        }
        TeamTaskStore.Save(_data);
        VulLijst(eerste);
        Toast.Toon(this, form.Gekozen.Count == 1
            ? "1 taak toegevoegd" : $"{form.Gekozen.Count} taken toegevoegd", Fluent.Ster);
    }

    private void SelectieVerwijderen()
    {
        var geselecteerd = _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).ToList();
        var taken = geselecteerd.OfType<TeamTaak>().ToList();
        var subtaken = geselecteerd.OfType<SubTaak>().ToList();
        if (taken.Count == 0 && subtaken.Count == 0)
        {
            return;
        }

        _data.Taken.RemoveAll(t => taken.Any(s => s.Id == t.Id));
        // Geselecteerde subtaken uit hun hoofdtaak halen.
        foreach (var t in _data.Taken)
        {
            t.Subtaken.RemoveAll(s => subtaken.Contains(s));
        }
        TeamTaskStore.Save(_data);
        VulLijst();
    }

    private void SelectieVerplaatsenNaar(string lid)
    {
        var taken = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<TeamTaak>().ToList();
        foreach (var taak in taken)
        {
            taak.Lid = lid;
        }
        if (taken.Count > 0)
        {
            TeamTaskStore.Save(_data);
            VulLijst(taken[0].Id);
        }
    }

    /// <summary>
    /// Verwerkt het neerzetten van gesleepte taken: ze krijgen het teamlid van de doelrij en
    /// komen erboven of eronder (afhankelijk van de positie binnen de rij) in de volgorde.
    /// </summary>
    private void TaakVerslepen(DragEventArgs e)
    {
        var punt = _list.PointToClient(new Point(e.X, e.Y));
        var doelTag = _list.HitTest(punt).Item?.Tag;

        // Een subtaak naar een andere hoofdtaak slepen: bepaal de doelhoofdtaak (de rij zelf
        // als het een hoofdtaak is, anders de hoofdtaak van de doelsubtaak) en verplaats.
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is SubTaak sub)
        {
            var doelTaak = doelTag as TeamTaak
                ?? (doelTag is SubTaak ds ? _data.Taken.FirstOrDefault(t => t.Subtaken.Contains(ds)) : null);
            var bron = _data.Taken.FirstOrDefault(t => t.Subtaken.Contains(sub));
            if (doelTaak is null || bron is null || ReferenceEquals(doelTaak, bron))
            {
                return;
            }
            bron.Subtaken.Remove(sub);
            doelTaak.Subtaken.Add(sub);
            TeamTaskStore.Save(_data);
            VulLijst(doelTaak.Id);
            return;
        }

        if (doelTag is not TeamTaak doel || _list.HitTest(punt).Item is not { } doelItem)
        {
            return;
        }

        var gesleept = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<TeamTaak>().ToList();
        if (gesleept.Count == 0 || gesleept.Any(t => t.Id == doel.Id))
        {
            return;
        }

        var onderHelft = punt.Y > doelItem.Bounds.Top + doelItem.Bounds.Height / 2;
        _data.Taken.RemoveAll(t => gesleept.Any(g => g.Id == t.Id));
        var doelIndex = _data.Taken.FindIndex(t => t.Id == doel.Id);
        if (doelIndex < 0)
        {
            return;
        }
        foreach (var taak in gesleept)
        {
            taak.Lid = doel.Lid;
        }
        _data.Taken.InsertRange(doelIndex + (onderHelft ? 1 : 0), gesleept);
        TeamTaskStore.Save(_data);
        VulLijst(gesleept[0].Id);
    }

    /// <summary>Verschuift de geselecteerde taak één plaats binnen de taken van hetzelfde teamlid.</summary>
    private void VerplaatsInVolgorde(int richting)
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not TeamTaak taak)
        {
            return;
        }

        var eigen = _data.Taken
            .Where(t => string.Equals(t.Lid, taak.Lid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var positie = eigen.FindIndex(t => t.Id == taak.Id);
        var buurPositie = positie + richting;
        if (positie < 0 || buurPositie < 0 || buurPositie >= eigen.Count)
        {
            return;
        }

        var index = _data.Taken.FindIndex(t => t.Id == taak.Id);
        var buurIndex = _data.Taken.FindIndex(t => t.Id == eigen[buurPositie].Id);
        (_data.Taken[index], _data.Taken[buurIndex]) = (_data.Taken[buurIndex], _data.Taken[index]);
        TeamTaskStore.Save(_data);
        VulLijst(taak.Id);
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
            "Taken team", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }

        _data.Taken.RemoveAll(t => t.Klaar);
        TeamTaskStore.Save(_data);
        VulLijst();
    }

    private void LedenBeheren()
    {
        using var form = new TeamLedenForm(_data.Leden);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _data.Leden = form.Leden;
        TeamTaskStore.Save(_data);
        VulLidCombo();
        VulLijst();
    }

    private void MailOpstellen()
    {
        // De mail bevat alleen taken met hoge prioriteit (★★★); normaal en laag blijven eruit.
        if (!_data.Taken.Any(t => !t.Klaar && t.Prioriteit == 0))
        {
            MessageBox.Show(this,
                "Er staan geen open taken met hoge prioriteit (★★★) — niets om te mailen.\n" +
                "Zet de sterren van de belangrijkste taken op ★★★ (klik op de derde ster).",
                "Taken team", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var mail = TeamMailBuilder.BouwZelf(_data);
        using var form = new TeamMailForm(_data, TeamTaskStore.LoadStijl(), mail.Onderwerp, mail.Tekst);
        form.ShowDialog(this);
    }
}
