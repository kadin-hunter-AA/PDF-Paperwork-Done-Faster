using System.Text.Json;

namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    const int MaximumPageMoveIndices = 50_000;
    const int MaximumPageOperationIndices = 5_000;
    const double MaximumBlankPageDimension = 14_400;

    public Task MovePagesAsync(int[] pageIndices, int destinationIndex, CancellationToken cancellation = default)
    {
        if (SignatureCount > 0 && !SignedCopyAuthorized) throw new SignedDocumentException();
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Length > MaximumPageMoveIndices)
            throw new InvalidOperationException($"A page move supports at most {MaximumPageMoveIndices:N0} selected pages.");
        return EditAsync("MovePages", new { Pages = pageIndices, Destination = destinationIndex }, cancellation);
    }

    public Task InsertBlankPageAsync(int index, double width, double height, CancellationToken cancellation = default)
    {
        if (SignatureCount > 0 && !SignedCopyAuthorized) throw new SignedDocumentException();
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > MaximumBlankPageDimension || height > MaximumBlankPageDimension)
            throw new InvalidOperationException("Blank page dimensions must be between 1 and 14,400 PDF points.");
        return EditAsync("InsertBlankPage", new { Page = index, Width = width, Height = height }, cancellation, pageCountDelta: 1);
    }

    /// <summary>Rotates the selected pages clockwise by 1–3 quarter turns as one undoable edit.</summary>
    public Task RotatePagesAsync(int[] pageIndices, int quarterTurns, CancellationToken cancellation = default)
    {
        EnsureSignedCopyConsent();
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Length < 1 || pageIndices.Length > MaximumPageMoveIndices || pageIndices.Any(page => page < 0 || page >= PageCount) ||
            pageIndices.Distinct().Count() != pageIndices.Length)
            throw new InvalidOperationException("Select one or more distinct pages within the document.");
        if (quarterTurns is < 1 or > 3) throw new InvalidOperationException("Choose a rotation of 90, 180 or 270 degrees.");
        return EditAsync("RotatePages", new { Pages = pageIndices, QuarterTurns = quarterTurns }, cancellation);
    }

    public Task DeletePagesAsync(int[] pageIndices, CancellationToken cancellation = default)
    {
        EnsureSignedCopyConsent();
        ValidateOperationSelection(pageIndices);
        if (pageIndices.Length >= PageCount) throw new InvalidOperationException("At least one page must remain in the document.");
        return EditAsync("DeletePages", new { Pages = pageIndices }, cancellation, pageCountDelta: -pageIndices.Length);
    }

    public Task DuplicatePagesAsync(int[] pageIndices, int destinationIndex, CancellationToken cancellation = default)
    {
        EnsureSignedCopyConsent();
        ValidateOperationSelection(pageIndices);
        if (destinationIndex < 0 || destinationIndex > PageCount)
            throw new InvalidOperationException("Choose a duplicate insertion position from the start through the end of the page list.");
        if ((long)PageCount + pageIndices.Length > 50_000) throw new InvalidOperationException("Page organization supports up to 50,000 pages.");
        return EditAsync("DuplicatePages", new { Pages = pageIndices, Destination = destinationIndex }, cancellation,
            pageCountDelta: pageIndices.Length);
    }

    public Task InsertPagesAsync(string sourcePath, int[] pageIndices, int destinationIndex, CancellationToken cancellation = default)
    {
        EnsureSignedCopyConsent();
        ValidateOperationSelectionShape(pageIndices);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException("Choose an accessible source PDF.");
        var fullSource = Path.GetFullPath(sourcePath);
        if (IsSessionPath(fullSource)) throw new InvalidOperationException("Choose a different source PDF. Use Duplicate for pages in this document.");
        if (destinationIndex < 0 || destinationIndex > PageCount)
            throw new InvalidOperationException("Choose an insertion position from the start through the end of the page list.");
        if ((long)PageCount + pageIndices.Length > 50_000) throw new InvalidOperationException("Page organization supports up to 50,000 pages.");
        return EditAsync("InsertPages", new { SourcePath = fullSource, Pages = pageIndices, Destination = destinationIndex }, cancellation,
            pageCountDelta: pageIndices.Length);
    }

    public Task<JsonElement> ExtractPagesAsync(int[] pageIndices, string outputPath, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            EnsureSignedCopyConsent();
            ValidateOperationSelection(pageIndices);
            var output = ValidateExportFilePath(outputPath);
            return (await RequestAsync("ExtractPages", new { Pages = pageIndices, OutputPath = output }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> SplitPagesAsync(string outputDirectory, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            EnsureSignedCopyConsent();
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("Choose a new output folder for the split PDFs.");
            var output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Choose a new, unused output folder.");
            var parent = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) throw new InvalidOperationException("The output folder's parent must already exist.");
            return (await RequestAsync("SplitPages", new { OutputDirectory = output }, cancellation)).Data;
        }, cancellation);

    void EnsureSignedCopyConsent()
    {
        if (SignatureCount > 0 && !SignedCopyAuthorized) throw new SignedDocumentException();
    }

    void ValidateOperationSelection(int[] pageIndices)
    {
        ValidateOperationSelectionShape(pageIndices);
        if (pageIndices.Any(page => page >= PageCount))
            throw new InvalidOperationException("A selected page is outside the current document.");
    }

    static void ValidateOperationSelectionShape(int[] pageIndices)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Length is < 1 or > MaximumPageOperationIndices ||
            pageIndices.Any(page => page < 0) || pageIndices.Distinct().Count() != pageIndices.Length)
            throw new InvalidOperationException($"Select between 1 and {MaximumPageOperationIndices:N0} distinct pages within the document.");
    }

    string ValidateExportFilePath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Choose a PDF output path.");
        var output = Path.GetFullPath(outputPath);
        if (IsSessionPath(output)) throw new InvalidOperationException("Choose a new output path that does not overwrite the source or a recovery checkpoint.");
        if (File.Exists(output) || Directory.Exists(output)) throw new InvalidOperationException("Choose a new output filename. Existing files are never overwritten.");
        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) throw new InvalidOperationException("The output folder must already exist.");
        return output;
    }

    bool IsSessionPath(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(OriginalPath), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(LastSavePath), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(CurrentSnapshot), StringComparison.OrdinalIgnoreCase);
}
