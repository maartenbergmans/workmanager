# Genereert Assets\workmanager.ico (16-256 px) in de WorkManager-huisstijl:
# afgeronde tegel met indigo verloop, subtiele glans en een vette witte W.
# Opnieuw draaien na een ontwerpwijziging; het resultaat staat in git.
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Afgeronde tegel (Windows 11-stijl, hoekradius ~22%)
    $r = [Math]::Max(2, [int]([Math]::Round($s * 0.22)))
    $d = 2 * $r
    $w = $s - 1
    $tegel = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tegel.AddArc(0, 0, $d, $d, 180, 90)
    $tegel.AddArc($w - $d, 0, $d, $d, 270, 90)
    $tegel.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $tegel.AddArc(0, $w - $d, $d, $d, 90, 90)
    $tegel.CloseFigure()

    # Diagonaal indigo verloop (licht linksboven -> diep violet rechtsonder)
    $c1 = [System.Drawing.Color]::FromArgb(172, 152, 255)
    $c2 = [System.Drawing.Color]::FromArgb(54, 30, 168)
    $verloop = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point($s, $s)), $c1, $c2)
    $g.FillPath($verloop, $tegel)
    $verloop.Dispose()

    # Koele "aurora"-gloed vanuit de linkeronderhoek
    $gloedKleur = [System.Drawing.Color]::FromArgb(95, 0, 200, 255)
    $doorzichtig = [System.Drawing.Color]::FromArgb(0, 0, 200, 255)
    $gloed = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, $s)), (New-Object System.Drawing.Point([int]($s * 0.8), [int]($s * 0.2))),
        $gloedKleur, $doorzichtig)
    $gloed.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY # geen naad voorbij het eindpunt
    $g.FillPath($gloed, $tegel)
    $gloed.Dispose()

    # Dunne lichte binnenrand voor definitie
    if ($s -ge 32) {
        $rand = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [Math]::Max(1, $s / 128))
        $g.DrawPath($rand, $tegel)
        $rand.Dispose()
    }

    # Glans bovenaan (wit -> transparant over de bovenste helft)
    if ($s -ge 24) {
        $glansKleur = [System.Drawing.Color]::FromArgb(55, 255, 255, 255)
        $glansWeg = [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
        $glans = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, [int]($s * 0.55))),
            $glansKleur, $glansWeg)
        $oudeClip = $g.Clip
        $g.SetClip($tegel)
        $g.FillRectangle($glans, 0, 0, $s, [int]($s * 0.55))
        $g.Clip = $oudeClip
        $glans.Dispose()
    }

    # De W: klein schaduwtje voor diepte, dan wit
    $fontMaat = [float]($s * 0.56)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontMaat, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $vak = New-Object System.Drawing.RectangleF(0, [float]($s * 0.02), $s, $s)
    if ($s -ge 24) {
        $schaduw = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 15, 8, 50))
        $schaduwVak = New-Object System.Drawing.RectangleF([float]($s * 0.008), [float]($s * 0.042), $s, $s)
        $g.DrawString('W', $font, $schaduw, $schaduwVak, $sf)
        $schaduw.Dispose()
    }
    $g.DrawString('W', $font, [System.Drawing.Brushes]::White, $vak, $sf)

    $font.Dispose(); $sf.Dispose(); $tegel.Dispose(); $g.Dispose()
    return $bmp
}

$maten = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $maten) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($s, $ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

# Preview op groot formaat, handig om het ontwerp te beoordelen
$preview = New-IconBitmap 256
$preview.Save("$PSScriptRoot\workmanager-preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

# ICO-container schrijven (PNG-entries; prima voor Windows 10/11 en .NET)
$ico = New-Object System.IO.MemoryStream
$schrijver = New-Object System.IO.BinaryWriter($ico)
$schrijver.Write([uint16]0); $schrijver.Write([uint16]1); $schrijver.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $schrijver.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))  # breedte (0 = 256)
    $schrijver.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))  # hoogte
    $schrijver.Write([byte]0); $schrijver.Write([byte]0)          # palet, reserved
    $schrijver.Write([uint16]1); $schrijver.Write([uint16]32)     # planes, bpp
    $schrijver.Write([uint32]$data.Length)
    $schrijver.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $schrijver.Write([byte[]]$p[1]) }
[System.IO.File]::WriteAllBytes("$PSScriptRoot\workmanager.ico", $ico.ToArray())
$schrijver.Dispose(); $ico.Dispose()
Write-Output "workmanager.ico geschreven ($($pngs.Count) formaten)."
