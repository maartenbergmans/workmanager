using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkManager;

/// <summary>
/// Houdt de lokale producttabel (ah-producten.json) vanzelf actueel: maximaal één keer per
/// maand wordt /producten/eerder-gekocht opgehaald in de levende AH-sessie en worden de
/// producten die daar nieuw bij staan aan de tabel toegevoegd. Dat respecteert Maartens regel
/// per definitie — het zíjn zijn eigen bestellingen. Titels en nette urls komen via de
/// mobiele API; bestaande producten (en hun handmatig verfijnde trefwoorden) blijven
/// onaangeroerd. Moet — zoals alles met WebView2 — vanaf de UI-thread draaien.
/// </summary>
public static class AhProductOogst
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(30);

    /// <summary>Bij een mislukte poging (bv. login vereist) al na een dag opnieuw proberen.</summary>
    private static readonly TimeSpan Herkansing = TimeSpan.FromDays(1);

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string StateFile = Path.Combine(DataDir, "ah-product-oogst.json");
    private static readonly string CatalogusFile = Path.Combine(DataDir, "ah-producten.json");

    private static bool _bezig;

    /// <summary>Draait de maandelijkse oogst als hij aan de beurt is; stil bij tegenslag.</summary>
    public static async Task ZorgVoorAsync(Form eigenaar, CancellationToken ct)
    {
        if (_bezig || AhSessie.Instance.VensterZichtbaar || DateTime.Now.Hour < 7)
        {
            return;
        }
        var state = LaadState();
        if (state.LaatsteCheck is { } vorige && DateTimeOffset.Now - vorige < Interval)
        {
            return;
        }
        _bezig = true;
        try
        {
            state.LaatsteCheck = DateTimeOffset.Now;
            BewaarState(state);

            var (links, paginaTekst) = await AhSessie.Instance.EerderGekochtLinksAsync(ct);
            if (links.Count == 0)
            {
                // Pagina niet kunnen lezen (meestal: login gevraagd): morgen opnieuw.
                state.LaatsteCheck = DateTimeOffset.Now - Interval + Herkansing;
                BewaarState(state);
                SchrijfDebug(links.Count, new List<string>(), "geen links (login?)", paginaTekst);
                return;
            }

            var bekend = AhProducten.Alles
                .Select(p => AhApi.WebshopId(p.Url))
                .OfType<string>()
                .ToHashSet();
            var nieuw = new List<AhApi.ProductInfo>();
            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                if (AhApi.WebshopId(link) is not { } id || !bekend.Add(id))
                {
                    continue;
                }
                if (await AhApi.DetailAsync(id, ct) is { } info)
                {
                    nieuw.Add(info);
                }
            }
            SchrijfDebug(links.Count, nieuw.Select(p => p.Titel).ToList(), "ok", "");
            if (nieuw.Count == 0)
            {
                return;
            }
            if (VoegToeAanTabel(nieuw))
            {
                AhProducten.Herlaad();
                if (!eigenaar.IsDisposed)
                {
                    Toast.Toon(eigenaar,
                        $"AH-producttabel aangevuld: {nieuw.Count} nieuwe producten uit je bestelgeschiedenis",
                        Fluent.Winkelwagen);
                }
            }
        }
        catch
        {
            // Best effort; volgende ronde opnieuw.
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>
    /// Voegt de nieuwe producten achteraan de bestaande json toe (via JsonNode, zodat de rest
    /// van het bestand — volgorde, handmatige trefwoorden — intact blijft). Als trefwoord
    /// krijgt elk product zijn eigen naam zonder het merkvoorvoegsel ("AH Broccoli" →
    /// "broccoli"); de matcher probeert de volledige naam sowieso al.
    /// </summary>
    private static bool VoegToeAanTabel(List<AhApi.ProductInfo> nieuw)
    {
        try
        {
            if (!File.Exists(CatalogusFile) ||
                JsonNode.Parse(File.ReadAllText(CatalogusFile)) is not { } wortel ||
                wortel["producten"] is not JsonArray producten)
            {
                return false;
            }
            foreach (var info in nieuw)
            {
                var trefwoord = ZonderMerk(info.Titel);
                var trefwoorden = new JsonArray();
                if (trefwoord.Length > 2)
                {
                    trefwoorden.Add(trefwoord);
                }
                producten.Add(new JsonObject
                {
                    ["naam"] = info.Titel,
                    ["url"] = info.Url,
                    ["trefwoorden"] = trefwoorden,
                });
            }
            File.WriteAllText(CatalogusFile,
                wortel.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"AH Terra Plantaardige soepballetjes" → "terra plantaardige soepballetjes".</summary>
    private static string ZonderMerk(string titel)
    {
        var naam = titel.Trim();
        foreach (var merk in new[] { "AH Biologisch ", "AH " })
        {
            if (naam.StartsWith(merk, StringComparison.OrdinalIgnoreCase))
            {
                naam = naam[merk.Length..];
                break;
            }
        }
        return naam.ToLowerInvariant();
    }

    private static void SchrijfDebug(int gevonden, List<string> nieuw, string uitkomst, string paginaTekst)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "ah-oogst-debug.json"),
                JsonSerializer.Serialize(new
                {
                    datum = DateTimeOffset.Now,
                    uitkomst,
                    linksGevonden = gevonden,
                    nieuweProducten = nieuw,
                    // Waar bleef de pagina op hangen? ("Even controleren" = login nodig.)
                    paginaTekst = paginaTekst.Length > 800 ? paginaTekst[..800] + "…" : paginaTekst,
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    // ---------- state ----------

    private sealed class State
    {
        public DateTimeOffset? LaatsteCheck { get; set; }
    }

    private static State LaadState()
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
            // Onleesbaar: als "nog nooit" behandelen.
        }
        return new State();
    }

    private static void BewaarState(State state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort.
        }
    }
}
