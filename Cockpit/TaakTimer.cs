using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Eén lopende werk-timer: gestart vanaf een taak, gestopt bij het boeken (of het afvinken van
/// die taak). Geen schattingen meer — je boekt wat de klok zegt. De stand staat in
/// %APPDATA%\WorkManager\taak-timer.json en overleeft dus een herstart van de app.
/// </summary>
public static class TaakTimer
{
    public sealed class Lopend
    {
        public Guid? TaakId { get; set; }
        public string AsanaGid { get; set; } = "";
        public string Tekst { get; set; } = "";
        public string Klant { get; set; } = "";
        public DateTimeOffset Start { get; set; }

        /// <summary>Verstreken tijd, afgerond op 5 minuten naar boven (minimum 5).</summary>
        public int Minuten
        {
            get
            {
                var verstreken = (DateTimeOffset.Now - Start).TotalMinutes;
                return Math.Max(5, (int)Math.Ceiling(verstreken / 5) * 5);
            }
        }

        /// <summary>Ruwe verstreken minuten, voor weergave ("⏱ 38 min").</summary>
        public int Ruw => Math.Max(0, (int)(DateTimeOffset.Now - Start).TotalMinutes);
    }

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "taak-timer.json");

    /// <summary>De lopende timer, of null.</summary>
    public static Lopend? Huidig()
    {
        try
        {
            if (File.Exists(StateFile) &&
                JsonSerializer.Deserialize<Lopend>(File.ReadAllText(StateFile)) is { } t)
            {
                // Een timer die de nacht overleefde is vergeten werk, geen werk van 14 uur:
                // stilletjes opruimen in plaats van een absurde boeking voorstellen.
                if (DateTimeOffset.Now - t.Start > TimeSpan.FromHours(12))
                {
                    Wis();
                    return null;
                }
                return t;
            }
        }
        catch
        {
            // Onleesbaar: als "geen timer" behandelen.
        }
        return null;
    }

    /// <summary>Start (of vervangt) de timer voor deze taak.</summary>
    public static void Start(Guid? taakId, string asanaGid, string tekst, string klant)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new Lopend
            {
                TaakId = taakId,
                AsanaGid = asanaGid,
                Tekst = tekst,
                Klant = klant,
                Start = DateTimeOffset.Now,
            }));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Stopt de timer en geeft de eindstand terug (null als er geen liep).</summary>
    public static Lopend? Stop()
    {
        var huidig = Huidig();
        Wis();
        return huidig;
    }

    private static void Wis()
    {
        try
        {
            File.Delete(StateFile);
        }
        catch
        {
            // Best effort.
        }
    }
}
