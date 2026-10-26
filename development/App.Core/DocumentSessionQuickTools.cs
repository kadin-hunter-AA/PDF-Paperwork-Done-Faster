using System.Text.Json;

namespace HunterPdf.Core;

public enum PageMarkPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

public sealed record QuickToolResult(long InputBytes, long OutputBytes);
public sealed record WatermarkOptions(string Text, double FontSize, int Red, int Green, int Blue, double Opacity, int Rotation, IReadOnlyList<int>? Pages = null);
public sealed record HeaderFooterOptions(string Template, string FileName, string Date, PageMarkPosition Position, double FontSize = 10,
    double Margin = 36, int StartNumber = 1, IReadOnlyList<int>? Pages = null);
public sealed record BatesOptions(string Prefix, string Suffix, long Start, int Digits, PageMarkPosition Position, double FontSize = 10,
    double Margin = 36, IReadOnlyList<int>? Pages = null);
public sealed record BatesResult(long FirstNumber, long LastNumber, string FirstLabel, string LastLabel, int Pages);
/// <summary>Absolute rectangle in PDF default user space (unrotated, origin bottom-left).</summary>
public sealed record CropOptions(double Left, double Bottom, double Right, double Top, IReadOnlyList<int>? Pages = null);
public sealed record FlattenResult(int AnnotationsFlattened, int FormFieldsFlattened, long InputBytes, long OutputBytes);
public sealed record PdfProperties(string Title, string Author, string Subject, string Keywords, string Creator, string Producer,
    string CreationDate, string ModDate, string PdfVersion, int PageCount, bool Encrypted, bool HasXmp);
public sealed record BookmarkEntry(string Title, int Page, int Level);
public sealed record BookmarkList(IReadOnlyList<BookmarkEntry> Bookmarks, int Count, bool Truncated);
public sealed record ImageExtractResult(int Count, IReadOnlyList<string> Names, bool Truncated);
public sealed record PdfLinkInfo(int Index, double Left, double Bottom, double Right, double Top, string Kind, string Uri, int TargetPage);
public sealed record PdfLinkList(IReadOnlyList<PdfLinkInfo> Links, int Count, bool Truncated);

public sealed partial class DocumentSession
{
    TimeSpan QuickTimeout()
    {
        long bytes = 0;
        try { bytes = new FileInfo(CurrentSnapshot).Length; } catch (IOException) { }
        return TimeSpan.FromMinutes(Math.Min(15, 3 + bytes / (25.0 * 1024 * 1024)));
    }

