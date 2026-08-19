namespace WorkManager;

/// <summary>
/// Houdt per bron (Outlook, Teams) bij wanneer voor het laatst écht een MFA-aanmelding
/// gebeurde. Het CED-tenant vraagt elke 24 uur opnieuw MFA; met deze tijd kan de cockpit
/// bij het opstarten meteen de "aanmelden"-knop tonen als dat venster verlopen is, zonder
/// eerst een mislukte sync af te wachten.
///
/// <para>Bewust los van de <c>*-linked.txt</c>-markers: die worden bij élke geslaagde
/// sessiecheck herschreven ("sessie leeft nog") en zeggen dus niets over het laatste
/// MFA-moment. Deze marker schrijven we alleen bij een echte interactieve aanmelding.</para>
/// </summary>
public static class MfaTijd
{
    /// <summary>Na hoeveel uur het CED-tenant opnieuw MFA vraagt.</summary>
    public const double VensterUren = 24;

    private static string Pad(string bron) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", $"{bron.ToLowerInvariant()}-mfa.txt");

    /// <summary>Legt vast dat er nu een MFA-aanmelding voor deze bron voltooid is.</summary>
    public static void Noteer(string bron)
    {
        try
        {
            var pad = Pad(bron);
            Directory.CreateDirectory(Path.GetDirectoryName(pad)!);
            File.WriteAllText(pad, DateTimeOffset.Now.ToString("O"));
        }
        catch
        {
            // Alleen een gemak; niet kritiek als het even niet lukt.
        }
    }

    /// <summary>Wanneer deze bron voor het laatst MFA deed, of null als dat niet bekend is.</summary>
    public static DateTimeOffset? Laatste(string bron)
    {
        try
        {
            var pad = Pad(bron);
            if (File.Exists(pad) &&
                DateTimeOffset.TryParse(File.ReadAllText(pad).Trim(), out var moment))
            {
                return moment;
            }
        }
        catch
        {
            // Onleesbaar: als onbekend behandelen.
        }
        return null;
    }

    /// <summary>True als er nog nooit MFA was of het 24u-venster verlopen is.</summary>
    public static bool Verlopen(string bron)
    {
        var laatste = Laatste(bron);
        return laatste is null ||
            DateTimeOffset.Now - laatste.Value > TimeSpan.FromHours(VensterUren);
    }
}
