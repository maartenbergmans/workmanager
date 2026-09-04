namespace WorkManager;

/// <summary>
/// De verjaardag- en cadeauradar: links wie er aan de beurt is (met aftelling en leeftijd),
/// rechts alles over de geselecteerde persoon — notities, budget, bewaarde cadeau-ideeën en
/// wat je eerder gaf. "Ideeën vragen" laat Claude zes voorstellen doen die rekening houden
/// met de vorige cadeaus. De taken zelf verschijnen vanzelf in Mijn taken
/// (zie <see cref="Verjaardagen"/>).
/// </summary>
public sealed class VerjaardagenForm : Form
{
    private readonly ModernListView _lijst;
    private readonly TextBox _naam;
    private readonly NumericUpDown _dag;
    private readonly ComboBox _maand;
    private readonly NumericUpDown _jaar;
    private readonly TextBox _relatie;
    private readonly NumericUpDown _budget;
    private readonly NumericUpDown _dagenVooraf;
    private readonly TextBox _notities;
    private readonly ModernListView _ideeen;
    private readonly ModernListView _gegeven;
    private readonly ModernButton _ideeKnop;
    private readonly CancellationTokenSource _cts = new();
    private VerjaardagData _data;
    private Jarige? _huidig;

    private static readonly string[] Maanden =
    {
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december",
    };

    public VerjaardagenForm()
    {
        Text = "Verjaardagen & cadeaus";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1020, 660);
        MinimumSize = new Size(880, 560);

        _data = Verjaardagen.Load();

