namespace WorkManager;

/// <summary>
/// De prijzenkast: behaalde verborgen prestaties met naam, omschrijving en datum; wat nog
/// niet behaald is staat er als "???" — de voorwaarde blijft geheim tot je hem haalt.
/// </summary>
public sealed class PrestatiesForm : Form
{
    public PrestatiesForm()
    {
        Text = "Prijzenkast";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 480);
        MinimizeBox = false;
        MaximizeBox = false;

        var lijst = new ModernListView
        {
            Dock = DockStyle.Fill,
            LegeTekst = "Nog niets behaald — de prestaties zijn geheim, dus gewoon doorwerken 😉",
            LeegGlyph = Fluent.Ster,
        };
        lijst.Columns.Add("Prestatie", 200);
        lijst.Columns.Add("Hoe", 280);
        lijst.Columns.Add("Behaald", 110);

        var behaald = Prestaties.Behaald();
        foreach (var p in Prestaties.Alle.OrderByDescending(p => behaald.ContainsKey(p.Id)))
        {
            if (behaald.TryGetValue(p.Id, out var datum))
            {
                var rij = new ListViewItem(p.Naam) { UseItemStyleForSubItems = false };
                rij.SubItems.Add(p.Omschrijving);
                rij.SubItems.Add(DateOnly.TryParse(datum, out var d) ? d.ToString("d MMM yyyy") : datum);
                lijst.Items.Add(rij);
            }
            else
            {
                var rij = new ListViewItem("???")
                {
                    UseItemStyleForSubItems = false, ForeColor = Theme.Muted,
                };
                var hoe = rij.SubItems.Add("nog te ontdekken");
                hoe.ForeColor = Theme.Muted;
                rij.SubItems.Add("");
                lijst.Items.Add(rij);
            }
        }

        var voet = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(12, 8, 12, 0),
            Text = $"{behaald.Count} van {Prestaties.Alle.Length} ontdekt",
        };
        Theme.AsStatus(voet);

        Controls.Add(lijst);
        Controls.Add(voet);
        Theme.Apply(this);
        Theme.EscSluit(this); // Esc sluit, zoals elk venster
    }
}
