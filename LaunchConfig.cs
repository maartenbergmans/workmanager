using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkManager;

/// <summary>
/// Per-klant acties die uitgevoerd worden bij een switch. Bewerkbaar via
/// %APPDATA%\WorkManager\launch-config.json (wordt met defaults aangemaakt als het ontbreekt).
/// </summary>
public sealed class LaunchConfig
{
    private static readonly string ConfigFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager", "launch-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Gedeelde instellingen voor de UrbanAdmin-timesheetkoppeling.</summary>
    public TimesheetSettings? Timesheets { get; set; }

    public Dictionary<string, ClientActions?> Clients { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static LaunchConfig LoadOrCreate()
    {
        if (File.Exists(ConfigFile))
        {
            try
            {
                return JsonSerializer.Deserialize<LaunchConfig>(File.ReadAllText(ConfigFile), JsonOpts) ?? Default();
            }
            catch
            {
                return Default();
            }
        }

        var config = Default();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, JsonOpts));
        return config;
    }

    private static LaunchConfig Default() => new()
    {
        Timesheets = new TimesheetSettings(),
        Clients = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Aqurat"] = new ClientActions
            {
                Timesheet = new TimesheetAction { ProjectId = 114 },
                PhpStorm = new JetBrainsProject
                {
                    ProjectPath = @"\\wsl.localhost\ubuntu\home\maarten\projecten\aqurat",
                    WindowTitleMatch = "aqurat",
                    Monitor = 1,
                },
                DataGrip = new JetBrainsProject
                {
                    ProjectPath = @"C:\Users\maart\DataGripProjects\Aqurat",
                    WindowTitleMatch = "aqurat",
                    Monitor = 3,
                },
                Browser = new BrowserAction
                {
                    Url = "http://localhost:4200/app/",
                    WindowTitleMatch = "localhost",
                    WaitForApp = true,
                    Monitor = 2,
                    ExtraWindows =
                    [
                        new BrowserWindow
                        {
                            Url = "http://localhost:8025/view/2SlTp7PR8Nd6IQ1E2lgB5h",
                            WindowTitleMatch = "Mailpit",
                        },
                        new BrowserWindow
                        {
                            Url = "https://app.asana.com/1/1351254360575/project/1213265431188205/list/1213265461997200",
                            WindowTitleMatch = "Asana",
                        },
                    ],
                },
                Claude = new ClaudeAction
                {
                    WorkingDirectory = @"\\wsl.localhost\ubuntu\home\maarten\projecten\aqurat",
                },
            },
            ["RadiologyPartners"] = new ClientActions
            {
                Timesheet = new TimesheetAction { ProjectId = 99 },
                PhpStorm = new JetBrainsProject
                {
                    ProjectPath = @"\\wsl.localhost\ubuntu\home\maarten\projecten\bloom-datawarehouse",
                    WindowTitleMatch = "bloom-datawarehouse",
                },
                Browser = new BrowserAction
                {
                    Url = "https://datawarehouse.bloom-caregroup.com/datastatus.php",
                    WindowTitleMatch = "Logs Overview",
                    Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    ProcessName = "chrome",
                },
            },
            ["CED"] = new ClientActions
            {
                Timesheet = new TimesheetAction { ProjectId = 1 },
                Programs =
                [
                    // Nieuwe Outlook (Store-app); de alias staat in %LOCALAPPDATA%\Microsoft\WindowsApps.
                    new ProgramAction
                    {
                        Path = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            @"Microsoft\WindowsApps\olk.exe"),
                        ProcessName = "olk",
                    },
                ],
            },
        },
    };
}

/// <summary>Instellingen voor de UrbanAdmin-timesheetkoppeling (endpoints /api/workmanager/...).</summary>
public sealed class TimesheetSettings
{
    public string BaseUrl { get; set; } = "https://timesheets.urbanit.be/api";

    /// <summary>Moet gelijk zijn aan WORKMANAGER_TOKEN in de .env van UrbanAdmin; leeg = koppeling uit.</summary>
    public string Token { get; set; } = "";

    public int GebruikerId { get; set; } = 1;
}

/// <summary>Start bij het aanzetten van de context een werkuur in UrbanAdmin; uitzetten zet de eindtijd.</summary>
public sealed class TimesheetAction
{
    public int ProjectId { get; set; }

    /// <summary>Omschrijving (extra) van het werkuur; leeg = "WorkManager &lt;context&gt;".</summary>
    public string Omschrijving { get; set; } = "";
}

public sealed class ClientActions
{
    public TimesheetAction? Timesheet { get; set; }
    public JetBrainsProject? PhpStorm { get; set; }
    public JetBrainsProject? DataGrip { get; set; }
    public BrowserAction? Browser { get; set; }
    public ClaudeAction? Claude { get; set; }
    public List<ProgramAction>? Programs { get; set; }
}

/// <summary>Willekeurig programma dat bij het aanzetten van de context start (bv. Outlook).</summary>
public sealed class ProgramAction
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";

    /// <summary>Procesnaam (zonder .exe) voor open-detectie en voor het sluiten bij uitzetten.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Optioneel: alleen vensters met deze tekst in de titel tellen mee.</summary>
    public string WindowTitleMatch { get; set; } = "";

    /// <summary>Scherm (1 = meest links) waarop het venster gemaximaliseerd wordt; alleen als dat scherm bestaat.</summary>
    public int? Monitor { get; set; }
}

public sealed class JetBrainsProject
{
    public string ProjectPath { get; set; } = "";

    /// <summary>Al open als een venster van de IDE deze tekst in de titel heeft.</summary>
    public string WindowTitleMatch { get; set; } = "";

    /// <summary>Scherm (1 = meest links) waarop het venster gemaximaliseerd wordt; alleen als dat scherm bestaat.</summary>
    public int? Monitor { get; set; }
}

public sealed class BrowserAction
{
    public string Url { get; set; } = "";

    /// <summary>Al open als een browservenster deze tekst in de titel heeft (alleen actieve tab zichtbaar).</summary>
    public string WindowTitleMatch { get; set; } = "";

    public string Path { get; set; } = @"C:\Program Files\Mozilla Firefox\firefox.exe";

    /// <summary>Procesnaam (zonder .exe) voor open-detectie en voor het sluiten bij uitzetten.</summary>
    public string ProcessName { get; set; } = "firefox";

    /// <summary>Wacht tot de URL bereikbaar is (app gestart vanuit PhpStorm) voordat de browser opent.</summary>
    public bool WaitForApp { get; set; }

    /// <summary>Scherm (1 = meest links) waarop de browservensters gemaximaliseerd worden; alleen als dat scherm bestaat.</summary>
    public int? Monitor { get; set; }

    /// <summary>Extra browservensters die na het hoofdvenster geopend worden (bv. Mailpit, Asana).</summary>
    public List<BrowserWindow>? ExtraWindows { get; set; }
}

public sealed class BrowserWindow
{
    public string Url { get; set; } = "";

    /// <summary>Al open als een browservenster deze tekst in de titel heeft; ook gebruikt bij het sluiten.</summary>
    public string WindowTitleMatch { get; set; } = "";
}

public sealed class ClaudeAction
{
    /// <summary>Al open als een claude.exe-proces deze working directory heeft.</summary>
    public string WorkingDirectory { get; set; } = "";
}
