using System.Collections.Specialized;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Kleine opruimhulp voor het bureaublad: toont de losse bestanden (foto's, pdf's, documenten),
/// laat ze rechts voorbekijken (WebView2 rendert zowel afbeeldingen als pdf's), en biedt per
/// bestand een Claude-suggestie ("verwijderen / archiveren waar / bewaren"). Je kunt een bestand
/// naar de prullenbak sturen of naar het klembord knippen (verplaats-modus) om het elders te
/// plakken.
/// </summary>
public sealed class BureaubladCleanerForm : Form
{
    private readonly ModernListView _lijst;
    private readonly WebView2 _preview = new() { Dock = DockStyle.Fill };
    private readonly Label _suggestie;
    private readonly CancellationTokenSource _cts = new();
    private int _sorteer; // 0 = datum (nieuwste eerst), 1 = naam, 2 = type

    private static readonly string Bureaublad =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public BureaubladCleanerForm()
    {
        Text = "Bureaublad opruimen";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1150, 760);

        _lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.None,
            MultiSelect = true, // Shift/Ctrl+klik: acties op meerdere bestanden tegelijk
        };
        _lijst.Columns.Add("", 360);
        _lijst.Resize += (_, _) => _lijst.Columns[0].Width = Math.Max(200, _lijst.ClientSize.Width - 4);
        _lijst.SelectedIndexChanged += (_, _) => ToonSelectie();
        // Dubbelklik (of Enter) start het bestand in zijn eigen programma — Word, PowerPoint,
        // Excel… Handig voor alles waarvan het voorbeeld alleen de platte tekst kan tonen.
        _lijst.DoubleClick += (_, _) => OpenGeselecteerde();
        _lijst.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenGeselecteerde();
            }
        };
        var lijstGroep = new ModernGroupBox
        {
            Text = "Bestanden op je bureaublad", Dock = DockStyle.Left, Width = 400,
            Padding = new Padding(10, 8, 10, 10),
        };
        // Sorteerkeuze bovenaan de lijst: datum, naam of bestandstype (met groepskopjes).
        var sorteerKnop = new ModernButton { Text = "Sorteren: datum ▾", Dock = DockStyle.Top };
        var sorteerMenu = new ContextMenuStrip();
        Theme.Style(sorteerMenu);
        foreach (var (label, stand) in new[]
        {
            ("Datum (nieuwste eerst)", 0), ("Naam", 1), ("Bestandstype", 2),
        })
        {
            var mi = new ToolStripMenuItem(label);
            mi.Click += (_, _) =>
            {
                _sorteer = stand;
                sorteerKnop.Text = stand switch
                {
                    1 => "Sorteren: naam ▾",
                    2 => "Sorteren: type ▾",
                    _ => "Sorteren: datum ▾",
                };
                VulLijst();
            };
            sorteerMenu.Items.Add(mi);
        }
        sorteerKnop.Click += (_, _) => sorteerMenu.Show(sorteerKnop, new Point(0, sorteerKnop.Height + 4));
        lijstGroep.Controls.Add(_lijst);
        lijstGroep.Controls.Add(sorteerKnop);

        _suggestie = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(10, 8, 10, 0),
            Text = "Selecteer een bestand voor een voorbeeld en een Claude-suggestie.",
        };

        var knoppen = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, Height = 52,
            Padding = new Padding(10),
        };
        var verwijderKnop = new ModernButton { Text = "🗑 Naar prullenbak", Width = 170, Kind = ButtonKind.Accent };
        verwijderKnop.Click += (_, _) => VerwijderGeselecteerde();
        var knipKnop = new ModernButton { Text = "✂ Knippen (klembord)", Width = 180 };
        knipKnop.Click += (_, _) => KnipGeselecteerde();
        // Kopiëren naar een voorkeurs-Drive-map: de lokale "Drive voor desktop"-spiegel
        // (G:\Mijn Drive) synct het bestand daarna vanzelf naar Google Drive.
        var driveMenu = new ContextMenuStrip();
        Theme.Style(driveMenu);
        foreach (var (label, map) in DriveMappen)
        {
            var mi = new ToolStripMenuItem(label);
            mi.Click += (_, _) => KopieerNaarDrive(label, map);
            driveMenu.Items.Add(mi);
        }
        var driveKnop = new ModernButton { Text = "📁 Naar Drive ▾", Width = 150 };
        driveKnop.Click += (_, _) => driveMenu.Show(driveKnop, new Point(0, driveKnop.Height + 4));
        var classeerKnop = new ModernButton { Text = "🤖 Auto-classeren", Width = 165 };
        classeerKnop.Click += async (_, _) =>
        {
            classeerKnop.Bezig = true;
            classeerKnop.Enabled = false;
            try
            {
                await AutoClasseerAsync();
            }
            finally
            {
                classeerKnop.Bezig = false;
                classeerKnop.Enabled = true;
            }
        };
        var suggestieKnop = new ModernButton { Text = "💡 Claude-suggestie", Width = 175 };
        suggestieKnop.Click += async (_, _) => await VraagSuggestieAsync();
        var openKnop = new ModernButton { Text = "Openen", Width = 110 };
        openKnop.Click += (_, _) => OpenGeselecteerde();
        var verversKnop = new ModernButton { Text = "Verversen", Width = 120 };
        verversKnop.Click += (_, _) => VulLijst();
        knoppen.Controls.AddRange(new Control[]
        {
            verwijderKnop, knipKnop, driveKnop, classeerKnop, suggestieKnop, openKnop, verversKnop,
        });

        var previewGroep = new ModernGroupBox
        {
            Text = "Voorbeeld", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        previewGroep.Controls.Add(_preview);
        previewGroep.Controls.Add(_suggestie);
        previewGroep.Controls.Add(knoppen);

        Controls.Add(previewGroep);
        Controls.Add(lijstGroep);
        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await InitAsync();
        Theme.Apply(this, fade: false);
        _preview.DefaultBackgroundColor = Theme.Bg;
        _suggestie.ForeColor = Theme.Muted;
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WorkManager", "webview2-cleaner"));
            await _preview.EnsureCoreWebView2Async(env);
        }
        catch
        {
            // Zonder WebView blijft alleen de lijst + acties werken.
        }
        VulLijst();
    }

    private void VulLijst()
    {
        _lijst.BeginUpdate();
        _lijst.Items.Clear();
        try
        {
            var bestanden = Directory.EnumerateFiles(Bureaublad)
                .Where(p => !Path.GetFileName(p).StartsWith('.'));
            bestanden = _sorteer switch
            {
                1 => bestanden.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
                2 => bestanden.OrderBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
                _ => bestanden.OrderByDescending(p => new FileInfo(p).LastWriteTime),
            };
            string? vorigeExt = null;
            foreach (var pad in bestanden)
            {
                var info = new FileInfo(pad);
                // Bij type-sortering een groepskopje per bestandstype (Tag = null → geen
                // bestand, doet niet mee met selecteren/acties).
                if (_sorteer == 2)
                {
                    var ext = info.Extension.ToLowerInvariant();
                    if (ext != vorigeExt)
                    {
                        var kop = new ListViewItem(
                            (ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : "OVERIG"))
                        {
                            ForeColor = Theme.Muted,
                            Font = new Font(_lijst.Font, FontStyle.Bold),
                        };
                        _lijst.Items.Add(kop);
                        vorigeExt = ext;
                    }
                }
                var item = new ListViewItem($"{Icoon(info.Extension)}  {info.Name}   " +
                    $"({Grootte(info.Length)}, {info.LastWriteTime:d/M/yyyy})")
                {
                    Tag = pad,
                };
                _lijst.Items.Add(item);
            }
        }
        catch
        {
            // Bureaublad niet leesbaar.
        }
        _lijst.EndUpdate();
        var aantal = _lijst.Items.Cast<ListViewItem>().Count(i => i.Tag is string);
        _suggestie.Text = aantal == 0
            ? "Je bureaublad is leeg 🎉"
            : $"{aantal} bestand(en). Selecteer er één voor voorbeeld + Claude-suggestie.";
    }

    private string? Geselecteerd =>
        _lijst.SelectedItems.Count > 0 ? _lijst.SelectedItems[0].Tag as string : null;

    /// <summary>Alle geselecteerde bestandspaden (Shift/Ctrl+klik voor meerdere).</summary>
    private List<string> GeselecteerdeAlle => _lijst.SelectedItems.Cast<ListViewItem>()
        .Select(i => i.Tag as string).OfType<string>().ToList();

    /// <summary>Extensies die als platte tekst voorbekeken worden (code, config, logs…).</summary>
    private static readonly string[] TekstExtensies =
    {
        ".php", ".txt", ".md", ".cs", ".js", ".ts", ".json", ".xml", ".sql", ".log",
        ".ini", ".csv", ".bat", ".ps1", ".sh", ".yml", ".yaml", ".css",
    };

    private void ToonSelectie()
    {
        _suggestie.Text = _lijst.SelectedItems.Count > 1
            ? $"{_lijst.SelectedItems.Count} bestanden geselecteerd — acties gelden voor allemaal."
            : "";
        if (Geselecteerd is not { } pad || _preview.CoreWebView2 is not { } core)
        {
            return;
        }
        try
        {
            var ext = Path.GetExtension(pad).ToLowerInvariant();
            if (ext is ".docx" or ".docm")
            {
                core.NavigateToString(PreHtml(DocxTekst(pad)));
                _suggestie.Text = "Alleen de tekst — dubbelklik opent het in Word.";
            }
            else if (ext is ".xlsx" or ".xlsm")
            {
                // .xlsm is dezelfde zip-structuur als .xlsx, alleen met macro's erbij.
                core.NavigateToString(XlsxHtml(pad));
                _suggestie.Text = "Alleen de waarden — dubbelklik opent het in Excel.";
            }
            else if (ext is ".pptx" or ".pptm")
            {
                core.NavigateToString(PreHtml(PptxTekst(pad)));
                _suggestie.Text = "Alleen de tekst per slide — dubbelklik opent het in PowerPoint.";
            }
            else if (TekstExtensies.Contains(ext))
            {
                core.NavigateToString(PreHtml(LeesTekst(pad)));
            }
            else if (WebExtensies.Contains(ext))
            {
                // WebView2 rendert afbeeldingen, pdf's en html rechtstreeks vanaf het pad.
                core.Navigate(new Uri(pad).AbsoluteUri);
            }
            else
            {
                // Alles wat de webview niet kan tonen (.exe, .zip, .msg, .dwg, oude .doc/.xls…):
                // geen voorbeeld forceren, maar meteen aanbieden het te starten.
                core.NavigateToString(GeenVoorbeeldHtml(pad));
                _suggestie.Text = "Geen voorbeeld mogelijk — dubbelklik of Enter start het bestand.";
            }
        }
        catch
        {
            core.NavigateToString(GeenVoorbeeldHtml(pad));
            _suggestie.Text = "Voorbeeld mislukt — dubbelklik of Enter start het bestand.";
        }
    }

    /// <summary>Bestandstypes die WebView2 zelf kan tonen.</summary>
    private static readonly HashSet<string> WebExtensies = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".htm", ".html", ".mp4", ".webm", ".mp3", ".wav",
    };

    /// <summary>Kaartje in het voorbeeldvak voor bestanden die niet te tonen zijn.</summary>
    private static string GeenVoorbeeldHtml(string pad)
    {
        var info = new FileInfo(pad);
        var grootte = info.Length >= 1024 * 1024
            ? $"{info.Length / 1024d / 1024d:0.#} MB"
            : $"{Math.Max(1, info.Length / 1024)} kB";
        var ext = Path.GetExtension(pad).TrimStart('.').ToUpperInvariant();
        return "<!doctype html><html><head><meta charset=\"utf-8\"></head>" +
               "<body style=\"margin:0;background:#15151c;display:flex;align-items:center;" +
               "justify-content:center;height:100vh;font-family:'Segoe UI',Arial,sans-serif\">" +
               "<div style=\"text-align:center;color:#eaeaf2\">" +
               $"<div style=\"font-size:44px;font-weight:600;color:#8b8b9e\">{System.Net.WebUtility.HtmlEncode(ext)}</div>" +
               $"<div style=\"font-size:15px;margin-top:10px\">{System.Net.WebUtility.HtmlEncode(Path.GetFileName(pad))}</div>" +
               $"<div style=\"font-size:12.5px;color:#8b8b9e;margin-top:6px\">{grootte} · " +
               $"{info.LastWriteTime:ddd d MMM yyyy HH:mm}</div>" +
               "<div style=\"font-size:12.5px;color:#8b8b9e;margin-top:18px\">" +
               "Geen voorbeeld beschikbaar — dubbelklik om te openen</div>" +
               "</div></body></html>";
    }

    /// <summary>Donkere pagina met de tekst in een leesbaar monospace-blok.</summary>
    private static string PreHtml(string tekst) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"></head>" +
        "<body style=\"margin:0;background:#15151c;padding:14px\">" +
        "<pre style=\"white-space:pre-wrap;word-break:break-word;color:#eaeaf2;" +
        "font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;margin:0\">" +
        System.Net.WebUtility.HtmlEncode(tekst) + "</pre></body></html>";

    private static string LeesTekst(string pad)
    {
        var tekst = File.ReadAllText(pad);
        return tekst.Length > 200_000 ? tekst[..200_000] + "\n\n[… ingekort …]" : tekst;
    }

    /// <summary>Haalt de platte tekst uit een .docx (word/document.xml, alinea's op eigen regel).</summary>
    private static string DocxTekst(string pad)
    {
        using var zip = ZipFile.OpenRead(pad);
        if (zip.GetEntry("word/document.xml") is not { } entry)
        {
            return "(Geen document.xml gevonden in dit docx-bestand.)";
        }
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        xml = System.Text.RegularExpressions.Regex.Replace(xml, @"<w:(p|br)\b[^>]*/?>", "\n");
        xml = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", "");
        var tekst = System.Net.WebUtility.HtmlDecode(xml).Trim();
        return tekst.Length > 200_000 ? tekst[..200_000] + "\n\n[… ingekort …]" : tekst;
    }

    /// <summary>Haalt de tekst uit een .pptx: per slide een kopje met de tekstregels eronder.</summary>
    private static string PptxTekst(string pad)
    {
        using var zip = ZipFile.OpenRead(pad);
        // Slides numeriek sorteren (slide10 hoort ná slide2).
        var slides = zip.Entries
            .Select(e => (Entry: e, Match: System.Text.RegularExpressions.Regex.Match(
                e.FullName, @"^ppt/slides/slide(\d+)\.xml$")))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .ToList();
        if (slides.Count == 0)
        {
            return "(Geen slides gevonden in dit pptx-bestand.)";
        }
        var sb = new System.Text.StringBuilder();
        foreach (var (entry, match) in slides)
        {
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            // Alinea-einden naar regels, overige tags weg — zelfde aanpak als bij docx.
            xml = System.Text.RegularExpressions.Regex.Replace(xml, @"</a:p>", "\n");
            xml = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", "");
            var tekst = System.Net.WebUtility.HtmlDecode(xml).Trim();
            sb.AppendLine($"═══ Slide {match.Groups[1].Value} ═══");
            sb.AppendLine(tekst.Length > 0 ? tekst : "(geen tekst)");
            sb.AppendLine();
            if (sb.Length > 200_000)
            {
                sb.AppendLine("[… ingekort …]");
                break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Rendert het eerste werkblad van een .xlsx als eenvoudige HTML-tabel (max 200 rijen).</summary>
    private static string XlsxHtml(string pad)
    {
        using var zip = ZipFile.OpenRead(pad);

        // Gedeelde tekstwaarden (t="s"-cellen verwijzen hiernaar op index).
        var gedeeld = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
        {
            var ssDoc = XDocument.Load(ss.Open());
            var ns = ssDoc.Root!.GetDefaultNamespace();
            gedeeld = ssDoc.Root.Elements(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
                .ToList();
        }

        var blad = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (blad is null)
        {
            return PreHtml("(Geen werkblad gevonden in dit xlsx-bestand.)");
        }
        var doc = XDocument.Load(blad.Open());
        var wns = doc.Root!.GetDefaultNamespace();

        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"></head>")
          .Append("<body style=\"margin:0;background:#15151c;padding:14px;color:#eaeaf2;")
          .Append("font-family:'Segoe UI',sans-serif;font-size:12.5px\">")
          .Append("<table style=\"border-collapse:collapse\">");
        var rijen = 0;
        foreach (var rij in doc.Descendants(wns + "row"))
        {
            if (++rijen > 200)
            {
                sb.Append("<tr><td style=\"padding:6px;color:#98989f\">[… ingekort …]</td></tr>");
                break;
            }
            sb.Append("<tr>");
            foreach (var cel in rij.Elements(wns + "c"))
            {
                var waarde = cel.Element(wns + "v")?.Value ?? "";
                if (cel.Attribute("t")?.Value == "s" &&
                    int.TryParse(waarde, out var idx) && idx >= 0 && idx < gedeeld.Count)
                {
                    waarde = gedeeld[idx];
                }
                else if (cel.Attribute("t")?.Value == "inlineStr")
                {
                    waarde = string.Concat(cel.Descendants(wns + "t").Select(t => t.Value));
                }
                sb.Append("<td style=\"border:1px solid #34343f;padding:4px 8px\">")
                  .Append(System.Net.WebUtility.HtmlEncode(waarde))
                  .Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table></body></html>");
        var html = sb.ToString();
        return html.Length < 1_500_000 ? html : PreHtml("(Werkblad te groot voor voorbeeld — gebruik 'Openen'.)");
    }

    private void OpenGeselecteerde()
    {
        if (Geselecteerd is not { } pad)
        {
            return;
        }
        if (!File.Exists(pad))
        {
            Toast.Toon(this, "Bestand bestaat niet meer — ververs de lijst", Fluent.Globe);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(pad) { UseShellExecute = true });
            Toast.Toon(this, $"Geopend: {Path.GetFileName(pad)}", Fluent.Globe);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Geen programma gekoppeld aan dit type: Windows laten vragen waarmee te openen.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe",
                    $"shell32.dll,OpenAs_RunDLL {pad}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Toast.Toon(this, $"Openen mislukt: {ex.Message}", Fluent.Globe);
            }
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Openen mislukt: {ex.Message}", Fluent.Globe);
        }
    }

    private void VerwijderGeselecteerde()
    {
        var paden = GeselecteerdeAlle;
        if (paden.Count == 0)
        {
            return;
        }
        var idx = _lijst.SelectedIndices.Count > 0 ? _lijst.SelectedIndices[0] : -1;
        var verwijderd = 0;
        foreach (var pad in paden)
        {
            try
            {
                // Naar de prullenbak (veilig terug te halen), geen definitieve verwijdering.
                FileSystem.DeleteFile(pad, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                verwijderd++;
            }
            catch
            {
                // In gebruik of geen rechten: dit bestand overslaan, de rest gewoon doen.
            }
        }
        VulLijst();
        if (idx >= 0 && _lijst.Items.Count > 0)
        {
            _lijst.Items[Math.Min(idx, _lijst.Items.Count - 1)].Selected = true;
        }
        Toast.Toon(this, verwijderd == 1
            ? "Naar de prullenbak"
            : $"{verwijderd} bestanden naar de prullenbak", Fluent.Archive);
    }

    private void KnipGeselecteerde()
    {
        var paden = GeselecteerdeAlle;
        if (paden.Count == 0)
        {
            return;
        }
        try
        {
            var data = new DataObject();
            var bestanden = new StringCollection();
            bestanden.AddRange(paden.ToArray());
            data.SetFileDropList(bestanden);
            // "Preferred DropEffect" = Move (2): plakken in Verkenner verplaatst de bestanden.
            using var ms = new MemoryStream(new byte[] { 2, 0, 0, 0 });
            data.SetData("Preferred DropEffect", ms);
            Clipboard.SetDataObject(data, copy: true);
            Toast.Toon(this, paden.Count == 1
                ? "Geknipt — plak het met Ctrl+V in de doelmap"
                : $"{paden.Count} bestanden geknipt — plak ze met Ctrl+V in de doelmap", Fluent.Copy);
        }
        catch (Exception ex)
        {
            Toast.Toon(this, $"Knippen mislukt: {ex.Message}", Fluent.Copy);
        }
    }

    private async Task VraagSuggestieAsync()
    {
        if (Geselecteerd is not { } pad)
        {
            return;
        }
        _suggestie.Text = "Claude denkt na…";
        try
        {
            var naam = Path.GetFileName(pad);
            var prompt =
                $"""
                Dit bestand staat op het bureaublad van Maarten: "{naam}".
                Geef in één korte Nederlandse zin een concreet advies: weggooien, of archiveren (en
                zo ja waar/in welke map), of bewaren. Geen uitleg, alleen die ene zin.
                """;
            var advies = (await ClaudeDrafter.RunClaudeAsync(prompt, _cts.Token)).Trim();
            _suggestie.Text = advies.Length > 0 ? "💡 " + advies : "Geen suggestie ontvangen.";
        }
        catch (Exception ex)
        {
            _suggestie.Text = $"Suggestie mislukt: {ex.Message}";
        }
    }

    /// <summary>Voorkeurs-Drive-mappen (lokale "Drive voor desktop"-paden; sync uploadt vanzelf).</summary>
    private static readonly (string Label, string Map)[] DriveMappen =
    {
        ("Maarten 2026", @"G:\Mijn Drive\administratie\maarten\2026"),
        ("Hilke 2026", @"G:\Mijn Drive\administratie\hilke\2026"),
        ("Lisa 2026", @"G:\Mijn Drive\administratie\lisa\2026"),
        ("Emilia 2026", @"G:\Mijn Drive\administratie\emilia\2026"),
        ("Bermacon", @"G:\Mijn Drive\UrbanIT\Aqurat\UrbanIT - Bermacon - Vianext\bermacon"),
        ("Urbanit", @"G:\Mijn Drive\UrbanIT\Aqurat\UrbanIT - Bermacon - Vianext\urbanit"),
    };

    private void KopieerNaarDrive(string label, string map)
    {
        var paden = GeselecteerdeAlle;
        if (paden.Count == 0)
        {
            return;
        }
        if (!Directory.Exists(map))
        {
            Toast.Toon(this, $"Drive-map niet gevonden ({map}) — draait Drive voor desktop?", Fluent.Globe);
            return;
        }
        var verplaatst = 0;
        foreach (var pad in paden)
        {
            try
            {
                if (VerplaatsNaarDrive(pad, map))
                {
                    verplaatst++;
                }
            }
            catch
            {
                // Dit bestand overslaan; de rest gewoon doen.
            }
        }
        VulLijst(); // verplaatste bestanden zijn weg van het bureaublad
        Toast.Toon(this, verplaatst == 1
            ? $"Verplaatst naar Drive · {label}"
            : $"{verplaatst} bestanden verplaatst naar Drive · {label}", Fluent.Copy);
    }

    /// <summary>
    /// Verplaatst een bestand echt naar de Drive-map: eerst kopiëren, en pas als de kopie er
    /// aantoonbaar staat gaat het origineel naar de prullenbak (vangnet — nooit dataverlies
    /// bij een haperende Drive-sync). Geeft false als de doelmap niet bestaat.
    /// </summary>
    private static bool VerplaatsNaarDrive(string pad, string map)
    {
        if (!Directory.Exists(map))
        {
            return false;
        }
        var doel = Path.Combine(map, Path.GetFileName(pad));
        // Nooit stil overschrijven: bij een naamsbotsing een volgnummer toevoegen.
        for (var i = 2; File.Exists(doel); i++)
        {
            doel = Path.Combine(map,
                $"{Path.GetFileNameWithoutExtension(pad)} ({i}){Path.GetExtension(pad)}");
        }
        File.Copy(pad, doel);
        if (new FileInfo(doel).Length == new FileInfo(pad).Length)
        {
            FileSystem.DeleteFile(pad, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        return true;
    }

    /// <summary>
    /// Laat Claude alle bureaubladbestanden in één keer classeren naar de voorkeurs-Drive-mappen
    /// (of "laten staan"), toont het voorstel ter bevestiging (aanvinkbaar) en kopieert daarna
    /// de aangevinkte bestanden naar hun map.
    /// </summary>
    private async Task AutoClasseerAsync()
    {
        var bestanden = _lijst.Items.Cast<ListViewItem>()
            .Select(i => i.Tag as string).OfType<string>().ToList();
        if (bestanden.Count == 0)
        {
            Toast.Toon(this, "Niets te classeren", Fluent.Check);
            return;
        }
        _suggestie.Text = "Claude classeert de bestanden…";
        var labels = string.Join(", ", DriveMappen.Select(d => $"\"{d.Label}\""));
        var lijst = string.Join("\n", bestanden.Select((p, i) => $"{i}: {Path.GetFileName(p)}"));
        List<(string Pad, string Doel)> voorstel;
        try
        {
            var prompt =
                $$"""
                Dit zijn losse bestanden op het bureaublad van Maarten. Kies per bestand de beste
                bestemming uit deze Google Drive-mappen: {{labels}} — of "laten" als het bestand
                nergens duidelijk bij hoort. Richtlijnen: administratieve documenten over een
                gezinslid horen bij die persoon (Maarten/Hilke/Lisa/Emilia), facturen/boekhouding
                van de zaak bij "Bermacon" of "Urbanit".

                Antwoord UITSLUITEND met één JSON-array, zonder verdere tekst of markdown:
                [{"i": 0, "doel": "Maarten 2026"}, …]

                Bestanden:
                {{lijst}}
                """;
            var output = await ClaudeDrafter.RunClaudeAsync(prompt, _cts.Token);
            using var doc = ClaudeDrafter.ParseJson(output);
            voorstel = doc.RootElement.EnumerateArray()
                .Select(e => (
                    Index: e.TryGetProperty("i", out var i) ? i.GetInt32() : -1,
                    Doel: e.TryGetProperty("doel", out var d) ? d.GetString() ?? "laten" : "laten"))
                .Where(x => x.Index >= 0 && x.Index < bestanden.Count &&
                    !x.Doel.Equals("laten", StringComparison.OrdinalIgnoreCase) &&
                    DriveMappen.Any(m => m.Label.Equals(x.Doel, StringComparison.OrdinalIgnoreCase)))
                .Select(x => (bestanden[x.Index], x.Doel))
                .ToList();
        }
        catch (Exception ex)
        {
            _suggestie.Text = $"Classeren mislukt: {ex.Message}";
            return;
        }
        if (voorstel.Count == 0)
        {
            _suggestie.Text = "Claude vond geen bestanden die duidelijk ergens thuishoren.";
            return;
        }

        // Bevestiging: aanvinkbare lijst "bestand → map"; alleen aangevinkte worden gekopieerd.
        using var dialog = new Form
        {
            Text = "Voorstel — verplaatsen naar Drive",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(560, 420),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var keuzes = new CheckedListBox
        {
            Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        };
        foreach (var (pad, doel) in voorstel)
        {
            keuzes.Items.Add($"{Path.GetFileName(pad)}   →   {doel}", isChecked: true);
        }
        var knoppenPaneel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52,
            Padding = new Padding(10),
        };
        var okKnop = new ModernButton
        {
            Text = "Verplaatsen", Width = 130, Kind = ButtonKind.Accent,
            DialogResult = DialogResult.OK, Glyph = Fluent.Copy,
        };
        var cancelKnop = new ModernButton { Text = "Annuleren", Width = 100, DialogResult = DialogResult.Cancel };
        knoppenPaneel.Controls.Add(okKnop);
        knoppenPaneel.Controls.Add(cancelKnop);
        dialog.Controls.Add(keuzes);
        dialog.Controls.Add(knoppenPaneel);
        dialog.AcceptButton = okKnop;
        dialog.CancelButton = cancelKnop;
        Theme.Apply(dialog);
        _suggestie.Text = $"{voorstel.Count} voorstel(len) — kijk na en bevestig.";
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var verplaatst = 0;
        for (var i = 0; i < voorstel.Count; i++)
        {
            if (!keuzes.GetItemChecked(i))
            {
                continue;
            }
            var (pad, doel) = voorstel[i];
            var map = DriveMappen.First(m => m.Label.Equals(doel, StringComparison.OrdinalIgnoreCase)).Map;
            try
            {
                if (VerplaatsNaarDrive(pad, map))
                {
                    verplaatst++;
                }
            }
            catch
            {
                // Dit bestand overslaan; de rest gewoon doen.
            }
        }
        VulLijst(); // verplaatste bestanden zijn weg van het bureaublad
        _suggestie.Text = $"{verplaatst} bestand(en) naar Drive verplaatst.";
        Toast.Toon(this, $"{verplaatst} bestand(en) naar Drive verplaatst", Fluent.Copy);
    }

    private static string Icoon(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" => "🖼",
        ".pdf" => "📕",
        ".doc" or ".docx" or ".txt" or ".rtf" or ".odt" => "📄",
        ".xls" or ".xlsx" or ".csv" => "📊",
        ".ppt" or ".pptx" => "📽",
        ".php" or ".cs" or ".js" or ".ts" or ".sql" or ".ps1" or ".bat" or ".sh" => "📜",
        ".zip" or ".rar" or ".7z" => "🗜",
        ".exe" or ".msi" => "⚙",
        _ => "📎",
    };

    private static string Grootte(long bytes) => bytes switch
    {
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} kB",
        _ => $"{bytes} B",
    };
}
