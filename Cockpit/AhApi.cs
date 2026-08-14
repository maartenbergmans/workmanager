using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Gedeelde toegang tot de publieke AH-mobiele-API (anonieme token → productdetail/zoeken).
/// Geen browser of login nodig: elk product is te vinden op zijn webshop-id (de "wi4076" uit
/// een productlink). Gebruikt door <see cref="AhAfbeeldingen"/> (foto's), de prijsindicatie en
/// de automatische productmatch. De token wordt gedeeld en hergebruikt tot vlak voor hij verloopt.
/// </summary>
public static class AhApi
{
    /// <summary>Of een product glutenvrij is volgens AH's dieet-facet.</summary>
    public enum GlutenStatus
    {
        Onbekend,
        Vrij,
        Bevat,
    }

    /// <summary>Kerngegevens van één AH-product uit de API.</summary>
    public sealed record ProductInfo(
        string WebshopId, string Titel, string? BeeldUrl,
        decimal? Prijs, decimal? PrijsVoorBonus, bool Bonus, GlutenStatus Gluten, string Url,
        string? Nutri = null);

    private static readonly Regex IdRegex =
        new(@"/product/wi(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Webshop-id uit een productlink ("…/product/wi4076/…" → "4076"), of null.</summary>
    public static string? WebshopId(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        var m = IdRegex.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string? _token;
    private static DateTime _tokenTot;
    private static readonly SemaphoreSlim _tokenPoort = new(1);

    static AhApi()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Appie/8.22.3");
    }

    /// <summary>Productdetail op webshop-id (het getal na "wi" in een productlink).</summary>
    public static async Task<ProductInfo?> DetailAsync(string webshopId, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"product/detail/v4/fir/{webshopId}", ct);
        if (doc is null)
        {
            return null;
        }
        using (doc)
        {
            return doc.RootElement.TryGetProperty("productCard", out var kaart)
                ? Lees(kaart)
                : null;
        }
    }

    /// <summary>Het beste zoekresultaat voor een ingrediëntnaam, of null als er niets past.</summary>
    public static async Task<ProductInfo?> ZoekTopAsync(string query, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync(
            $"product/search/v2?query={Uri.EscapeDataString(query)}&size=1", ct);
        if (doc is null)
        {
            return null;
        }
        using (doc)
        {
            return doc.RootElement.TryGetProperty("products", out var lijst) &&
                   lijst.ValueKind == JsonValueKind.Array && lijst.GetArrayLength() > 0
                ? Lees(lijst[0])
                : null;
        }
    }

    /// <summary>Vertaalt een product-JSON (productCard of zoekresultaat) naar <see cref="ProductInfo"/>.</summary>
    private static ProductInfo? Lees(JsonElement p)
    {
        if (!p.TryGetProperty("webshopId", out var idEl))
        {
            return null;
        }
        var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "";
        var titel = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

        string? beeld = null;
        if (p.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array &&
            imgs.GetArrayLength() > 0 && imgs[0].TryGetProperty("url", out var u) &&
            u.GetString() is { } ruw)
        {
            // GDI+ leest geen WebP: forceer een JPG-rendition.
            beeld = Regex.Replace(ruw, @"rendition=[^&]+", "rendition=200x200_JPG");
        }

        var voorBonus = Decimal(p, "priceBeforeBonus");
        var prijs = Decimal(p, "currentPrice") ?? voorBonus;
        var bonus = p.TryGetProperty("isBonus", out var b) && b.ValueKind == JsonValueKind.True;

        // AH's eigen dieet-facet: sp_include_… = glutenvrij, sp_exclude_… = bevat gluten.
        // In dezelfde properties zit ook de Nutri-Score (["C"]).
        var gluten = GlutenStatus.Onbekend;
        string? nutri = null;
        if (p.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("sp_include_intolerance_geen_gluten", out _))
            {
                gluten = GlutenStatus.Vrij;
            }
            else if (props.TryGetProperty("sp_exclude_intolerance_geen_gluten", out _))
            {
                gluten = GlutenStatus.Bevat;
            }
            if (props.TryGetProperty("nutriscore", out var ns) &&
                ns.ValueKind == JsonValueKind.Array && ns.GetArrayLength() > 0 &&
                ns[0].GetString() is { Length: 1 } letter)
            {
                nutri = letter.ToUpperInvariant();
            }
        }

        var url = $"https://www.ah.be/producten/product/wi{id}/{Slug(titel)}";
        return new ProductInfo(id, titel, beeld, prijs, voorBonus, bonus, gluten, url, nutri);
    }

    private static decimal? Decimal(JsonElement p, string naam) =>
        p.TryGetProperty(naam, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : null;

    /// <summary>Maakt een leesbare url-slug van een producttitel ("AH Winterpeen" → "ah-winterpeen").</summary>
    private static string Slug(string titel)
    {
        var slug = Regex.Replace(titel.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "product";
    }

    // ---------- HTTP + token ----------

    private static async Task<JsonDocument?> GetJsonAsync(string pad, CancellationToken ct)
    {
        try
        {
            var token = await TokenAsync(ct);
            if (token is null)
            {
                return null;
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.ah.nl/mobile-services/" + pad);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("X-Application", "AHWEBSHOP");
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenTot)
        {
            return _token;
        }
        await _tokenPoort.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenTot)
            {
                return _token;
            }
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous")
            {
                Content = new StringContent("{\"clientId\":\"appie\"}", Encoding.UTF8, "application/json"),
            };
            using var res = await Http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            var sec = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _tokenTot = DateTime.UtcNow.AddSeconds(Math.Max(60, sec - 120));
            return _token;
        }
        catch
        {
            return null;
        }
        finally
        {
            _tokenPoort.Release();
        }
    }
}
