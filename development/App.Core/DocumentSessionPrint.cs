using System.Text.Json;

namespace HunterPdf.Core;

public enum PrintRenderMode { Raster, Emf }

public sealed partial class DocumentSession
{
    /// <summary>Renders one page for printing into <paramref name="outputPath"/> (a not-yet-existing file under the temp folder).
    /// Raster writes top-down BGRA (width x height); Emf records vector GDI output at <paramref name="emfDpi"/> logical units per inch.</summary>
    public Task<JsonElement> RenderPrintPageAsync(int page, PrintRenderMode mode, string outputPath, int width = 0, int height = 0, int emfDpi = 600, CancellationToken cancellation = default)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("An absolute output path is required.", nameof(outputPath));
        if (mode == PrintRenderMode.Raster && (width < 1 || height < 1)) throw new ArgumentOutOfRangeException(nameof(width));
        if (mode == PrintRenderMode.Emf && emfDpi is < 72 or > 1200) throw new ArgumentOutOfRangeException(nameof(emfDpi));
        return RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("RenderPrintPage", new { Page = page, Mode = mode.ToString(), Width = width, Height = height, Dpi = emfDpi, OutputPath = outputPath }, cancellation)).Data;
        }, cancellation);
    }
}
