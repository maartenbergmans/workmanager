using System.Drawing.Drawing2D;
using System.Reflection;

namespace WorkManager;

/// <summary>
/// Bron-iconen (18×18) voor de lijsten: de echte bedrijfslogo's (Gmail, Google Chat,
/// Google Agenda, Outlook, Teams, WhatsApp) als embedded PNG's in Assets\logos; voor
/// bronnen zonder logo (zoals Hilkes agenda) een getekende kleurbadge. Eén keer geladen
/// en daarna gecachet; gebruikt via <see cref="ModernListView.RijIcoon"/>.
/// </summary>
public static class BronIconen
{
    private static readonly Dictionary<string, Image> Cache = new();

    public static Image Voor(string sleutel)
    {
        if (!Cache.TryGetValue(sleutel, out var img))
        {
            Cache[sleutel] = img = LaadLogo(sleutel) ?? TekenBadge(sleutel);
        }
        return img;
    }

    /// <summary>Embedded logo-PNG laden en glad naar 18×18 schalen; null als hij er niet is.</summary>
    private static Image? LaadLogo(string sleutel)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"WorkManager.Assets.logos.{sleutel}.png");
            if (stream is null)
            {
                return null;
            }
            using var origineel = new Bitmap(stream);
            // Sommige favicons (o.a. Outlook) hebben een dekkende witte achtergrond:
            // die maken we transparant zodat het logo netjes op de donkere lijst staat.
            var hoek = origineel.GetPixel(0, 0);
            if (hoek.A == 255 && hoek.R > 240 && hoek.G > 240 && hoek.B > 240)
            {
                origineel.MakeTransparent(hoek);
            }
            var bmp = new Bitmap(18, 18);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(origineel, new Rectangle(0, 0, 18, 18));
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Image TekenBadge(string sleutel)
    {
        // Recept-afspraken (avondeten uit de AH-planning) krijgen een warme badge met
        // mes-en-vork.
        if (sleutel == "recept")
        {
            var receptBmp = new Bitmap(18, 18);
            using var rg = Graphics.FromImage(receptBmp);
            rg.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pad = Theme.RoundedPath(new Rectangle(0, 0, 17, 17), 5))
            using (var vlak = new SolidBrush(Color.FromArgb(214, 106, 44))) // terracotta
            {
                rg.FillPath(vlak, pad);
            }
            using var receptFont = new Font(Theme.IconFont.FontFamily, 9.5f);
            TextRenderer.DrawText(rg, Fluent.EtenDrinken, receptFont, new Rectangle(0, 0, 18, 18),
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return receptBmp;
        }

        // De afvaltaak krijgt een groene badge met de prullenbak-glyph uit Segoe Fluent Icons.
        if (sleutel == "afval")
        {
            var afvalBmp = new Bitmap(18, 18);
            using var ag = Graphics.FromImage(afvalBmp);
            ag.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pad = Theme.RoundedPath(new Rectangle(0, 0, 17, 17), 5))
            using (var vlak = new SolidBrush(Color.FromArgb(46, 139, 87))) // zeegroen
            {
                ag.FillPath(vlak, pad);
            }
            using var iconFont = new Font(Theme.IconFont.FontFamily, 9.5f);
            TextRenderer.DrawText(ag, Fluent.Delete, iconFont, new Rectangle(0, 0, 18, 18),
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return afvalBmp;
        }

        var (letter, kleur) = sleutel switch
        {
            "hilke" => ("H", Theme.Mix(Theme.Bg, Theme.Muted, 0.75f)),
            _ => ("•", Theme.Mix(Theme.Bg, Theme.Muted, 0.6f)),
        };
        var bmp = new Bitmap(18, 18);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pad = Theme.RoundedPath(new Rectangle(0, 0, 17, 17), 5))
            using (var vlak = new SolidBrush(kleur))
            {
                g.FillPath(vlak, pad);
            }
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, letter, font, new Rectangle(0, 0, 18, 18), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        return bmp;
    }
}
