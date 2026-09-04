using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// De maandelijkse Apple-factuur van € 0,99 (iCloud-opslag) automatisch archiveren, behalve
/// één keer per jaar: de eerste factuur in januari blijft in de cockpit staan als jaarlijkse
/// herinnering dat het abonnement loopt. Facturen met een ander bedrag blijven gewoon zichtbaar.
/// Welke januarifactuur getoond wordt staat in apple-factuur.json.
/// </summary>
public static class AppleFactuur
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "apple-factuur.json");

    private sealed class State
    {
        public int Jaar { get; set; }
        public string MessageId { get; set; } = "";
    }

    /// <summary>Automatisch archiveren? (false = tonen: geen match, of dé januarifactuur)</summary>
    public static bool MoetArchiveren(MailBericht m)
    {
        if (!IsFactuur099(m))
        {
            return false;
        }
        if (m.Datum.Month != 1)
        {
            return true;
        }
        var state = Laad();
        if (state.Jaar == m.Datum.Year)
        {
            // Dit jaar is er al één getoond: alleen datzelfde bericht blijft staan.
            return state.MessageId != m.MessageId;
        }
        Bewaar(new State { Jaar = m.Datum.Year, MessageId = m.MessageId });
        return false;
    }

    private static bool IsFactuur099(MailBericht m) =>
        m.VanAdres.Contains("apple.com", StringComparison.OrdinalIgnoreCase) &&
        (m.Onderwerp.Contains("factuur", StringComparison.OrdinalIgnoreCase) ||
         m.Onderwerp.Contains("invoice", StringComparison.OrdinalIgnoreCase) ||
         m.Onderwerp.Contains("receipt", StringComparison.OrdinalIgnoreCase)) &&
        Regex.IsMatch(m.Tekst.Length > 0 ? m.Tekst : m.Html,
            @"[€$]\s*0[.,]99|(?<!\d)0[.,]99\s*€");

    private static State Laad()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
            {
                return s;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
