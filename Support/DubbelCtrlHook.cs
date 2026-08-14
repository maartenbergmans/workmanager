using System.Runtime.InteropServices;

namespace WorkManager;

/// <summary>
/// Globale sneltoets "Ctrl, Ctrl": twee keer kort na elkaar op Ctrl tikken (zonder andere
/// toets ertussen) vuurt <see cref="Getikt"/>, waar je ook zit in Windows. Een lichte
/// low-level-toetsenbordhook; alle toetsen worden gewoon doorgelaten, er wordt alleen
/// meegekeken. Ctrl+C en consorten tellen niet: zodra er een andere toets bij komt, telt
/// die Ctrl-aanslag niet mee.
/// </summary>
public sealed class DubbelCtrlHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int VkLcontrol = 0xA2;
    private const int VkRcontrol = 0xA3;

    /// <summary>Maximale tijd tussen de twee tikken.</summary>
    private static readonly TimeSpan Venster = TimeSpan.FromMilliseconds(400);

    public event Action? Getikt;

    private readonly LowLevelKeyboardProc _proc; // referentie vasthouden tegen GC
    private readonly SynchronizationContext? _sync;
    private IntPtr _hook;
    private bool _ctrlIngedrukt;
    private bool _andereToetsErbij;
    private DateTime _vorigeTik = DateTime.MinValue;

    public DubbelCtrlHook()
    {
        _sync = SynchronizationContext.Current;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        // Mislukt de hook (zeldzaam), dan doet de sneltoets gewoon niets — geen foutmelding.
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam); // eerste veld van KBDLLHOOKSTRUCT = vkCode
            var isCtrl = vk is VkLcontrol or VkRcontrol;
            var msg = (int)wParam;
            if (msg is WmKeydown or WmSyskeydown)
            {
                if (isCtrl)
                {
                    if (!_ctrlIngedrukt)
                    {
                        _ctrlIngedrukt = true;
                        _andereToetsErbij = false;
                    }
                }
                else if (_ctrlIngedrukt)
                {
                    _andereToetsErbij = true; // Ctrl+<iets>: geen tik
                    _vorigeTik = DateTime.MinValue;
                }
                else
                {
                    _vorigeTik = DateTime.MinValue; // gewone toets breekt de reeks
                }
            }
            else if (msg is WmKeyup or WmSyskeyup && isCtrl)
            {
                var wasSolo = _ctrlIngedrukt && !_andereToetsErbij;
                _ctrlIngedrukt = false;
                if (wasSolo)
                {
                    var nu = DateTime.UtcNow;
                    if (nu - _vorigeTik <= Venster)
                    {
                        _vorigeTik = DateTime.MinValue;
                        // Niet op de hook-thread blijven hangen: doorschuiven naar de UI-thread.
                        if (_sync is not null)
                        {
                            _sync.Post(_ => Getikt?.Invoke(), null);
                        }
                        else
                        {
                            Getikt?.Invoke();
                        }
                    }
                    else
                    {
                        _vorigeTik = nu;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
