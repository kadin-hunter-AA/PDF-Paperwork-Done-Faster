using System.IO;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>One rendered page on disk (EMF or top-down BGRA). Disposing deletes the file, so only one page is alive at a time.</summary>
internal class PrintPagePayload(bool isEmf, string path, int pixelWidth, int pixelHeight, string? note = null) : IDisposable
{
    internal bool IsEmf { get; } = isEmf;
    internal string Path { get; } = path;
    internal int PixelWidth { get; } = pixelWidth;
    internal int PixelHeight { get; } = pixelHeight;
    internal string? Note { get; } = note;
    public virtual void Dispose() { try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

internal readonly record struct PrintDeviceInfo(double AreaWidthPoints, double AreaHeightPoints, bool TicketLandscape, int SoftwareCopies);

internal interface IPrintSink
{
    PrintDeviceInfo Device { get; }
    Task BeginJobAsync(string jobName, CancellationToken cancellation);
    Task BeginSheetAsync(SheetPlan plan, CancellationToken cancellation);
    Task DrawPageAsync(PrintPlacement placement, PrintPagePayload payload, CancellationToken cancellation);
    Task EndSheetAsync(CancellationToken cancellation);
    /// <summary>completed=false aborts the spooler job.</summary>
    Task EndJobAsync(bool completed);
}

internal interface IPrintPageSource
{
    Task<PrintPagePayload> RenderAsync(int pageIndex, PrintPageSize size, double scale, bool preferVector, CancellationToken cancellation);
}

internal sealed record PrintProgress(int Page, int PageTotal, int Copy, int CopyTotal)
{
    public override string ToString() => CopyTotal > 1 ? $"Printing page {Page} of {PageTotal} (copy {Copy} of {CopyTotal})…" : $"Printing page {Page} of {PageTotal}…";
}

internal sealed record PrintJobSummary(int Sheets, int Pages, int VectorPages, int ImagePages, IReadOnlyList<string> Notes);

internal sealed class SessionPrintPageSource(DocumentSession session) : IPrintPageSource, IDisposable
{
    internal const int EmfDpi = 600;
    readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "HunterPdfPrint-" + Guid.NewGuid().ToString("N"))).FullName;
    int counter;

    public async Task<PrintPagePayload> RenderAsync(int pageIndex, PrintPageSize size, double scale, bool preferVector, CancellationToken cancellation)
    {
        string? fallback = null;
        if (preferVector)
        {
            var emfPath = Path.Combine(directory, $"p{Interlocked.Increment(ref counter)}.emf");
            try
            {
                var info = await session.RenderPrintPageAsync(pageIndex, PrintRenderMode.Emf, emfPath, emfDpi: EmfDpi, cancellation: cancellation);
                return new PrintPagePayload(true, emfPath, 0, 0);
            }
            catch (InvalidOperationException error)
            {
                try { File.Delete(emfPath); } catch (IOException) { }
                fallback = $"Page {pageIndex + 1} was printed as an image because vector output was unavailable: {error.Message}";
            }
        }
        var raster = PrintLayout.GetRasterSize(size.Width, size.Height, scale);
        var path = Path.Combine(directory, $"p{Interlocked.Increment(ref counter)}.bgra");
        var data = await session.RenderPrintPageAsync(pageIndex, PrintRenderMode.Raster, path, raster.Width, raster.Height, cancellation: cancellation);
        var note = raster.Reduced ? $"Page {pageIndex + 1} is very large and was printed at about {raster.Dpi:0} DPI." : null;
        return new PrintPagePayload(false, path, data.GetProperty("Width").GetInt32(), data.GetProperty("Height").GetInt32(), fallback ?? note);
    }

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

internal static class PrintPipeline
{
    internal static int[] ResolvePages(int pageCount, int currentPage, PageRangeSelection selection, PageRange range)
    {
        if (pageCount <= 0) throw new InvalidOperationException("This PDF has no pages to print.");
        int first, last;
        switch (selection)
        {
            case PageRangeSelection.AllPages: first = 1; last = pageCount; break;
            case PageRangeSelection.CurrentPage: first = last = currentPage + 1; break;
            case PageRangeSelection.UserPages: first = range.PageFrom; last = range.PageTo; break;
            default: throw new InvalidOperationException("Selected-page printing is unavailable. Choose all pages, the current page, or a page range.");
        }
        if (first < 1 || last < first || last > pageCount) throw new InvalidOperationException($"Enter a page range from 1 to {pageCount}.");
        return Enumerable.Range(first - 1, last - first + 1).ToArray();
    }

    /// <summary>Streams the job one page at a time (render, draw, release). Cancellation or failure aborts the sink's job; the source document is never touched.</summary>
    internal static async Task<PrintJobSummary> RunJobAsync(IPrintSink sink, IPrintPageSource source, int[] pages, IReadOnlyList<PrintPageSize> sizes,
        PrintOptions options, string jobName, Action<PrintProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(sink); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(pages); ArgumentNullException.ThrowIfNull(sizes);
        PrintLayout.Validate(options);
        if (pages.Length == 0 || pages.Any(p => p < 0 || p >= sizes.Count)) throw new InvalidOperationException("Choose at least one valid page to print.");
        var device = sink.Device;
        var passes = Math.Max(1, device.SoftwareCopies);
        var notes = new List<string>();
        int sheets = 0, vector = 0, image = 0;
        var started = false;
        var completed = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await sink.BeginJobAsync(jobName, cancellation);
            started = true;
            for (var pass = 1; pass <= passes; pass++)
            {
                var done = 0;
                foreach (var group in PrintLayout.Sheets(pages, options.PagesPerSheet))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var plan = PrintLayout.PlanSheet(group, sizes, options, device.AreaWidthPoints, device.AreaHeightPoints, device.TicketLandscape);
                    await sink.BeginSheetAsync(plan, cancellation);
                    foreach (var placement in plan.Placements)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress?.Invoke(new PrintProgress(++done, pages.Length, pass, passes));
                        using var payload = await source.RenderAsync(placement.PageIndex, sizes[placement.PageIndex], placement.Scale, !options.AsImage, cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        await sink.DrawPageAsync(placement, payload, cancellation);
                        if (pass == 1)
                        {
                            if (payload.IsEmf) vector++; else image++;
                            if (payload.Note is not null && notes.Count < 20) notes.Add(payload.Note);
                        }
                    }
                    await sink.EndSheetAsync(cancellation);
                    if (pass == 1) sheets++;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            if (started)
            {
                try { await sink.EndJobAsync(completed); } catch (Exception) when (!completed) { }
            }
        }
        return new PrintJobSummary(sheets, pages.Length, vector, image, notes);
    }
}
