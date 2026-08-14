using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Bewaakt de andere kant van de follow-up: conversaties waarin de klant het laatste woord
/// had en waar sindsdien niets van jou terugkwam. Die vragen blijven anders liggen — bij
/// Nemijtek lag er zo eentje acht weken. Eén keer per week (maandag) wordt er gescand en van
/// elke onbeantwoorde vraag een taak gemaakt; per conversatie hooguit één keer, en zodra je
/// geantwoord hebt verdwijnt hij vanzelf uit de scan.
/// </summary>
public static class OnbeantwoordRadar
{
    public const string TaakPrefix = "❓ Onbeantwoord";

    /// <summary>Pas na zoveel stille dagen telt een vraag als blijven liggen.</summary>
    private const int MinimumDagen = 5;

    /// <summary>Hoe ver terug er gekeken wordt.</summary>
    private const int MaxDagen = 90;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "onbeantwoord-radar.json");

    private sealed class State
    {
        public string LaatsteScan { get; set; } = "";        // yyyy-'W'ww
        public List<string> GemeldeThreads { get; set; } = new();
    }

    private static bool _bezig;

    /// <summary>
    /// Scant hooguit één keer per week (maandag) en maakt taken van wat er blijft liggen.
    /// Best effort: zonder mailkoppeling of bij een fout gebeurt er gewoon niets.
    /// </summary>
    public static async Task ZorgVoorTakenAsync(CancellationToken ct)
    {
        if (_bezig || DateTime.Now.DayOfWeek != DayOfWeek.Monday || DateTime.Now.Hour < 8)
        {
            return;
        }
        var week = $"{DateTime.Now:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now):00}";
        var state = Laad();
        if (state.LaatsteScan == week)
        {
            return;
        }
        var settings = MailReplySettings.Load();
        if (settings.AppWachtwoord.Length == 0 || settings.Email.Length == 0)
        {
            return; // geen mailkoppeling ingesteld
        }

        _bezig = true;
        try
        {
            state.LaatsteScan = week;
            Bewaar(state);

            var open = await GmailClient.WachtOpMijAsync(settings, MinimumDagen, MaxDagen, 40, ct);
            // Alleen echte vragen, en de oudste eerst: die zijn het pijnlijkst.
            var vragen = open.Where(m => m.BevatVraag)
                .OrderBy(m => m.Ontvangen)
                .Where(m => !state.GemeldeThreads.Contains(m.ThreadId))
                .Take(10)
                .ToList();
            if (vragen.Count == 0)
            {
                return;
            }

            var taken = MijnTaakStore.Load();
            foreach (var vraag in vragen)
            {
                var naam = FollowUpItem.Naam(vraag.Van);
                var tekst = $"{TaakPrefix}: {naam} — {Kort(vraag.Onderwerp, 60)} " +
                            $"({vraag.DagenStil} dagen)";
                taken.Taken.Add(new MijnTaak
                {
                    Tekst = tekst,
                    Categorie = CategorieVoor(vraag.VanAdres, taken.Categorieen),
                    // Hoe langer het ligt, hoe hoger de prioriteit.
                    Prioriteit = vraag.DagenStil >= 21 ? 0 : 1,
                    Deadline = DateOnly.FromDateTime(DateTime.Now).AddDays(1),
                    Mail = new TaakMail
                    {
                        Van = naam,
                        VanAdres = vraag.VanAdres,
                        Onderwerp = vraag.Onderwerp,
                        Tekst = vraag.Tekst,
                        Datum = vraag.Ontvangen,
                    },
                });
                state.GemeldeThreads.Add(vraag.ThreadId);
            }
            // De lijst met gemelde conversaties niet eeuwig laten groeien.
            if (state.GemeldeThreads.Count > 400)
            {
                state.GemeldeThreads.RemoveRange(0, state.GemeldeThreads.Count - 400);
            }
            MijnTaakStore.Save(taken);
            Bewaar(state);
        }
        catch
        {
            // Geen mailverbinding of een IMAP-hik: volgende maandag opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>De taakcategorie die bij het domein van de afzender past; anders de eerste.</summary>
    private static string CategorieVoor(string adres, List<string> categorieen)
    {
        var domein = adres.Contains('@') ? adres[(adres.IndexOf('@') + 1)..] : adres;
        var treffer = categorieen.FirstOrDefault(c =>
            domein.Contains(c.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (treffer is not null)
        {
            return treffer;
        }
        // Vaste vertalingen voor domeinen die niet op hun categorie lijken.
        return domein switch
        {
            var d when d.Contains("vriesveem", StringComparison.OrdinalIgnoreCase) ||
                       d.Contains("nemijtek", StringComparison.OrdinalIgnoreCase) => "Urban IT",
            var d when d.Contains("lauryssens", StringComparison.OrdinalIgnoreCase) => "Lauryssens",
            var d when d.Contains("ced.be", StringComparison.OrdinalIgnoreCase) => "CED",
            var d when d.Contains("aqurat", StringComparison.OrdinalIgnoreCase) => "Aqurat",
            var d when d.Contains("bloom", StringComparison.OrdinalIgnoreCase) => "RadiologyPartners",
            _ => categorieen.FirstOrDefault() ?? "",
        };
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
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
            // Onleesbaar: opnieuw beginnen.
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
