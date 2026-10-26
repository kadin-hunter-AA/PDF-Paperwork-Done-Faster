using System.Runtime.InteropServices;
using System.Text.Json;
using HunterPdf.Core;

static class PageCoordinateSafetyProof
{
    public static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "page-coordinate-safety");
        Directory.CreateDirectory(work);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }

        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var packages = Path.Combine(root, "tools", "python-packages");
        var nativePath = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "pdfium.dll");
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (_, _, _) => NativeLibrary.Load(nativePath));

        var ordinary = Path.Combine(work, "ordinary.pdf");
        File.Copy(source, ordinary, overwrite: true);
        using (var ordinarySession = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(work, "ordinary-recovery")), ordinary))
        {
            var page = await ordinarySession.ExtractAsync(0);
            Check(page.GetProperty("SupportsZeroOriginCoordinates").GetBoolean(),
                "A normal zero-origin page reports coordinate support");
            await ordinarySession.CloseAsync();
        }

        foreach (var mode in new[] { "crop", "media" })
        {
            var fixture = Path.Combine(work, $"offset-{mode}.pdf");
            MakeOffsetFixture(python, packages, source, fixture, mode);
            var native = ReadNativeBounds(fixture);
            Check(native.Left > 0 && native.Bottom > 0 && native.Right > native.Left && native.Top > native.Bottom,
                $"Native PDFium reports a finite positive offset visible box for {mode} fixture");
            Check(Math.Abs(native.OriginX - native.Left) < 0.01 && Math.Abs(native.OriginY - native.Top) < 0.01,
                $"FPDF_DeviceToPage maps the rendered top-left to the actual offset origin for {mode} fixture");

            var fixtureHash = RecoveryStore.Fingerprint(fixture);
            using var session = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(work, $"{mode}-recovery")), fixture);
            var extracted = await session.ExtractAsync(0);
            Check(!extracted.GetProperty("SupportsZeroOriginCoordinates").GetBoolean(),
                $"Extract still discloses a non-zero origin for the offset {mode} box");
            var revision = session.Revision;
            var annotationCount = extracted.GetProperty("AnnotationCount").GetInt32();
            var pixels = (await session.RenderAsync(0, 320, 320)).GetProperty("Bgra").GetString();
            // Coordinates that would be valid only under a zero-origin assumption lie outside the visible box and are refused.
            try
            {
                await session.AddDrawingAsync(0, "Rectangle", 10, 10, 60, 60);
                throw new Exception($"Out-of-box drawing unexpectedly succeeded for offset {mode} box");
            }
            catch (InvalidOperationException) { checks.Add($"Drawing outside the offset {mode} visible box is refused before mutation"); }
            var after = await session.ExtractAsync(0);
            Check(session.Revision == revision && !session.IsDirty && !session.CanUndo,
                $"Refused drawing leaves revision, dirty state, and undo history unchanged for {mode} box");
            Check(after.GetProperty("AnnotationCount").GetInt32() == annotationCount &&
                  (await session.RenderAsync(0, 320, 320)).GetProperty("Bgra").GetString() == pixels,
                $"Refused drawing leaves annotations and rendering unchanged for {mode} box");
            // Inside the visible box the same drawing is now supported, with /Rect in default user space.
            var box = extracted.GetProperty("VisibleBox");
            double vl = box.GetProperty("Left").GetDouble(), vb = box.GetProperty("Bottom").GetDouble();
            Check(Math.Abs(vl - native.Left) < 0.01 && Math.Abs(vb - native.Bottom) < 0.01, $"Extract reports the visible box origin for {mode} fixture");
            await session.AddDrawingAsync(0, "Rectangle", vl + 20, vb + 20, vl + 70, vb + 70);
            Check(session.IsDirty && (await session.RenderAsync(0, 320, 320)).GetProperty("Bgra").GetString() != pixels, $"Drawing inside the offset {mode} box is applied and rendered");
            var drawn = (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Last();
            Check(Math.Abs(drawn.GetProperty("Left").GetDouble() - (vl + 20)) < 0.01 && Math.Abs(drawn.GetProperty("Bottom").GetDouble() - (vb + 20)) < 0.01,
                $"Annotation /Rect keeps user-space coordinates for the {mode} fixture");
            await session.UndoAsync();
            Check((await session.RenderAsync(0, 320, 320)).GetProperty("Bgra").GetString() == pixels, $"Undo restores the offset {mode} page");
            var saved = Path.Combine(work, $"offset-{mode}-saved.pdf");
            await session.SaveAsync(saved);
            Check(RecoveryStore.Fingerprint(fixture) == fixtureHash, $"Offset {mode} fixture bytes are unchanged after editing and saving a copy");
            await session.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Frozen source fixture remains byte-for-byte unchanged");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks, NativeComparisons = 2 }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} page coordinate safety checks (2 native comparisons). Evidence: {work}");
    }

    static void MakeOffsetFixture(string python, string packages, string source, string destination, string mode)
    {
        const string script = """
            import sys
            sys.path.insert(0,sys.argv[5])
            from pypdf import PdfReader, PdfWriter
            reader=PdfReader(sys.argv[1]); writer=PdfWriter(clone_from=reader); page=writer.pages[0]
            mode=sys.argv[3]
            if mode=='crop':
                page.cropbox.lower_left=(100,200); page.cropbox.upper_right=(500,600)
            else:
                page.mediabox.lower_left=(100,200); page.mediabox.upper_right=(712,992)
                page.cropbox.lower_left=(100,200); page.cropbox.upper_right=(712,992)
            with open(sys.argv[2],'wb') as stream: writer.write(stream)
            """;
        var start = new System.Diagnostics.ProcessStartInfo(python)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, source, destination, mode, "", packages }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start offset fixture generator.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Offset fixture generation failed: {stderr}{stdout}");
    }

    static (double Left, double Top, double Right, double Bottom, double OriginX, double OriginY) ReadNativeBounds(string path)
    {
        Native.InitLibrary();
        try
        {
            var document = Native.LoadDocument(path, null);
            if (document == 0) throw new Exception("Native PDFium could not load generated fixture.");
            try
            {
                var page = Native.LoadPage(document, 0);
                if (page == 0) throw new Exception("Native PDFium could not load generated fixture page.");
                try
                {
                    if (Native.GetPageBoundingBox(page, out var rect) == 0)
                        throw new Exception("Native PDFium did not return the visible page box.");
                    var width = checked((int)Math.Ceiling(Native.GetPageWidthF(page) * 2));
                    var height = checked((int)Math.Ceiling(Native.GetPageHeightF(page) * 2));
                    if (width <= 0 || height <= 0 || Native.DeviceToPage(page, 0, 0, width, height, 0, 0, 0,
                            out var x, out var y) == 0)
                        throw new Exception("Native PDFium device-to-page conversion failed.");
                    return (rect.Left, rect.Top, rect.Right, rect.Bottom, x, y);
                }
                finally { Native.ClosePage(page); }
            }
            finally { Native.CloseDocument(document); }
        }
        finally { Native.DestroyLibrary(); }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RectF { public float Left, Top, Right, Bottom; }
        [DllImport("pdfium", EntryPoint = "FPDF_InitLibrary")] internal static extern void InitLibrary();
        [DllImport("pdfium", EntryPoint = "FPDF_DestroyLibrary")] internal static extern void DestroyLibrary();
        [DllImport("pdfium", EntryPoint = "FPDF_LoadDocument", CharSet = CharSet.Ansi)] internal static extern nint LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport("pdfium", EntryPoint = "FPDF_CloseDocument")] internal static extern void CloseDocument(nint document);
        [DllImport("pdfium", EntryPoint = "FPDF_LoadPage")] internal static extern nint LoadPage(nint document, int index);
        [DllImport("pdfium", EntryPoint = "FPDF_ClosePage")] internal static extern void ClosePage(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageBoundingBox")] internal static extern int GetPageBoundingBox(nint page, out RectF rect);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageWidthF")] internal static extern float GetPageWidthF(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageHeightF")] internal static extern float GetPageHeightF(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_DeviceToPage")] internal static extern int DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    }
}
