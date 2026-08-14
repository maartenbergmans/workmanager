namespace WorkManager;

/// <summary>
/// Beheerscherm voor de instructies die Claude gebruikt bij het opstellen van conceptantwoorden
/// (toon, ondertekening, wat wel/niet beantwoorden). Vrije tekst, persistent in
/// %APPDATA%\WorkManager\mail-reply-instructions.txt.
/// </summary>
public class InstructionsForm : Form
{
    private readonly TextBox _tekst;

    public InstructionsForm()
    {
        Text = "Instructies mailassistent";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 560);
        MinimizeBox = false;

        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoFont,
            Text = MailReplySettings.LoadInstructies(),
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Deze instructies krijgt Claude bij elke mail: toon, ondertekening en welke mails\n" +
                   "wel of geen antwoord verdienen. Nieuwsbrieven e.d. worden sowieso overgeslagen.",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
        };
        var cancel = new ModernButton { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 100 };
        var ok = new ModernButton { Text = "Opslaan", DialogResult = DialogResult.OK, Width = 100, Kind = ButtonKind.Accent };
        ok.Click += (_, _) => MailReplySettings.SaveInstructies(_tekst.Text);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;

        Controls.Add(_tekst);
        Controls.Add(hint);
        Controls.Add(buttons);
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted;
    }
}
