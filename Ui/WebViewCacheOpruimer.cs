using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Maandelijkse opruiming van de browsercache in de WebView2-profielen (webview2-outlook,
/// -whatsapp, -teams, -ah, …). Die groeiden samen tot enkele GB, vooral de HTTP-cache en de
/// gecompileerde scriptcache. Alleen herbouwbare caches gaan weg; cookies, Local Storage,
/// IndexedDB en Service Worker-data blijven staan, dus logins en koppelingen (MFA, WhatsApp-QR)
/// blijven werken.
/// <para>Draait bij de start van de app, vóór er een WebView2 opent (anders zijn de mappen
/// vergrendeld). State: %APPDATA%\WorkManager\webview-cache-opruim.json.</para>
/// </summary>
public static class WebViewCacheOpruimer
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "webview-cache-opruim.json");

    private static readonly TimeSpan Interval = TimeSpan.FromDays(30);

    /// <summary>Herbouwbare cachemappen, relatief t.o.v. de EBWebView-map van een profiel.</summary>
    private static readonly string[] CacheMappen =
    {
        @"Default\Cache",
        @"Default\Code Cache",
        @"Default\GPUCache",
        @"Default\DawnGraphiteCache",
        @"Default\DawnWebGPUCache",
        @"GrShaderCache",
        @"ShaderCache",
        @"GraphiteDawnCache",
    };

    private sealed class State
    {
        public DateTimeOffset LaatsteKeer { get; set; }
        public long LaatstVrijgemaakt { get; set; }
    }

    /// <summary>Ruimt op als de vorige keer meer dan 30 dagen geleden is. Retourneert de vrijgemaakte bytes.</summary>
    public static long ZorgVoorMaandelijks()
    {
        try
        {
            var state = File.Exists(StateFile)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new State()
                : new State();
            if (DateTimeOffset.Now - state.LaatsteKeer < Interval)
            {
                return 0;
            }
            var vrij = RuimOp();
            state.LaatsteKeer = DateTimeOffset.Now;
            state.LaatstVrijgemaakt = vrij;
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
            return vrij;
        }
        catch
        {
            return 0; // opruimen mag de start nooit hinderen
        }
    }

    /// <summary>Wist de cachemappen van alle webview2-*-profielen; vergrendelde bestanden blijven staan.</summary>
    public static long RuimOp()
    {
        long vrij = 0;
        foreach (var profiel in Directory.EnumerateDirectories(DataDir, "webview2-*"))
        {
            var basis = Path.Combine(profiel, "EBWebView");
            foreach (var rel in CacheMappen)
            {
                var map = Path.Combine(basis, rel);
                if (!Directory.Exists(map))
                {
                    continue;
                }
                foreach (var bestand in Directory.EnumerateFiles(map, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var grootte = new FileInfo(bestand).Length;
                        File.Delete(bestand);
                        vrij += grootte;
                    }
                    catch
                    {
                        // In gebruik: laten staan, volgende maand opnieuw.
                    }
                }
            }
        }
        return vrij;
    }
}
