namespace WorkManager;

/// <summary>
/// Beheerscherm voor de voorbeeldmails waarop Claude de stijl van de weekmail baseert
/// (aanhef, talenmix, afsluiting). Vrije tekst, persistent in
/// %APPDATA%\WorkManager\team-mail-style.txt.
/// </summary>
public class TeamStijlForm : Form
{
    private readonly TextBox _tekst;

    public TeamStijlForm()
    {
        Text = "Stijl weekmail";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 640);
        MinimizeBox = false;

        _tekst = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.MonoFont,
            Text = TeamTaskStore.LoadStijl(),
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Claude baseert de stijl van de weekmail op deze voorbeeldmails (aanhef, talenmix,\n" +
                   "afsluiting). Vervang of vul aan met eigen mails om de stijl bij te sturen.",
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
        ok.Click += (_, _) => TeamTaskStore.SaveStijl(_tekst.Text);
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
