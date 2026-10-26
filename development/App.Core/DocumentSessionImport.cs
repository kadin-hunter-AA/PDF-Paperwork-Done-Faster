using System.Globalization;
using System.Text.Json;

namespace HunterPdf.Core;

/// <summary>Choices for a best-effort page import. Defaults keep as much visible content as possible.</summary>
public sealed record ImportOptions(bool KeepAnnotationsAsContent = true, bool KeepFormValues = true,
    bool AllowSigned = false, string? SourcePassword = null);

/// <summary>What an import did. <see cref="Dropped"/> counts structures that were not carried over or were flattened into page content.</summary>
public sealed record ImportReport(string Tier, int PagesInserted, IReadOnlyDictionary<string, int> Dropped)
{
    public const string Strict = "Strict", BestEffort = "BestEffort";

    static readonly (string Key, string Singular, string Plural, bool Lost)[] Labels =
    [
        ("Bookmarks", "bookmark", "bookmarks", true), ("Links", "link", "links", true),
        ("Annotations", "annotation", "annotations", true), ("AnnotationsFlattened", "annotation kept as page content", "annotations kept as page content", false),
        ("FormFields", "form field", "form fields", true), ("FormFieldsFlattened", "form field value kept as page content", "form field values kept as page content", false),
        ("SignatureFields", "signature field", "signature fields", true), ("Attachments", "attached file", "attached files", true),
        ("PageLabels", "page label range", "page label ranges", true), ("NamedDestinations", "named destination", "named destinations", true),
        ("JavaScriptAndActions", "script or automatic action", "scripts or automatic actions", true),
        ("StructureTree", "accessibility tag tree", "accessibility tag trees", true),
        ("MarkedContentSections", "tagged content section", "tagged content sections", true),
    ];

    /// <summary>Plain-language notice for the UI; empty when nothing was dropped.</summary>
    public string Describe()
    {
        var lost = new List<string>(); var kept = new List<string>();
        foreach (var (key, singular, plural, isLost) in Labels)
            if (Dropped.TryGetValue(key, out var n) && n > 0)
                (isLost ? lost : kept).Add($"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? singular : plural)}");
        var text = $"Inserted {PagesInserted:N0} page{(PagesInserted == 1 ? "" : "s")}.";
        if (Tier == Strict) return text;
        text += " Only the page content was imported.";
        if (kept.Count > 0) text += " Kept as page content: " + string.Join(", ", kept) + ".";
        if (lost.Count > 0) text += " Not carried over: " + string.Join(", ", lost) + ".";
        return text;
    }
}

public sealed partial class DocumentSession
{
    /// <summary>
    /// Imports pages of another PDF after sanitizing a private copy of it with the companion engine: page content is kept,
    /// and outlines, annotations, forms, tags, labels, destinations, attachments and scripts are removed (annotations and form
    /// values can be flattened into content). The sanitized copy then goes through the strict native
    /// <see cref="InsertPagesAsync"/>, so the same validation applies. One undoable step; the original source file is never changed
    /// and temporary copies are deleted. Source page indices are 0-based and distinct; they are inserted in the order given.
    /// </summary>
    public async Task<ImportReport> InsertPagesBestEffortAsync(AdvancedEngineLocation location, string sourcePath, int[] pages,
        int destination, ImportOptions? options = null, CancellationToken cancellation = default)
    {
        RequireLocation(location);
        options ??= new ImportOptions();
        EnsureSignedCopyConsent();
        ValidateOperationSelectionShape(pages);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new InvalidOperationException("Choose an accessible source PDF.");
        var fullSource = Path.GetFullPath(sourcePath);
        if (IsSessionPath(fullSource)) throw new InvalidOperationException("Choose a different source PDF. Use Duplicate for pages in this document.");
        if (destination < 0 || destination > PageCount)
            throw new InvalidOperationException("Choose an insertion position from the start through the end of the page list.");
        if ((long)PageCount + pages.Length > 50_000) throw new InvalidOperationException("Page organization supports up to 50,000 pages.");
        var length = new FileInfo(fullSource).Length;
        var sourceHash = RecoveryStore.Fingerprint(fullSource);
        var copy = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
        var sanitized = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            RecoveryStore.CopyDurably(fullSource, copy);
            if (RecoveryStore.Fingerprint(copy) != sourceHash) throw new IOException("The source PDF changed while it was being copied.");
            var request = CompanionRequest("PrepareImport", copy, sanitized, new Dictionary<string, object?>
            {
                ["Pages"] = pages, ["KeepAnnotationsAsContent"] = options.KeepAnnotationsAsContent, ["KeepFormValues"] = options.KeepFormValues,
                ["AllowSigned"] = options.AllowSigned,
            });
            if (options.SourcePassword is not null) request["Password"] = options.SourcePassword;
            var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, AdvancedEngineClient.ScaledTimeout(length, pages.Length))
                .RunAsync(request, cancellation);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True || !File.Exists(sanitized) ||
                !result.TryGetProperty("PageCount", out var count) || count.GetInt32() != pages.Length || !result.TryGetProperty("Dropped", out var dropped) ||
                dropped.ValueKind != JsonValueKind.Object)
                throw new IOException("The advanced PDF engine did not prepare the pages.");
            var counts = dropped.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetInt32());
            await InsertPagesAsync(sanitized, Enumerable.Range(0, pages.Length).ToArray(), destination, cancellation);
            if (RecoveryStore.Fingerprint(fullSource) != sourceHash) throw new IOException("The source PDF changed during the import.");
            return new ImportReport(ImportReport.BestEffort, pages.Length, counts);
        }
        finally
        {
            TryRemoveTemporary(copy);
            TryRemoveTemporary(sanitized);
        }
    }

    /// <summary>
    /// Tries the strict native insert first and falls back to <see cref="InsertPagesBestEffortAsync"/> only when the strict path
    /// refuses because of the source's structure (bookmarks, annotations, forms, tags, attachments, destinations, marked content,
    /// encryption or signatures). Other failures, such as a bad destination, are rethrown. Tier is "Strict" when nothing was dropped.
    /// A UI that wants an explicit consent step should call <see cref="InsertPagesBestEffortAsync"/> directly after a refusal.
    /// </summary>
    public async Task<ImportReport> InsertPagesAutoAsync(AdvancedEngineLocation location, string sourcePath, int[] pages,
        int destination, ImportOptions? options = null, CancellationToken cancellation = default)
    {
        try
        {
            await InsertPagesAsync(sourcePath, pages, destination, cancellation);
            return new ImportReport(ImportReport.Strict, pages.Length, new Dictionary<string, int>());
        }
        catch (InvalidOperationException e) when (IsSourceStructureRefusal(e))
        {
            return await InsertPagesBestEffortAsync(location, sourcePath, pages, destination, options, cancellation);
        }
    }

    /// <summary>True when a strict import refusal is about the source PDF's structure (so best-effort may help).</summary>
    public static bool IsSourceStructureRefusal(InvalidOperationException e) =>
        e is not SignedDocumentException && e.Message is { } m &&
        (m.Contains("cannot be inserted", StringComparison.Ordinal) || m.Contains("imported safely yet", StringComparison.Ordinal) ||
         m.Contains("duplicated or imported", StringComparison.Ordinal) || m.Contains("source bookmark", StringComparison.Ordinal));
}
