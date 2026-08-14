using System.Collections.Concurrent;

namespace WorkManager;

/// <summary>
/// Zoekt (op de achtergrond) via de AH-zoek-API het beste product bij een ingrediëntnaam, voor
/// ingrediënten die geen eigen link hebben én niet in de lokale producttabel voorkomen. Zo hoeft
/// er veel minder handmatig gezocht te worden; het resultaat is een gok (≈) die je kunt houden
/// of vervangen. Per sessie gecachet (ook de "niets gevonden"-uitkomst).
/// </summary>
public static class AhZoeker
{
    private static readonly ConcurrentDictionary<string, AhApi.ProductInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _klaar = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _poort = new(4);

    /// <summary>Vuurt (op een achtergrondthread) zodra er een nieuwe match gevonden is.</summary>
    public static event Action? MatchKlaar;

    /// <summary>Het gevonden product voor een ingrediëntnaam, of null als er (nog) geen is.</summary>
    public static AhApi.ProductInfo? Voor(string naam)
    {
        var sleutel = naam.Trim();
        if (sleutel.Length == 0)
        {
            return null;
        }
        if (_cache.TryGetValue(sleutel, out var info))
        {
            return info;
        }
        if (!_klaar.ContainsKey(sleutel) && _klaar.TryAdd(sleutel, 0))
        {
            _ = Task.Run(() => Zoek(sleutel));
        }
        return null;
    }

    private static async Task Zoek(string naam)
    {
        try
        {
            await _poort.WaitAsync();
            try
            {
                // Alleen een echt product met link is bruikbaar als automatische match.
                if (await AhApi.ZoekTopAsync(naam) is { } info && AhApi.WebshopId(info.Url) is not null)
                {
                    _cache[naam] = info;
                    MatchKlaar?.Invoke();
                }
            }
            finally
            {
                _poort.Release();
            }
        }
        catch
        {
            // Niets gevonden deze sessie; naam blijft in _klaar zodat we niet blijven zoeken.
        }
    }
}
