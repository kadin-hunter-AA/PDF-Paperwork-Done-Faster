using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

// Real worker rendering (EMF and raster) through the streaming runner into a fake sink, plus layout math. No printer queue is used.
internal static class PrintOutputProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "print-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }

        // ---- layout math ----
        var letter = new[] { new PrintPageSize(612, 792), new PrintPageSize(792, 612), new PrintPageSize(612, 792), new PrintPageSize(612, 792), new PrintPageSize(612, 792) };
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        var fit = PrintLayout.PlanSheet(new[] { 0 }, letter, new PrintOptions(AutoOrientation: false), 576, 756, false);
        Check(!fit.Landscape && Near(fit.Placements[0].Scale, Math.Min(576 / 612d, 756 / 792d)) && fit.Placements[0].Target.Width <= 576 + 1e-6 && Near(fit.Placements[0].Target.X, 576 / 2 - fit.Placements[0].Target.Width / 2),
            "Fit to page scales a Letter page into the printable area and centers it");
        var actual = PrintLayout.PlanSheet(new[] { 0 }, letter, new PrintOptions(PrintScaling.ActualSize, AutoOrientation: false), 576, 756, false);
        Check(Near(actual.Placements[0].Scale, 1) && Near(actual.Placements[0].Target.Width, 612) && Near(actual.Placements[0].Target.X, (576 - 612) / 2d) && actual.Placements[0].Clip.Width == 576,
            "Actual size keeps 100% scale, centered and clipped to the printable area");
        var custom = PrintLayout.PlanSheet(new[] { 0 }, letter, new PrintOptions(PrintScaling.Custom, 50, AutoOrientation: false), 576, 756, false);
        Check(Near(custom.Placements[0].Scale, 0.5) && Near(custom.Placements[0].Target.Height, 396) && Near(custom.Placements[0].Target.Y, (756 - 396) / 2d), "Custom percent scales and centers the page");
        var autoLandscape = PrintLayout.PlanSheet(new[] { 1 }, letter, new PrintOptions(), 576, 756, false);
        Check(autoLandscape.Landscape && Near(autoLandscape.AreaWidth, 756), "Automatic orientation turns the sheet for a landscape page");
        var autoPortrait = PrintLayout.PlanSheet(new[] { 0 }, letter, new PrintOptions(), 576, 756, false);
        Check(!autoPortrait.Landscape, "Automatic orientation keeps portrait for a portrait page");
        var fixedOrientation = PrintLayout.PlanSheet(new[] { 1 }, letter, new PrintOptions(AutoOrientation: false), 576, 756, false);
        Check(!fixedOrientation.Landscape, "Orientation stays with the printer when automatic orientation is off");
        var two = PrintLayout.PlanSheet(new[] { 0, 2 }, letter, new PrintOptions(PagesPerSheet: 2, AutoOrientation: true), 576, 756, false);
        Check(two.Landscape && Near(two.Placements[0].Clip.Y, two.Placements[1].Clip.Y) && two.Placements[1].Clip.X > two.Placements[0].Clip.Right - 1e-6 && two.Placements.All(p => p.Clip.Right <= two.AreaWidth + 1e-6),
            "2-up places two portrait pages side by side on a landscape sheet");
        var four = PrintLayout.PlanSheet(new[] { 0, 2, 3, 4 }, letter, new PrintOptions(PagesPerSheet: 4), 576, 756, false);
        Check(four.Placements.Count == 4 && four.Placements[2].Clip.Y > four.Placements[0].Clip.Y && four.Placements[1].Clip.X > four.Placements[0].Clip.X
            && four.Placements.All(p => p.Target.Right <= p.Clip.Right + 1e-6 && p.Target.Bottom <= p.Clip.Bottom + 1e-6) && four.Placements.Select(p => p.PageIndex).SequenceEqual(new[] { 0, 2, 3, 4 }),
            "4-up fills a 2 by 2 grid in reading order with every page inside its cell");
        Check(PrintLayout.Sheets(Enumerable.Range(0, 5).ToArray(), 2).Select(s => s.Length).SequenceEqual(new[] { 2, 2, 1 }), "N-up groups an odd page count into a short final sheet");
        foreach (var dimensions in new[] { (double.MaxValue, double.MaxValue), (double.MaxValue, 1d), (612d, 792d), (14400d, 14400d) })
        {
            var raster = PrintLayout.GetRasterSize(dimensions.Item1, dimensions.Item2);
            Check(raster.Width is >= 1 and <= 32768 && raster.Height is >= 1 and <= 32768 && (long)raster.Width * raster.Height <= 40_000_000, $"Raster budget stays bounded for page {dimensions.Item1} by {dimensions.Item2}");
        }
        var letterRaster = PrintLayout.GetRasterSize(612, 792);
        Check(letterRaster.Width == 2550 && letterRaster.Height == 3300 && !letterRaster.Reduced && Near(letterRaster.Dpi, 300), "A Letter page rasterizes at 300 DPI");
        var huge = PrintLayout.GetRasterSize(14400, 14400);
        Check(huge.Reduced && huge.Dpi < 300, "A huge page falls back to a lower DPI and says so");
        Check(PrintPipeline.ResolvePages(8, 4, PageRangeSelection.CurrentPage, default).SequenceEqual(new[] { 4 }), "Current-page printing resolves the active PDF page");
        Check(PrintPipeline.ResolvePages(8, 0, PageRangeSelection.UserPages, new PageRange(3, 5)).SequenceEqual(new[] { 2, 3, 4 }), "User print range retains inclusive PDF page order");
        Check(PrintPipeline.ResolvePages(500, 0, PageRangeSelection.AllPages, default).Length == 500, "There is no page-count limit on a print job");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
        {
            try { PrintLayout.GetRasterSize(invalid, 100); throw new Exception("Invalid dimension accepted"); }
            catch (InvalidOperationException) { }
        }
        checks.Add("Invalid page dimensions are refused");

        // ---- real worker: 30-page document, EMF and raster, through the runner ----
        var source = Path.Combine(output, "thirty-pages.pdf");
        File.WriteAllBytes(source, BuildPdf(30));
        var hash = RecoveryStore.Fingerprint(source);
        using var session = await DocumentSession.OpenAsync(Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe"),
            new RecoveryStore(Path.Combine(output, "recovery")), source);
        var sizes = Enumerable.Range(0, 30).Select(_ => new PrintPageSize(612, 792)).ToArray();
        var all = Enumerable.Range(0, 30).ToArray();

        var emfSink = new RecordingSink();
        long peakBefore = Environment.WorkingSet;
        using (var pageSource = new SessionPrintPageSource(session))
        {
            var summary = await PrintPipeline.RunJobAsync(emfSink, pageSource, all, sizes, new PrintOptions(), "proof", null, CancellationToken.None);
            Check(summary.Pages == 30 && summary.VectorPages == 30 && summary.ImagePages == 0 && emfSink.Pages.Count == 30, "All 30 pages print through the vector (EMF) path without falling back");
        }
        Check(emfSink.Pages.All(p => p.Bytes > 1000 && p.Bytes < 64L * 1024 * 1024), "Each EMF page stays under the 64 MB bound");
        var firstEmf = emfSink.Pages[0];
        File.WriteAllBytes(Path.Combine(output, "page1.emf"), firstEmf.Data);
        var histogram = EmfRecords(firstEmf.Data);
        File.WriteAllText(Path.Combine(output, "emf-records.json"), JsonSerializer.Serialize(histogram.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(), k => k.Value)));
        int Count(params int[] types) => types.Sum(t => histogram.GetValueOrDefault(t));
        var bitmapRecords = EmfBitmapDataRecords(firstEmf.Data); // BITBLT/STRETCHDIBITS/etc. that actually carry pixel data (pattern-only BITBLT fills carry none)
        var textRecords = Count(83, 84); // EXTTEXTOUTA/W
        var pathRecords = Count(59, 60, 61, 62, 63, 64, 3, 4, 85, 86, 87, 88, 89, 90, 91); // path + polygon/polyline families
        Check(histogram.GetValueOrDefault(1) == 1 && histogram.GetValueOrDefault(14) == 1, "EMF has a header and end record");
        Check(textRecords + pathRecords > 0 && bitmapRecords == 0, $"EMF holds vector drawing (text-out records {textRecords}, path/polygon/bezier records {pathRecords}) and no records carrying bitmap pixels");

        // Sharpness: EMF played at 600 DPI vs PDFium's direct 600 DPI render (ground truth) and the old 150 DPI raster scaled up.
        const int w = 5100, h = 6600;
        var truthPath = Path.Combine(Path.GetTempPath(), "HunterPdfProof-" + Guid.NewGuid().ToString("N") + ".bgra");
        byte[] truth, lowUp, emfPixels;
        try
        {
            await session.RenderPrintPageAsync(0, PrintRenderMode.Raster, truthPath, w, h);
            truth = File.ReadAllBytes(truthPath);
        }
        finally { File.Delete(truthPath); }
        var lowPath = Path.Combine(Path.GetTempPath(), "HunterPdfProof-" + Guid.NewGuid().ToString("N") + ".bgra");
        try
        {
            await session.RenderPrintPageAsync(0, PrintRenderMode.Raster, lowPath, 1275, 1650);
            lowUp = UpscaleBilinear(File.ReadAllBytes(lowPath), 1275, 1650, w, h);
        }
        finally { File.Delete(lowPath); }
        emfPixels = PlayEmf(firstEmf.Data, w, h);
        foreach (var (name, pixels) in new[] { ("emf-600dpi.png", emfPixels), ("pdfium-600dpi.png", truth) })
        {
            var crop = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, w * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new System.Windows.Media.Imaging.CroppedBitmap(crop, new System.Windows.Int32Rect(0, 0, w, 2400))));
            using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        var emfError = MeanLumaError(truth, emfPixels);
        var lowError = MeanLumaError(truth, lowUp);
        File.WriteAllText(Path.Combine(output, "sharpness.json"), JsonSerializer.Serialize(new { EmfMeanLumaError = emfError, Raster150UpscaledMeanLumaError = lowError }));
        Check(emfError < lowError, $"EMF played at 600 DPI is closer to the true 600 DPI page than a 150 DPI raster (error {emfError:0.00} vs {lowError:0.00})");
        Check(Ink(emfPixels) > 1000, "EMF playback draws visible page content");

        var memoryBefore = GC.GetTotalMemory(true);
        var rasterSink = new RecordingSink();
        using (var pageSource = new SessionPrintPageSource(session))
        {
            var summary = await PrintPipeline.RunJobAsync(rasterSink, pageSource, all, sizes, new PrintOptions(AsImage: true), "proof", null, CancellationToken.None);
            Check(summary.Pages == 30 && summary.ImagePages == 30 && rasterSink.Pages.All(p => !p.IsEmf && p.Width == 2550 && p.Height == 3300 && p.Bytes == 2550L * 3300 * 4), "Print as image renders all 30 pages at 300 DPI (2550 by 3300)");
        }
        GC.Collect();
        Check(GC.GetTotalMemory(true) - memoryBefore < 64L * 1024 * 1024, "Managed memory does not grow across a 30-page 300 DPI raster job");
        var nupSink = new RecordingSink();
        using (var pageSource = new SessionPrintPageSource(session))
        {
            var summary = await PrintPipeline.RunJobAsync(nupSink, pageSource, all.Take(8).ToArray(), sizes, new PrintOptions(PagesPerSheet: 4), "proof", null, CancellationToken.None);
            Check(summary.Sheets == 2 && nupSink.Pages.Count == 8 && nupSink.Sheets.All(s => s.Placements.Count == 4), "4-up job prints 8 real pages on 2 sheets");
        }
        Check(RecoveryStore.Fingerprint(source) == hash && session.Revision == 0, "Printing leaves the source file and document revision unchanged");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            Passed = checks,
            Scope = "Real PDFium worker EMF/raster rendering, GDI playback to memory, streaming runner with a recording sink. No printer queue, driver, spooler or physical paper; the GDI printer sink is not exercised."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} print output checks. Evidence: {output}");
    }

    sealed record RecordedPage(bool IsEmf, int Width, int Height, long Bytes, byte[] Data);

    sealed class RecordingSink : IPrintSink
    {
        internal List<RecordedPage> Pages { get; } = new();
        internal List<SheetPlan> Sheets { get; } = new();
        public PrintDeviceInfo Device => new(612, 792, false, 1);
        public Task BeginJobAsync(string jobName, CancellationToken cancellation) => Task.CompletedTask;
        public Task BeginSheetAsync(SheetPlan plan, CancellationToken cancellation) { Sheets.Add(plan); return Task.CompletedTask; }
        public Task DrawPageAsync(PrintPlacement placement, PrintPagePayload payload, CancellationToken cancellation)
        {
            var length = new FileInfo(payload.Path).Length;
            // Keep full data only for the first EMF; the rest only need their size.
            Pages.Add(new RecordedPage(payload.IsEmf, payload.PixelWidth, payload.PixelHeight, length, payload.IsEmf && Pages.Count == 0 ? File.ReadAllBytes(payload.Path) : Array.Empty<byte>()));
            return Task.CompletedTask;
        }
        public Task EndSheetAsync(CancellationToken cancellation) => Task.CompletedTask;
        public Task EndJobAsync(bool completed) => Task.CompletedTask;
    }

    static Dictionary<int, int> EmfRecords(byte[] emf)
    {
        var result = new Dictionary<int, int>();
        for (var offset = 0; offset + 8 <= emf.Length;)
        {
            var type = BitConverter.ToInt32(emf, offset);
            var size = BitConverter.ToInt32(emf, offset + 4);
            if (size < 8 || size % 4 != 0 || offset + size > emf.Length) throw new Exception("Malformed EMF record stream");
            result[type] = result.GetValueOrDefault(type) + 1;
            offset += size;
        }
        return result;
    }

    static int EmfBitmapDataRecords(byte[] emf)
    {
        var count = 0;
        for (var offset = 0; offset + 8 <= emf.Length;)
        {
            var type = BitConverter.ToInt32(emf, offset); var size = BitConverter.ToInt32(emf, offset + 4);
            if (type == 76 && size >= 100 && BitConverter.ToInt32(emf, offset + 96) > 0) count++; // EMR_BITBLT cbBitsSrc
            else if (type is 81 or 80 or 77 or 114 or 115 or 78 or 79) count++; // STRETCHDIBITS, SETDIBITSTODEVICE, MASKBLT, ALPHABLEND, TRANSPARENTBLT, PLGBLT, SETDIBITSTODEVICE
            offset += size;
        }
        return count;
    }

    static byte[] PlayEmf(byte[] emf, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), "HunterPdfProof-" + Guid.NewGuid().ToString("N") + ".emf");
        File.WriteAllBytes(path, emf);
        try
        {
            var metafile = GetEnhMetaFileW(path);
            if (metafile == 0) throw new Exception("EMF could not be loaded");
            var dc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var section = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            var old = SelectObject(dc, section);
            try
            {
                var white = new byte[width * 4]; Array.Fill(white, (byte)255);
                for (var y = 0; y < height; y++) Marshal.Copy(white, 0, bits + (nint)((long)y * width * 4), white.Length);
                var rect = new Rect { Right = width, Bottom = height };
                if (!PlayEnhMetaFile(dc, metafile, ref rect)) throw new Exception("EMF playback failed");
                GdiFlush();
                var pixels = new byte[(long)width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally { SelectObject(dc, old); DeleteObject(section); DeleteDC(dc); DeleteEnhMetaFile(metafile); }
        }
        finally { File.Delete(path); }
    }

    static double Luma(byte[] p, long i) => 0.114 * p[i] + 0.587 * p[i + 1] + 0.299 * p[i + 2];
    static double MeanLumaError(byte[] a, byte[] b)
    {
        double sum = 0; long count = a.Length / 4;
        for (long i = 0; i < a.Length; i += 4) sum += Math.Abs(Luma(a, i) - Luma(b, i));
        return sum / count;
    }
    static long Ink(byte[] p) { long n = 0; for (long i = 0; i < p.Length; i += 4) if (Luma(p, i) < 128) n++; return n; }

    static byte[] UpscaleBilinear(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[(long)dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var fy = Math.Clamp((y + 0.5) * sh / dh - 0.5, 0, sh - 1); var y0 = (int)fy; var y1 = Math.Min(sh - 1, y0 + 1); var ty = fy - y0;
            for (var x = 0; x < dw; x++)
            {
                var fx = Math.Clamp((x + 0.5) * sw / dw - 0.5, 0, sw - 1); var x0 = (int)fx; var x1 = Math.Min(sw - 1, x0 + 1); var tx = fx - x0;
                for (var c = 0; c < 3; c++)
                {
                    double v00 = src[((long)y0 * sw + x0) * 4 + c], v10 = src[((long)y0 * sw + x1) * 4 + c], v01 = src[((long)y1 * sw + x0) * 4 + c], v11 = src[((long)y1 * sw + x1) * 4 + c];
                    dst[((long)y * dw + x) * 4 + c] = (byte)Math.Round((v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty);
                }
                dst[((long)y * dw + x) * 4 + 3] = 255;
            }
        }
        return dst;
    }

    // Minimal valid PDF: Helvetica text, a drawn rule and a filled box on every page.
    static byte[] BuildPdf(int pageCount)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new StringBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            var page = 4 + i * 2;
            kids.Append($"{page} 0 R ");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {page + 1} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            var stream = $"BT /F1 24 Tf 72 700 Td (Print proof page {i + 1}) Tj ET BT /F1 9 Tf 72 670 Td (The quick brown fox jumps over the lazy dog 0123456789) Tj ET 0.5 w 72 660 m 540 660 l S 0.2 0.3 0.8 rg 72 560 120 60 re f";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>";
        objects.Insert(2, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern nint GetEnhMetaFileW(string path);
    [DllImport("gdi32")] static extern bool DeleteEnhMetaFile(nint emf);
    [DllImport("gdi32")] static extern bool PlayEnhMetaFile(nint dc, nint emf, ref Rect rect);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] static extern bool GdiFlush();
}
