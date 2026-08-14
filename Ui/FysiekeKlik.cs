using System.Runtime.InteropServices;

namespace WorkManager;

/// <summary>
/// Stuurt échte muisberichten (WM_LBUTTONDOWN/UP) naar het Chromium-childvenster van een
/// WebView2. Synthetische JS-events zijn "untrusted" en worden door sommige web-UI's —
/// zoals de Outlook-ribbon met de Quick Steps — botweg genegeerd; deze Win32-route is voor
/// de pagina niet te onderscheiden van een echte muisklik en werkt ook terwijl het venster
/// verborgen buiten beeld staat (PostMessage heeft geen focus of cursor nodig).
/// </summary>
public static class FysiekeKlik
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const int MkLButton = 0x0001;
    private const int MkRButton = 0x0002;

    /// <summary>
    /// Klikt op CSS-coördinaten (viewport) in de opgegeven WebView2-control. De schaal
    /// CSS→fysieke pixels wordt gemeten (vensterbreedte ÷ paginabreedte) in plaats van
    /// afgeleid uit DPI-aannames: bij schermschaling landde de klik anders nét ernaast.
    /// </summary>
    public static bool Klik(Control web, double cssX, double cssY, double cssViewportBreedte,
        bool rechts = false)
    {
        var doel = VindRenderVenster(web.Handle);
        if (doel == IntPtr.Zero || cssViewportBreedte <= 0 ||
            !GetClientRect(doel, out var client))
        {
            return false;
        }
        var schaal = client.Right / cssViewportBreedte;
        var x = (int)Math.Round(cssX * schaal);
        var y = (int)Math.Round(cssY * schaal);
        var lparam = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(doel, WmMouseMove, IntPtr.Zero, lparam);
        Thread.Sleep(40); // Chromium de hover laten verwerken vóór de klik
        if (rechts)
        {
            PostMessage(doel, WmRButtonDown, (IntPtr)MkRButton, lparam);
            Thread.Sleep(40);
            PostMessage(doel, WmRButtonUp, IntPtr.Zero, lparam);
        }
        else
        {
            PostMessage(doel, WmLButtonDown, (IntPtr)MkLButton, lparam);
            Thread.Sleep(40);
            PostMessage(doel, WmLButtonUp, IntPtr.Zero, lparam);
        }
        return true;
    }

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    /// <summary>Enter (VK 0x0D). </summary>
    public const int VkReturn = 0x0D;

    /// <summary>
    /// Stuurt een echte toetsaanslag naar het element met focus in de pagina — ook
    /// toetsen als Enter in de zoekbalk negeert OWA als ze synthetisch (JS) zijn.
    /// </summary>
    public static bool Toets(Control web, int virtualKey)
    {
        var doel = VindRenderVenster(web.Handle);
        if (doel == IntPtr.Zero)
        {
            return false;
        }
        // lParam: herhaal=1 + scancode 0x1C (Enter); keyup met transition/previous-flags.
        PostMessage(doel, WmKeyDown, (IntPtr)virtualKey, (IntPtr)0x001C0001);
        PostMessage(doel, WmKeyUp, (IntPtr)virtualKey, unchecked((IntPtr)(long)0xC01C0001));
        return true;
    }

    private static IntPtr VindRenderVenster(IntPtr wortel)
    {
        var gevonden = IntPtr.Zero;
        EnumChildWindows(wortel, (h, _) =>
        {
            var naam = new System.Text.StringBuilder(64);
            GetClassName(h, naam, 64);
            if (naam.ToString() == "Chrome_RenderWidgetHostHWND")
            {
                gevonden = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return gevonden;
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc cb, IntPtr lparam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
