namespace WorkManager;

/// <summary>Eén voorgestelde timesheetregel voor een CED-dag.</summary>
public sealed class CedBlok
{
    public TimeOnly Van { get; set; }
    public TimeOnly Tot { get; set; }
    public string Omschrijving { get; set; } = "";

    /// <summary>Komt dit blok uit een agenda-afspraak? Puur voor de weergave.</summary>
    public bool IsMeeting { get; init; }

    public int Minuten => Math.Max(0, (int)(Tot - Van).TotalMinutes);
}

/// <summary>
/// Verdeelt een CED-werkdag in timesheetblokken: de meetings uit de Office 365-agenda krijgen
/// hun eigen regel, en de gaten ertussen worden opgevuld met gewone werkblokken.
///
/// Apart van de UI omdat de randgevallen hier zitten — meetings die buiten de werkdag vallen,
/// elkaar overlappen of exact aansluiten — en die wil je kunnen nalezen zonder een venster.
/// </summary>
public static class CedDagPlanner
{
    public static readonly TimeOnly StandaardStart = new(8, 0);
    public static readonly TimeOnly StandaardEinde = new(17, 0);

    /// <summary>
    /// Bouwt de blokken voor één dag. Meetings buiten [start, einde] worden afgeknipt; wat
    /// helemaal buiten de werkdag valt, verdwijnt. Overlappende meetings schuiven achter
    /// elkaar: de tweede begint waar de eerste eindigt, zodat er nooit dubbel geboekt wordt.
    /// </summary>
    public static List<CedBlok> Maak(
        DateOnly dag, TimeOnly start, TimeOnly einde, IEnumerable<AgendaClient.AgendaItem> meetings)
    {
        var blokken = new List<CedBlok>();
        if (einde <= start)
        {
            return blokken;
        }

        var relevant = meetings
            .Where(m => !m.HeleDag)
            .Select(m => (
                Van: TimeOnly.FromDateTime(m.Start.LocalDateTime),
                Tot: TimeOnly.FromDateTime(m.Einde.LocalDateTime),
                m.Titel,
                Dag: DateOnly.FromDateTime(m.Start.LocalDateTime)))
            .Where(m => m.Dag == dag && m.Tot > start && m.Van < einde)
            .OrderBy(m => m.Van)
            .ToList();

        var cursor = start;
        foreach (var (van, tot, titel, _) in relevant)
        {
            var mVan = van < cursor ? cursor : van;
            var mTot = tot > einde ? einde : tot;
            if (mTot <= mVan)
            {
                continue; // volledig opgeslokt door een eerdere (overlappende) meeting
            }

            if (mVan > cursor)
            {
                blokken.Add(new CedBlok { Van = cursor, Tot = mVan });
            }
            blokken.Add(new CedBlok
            {
                Van = mVan,
                Tot = mTot,
                Omschrijving = "Meeting: " + SchoonTitel(titel),
                IsMeeting = true,
            });
            cursor = mTot;
        }

        if (cursor < einde)
        {
            blokken.Add(new CedBlok { Van = cursor, Tot = einde });
        }
        return blokken;
    }

    /// <summary>Haalt het "CED · "-voorvoegsel weg dat de meetinglijst erbij zet.</summary>
    private static string SchoonTitel(string titel) =>
        titel.StartsWith("CED · ", StringComparison.Ordinal) ? titel["CED · ".Length..] : titel;
}
