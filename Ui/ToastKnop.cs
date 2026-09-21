namespace WorkManager;

/// <summary>
/// Het 🔔-knopje rechtsonder in de cockpit, onderaan naast de agendaknoppen. Toasts zijn
/// vluchtig: net die ene melding die je met je ogen ergens anders miste is weg voor je ze
/// gelezen hebt. Eén klik haalt de laatste melding terug, rechtsklik geeft de laatste
/// twintig op een rij.
/// </summary>
public static class ToastKnop
{
    /// <summary>Maakt de knop; hang hem zelf in de gewenste knoppenbalk.</summary>
    public static ModernButton Maak(Form eigenaar)
    {
        var knop = new ModernButton { Text = "🔔", Width = 44, Dock = DockStyle.Right };
        knop.Click += (_, _) => Toast.Herhaal(eigenaar);
        // Rechtsklik: het hele logje. Via MouseUp, want Click vuurt alleen op links.
        knop.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ToonLog(knop);
            }
        };
        return knop;
    }

    /// <summary>De laatste twintig meldingen als lijstje, naar boven uitklappend.</summary>
    private static void ToonLog(Control knop)
    {
        var log = new ContextMenuStrip();
        Theme.Style(log);
        foreach (var (moment, tekst) in Toast.Recent.Take(20))
        {
            var regel = tekst.Length <= 80 ? tekst : tekst[..80] + "…";
            log.Items.Add(new ToolStripMenuItem($"{moment:HH:mm}  {regel}") { Enabled = false });
        }
        if (log.Items.Count == 0)
        {
            log.Items.Add(new ToolStripMenuItem("Nog geen meldingen deze sessie") { Enabled = false });
        }
        // Boven het knopje uitklappen: eronder is het venster op.
        log.Show(knop, new Point(0, -log.GetPreferredSize(Size.Empty).Height - 4));
    }
}
