using System.Drawing.Drawing2D;

namespace WorkManager;

/// <summary>
/// Het embleem van een thema: een getekend motief (geen emoji) dat als watermerk achter de
/// inhoud staat — de loop van een pistool voor 007, een roos voor Godfather, een zon voor
/// Zomer. Vectorlijnen in plaats van een plaatje, zodat het meeschaalt en automatisch de
/// themakleur volgt.
///
/// <para>Bewust in lijnwerk en op lage dekking: het mag opvallen als je erop let, en
/// verdwijnen als je aan het werk bent. Daarom tekent <see cref="Teken"/> altijd met een
/// pen — nooit een gevuld vlak dat met tekst zou concurreren.</para>
/// </summary>
public static class ThemaEmbleem
{
    /// <summary>
    /// Tekent het embleem van het huidige thema binnen <paramref name="vlak"/>. De kleur is
    /// de accentkleur, gemengd met de achtergrond op <paramref name="sterkte"/> (0..1).
    /// </summary>
    public static void Teken(Graphics g, Rectangle vlak, float sterkte = 0.16f, Color? achter = null)
    {
        if (vlak.Width < 40 || vlak.Height < 40)
        {
            return;
        }
        var oud = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var zijde = Math.Min(vlak.Width, vlak.Height);
        var kleur = Theme.Mix(achter ?? Theme.Bg, Theme.Accent, sterkte);
        using var pen = new Pen(kleur, Math.Max(1.5f, zijde / 44f)) { LineJoin = LineJoin.Round };
        var midden = new PointF(vlak.X + vlak.Width / 2f, vlak.Y + vlak.Height / 2f);
        var straal = zijde / 2f - pen.Width;
        switch (Theme.Palet.Naam)
        {
            case "007":
                Loop(g, pen, midden, straal);
                break;
            case "Godfather":
                Roos(g, pen, midden, straal);
                break;
            case "Zomer":
                ZonBovenZee(g, pen, midden, straal);
                break;
            case "Daglicht":
                Zon(g, pen, midden, straal);
                break;
            case "Neon":
                Skyline(g, pen, midden, straal);
                break;
            case "Espresso":
                Boon(g, pen, midden, straal);
                break;
            default:
                Maan(g, pen, midden, straal);
                break;
        }
        g.SmoothingMode = oud;
    }

    /// <summary>
    /// De gun barrel: ringen die naar binnen lopen, de karakteristieke schuine trekken
    /// (rifling) tussen de buitenste ringen, en de korrel in het midden.
    /// </summary>
    private static void Loop(Graphics g, Pen pen, PointF m, float r)
    {
        for (var i = 0; i < 3; i++)
        {
            var straal = r * (1f - i * 0.22f);
            g.DrawEllipse(pen, m.X - straal, m.Y - straal, straal * 2, straal * 2);
        }
        // Rifling: korte schuine streepjes tussen de buitenste en middelste ring, zoals in
        // de openingssequentie van elke Bondfilm.
        for (var i = 0; i < 8; i++)
        {
            var rad = (i * 45f + 22f) * MathF.PI / 180f;
            var radSchuin = rad + 0.35f;
            g.DrawLine(pen,
                m.X + MathF.Cos(rad) * r * 0.98f, m.Y + MathF.Sin(rad) * r * 0.98f,
                m.X + MathF.Cos(radSchuin) * r * 0.8f, m.Y + MathF.Sin(radSchuin) * r * 0.8f);
        }
        g.DrawLine(pen, m.X - r * 0.16f, m.Y, m.X + r * 0.16f, m.Y);
        g.DrawLine(pen, m.X, m.Y - r * 0.16f, m.X, m.Y + r * 0.16f);
    }

