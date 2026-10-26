using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

// Print rendering into temp files so large pages never travel through the worker's JSON pipe.
// EMF mode records PDFium's GDI output (text/paths stay vector); raster mode writes top-down BGRA.
public sealed partial class Engine
{
    public const long MaximumPrintEmfBytes = 64L * 1024 * 1024;
    public const long MaximumPrintRasterPixels = 40_000_000;
    const int PrintRenderFlags = RenderAnnotations | RenderPrinting;

    public object RenderPrintPage(Guid id, long revision, int index, string mode, int width, int height, int dpi, string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || File.Exists(full) || Directory.Exists(full) || !Directory.Exists(Path.GetDirectoryName(full)))
            throw new InvalidOperationException("The print render output location is not valid.");
        var pdf = IsolatedRenderDocument(id, revision);
        CheckPage(pdf, index);
        var formType = FormFillNative.FPDF_GetFormType(pdf.Handle);
        if (formType is FormTypeXfaFull or FormTypeXfaForeground)
            throw new InvalidOperationException("XFA form rendering is not supported. The page was not rendered without its form values.");
        if (formType != FormTypeNone && formType != FormTypeAcroForm)
            throw new InvalidOperationException("This PDF uses an unsupported form type. The page was not rendered without its form values.");
        try
        {
            return mode switch
            {
                "Raster" => PrintRaster(pdf, index, formType, width, height, full),
                "Emf" => PrintEmf(pdf, index, formType, dpi, full),
                _ => throw new InvalidOperationException("Unknown print render mode.")
            };
        }
        catch { try { File.Delete(full); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }

    object PrintRaster(Pdf pdf, int index, int formType, int width, int height, string path)
    {
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > MaximumPrintRasterPixels)
            throw new InvalidOperationException("Print render size exceeds limit.");
        using var page = pdf.Page(index);
        using var form = formType == FormTypeAcroForm ? new FormFillPage(pdf.Handle, page.Handle, index) : null;
        var bitmap = Pdf.Native.FPDFBitmap_Create(width, height, 0);
        Pdf.Check(bitmap != 0, "bitmap");
        try
        {
            Pdf.Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
            Pdf.Native.FPDF_RenderPageBitmap(bitmap, page.Handle, 0, 0, width, height, 0, PrintRenderFlags);
            if (form is not null) FormFillNative.FPDF_FFLDraw(form.Handle, bitmap, page.Handle, 0, 0, width, height, 0, PrintRenderFlags);
            var stride = Pdf.Native.FPDFBitmap_GetStride(bitmap);
            var buffer = Pdf.Native.FPDFBitmap_GetBuffer(bitmap);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var chunk = new byte[Math.Max(stride, 4 * 1024 * 1024 / stride * stride)];
            long remaining = (long)stride * height, offset = 0;
            while (remaining > 0)
            {
                var count = (int)Math.Min(chunk.Length, remaining);
                Marshal.Copy(buffer + (nint)offset, chunk, 0, count);
                file.Write(chunk, 0, count);
                offset += count; remaining -= count;
            }
            return new { Mode = "Raster", Width = width, Height = height, Stride = stride, Bytes = (long)stride * height };
        }
        finally { Pdf.Native.FPDFBitmap_Destroy(bitmap); }
    }

