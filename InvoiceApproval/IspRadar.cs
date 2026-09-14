using System.Globalization;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Dagelijkse stille peiling van ISPnext (werkdagen, na 08:00): hoeveel facturen wachten
/// er, voor hoeveel, en hoeveel keuren de regels automatisch goed? De uitkomst komt in de
/// tekst van de weektaak "Facturen goedkeuren (ISPnext)" — op woensdag bestaat die al; op
/// andere dagen komt er alleen een taak als er vervallen facturen liggen te wachten.
/// Peilt nooit terwijl het goedkeurvenster open staat en meldt zich niet aan (MFA).
/// </summary>
public static class IspRadar
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "ispnext-radar.json");

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("nl-BE");

    private sealed class State
    {
        public string LaatsteDag { get; set; } = "";
    }

    private static bool _bezig;

    public static async Task ZorgVoorAsync(CancellationToken ct)
    {
        var nu = DateTime.Now;
        if (_bezig || nu.Hour < 8 || nu.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }
        var state = LoadState();
        var vandaag = nu.ToString("yyyy-MM-dd");
        if (state.LaatsteDag == vandaag)
        {
            return;
        }
        _bezig = true;
        try
        {
            var peiling = await IspNextClient.Instance.PeilAsync(ct);
            if (peiling is null)
            {
                return; // venster open of pagina niet te lezen: morgen (of straks) opnieuw
            }
            state.LaatsteDag = vandaag;
            SaveState(state);
            WerkTaakBij(peiling);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.ChangeExtension(StateFile, ".log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Message}\r\n");
            }
            catch
            {
                // Alleen diagnose.
            }
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Eén regel voor in de taaktekst / het venster: "7 facturen, € 12.345,00 · 5 auto · ⚠ 2 vervallen".</summary>
    public static string Samenvatting(IspPeiling p)
    {
        if (!p.Aangemeld)
        {
            return "sessie verlopen — aanmelden bij openen";
        }
        if (p.Facturen.Count == 0)
        {
            return "geen facturen wachten";
        }
        var s = string.Create(Culture, $"{p.Facturen.Count} facturen, € {p.Totaal:N2}");
        s += $" · {p.AantalAuto} auto ≤ plafond";
        if (p.AantalVervallen > 0)
        {
            s += $" · ⚠ {p.AantalVervallen} vervallen";
        }
        return s;
    }

    private static void WerkTaakBij(IspPeiling p)
    {
        var data = MijnTaakStore.Load();
        var taak = data.Taken.FirstOrDefault(t => !t.Klaar &&
            t.Tekst.StartsWith(VasteTaken.FacturenTaak, StringComparison.OrdinalIgnoreCase));
        var tekst = $"{VasteTaken.FacturenTaak} — {Samenvatting(p)}";
        if (taak is not null)
        {
            if (taak.Tekst != tekst)
            {
                taak.Tekst = tekst;
                MijnTaakStore.Save(data);
            }
            return;
        }
        // Geen weektaak (niet woensdag): alleen aandringen als er facturen vervallen zijn.
        if (p.Aangemeld && p.AantalVervallen > 0)
        {
            data.Taken.Add(new MijnTaak
            {
                Tekst = tekst,
                Categorie = "Urban IT",
                Prioriteit = 0,
                Deadline = DateOnly.FromDateTime(DateTime.Now),
            });
            MijnTaakStore.Save(data);
            TrayMelding.Toon("ISPnext", string.Create(Culture,
                $"{p.AantalVervallen} vervallen factuur/facturen wachten op goedkeuring (totaal € {p.Totaal:N2})"),
                null, 15000);
        }
    }

    private static State LoadState()
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
            // Opnieuw beginnen.
        }
        return new State();
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Dan peilt hij straks nog eens.
        }
    }
}
