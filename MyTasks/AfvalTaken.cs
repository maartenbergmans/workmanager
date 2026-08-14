using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Wekelijkse afvalreminder: op zondag verschijnt een taak in "Mijn taken" met de fracties
/// die de komende week opgehaald worden (Henrilei 95, 2930 Brasschaat — in de praktijk
/// telkens op maandag). De ophaaldata staan in %APPDATA%\WorkManager\afval-kalender.json
/// en komen van recycleapp.be/calendar; vul dat bestand begin volgend jaar aan met de
/// nieuwe jaarkalender (KGA-ophalingen bewust niet opgenomen).
/// </summary>
public static class AfvalTaken
{
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "afval-kalender.json");

    private sealed class Data
    {
        public string LaatsteReminder { get; set; } = ""; // zondag (yyyy-MM-dd) van de laatste taak
        public Dictionary<string, List<string>> Ophalingen { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Maakt op zondag (één keer) de reminder-taak voor de ophalingen van de komende week.</summary>
    public static void ZorgVoorReminder()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Now);
        if (vandaag.DayOfWeek != DayOfWeek.Sunday)
        {
            return;
        }

        Data data;
        try
        {
            if (!File.Exists(DataFile) ||
                JsonSerializer.Deserialize<Data>(File.ReadAllText(DataFile), JsonOpts) is not { } geladen)
            {
                return;
            }
            data = geladen;
        }
        catch
        {
            return; // onleesbare kalender: geen reminder, geen crash
        }

        var sleutel = vandaag.ToString("yyyy-MM-dd");
        if (data.LaatsteReminder == sleutel)
        {
            return;
        }

        // Alle ophalingen in de komende zeven dagen (in Brasschaat is dat de maandag).
        var komende = data.Ophalingen
            .Select(o => (Ok: DateOnly.TryParse(o.Key, out var d), Datum: d, Fracties: o.Value))
            .Where(o => o.Ok && o.Datum > vandaag && o.Datum <= vandaag.AddDays(7))
            .OrderBy(o => o.Datum)
            .ToList();
        if (komende.Count > 0)
        {
            var omschrijving = string.Join("; ", komende.Select(o =>
                $"{string.Join(", ", o.Fracties)} ({o.Datum:ddd d/M})"));
            var taken = MijnTaakStore.Load();
            if (!taken.Taken.Any(t => !t.Klaar &&
                t.Tekst.StartsWith("Afvalbakken buitenzetten", StringComparison.OrdinalIgnoreCase)))
            {
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = $"Afvalbakken buitenzetten: {omschrijving}",
                    Categorie = "Privé",
                    Prioriteit = 1,
                    Deadline = vandaag,
                });
                MijnTaakStore.Save(taken);

                // Ook in de Google-agenda: per ophaling een afspraak op de avond ervóór
                // (buitenzetten), best effort — de agenda mag de reminder nooit blokkeren.
                var afspraken = komende
                    .Select(o => (Datum: o.Datum, Fracties: string.Join(", ", o.Fracties)))
                    .ToList();
                _ = ZetInAgendaAsync(afspraken);
            }
        }

        data.LaatsteReminder = sleutel;
        File.WriteAllText(DataFile, JsonSerializer.Serialize(data, JsonOpts));
    }

    /// <summary>
    /// Zet per ophaling een afspraak "Afvalbakken buitenzetten" in de Google-agenda op de
    /// avond vóór de ophaling (19:00–19:15). Best effort: mislukt de agendaschrijfactie, dan
    /// blijft de taak in "Mijn taken" alsnog staan.
    /// </summary>
    private static async Task ZetInAgendaAsync(List<(DateOnly Datum, string Fracties)> ophalingen)
    {
        if (!CalendarClient.Beschikbaar)
        {
            return;
        }
        foreach (var (datum, fracties) in ophalingen)
        {
            var avondErvoor = datum.AddDays(-1).ToDateTime(new TimeOnly(19, 0));
            try
            {
                await CalendarClient.MaakAfspraakAsync(
                    $"🗑️ Afvalbakken buitenzetten: {fracties}",
                    avondErvoor,
                    TimeSpan.FromMinutes(15),
                    $"Ophaling {datum:dddd d MMMM}: {fracties} (Henrilei 95, Brasschaat).",
                    CancellationToken.None);
            }
            catch
            {
                // Agenda even niet bereikbaar: geen probleem, de taak staat er.
            }
        }
    }
}
