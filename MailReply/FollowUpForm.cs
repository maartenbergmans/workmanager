namespace WorkManager;

/// <summary>
/// Toont op wie je nog wacht: links de conversaties waarin jij het laatste woord had en er
/// sindsdien niets terugkwam, rechts jouw oorspronkelijke mail en de herinnering die Claude
/// erbij schrijft. Die herinnering vertrekt als antwoord in dezelfde thread, zodat de
/// tegenpartij hem onder het oude gesprek ziet staan.
/// </summary>
public class FollowUpForm : Form
{
    private readonly ModernListView _lijst;
    private readonly TextBox _origineel;
    private readonly TextBox _concept;
    private readonly Label _status;
    private readonly Label _kop;
    private readonly ModernButton _vernieuw;
    private readonly ModernButton _schrijf;
    private readonly ModernButton _verstuur;
    private readonly ModernButton _uitstel;
    private readonly ModernButton _negeer;
    private readonly NumericUpDown _dagen;
    private readonly CancellationTokenSource _cts = new();
    private List<FollowUpItem> _items = new();

    public FollowUpForm()
    {
        Text = "Wacht op antwoord";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 720);
        MinimumSize = new Size(880, 520);

        var werkbalk = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = false };
        Theme.AsToolbar(werkbalk);

        _vernieuw = new ModernButton
        {
            Text = "Scannen", Width = 130, Kind = ButtonKind.Accent, Glyph = Fluent.Sync,
        };
        _vernieuw.Click += async (_, _) => await ScanAsync();

        _schrijf = new ModernButton
        {
            Text = "Herinnering schrijven", Width = 190, Glyph = Fluent.Edit, Enabled = false,
        };
        _schrijf.Click += async (_, _) => await SchrijfAsync();

        _verstuur = new ModernButton
        {
            Text = "Versturen", Width = 130, Glyph = Fluent.Send, Enabled = false,
        };
        _verstuur.Click += async (_, _) => await VerstuurAsync();

        _uitstel = new ModernButton
        {
            Text = "Week uitstellen", Width = 160, Glyph = Fluent.Klok, Enabled = false,
        };
        _uitstel.Click += (_, _) => Markeer(uitstel: true);

        _negeer = new ModernButton
        {
            Text = "Negeren", Width = 120, Glyph = Fluent.Archive, Enabled = false,
        };
        _negeer.Click += (_, _) => Markeer(uitstel: false);

        var data = FollowUpRadar.Laad();
        _dagen = new NumericUpDown
        {
            Minimum = 1, Maximum = 60, Value = Math.Clamp(data.MinimumDagen, 1, 60), Width = 60,
            Margin = new Padding(16, 11, 4, 0),
        };
        _dagen.ValueChanged += (_, _) =>
        {
            var huidig = FollowUpRadar.Laad();
            huidig.MinimumDagen = (int)_dagen.Value;
            FollowUpRadar.Bewaar(huidig);
        };
        var dagenLabel = new Label
        {
            Text = "dagen stil", AutoSize = true, ForeColor = Theme.Muted,
            Margin = new Padding(0, 14, 0, 0),
        };

        _status = new Label { AutoSize = true, Text = "" };
        Theme.AsStatus(_status);

        werkbalk.Controls.AddRange(new Control[]
        {
            _vernieuw, _schrijf, _verstuur, _uitstel, _negeer, _dagen, dagenLabel, _status,
        });

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            LegeTekst = "Niemand laat je wachten. Klik op Scannen om opnieuw te kijken.",
            LeegGlyph = Fluent.Check,
            HeeftCheckbox = _ => false,
        };
        _lijst.Columns.Add("Wie", 190);
        _lijst.Columns.Add("Onderwerp", 300);
        _lijst.Columns.Add("Stil", 70, HorizontalAlignment.Right);
        _lijst.Columns.Add("Verstuurd", 100);
        _lijst.SelectedIndexChanged += (_, _) => ToonSelectie();
        _lijst.Resize += (_, _) => _lijst.Columns[1].Width = Math.Max(140,
            _lijst.ClientSize.Width - _lijst.Columns[0].Width - _lijst.Columns[2].Width -
            _lijst.Columns[3].Width - 4);

        var links = new Panel { Dock = DockStyle.Left, Width = 560 };
        links.Controls.Add(_lijst);
        links.Controls.Add(Kop("CONVERSATIES ZONDER ANTWOORD"));

        _kop = new Label
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 9, 8, 0),
            Text = "Selecteer een conversatie", ForeColor = Theme.Muted, Font = Theme.CaptionFont,
            BackColor = Theme.Surface, AutoEllipsis = true,
        };

        _origineel = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        };
        _concept = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        };
        _concept.TextChanged += (_, _) =>
        {
            _verstuur.Enabled = Geselecteerd() is not null && _concept.Text.Trim().Length > 0;
        };

        var splitsing = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260,
            BackColor = Theme.Border,
        };
        splitsing.Panel1.Controls.Add(_origineel);
        splitsing.Panel1.Controls.Add(Kop("MIJN LAATSTE BERICHT"));
        splitsing.Panel2.Controls.Add(_concept);
        splitsing.Panel2.Controls.Add(Kop("HERINNERING"));

        var rechts = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1, 0, 0, 0) };
        rechts.Controls.Add(splitsing);
        rechts.Controls.Add(_kop);

        Controls.Add(rechts);
        Controls.Add(links);
        Controls.Add(werkbalk);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
        VensterGeheugen.Volg(this, "followup");

        // Na Apply: het conceptvak is bewerkbaar en mag dus niet als logvenster gestijld worden.
        _concept.BackColor = Theme.Field;
        _concept.ForeColor = Theme.Text;
        _concept.Font = Theme.BaseFont;

        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
        Shown += async (_, _) =>
        {
            Vul(FollowUpRadar.Actief());
            var laatst = FollowUpRadar.Laad().LaatstGescand;
            if (laatst is null || DateTimeOffset.Now - laatst > TimeSpan.FromHours(4))
            {
                await ScanAsync();
            }
            else
            {
                _status.Text = $"Laatst gescand om {laatst:HH:mm}";
            }
        };
    }

    private static Label Kop(string tekst) => new()
    {
        Dock = DockStyle.Top, Height = 28, Text = tekst, Font = Theme.CaptionFont,
        ForeColor = Theme.Muted, Padding = new Padding(12, 8, 0, 0), BackColor = Theme.Surface,
    };

    private FollowUpItem? Geselecteerd() =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as FollowUpItem : null;

    private async Task ScanAsync()
    {
        if (MailReplySettings.Load().AppWachtwoord.Length == 0)
        {
            _status.Text = "Stel eerst je Gmail-app-wachtwoord in bij \"Mail beantwoorden\".";
            return;
        }
        _vernieuw.Bezig = true;
        _status.Text = "Gmail doorzoeken…";
        try
        {
            Vul(await FollowUpRadar.ScanAsync(_cts.Token));
            _status.Text = _items.Count == 0
                ? "Niemand wacht op antwoord."
                : $"{_items.Count} conversatie(s) zonder antwoord.";
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens de scan.
        }
        catch (Exception ex)
        {
            _status.Text = $"Scannen mislukt: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _vernieuw.Bezig = false;
            }
        }
    }

    private void Vul(List<FollowUpItem> items)
    {
        var vorige = Geselecteerd()?.ThreadId;
        _items = items;

        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var item in items)
        {
            var rij = new ListViewItem(new[]
            {
                item.Wie,
                item.Onderwerp,
                $"{item.DagenStil} d",
                item.Verstuurd.ToString("d MMM"),
            })
            {
                Tag = item,
            };
            if (item.DagenStil >= 14)
            {
                rij.ForeColor = Theme.Danger; // hier is echt iets blijven liggen
            }
            else if (item.DagenStil >= 7)
            {
                rij.ForeColor = Theme.Warn;
            }
            _lijst.Items.Add(rij);
            if (item.ThreadId == vorige)
            {
                rij.Selected = true;
            }
        }
        _lijst.EndUpdate();

        if (_lijst.SelectedItems.Count == 0 && _lijst.Items.Count > 0)
        {
            _lijst.Items[0].Selected = true;
        }
        ToonSelectie();
    }

    private void ToonSelectie()
    {
        var item = Geselecteerd();
        var geselecteerd = item is not null;
        _schrijf.Enabled = geselecteerd;
        _uitstel.Enabled = geselecteerd;
        _negeer.Enabled = geselecteerd;

        if (item is null)
        {
            _kop.Text = "Selecteer een conversatie";
            _origineel.Text = "";
            _concept.Text = "";
            _verstuur.Enabled = false;
            return;
        }

        _kop.Text = $"{item.Onderwerp}  ·  {string.Join(", ", item.Ontvangers)}  ·  " +
                    $"{item.BerichtenInThread} bericht(en), {item.DagenStil} dagen stil";
        _origineel.Text = item.Tekst.Length > 0 ? item.Tekst : "(geen tekst gevonden)";
        _concept.Text = item.Concept;
        _verstuur.Enabled = _concept.Text.Trim().Length > 0;
    }

    private async Task SchrijfAsync()
    {
        if (Geselecteerd() is not { } item)
        {
            return;
        }
        _schrijf.Bezig = true;
        _status.Text = "Claude schrijft de herinnering…";
        try
        {
            _concept.Text = await FollowUpRadar.ConceptAsync(item, _cts.Token);
            _status.Text = "Herinnering klaar — nalezen en versturen.";
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _status.Text = $"Opstellen mislukt: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _schrijf.Bezig = false;
            }
        }
    }

    private async Task VerstuurAsync()
    {
        if (Geselecteerd() is not { } item)
        {
            return;
        }
        item.Concept = _concept.Text;
        _verstuur.Bezig = true;
        _status.Text = "Versturen…";
        try
        {
            await FollowUpRadar.VerstuurAsync(item, _cts.Token);
            Toast.Toon(this, $"Herinnering verstuurd aan {item.Wie}", Fluent.Send);
            Vul(FollowUpRadar.Actief());
            _status.Text = "Verstuurd.";
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten.
        }
        catch (Exception ex)
        {
            _status.Text = $"Versturen mislukt: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _verstuur.Bezig = false;
            }
        }
    }

    private void Markeer(bool uitstel)
    {
        if (Geselecteerd() is not { } item)
        {
            return;
        }
        if (uitstel)
        {
            FollowUpRadar.Markeer(item.ThreadId, uitstelTot: DateTimeOffset.Now.AddDays(7));
            Toast.Toon(this, "Een week uitgesteld", Fluent.Klok);
        }
        else
        {
            FollowUpRadar.Markeer(item.ThreadId, genegeerd: true);
            Toast.Toon(this, "Niet meer opvolgen", Fluent.Archive);
        }
        Vul(FollowUpRadar.Actief());
    }
}
