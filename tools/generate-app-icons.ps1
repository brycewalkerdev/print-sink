Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$appAssets = Join-Path $PSScriptRoot '..\src\PrintSink.App\Assets'
$testAssets = Join-Path $PSScriptRoot '..\tests\PrintSink.App.Tests\Assets'

$source = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class PrintSinkIconArt
{
    static Color Ink = Color.FromArgb(15, 39, 59);
    static Color Deep = Color.FromArgb(12, 38, 57);
    static Color Teal = Color.FromArgb(32, 190, 190);
    static Color Paper = Color.FromArgb(246, 251, 255);
    static Color Pale = Color.FromArgb(211, 235, 240);

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r, r, 180, 90);
        p.AddArc(x + w - r, y, r, r, 270, 90);
        p.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        p.AddArc(x, y + h - r, r, r, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h, float r)
    {
        using (var p = RoundRect(x, y, w, h, r)) g.FillPath(b, p);
    }

    static void DrawMark(Graphics g, bool tile)
    {
        if (tile)
        {
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 1024, 1024), Deep, Color.FromArgb(20, 83, 103), 45f))
                FillRound(g, bg, 36, 36, 952, 952, 210);
        }

        // A sheet entering the printer from above.
        FillRound(g, Brushes.White, 354, 126, 316, 355, 26);
        PointF[] fold = { new PointF(566, 126), new PointF(670, 230), new PointF(566, 230) };
        using (var b = new SolidBrush(Teal)) g.FillPolygon(b, fold);
        using (var pen = new Pen(Color.FromArgb(70, Ink), 20) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, 408, 282, 572, 282);
            g.DrawLine(pen, 408, 340, 612, 340);
            g.DrawLine(pen, 408, 398, 535, 398);
        }

        // Printer housing and control light.
        FillRound(g, new SolidBrush(Color.FromArgb(233, 245, 249)), 158, 348, 708, 356, 84);
        FillRound(g, new SolidBrush(Pale), 222, 407, 580, 226, 50);
        FillRound(g, new SolidBrush(Deep), 290, 536, 444, 62, 28);
        using (var led = new SolidBrush(Teal)) g.FillEllipse(led, 710, 432, 34, 34);
        using (var seam = new Pen(Color.FromArgb(125, 168, 187), 18) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(seam, 234, 674, 790, 674);

        // The output page and downward arrow make the virtual printer's destination clear.
        FillRound(g, new SolidBrush(Teal), 366, 574, 292, 294, 34);
        PointF[] arrow = {
            new PointF(466, 626), new PointF(558, 626), new PointF(558, 716),
            new PointF(608, 716), new PointF(512, 812), new PointF(416, 716), new PointF(466, 716)
        };
        using (var white = new SolidBrush(Color.White)) g.FillPolygon(white, arrow);
    }

    public static Bitmap Render(int width, int height, bool tile, bool wordmark, bool monochrome)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            if (width == height)
            {
                g.ScaleTransform(width / 1024f, height / 1024f);
                DrawMark(g, tile);
            }
            else
            {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Deep, Color.FromArgb(20, 83, 103), 0f))
                    g.FillRectangle(bg, 0, 0, width, height);
                if (wordmark)
                {
                    float mark = height * .82f;
                    g.TranslateTransform(height * .08f, height * .09f);
                    g.ScaleTransform(mark / 1024f, mark / 1024f);
                    DrawMark(g, true);
                    g.ResetTransform();
                    using (var font = new Font("Segoe UI", height * .22f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Paper))
                        g.DrawString("PrintSink", font, brush, height * .98f, (height - font.GetHeight(g)) / 2f);
                }
                else
                {
                    float mark = height * .8f;
                    g.TranslateTransform((width - mark) / 2f, (height - mark) / 2f);
                    g.ScaleTransform(mark / 1024f, mark / 1024f);
                    DrawMark(g, true);
                }
            }

            if (monochrome)
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Color c = bitmap.GetPixel(x, y);
                        if (c.A > 0) bitmap.SetPixel(x, y, Color.FromArgb(c.A, 255, 255, 255));
                    }
            }
        }
        return bitmap;
    }

    public static void Save(Bitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        bitmap.Save(path, ImageFormat.Png);
        bitmap.Dispose();
    }

    public static void SaveIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            long dataOffset = 6 + 16 * sizes.Length;
            var entries = new MemoryStream();
            foreach (int size in sizes)
            {
                using (var image = Render(size, size, true, false, false))
                using (var png = new MemoryStream())
                {
                    image.Save(png, ImageFormat.Png);
                    byte[] bytes = png.ToArray();
                    writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write((uint)bytes.Length); writer.Write((uint)dataOffset);
                    dataOffset += bytes.Length;
                    entries.Write(bytes, 0, bytes.Length);
                }
            }
            writer.Flush();
            entries.Position = 0; entries.CopyTo(stream); entries.Dispose();
        }
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing

function Save-IconPng([string] $folder, [string] $name, [int] $width, [int] $height, [bool] $tile, [bool] $wordmark, [bool] $monochrome = $false) {
    $path = Join-Path $folder $name
    $bitmap = [PrintSinkIconArt]::Render($width, $height, $tile, $wordmark, $monochrome)
    [PrintSinkIconArt]::Save($bitmap, $path)
}

foreach ($folder in @($appAssets, $testAssets)) {
    Save-IconPng $folder 'Square150x150Logo.scale-200.png' 300 300 $true $false
    Save-IconPng $folder 'Square44x44Logo.scale-200.png' 88 88 $true $false
    Save-IconPng $folder 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 $false $false $true
    Save-IconPng $folder 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48 $false $false $true
    Save-IconPng $folder 'StoreLogo.png' 50 50 $true $false
    Save-IconPng $folder 'Wide310x150Logo.scale-200.png' 620 300 $false $true
    Save-IconPng $folder 'SplashScreen.scale-200.png' 1240 600 $false $false
    Save-IconPng $folder 'LockScreenLogo.scale-200.png' 48 48 $false $false $true
}

[PrintSinkIconArt]::SaveIcon((Join-Path $appAssets 'AppIcon.ico'))

