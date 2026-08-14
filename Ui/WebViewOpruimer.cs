using System.Management;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Zelfherstel voor het "profiel vergrendeld"-probleem: crasht de host-app, dan blijven er
/// soms msedgewebview2-processen hangen die het WebView2-profiel vasthouden — waarna
/// EnsureCoreWebView2Async eeuwig blijft hangen en tot nu toe alleen een handmatige
/// app-herstart hielp. Dit ruimt precies díe processen op (herkend aan de profielmap in
/// hun commandline) en probeert de initialisatie één keer opnieuw met een verse control.
/// </summary>
public static class WebViewOpruimer
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    /// <summary>
    /// EnsureCoreWebView2Async met tijdslimiet én zelfherstel. Retourneert de (bij een
    /// herstelpoging vervangen) WebView2-control; de aanroeper moet zijn veld bijwerken.
    /// </summary>
    public static async Task<WebView2> InitMetHerstelAsync(
        Form venster, WebView2 web, CoreWebView2Environment env, string profielMap,
        string omschrijving, CancellationToken ct)
    {
        var init = web.EnsureCoreWebView2Async(env);
        if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20), ct)) == init)
        {
            await init;
            Demp(web);
            return web;
        }

        var opgeruimd = RuimProfielOp(profielMap);
        Log($"{omschrijving}: init hing; {opgeruimd} achtergebleven browserproces(sen) " +
            "opgeruimd, tweede poging");
        try
        {
            venster.Controls.Remove(web);
            web.Dispose();
        }
        catch
        {
            // De oude control was toch al onbruikbaar.
        }
        var vers = new WebView2 { Dock = DockStyle.Fill };
        venster.Controls.Add(vers);
        var poging2 = vers.EnsureCoreWebView2Async(env);
        if (await Task.WhenAny(poging2, Task.Delay(TimeSpan.FromSeconds(20), ct)) != poging2)
        {
            throw new InvalidOperationException(
                $"De ingebedde {omschrijving}-browser start niet op — ook niet na het " +
                $"opruimen van {opgeruimd} achtergebleven browserproces(sen). " +
                "Herstart de app en probeer opnieuw.");
        }
        await poging2;
        Log($"{omschrijving}: tweede poging geslaagd");
        Demp(vers);
        return vers;
    }

    /// <summary>
    /// Geluid uit: de ingebedde sites (WhatsApp, Teams, OWA) spelen anders hun eigen
    /// notificatiegeluiden af terwijl ze onzichtbaar op de achtergrond draaien.
    /// </summary>
    private static void Demp(WebView2 web)
    {
        try
        {
            web.CoreWebView2.IsMuted = true;
        }
        catch
        {
            // Oudere runtime zonder IsMuted: dan blijft alles werken zoals voorheen.
        }
    }

    /// <summary>
    /// Schiet msedgewebview2-processen af waarvan de commandline naar het opgegeven
    /// profielmapje wijst. Raakt dus nooit webviews van andere apps of andere profielen.
    /// </summary>
    public static int RuimProfielOp(string profielMap)
    {
        var geraakt = 0;
        try
        {
            using var zoeker = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process " +
                "WHERE Name = 'msedgewebview2.exe'");
            foreach (var proces in zoeker.Get())
            {
                var cmd = proces["CommandLine"] as string ?? "";
                if (!cmd.Contains(profielMap, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(
                        Convert.ToInt32(proces["ProcessId"]));
                    p.Kill(entireProcessTree: true);
                    geraakt++;
                }
                catch
                {
                    // Al weg of geen rechten; de rest gewoon proberen.
                }
            }
        }
        catch
        {
            // WMI niet beschikbaar: dan blijft alleen de duidelijke foutmelding over.
        }
        return geraakt;
    }

    private static void Log(string melding)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "sessie-onderhoud-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {melding}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }
}
