using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Duurzame wachtrij voor schrijfacties richting de verborgen sessies: archiveren in
/// Outlook en gelezen zetten in Teams/WhatsApp. Een actie die mislukt — sessie net
/// gecrasht, OWA-hapering, MFA verlopen — gaat niet verloren maar wordt bij volgende
/// polls opnieuw geprobeerd met oplopende tussenpozen (2, 4, 8, 16 minuten); na vijf
/// pogingen geeft hij op met een melding. Herstart-bestendig via actie-wachtrij.json.
/// </summary>
public static class ActieWachtrij
{
    public sealed class Actie
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Soort { get; set; } = ""; // outlook-archief | teams-gelezen | wa-gelezen
        public string Van { get; set; } = "";
        public string Onderwerp { get; set; } = "";
        public string Url { get; set; } = "";
        public string Chat { get; set; } = "";
        public int Pogingen { get; set; }
        public DateTimeOffset VolgendePoging { get; set; } = DateTimeOffset.MinValue;
        public string LaatsteFout { get; set; } = "";

        public string Omschrijving => Soort switch
        {
            "outlook-archief" => $"Outlook-archivering \"{Onderwerp}\" ({Van})",
            "teams-gelezen" => $"Teams-chat \"{Chat}\" gelezen zetten",
            "wa-gelezen" => $"WhatsApp-chat \"{Chat}\" gelezen zetten",
            _ => Soort,
        };
    }

    private static readonly string Bestand = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "actie-wachtrij.json");

    private static readonly object Slot = new();
    private static bool _bezig;

    public static void Voeg(Actie actie)
    {
        lock (Slot)
        {
            var acties = Laad();
            // Dezelfde actie niet dubbel in de rij (bv. twee keer archiveren geklikt).
            if (acties.Any(a => a.Soort == actie.Soort && a.Van == actie.Van &&
                                a.Onderwerp == actie.Onderwerp && a.Chat == actie.Chat))
            {
                return;
            }
            acties.Add(actie);
            Bewaar(acties);
        }
    }

    public static int Aantal()
    {
        lock (Slot)
        {
            return Laad().Count;
        }
    }

    /// <summary>
    /// Werkt de acties af die aan de beurt zijn. Aanroepen vanaf de UI-thread (de verborgen
    /// sessies zijn WebView2-controls); meldingen gaan via <paramref name="meld"/>.
    /// </summary>
    public static async Task VerwerkAsync(CancellationToken ct, Action<string> meld)
    {
        if (_bezig)
        {
            return; // nooit twee verwerkrondes tegelijk (poll + handmatige verversing)
        }
        _bezig = true;
        try
        {
            List<Actie> teDoen;
            lock (Slot)
            {
                teDoen = Laad().Where(a => a.VolgendePoging <= DateTimeOffset.Now).ToList();
            }
            foreach (var actie in teDoen)
            {
                ct.ThrowIfCancellationRequested();
                string fout;
                try
                {
                    fout = await VoerUitAsync(actie, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    fout = ex.Message;
                }

                lock (Slot)
                {
                    var acties = Laad();
                    var huidig = acties.FirstOrDefault(a => a.Id == actie.Id);
                    if (huidig is null)
                    {
                        continue;
                    }
                    if (fout.Length == 0)
                    {
                        acties.Remove(huidig);
                        Bewaar(acties);
                        meld($"Alsnog gelukt: {huidig.Omschrijving}");
                        continue;
                    }
                    huidig.Pogingen++;
                    huidig.LaatsteFout = fout;
                    if (huidig.Pogingen >= 5)
                    {
                        acties.Remove(huidig);
                        meld($"Opgegeven na 5 pogingen: {huidig.Omschrijving} ({fout})");
                    }
                    else
                    {
                        huidig.VolgendePoging = DateTimeOffset.Now
                            .AddMinutes(Math.Pow(2, huidig.Pogingen));
                    }
                    Bewaar(acties);
                }
            }
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Voert één actie uit; lege string = gelukt, anders de foutomschrijving.</summary>
    private static async Task<string> VoerUitAsync(Actie actie, CancellationToken ct)
    {
        switch (actie.Soort)
        {
            case "outlook-archief":
                var stand = await OutlookClient.Instance.ArchiveerAsync(
                    actie.Van, actie.Onderwerp, ct, actie.Url);
                return stand == "ok" ? "" : stand;
            case "teams-gelezen":
                await TeamsClient.Instance.MarkeerGelezenAsync(actie.Chat, ct);
                return "";
            case "wa-gelezen":
                await WhatsAppClient.Instance.MarkeerGelezenAsync(actie.Chat, ct);
                return "";
            default:
                return $"onbekende actiesoort \"{actie.Soort}\"";
        }
    }

    private static List<Actie> Laad()
    {
        try
        {
            if (File.Exists(Bestand) &&
                JsonSerializer.Deserialize<List<Actie>>(File.ReadAllText(Bestand)) is { } acties)
            {
                return acties;
            }
        }
        catch
        {
            // Onleesbaar: met een lege rij verder (de zelfheling van de poll vangt de rest).
        }
        return new List<Actie>();
    }

    private static void Bewaar(List<Actie> acties)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Bestand)!);
            File.WriteAllText(Bestand, JsonSerializer.Serialize(acties,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