    static IReadOnlyDictionary<string, object?> Args(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    static void Range(double value, double low, double high, string name)
    {
        if (double.IsNaN(value) || value < low || value > high) throw new ArgumentOutOfRangeException(name);
    }

    static int[]? PageList(IReadOnlyList<int>? pages, int count, string name)
    {
        if (pages is null) return null;
        if (pages.Count == 0 || pages.Count > count || pages.Distinct().Count() != pages.Count || pages.Any(p => p < 0 || p >= count))
            throw new ArgumentOutOfRangeException(name);
        return pages.ToArray();
    }

    static void Text(string? value, int low, int high, string name, bool ascii = false)
    {
        if (value is null || value.Length < low || value.Length > high || value.Any(c => c < 0x20 || (c >= 0x7F && c <= 0x9F) || (ascii && c > 0x7E)))
            throw new ArgumentException("Invalid text value.", name);
    }

    static void Latin1(string value, string name)
    {
        if (value.Any(c => c > 0xFF)) throw new ArgumentException("Only Western European (Latin-1) characters are supported.", name);
    }

    void RequireUnsignedOrAuthorized()
    {
        if (SignatureCount > 0 && !SignedCopyAuthorized) throw new SignedDocumentException();
    }

    static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    // ---- exports (new file, session unchanged)

    public async Task<QuickToolResult> CompressCopyAsync(AdvancedEngineLocation location, string destination, int? imageDpi, CancellationToken ct = default)
    {
        if (imageDpi is < 72 or > 300) throw new ArgumentOutOfRangeException(nameof(imageDpi));
        RequireUnsignedOrAuthorized();
        var r = await ExportCompanionAsync(location, "Compress", Args(("ImageDpi", imageDpi)), destination, null, false, ct, QuickTimeout());
        return new QuickToolResult(r.GetProperty("InputBytes").GetInt64(), r.GetProperty("OutputBytes").GetInt64());
    }

    public Task ProtectCopyAsync(AdvancedEngineLocation location, string destination, string userPassword, string ownerPassword,
        bool allowPrint, bool allowCopy, bool allowEdit, CancellationToken ct = default)
    {
        Text(userPassword, 1, 128, nameof(userPassword));
        Text(ownerPassword, 1, 128, nameof(ownerPassword));
        RequireUnsignedOrAuthorized();
        return ExportCompanionAsync(location, "Protect", Args(("UserPassword", userPassword), ("OwnerPassword", ownerPassword),
            ("AllowPrint", allowPrint), ("AllowCopy", allowCopy), ("AllowEdit", allowEdit)), destination, userPassword, false, ct, QuickTimeout());
    }

    /// <summary>Writes an unencrypted copy. The session must have been opened with the document password.</summary>
    public Task UnprotectCopyAsync(AdvancedEngineLocation location, string destination, CancellationToken ct = default)
    {
        RequireUnsignedOrAuthorized();
        return ExportCompanionAsync(location, "Unprotect", null, destination, null, false, ct, QuickTimeout());
    }

    public async Task<FlattenResult> FlattenCopyAsync(AdvancedEngineLocation location, string destination, bool annotations, bool forms, CancellationToken ct = default)
    {
        if (!annotations && !forms) throw new ArgumentException("Choose annotations, forms or both.");
        RequireUnsignedOrAuthorized();
        var r = await ExportCompanionAsync(location, "Flatten", Args(("Annotations", annotations), ("Forms", forms)), destination, null, false, ct, QuickTimeout());
        long size = 0;
        try { size = new FileInfo(destination).Length; } catch (IOException) { }
        return new FlattenResult(Int(r, "AnnotationsFlattened"), Int(r, "FormFieldsFlattened"), 0, size);
    }

    /// <summary>Writes every embedded image of the pages into a NEW folder (parent must exist).</summary>
    public async Task<ImageExtractResult> ExtractImagesAsync(AdvancedEngineLocation location, string outputDirectory, IReadOnlyList<int>? pages = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var full = Path.GetFullPath(outputDirectory);
        var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null || !Directory.Exists(parent) || Directory.Exists(full) || File.Exists(full))
            throw new IOException("Choose a new folder that does not exist yet.");
        RecoveryStore.RejectReparse(parent);
        if (full.StartsWith(store.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a folder outside recovery storage.");
        var r = await QueryCompanionAsync(location, "ExtractImages", Args(("OutputDirectory", AdvancedEngineClient.FilePath(full)),
            ("Pages", PageList(pages, PageCount, nameof(pages)))), ct, QuickTimeout());
        return new ImageExtractResult(Int(r, "Count"), r.GetProperty("Names").EnumerateArray().Select(n => n.GetString()!).ToArray(), r.GetProperty("Truncated").GetBoolean());
    }

    // ---- edits (one undoable checkpoint)

    public Task AddWatermarkAsync(AdvancedEngineLocation location, WatermarkOptions o, long expectedRevision, CancellationToken ct = default)
    {
        Text(o.Text, 1, 200, nameof(o.Text)); Latin1(o.Text, nameof(o.Text));
        Range(o.FontSize, 8, 200, nameof(o.FontSize)); Range(o.Opacity, 0.05, 1, nameof(o.Opacity));
        foreach (var (v, n) in new[] { (o.Red, "Red"), (o.Green, "Green"), (o.Blue, "Blue") }) Range(v, 0, 255, n);
        if (o.Rotation is not (0 or 45 or 90 or -45)) throw new ArgumentOutOfRangeException(nameof(o.Rotation));
        var pages = PageList(o.Pages, PageCount, nameof(o.Pages));
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "Watermark", Args(("Text", o.Text), ("FontSize", o.FontSize),
            ("Red", o.Red), ("Green", o.Green), ("Blue", o.Blue), ("Opacity", o.Opacity), ("Rotation", o.Rotation), ("Pages", pages)),
            expectedRevision, ct, QuickTimeout());
    }

    public Task AddHeaderFooterAsync(AdvancedEngineLocation location, HeaderFooterOptions o, long expectedRevision, CancellationToken ct = default)
    {
        Text(o.Template, 1, 200, nameof(o.Template)); Text(o.FileName, 0, 260, nameof(o.FileName)); Text(o.Date, 0, 260, nameof(o.Date));
        Latin1(o.Template, nameof(o.Template)); Latin1(o.FileName, nameof(o.FileName)); Latin1(o.Date, nameof(o.Date));
        Range(o.FontSize, 6, 72, nameof(o.FontSize)); Range(o.Margin, 9, 144, nameof(o.Margin));
        if (o.StartNumber < 1) throw new ArgumentOutOfRangeException(nameof(o.StartNumber));
        if (!Enum.IsDefined(o.Position)) throw new ArgumentOutOfRangeException(nameof(o.Position));
        var pages = PageList(o.Pages, PageCount, nameof(o.Pages));
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "HeaderFooter", Args(("Template", o.Template), ("FileName", o.FileName),
            ("Date", o.Date), ("Position", o.Position.ToString()), ("FontSize", o.FontSize), ("Margin", o.Margin), ("StartNumber", o.StartNumber), ("Pages", pages)),
            expectedRevision, ct, QuickTimeout());
    }

