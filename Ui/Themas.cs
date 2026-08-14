using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Eén kleurenschema. <see cref="Theme"/> leest al zijn kleuren hieruit, dus een ander palet
/// kiezen verandert de hele app. Donker of licht bepaalt ook de titelbalk en de tint van de
/// klantkleuren (op wit moeten die dieper zijn om leesbaar te blijven).
/// </summary>
public sealed record ThemaPalet(
    string Naam,
    string Omschrijving,
    bool Donker,
    Color Bg,
    Color Surface,
    Color Card,
    Color CardHover,
    Color Field,
    Color Border,
    Color BorderLight,
    Color Text,
    Color Muted,
    Color Accent,
    Color AccentHover,
    Color AccentPress,
    Color Warn,
    Color Success,
    Color Danger,
    Color KlantCed,
    Color KlantAqurat,
    Color KlantRadiology,
    Color KlantUrbanIt,
    Color KlantPrive,
    Color KlantLauryssens);

/// <summary>
/// De beschikbare kleurenschema's en de keuze van de gebruiker
/// (%APPDATA%\WorkManager\thema.json). Een nieuw thema toevoegen = één record in
/// <see cref="Alle"/>; alle vensters volgen vanzelf, want ze tekenen met Theme.*.
/// </summary>
public static class Themas
{
    private static Color C(string hex) => ColorTranslator.FromHtml("#" + hex.TrimStart('#'));

    /// <summary>Het originele schema: blauwpaars getint donker met indigo accent.</summary>
    public static readonly ThemaPalet Middernacht = new(
        "Middernacht", "Donker blauwpaars met indigo accent (standaard)", true,
        Bg: C("15151C"), Surface: C("1C1C26"), Card: C("262632"), CardHover: C("2F2F3E"),
        Field: C("282835"), Border: C("343444"), BorderLight: C("4A4A5E"),
        Text: C("EAEAF2"), Muted: C("AAAAC0"),
        Accent: C("7A6CFF"), AccentHover: C("8E82FF"), AccentPress: C("6557E2"),
        Warn: C("FFB75C"), Success: C("62D495"), Danger: C("FF7682"),
        KlantCed: C("5C9CFF"), KlantAqurat: C("FFA35C"), KlantRadiology: C("56D6C8"),
        KlantUrbanIt: C("B496FF"), KlantPrive: C("7ED9A0"), KlantLauryssens: C("E2C86A"));

    /// <summary>Licht schema voor daglicht en gedeelde schermen.</summary>
    public static readonly ThemaPalet Daglicht = new(
        "Daglicht", "Wit en rustig, voor bij fel licht of screensharing", false,
        Bg: C("F5F6FA"), Surface: C("FFFFFF"), Card: C("FFFFFF"), CardHover: C("ECEFF7"),
        Field: C("FFFFFF"), Border: C("C9CFDE"), BorderLight: C("A9B2C6"),
        Text: C("1A1C24"), Muted: C("5E6478"),
        Accent: C("5647DC"), AccentHover: C("6857EE"), AccentPress: C("4436B8"),
        Warn: C("8A5200"), Success: C("0F6A40"), Danger: C("B02A38"),
        KlantCed: C("1F62C4"), KlantAqurat: C("9A4B08"), KlantRadiology: C("0F6259"),
        KlantUrbanIt: C("6741C8"), KlantPrive: C("15633A"), KlantLauryssens: C("6F540B"));

    /// <summary>Warm en zonnig: zandtinten met een oranje accent.</summary>
    public static readonly ThemaPalet Zomer = new(
        "Zomer", "Zandtinten met zonnig oranje — licht en warm", false,
        Bg: C("FFF7EC"), Surface: C("FFFCF5"), Card: C("FFFFFF"), CardHover: C("FFEFD8"),
        Field: C("FFFFFF"), Border: C("DFC7A2"), BorderLight: C("C2A374"),
        Text: C("38281A"), Muted: C("6F583D"),
        Accent: C("B85000"), AccentHover: C("9C4400"), AccentPress: C("8A3C00"),
        Warn: C("8A5A00"), Success: C("1C6B45"), Danger: C("B23A22"),
        KlantCed: C("13598C"), KlantAqurat: C("9A4B06"), KlantRadiology: C("0D6055"),
        KlantUrbanIt: C("64409E"), KlantPrive: C("1F6B41"), KlantLauryssens: C("74560F"));

    /// <summary>
    /// Smoking, martini en messing. Diep gitzwart met een warme goudtoon, en bewust
    /// contrastrijker dan de andere donkere paletten: crèmewitte tekst op bijna-zwart, en
    /// een gouden accent dat op elke kaart opvalt. De klantkleuren zijn ontzadigd naar
    /// gedempte juweeltinten zodat het geheel filmisch blijft in plaats van bont.
    /// </summary>
    public static readonly ThemaPalet Bond = new(
        "007", "Gitzwart met champagnegoud — smoking, martini, messing", true,
        Bg: C("07070A"), Surface: C("101015"), Card: C("18181F"), CardHover: C("24242E"),
        Field: C("13131A"), Border: C("31313D"), BorderLight: C("5A5468"),
        Text: C("F7F2E6"), Muted: C("BFB49A"),
        Accent: C("D4AF37"), AccentHover: C("F0CE63"), AccentPress: C("A8892A"),
        Warn: C("E8B54B"), Success: C("9CBE7A"), Danger: C("E8734C"),
        KlantCed: C("8AAFD8"), KlantAqurat: C("D9A05B"), KlantRadiology: C("77C4B4"),
        KlantUrbanIt: C("A9A6C4"), KlantPrive: C("9BBE86"), KlantLauryssens: C("F0CE63"));

