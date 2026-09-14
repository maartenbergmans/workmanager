using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>Eén wachtende factuur uit het ISPnext-overzicht "My Activities".</summary>
public sealed record IspFactuur(
    string Leverancier, string Factuurnummer, string Factuurdatum, string Vervaldatum,
    string Valuta, string BedragText, decimal? Bedrag, bool Vervallen, bool Auto, string Reden);

/// <summary>Uitkomst van een stille peiling: aangemeld of niet, en de wachtende facturen.</summary>
public sealed record IspPeiling(DateTimeOffset Moment, bool Aangemeld, List<IspFactuur> Facturen)
{
    public decimal Totaal => Facturen.Sum(f => f.Bedrag ?? 0);
    public int AantalAuto => Facturen.Count(f => f.Auto);
    public int AantalVervallen => Facturen.Count(f => f.Vervallen);
}

/// <summary>
/// Stille lezer van de ISPnext-facturenlijst (CED): een verborgen WebView2 op hetzelfde
/// profiel als het goedkeurvenster (de SSO-sessie), die alleen kijkt — nooit aanmeldt
/// (dat zou een onverwachte MFA-push geven) en nooit goedkeurt. Staat de sessie nog, dan
/// weet de cockpit hoeveel facturen er wachten, voor hoeveel, en hoeveel de regels
/// automatisch zouden goedkeuren. Na elke peiling wordt de webview weer weggegooid:
/// het profiel kan maar door één webview tegelijk gebruikt worden en het echte venster
/// heeft voorrang.
/// </summary>
public sealed class IspNextClient
{
    public static IspNextClient Instance { get; } = new();

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string PeilingFile = Path.Combine(DataDir, "ispnext-peiling.json");

    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>De laatst bewaarde peiling (null als er nog nooit gepeild is).</summary>
    public static IspPeiling? Laatste()
    {
        try
        {
            return File.Exists(PeilingFile)
                ? JsonSerializer.Deserialize<IspPeiling>(File.ReadAllText(PeilingFile))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Peilt de facturenlijst. Null als het goedkeurvenster open staat (dat gebruikt het
    /// profiel) of de pagina binnen de tijd noch een tabel noch een loginscherm toonde.
    /// </summary>
    public async Task<IspPeiling?> PeilAsync(CancellationToken ct)
    {
        if (InvoiceApprovalForm.IsOpen)
        {
            return null;
        }
        await _slot.WaitAsync(ct);
        Form? venster = null;
        WebView2? web = null;
        try
        {
            venster = new Form
            {
                Size = new Size(1400, 1000), StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000), ShowInTaskbar = false,
            };
            web = new WebView2 { Dock = DockStyle.Fill };
            venster.Controls.Add(web);
            venster.Show();
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataDir, "webview2-ispnext"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
            core.Navigate(InvoiceApprovalForm.InvoicesUrl);

            IspPeiling? uit = null;
            for (var i = 0; i < 40 && uit is null; i++) // max. 40 s
            {
                await Task.Delay(1000, ct);
                if (await Js(core, InvoiceApprovalForm.LoginSchermScript) == "true")
                {
                    uit = new IspPeiling(DateTimeOffset.Now, false, []);
                    break;
                }
                if (await Js(core, InvoiceApprovalForm.HeeftTabelScript) != "true")
                {
                    continue;
                }
                await Task.Delay(1500, ct); // rijen laten renderen
                var json = await Js(core, InvoiceApprovalForm.ExtractScript);
                if (json is "null" or "")
                {
                    continue;
                }
                uit = new IspPeiling(DateTimeOffset.Now, true, Parse(json));
            }
            if (uit is not null)
            {
                try
                {
                    File.WriteAllText(PeilingFile, JsonSerializer.Serialize(uit));
                }
                catch
                {
                    // Cache is comfort.
                }
            }
            return uit;
        }
        finally
        {
            try { web?.Dispose(); } catch { /* al weg */ }
            try { venster?.Dispose(); } catch { /* al weg */ }
            _slot.Release();
        }
    }

    private static async Task<string> Js(CoreWebView2 core, string script)
    {
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch
        {
            return "null";
        }
    }

    private static List<IspFactuur> Parse(string json)
    {
        var regels = ApprovalRules.Load();
        var uit = new List<IspFactuur>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("invoices", out var invoices))
        {
            return uit;
        }
        foreach (var el in invoices.EnumerateArray())
        {
            string Get(string naam) => el.TryGetProperty(naam, out var v) ? v.GetString() ?? "" : "";
            var bedragText = Get("bedrag");
            var bedrag = InvoiceApprovalForm.ParseBedrag(bedragText);
            var leverancier = Get("leverancier");
            var factuurnummer = Get("factuurnummer");
            var valuta = Get("valuta");
            var (auto, reden) = ApprovalRules.Beoordeel(regels, leverancier, factuurnummer, bedrag, valuta);
            uit.Add(new IspFactuur(leverancier, factuurnummer, Get("factuurdatum"), Get("vervaldatum"),
                valuta, bedragText, bedrag,
                el.TryGetProperty("vervallen", out var w) && w.ValueKind == JsonValueKind.True, auto, reden));
        }
        return uit;
    }
}
