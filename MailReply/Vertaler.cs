using System.Collections.Concurrent;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Vertaalt Franse of Engelse mails naar het Nederlands (via de Claude CLI) zodat de
/// vertaling onder de mail getoond kan worden. Resultaten worden per bericht gecachet in
/// %APPDATA%\WorkManager\vertaling-cache.json; een lege vertaling betekent "Nederlands of
/// niet te vertalen" en wordt óók gecachet zodat er niet telkens opnieuw vertaald wordt.
/// </summary>
public static class Vertaler
{
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "vertaling-cache.json");

    private static readonly ConcurrentDictionary<string, string> Cache = Laad();
    private static readonly SemaphoreSlim Slot = new(1, 1); // niet twee vertalingen tegelijk

    /// <summary>
    /// Vult <see cref="MailBericht.Vertaling"/> als de mail Frans/Engels is. Geeft true als er
    /// een (nieuwe) vertaling gezet is. Chats en al vertaalde/gecachte mails worden overgeslagen.
    /// </summary>
    public static async Task<bool> VertaalAlsNodigAsync(
        MailBericht mail, CancellationToken ct, bool forceerChat = false)
    {
        if (mail.Vertaling.Length > 0)
        {
            return false;
        }
        // Echte mails (Gmail/Outlook) vertalen we automatisch; chatbronnen alleen wanneer de
        // gebruiker het via de 🌐-knop expliciet vraagt (forceerChat).
        var isChat = mail.ChatSpace.Length > 0 || mail.WhatsAppChat.Length > 0 ||
            mail.TeamsChat.Length > 0;
        if (isChat && !forceerChat)
        {
            return false;
        }
        var sleutel = mail.MessageId.Length > 0
            ? mail.MessageId
            : $"{mail.Van}|{mail.Onderwerp}|{mail.Datum:yyyyMMddHHmm}";
        if (Cache.TryGetValue(sleutel, out var bewaard))
        {
            if (bewaard.Length > 0)
            {
                mail.Vertaling = bewaard;
                return true;
            }
            return false; // eerder al beoordeeld als "geen vertaling nodig"
        }

        var brontekst = mail.Tekst;
        if (string.IsNullOrWhiteSpace(brontekst))
        {
            return false;
        }
        if (brontekst.Length > 6000)
        {
            brontekst = brontekst[..6000];
        }

        await Slot.WaitAsync(ct);
        try
        {
            var prompt =
                $$"""
                Hieronder staat de tekst van een e-mail. Als de mail in het Frans of het Engels
                is, vertaal hem dan volledig en natuurlijk naar het Nederlands (behoud de
                alinea-indeling). Is de mail al in het Nederlands (of een andere taal), antwoord
                dan met een lege vertaling.

                Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown:
                {"vertaling": "de Nederlandse vertaling, of leeg"}

                Onderwerp: {{mail.Onderwerp}}

                {{brontekst}}
                """;
            var output = await ClaudeDrafter.RunClaudeAsync(prompt, ct);
            var vertaling = "";
            try
            {
                using var doc = ClaudeDrafter.ParseJson(output);
                if (doc.RootElement.TryGetProperty("vertaling", out var v))
                {
                    vertaling = (v.GetString() ?? "").Trim();
                }
            }
            catch
            {
                vertaling = ""; // onparseerbaar antwoord: als "geen vertaling" behandelen
            }
            Cache[sleutel] = vertaling;
            Bewaar();
            if (vertaling.Length > 0)
            {
                mail.Vertaling = vertaling;
                return true;
            }
            return false;
        }
        finally
        {
            Slot.Release();
        }
    }

    private static ConcurrentDictionary<string, string> Laad()
    {
        try
        {
            if (File.Exists(CacheFile) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) is { } d)
            {
                return new ConcurrentDictionary<string, string>(d);
            }
        }
        catch
        {
            // Onleesbaar: leeg beginnen.
        }
        return new ConcurrentDictionary<string, string>();
    }

    private static void Bewaar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(
                new Dictionary<string, string>(Cache), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
