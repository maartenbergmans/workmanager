using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Verjaardagen-radar: kijkt één keer per dag in de Google-agenda (de geconfigureerde
/// ICS-feeds, inclusief de verjaardagskalender als die erbij zit) naar verjaardag-events van
/// vandaag en zet er een taak voor klaar: "🎂 X is jarig — stuur een berichtje". Herkent
/// hele-dag-events met "verjaardag"/"birthday"/🎂 in de titel.
///
/// <para>Dit is het vangnet voor iedereen die alléén in de agenda staat. De belangrijke
/// verjaardagen staan in <see cref="Verjaardagen"/>: die krijgen een eigen cadeautraject
/// (bedenken → kopen → feliciteren) en worden hier daarom overgeslagen.</para>
/// </summary>
public static class VerjaardagRadar
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "verjaardag-radar.json");

    private static readonly Regex VerjaardagRegex = new(
        @"verjaardag(\s+van)?|birthday|🎂|anniversaire", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool _bezig;

    public static async Task ZorgVoorAsync(CancellationToken ct)
    {
        if (_bezig)
        {
            return;
        }
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (LaatsteDag() == vandaag.ToString("yyyy-MM-dd"))
        {
            return;
        }
        var agenda = AgendaSettings.Load();
        if (!agenda.Compleet)
        {
            return;
        }
        _bezig = true;
        try
        {
            BewaarDag(vandaag.ToString("yyyy-MM-dd")); // één poging per dag

            var items = await AgendaClient.OphalenAsync(agenda.Urls, vandaag, vandaag, ct);
            var taken = MijnTaakStore.Load();
            // Wie in de cadeauradar staat, krijgt daar al zijn eigen taken: hier overslaan.
            var eigenLijst = Verjaardagen.Load().Jarigen.Select(j => j.Naam).ToList();
            var nieuw = 0;
            foreach (var item in items.Where(i => i.HeleDag && VerjaardagRegex.IsMatch(i.Titel)))
            {
                var naam = Naam(item.Titel);
                if (eigenLijst.Any(n => naam.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var tekst = $"🎂 {naam} is jarig — stuur een berichtje";
                if (taken.Taken.Any(t => !t.Klaar &&
                    t.Tekst.Equals(tekst, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = tekst,
                    Categorie = "Privé",
                    Prioriteit = 1,
                    Deadline = vandaag,
                });
                nieuw++;
            }
            if (nieuw > 0)
            {
                MijnTaakStore.Save(taken);
            }
        }
        catch
        {
            // Agenda even niet bereikbaar: morgen opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Haalt de naam uit een verjaardag-titel ("Verjaardag van Piet" → "Piet").</summary>
    private static string Naam(string titel)
    {
        var schoon = Regex.Replace(titel, @"verjaardag(\s+van)?|birthday( of)?|anniversaire( de)?|🎂|['’]s",
            "", RegexOptions.IgnoreCase).Trim(' ', '-', ':', ',');
        return schoon.Length > 0 ? schoon : titel.Trim();
    }

    private static string LaatsteDag()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                return JsonSerializer.Deserialize<string>(File.ReadAllText(StateFile)) ?? "";
            }
        }
        catch
        {
            // Als "nog niet" behandelen.
        }
        return "";
    }

    private static void BewaarDag(string dag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(dag));
        }
        catch
        {
            // Best effort.
        }
    }
}
