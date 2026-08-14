namespace WorkManager;

/// <summary>
/// Snel beantwoorden vanuit de cockpit: origineel bericht boven, antwoord onder, met een
/// knop om Claude een concept te laten schrijven. Versturen gebruikt dezelfde kanalen als
/// het mailvenster (SMTP-reply, Google Chat API of WhatsApp Web); mails worden na het
/// versturen meteen gearchiveerd.
/// </summary>
public class SnelAntwoordForm : Form
{
    private readonly MailBericht _bericht;
    private readonly TextBox _antwoord;
    private readonly ModernButton _claudeButton;
    private readonly ModernButton _verstuurButton;
    private readonly PulseBar _pulse = new();
    private readonly CancellationTokenSource _cts = new();

    public SnelAntwoordForm(MailBericht bericht)
    {
        _bericht = bericht;
        Text = $"Beantwoorden – {bericht.Van}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(950, 750);
        MinimizeBox = false;

        var origineel = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = (bericht.IsChat ? "" : $"Onderwerp: {bericht.Onderwerp}\r\n\r\n") +
                bericht.Tekst.ReplaceLineEndings("\r\n"),
        };
        var origineelGroup = new ModernGroupBox
        {
            Text = bericht.WhatsAppChat.Length > 0 ? "WhatsApp-gesprek"
                : bericht.ChatSpace.Length > 0 ? "Google Chat-gesprek"
                : "Ontvangen mail",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };
        origineelGroup.Controls.Add(origineel);

        _antwoord = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        _claudeButton = new ModernButton { Text = "Claude-concept", Width = 150, Dock = DockStyle.Left };
        _claudeButton.Click += async (_, _) => await ConceptAsync();
        _verstuurButton = new ModernButton
        {
            Text = "Versturen", Width = 130, Dock = DockStyle.Right, Kind = ButtonKind.Accent,
        };
        _verstuurButton.Click += async (_, _) => await VerstuurAsync();
        var knoppen = new Panel { Dock = DockStyle.Bottom, Height = 41, Padding = new Padding(0, 8, 0, 0) };
        knoppen.Controls.Add(_claudeButton);
        knoppen.Controls.Add(_verstuurButton);
        var antwoordGroup = new ModernGroupBox
        {
            Text = "Jouw antwoord", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10),
        };
        antwoordGroup.Controls.Add(_antwoord);
        antwoordGroup.Controls.Add(knoppen);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 380,
        };
        split.Panel1.Controls.Add(origineelGroup);
        split.Panel2.Controls.Add(antwoordGroup);

        Controls.Add(split);
        Controls.Add(_pulse);
        FormClosed += (_, _) => _cts.Cancel();
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster

        // Bestaand concept (uit de mailflow-cache) meteen aanbieden.
        if (!string.IsNullOrWhiteSpace(bericht.Concept))
        {
            _antwoord.Text = bericht.Concept.ReplaceLineEndings("\r\n");
        }
    }

    private async Task ConceptAsync()
    {
        _claudeButton.Enabled = false;
        _claudeButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            var resultaat = await ClaudeDrafter.DraftAsync(
                _bericht, MailReplySettings.LoadInstructies(), MailReplySettings.Load(), _cts.Token);
            if (!string.IsNullOrWhiteSpace(resultaat.Concept))
            {
                _antwoord.Text = resultaat.Concept.ReplaceLineEndings("\r\n");
            }
            else
            {
                Toast.Toon(this, $"Claude stelt geen antwoord voor ({resultaat.Reden})", Fluent.Ster);
            }
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het genereren.
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Concept genereren mislukt", ex.Message);
        }
        finally
        {
            _claudeButton.Bezig = false;
            _claudeButton.Enabled = true;
            _pulse.Actief = false;
        }
    }

    private async Task VerstuurAsync()
    {
        var tekst = _antwoord.Text.Trim();
        if (tekst.Length == 0)
        {
            return;
        }

        _verstuurButton.Enabled = false;
        _verstuurButton.Bezig = true;
        _pulse.Actief = true;
        try
        {
            _bericht.Concept = tekst;
            if (_bericht.WhatsAppChat.Length > 0)
            {
                await WhatsAppClient.Instance.VerstuurAsync(_bericht.WhatsAppChat, tekst, _cts.Token);
            }
            else if (_bericht.ChatSpace.Length > 0)
            {
                await GoogleChatClient.VerstuurAsync(
                    GoogleChatSettings.Load(), _bericht.ChatSpace, tekst, _cts.Token);
            }
            else
            {
                var settings = MailReplySettings.Load();
                var verstuurd = await GmailClient.VerstuurAsync(
                    settings, new[] { _bericht }, _ => { }, _cts.Token);
                if (verstuurd.Count == 0)
                {
                    throw new InvalidOperationException("De mail kon niet verstuurd worden.");
                }
                try
                {
                    await GmailClient.ArchiveerAsync(settings, verstuurd, _cts.Token);
                }
                catch
                {
                    // Antwoord is verstuurd; archiveren kan later nog handmatig.
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens het versturen.
        }
        catch (Exception ex)
        {
            Toast.Fout(this, "Versturen mislukt", ex.Message);
        }
        finally
        {
            _verstuurButton.Bezig = false;
            _verstuurButton.Enabled = true;
            _pulse.Actief = false;
        }
    }
}
