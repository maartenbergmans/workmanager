using System.Text;

namespace WorkManager;

/// <summary>
/// Diagnosevenster voor de WhatsApp-koppeling: laat zien of de sessie ingelogd is, hoeveel
/// chatrijen de zijbalk oplevert, en — belangrijkst — wat er in een geopend gesprek aan
/// afbeeldingen te vinden is. Zo is bij een DOM-wijziging van WhatsApp meteen zichtbaar
/// wélke selector het niet meer doet, in plaats van dat foto's stil wegvallen.
///
/// <para>Er wordt bewust een chat <em>zonder</em> ongelezen berichten geopend: dan stuurt
/// WhatsApp geen leesbevestiging. Starten met: WorkManager.exe --venster wadiag</para>
/// </summary>
public sealed class WhatsAppDiagnoseForm : Form
{
    private readonly TextBox _rapport;
    private readonly PictureBox _voorbeeld;
    private readonly ModernButton _start;
    private readonly CancellationTokenSource _cts = new();

    public WhatsAppDiagnoseForm()
    {
        Text = "WhatsApp-diagnose";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 640);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top };
        Theme.AsToolbar(toolbar);
        _start = new ModernButton
        {
            Text = "Diagnose draaien", Width = 170, Kind = ButtonKind.Accent, Glyph = Fluent.Sync,
        };
        _start.Click += async (_, _) => await DraaiAsync();
        var kopieer = new ModernButton { Text = "Rapport kopiëren", Width = 160, Glyph = Fluent.Copy };
        kopieer.Click += (_, _) =>
        {
            if (_rapport.Text.Length > 0)
            {
                Clipboard.SetText(_rapport.Text);
                Toast.Toon(this, "Rapport gekopieerd", Fluent.Copy);
            }
        };
        toolbar.Controls.AddRange(new Control[] { _start, kopieer });

        _rapport = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = "Klik op \"Diagnose draaien\".",
        };
        _voorbeeld = new PictureBox
        {
            Dock = DockStyle.Right,
            Width = 300,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
        };

        Controls.Add(_rapport);
        Controls.Add(_voorbeeld);
        Controls.Add(toolbar);
        Theme.Apply(this, fade: false);
        Theme.EscSluit(this);
        VensterGeheugen.Volg(this, "wa-diagnose");
        FormClosing += (_, _) => _cts.Cancel();
    }

    private void Log(StringBuilder sb, string regel)
    {
        sb.AppendLine(regel);
        _rapport.Text = sb.ToString();
        _rapport.SelectionStart = _rapport.TextLength;
        _rapport.ScrollToCaret();
        Application.DoEvents();
    }

    private async Task DraaiAsync()
    {
        _start.Bezig = true;
        _start.Enabled = false;
        var sb = new StringBuilder();
        try
        {
            Log(sb, $"WhatsApp-diagnose — {DateTime.Now:dd/MM/yyyy HH:mm}");
            Log(sb, $"Ooit gekoppeld: {WhatsAppClient.OoitGekoppeld}");
            Log(sb, "Sessie starten…");

            var uitslag = await WhatsAppClient.Instance.DiagnoseAsync(
                r => Log(sb, r), _cts.Token);
            if (uitslag.Voorbeeld.Length > 0)
            {
                try
                {
                    var komma = uitslag.Voorbeeld.IndexOf(',');
                    var bytes = Convert.FromBase64String(uitslag.Voorbeeld[(komma + 1)..]);
                    using var stroom = new MemoryStream(bytes);
                    _voorbeeld.Image = Image.FromStream(stroom);
                    Log(sb, $"Voorbeeldfoto gedecodeerd: {_voorbeeld.Image.Width}×" +
                            $"{_voorbeeld.Image.Height} px, {bytes.Length / 1024} kB — " +
                            "hij staat rechts in beeld.");
                }
                catch (Exception ex)
                {
                    Log(sb, $"Voorbeeldfoto kon niet gedecodeerd worden: {ex.Message}");
                }
            }
            else
            {
                Log(sb, "Geen foto gevonden om te tonen (zie de tellingen hierboven).");
            }
        }
        catch (Exception ex)
        {
            Log(sb, $"FOUT: {ex.Message}");
        }
        finally
        {
            _start.Bezig = false;
            _start.Enabled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _voorbeeld.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
