using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Eén subtaak onder een teamtaak: eigen tekst, afvinkstatus en prioriteit (sterren), zodat
/// subtaken in het venster individueel af te vinken en te prioriteren zijn en in de weekmail
/// ingesprongen onder de hoofdtaak komen.
/// </summary>
public sealed class SubTaak
{
    public string Tekst { get; set; } = "";
    public bool Klaar { get; set; }
    /// <summary>0 = hoog, 1 = normaal, 2 = laag.</summary>
    public int Prioriteit { get; set; } = 1;
}

/// <summary>Eén taak voor een teamlid.</summary>
public sealed class TeamTaak
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Lid { get; set; } = "";
    public string Tekst { get; set; } = "";
    /// <summary>0 = hoog (gemarkeerd in de weekmail), 1 = normaal, 2 = laag.</summary>
    public int Prioriteit { get; set; } = 1;

    /// <summary>Subtaken: komen in de weekmail als ingesprongen opsomming onder de taak.</summary>
    [JsonConverter(typeof(SubtakenConverter))]
    public List<SubTaak> Subtaken { get; set; } = new();
    public bool Klaar { get; set; }
    public DateTimeOffset Aangemaakt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? KlaarOp { get; set; }
}

/// <summary>
/// Leest de subtaken zowel uit het oude formaat (een lijst platte strings) als uit het
/// nieuwe (objecten met tekst/klaar/prioriteit). Zo blijven bestaande team-tasks.json'en
/// werken; bij de eerstvolgende save worden ze in het nieuwe formaat weggeschreven.
/// </summary>
public sealed class SubtakenConverter : JsonConverter<List<SubTaak>>
{
    public override List<SubTaak> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var lijst = new List<SubTaak>();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return lijst;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var tekst = reader.GetString() ?? "";
                if (tekst.Trim().Length > 0)
                {
                    lijst.Add(new SubTaak { Tekst = tekst.Trim() });
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var sub = new SubTaak();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var naam = reader.GetString() ?? "";
                    reader.Read();
                    switch (naam.ToLowerInvariant())
                    {
                        case "tekst":
                            sub.Tekst = reader.GetString() ?? "";
                            break;
                        case "klaar":
                            sub.Klaar = reader.TokenType == JsonTokenType.True;
                            break;
                        case "prioriteit":
                            sub.Prioriteit = reader.TokenType == JsonTokenType.Number
                                ? reader.GetInt32() : 1;
                            break;
                    }
                }
                if (sub.Tekst.Trim().Length > 0)
                {
                    lijst.Add(sub);
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return lijst;
    }

    public override void Write(
        Utf8JsonWriter writer, List<SubTaak> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var sub in value)
        {
            writer.WriteStartObject();
            writer.WriteString("tekst", sub.Tekst);
            writer.WriteBoolean("klaar", sub.Klaar);
            writer.WriteNumber("prioriteit", sub.Prioriteit);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>Eén handmatig ingegeven vakantieperiode (van t/m tot, beide inclusief).</summary>
public sealed class VakantiePeriode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Persoon { get; set; } = "";
    public DateOnly Van { get; set; }
    public DateOnly Tot { get; set; }
}

/// <summary>
/// Alle takendata: teamleden (in mailvolgorde), taken, de opmerking bovenaan de
/// weekmail, de mailontvangers en handmatig ingegeven vakanties.
/// </summary>
public sealed class TeamTasksData
{
    public List<string> Leden { get; set; } = new() { "Wim", "Christophe", "Laurent", "Alex", "Kris", "Henny", "Ludo" };
    public List<TeamTaak> Taken { get; set; } = new();
    public string Opmerking { get; set; } = "";
    public string MailAan { get; set; } = "";

    /// <summary>Leden die wel taken kunnen krijgen maar nooit in de weekmail komen.</summary>
    public List<string> NietInMail { get; set; } = new() { "Ludo" };

    /// <summary>
    /// Handmatige vakanties (los van SD Worx), ook voor Maarten zelf: een lid dat de hele
    /// werkweek afwezig is krijgt in de weekmail geen taken toegewezen.
    /// </summary>
    public List<VakantiePeriode> Vakanties { get; set; } = new();
}

/// <summary>
/// Persistentie voor het takensysteem: team-tasks.json (leden, taken, opmerking,
/// ontvangers) en team-mail-style.txt (voorbeeldmails die de stijl van de weekmail
/// bepalen — vrij te bewerken via "Stijl weekmail…").
/// </summary>
public static class TeamTaskStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string TasksFile = Path.Combine(DataDir, "team-tasks.json");
    private static readonly string StijlFile = Path.Combine(DataDir, "team-mail-style.txt");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static TeamTasksData Load()
    {
        try
        {
            if (File.Exists(TasksFile))
            {
                var data = JsonSerializer.Deserialize<TeamTasksData>(File.ReadAllText(TasksFile), JsonOpts);
                if (data is not null)
                {
                    return data;
                }
            }
        }
        catch
        {
            // Onleesbaar bestand: start met defaults (wordt bij de eerstvolgende save hersteld).
        }
        return new TeamTasksData();
    }