    /// <summary>Nachtelijk neon: diepblauw met turquoise en magenta.</summary>
    public static readonly ThemaPalet Neon = new(
        "Neon", "Diepblauwe nacht met turquoise neon", true,
        Bg: C("090E1C"), Surface: C("0F172C"), Card: C("152038"), CardHover: C("1D2B4C"),
        Field: C("121B33"), Border: C("223255"), BorderLight: C("375188"),
        Text: C("E6F1FF"), Muted: C("93A9CC"),
        Accent: C("00D9BE"), AccentHover: C("3DEFD6"), AccentPress: C("00AD98"),
        Warn: C("FFC857"), Success: C("46E08A"), Danger: C("FF5C7A"),
        KlantCed: C("58A6FF"), KlantAqurat: C("FF9F45"), KlantRadiology: C("2FE0CE"),
        KlantUrbanIt: C("C77DFF"), KlantPrive: C("6EE7A8"), KlantLauryssens: C("F2D45C"));

    /// <summary>Warm donker: koffiebruin met een koperen accent.</summary>
    public static readonly ThemaPalet Espresso = new(
        "Espresso", "Koffiebruin met koper — warm donker, zacht voor 's avonds", true,
        Bg: C("1B1512"), Surface: C("241C18"), Card: C("2F251F"), CardHover: C("3C2F27"),
        Field: C("29201B"), Border: C("42342C"), BorderLight: C("5C483C"),
        Text: C("F2E7DC"), Muted: C("BCA593"),
        Accent: C("D08C4A"), AccentHover: C("E5A366"), AccentPress: C("B0733A"),
        Warn: C("E0B054"), Success: C("8FBF6A"), Danger: C("E8796A"),
        KlantCed: C("7FA6D8"), KlantAqurat: C("E0964F"), KlantRadiology: C("6FBFAE"),
        KlantUrbanIt: C("BFA0E0"), KlantPrive: C("94C58A"), KlantLauryssens: C("E3C978"));

    /// <summary>
    /// Godfather: de sepia van de film — verschoten fotopapier, mahonie en het diepe
    /// bloedrood van de titelletters, met olijfgoud als accent. Bewust warmer en doffer dan
    /// 007: dat is champagne en messing, dit is een schemerig kantoor met houten lambrisering.
    /// </summary>
    public static readonly ThemaPalet Godfather = new(
        "Godfather", "Sepia en mahonie met bloedrood en olijfgoud", true,
        Bg: C("140F0C"), Surface: C("1D1613"), Card: C("281E19"), CardHover: C("352822"),
        Field: C("221A16"), Border: C("3E2E25"), BorderLight: C("614637"),
        Text: C("F0E4D2"), Muted: C("BBA187"),
        // Het rood van de filmtitel, maar één stap opgehaald: het diepere C0392B haalde de
        // contrastnorm niet op de kaarten (2,99:1). Dit blijft donkerrood en is wél leesbaar.
        Accent: C("D9553F"), AccentHover: C("E56C55"), AccentPress: C("A8382A"),
        Warn: C("D9A441"), Success: C("8FAE63"), Danger: C("E8735C"),
        KlantCed: C("8FA9C9"), KlantAqurat: C("D69A55"), KlantRadiology: C("6FB9A8"),
        KlantUrbanIt: C("BFA26B"), KlantPrive: C("9BB77E"), KlantLauryssens: C("E0BE6C"));

    public static readonly ThemaPalet[] Alle =
        { Middernacht, Daglicht, Zomer, Bond, Neon, Espresso, Godfather };

    /// <summary>
    /// Alle kleuren van een palet op een rij, in vaste volgorde. Bij een themawissel worden
    /// twee paletten paarsgewijs vergeleken om vastgelegde kleuren in bestaande vensters om
    /// te zetten (zie <see cref="Theme.ZetThema"/>).
    /// </summary>
    public static Color[] Kleuren(ThemaPalet p) => new[]
    {
        p.Bg, p.Surface, p.Card, p.CardHover, p.Field, p.Border, p.BorderLight,
        p.Text, p.Muted, p.Accent, p.AccentHover, p.AccentPress,
        p.Warn, p.Success, p.Danger,
        p.KlantCed, p.KlantAqurat, p.KlantRadiology, p.KlantUrbanIt, p.KlantPrive, p.KlantLauryssens,
    };

    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "thema.json");

    /// <summary>De bewaarde keuze (Middernacht als er nog niets gekozen is).</summary>
    public static ThemaPalet Laad()
    {
        try
        {
            if (File.Exists(DataFile) &&
                JsonSerializer.Deserialize<string>(File.ReadAllText(DataFile)) is { } naam &&
                Alle.FirstOrDefault(t => t.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase))
                    is { } gevonden)
            {
                return gevonden;
            }
        }
        catch
        {
            // Onleesbaar: gewoon het standaardthema.
        }
        return Middernacht;
    }

    public static void Bewaar(ThemaPalet palet)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
            File.WriteAllText(DataFile, JsonSerializer.Serialize(palet.Naam));
        }
        catch
        {
            // Best effort: dan staat het thema er na een herstart weer af.
        }
    }
}