    public async Task<BatesResult> AddBatesAsync(AdvancedEngineLocation location, BatesOptions o, long expectedRevision, CancellationToken ct = default)
    {
        Text(o.Prefix, 0, 40, nameof(o.Prefix), true); Text(o.Suffix, 0, 40, nameof(o.Suffix), true);
        if (o.Digits is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(o.Digits));
        Range(o.FontSize, 6, 72, nameof(o.FontSize)); Range(o.Margin, 9, 144, nameof(o.Margin));
        if (!Enum.IsDefined(o.Position)) throw new ArgumentOutOfRangeException(nameof(o.Position));
        var pages = PageList(o.Pages, PageCount, nameof(o.Pages));
        var count = pages?.Length ?? PageCount;
        var limit = (long)Math.Pow(10, o.Digits);
        if (o.Start < 0 || o.Start + count > limit) throw new ArgumentOutOfRangeException(nameof(o.Start), "The numbers need more digits.");
        await ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "Bates", Args(("Prefix", o.Prefix), ("Suffix", o.Suffix), ("Start", o.Start),
            ("Digits", o.Digits), ("Position", o.Position.ToString()), ("FontSize", o.FontSize), ("Margin", o.Margin), ("Pages", pages)),
            expectedRevision, ct, QuickTimeout());
        var last = o.Start + count - 1;
        string Label(long n) => o.Prefix + n.ToString().PadLeft(o.Digits, '0') + o.Suffix;
        return new BatesResult(o.Start, last, Label(o.Start), Label(last), count);
    }

    public Task CropPagesAsync(AdvancedEngineLocation location, CropOptions o, long expectedRevision, CancellationToken ct = default)
    {
        foreach (var (v, n) in new[] { (o.Left, "Left"), (o.Bottom, "Bottom"), (o.Right, "Right"), (o.Top, "Top") }) Range(v, -1e6, 1e6, n);
        if (o.Right - o.Left < 36 || o.Top - o.Bottom < 36) throw new ArgumentException("The cropped page must be at least 36 by 36 points.");
        var pages = PageList(o.Pages, PageCount, nameof(o.Pages));
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "Crop", Args(("Left", o.Left), ("Bottom", o.Bottom), ("Right", o.Right),
            ("Top", o.Top), ("Pages", pages)), expectedRevision, ct, QuickTimeout());
    }

    /// <summary>Null leaves a field unchanged; empty clears it. With stripMetadata everything is removed and the values are ignored.</summary>
    public Task SetPropertiesAsync(AdvancedEngineLocation location, string? title, string? author, string? subject, string? keywords,
        bool stripMetadata, long expectedRevision, CancellationToken ct = default)
    {
        if (!stripMetadata) foreach (var (v, n) in new[] { (title, "title"), (author, "author"), (subject, "subject"), (keywords, "keywords") })
                if (v is not null) Text(v, 0, 1000, n);
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "SetProperties", Args(("Title", stripMetadata ? null : title),
            ("Author", stripMetadata ? null : author), ("Subject", stripMetadata ? null : subject), ("Keywords", stripMetadata ? null : keywords),
            ("StripMetadata", stripMetadata)), expectedRevision, ct, QuickTimeout());
    }

    public Task SetBookmarksAsync(AdvancedEngineLocation location, IReadOnlyList<BookmarkEntry> bookmarks, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        if (bookmarks.Count > 5000) throw new ArgumentException("At most 5000 bookmarks.", nameof(bookmarks));
        var previous = 0;
        foreach (var b in bookmarks)
        {
            Text(b.Title, 1, 500, "Title");
            if (b.Level < 1 || b.Level > previous + 1) throw new ArgumentException("Bookmark levels must start at 1 and rise by at most one.", nameof(bookmarks));
            if (b.Page < -1 || b.Page >= PageCount) throw new ArgumentOutOfRangeException(nameof(bookmarks));
            previous = b.Level;
        }
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "SetBookmarks", Args(("Bookmarks",
            bookmarks.Select(b => new { b.Title, b.Page, b.Level }).ToArray())), expectedRevision, ct, QuickTimeout());
    }

    static void LinkRect(double l, double b, double r, double t)
    {
        foreach (var v in new[] { l, b, r, t }) Range(v, -1e6, 1e6, "rectangle");
        if (r - l < 1 || t - b < 1) throw new ArgumentException("The link area must be at least 1 by 1 point.");
    }

    /// <summary>Adds a link over the rectangle (PDF user space). Give exactly one of uri (http, https, mailto) or targetPage.</summary>
    public Task AddLinkAsync(AdvancedEngineLocation location, int page, double left, double bottom, double right, double top,
        string? uri, int? targetPage, long expectedRevision, CancellationToken ct = default)
    {
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        LinkRect(left, bottom, right, top);
        if ((uri is null) == (targetPage is null)) throw new ArgumentException("Provide either a web address or a target page.");
        if (uri is not null)
        {
            if (uri.Length is < 1 or > 2000 || uri.Any(c => c < 0x21 || c > 0x7E)) throw new ArgumentException("Invalid link address.", nameof(uri));
            var ok = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            if (!ok) throw new ArgumentException("Only http, https and mailto links are allowed.", nameof(uri));
        }
        if (targetPage is { } t && (t < 0 || t >= PageCount)) throw new ArgumentOutOfRangeException(nameof(targetPage));
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "AddLink", Args(("Page", page), ("Left", left), ("Bottom", bottom),
            ("Right", right), ("Top", top), ("Uri", uri), ("TargetPage", targetPage)), expectedRevision, ct, QuickTimeout());
    }

    /// <summary>Removes link <paramref name="index"/> of the page; refused as stale if its rectangle no longer matches.</summary>
    public Task RemoveLinkAsync(AdvancedEngineLocation location, PdfLinkInfo link, int page, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (link.Index < 0) throw new ArgumentOutOfRangeException(nameof(link));
        LinkRect(link.Left, link.Bottom, link.Right, link.Top);
        return ApplyCompanionEditAsync(location, ExternalEditKind.QuickTool, "RemoveLink", Args(("Page", page), ("Index", link.Index),
            ("Left", link.Left), ("Bottom", link.Bottom), ("Right", link.Right), ("Top", link.Top)), expectedRevision, ct, QuickTimeout());
    }

    // ---- queries

    public async Task<PdfProperties> GetPropertiesAsync(AdvancedEngineLocation location, CancellationToken ct = default)
    {
        var r = await QueryCompanionAsync(location, "GetProperties", null, ct, QuickTimeout());
        string S(string n) => r.GetProperty(n).GetString() ?? "";
        return new PdfProperties(S("Title"), S("Author"), S("Subject"), S("Keywords"), S("Creator"), S("Producer"), S("CreationDate"), S("ModDate"),
            S("PdfVersion"), Int(r, "PageCount"), r.GetProperty("Encrypted").GetBoolean(), r.GetProperty("HasXmp").GetBoolean());
    }

    public async Task<BookmarkList> GetBookmarksListAsync(AdvancedEngineLocation location, CancellationToken ct = default)
    {
        var r = await QueryCompanionAsync(location, "GetBookmarks", null, ct, QuickTimeout());
        return new BookmarkList(r.GetProperty("Bookmarks").EnumerateArray().Select(b =>
            new BookmarkEntry(b.GetProperty("Title").GetString() ?? "", Int(b, "Page"), Int(b, "Level"))).ToArray(),
            Int(r, "Count"), r.GetProperty("Truncated").GetBoolean());
    }

    public async Task<PdfLinkList> ListLinksAsync(AdvancedEngineLocation location, int page, CancellationToken ct = default)
    {
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        var r = await QueryCompanionAsync(location, "ListLinks", Args(("Page", page)), ct, QuickTimeout());
        return new PdfLinkList(r.GetProperty("Links").EnumerateArray().Select(l => new PdfLinkInfo(Int(l, "Index"), l.GetProperty("Left").GetDouble(),
            l.GetProperty("Bottom").GetDouble(), l.GetProperty("Right").GetDouble(), l.GetProperty("Top").GetDouble(),
            l.GetProperty("Kind").GetString() ?? "", l.GetProperty("Uri").GetString() ?? "", Int(l, "TargetPage"))).ToArray(),
            Int(r, "Count"), r.GetProperty("Truncated").GetBoolean());
    }
}
