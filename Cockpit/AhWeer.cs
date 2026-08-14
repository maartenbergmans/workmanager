using System.Net.Http;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Weerhulp voor het weekmenu: bij zomerse vooruitzichten (maximum ≥ 22 °C in de komende
/// dagen) mogen BBQ- en koude gerechten iets vaker voorgesteld worden. Haalt maximaal één
/// keer per dag de verwachting op bij Open-Meteo (gratis, geen sleutel); lukt dat niet, dan
/// telt het weer gewoon niet mee.
/// </summary>
public static class AhWeer
{
    /// <summary>Vanaf deze verwachte maximumtemperatuur telt het als zomers weer.</summary>
    private const double ZomerseGraden = 22;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static DateOnly _opgehaald;
    private static double? _maxTemp;

    /// <summary>Of de komende dagen zomers ogen (false zolang er geen verwachting bekend is).</summary>
    public static bool Zomers => _maxTemp is { } t && t >= ZomerseGraden;

    /// <summary>Haalt de verwachting op als dat vandaag nog niet gebeurd is (stil bij falen).</summary>
    public static async Task VerversAsync()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        if (_opgehaald == vandaag)
        {
            return;
        }
        _opgehaald = vandaag; // één poging per dag, ook als hij mislukt
        try
        {
            // België (Brussel als middelpunt): op weekmenu-schaal is dat precies genoeg.
            var json = await Http.GetStringAsync(
                "https://api.open-meteo.com/v1/forecast?latitude=50.85&longitude=4.35" +
                "&daily=temperature_2m_max&forecast_days=5&timezone=Europe%2FBrussels");
            using var doc = JsonDocument.Parse(json);
            var dagen = doc.RootElement.GetProperty("daily").GetProperty("temperature_2m_max");
            double? max = null;
            foreach (var dag in dagen.EnumerateArray())
            {
                if (dag.ValueKind == JsonValueKind.Number)
                {
                    var t = dag.GetDouble();
                    max = max is { } m ? Math.Max(m, t) : t;
                }
            }
            _maxTemp = max;
        }
        catch
        {
            _maxTemp = null; // geen verwachting: het weer telt gewoon niet mee
        }
    }
}
