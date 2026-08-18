using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WorkManager;

/// <summary>
/// Centrale internetcheck: is er nu een werkende verbinding? Bij netwerkfouten kijken we
/// eerst hier — zonder internet is de echte oorzaak niet de bron zelf, en dan volstaat
/// één duidelijke "geen internetverbinding" in plaats van een regen van cryptische
/// foutmeldingen per bron. De uitkomst wordt ±10 s gecachet zodat herhaalde checks
/// niets kosten.
/// </summary>
public static class Internet
{
    private static readonly object Slot = new();
    private static bool _online = true;
    private static DateTimeOffset _gecheckt = DateTimeOffset.MinValue;
    private static Task<bool>? _peiling;

    /// <summary>
    /// Laatst bekende stand, zonder te blokkeren (veilig op de UI-thread). Geen enkele
    /// netwerkadapter actief = meteen offline; anders de gecachete uitkomst van de laatste
    /// peiling — is die ouder dan 10 s, dan start op de achtergrond een verse.
    /// </summary>
    public static bool Offline
    {
        get
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return true;
            }
            lock (Slot)
            {
                if (DateTimeOffset.Now - _gecheckt > TimeSpan.FromSeconds(10))
                {
                    _peiling ??= Task.Run(PeilAsync);
                }
                return !_online;
            }
        }
    }

    /// <summary>Echte peiling, hooguit ±3 s: true als er internet is.</summary>
    public static Task<bool> CheckAsync()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return Task.FromResult(false);
        }
        lock (Slot)
        {
            if (DateTimeOffset.Now - _gecheckt <= TimeSpan.FromSeconds(10))
            {
                return Task.FromResult(_online);
            }
            return _peiling ??= Task.Run(PeilAsync);
        }
    }

    private static async Task<bool> PeilAsync()
    {
        // TCP-connect naar twee publieke DNS-diensten (poort 443): lichter dan een echte
        // HTTP-request en niet afhankelijk van één partij. Eén geslaagde volstaat.
        var ok = false;
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, 443).WaitAsync(TimeSpan.FromMilliseconds(1500));
                ok = true;
                break;
            }
            catch
            {
                // Volgende proberen.
            }
        }
        lock (Slot)
        {
            _online = ok;
            _gecheckt = DateTimeOffset.Now;
            _peiling = null;
        }
        return ok;
    }
}