    public static void Save(TeamTasksData data)
    {
        // Lang verlopen vakanties opruimen zodat de lijst niet eindeloos groeit.
        data.Vakanties.RemoveAll(v => v.Tot < DateOnly.FromDateTime(DateTime.Now).AddDays(-30));
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(TasksFile, JsonSerializer.Serialize(data, JsonOpts));
    }

    public static string LoadStijl()
    {
        try
        {
            if (File.Exists(StijlFile))
            {
                return File.ReadAllText(StijlFile);
            }
        }
        catch
        {
            // Onleesbaar bestand: val terug op de voorbeeldmails.
        }

        SaveStijl(DefaultStijl);
        return DefaultStijl;
    }

    public static void SaveStijl(string tekst)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(StijlFile, tekst);
    }

    private const string DefaultStijl =
        """
        === Voorbeeld 1 ===

        Bonjour tout le monde,

        Voici les priorités pour la semaine prochaine. Henny en congé la semaine prochaine (donc merci d'assister aussi avec les tickets)

        Wim:
        Verder omzetten AS400 planningsprocedures naar service via claude

        Christophe:
        Opvolgen SSP End of lease indien nog aanpassingen nodig
        Bij aanmaak nieuw dossier op gekende nummerplaat, overnemen gegevens voertuig en schadelijder in nieuwe opdracht
        Vereenvoudigd end of lease dossier aanmaken
        documenten dossier, bij verwijderen document in opmaak, cc documenten ook verwijderen
        Afsluiten dossiers in advies moet mogelijk zijn volgens logica carex

        Laurent:
        Informextab integreren in Carex Mobile (important de finir ça cette semaine)

        Alex:
        Implement and test security patches webserver
        Restructure robot Mobility

        Kris:
        Dubbele documenten Ethias
        Changes property op basis lijst Tiemen

        Met vriendelijke groeten, Sincères salutations,

        === Voorbeeld 2 ===

        Hi all,

        Find below the priorities for next week:

        Laurent:
        End of lease: VDFin et mettre en teste la nouvelle version iPad
        Ajouter facturation libre en carex mobile
        Ajouter menu informex en carex mobile
        Changer mission ubench/informex vers un autre dossier

        Kris:
        Documentsysteem en powerbi AS400 omzetten naar service
        Changes Tiemen analyseren

        Christophe:
        Wijzigingen ivm SSP voor Pierre
        Carex Mobile
        pdf omzetten naar foto en omgekeerd
        overnemen btw Percentage bij kiezen SL bij aanmaak dossier
        bij aanmaak dossier schadedatum nogmaals correct te zetten - wordt niet goed overgenomen van beginscherm aanmaak dossier
        Ingave technische keuring
        niet mogelijk om te mailen indien voertuigmerk niet ingevuld staat (zoals bev afspraak)
        voorstel naar sl sturen (bv TV) indien geen mailadres is niet mogelijk (als per post te versturen)
        in bestaand dossier kan een schadelijder niet meer gekozen worden en is deze manueel aan te vullen indien niet goed aangemaakt

        Alex:
        Bring connect value maintenance screen for questions to production
        Check password based on the hash in existing application
        Analyze possibilities for testing existing application

        Wim:
        Planning AS400 omzetten naar service

        === Voorbeeld 3 ===

        Beste collega's,

        Hieronder de prioriteiten voor volgende week:

        Henny, Kris en ik zelf zijn heel de week afwezig. Dus graag de tickets opvolgen.

        Alex
          • Migrate old mobility service to new mobility service
          • Prepare automated test for mobility api (for example patch a bodyshop). Demo to the team 14/4.
          • CED Admin should be able to add and edit a broker in Connect Value

        Christophe
          • Opvolgen La Cour Calculatie en opstart Arval Key 4 Key
          • VDFin SSP architectuur bekijken
          • Carex Mobile Parameter medewerker toevoegen / andere punten carex mobile

        Wim (afwezig donderdag 9 april)
          • Facturatie VDFin implementeren zoals Arval bij meerdere voertuigen op dezelfde locatie
          • AS400 - planning migratie

        Laurent
          • Pitstop followup
          • Convert remaining end of lease clients to CED app
          • Mettre menu informex de carex en carex mobile

        Tot binnen een week!
        """;
}
