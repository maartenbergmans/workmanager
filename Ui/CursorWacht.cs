using System.Runtime.InteropServices;

namespace WorkManager;

/// <summary>
/// Haalt een verdwenen muisaanwijzer terug. Windows houdt per invoerwachtrij een teller bij
/// van hoe vaak de cursor verborgen is (<c>ShowCursor</c>); zakt die onder nul, dan is de
/// aanwijzer onzichtbaar boven álle vensters van déze app terwijl de rest van Windows hem
/// gewoon toont. Hoveren en klikken blijven wel werken, waardoor het eerder op een
/// grafische hapering lijkt dan op een teller die is blijven hangen. WorkManager verbergt
/// de cursor zelf nergens, maar de WebView2's doen dat wel (Chromium bij pointer-lock,
/// schermvullende video en <c>cursor: none</c>) en halen hem er niet altijd weer bij.
/// Daarom zet elke vensteractivatie en elke pollronde de teller terug op nul.
/// </summary>
public static class CursorWacht
{
    /// <summary>
    /// Zet de verbergteller van de huidige thread terug op nul, zodat de aanwijzer weer
    /// zichtbaar is. Was hij al nul, dan gebeurt er netto niets. Moet op de UI-thread
    /// lopen: de teller hangt aan de invoerwachtrij van de aanroepende thread.
    /// </summary>
    public static void Herstel()
    {
        // ShowCursor geeft de nieuwe stand terug. Omhoog tot nul (de grens vangt een
        // theoretisch onbegrensde lus af), en niet doorschieten: bij een teller > 0 zou een
        // latere ShowCursor(false) van Chromium de aanwijzer niet meer wegnemen, maar blijft
        // hij wél hangen bij het volgende echte verbergmoment.
        var stand = ShowCursor(true);
        for (var i = 0; i < 16 && stand < 0; i++)
        {
            stand = ShowCursor(true);
        }
        while (stand > 0)
        {
            stand = ShowCursor(false);
        }

        // Tweede manier waarop de aanwijzer kan verdwijnen: niet de teller, maar de vorm
        // zelf staat op leeg (Chromium zet SetCursor(NULL)). Alleen dán terugzetten, anders
        // zou dit een lopende wachtcursor wegduwen.
        var info = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref info) && info.hCursor == IntPtr.Zero)
        {
            Cursor.Current = Cursors.Default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Point pt;
    }

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);
}
