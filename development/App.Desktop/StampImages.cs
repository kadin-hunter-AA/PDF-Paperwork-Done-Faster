using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HunterPdf.Desktop;

internal enum SignatureKind { Signature, Initials }

/// <summary>Straight-alpha BGRA raster ready for DocumentSession.AddImageAsync.</summary>
internal sealed record SignatureImage(byte[] Bgra, int Width, int Height, SignatureKind Kind);

/// <summary>Transparent stamp rendering and the shared BGRA helpers used by the signature dialog and store.</summary>
internal static class StampImages
{
    internal const int MaxWidth = 1600, MaxHeight = 600, MaxPngBytes = 16 * 1024 * 1024;
    internal static readonly Color Black = Color.FromRgb(0x17, 0x19, 0x28), DarkBlue = Color.FromRgb(0x1B, 0x2A, 0x6B);

    public static SignatureImage Date(string text, int pixelHeight = 48)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) throw new ArgumentException("Date text must be 1 to 40 characters.", nameof(text));
        CheckHeight(pixelHeight);
        var brush = new SolidColorBrush(Black); brush.Freeze();
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text.Trim(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, pixelHeight / 1.4, brush, 1.0);
        var width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 8;
        if (width > MaxWidth) throw new ArgumentException("Date text is too wide for the requested height.", nameof(text));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(formatted, new Point(4, (pixelHeight - formatted.Height) / 2));
        return new SignatureImage(Render(visual, width, pixelHeight), width, pixelHeight, SignatureKind.Signature);
    }

    public static SignatureImage Check(int pixelHeight = 48) => Mark(pixelHeight, dc =>
    {
        var s = pixelHeight; var geometry = new StreamGeometry();
        using (var g = geometry.Open()) { g.BeginFigure(new Point(s * .14, s * .55), false, false); g.LineTo(new Point(s * .40, s * .82), true, true); g.LineTo(new Point(s * .88, s * .16), true, true); }
        geometry.Freeze(); dc.DrawGeometry(null, Pen(s), geometry);
    });

    public static SignatureImage Cross(int pixelHeight = 48) => Mark(pixelHeight, dc =>
    {
        var s = pixelHeight; var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(s * .18, s * .18), false, false); g.LineTo(new Point(s * .82, s * .82), true, true);
            g.BeginFigure(new Point(s * .82, s * .18), false, false); g.LineTo(new Point(s * .18, s * .82), true, true);
        }
        geometry.Freeze(); dc.DrawGeometry(null, Pen(s), geometry);
    });

    static Pen Pen(int s) { var brush = new SolidColorBrush(Black); brush.Freeze(); var pen = new Pen(brush, Math.Max(2, s * .1)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    static SignatureImage Mark(int pixelHeight, Action<DrawingContext> draw)
    {
        CheckHeight(pixelHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        return new SignatureImage(Render(visual, pixelHeight, pixelHeight), pixelHeight, pixelHeight, SignatureKind.Signature);
    }
    static void CheckHeight(int h) { if (h < 8 || h > MaxHeight) throw new ArgumentOutOfRangeException(nameof(h), "Stamp height must be 8 to 600 pixels."); }

    /// <summary>Renders a visual to straight-alpha BGRA at exactly width x height pixels.</summary>
    internal static byte[] Render(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
        return ToBgra(target);
    }
    internal static byte[] ToBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }
    internal static BitmapSource ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4); bitmap.Freeze(); return bitmap;
    }

    public static byte[] BgraToPng(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length != checked(width * height * 4)) throw new ArgumentException("Pixel data does not match its dimensions.", nameof(bgra));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ToBitmap(bgra, width, height)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    public static (byte[] Bgra, int Width, int Height) PngToBgra(byte[] png)
    {
        if (png.Length < 8 || png.Length > MaxPngBytes) throw new InvalidDataException("The signature image is not a valid PNG.");
        using var stream = new MemoryStream(png, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not PngBitmapDecoder || decoder.Frames.Count != 1) throw new InvalidDataException("The signature image is not a valid PNG.");
        var frame = decoder.Frames[0]; int w = frame.PixelWidth, h = frame.PixelHeight;
        if (w < 1 || h < 1 || w > 4096 || h > 4096 || (long)w * h > 4_000_000) throw new InvalidDataException("The signature image has unsupported dimensions.");
        return (ToBgra(frame), w, h);
    }

    /// <summary>Makes near-white pixels transparent with a soft edge so scanned strokes keep smooth anti-aliasing.</summary>
    internal static byte[] RemoveWhite(byte[] bgra)
    {
        var result = (byte[])bgra.Clone();
        for (var i = 0; i < result.Length; i += 4)
        {
            var lightest = Math.Min(result[i], Math.Min(result[i + 1], result[i + 2]));
            if (lightest >= 240) result[i + 3] = 0;
            else if (lightest > 200) result[i + 3] = (byte)(result[i + 3] * (240 - lightest) / 40);
        }
        return result;
    }

    /// <summary>Crops fully-transparent margins (keeps a 2 px pad). Returns null when there is no visible ink.</summary>
    internal static SignatureImage? Trim(byte[] bgra, int width, int height, SignatureKind kind)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (bgra[(y * width + x) * 4 + 3] >= 8) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        if (maxX < 0) return null;
        minX = Math.Max(0, minX - 2); minY = Math.Max(0, minY - 2); maxX = Math.Min(width - 1, maxX + 2); maxY = Math.Min(height - 1, maxY + 2);
        int w = maxX - minX + 1, h = maxY - minY + 1; var cropped = new byte[w * h * 4];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(bgra, ((minY + y) * width + minX) * 4, cropped, y * w * 4, w * 4);
        return new SignatureImage(cropped, w, h, kind);
    }

    /// <summary>Downscales (never upscales) to fit within 1600 x 600, keeping aspect ratio.</summary>
    internal static SignatureImage Fit(SignatureImage image)
    {
        if (image.Width <= MaxWidth && image.Height <= MaxHeight) return image;
        var scale = Math.Min((double)MaxWidth / image.Width, (double)MaxHeight / image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var source = ToBitmap(image.Bgra, image.Width, image.Height);
        var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, w, h));
        return new SignatureImage(Render(visual, w, h), w, h, image.Kind);
    }
}
