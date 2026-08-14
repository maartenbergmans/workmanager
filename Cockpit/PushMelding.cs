using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Pushmeldingen naar de gsm via ntfy.sh: de pc publiceert op een geheim topic, de ntfy-app
/// op de telefoon is erop geabonneerd. Geen account, geen sleutels — het topic ís het geheim,
/// dus het staat DPAPI-versleuteld bij de andere webinstellingen.
///
/// <para>Er wordt met mate gepusht: alleen een urgente mail, een taak die vandaag te laat
/// wordt en de dagelijkse afsluiting. Elk onderwerp hooguit één keer per dag, anders leert
/// een mens de melding weg te vegen.</para>
/// </summary>
public static class PushMelding
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "push-melding.json");

    private sealed class State
    {
        /// <summary>Sleutel "yyyy-MM-dd|onderwerp" van wat er al gepusht is.</summary>
        public List<string> Gestuurd { get; set; } = new();
    }

    /// <summary>
    /// Stuurt een melding, tenzij die vandaag al de deur uit ging. <paramref name="sleutel"/>
    /// is waar de "één keer per dag" op slaat (leeg = altijd sturen).
    /// </summary>
    public static async Task StuurAsync(
        string titel, string tekst, string sleutel = "", string prioriteit = "default")
    {
        var settings = WmWebSettings.Load();
        if (settings.PushTopic.Length == 0)
        {
            return;
        }
        var vandaagSleutel = $"{DateTime.Now:yyyy-MM-dd}|{sleutel}";
        var state = Laad();
        if (sleutel.Length > 0 && state.Gestuurd.Contains(vandaagSleutel))
        {
            return;
        }
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://ntfy.sh/{settings.PushTopic}")
            {
                Content = new StringContent(tekst, Encoding.UTF8),
            };
            // ntfy leest titel/prioriteit/tags uit headers; die moeten ASCII zijn.
            request.Headers.TryAddWithoutValidation("Title", NaarAscii(titel));
            request.Headers.TryAddWithoutValidation("Priority", prioriteit);
            request.Headers.TryAddWithoutValidation("Tags", "briefcase");
            if (WmWebSettings.Load().Link is { Length: > 0 } link)
            {
                // Tikken op de melding opent meteen de webversie.
                request.Headers.TryAddWithoutValidation("Click", link);
            }
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
            {
                CharSet = "utf-8",
            };
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return; // niet als verstuurd wegschrijven: volgende ronde opnieuw
            }
        }
        catch
        {
            return; // geen internet: gewoon overslaan
        }

        if (sleutel.Length > 0)
        {
            state.Gestuurd.Add(vandaagSleutel);
            // Alleen de laatste dagen bewaren; de sleutel bevat de datum.
            if (state.Gestuurd.Count > 200)
            {
                state.Gestuurd.RemoveRange(0, state.Gestuurd.Count - 200);
            }
            Bewaar(state);
        }
    }

    /// <summary>
    /// De vaste ronde (vanuit de pollronde van de webversie): urgente mail en taken die
    /// vandaag over hun deadline gaan. Stil als er niets te melden valt.
    /// </summary>
    public static async Task RondeAsync()
    {
        if (WmWebSettings.Load().PushTopic.Length == 0)
        {
            return;
        }

        // Urgente mail: per afzender hooguit één keer per dag.
        foreach (var bericht in CockpitCache.Load()
                     .Where(b => b.Urgent && !b.Genegeerd)
                     .OrderByDescending(b => b.Datum)
                     .Take(3))
        {
            await StuurAsync(
                $"Urgent: {bericht.Van}",
                bericht.Onderwerp,
                $"urgent|{bericht.MessageId}",
                "high");
        }

        // Taken die vandaag verlopen, vanaf de late namiddag: dan kun je er nog iets aan doen.
        if (DateTime.Now.Hour is >= 16 and < 20)
        {
            var vandaag = DateOnly.FromDateTime(DateTime.Now);
            var open = MijnTaakStore.Load().Taken
                .Where(t => !t.Klaar && !t.Gesnoozed && !t.NogNietGestart &&
                            t.Deadline is { } d && d <= vandaag)
                .ToList();
            if (open.Count > 0)
            {
                await StuurAsync(
                    open.Count == 1 ? "Nog 1 taak voor vandaag" : $"Nog {open.Count} taken voor vandaag",
                    string.Join("\n", open.Take(5).Select(t => "• " + t.Tekst)),
                    "deadlines", "high");
            }
        }
    }

    /// <summary>Titels met accenten of emoji breken de HTTP-header; daarom kaal.</summary>
    private static string NaarAscii(string tekst)
    {
        var genormaliseerd = tekst.Normalize(System.Text.NormalizationForm.FormD);
        var bouwer = new StringBuilder();
        foreach (var teken in genormaliseerd)
        {
            var soort = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(teken);
            if (soort != System.Globalization.UnicodeCategory.NonSpacingMark && teken < 128)
            {
                bouwer.Append(teken);
            }
        }
        var kaal = bouwer.ToString().Trim();
        return kaal.Length == 0 ? "WorkManager" : kaal;
    }

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
            // Onleesbaar: opnieuw beginnen (ergste geval: één dubbele melding).
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