    /// <summary>
    /// De roos uit het knoopsgat: de knop (spiraal) bovenaan, met blaadjes eromheen en een
    /// steel met blad eronder — zo leest hij ook in één oogopslag als roos.
    /// </summary>
    private static void Roos(Graphics g, Pen pen, PointF m, float r)
    {
        var knop = new PointF(m.X, m.Y - r * 0.35f);
        // Spiraal van binnen naar buiten: elke stap iets verder van het midden.
        var punten = new List<PointF>();
        for (var hoek = 0f; hoek < 720f; hoek += 8f)
        {
            var afstand = r * 0.38f * (hoek / 720f);
            var rad = hoek * MathF.PI / 180f;
            punten.Add(new PointF(knop.X + MathF.Cos(rad) * afstand, knop.Y + MathF.Sin(rad) * afstand));
        }
        g.DrawCurve(pen, punten.ToArray());
        // Vijf blaadjes als open bogen rond de knop.
        for (var i = 0; i < 5; i++)
        {
            var start = i * 72f - 30f;
            var straal = r * 0.58f;
            g.DrawArc(pen, knop.X - straal, knop.Y - straal, straal * 2, straal * 2, start, 54f);
        }
        // De steel: een lichte S-curve naar beneden, met één blad halverwege.
        var steel = new[]
        {
            new PointF(knop.X, knop.Y + r * 0.55f),
            new PointF(knop.X + r * 0.12f, knop.Y + r * 0.85f),
            new PointF(knop.X - r * 0.06f, knop.Y + r * 1.15f),
            new PointF(knop.X + r * 0.04f, knop.Y + r * 1.35f),
        };
        g.DrawCurve(pen, steel);
        var bladX = knop.X + r * 0.1f;
        var bladY = knop.Y + r * 0.95f;
        g.DrawArc(pen, bladX, bladY - r * 0.3f, r * 0.55f, r * 0.5f, 120f, 110f);
        g.DrawArc(pen, bladX, bladY - r * 0.05f, r * 0.55f, r * 0.5f, 190f, 110f);
    }

    /// <summary>De zon: een cirkel met stralen.</summary>
    private static void Zon(Graphics g, Pen pen, PointF m, float r)
    {
        var kern = r * 0.45f;
        g.DrawEllipse(pen, m.X - kern, m.Y - kern, kern * 2, kern * 2);
        for (var i = 0; i < 12; i++)
        {
            var rad = i * 30f * MathF.PI / 180f;
            var van = kern * 1.35f;
            var tot = r * (i % 2 == 0 ? 1f : 0.8f);
            g.DrawLine(pen,
                m.X + MathF.Cos(rad) * van, m.Y + MathF.Sin(rad) * van,
                m.X + MathF.Cos(rad) * tot, m.Y + MathF.Sin(rad) * tot);
        }
    }

    /// <summary>De nachtelijke stad: torens op een horizon, antennes en een maan erboven.</summary>
    private static void Skyline(Graphics g, Pen pen, PointF m, float r)
    {
        var breedtes = new[] { 0.9f, 1.4f, 0.7f, 1.1f, 0.6f };
        var hoogtes = new[] { 0.55f, 0.95f, 0.4f, 0.75f, 0.3f };
        var x = m.X - r * 0.85f;
        var basis = m.Y + r * 0.7f;
        for (var i = 0; i < breedtes.Length; i++)
        {
            var b = r * 0.3f * breedtes[i];
            var h = r * hoogtes[i] * 1.4f;
            g.DrawRectangle(pen, x, basis - h, b, h);
            if (hoogtes[i] > 0.7f)
            {
                // Antenne op de hoge torens: het herkenbare silhouet van een nachtskyline.
                g.DrawLine(pen, x + b / 2, basis - h, x + b / 2, basis - h - r * 0.22f);
            }
            x += b + r * 0.09f;
        }
        g.DrawLine(pen, m.X - r, basis, m.X + r, basis);
        // Een kleine volle maan rechtsboven maakt het een nachtbeeld.
        var maan = r * 0.16f;
        g.DrawEllipse(pen, m.X + r * 0.6f, m.Y - r * 0.85f, maan * 2, maan * 2);
    }

