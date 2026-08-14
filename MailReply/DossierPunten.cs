using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkManager;

/// <summary>
/// Haalt de openstaande punten uit de klantdossiers en zet ze als taken op de lijst. In die
/// dossiers staat per klant een kopje in de trant van "Wat er nu nog openstaat"; wat daar
/// staat is precies het werk dat anders in een tekstbestand blijft liggen (een rekeningnummer
/// dat nooit doorkwam, een vraag die nooit beantwoord is).
///
/// <para>Eén keer per week (maandag) wordt er gekeken; elk punt wordt hooguit één keer een
/// taak. Streep je een punt in het dossier weg, dan komt het ook niet meer terug.</para>
/// </summary>
public static class DossierPunten
{
    public const string TaakPrefix = "📁 Openstaand";

    /// <summary>Hooguit zoveel nieuwe taken per klant per week.</summary>
    private const int MaxPerKlant = 4;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "dossier-punten.json");

    private static readonly string DossierDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "klantdossiers");

    private sealed class State
    {
        public string LaatsteScan { get; set; } = "";     // yyyy-'W'ww
        public List<string> GemeldePunten { get; set; } = new(); // "klant|hash"
    }

    /// <summary>Scant de dossiers en maakt taken van nieuwe openstaande punten.</summary>
    public static void ZorgVoorTaken()
    {
        try
        {
            if (DateTime.Now.DayOfWeek != DayOfWeek.Monday || DateTime.Now.Hour < 8 ||
                !Directory.Exists(DossierDir))
            {
                return;
            }
            var week = $"{DateTime.Now:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now):00}";
            var state = Laad();
            if (state.LaatsteScan == week)
            {
                return;
            }
            state.LaatsteScan = week;

            var taken = MijnTaakStore.Load();
            var nieuw = 0;
            foreach (var pad in Directory.EnumerateFiles(DossierDir, "*.md"))
            {
                var klant = Path.GetFileNameWithoutExtension(pad);
                var dezeKlant = 0;
                foreach (var punt in PuntenUit(File.ReadAllText(pad)))
                {
                    // Hooguit een paar per klant per week: de eerste scan van een nieuw
                    // dossier levert er tien tegelijk op, en dan is de lijst onbruikbaar.
                    // De rest volgt vanzelf de maandagen erna.
                    if (dezeKlant >= MaxPerKlant)
                    {
                        break;
                    }
                    var sleutel = $"{klant}|{Hash(punt)}";
                    if (state.GemeldePunten.Contains(sleutel))
                    {
                        continue;
                    }
                    state.GemeldePunten.Add(sleutel);
                    taken.Taken.Add(new MijnTaak
                    {
                        Tekst = $"{TaakPrefix} {Hoofdletter(klant)}: {Kort(punt, 90)}",
                        Categorie = CategorieVoor(klant, taken.Categorieen),
                        Prioriteit = 1,
                        // Bewust geen deadline: dit is werk om in te plannen, niet om
                        // vandaag te moeten doen. Wel meteen zichtbaar.
                        Mail = new TaakMail
                        {
                            Onderwerp = $"Openstaand punt — {Hoofdletter(klant)}",
                            Tekst = punt + "\n\n(Uit het klantdossier " + Path.GetFileName(pad) + ")",
                        },
                    });
                    nieuw++;
                    dezeKlant++;
                }
            }
            if (nieuw > 0)
            {
                MijnTaakStore.Save(taken);
            }
            Bewaar(state);
        }
        catch
        {
            // Best effort: volgende maandag opnieuw.
        }
    }

    /// <summary>
    /// De punten onder een kopje dat over openstaand werk gaat. Genummerde of gestreepte
    /// opsommingen tellen mee; het kopje zelf en gewone alinea's niet. Doorlopende regels van
    /// hetzelfde punt worden samengevoegd.
    /// </summary>
    public static List<string> PuntenUit(string dossier)
    {
        var punten = new List<string>();
        var inSectie = false;
        var huidig = "";

        void Sluit()
        {
            // Markdown-opmaak (vet, code) weghalen: in een taaktekst leest dat als ruis.
            var schoon = Regex.Replace(huidig, @"\s+", " ")
                .Replace("**", "").Replace("`", "")
                .Trim(' ', '*', '—', '-', ':');
            if (schoon.Length > 8)
            {
                punten.Add(schoon);
            }
            huidig = "";
        }

        foreach (var ruweRegel in dossier.Split('\n'))
        {
            var regel = ruweRegel.TrimEnd('\r');
            const string Sleutelwoorden = @"openstaa|nog open|open punten|te doen|blijft liggen";
            if (regel.StartsWith('#'))
            {
                Sluit();
                // Kopjes als "## Wat er nu nog openstaat (8 augustus 2026)" of "## Nog open".
                inSectie = Regex.IsMatch(regel, Sleutelwoorden, RegexOptions.IgnoreCase);
                continue;
            }
            // Niet elk dossier gebruikt een kop: een aanloopzin die op een dubbele punt
            // eindigt ("Nog open bij de andere contacten:") telt net zo goed.
            if (regel.TrimEnd().EndsWith(':') &&
                Regex.IsMatch(regel, Sleutelwoorden, RegexOptions.IgnoreCase))
            {
                Sluit();
                inSectie = true;
                continue;
            }
            if (!inSectie)
            {
                continue;
            }
            var opsomming = Regex.Match(regel, @"^\s*(?:[-*•]|\d+[.)])\s+(.*)$");
            if (opsomming.Success)
            {
                Sluit();
                huidig = opsomming.Groups[1].Value;
            }
            else if (huidig.Length > 0 && regel.Trim().Length > 0)
            {
                huidig += " " + regel.Trim(); // vervolgregel van hetzelfde punt
            }
            else if (regel.Trim().Length == 0)
            {
                Sluit();
            }
            else
            {
                // Gewone alinea ná de opsomming: hier houdt de sectie op.
                Sluit();
                inSectie = false;
            }
        }
        Sluit();
        return punten;
    }

    private static string CategorieVoor(string klant, List<string> categorieen) =>
        categorieen.FirstOrDefault(c =>
            klant.Contains(c.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) ??
        klant.ToLowerInvariant() switch
        {
            "vriesveem" or "nemijtek" => "Urban IT",
            "lauryssens" => "Lauryssens",
            "ced" => "CED",
            "aqurat" => "Aqurat",
            _ => categorieen.FirstOrDefault() ?? "",
        };

    private static string Hoofdletter(string tekst) =>
        tekst.Length == 0 ? tekst : char.ToUpperInvariant(tekst[0]) + tekst[1..];

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }

    /// <summary>Korte, stabiele vingerafdruk van een punt (om dubbele taken te vermijden).</summary>
    private static string Hash(string punt)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(punt.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }

    private static State Laad()
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
            // Onleesbaar: opnieuw beginnen.
        }
        return new State();
    }

    private static void Bewaar(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
