using System.Security.Cryptography;
using System.Text;

namespace WorkManager;

/// <summary>
/// TOTP-codegenerator (RFC 6238, HMAC-SHA1, 30 s, 6 cijfers) — dezelfde codes als een
/// authenticator-app. Wordt gebruikt om op het Microsoft-MFA-scherm de actuele code op het
/// klembord te zetten, zodat Maarten enkel nog hoeft te plakken. De seed staat DPAPI-
/// versleuteld bij <see cref="CedLogin"/>; er wordt nooit een geheim uit een andere app
/// uitgelezen — de seed moet bewust ingesteld worden.
/// </summary>
public static class Totp
{
    /// <summary>
    /// De code die op dit moment geldig is voor het opgegeven base32-geheim, of "" als het
    /// geheim leeg of ongeldig is. Standaard 6 cijfers in een venster van 30 s.
    /// </summary>
    public static string Genereer(string base32Geheim, int cijfers = 6, int periodeSeconden = 30)
    {
        var sleutel = DecodeerBase32(base32Geheim);
        if (sleutel.Length == 0)
        {
            return "";
        }
        var teller = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / periodeSeconden);
        var tellerBytes = BitConverter.GetBytes(teller);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(tellerBytes); // RFC vereist big-endian (8 bytes)
        }
        using var hmac = new HMACSHA1(sleutel);
        var hash = hmac.ComputeHash(tellerBytes);
        // Dynamische truncatie: laatste nibble wijst de offset aan.
        var offset = hash[^1] & 0x0f;
        var binair = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);
        var code = binair % (int)Math.Pow(10, cijfers);
        return code.ToString().PadLeft(cijfers, '0');
    }

    /// <summary>Seconden tot de huidige code vervalt (voor een afteller in de melding).</summary>
    public static int SecondenGeldig(int periodeSeconden = 30) =>
        periodeSeconden - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % periodeSeconden);

    /// <summary>True als het geheim een geldige base32-seed lijkt (kan een code maken).</summary>
    public static bool IsGeldigGeheim(string base32Geheim) =>
        DecodeerBase32(base32Geheim).Length > 0;

    /// <summary>Base32 (RFC 4648) → bytes. Spaties en padding worden genegeerd.</summary>
    private static byte[] DecodeerBase32(string invoer)
    {
        if (string.IsNullOrWhiteSpace(invoer))
        {
            return Array.Empty<byte>();
        }
        const string Alfabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var schoon = invoer.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        if (schoon.Length == 0 || schoon.Any(c => Alfabet.IndexOf(c) < 0))
        {
            return Array.Empty<byte>();
        }
        var bits = new StringBuilder(schoon.Length * 5);
        foreach (var c in schoon)
        {
            bits.Append(Convert.ToString(Alfabet.IndexOf(c), 2).PadLeft(5, '0'));
        }
        var bytes = new List<byte>(bits.Length / 8);
        for (var i = 0; i + 8 <= bits.Length; i += 8)
        {
            bytes.Add(Convert.ToByte(bits.ToString(i, 8), 2));
        }
        return bytes.ToArray();
    }
}
