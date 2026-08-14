using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Eén VIP: een persoon of een gesprek waarvan berichten voorrang krijgen. De sleutel is bewust
/// vrije tekst en geen strak e-mailadres, want een VIP kan net zo goed een WhatsApp-groep of een
/// Teams-chat zijn. Wat de sleutel betekent, staat in <see cref="Soort"/>.
/// </summary>
public sealed class VipItem
{
    /// <summary>Waarop we matchen: een e-mailadres, of de naam van een chat/afzender.</summary>
    public string Sleutel { get; set; } = "";

    /// <summary>"mail" of "chat" — puur voor de weergave in de beheerlijst.</summary>
    public string Soort { get; set; } = "mail";

    /// <summary>Hoe de VIP in de lijst heet; leeg = toon de sleutel.</summary>
    public string Naam { get; set; } = "";

    public string Weergave => Naam.Length > 0 ? $"{Naam} ({Sleutel})" : Sleutel;
}

/// <summary>
/// De VIP-lijst: afzenders en gesprekken die voorrang krijgen in de berichtencockpit. Ze komen
/// bovenaan te staan, worden met een ster gemarkeerd en leveren een tray-melding op zodra er een
/// nieuw bericht van binnenkomt.
///
/// Persistent in %APPDATA%\WorkManager\vip-lijst.json. Bewust een eigen bestandje: de lijst wordt
/// vanuit twee kanten bewerkt (rechtsklik in de cockpit en het beheervenster) en moet dus tussen
/// vensters door telkens vers ingelezen kunnen worden.
/// </summary>
public static class VipLijst
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string LijstFile = Path.Combine(DataDir, "vip-lijst.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Er is een nieuw bericht van een VIP (titel, tekst). De tray hangt hieraan om er een
    /// ballontip van te maken; de cockpit vuurt het af, want die haalt de berichten op.
    /// </summary>
    public static event Action<string, string>? Melding;

    public static void Meld(string titel, string tekst) => Melding?.Invoke(titel, tekst);

    /// <summary>De lijst is bewaard. De cockpit hangt hieraan om meteen te herschikken.</summary>
    public static event Action? Gewijzigd;

    public static List<VipItem> Laad()
    {
        try
        {
            if (File.Exists(LijstFile))
            {
                var lijst = JsonSerializer.Deserialize<List<VipItem>>(File.ReadAllText(LijstFile), JsonOpts);
                if (lijst is not null)
                {
                    return lijst.Where(v => v.Sleutel.Trim().Length > 0).ToList();
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: begin met een lege lijst; de eerstvolgende bewaaractie herstelt ze.
        }
        return new List<VipItem>();
    }

    public static void Bewaar(IEnumerable<VipItem> lijst)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(LijstFile, JsonSerializer.Serialize(
            lijst.Where(v => v.Sleutel.Trim().Length > 0)
                 .GroupBy(v => v.Sleutel.Trim(), StringComparer.OrdinalIgnoreCase)
                 .Select(g => g.First())
                 .ToList(),
            JsonOpts));
        Gewijzigd?.Invoke();
    }

    /// <summary>
    /// De sleutels waarop een bericht als VIP herkend kan worden: het e-mailadres, de chatnaam en
    /// de weergavenaam van de afzender. Eén ervan volstaat, zodat je iemand zowel op adres als op
    /// naam kunt aanduiden zonder te weten via welk kanaal hij binnenkomt.
    /// </summary>
    public static IEnumerable<string> SleutelsVan(MailBericht m)
    {
        if (m.VanAdres.Length > 0 && m.VanAdres.Contains('@')) { yield return m.VanAdres; }
        if (m.AntwoordAan.Length > 0 && m.AntwoordAan.Contains('@')) { yield return m.AntwoordAan; }
        if (m.ChatSpace.Length > 0) { yield return m.ChatSpace; }
        if (m.WhatsAppChat.Length > 0) { yield return m.WhatsAppChat; }
        if (m.TeamsChat.Length > 0) { yield return m.TeamsChat; }
        if (m.Van.Length > 0) { yield return m.Van; }
    }

    /// <summary>
    /// De sleutel die het beste bij dit bericht past om het als VIP te bewaren: bij een mail het
    /// adres, bij een chat de gespreksnaam. Zo blijft de lijst leesbaar én blijft ze werken als
    /// dezelfde persoon morgen onder een iets andere weergavenaam binnenkomt.
    /// </summary>
    public static VipItem VoorstelVoor(MailBericht m)
    {
        if (m.ChatSpace.Length > 0) { return new VipItem { Sleutel = m.ChatSpace, Soort = "chat", Naam = m.Van }; }
        if (m.WhatsAppChat.Length > 0) { return new VipItem { Sleutel = m.WhatsAppChat, Soort = "chat" }; }
        if (m.TeamsChat.Length > 0) { return new VipItem { Sleutel = m.TeamsChat, Soort = "chat" }; }
        if (m.VanAdres.Length > 0 && m.VanAdres.Contains('@'))
        {
            return new VipItem { Sleutel = m.VanAdres, Soort = "mail", Naam = m.Van };
        }
        return new VipItem { Sleutel = m.Van, Soort = "mail" };
    }

    /// <summary>Snelle test tegen een vooraf ingelezen lijst (de berichtenlijst roept dit per rij aan).</summary>
    public static bool IsVip(MailBericht m, HashSet<string> sleutels) =>
        sleutels.Count > 0 && SleutelsVan(m).Any(sleutels.Contains);

    /// <summary>De sleutels als set, klaar om per rij tegen te testen.</summary>
    public static HashSet<string> AlsSet(IEnumerable<VipItem> lijst) =>
        lijst.Select(v => v.Sleutel.Trim()).Where(s => s.Length > 0)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsVip(MailBericht m) => IsVip(m, AlsSet(Laad()));
}
