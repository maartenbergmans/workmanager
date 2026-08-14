using System.Collections.Concurrent;

namespace WorkManager;

/// <summary>
/// Haalt (op de achtergrond) de productgegevens bij een AH-link op — prijs, bonus én of het
/// glutenvrij is — en cachet die voor deze sessie. Zo kan de keuzestap een geschat mandjetotaal
/// tonen, bonusproducten markeren en waarschuwen voor producten die gluten bevatten. Bewust geen
/// schijf­cache: prijzen en bonussen wisselen.
/// </summary>
public static class AhDetails
{
    private static readonly ConcurrentDictionary<string, AhApi.ProductInfo> _cache = new();
    private static readonly ConcurrentDictionary<string, byte> _klaar = new(); // opgehaald (met of zonder resultaat)
    private static readonly SemaphoreSlim _poort = new(4);

    /// <summary>Vuurt (op een achtergrondthread) zodra er nieuwe productgegevens bekend zijn.</summary>
    public static event Action? Klaar;

    /// <summary>De gecachete productgegevens voor een link, of null als ze (nog) niet bekend zijn.</summary>
    public static AhApi.ProductInfo? Voor(string? productUrl)
    {
        if (AhApi.WebshopId(productUrl) is not { } id)
        {
            return null;
        }
        if (_cache.TryGetValue(id, out var info))
        {
            return info;
        }
        if (!_klaar.ContainsKey(id) && _klaar.TryAdd(id, 0))
        {
            _ = Task.Run(() => Ophalen(id));
        }
        return null;
    }

    private static async Task Ophalen(string id)
    {
        try
        {
            await _poort.WaitAsync();
            try
            {
                if (await AhApi.DetailAsync(id) is { } info)
                {
                    _cache[id] = info;
                    Klaar?.Invoke();
                }
            }
            finally
            {
                _poort.Release();
            }
        }
        catch
        {
            // Geen gegevens deze sessie; id blijft in _klaar zodat we niet blijven proberen.
        }
    }
}
