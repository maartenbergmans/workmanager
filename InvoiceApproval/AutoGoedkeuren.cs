using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Mag de dagelijkse ISPnext-peiling klaarzetten wat de regels automatisch goedkeuren en
/// erom vragen? Eén schakelaar, apart van de regels bewaard
/// (%APPDATA%\WorkManager\ispnext-auto-goedkeuren.json), te vinden in het regelsvenster.
/// Staat hij uit, dan blijft de radar alleen kijken en komt de uitkomst enkel in de taaktekst.
/// Ook met de schakelaar aan wordt er nooit iets verstuurd zonder dat de vraag beantwoord is;
/// zie <see cref="AutoGoedkeurForm"/>.
/// </summary>
public static class AutoGoedkeuren
{
    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ispnext-auto-goedkeuren.json");

    private sealed class Instelling
    {
        public bool Aan { get; set; }
    }

    /// <summary>Staat stil goedkeuren aan? Zonder bestand: nee — dit zet je zelf aan.</summary>
    public static bool Aan
    {
        get
        {
            try
            {
                return File.Exists(Bestand) &&
                    JsonSerializer.Deserialize<Instelling>(File.ReadAllText(Bestand))?.Aan == true;
            }
            catch
            {
                return false; // bij twijfel niets goedkeuren
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
                File.WriteAllText(Bestand, JsonSerializer.Serialize(new Instelling { Aan = value }));
            }
            catch
            {
                // Niet kunnen bewaren betekent: blijft uit. Veilige kant.
            }
        }
    }
}