    /// <summary>Zomer: de zon zakt richting zee — zon boven twee golvende lijnen.</summary>
    private static void ZonBovenZee(Graphics g, Pen pen, PointF m, float r)
    {
        var zonM = new PointF(m.X, m.Y - r * 0.25f);
        var kern = r * 0.38f;
        g.DrawEllipse(pen, zonM.X - kern, zonM.Y - kern, kern * 2, kern * 2);
        // Alleen de bovenste stralen: de onderste "staan al in het water".
        for (var i = 0; i < 7; i++)
        {
            var rad = (180f + i * 30f) * MathF.PI / 180f;
            var van = kern * 1.3f;
            var tot = r * (i % 2 == 0 ? 0.85f : 0.68f);
            g.DrawLine(pen,
                zonM.X + MathF.Cos(rad) * van, zonM.Y + MathF.Sin(rad) * van,
                zonM.X + MathF.Cos(rad) * tot, zonM.Y + MathF.Sin(rad) * tot);
        }
        // Twee golvende lijnen als zee, elk uit een reeks kleine bogen.
        for (var golf = 0; golf < 2; golf++)
        {
            var y = m.Y + r * (0.45f + golf * 0.32f);
            var golfBreedte = r * 0.5f;
            for (var x = m.X - r; x < m.X + r - golfBreedte / 2; x += golfBreedte)
            {
                g.DrawArc(pen, x, y - r * 0.09f, golfBreedte, r * 0.24f, 180f, 180f);
            }
        }
    }

    /// <summary>De koffieboon met de karakteristieke groef, en verse stoom erboven.</summary>
    private static void Boon(Graphics g, Pen pen, PointF m, float r)
    {
        var boonM = new PointF(m.X, m.Y + r * 0.18f);
        var b = r * 0.62f;
        var h = r * 0.8f;
        g.DrawEllipse(pen, boonM.X - b, boonM.Y - h, b * 2, h * 2);
        var groef = new[]
        {
            new PointF(boonM.X, boonM.Y - h * 0.92f),
            new PointF(boonM.X - b * 0.45f, boonM.Y - h * 0.3f),
            new PointF(boonM.X + b * 0.45f, boonM.Y + h * 0.3f),
            new PointF(boonM.X, boonM.Y + h * 0.92f),
        };
        g.DrawCurve(pen, groef);
        // Twee stoomsliertjes: S-curves die boven de boon opstijgen.
        foreach (var dx in new[] { -0.28f, 0.24f })
        {
            var sx = boonM.X + b * dx * 2f;
            var top = boonM.Y - h - r * 0.12f;
            g.DrawCurve(pen, new[]
            {
                new PointF(sx, top),
                new PointF(sx - r * 0.1f, top - r * 0.18f),
                new PointF(sx + r * 0.08f, top - r * 0.36f),
                new PointF(sx - r * 0.04f, top - r * 0.5f),
            });
        }
    }

    /// <summary>De maansikkel: twee cirkels waarvan er één de andere uithapt.</summary>
    private static void Maan(Graphics g, Pen pen, PointF m, float r)
    {
        // De sikkel is het verschil van twee cirkels: de buitenrand (straal r) en een cirkel
        // die er een hap uit neemt (straal 0,9r, 0,45r naar rechts). De bogen moeten in hun
        // snijpunten samenkomen, anders trekt CloseFigure er een streep doorheen — vandaar
        // dat de hoeken hier uitgerekend worden en niet geschat.
        const float Hap = 0.9f;   // straal van de uitsnijdende cirkel
        const float Weg = 0.45f;  // hoever die naar rechts staat
        var snijX = (Weg * Weg + 1f - Hap * Hap) / (2f * Weg);
        var snijY = MathF.Sqrt(1f - snijX * snijX);
        var buiten = MathF.Atan2(snijY, snijX) * 180f / MathF.PI;
        var binnen = MathF.Atan2(snijY, snijX - Weg) * 180f / MathF.PI;
        using var pad = new GraphicsPath();
        pad.AddArc(m.X - r, m.Y - r, r * 2, r * 2, buiten, 360f - buiten * 2);
        pad.AddArc(m.X + (Weg - Hap) * r, m.Y - Hap * r, Hap * r * 2, Hap * r * 2,
            360f - binnen, -(360f - binnen * 2));
        pad.CloseFigure();
        g.DrawPath(pen, pad);
        // Twee sterretjes ernaast: net genoeg om het een nachtbeeld te maken.
        foreach (var (dx, dy, maat) in new[] { (0.62f, -0.66f, 0.1f), (0.78f, 0.2f, 0.07f) })
        {
            var sx = m.X + r * dx;
            var sy = m.Y + r * dy;
            var s = r * maat;
            g.DrawLine(pen, sx - s, sy, sx + s, sy);
            g.DrawLine(pen, sx, sy - s, sx, sy + s);
        }
    }
}
