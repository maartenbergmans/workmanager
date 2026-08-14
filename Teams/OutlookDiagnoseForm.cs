using System.Text;

namespace WorkManager;

/// <summary>
/// Diagnosevenster voor de Outlook-koppeling (CED). Laat in één rapport zien of de sessie
/// aangemeld is, welke selectors de OWA-pagina nog oplevert en hoeveel rijen de scrape
/// vindt — plus een schermafdruk van de verborgen pagina. Als "verversen doet niets", is
/// hiermee meteen duidelijk of het aan de dagelijkse MFA, aan een gewijzigde OWA-DOM of
/// gewoon aan een lege inbox ligt.
///
/// <para>Openen via de cockpit (rechtsklik op de Outlook-lamp) of
/// <c>WorkManager.exe --venster owadiag</c>.</para>
/// </summary>
public sealed class OutlookDiagnoseForm : Form
{
    private readonly TextBox _rapport;
    private readonly PictureBox _voorbeeld;
    private readonly ModernButton _start;
    private readonly CancellationTokenSource _cts = new();

    public OutlookDiagnoseForm()
    {
        Text = "Outlook-diagnose (CED)";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1020, 660);
        Theme.Apply(this);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "owadiag");

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _start = new ModernButton
        {
            Text = "Diagnose draaien", Width = 170, Kind = ButtonKind.Accent, Glyph = Fluent.Sync,
        };
        _start.Click += async (_, _) => await DraaiAsync();
        var herlaad = new ModernButton { Text = "Forceer volledige herlaadbeurt", Width = 250 };
        herlaad.Click += (_, _) =>
        {
            OutlookClient.Instance.ForceerHerlaad();
            Log("Volgende ophaalbeurt laadt de pagina vers.");
        };
        var verseSessie = new ModernButton { Text = "Sessie opnieuw opbouwen", Width = 215 };
        verseSessie.Click += (_, _) =>
        {
            OutlookClient.Instance.MarkeerVoorVerseStart();
            Log("Sessie wordt bij de volgende beurt vers opgebouwd (cookies blijven staan, " +
                "dus normaal geen nieuwe MFA).");
        };
        var kopieer = new ModernButton { Text = "Rapport kopiëren", Width = 165, Glyph = Fluent.Copy };
        kopieer.Click += (_, _) =>
        {
            if (_rapport.Text.Length > 0)
            {
                Clipboard.SetText(_rapport.Text);
                Toast.Toon(this, "Rapport gekopieerd", Fluent.Copy);
            }
        };
        toolbar.Controls.AddRange(new Control[] { _start, herlaad, verseSessie, kopieer });

        _rapport = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            Text = "Klik op \"Diagnose draaien\"." + Environment.NewLine,
        };
        _voorbeeld = new PictureBox
        {
            Dock = DockStyle.Right,
            Width = 340,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
        };

        Controls.Add(_rapport);
        Controls.Add(_voorbeeld);
        Controls.Add(toolbar);
        Padding = new Padding(12, 10, 12, 12);
    }

    private async Task DraaiAsync()
    {
        _start.Bezig = true;
        _start.Enabled = false;
        _rapport.Clear();
        Log($"— {DateTime.Now:HH:mm:ss} —");
        try
        {
            var uitslag = await OutlookClient.Instance.DiagnoseAsync(Log, _cts.Token);
            if (uitslag.Schermafdruk.Length > 0 && File.Exists(uitslag.Schermafdruk))
            {
                _voorbeeld.Image?.Dispose();
                // Via een kopie in het geheugen: anders houdt de PictureBox het bestand vast
                // en kan een volgende diagnose er niet overheen schrijven.
                _voorbeeld.Image = Image.FromStream(
                    new MemoryStream(File.ReadAllBytes(uitslag.Schermafdruk)));
            }
        }
        catch (Exception ex)
        {
            Log("Diagnose afgebroken: " + ex.Message);
        }
        finally
        {
            _start.Bezig = false;
            _start.Enabled = true;
        }
    }

    private void Log(string regel)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(regel));
            return;
        }
        _rapport.AppendText(regel + Environment.NewLine);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _voorbeeld.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
