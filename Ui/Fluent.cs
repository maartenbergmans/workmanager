namespace WorkManager;

/// <summary>
/// Veelgebruikte Segoe Fluent Icons-tekens, opgebouwd uit codepoints zodat de broncode
/// vrij blijft van onzichtbare private-use-tekens.
/// </summary>
public static class Fluent
{
    public static readonly string Refresh = Teken(0xE72C);
    public static readonly string Send = Teken(0xE724);
    public static readonly string Archive = Teken(0xE7B8);
    public static readonly string Klok = Teken(0xE823);
    public static readonly string Edit = Teken(0xE70F);
    public static readonly string Settings = Teken(0xE713);
    public static readonly string Add = Teken(0xE710);
    public static readonly string Mail = Teken(0xE715);
    public static readonly string People = Teken(0xE716);
    public static readonly string Delete = Teken(0xE74D);
    public static readonly string Color = Teken(0xE790);
    public static readonly string Check = Teken(0xE73E);
    public static readonly string Lijst = Teken(0xE8FD);
    public static readonly string Globe = Teken(0xE774);
    public static readonly string Copy = Teken(0xE8C8);
    public static readonly string Document = Teken(0xE8A5);
    public static readonly string Checkbox = Teken(0xE73A);
    public static readonly string Factuur = Teken(0xE8A1);
    public static readonly string Ster = Teken(0xE735);
    public static readonly string Kalender = Teken(0xE787);
    public static readonly string Sync = Teken(0xE895);
    public static readonly string Zoek = Teken(0xE721);
    public static readonly string Winkelwagen = Teken(0xE7BF);
    public static readonly string Terug = Teken(0xE72B);
    public static readonly string EtenDrinken = Teken(0xE807); // mes en vork
    public static readonly string Huis = Teken(0xE80F);

    private static string Teken(int codepoint) => char.ConvertFromUtf32(codepoint);
}
