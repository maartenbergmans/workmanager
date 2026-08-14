using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Onthoudt positie, grootte en maximalisatie van vensters over sessies heen
/// (%APPDATA%\WorkManager\vensters.json). Eén regel in de constructor volstaat:
/// VensterGeheugen.Volg(this, "cockpit"). Een bewaarde positie wordt alleen
/// teruggezet als hij nog (grotendeels) op een aangesloten scherm valt — anders
/// zou een venster op een losgekoppelde tweede monitor onbereikbaar openen.
/// </summary>
public static class VensterGeheugen
{
    private sealed class Stand
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Breedte { get; set; }
        public int Hoogte { get; set; }
        public bool Gemaximaliseerd { get; set; }
    }

    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "vensters.json");

    public static void Volg(Form form, string sleutel)
    {
        try
        {
            if (Laad().TryGetValue(sleutel, out var stand) &&
                stand.Breedte >= 200 && stand.Hoogte >= 150)
            {
                var bounds = new Rectangle(stand.X, stand.Y, stand.Breedte, stand.Hoogte);
                // Alleen terugzetten als minstens een werkbaar stuk zichtbaar is.
                if (Screen.AllScreens.Any(s =>
                    {
                        var zicht = Rectangle.Intersect(s.WorkingArea, bounds);
                        return zicht.Width >= 200 && zicht.Height >= 120;
                    }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Bounds = bounds;
                }
                form.WindowState = stand.Gemaximaliseerd
                    ? FormWindowState.Maximized
                    : FormWindowState.Normal;
            }
        }
        catch
        {
            // Zonder geheugen gewoon de standaardpositie.
        }

        form.FormClosing += (_, _) =>
        {
            try
            {
                var alles = Laad();
                var b = form.WindowState == FormWindowState.Normal
                    ? form.Bounds
                    : form.RestoreBounds;
                alles[sleutel] = new Stand
                {
                    X = b.X, Y = b.Y, Breedte = b.Width, Hoogte = b.Height,
                    Gemaximaliseerd = form.WindowState == FormWindowState.Maximized,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
                File.WriteAllText(DataFile, JsonSerializer.Serialize(
                    alles, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Best effort: hooguit opent het venster volgende keer op de standaardplek.
            }
        };
    }

    private static Dictionary<string, Stand> Laad()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<Dictionary<string, Stand>>(
                    File.ReadAllText(DataFile)) is { } alles)
            {
                return alles;
            }
        }
        catch
        {
            // Kapot bestand: opnieuw beginnen.
        }
        return new Dictionary<string, Stand>();
    }
}