        // ---- links: de radar
        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog niemand — voeg hieronder een verjaardag toe.",
            LeegGlyph = "🎂",
        };
        _lijst.Columns.Add("Wie", 130);
        _lijst.Columns.Add("Datum", 100);
        _lijst.Columns.Add("Nog", 90);
        _lijst.Columns.Add("Wordt", 60);
        _lijst.SelectedIndexChanged += (_, _) => ToonSelectie();

        var lijstMenu = new ContextMenuStrip();
        Theme.Style(lijstMenu);
        var verwijderItem = new ToolStripMenuItem("Verwijderen");
        verwijderItem.Click += (_, _) => Verwijderen();
        lijstMenu.Items.Add(verwijderItem);
        _lijst.ContextMenuStrip = lijstMenu;

        var nieuwKnop = new ModernButton
        {
            Text = "Verjaardag toevoegen", Dock = DockStyle.Bottom, Height = 34, Glyph = Fluent.Add,
        };
        nieuwKnop.Click += (_, _) => Nieuw();

        var linksGroup = new ModernGroupBox
        {
            Text = "Wie is er aan de beurt", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        linksGroup.Controls.Add(_lijst);
        linksGroup.Controls.Add(nieuwKnop);
        linksGroup.Accent = Theme.KlantPrive;

        // ---- rechts: de fiche
        var fiche = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 200, ColumnCount = 4, Padding = new Padding(10, 8, 10, 0),
        };
        fiche.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        fiche.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        fiche.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        fiche.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        _naam = new TextBox { Dock = DockStyle.Fill };
        _naam.TextChanged += (_, _) => Bewaar(j => j.Naam = _naam.Text.Trim());
        _dag = new NumericUpDown { Minimum = 1, Maximum = 31, Width = 60 };
        _maand = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _maand.Items.AddRange(Maanden.Cast<object>().ToArray());
        _jaar = new NumericUpDown { Minimum = 0, Maximum = 2100, Width = 80 };
        _dag.ValueChanged += (_, _) => Bewaar(j => j.Dag = (int)_dag.Value);
        _maand.SelectedIndexChanged += (_, _) => Bewaar(j => j.Maand = _maand.SelectedIndex + 1);
        _jaar.ValueChanged += (_, _) => Bewaar(j => j.Jaar = (int)_jaar.Value);
        var datumRij = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        datumRij.Controls.Add(_dag);
        datumRij.Controls.Add(_maand);
        datumRij.Controls.Add(new Label
        {
            Text = "geboortejaar", AutoSize = true, ForeColor = Theme.Muted,
            Margin = new Padding(10, 6, 4, 0),
        });
        datumRij.Controls.Add(_jaar);

        _relatie = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "echtgenote, dochter, oma, …" };
        _relatie.TextChanged += (_, _) => Bewaar(j => j.Relatie = _relatie.Text.Trim());
        _budget = new NumericUpDown { Minimum = 0, Maximum = 5000, Increment = 10, Width = 90 };
        _budget.ValueChanged += (_, _) => Bewaar(j => j.Budget = (int)_budget.Value);
        _dagenVooraf = new NumericUpDown { Minimum = 3, Maximum = 120, Width = 70 };
        _dagenVooraf.ValueChanged += (_, _) => Bewaar(j => j.DagenVooraf = (int)_dagenVooraf.Value);
        var budgetRij = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        budgetRij.Controls.Add(_budget);
        budgetRij.Controls.Add(new Label
        {
            Text = "euro · taak", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(8, 6, 4, 0),
        });
        budgetRij.Controls.Add(_dagenVooraf);
        budgetRij.Controls.Add(new Label
        {
            Text = "dagen vooraf", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(6, 6, 0, 0),
        });

        _notities = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "interesses, maten, wat zeker niet — dit gaat mee naar Claude",
        };
        _notities.TextChanged += (_, _) => Bewaar(j => j.Notities = _notities.Text.Trim());

        Label Lbl(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left };
        fiche.Controls.Add(Lbl("Naam"), 0, 0);
        fiche.Controls.Add(_naam, 1, 0);
        fiche.Controls.Add(Lbl("Relatie"), 2, 0);
        fiche.Controls.Add(_relatie, 3, 0);
        fiche.Controls.Add(Lbl("Jarig op"), 0, 1);
        fiche.Controls.Add(datumRij, 1, 1);
        fiche.Controls.Add(Lbl("Budget"), 2, 1);
        fiche.Controls.Add(budgetRij, 3, 1);
        fiche.Controls.Add(Lbl("Notities"), 0, 2);
        fiche.Controls.Add(_notities, 1, 2);
        fiche.SetColumnSpan(_notities, 3);
        fiche.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fiche.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fiche.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Ideeën
        _ideeen = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen ideeën — klik op \"Ideeën vragen\".",
            LeegGlyph = "🎁",
        };
        _ideeen.Columns.Add("Idee", 520);
        var ideeMenu = new ContextMenuStrip();
        Theme.Style(ideeMenu);
        var ideeGegeven = new ToolStripMenuItem("Dit heb ik gegeven → naar geschiedenis");
        ideeGegeven.Click += (_, _) => IdeeNaarGeschiedenis();
        ideeMenu.Items.Add(ideeGegeven);
        var ideeKopieer = new ToolStripMenuItem("Kopiëren");
        ideeKopieer.Click += (_, _) =>
        {
            if (_ideeen.SelectedItems.Count > 0)
            {
                Clipboard.SetText(_ideeen.SelectedItems[0].Text);
                Toast.Toon(this, "Idee gekopieerd", Fluent.Copy);
            }
        };
        ideeMenu.Items.Add(ideeKopieer);
        var ideeWeg = new ToolStripMenuItem("Verwijderen");
        ideeWeg.Click += (_, _) =>
        {
            if (_huidig is { } j && _ideeen.SelectedItems.Count > 0)
            {
                j.Ideeen.Remove(_ideeen.SelectedItems[0].Text);
                Verjaardagen.Save(_data);
                ToonSelectie();
            }
        };
        ideeMenu.Items.Add(ideeWeg);
        _ideeen.ContextMenuStrip = ideeMenu;

        _ideeKnop = new ModernButton
        {
            Text = "Ideeën vragen", Width = 150, Kind = ButtonKind.Accent, Glyph = Fluent.Ster,
        };
        _ideeKnop.Click += async (_, _) => await IdeeenVragenAsync();
        var eigenIdee = new ModernButton { Text = "Eigen idee…", Width = 130, Glyph = Fluent.Add };
        eigenIdee.Click += (_, _) => EigenIdee();
        var ideeBalk = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 6, 0, 0), WrapContents = false,
        };
        ideeBalk.Controls.Add(_ideeKnop);
        ideeBalk.Controls.Add(eigenIdee);

        var ideeGroup = new ModernGroupBox
        {
            Text = "Cadeau-ideeën", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        ideeGroup.Controls.Add(_ideeen);
        ideeGroup.Controls.Add(ideeBalk);
        ideeGroup.Accent = Theme.Accent;

        // Geschiedenis
        _gegeven = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog niets genoteerd. Wat je gaf, hoort hier — zo herhaal je jezelf niet.",
            LeegGlyph = Fluent.Kalender,
        };
        _gegeven.Columns.Add("Jaar", 60);
        _gegeven.Columns.Add("Cadeau", 380);
        var gegevenMenu = new ContextMenuStrip();
        Theme.Style(gegevenMenu);
        var gegevenWeg = new ToolStripMenuItem("Verwijderen");
        gegevenWeg.Click += (_, _) =>
        {
            if (_huidig is { } j && _gegeven.SelectedItems.Count > 0 &&
                _gegeven.SelectedItems[0].Tag is GegevenCadeau cadeau)
            {
                j.Gegeven.Remove(cadeau);
                Verjaardagen.Save(_data);
                ToonSelectie();
            }
        };
        gegevenMenu.Items.Add(gegevenWeg);
        _gegeven.ContextMenuStrip = gegevenMenu;
        var gegevenKnop = new ModernButton
        {
            Text = "Gegeven cadeau noteren…", Dock = DockStyle.Bottom, Height = 34, Glyph = Fluent.Check,
        };
        gegevenKnop.Click += (_, _) => NoteerGegeven();
        var gegevenGroup = new ModernGroupBox
        {
            Text = "Eerder gegeven", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        gegevenGroup.Controls.Add(_gegeven);
        gegevenGroup.Controls.Add(gegevenKnop);
        gegevenGroup.Accent = Theme.KlantLauryssens;

        var rechtsSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260,
        };
        rechtsSplit.Panel1.Controls.Add(ideeGroup);
        rechtsSplit.Panel2.Controls.Add(gegevenGroup);

        var rechts = new Panel { Dock = DockStyle.Fill };
        rechts.Controls.Add(rechtsSplit);
        rechts.Controls.Add(fiche);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 400,
        };
        split.Panel1.Controls.Add(linksGroup);
        split.Panel2.Controls.Add(rechts);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(12, 6, 10, 0),
            Text = "De taken komen vanzelf in Mijn taken: cadeau bedenken (standaard 3 weken " +
                   "vooraf), kopen (5 dagen) en feliciteren op de dag zelf.",
        };

        Controls.Add(split);
        Controls.Add(hint);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "verjaardagen");
        hint.ForeColor = Theme.Muted;
        Shown += (_, _) =>
        {
            split.SplitterDistance = 400;
            rechtsSplit.SplitterDistance = Math.Max(200, (int)(rechtsSplit.ClientSize.Height * 0.55));
        };
        FormClosing += (_, _) => _cts.Cancel();
        VulLijst();
    }

    // ---------------------------------------------------------------- lijst

    private void VulLijst(Guid? selecteer = null)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var jarige in _data.Jarigen.OrderBy(j => j.DagenTot(vandaag)))
        {
            var dagen = jarige.DagenTot(vandaag);
            var item = new ListViewItem(jarige.Naam)
            {
                Tag = jarige, UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(jarige.Volgende(vandaag).ToString("ddd d MMM"));
            var nog = item.SubItems.Add(dagen switch
            {
                0 => "vandaag! 🎂",
                1 => "morgen",
                _ => $"{dagen} dagen",
            });
            // Binnen de maand valt op, binnen de week springt eruit.
            nog.ForeColor = dagen <= 7 ? Theme.Danger : dagen <= 31 ? Theme.Warn : Theme.Muted;
            var wordt = item.SubItems.Add(jarige.WordtOp(vandaag)?.ToString() ?? "");
            wordt.ForeColor = Theme.Muted;
            _lijst.Items.Add(item);
            if (selecteer is { } id && jarige.Id == id)
            {
                item.Selected = true;
            }
        }
        _lijst.EndUpdate();
        if (_lijst.SelectedItems.Count == 0 && _lijst.Items.Count > 0)
        {
            _lijst.Items[0].Selected = true;
        }
        ToonSelectie();
    }

    private bool _vullen; // voorkomt dat het vullen van de velden meteen weer opslaat

    private void ToonSelectie()
    {
        _huidig = _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as Jarige : null;
        _vullen = true;
        try
        {
            var aan = _huidig is not null;
            foreach (Control c in new Control[]
                     { _naam, _relatie, _notities, _dag, _maand, _jaar, _budget, _dagenVooraf })
            {
                c.Enabled = aan;
            }
            _ideeKnop.Enabled = aan;
            _naam.Text = _huidig?.Naam ?? "";
            _relatie.Text = _huidig?.Relatie ?? "";
            _notities.Text = _huidig?.Notities ?? "";
            _dag.Value = Math.Clamp(_huidig?.Dag ?? 1, 1, 31);
            _maand.SelectedIndex = Math.Clamp((_huidig?.Maand ?? 1) - 1, 0, 11);
            _jaar.Value = Math.Clamp(_huidig?.Jaar ?? 0, 0, 2100);
            _budget.Value = Math.Clamp(_huidig?.Budget ?? 0, 0, 5000);
            _dagenVooraf.Value = Math.Clamp(_huidig?.DagenVooraf ?? 21, 3, 120);

            _ideeen.Items.Clear();
            foreach (var idee in _huidig?.Ideeen ?? new List<string>())
            {
                _ideeen.Items.Add(new ListViewItem(idee));
            }
            _gegeven.Items.Clear();
            foreach (var cadeau in (_huidig?.Gegeven ?? new List<GegevenCadeau>())
                     .OrderByDescending(g => g.Jaar))
            {
                var item = new ListViewItem(cadeau.Jaar.ToString()) { Tag = cadeau };
                item.SubItems.Add(cadeau.Wat);
                _gegeven.Items.Add(item);
            }
            SchaalKolommen();
        }
        finally
        {
            _vullen = false;
        }
    }

    private void SchaalKolommen()
    {
        // OnResize vuurt al bij het zetten van Size bovenin de constructor, vóórdat de
        // lijsten bestaan — dat legde het hele venster stilletjes om (klik "deed niets").
        if (_ideeen is null || _gegeven is null)
        {
            return;
        }
        if (_ideeen.Columns.Count > 0)
        {
            _ideeen.Columns[0].Width = Math.Max(200, _ideeen.ClientSize.Width - 4);
        }
        if (_gegeven.Columns.Count > 1)
        {
            _gegeven.Columns[1].Width = Math.Max(200, _gegeven.ClientSize.Width - 70);
        }
    }

    /// <summary>Past de geselecteerde persoon aan en bewaart meteen (zoals elders in de app).</summary>
    private void Bewaar(Action<Jarige> wijziging)
    {
        if (_vullen || _huidig is not { } jarige)
        {
            return;
        }
        wijziging(jarige);
        Verjaardagen.Save(_data);
        // De aftelling in de lijst kan veranderd zijn (datum aangepast).
        var id = jarige.Id;
        VulLijst(id);
    }

    private void Nieuw()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        var jarige = new Jarige
        {
            Naam = "Nieuwe naam", Dag = vandaag.Day, Maand = vandaag.Month, Relatie = "",
        };
        _data.Jarigen.Add(jarige);
        Verjaardagen.Save(_data);
        VulLijst(jarige.Id);
        _naam.Focus();
        _naam.SelectAll();
    }

    private void Verwijderen()
    {
        if (_huidig is not { } jarige)
        {
            return;
        }
        if (MessageBox.Show(this, $"{jarige.Naam} uit de verjaardagsradar halen?", "WorkManager",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        _data.Jarigen.Remove(jarige);
        Verjaardagen.Save(_data);
        VulLijst();
    }

    // ---------------------------------------------------------------- ideeën

    private async Task IdeeenVragenAsync()
    {
        if (_huidig is not { } jarige)
        {
            return;
        }
        _ideeKnop.Bezig = true;
        _ideeKnop.Enabled = false;
        try
        {
            var ideeen = await Verjaardagen.IdeeenAsync(jarige, _cts.Token);
            if (ideeen.Count == 0)
            {
                Toast.Toon(this, "Claude gaf geen bruikbare ideeën terug", Fluent.Ster);
                return;
            }
            // Nieuwe ideeën vooraan, zonder de bestaande te dubbelen.
            foreach (var idee in ideeen.Where(i => !jarige.Ideeen.Contains(i)).Reverse())
            {
                jarige.Ideeen.Insert(0, idee);
            }
            Verjaardagen.Save(_data);
            ToonSelectie();
            Toast.Toon(this, $"{ideeen.Count} ideeën voor {jarige.Naam}", Fluent.Ster);
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het denken.
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Ideeën ophalen mislukte", ex.Message);
        }
        finally
        {
            _ideeKnop.Bezig = false;
            _ideeKnop.Enabled = true;
        }
    }

    private void EigenIdee()
    {
        if (_huidig is not { } jarige || VraagTekst("Eigen cadeau-idee", "") is not { Length: > 0 } idee)
        {
            return;
        }
        jarige.Ideeen.Insert(0, idee);
        Verjaardagen.Save(_data);
        ToonSelectie();
    }

    private void IdeeNaarGeschiedenis()
    {
        if (_huidig is not { } jarige || _ideeen.SelectedItems.Count == 0)
        {
            return;
        }
        var idee = _ideeen.SelectedItems[0].Text;
        jarige.Gegeven.Add(new GegevenCadeau { Jaar = DateTime.Now.Year, Wat = idee });
        jarige.Ideeen.Remove(idee);
        Verjaardagen.Save(_data);
        ToonSelectie();
        Toast.Toon(this, $"Genoteerd als gegeven aan {jarige.Naam}", Fluent.Check);
    }

    private void NoteerGegeven()
    {
        if (_huidig is not { } jarige ||
            VraagTekst($"Wat gaf je {jarige.Naam}?", "") is not { Length: > 0 } wat)
        {
            return;
        }
        jarige.Gegeven.Add(new GegevenCadeau { Jaar = DateTime.Now.Year, Wat = wat });
        Verjaardagen.Save(_data);
        ToonSelectie();
    }

    /// <summary>Klein invoerdialoogje in de huisstijl (WinForms heeft er zelf geen).</summary>
    private string? VraagTekst(string titel, string start)
    {
        using var dialog = new Form
        {
            Text = titel,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(520, 168),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var box = new TextBox { Text = start, Location = new Point(16, 20), Width = 470 };
        var ok = new ModernButton
        {
            Text = "Bewaren", Width = 110, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Location = new Point(376, 68),
        };
        var annuleer = new ModernButton
        {
            Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel,
            Location = new Point(266, 68),
        };
        dialog.Controls.AddRange(new Control[] { box, ok, annuleer });
        dialog.AcceptButton = ok;
        dialog.CancelButton = annuleer;
        Theme.Apply(dialog);
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SchaalKolommen();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
