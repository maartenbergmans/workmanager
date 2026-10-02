namespace WorkManager;

/// <summary>
/// Harde tijdslimiet rond één WebView2-aanroep. Zonder limiet kan een ophaalbeurt oneindig
/// blijven hangen: ExecuteScriptAsync, de DevTools-aanroepen en CapturePreviewAsync wachten
/// allemaal op antwoord van de renderer, en een pagina die haar hoofdthread blokkeert
/// antwoordt nooit meer. Op 23-09-2026 bleef één Teams-beurt zo bijna drie uur staan en de
/// volgende bijna twee: de cockpit gaf na drie minuten op, maar de beurt zelf hield het slot
/// van de bron bezet, dus elke volgende poll mislukte ook — "herhaalde fouten" met steeds
/// langere pauzes, terwijl er met Teams zelf niets aan de hand hoefde te zijn.
///
/// Loopt de limiet af, dan geldt de sessie als vastgelopen: de aanroeper geeft mee hoe die
/// vers opgebouwd wordt (cookies blijven, dus zonder nieuwe aanmelding) en de vastloper komt
/// in hetzelfde logboek als de browsercrashes van die bron — zo telt het gezondheidsvenster
/// ze gewoon mee. De afgebroken aanroep loopt op de achtergrond door (WebView2-werk valt niet
/// halverwege af te breken); zijn uitkomst wordt genegeerd.
/// </summary>
public static class WebLimiet
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    /// <summary>Scripts: ruim bemeten, een drukke pagina mag even doen.</summary>
    public static readonly TimeSpan Script = TimeSpan.FromSeconds(45);

    /// <summary>Muis, toetsen en schermafdrukken: die horen meteen te antwoorden.</summary>
    public static readonly TimeSpan Invoer = TimeSpan.FromSeconds(15);

    /// <param name="wat">Korte omschrijving voor de foutmelding en het logboek.</param>
    /// <param name="logBestand">Crashlogboek van deze bron (bv. "teams-crash-log.txt").</param>
    /// <param name="bijVastloper">
    /// Wat er moet gebeuren als de limiet afloopt — doorgaans de sessie markeren voor een
    /// verse opbouw. Null waar dat juist niet moet: tijdens het opstarten en aanmelden is
    /// traagheid normaal, en een verse opbouw zou het opstarten dan eindeloos herhalen.
    /// </param>
    public static async Task<T> MetLimietAsync<T>(
        Task<T> taak, TimeSpan grens, string wat, string logBestand, Action? bijVastloper)
    {
        using var klok = new CancellationTokenSource();
        if (await Task.WhenAny(taak, Task.Delay(grens, klok.Token)) != taak)
        {
            throw new TimeoutException(Vastgelopen(taak, grens, wat, logBestand, bijVastloper));
        }
        klok.Cancel(); // wachtklok opruimen
        return await taak;
    }

    /// <summary>Zelfde limiet voor aanroepen zonder uitkomst (schermafdruk, sitedata wissen).</summary>
    public static async Task MetLimietAsync(
        Task taak, TimeSpan grens, string wat, string logBestand, Action? bijVastloper)
    {
        using var klok = new CancellationTokenSource();
        if (await Task.WhenAny(taak, Task.Delay(grens, klok.Token)) != taak)
        {
            throw new TimeoutException(Vastgelopen(taak, grens, wat, logBestand, bijVastloper));
        }
        klok.Cancel();
        await taak;
    }

    private static string Vastgelopen(
        Task taak, TimeSpan grens, string wat, string logBestand, Action? bijVastloper)
    {
        // De achtergelaten taak stilhouden: zijn latere fout mag nergens onopgemerkt
        // blijven rondslingeren.
        _ = taak.ContinueWith(t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        bijVastloper?.Invoke();
        try
        {
            File.AppendAllText(Path.Combine(DataDir, logBestand),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Vastgelopen: {wat} gaf geen antwoord " +
                $"binnen {grens.TotalSeconds:0} s (herstel: " +
                $"{(bijVastloper is null ? "beurt overgeslagen" : "verse sessie bij volgende poll")})" +
                Environment.NewLine);
        }
        catch
        {
            // Alleen diagnose.
        }
        return $"de sessie reageerde niet binnen {grens.TotalSeconds:0} s ({wat})" +
            (bijVastloper is null ? "." : " — hij wordt bij de volgende beurt vers opgebouwd.");
    }
}
