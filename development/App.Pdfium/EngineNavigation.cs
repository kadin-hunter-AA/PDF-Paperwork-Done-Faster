using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    const int MaximumNavigationEntries = 100_000;
    const int MaximumBookmarksPerParent = 20_000;
    const int MaximumBookmarkDepth = 64;
    const int MaximumNamedDestinations = 50_000;
    readonly HashSet<Guid> navigationValidatedDocuments = new();

    public object ValidateCandidate(Guid id, long revision, string path, string? password)
    {
        var current = Get(id, revision);
        using var candidate = Pdf.Open(path, password);
        if (candidate.PageCount < 1)
            throw new InvalidOperationException("The saved candidate has no pages.");
        if (candidate.PageCount != current.PageCount)
            throw new InvalidOperationException("The saved candidate has an unexpected page count.");
        if (navigationValidatedDocuments.Contains(id))
        {
            var before = CaptureNavigationTargets(current);
            var identity = Enumerable.Range(0, current.PageCount).ToDictionary(page => page, page => page);
            EnsureNavigationPreserved(candidate, before, identity);
        }
        for (var page = 0; page < candidate.PageCount; page++)
        {
            using var pageHandle = candidate.Page(page);
        }
        var signatures = SignatureCount(candidate.Handle);
        if (signatures < 0) throw new InvalidOperationException("The candidate signature information could not be read.");
        return new { PageCount = candidate.PageCount, SignatureCount = signatures,
            SecurityHandlerRevision = SecurityHandlerRevision(candidate.Handle) };
    }

    static Dictionary<string, int> CaptureNavigationTargets(Pdf pdf)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var traversedEntries = 0;
        void CountTraversal()
        {
            if (++traversedEntries > MaximumNavigationEntries)
                throw new InvalidOperationException("The document has too many navigation entries for a safe page operation.");
        }
        void Add(string key, nint destination)
        {
            if (destination == 0) throw new InvalidOperationException("A local navigation destination could not be read safely.");
            var page = DestPageIndex(pdf.Handle, destination);
            if (page < 0 || page >= pdf.PageCount)
                throw new InvalidOperationException("A local navigation destination is unresolved. Page changes are disabled to protect document navigation.");
            if (result.Count >= MaximumNavigationEntries)
                throw new InvalidOperationException("The document has too many navigation targets for a safe page operation.");
            if (!result.TryAdd(key, page))
                throw new InvalidOperationException("Navigation entries could not be identified uniquely. Page changes are disabled to protect document navigation.");
        }

        nint LocalDestination(nint directDestination, nint action)
        {
            if (directDestination != 0) return directDestination;
            if (action == 0 || ActionGetType(action) != PdfActionGoTo) return 0;
            var destination = ActionGetDest(pdf.Handle, action);
            if (destination == 0)
                throw new InvalidOperationException("A local GoTo action has no readable destination. Page changes are disabled to protect document navigation.");
            return destination;
        }

        var visitedBookmarks = new HashSet<nint>();
        void WalkBookmarks(nint parent, string path, int depth)
        {
            if (depth > MaximumBookmarkDepth)
                throw new InvalidOperationException("The bookmark tree is too deep for a safe page operation.");
            var item = FirstBookmark(pdf.Handle, parent);
            var sibling = 0;
            while (item != 0)
            {
                CountTraversal();
                if (++sibling > MaximumBookmarksPerParent || !visitedBookmarks.Add(item))
                    throw new InvalidOperationException("The bookmark tree exceeds safe traversal limits.");
                var key = path.Length == 0 ? sibling.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : path + "." + sibling.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var destination = LocalDestination(BookmarkDest(pdf.Handle, item), BookmarkAction(item));
                if (destination != 0) Add("bookmark:" + key, destination);
                WalkBookmarks(item, key, depth + 1);
                item = NextBookmark(pdf.Handle, item);
            }
        }
        WalkBookmarks(0, "", 0);

        for (var pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
        {
            using var page = pdf.Page(pageIndex);
            var position = 0;
            var ordinal = 0;
            while (true)
            {
                var previousPosition = position;
                if (LinkEnumerate(page.Handle, ref position, out var link) == 0) break;
                CountTraversal();
                if (position <= previousPosition || link == 0 || ++ordinal > MaximumBookmarksPerParent)
                    throw new InvalidOperationException("Page links exceed safe traversal limits.");
                var destination = LocalDestination(LinkGetDest(pdf.Handle, link), LinkGetAction(link));
                if (destination != 0)
                    Add($"link:{pageIndex}:{ordinal}", destination);
            }
        }

        var namedCount = NamedDestinationCount(pdf.Handle);
        if (namedCount > MaximumNamedDestinations)
            throw new InvalidOperationException("Named destinations exceed safe traversal limits.");
        for (uint index = 0; index < namedCount; index++)
        {
            CountTraversal();
            var nameLength = 0;
            var destination = GetNamedDestination(pdf.Handle, checked((int)index), 0, ref nameLength);
            Add("named:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), destination);
        }

        return result;
    }

    static Dictionary<int, int> MovePageMap(int count, int[] pages, int destination)
    {
        var selected = pages.ToHashSet();
        var order = Enumerable.Range(0, count).Where(index => !selected.Contains(index)).ToList();
        order.InsertRange(destination, pages);
        return order.Select((oldPage, newPage) => (oldPage, newPage)).ToDictionary(x => x.oldPage, x => x.newPage);
    }

    static void EnsureNavigationPreserved(Pdf pdf, Dictionary<string, int> before, Dictionary<int, int> pageMap,
        ISet<int>? removedLinkSourcePages = null)
    {
        var after = CaptureNavigationTargets(pdf);
        var expectedCount = before.Count(key => !IsLinkFromRemovedPage(key.Key, removedLinkSourcePages));
        if (expectedCount != after.Count)
            throw new InvalidOperationException("PDFium changed the document's navigation entries. The page operation was rolled back to the last checkpoint.");
        foreach (var (key, oldPage) in before)
        {
            if (IsLinkFromRemovedPage(key, removedLinkSourcePages)) continue;
            var mappedKey = key;
            if (key.StartsWith("link:", StringComparison.Ordinal))
            {
                var parts = key.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[1], out var linkPage) || !pageMap.TryGetValue(linkPage, out var mappedLinkPage))
                    throw new InvalidOperationException("A page link could not be mapped safely. The page operation was rolled back to the last checkpoint.");
                mappedKey = $"link:{mappedLinkPage}:{parts[2]}";
            }
            if (!after.TryGetValue(mappedKey, out var actual) || !pageMap.TryGetValue(oldPage, out var expected) || actual != expected)
                throw new InvalidOperationException("PDFium changed a bookmark, link, or named destination target. The page operation was rolled back to the last checkpoint.");
        }
    }

    static bool IsLinkFromRemovedPage(string key, ISet<int>? removedPages)
    {
        if (removedPages is null || !key.StartsWith("link:", StringComparison.Ordinal)) return false;
        var parts = key.Split(':');
        return parts.Length == 3 && int.TryParse(parts[1], out var page) && removedPages.Contains(page);
    }

    static void EnsureNavigationCanMap(Dictionary<string, int> targets, Dictionary<int, int> pageMap,
        ISet<int>? removedLinkSourcePages = null)
    {
        foreach (var (key, oldPage) in targets)
            if (!IsLinkFromRemovedPage(key, removedLinkSourcePages) && !pageMap.ContainsKey(oldPage))
                throw new InvalidOperationException("A bookmark, link, or named destination points to a page being removed. Page deletion was refused to keep navigation intact.");
    }

    const uint PdfActionGoTo = 1;
    [DllImport("pdfium", EntryPoint = "FPDFBookmark_GetAction", CallingConvention = CallingConvention.Cdecl)]
    static extern nint BookmarkAction(nint bookmark);
    [DllImport("pdfium", EntryPoint = "FPDFAction_GetType", CallingConvention = CallingConvention.Cdecl)]
    static extern uint ActionGetType(nint action);
    [DllImport("pdfium", EntryPoint = "FPDFAction_GetDest", CallingConvention = CallingConvention.Cdecl)]
    static extern nint ActionGetDest(nint document, nint action);
    [DllImport("pdfium", EntryPoint = "FPDFLink_Enumerate", CallingConvention = CallingConvention.Cdecl)]
    static extern int LinkEnumerate(nint page, ref int startPosition, out nint link);
    [DllImport("pdfium", EntryPoint = "FPDFLink_GetDest", CallingConvention = CallingConvention.Cdecl)]
    static extern nint LinkGetDest(nint document, nint link);
    [DllImport("pdfium", EntryPoint = "FPDFLink_GetAction", CallingConvention = CallingConvention.Cdecl)]
    static extern nint LinkGetAction(nint link);
    [DllImport("pdfium", EntryPoint = "FPDF_CountNamedDests", CallingConvention = CallingConvention.Cdecl)]
    static extern uint NamedDestinationCount(nint document);
    [DllImport("pdfium", EntryPoint = "FPDF_GetNamedDest", CallingConvention = CallingConvention.Cdecl)]
    static extern nint GetNamedDestination(nint document, int index, nint buffer, ref int bufferLength);
}
