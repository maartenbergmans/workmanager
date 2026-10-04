namespace WorkManager;

/// <summary>
/// Het git-overzicht van alle repo's op deze pc in één tabel: branch, hoeveel er ongecommit
/// is, hoeveel daarvan al gestaged, hoe ver je voor- of achterloopt op de remote, en hoe lang
/// de oudste wijziging er al ligt. Zo zie je in één oogopslag waar je nog werk hebt laten
/// liggen — in plaats van per project een venster te openen.
///
/// <para>De tabel leest uit de cache van <see cref="GitRadar"/> (die elk uur een ronde doet),
/// dus hij staat meteen gevuld. "Verversen" peilt alles opnieuw. Dubbelklik opent de volledige
/// bestandslijst; met rechtsklik commit, pull of open je het project — of haal je een repo uit
/// het beeld als je hem niet wil volgen.</para>
/// </summary>
public sealed class GitOverzichtForm : Form
{
    private readonly ModernListView _lijst;
    private readonly Label _status;
    private readonly ModernButton _verversKnop;
    private readonly CancellationTokenSource _cts = new();
    private bool _toonGenegeerd;

    public GitOverzichtForm()
    {
        Text = "Git-overzicht — alle projecten";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1020, 620);
        MinimumSize = new Size(720, 420);

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog geen repo's gevonden",
            LeegGlyph = Fluent.Document,
        };
        _lijst.Columns.Add("Project", 250);
        _lijst.Columns.Add("Branch", 130);
        _lijst.Columns.Add("Ongecommit", 110);
        _lijst.Columns.Add("Staged", 80);
        _lijst.Columns.Add("Remote", 140);
        _lijst.Columns.Add("Oudste", 90);
        _lijst.Columns.Add("Gepeild", 110);
        _lijst.DoubleClick += (_, _) => OpenStatus();

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
        };
        Theme.AsStatus(_status);

        var menu = new ContextMenuStrip();
        Theme.Style(menu);
        menu.Items.Add(Item("Bestandslijst…", OpenStatus));
        menu.Items.Add(Item("Committen…", OpenCommit));
        menu.Items.Add(Item("Pullen (fast-forward)", async () => await PullAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Claude hier starten", () => Start(ClientLauncher.StartClaude)));
        menu.Items.Add(Item("PhpStorm hier openen", () => Start(ClientLauncher.StartPhpStorm)));
        menu.Items.Add(Item("Verkenner openen", () => Start(map =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(map)
            {
                UseShellExecute = true,
            }))));
        menu.Items.Add(new ToolStripSeparator());
        var negeerItem = Item("Niet meer volgen", Negeer);
        negeerItem.ToolTipText =
            "Haalt deze repo uit het overzicht, de teller en de online tabel. " +
            "Met \"Genegeerde tonen\" krijg je hem terug.";
        menu.Items.Add(negeerItem);
        _lijst.ContextMenuStrip = menu;

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var sluit = new ModernButton { Text = "Sluiten", DialogResult = DialogResult.Cancel, Width = 100 };
        // Dit venster staat niet-modaal open (TrayAppContext.Show()), en dan sluit een
        // DialogResult op zichzelf niets: de knop moet het zelf doen.
        sluit.Click += (_, _) => Close();
        _verversKnop = new ModernButton { Text = "Verversen", Width = 120, Glyph = Fluent.Sync };
        _verversKnop.Click += async (_, _) => await ScanAsync();
        var statusKnop = new ModernButton { Text = "Bestandslijst…", Width = 150 };
        statusKnop.Click += (_, _) => OpenStatus();
        var commitKnop = new ModernButton { Text = "Committen…", Width = 140, Kind = ButtonKind.Accent };
        commitKnop.Click += (_, _) => OpenCommit();
        var onlineKnop = new ModernButton { Text = "Online tabel…", Width = 150, Glyph = Fluent.Globe };
        new ToolTip().SetToolTip(onlineKnop, "De pagina waarop je collega kan meevolgen wat er openstaat");
        onlineKnop.Click += (_, _) => new GitWebForm().Show(this);
        var nuOnlineKnop = new ModernButton { Text = "Nu online zetten", Width = 170 };
        nuOnlineKnop.Click += async (_, _) => await NuOnlineAsync();
        var genegeerdKnop = new ModernButton { Text = "Genegeerde tonen", Width = 170 };
        genegeerdKnop.Click += (_, _) =>
        {
            _toonGenegeerd = !_toonGenegeerd;
            genegeerdKnop.Text = _toonGenegeerd ? "Genegeerde verbergen" : "Genegeerde tonen";
            Vul();
        };
        knoppen.Controls.Add(sluit);
        knoppen.Controls.Add(_verversKnop);
        knoppen.Controls.Add(commitKnop);
        knoppen.Controls.Add(statusKnop);
        knoppen.Controls.Add(onlineKnop);
        knoppen.Controls.Add(nuOnlineKnop);
        knoppen.Controls.Add(genegeerdKnop);
        CancelButton = sluit;

        Controls.Add(_lijst);
        Controls.Add(_status);
        Controls.Add(knoppen);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "gitoverzicht");

        GitRadar.Bijgewerkt += OpRadarBijgewerkt;
        FormClosed += (_, _) =>
        {
            GitRadar.Bijgewerkt -= OpRadarBijgewerkt;
            _cts.Cancel();
            _cts.Dispose();
        };

        Vul();
        Shown += async (_, _) =>
        {
            // Nooit met een verouderd beeld beginnen, maar ook niet nutteloos opnieuw peilen:
            // is de laatste ronde jonger dan een uur, dan staat de tabel al goed.
            if (GitRadar.Verouderd)
            {
                await ScanAsync();
            }
        };
    }

    private static ToolStripMenuItem Item(string label, Action doe)
    {
        var it = new ToolStripMenuItem(label);
        it.Click += (_, _) => doe();
        return it;
    }

    private void OpRadarBijgewerkt()
    {
        if (!IsDisposed && IsHandleCreated)
        {
            BeginInvoke(Vul);
        }
    }

    /// <summary>De map van de geselecteerde regel, of null als er niets gekozen is.</summary>
    private string? Gekozen() =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as string : null;

    private void Vul()
    {
        var gekozen = Gekozen();
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        foreach (var (map, stand) in Rijen())
        {
            var genegeerd = GitRadar.Cache.Genegeerd.Contains(map, StringComparer.OrdinalIgnoreCase);
            var item = new ListViewItem(GitRadar.Naam(map) + (genegeerd ? " (genegeerd)" : ""))
            {
                Tag = map,
            };
            if (stand is null)
            {
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("nog niet");
                item.ForeColor = Theme.Muted;
                _lijst.Items.Add(item);
                continue;
            }
            item.SubItems.Add(stand.Fout.Length > 0 ? "git?" : stand.Branch);
            item.SubItems.Add(stand.Aantal == 0 ? "—" : stand.Aantal.ToString());
            item.SubItems.Add(stand.Staged == 0 ? "" : stand.Staged.ToString());
            item.SubItems.Add(Remote(stand));
            item.SubItems.Add(stand.Aantal == 0 ? "" : Ouderdom(stand.OudsteDagen));
            item.SubItems.Add(Moment(stand.Moment));
            item.ToolTipText = stand.Fout.Length > 0
                ? stand.Fout
                : string.Join(Environment.NewLine, stand.Bestanden.Take(12).Select(b => b.Pad)) +
                  (stand.Bestanden.Count > 12 ? $"{Environment.NewLine}… en {stand.Bestanden.Count - 12} meer" : "");
            // Kleur alleen waar het ergens over gaat: rood als er al meer dan een week werk
            // ligt, oranje bij een achterstand op de remote, grijs voor een schone repo.
            item.ForeColor = stand.Fout.Length > 0 ? Theme.Muted
                : stand.Aantal > 0 && stand.OudsteDagen >= 7 ? Theme.Danger
                : stand.Achter > 0 ? Theme.Accent
                : stand.Aantal > 0 ? Theme.Text
                : Theme.Muted;
            _lijst.Items.Add(item);
        }
        _lijst.EndUpdate();
        if (gekozen is not null)
        {
            foreach (ListViewItem item in _lijst.Items)
            {
                if (string.Equals(item.Tag as string, gekozen, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    break;
                }
            }
        }
        WerkStatusBij();
    }

    /// <summary>De regels van de tabel: gevolgde repo's, en op verzoek ook de genegeerde.</summary>
    private List<(string Map, GitStatusCache.Stand? Stand)> Rijen()
    {
        var rijen = GitRadar.Standen()
            .Select(p => (p.Key, (GitStatusCache.Stand?)p.Value))
            .ToList();
        // Repo's die nog nooit gepeild zijn (vers gekloond, of de eerste start) horen er ook
        // bij — anders lijkt het alsof ze niet bestaan.
        rijen.AddRange(GitRadar.Repos
            .Where(m => !GitRadar.Cache.PerMap.ContainsKey(m))
            .Select(m => (m, (GitStatusCache.Stand?)null)));
        if (_toonGenegeerd)
        {
            rijen.AddRange(GitRadar.Cache.Genegeerd
                .Select(m => (m, (GitStatusCache.Stand?)null)));
        }
        return rijen;
    }

    private static string Remote(GitStatusCache.Stand stand) =>
        stand.Voor == 0 && stand.Achter == 0
            ? (stand.Fout.Length > 0 ? "" : "gelijk")
            : (stand.Voor > 0 ? $"⬆ {stand.Voor}" : "") +
              (stand.Voor > 0 && stand.Achter > 0 ? "  " : "") +
              (stand.Achter > 0 ? $"⬇ {stand.Achter}" : "");

    private static string Ouderdom(int dagen) => dagen switch
    {
        < 0 => "",
        0 => "vandaag",
        1 => "1 dag",
        < 14 => $"{dagen} dagen",
        < 60 => $"{dagen / 7} weken",
        _ => $"{dagen / 30} maanden",
    };

    private static string Moment(DateTimeOffset moment)
    {
        if (moment == default)
        {
            return "nog niet";
        }
        var lokaal = moment.LocalDateTime;
        return lokaal.Date == DateTime.Now.Date ? lokaal.ToString("HH:mm") : lokaal.ToString("d MMM HH:mm");
    }

    private void WerkStatusBij()
    {
        var online = GitWebSettings.Load();
        var onlineTekst = online.Compleet
            ? " · online: " + (GitWebSync.LaatsteUpload is { } u
                ? $"{Moment(u)}"
                : "nog niet deze sessie")
            : " · online staat uit";
        var ronde = GitRadar.Cache.LaatsteControle == default
            ? "nog geen ronde"
            : $"ronde van {Moment(GitRadar.Cache.LaatsteControle)}";
        _status.Text =
            $"{GitRadar.TotaalOngecommit} ongecommit over {GitRadar.ProjectenMetWerk} van " +
            $"{GitRadar.Repos.Count} projecten · {GitRadar.ProjectenAchter} achter op de remote · " +
            ronde + onlineTekst;
    }

    private async Task ScanAsync()
    {
        _verversKnop.Bezig = true;
        _verversKnop.Enabled = false;
        try
        {
            await GitRadar.ScanAsync(_cts.Token, naam =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() => _status.Text = $"Peilen… {naam}");
                }
            });
            Vul();
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het peilen.
        }
        catch (Exception ex)
        {
            _status.Text = $"Peilen mislukt: {ex.Message}";
        }
        finally
        {
            _verversKnop.Bezig = false;
            _verversKnop.Enabled = true;
        }
    }

    private async Task NuOnlineAsync()
    {
        var settings = GitWebSettings.Load();
        if (!settings.Compleet)
        {
            Toast.Toon(this, "Stel eerst de online tabel in", Fluent.Globe);
            new GitWebForm().Show(this);
            return;
        }
        try
        {
            await new GitWebSync().PollAsync(forceren: true);
            WerkStatusBij();
            Toast.Toon(this, "Stand online gezet", Fluent.Globe);
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Online zetten mislukt", ex.Message);
        }
    }

    private void OpenStatus()
    {
        if (Gekozen() is not { } map)
        {
            Toast.Toon(this, "Kies eerst een project", Fluent.Lijst);
            return;
        }
        using var form = new GitStatusForm(map, GitRadar.Naam(map));
        form.ShowDialog(this);
        Vul();
    }

    private void OpenCommit()
    {
        if (Gekozen() is not { } map)
        {
            Toast.Toon(this, "Kies eerst een project", Fluent.Lijst);
            return;
        }
        using var form = new GitCommitForm(map, GitRadar.Naam(map));
        form.ShowDialog(this);
        Vul();
    }

    /// <summary>
    /// Pullt de gekozen repo fast-forward. Lopen lokaal en remote uiteen, dan gebeurt er
    /// bewust niets: zulke gevallen hoor je zelf te bekijken, niet blind te laten mergen.
    /// </summary>
    private async Task PullAsync()
    {
        if (Gekozen() is not { } map)
        {
            Toast.Toon(this, "Kies eerst een project", Fluent.Lijst);
            return;
        }
        _status.Text = $"Pullen… {GitRadar.Naam(map)}";
        try
        {
            var (uit, fout, code) = await GitStatus.GitAsync(map, "pull --ff-only", _cts.Token);
            if (code == 0)
            {
                Toast.Toon(this, $"{GitRadar.Naam(map)} bijgewerkt", Fluent.Check);
                await GitRadar.VerversAsync(map, _cts.Token, fetchen: true);
            }
            else
            {
                Toast.Fout(this, "Pull niet gelukt", (fout.Length > 0 ? fout : uit).Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het pullen.
        }
        finally
        {
            WerkStatusBij();
        }
    }

    private void Start(Action<string> wat)
    {
        if (Gekozen() is not { } map)
        {
            Toast.Toon(this, "Kies eerst een project", Fluent.Lijst);
            return;
        }
        try
        {
            wat(map);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Starten mislukt: {ex.Message}", Fluent.Globe);
        }
    }

    private void Negeer()
    {
        if (Gekozen() is not { } map)
        {
            return;
        }
        if (GitRadar.Cache.Genegeerd.Contains(map, StringComparer.OrdinalIgnoreCase))
        {
            GitRadar.VolgWeer(map);
            Toast.Toon(this, $"{GitRadar.Naam(map)} wordt weer gevolgd", Fluent.Check);
        }
        else
        {
            GitRadar.Negeer(map);
            Toast.ToonUndo(this, $"{GitRadar.Naam(map)} niet meer volgen",
                () => GitRadar.VolgWeer(map), Fluent.Delete);
        }
        Vul();
    }
}
