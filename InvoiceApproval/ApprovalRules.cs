using System.Text.Json;

namespace WorkManager;

public class ApprovalRule
{
    public string Leverancier { get; set; } = "";
    public decimal MaxBedrag { get; set; }
}

/// <summary>
/// Auto-goedkeuringsregels voor ISPnext: per leverancier het maximumbedrag dat zonder
/// individuele beoordeling goedgekeurd mag worden. Persistent in %APPDATA%\WorkManager.
/// </summary>
public static class ApprovalRules
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string RulesFile = Path.Combine(DataDir, "invoice-approval-rules.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<ApprovalRule> Load()
    {
        try
        {
            if (File.Exists(RulesFile))
            {
                var rules = JsonSerializer.Deserialize<List<ApprovalRule>>(File.ReadAllText(RulesFile), JsonOpts);
                if (rules is not null)
                {
                    return rules;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op defaults (bestand wordt bij eerstvolgende save hersteld).
        }

        var defaults = Defaults();
        Save(defaults);
        return defaults;
    }

    public static void Save(List<ApprovalRule> rules)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(RulesFile, JsonSerializer.Serialize(
            rules.OrderByDescending(r => r.MaxBedrag).ThenBy(r => r.Leverancier).ToList(), JsonOpts));
    }

    /// <summary>Vindt de regel voor een leverancier (exacte naam, hoofdletterongevoelig).</summary>
    public static ApprovalRule? Match(IEnumerable<ApprovalRule> rules, string leverancier) =>
        rules.FirstOrDefault(r => string.Equals(
            r.Leverancier.Trim(), leverancier.Trim(), StringComparison.OrdinalIgnoreCase));

    private static List<ApprovalRule> Defaults() => new()
    {
        new() { Leverancier = "Proximus (vaste telefonie)", MaxBedrag = 25000 },
        new() { Leverancier = "Proximus business", MaxBedrag = 25000 },
        new() { Leverancier = "Coolblue (België NV)", MaxBedrag = 15000 },
        new() { Leverancier = "FLE IT", MaxBedrag = 10000 },
        new() { Leverancier = "De Lage Landen Leasing NV", MaxBedrag = 6000 },
        new() { Leverancier = "Orange", MaxBedrag = 5000 },
        new() { Leverancier = "Jaan BVBA", MaxBedrag = 5000 },
        new() { Leverancier = "Canon Business Center Brussel", MaxBedrag = 3500 },
        new() { Leverancier = "GBK", MaxBedrag = 2500 },
        new() { Leverancier = "Aviloo (Battery diagnostics)", MaxBedrag = 2500 },
        new() { Leverancier = "ComponentSource Ltd", MaxBedrag = 2500 },
        new() { Leverancier = "A&M (Proximus)", MaxBedrag = 2500 },
        new() { Leverancier = "Anthropic Ireland Limited", MaxBedrag = 2500 },
        new() { Leverancier = "UBench International NV", MaxBedrag = 2000 },
        new() { Leverancier = "Impact Software NV", MaxBedrag = 1500 },
        new() { Leverancier = "Orange - ICT Experts", MaxBedrag = 1500 },
        new() { Leverancier = "Datacenter United Brussels NV", MaxBedrag = 1000 },
        new() { Leverancier = "AVF - Security & Network Solutions", MaxBedrag = 1000 },
        new() { Leverancier = "Telenet", MaxBedrag = 1000 },
        new() { Leverancier = "Dstny BE NV", MaxBedrag = 500 },
        new() { Leverancier = "Dynatos NV", MaxBedrag = 500 },
        new() { Leverancier = "CM.Com", MaxBedrag = 250 },
    };
}
