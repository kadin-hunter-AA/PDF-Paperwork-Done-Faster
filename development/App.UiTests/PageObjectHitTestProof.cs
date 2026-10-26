using System.Text.Json;
using System.Runtime.InteropServices;
using System.IO;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class PageObjectHitTestProof
{
    public static Task RunAsync(string root)
    {
        // PDFium PageInfo reports dimensions after intrinsic rotation. These tests use a
        // non-square 600 × 800 pt page and both native-size and differently-scaled viewports.
        var cases = new[]
        {
            (Rotation: 0, Width: 600d, Height: 800d, ViewWidth: 300d, ViewHeight: 400d, Expected: (150d, 200d)),
            (Rotation: 1, Width: 800d, Height: 600d, ViewWidth: 400d, ViewHeight: 300d, Expected: (450d, 200d)),
            (Rotation: 2, Width: 600d, Height: 800d, ViewWidth: 450d, ViewHeight: 600d, Expected: (450d, 600d)),
            (Rotation: 3, Width: 800d, Height: 600d, ViewWidth: 600d, ViewHeight: 450d, Expected: (150d, 600d))
        };
        foreach (var test in cases)
        {
            var point = PageObjectHitTest.ToPdf(test.ViewWidth / 4, test.ViewHeight * 3 / 4,
                test.Width, test.Height, test.ViewWidth, test.ViewHeight, test.Rotation);
            if (Math.Abs(point.X - test.Expected.Item1) > 1e-9 || Math.Abs(point.Y - test.Expected.Item2) > 1e-9)
                throw new Exception($"PDFium page/device transform mismatch for rotation {test.Rotation}: {point}");
        }

        // The same relative point at 50% and 200% view scale resolves to the same page point.
        var small = PageObjectHitTest.ToPdf(100, 300, 600, 800, 200, 400, 0);
        var large = PageObjectHitTest.ToPdf(400, 1200, 600, 800, 800, 1600, 0);
        if (small != large || small != (300d, 200d))
            throw new Exception("Page hit coordinates remain stable across view scales");

        using var data = JsonDocument.Parse("""
            {"Objects":[
              {"Id":"under","Type":1,"HasBounds":true,"Left":0,"Bottom":0,"Right":600,"Top":800},
              {"Id":"top","Type":3,"HasBounds":true,"Left":100,"Bottom":100,"Right":500,"Top":700},
              {"Id":"overhang","Type":1,"HasBounds":true,"Left":-100,"Bottom":-100,"Right":900,"Top":1000}]}
            """);
        if (PageObjectHitTest.Find(data.RootElement, 300, 400)?.GetProperty("Id").GetString() != "overhang")
            throw new Exception("Last painted eligible object wins when bounds overlap");
        if (PageObjectHitTest.Find(data.RootElement, 0, 0)?.GetProperty("Id").GetString() != "overhang")
            throw new Exception("Page-edge hits remain eligible for overlapping object bounds");

        var outside = PageObjectHitTest.ToPdf(-0.01, 200, 600, 800, 300, 400, 0);
        if (PageObjectHitTest.Find(data.RootElement, outside.X, outside.Y) is not null)
            throw new Exception("Outside-page coordinates cannot select an overhanging object");
        var nonfinite = PageObjectHitTest.ToPdf(double.NaN, 200, 600, 800, 300, 400, 0);
        if (PageObjectHitTest.Find(data.RootElement, nonfinite.X, nonfinite.Y) is not null)
            throw new Exception("Non-finite pointer coordinates cannot select an object");
        foreach (var invalid in new[]
        {
            (Width: 0d, Height: 800d, ViewWidth: 300d, ViewHeight: 400d),
            (Width: 600d, Height: double.NaN, ViewWidth: 300d, ViewHeight: 400d),
            (Width: 600d, Height: 800d, ViewWidth: 0d, ViewHeight: 400d),
            (Width: 600d, Height: 800d, ViewWidth: 300d, ViewHeight: double.PositiveInfinity)
        })
        {
            try
            {
                _ = PageObjectHitTest.ToPdf(1, 1, invalid.Width, invalid.Height,
                    invalid.ViewWidth, invalid.ViewHeight, 0);
                throw new Exception("Invalid page or viewport dimensions must be rejected");
            }
            catch (InvalidOperationException) { }
        }

        var nativeComparisons = VerifyAgainstNativePdfium(root);
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var resultPath = Path.Combine(artifacts, "page-hit-geometry-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            Proof = "PageObjectHitTest agrees with PDFium FPDF_DeviceToPage",
            Fixture = "fixtures/corpus-v1/engine/page-source.pdf",
            NativeLibrary = "App.Worker/bin/Debug/net10.0/pdfium.dll",
            NativeComparisons = nativeComparisons,
            PhysicalMouseInteraction = false,
            EvidenceLimit = "Native coordinate transform and hit selection only; does not validate WPF pointer routing or physical input",
            Rotations = new[] { 0, 1, 2, 3 },
            ViewScales = new[] { 0.5, 1.0, 2.0 },
            SamplesPerRotationAndScale = 4,
            OverlapPaintOrder = "last eligible bounded text/image wins",
            OutsideAndInvalidInputs = "outside/non-finite pointer coordinates do not hit; zero/non-finite page or viewport dimensions are rejected"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }

    static int VerifyAgainstNativePdfium(string root)
    {
        var pdfium = Path.GetFullPath(Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "pdfium.dll"));
        var source = Path.GetFullPath(Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf"));
        if (!File.Exists(pdfium) || !File.Exists(source)) throw new FileNotFoundException("PDFium geometry proof inputs are missing.");
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            name == "pdfium" ? NativeLibrary.Load(pdfium) : IntPtr.Zero);

        var comparisons = 0;
        Native.FPDF_InitLibrary();
        try
        {
            var document = Native.FPDF_LoadDocument(source, null);
            if (document == IntPtr.Zero) throw new Exception("Native PDFium could not open the non-square geometry fixture.");
            try
            {
                var page = Native.FPDF_LoadPage(document, 0);
                if (page == IntPtr.Zero) throw new Exception("Native PDFium could not load the geometry fixture page.");
                try
                {
                    Native.FPDFPage_SetRotation(page, 0);
                    var baseWidth = Native.FPDF_GetPageWidthF(page);
                    var baseHeight = Native.FPDF_GetPageHeightF(page);
                    if (Math.Abs(baseWidth - baseHeight) < 1)
                        throw new Exception("Geometry fixture must be non-square for rotated mapping proof.");

                    for (var rotation = 0; rotation < 4; rotation++)
                    {
                        Native.FPDFPage_SetRotation(page, rotation);
                        if (Native.FPDFPage_GetRotation(page) != rotation)
                            throw new Exception($"Native PDFium did not apply rotation {rotation}.");
                        var width = (double)Native.FPDF_GetPageWidthF(page);
                        var height = (double)Native.FPDF_GetPageHeightF(page);
                        if (rotation % 2 == 1 && Math.Abs(width - baseHeight) > 0.1 ||
                            rotation % 2 == 0 && Math.Abs(width - baseWidth) > 0.1)
                            throw new Exception($"Native PDFium did not report rotated display width for rotation {rotation}.");

                        foreach (var scale in new[] { 0.5, 1.0, 2.0 })
                        {
                            var viewWidth = Math.Max(2, (int)Math.Round(width * scale));
                            var viewHeight = Math.Max(2, (int)Math.Round(height * scale));
                            var samples = new[]
                            {
                                (X: 0, Y: 0), (X: viewWidth / 4, Y: viewHeight / 4),
                                (X: viewWidth / 2, Y: viewHeight / 2), (X: viewWidth - 1, Y: viewHeight - 1)
                            };
                            foreach (var pixel in samples)
                            {
                                if (Native.FPDF_DeviceToPage(page, 0, 0, viewWidth, viewHeight, 0,
                                    pixel.X, pixel.Y, out var nativeX, out var nativeY) == 0)
                                    throw new Exception($"Native FPDF_DeviceToPage failed at rotation {rotation}, scale {scale}, pixel {pixel}.");
                                var appPoint = PageObjectHitTest.ToPdf(pixel.X, pixel.Y,
                                    width, height, viewWidth, viewHeight, rotation);
                                if (Math.Abs(nativeX - appPoint.X) > 0.5 || Math.Abs(nativeY - appPoint.Y) > 0.5)
                                    throw new Exception($"Hit mapping differs from native FPDF_DeviceToPage at rotation {rotation}, scale {scale}, pixel {pixel}: app={appPoint}, native=({nativeX},{nativeY}).");
                                comparisons++;
                            }
                        }
                    }
                }
                finally { Native.FPDF_ClosePage(page); }
            }
            finally { Native.FPDF_CloseDocument(document); }
        }
        finally { Native.FPDF_DestroyLibrary(); }
        return comparisons;
    }

    static class Native
    {
        const string Dll = "pdfium";

        [DllImport(Dll)] internal static extern void FPDF_InitLibrary();
        [DllImport(Dll)] internal static extern void FPDF_DestroyLibrary();
        [DllImport(Dll, EntryPoint = "FPDF_LoadDocument", CharSet = CharSet.Ansi)]
        internal static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, string? password);
        [DllImport(Dll)] internal static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll, EntryPoint = "FPDF_GetPageWidthF")] internal static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll, EntryPoint = "FPDF_GetPageHeightF")] internal static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] internal static extern void FPDFPage_SetRotation(IntPtr page, int rotation);
        [DllImport(Dll)] internal static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] internal static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    }
}
