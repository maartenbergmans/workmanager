using QRCoder;

namespace WorkManager;

/// <summary>
/// QR-codes in de kleuren van het actieve thema. Gebruikt voor links die van de pc naar de
/// gsm moeten (de persoonlijke webpagina); scannen is nu eenmaal sneller dan overtypen.
/// </summary>
public static class QrCode
{
    /// <summary>
    /// Tekent <paramref name="tekst"/> als QR-code van ongeveer <paramref name="grootte"/>
    /// pixels. Foutcorrectie op Q: een code op een scherm mag best wat glans of reflectie
    /// hebben. Geeft null als het coderen niet lukt (te lange tekst).
    /// </summary>
    public static Bitmap? Teken(string tekst, int grootte, Color voorgrond, Color achtergrond)
    {
        if (tekst.Length == 0)
        {
            return null;
        }
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(tekst, QRCodeGenerator.ECCLevel.Q);
            using var code = new QRCode(data);
            // De modulegrootte zo kiezen dat het geheel dicht bij de gevraagde maat komt:
            // schalen achteraf zou de scherpe randen van een QR-code juist kapotmaken.
            var modules = data.ModuleMatrix.Count; // inclusief de stille rand
            var pixelsPerModule = Math.Max(2, grootte / Math.Max(1, modules));
            return code.GetGraphic(pixelsPerModule, voorgrond, achtergrond, drawQuietZones: true);
        }
        catch
        {
            return null;
        }
    }
}
