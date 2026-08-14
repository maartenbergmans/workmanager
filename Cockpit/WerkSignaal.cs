namespace WorkManager;

/// <summary>
/// Onthoudt per bron (topdesk, devops, …) of er vermoedelijk werk klaarstaat. Gaat aan
/// zodra er een toewijzingsmail binnenkomt, en weer uit zodra het bijbehorende venster na
/// het ophalen niets open meer ziet. De cockpit toont de werkbalkknop van zo'n bron alleen
/// zolang het signaal aan staat (via het ⋯-menu en het tray-menu kan het altijd).
/// </summary>
public static class WerkSignaal
{
    private static string Pad(string naam) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", $"{naam}-signaal.txt");

    public static bool Actief(string naam) => File.Exists(Pad(naam));

    public static void Zet(string naam, bool aan)
    {
        try
        {
            var pad = Pad(naam);
            if (aan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pad)!);
                File.WriteAllText(pad, DateTimeOffset.Now.ToString("O"));
            }
            else if (File.Exists(pad))
            {
                File.Delete(pad);
            }
        }
        catch
        {
            // Best effort: dan blijft de knop hooguit één ronde langer (on)zichtbaar.
        }
    }
}