    object PrintEmf(Pdf pdf, int index, int formType, int dpi, string path)
    {
        if (dpi is < 72 or > 1200) throw new InvalidOperationException("EMF resolution is out of range.");
        using var page = pdf.Page(index);
        var widthPoints = PrintNative.FPDF_GetPageWidthF(page.Handle);
        var heightPoints = PrintNative.FPDF_GetPageHeightF(page.Handle);
        if (!float.IsFinite(widthPoints) || !float.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0 || widthPoints > 14400 || heightPoints > 14400)
            throw new InvalidOperationException("This page has invalid dimensions for vector printing.");
        // Logical units are 1/dpi inch so GDI coordinates keep sub-pixel precision regardless of the reference display.
        var logicalWidth = (int)Math.Round(widthPoints / 72d * dpi);
        var logicalHeight = (int)Math.Round(heightPoints / 72d * dpi);
        var reference = PrintNative.GetDC(0);
        try
        {
            var frame = new PrintNative.Rect(0, 0, (int)Math.Round(widthPoints * 2540d / 72d), (int)Math.Round(heightPoints * 2540d / 72d));
            // GDI converts the 0.01 mm frame to device pixels with HORZRES/HORZSIZE, which is not always LOGPIXELS; use the same ratio so the viewport matches the frame.
            double PixelsPerHundredthMm(int resolution, int size) => size > 0 ? (double)PrintNative.GetDeviceCaps(reference, resolution) / (size * 100d) : 96d / 2540d;
            var viewportX = Math.Max(1, (int)Math.Round(frame.Right * PixelsPerHundredthMm(8, PrintNative.GetDeviceCaps(reference, 4))));
            var viewportY = Math.Max(1, (int)Math.Round(frame.Bottom * PixelsPerHundredthMm(10, PrintNative.GetDeviceCaps(reference, 6))));
            var dc = PrintNative.CreateEnhMetaFileW(reference, path, ref frame, null);
            if (dc == 0) throw new InvalidOperationException("The vector print recorder could not be created.");
            nint metafile = 0;
            try
            {
                PrintNative.SetMapMode(dc, 8); // MM_ANISOTROPIC
                PrintNative.SetWindowExtEx(dc, logicalWidth, logicalHeight, 0);
                PrintNative.SetViewportExtEx(dc, viewportX, viewportY, 0);
                var region = PrintNative.CreateRectRgn(0, 0, logicalWidth, logicalHeight);
                PrintNative.SelectClipRgn(dc, region); PrintNative.DeleteObject(region);
                PrintNative.SelectObject(dc, PrintNative.GetStockObject(8)); // NULL_PEN
                PrintNative.SelectObject(dc, PrintNative.GetStockObject(5)); // NULL_BRUSH
                PrintNative.FPDF_SetPrintMode(0); // EMF
                PrintNative.FPDF_RenderPage(dc, page.Handle, 0, 0, logicalWidth, logicalHeight, 0, PrintRenderFlags);
            }
            finally { metafile = PrintNative.CloseEnhMetaFile(dc); if (metafile != 0) PrintNative.DeleteEnhMetaFile(metafile); }
            if (metafile == 0) throw new InvalidOperationException("The vector print recorder failed.");
        }
        finally { PrintNative.ReleaseDC(0, reference); }
        var length = new FileInfo(path).Length;
        if (length > MaximumPrintEmfBytes)
            throw new InvalidOperationException("This page's vector print data exceeds the size limit.");
        return new { Mode = "Emf", WidthPoints = (double)widthPoints, HeightPoints = (double)heightPoints, Bytes = length, Dpi = dpi };
    }

    static class PrintNative
    {
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);
        [DllImport("pdfium")] internal static extern void FPDF_RenderPage(nint dc, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
        [DllImport("pdfium")] internal static extern void FPDF_SetPrintMode(int mode);
        [DllImport("pdfium")] internal static extern float FPDF_GetPageWidthF(nint page);
        [DllImport("pdfium")] internal static extern float FPDF_GetPageHeightF(nint page);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] internal static extern nint CreateEnhMetaFileW(nint reference, string file, ref Rect frame, string? description);
        [DllImport("gdi32")] internal static extern nint CloseEnhMetaFile(nint dc);
        [DllImport("gdi32")] internal static extern bool DeleteEnhMetaFile(nint metafile);
        [DllImport("gdi32")] internal static extern int SetMapMode(nint dc, int mode);
        [DllImport("gdi32")] internal static extern bool SetWindowExtEx(nint dc, int x, int y, nint previous);
        [DllImport("gdi32")] internal static extern bool SetViewportExtEx(nint dc, int x, int y, nint previous);
        [DllImport("gdi32")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32")] internal static extern int SelectClipRgn(nint dc, nint region);
        [DllImport("gdi32")] internal static extern bool DeleteObject(nint handle);
        [DllImport("gdi32")] internal static extern nint GetStockObject(int index);
        [DllImport("gdi32")] internal static extern nint SelectObject(nint dc, nint handle);
        [DllImport("gdi32")] internal static extern int GetDeviceCaps(nint dc, int index);
        [DllImport("user32")] internal static extern nint GetDC(nint window);
        [DllImport("user32")] internal static extern int ReleaseDC(nint window, nint dc);
    }
}
