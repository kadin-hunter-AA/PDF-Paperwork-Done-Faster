using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    const double MaximumBlankPageDimension = 14_400;
    const int MaximumPagesPerMove = 50_000;
    const int MaximumDocumentPagesForOrganization = 50_000;
    const int MaximumPageOperationSelection = 5_000;

    public object MovePages(Guid id, long revision, int[] pages, int destination)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        var count = pdf.PageCount;
        if (count > MaximumDocumentPagesForOrganization)
            throw new InvalidOperationException($"Page organization supports documents with up to {MaximumDocumentPagesForOrganization:N0} pages.");
        if (pages is null || pages.Length < 1 || pages.Length > MaximumPagesPerMove || pages.Length >= count ||
            pages.Any(page => page < 0 || page >= count) || pages.Distinct().Count() != pages.Length)
            throw new InvalidOperationException("Select one or more distinct pages, leaving at least one page in the document.");
        if (destination < 0 || destination > count - pages.Length)
            throw new InvalidOperationException("The destination must be a position in the remaining page list.");
        EnsureNoCustomPageLabels(pdf);
        var navigation = CaptureNavigationTargets(pdf);
        var pageMap = MovePageMap(count, pages, destination);
        var result = MovePagesNative(pdf.Handle, pages, checked((uint)pages.Length), destination);
        if (result == 0)
            throw new InvalidOperationException("PDFium could not safely reorder these pages. The last committed checkpoint will be restored.");
        if (pdf.PageCount != count)
            throw new InvalidOperationException("PDFium changed the page count unexpectedly during reorder. The last committed checkpoint will be restored.");
        EnsureNavigationPreserved(pdf, navigation, pageMap);
        navigationValidatedDocuments.Add(id);
        documents[id] = (pdf, revision + 1);
        return new { PageCount = pdf.PageCount };
    }

    public object InsertBlankPage(Guid id, long revision, int index, double width, double height)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        if (pdf.PageCount >= MaximumDocumentPagesForOrganization)
            throw new InvalidOperationException($"Page organization supports documents with up to {MaximumDocumentPagesForOrganization:N0} pages.");
        if (index < 0 || index > pdf.PageCount)
            throw new InvalidOperationException("Choose an insertion position between the first page and the end of the document.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > MaximumBlankPageDimension || height > MaximumBlankPageDimension)
            throw new InvalidOperationException("Blank page dimensions must be between 1 and 14,400 PDF points.");
        EnsureNoCustomPageLabels(pdf);
        var oldCount = pdf.PageCount;
        var navigation = CaptureNavigationTargets(pdf);
        var handle = NewPage(pdf.Handle, index, width, height);
        if (handle == 0)
            throw new InvalidOperationException("PDFium could not insert a blank page. The last committed checkpoint will be restored.");
        Pdf.Native.FPDF_ClosePage(handle);
        if (pdf.PageCount != oldCount + 1)
            throw new InvalidOperationException("PDFium did not insert exactly one page. The last committed checkpoint will be restored.");
        var pageMap = Enumerable.Range(0, oldCount).ToDictionary(oldPage => oldPage,
            oldPage => oldPage < index ? oldPage : oldPage + 1);
        EnsureNavigationPreserved(pdf, navigation, pageMap);
        navigationValidatedDocuments.Add(id);
        documents[id] = (pdf, revision + 1);
        return new { PageCount = pdf.PageCount };
    }

    public object DeletePages(Guid id, long revision, int[] pages)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        var count = pdf.PageCount;
        CheckOrganizationDocumentSize(count);
        ValidatePageSelection(pages, count, allowAll: false);
        EnsureNoCustomPageLabels(pdf);
        if (CatalogIsTagged(pdf.Handle) != 0)
            throw new InvalidOperationException("Tagged PDFs cannot be deleted page by page because structure references cannot be remapped safely.");
        EnsurePagesSafeToRemove(pdf, pages);
        var navigation = CaptureNavigationTargets(pdf);
        var removed = pages.ToHashSet();
        var pageMap = Enumerable.Range(0, count).Where(index => !removed.Contains(index))
            .Select((oldPage, newPage) => (oldPage, newPage)).ToDictionary(x => x.oldPage, x => x.newPage);
        EnsureNavigationCanMap(navigation, pageMap, removed);
        foreach (var page in pages.OrderByDescending(page => page)) DeletePage(pdf.Handle, page);
        if (pdf.PageCount != count - pages.Length)
            throw new InvalidOperationException("PDFium did not delete the selected pages. The last committed checkpoint will be restored.");
        EnsureNavigationPreserved(pdf, navigation, pageMap, removed);
        navigationValidatedDocuments.Add(id);
        documents[id] = (pdf, revision + 1);
        return new { PageCount = pdf.PageCount };
    }

    public object DuplicatePages(Guid id, long revision, int[] pages, int destination)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        var count = pdf.PageCount;
        CheckOrganizationDocumentSize(count);
        ValidatePageSelection(pages, count, allowAll: true);
        if ((long)count + pages.Length > MaximumDocumentPagesForOrganization)
            throw new InvalidOperationException($"Page organization supports documents with up to {MaximumDocumentPagesForOrganization:N0} pages.");
        if (destination < 0 || destination > count)
            throw new InvalidOperationException("Choose a duplicate insertion position from the start through the end of the page list.");
        EnsureNoCustomPageLabels(pdf);
        if (SecurityHandlerRevision(pdf.Handle) != -1)
            throw new InvalidOperationException("Encrypted PDFs cannot be duplicated yet. Use an unencrypted copy.");
        EnsurePagesSafeToImport(pdf, pages);
        var navigation = CaptureNavigationTargets(pdf);
        var pageMap = InsertPageMap(count, destination, pages.Length);
        var temp = Path.Combine(Path.GetTempPath(), $"pdfeditor-duplicate-{Guid.NewGuid():N}.pdf");
        try
        {
            pdf.Save(temp);
            using var source = Pdf.Open(temp);
            var result = ImportPagesByIndex(pdf.Handle, source.Handle, pages, checked((uint)pages.Length), destination);
            if (result == 0 || pdf.PageCount != count + pages.Length)
                throw new InvalidOperationException("PDFium could not duplicate these pages safely. The last committed checkpoint will be restored.");
            EnsureNavigationPreserved(pdf, navigation, pageMap);
            navigationValidatedDocuments.Add(id);
            documents[id] = (pdf, revision + 1);
            return new { PageCount = pdf.PageCount };
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    public object InsertPages(Guid id, long revision, string sourcePath, int[] pages, int destination)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        var count = pdf.PageCount;
        CheckOrganizationDocumentSize(count);
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("Choose an accessible source PDF.");
        if (destination < 0 || destination > count)
            throw new InvalidOperationException("Choose an insertion position from the start through the end of the page list.");
        EnsureNoCustomPageLabels(pdf);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new InvalidOperationException("The selected source PDF cannot be found.");
        var sourceHash = HashFile(fullSourcePath);
        using var source = Pdf.Open(fullSourcePath);
        EnsureSimpleImportSource(source);
        ValidatePageSelection(pages, source.PageCount, allowAll: true);
        if ((long)count + pages.Length > MaximumDocumentPagesForOrganization)
            throw new InvalidOperationException($"Page organization supports documents with up to {MaximumDocumentPagesForOrganization:N0} pages.");
        EnsurePagesSafeToImport(source, pages);
        var navigation = CaptureNavigationTargets(pdf);
        var pageMap = InsertPageMap(count, destination, pages.Length);
        var importedGeometry = pages.Select(index => PageGeometry(source, index)).ToArray();
        var result = ImportPagesByIndex(pdf.Handle, source.Handle, pages, checked((uint)pages.Length), destination);
        if (result == 0 || pdf.PageCount != count + pages.Length)
            throw new InvalidOperationException("PDFium could not insert the selected source pages safely. The last committed checkpoint will be restored.");
        EnsureImportedGeometry(pdf, destination, importedGeometry);
        EnsureNavigationPreserved(pdf, navigation, pageMap);
        if (!sourceHash.SequenceEqual(HashFile(fullSourcePath)))
            throw new InvalidOperationException("The source PDF changed during insertion. The last committed checkpoint will be restored.");
        navigationValidatedDocuments.Add(id);
        documents[id] = (pdf, revision + 1);
        return new { PageCount = pdf.PageCount };
    }

    public object ExtractPages(Guid id, long revision, int[] pages, string outputPath)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        CheckOrganizationDocumentSize(pdf.PageCount);
        ValidatePageSelection(pages, pdf.PageCount, allowAll: true);
        EnsureSimpleExportSource(pdf, pages);
        var output = ValidateNewOutputFile(outputPath);
        var temp = Path.Combine(Path.GetDirectoryName(output)!, $".pdfeditor-extract-{Guid.NewGuid():N}.tmp");
        try
        {
            SavePagesToNewPdf(pdf, pages, temp);
            File.Move(temp, output, false);
            return new { PageCount = pages.Length, Path = output };
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    public object SplitPages(Guid id, long revision, string outputDirectory)
    {
        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        CheckOrganizationDocumentSize(pdf.PageCount);
        EnsureSimpleExportSource(pdf, Enumerable.Range(0, pdf.PageCount).ToArray());
        var output = Path.GetFullPath(outputDirectory ?? "");
        if (Directory.Exists(output) || File.Exists(output))
            throw new InvalidOperationException("Choose a new, unused folder for split output. Existing content will never be replaced.");
        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new InvalidOperationException("The split output folder's parent must already exist.");
        var leaf = Path.GetFileName(output);
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            throw new InvalidOperationException("Choose a named output folder.");
        var staging = Path.Combine(parent, $".{leaf}.pdfeditor-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            for (var page = 0; page < pdf.PageCount; page++)
                SavePagesToNewPdf(pdf, [page], Path.Combine(staging, $"page-{page + 1:D4}.pdf"));
            Directory.Move(staging, output);
            return new { PageCount = pdf.PageCount, OutputDirectory = output };
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
            }
        }
    }

    static string ValidateNewOutputFile(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Choose a PDF output path.");
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output))
            throw new InvalidOperationException("Choose a new output filename. Existing files are never overwritten.");
        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new InvalidOperationException("The output folder does not exist or is unavailable.");
        return output;
    }

    static void SavePagesToNewPdf(Pdf source, int[] pages, string outputPath)
    {
        using var output = Pdf.Create();
        if (ImportPagesByIndex(output.Handle, source.Handle, pages, checked((uint)pages.Length), 0) == 0 || output.PageCount != pages.Length)
            throw new InvalidOperationException("PDFium could not extract the selected pages safely.");
        EnsureImportedGeometry(output, 0, pages.Select(index => PageGeometry(source, index)).ToArray());
        output.Save(outputPath);
        using var reopened = Pdf.Open(outputPath);
        if (reopened.PageCount != pages.Length)
            throw new InvalidOperationException("The exported PDF did not reopen with the expected page count.");
        EnsureImportedGeometry(reopened, 0, pages.Select(index => PageGeometry(source, index)).ToArray());
        for (var index = 0; index < reopened.PageCount; index++) { using var page = reopened.Page(index); }
    }

    static void EnsureSimpleExportSource(Pdf pdf, int[] pageIndices)
    {
        if (SecurityHandlerRevision(pdf.Handle) != -1)
            throw new InvalidOperationException("Encrypted PDFs cannot be extracted or split yet. Use an unencrypted copy.");
        if (NamedDestinationCount(pdf.Handle) != 0 || AttachmentCount(pdf.Handle) != 0 || FormType(pdf.Handle) != 0 ||
            CatalogIsTagged(pdf.Handle) != 0)
            throw new InvalidOperationException("Extract and split currently support only PDFs without forms, bookmarks, named destinations, attachments, or tagged structures.");
        EnsureNoBookmarks(pdf);
        EnsureNoCustomPageLabels(pdf);
        EnsurePagesSafeToImport(pdf, pageIndices);
    }

    static void ValidatePageSelection(int[] pages, int pageCount, bool allowAll)
    {
        if (pages is null || pages.Length < 1 || pages.Length > MaximumPageOperationSelection ||
            (!allowAll && pages.Length >= pageCount) || pages.Any(page => page < 0 || page >= pageCount) ||
            pages.Distinct().Count() != pages.Length)
            throw new InvalidOperationException($"Select between 1 and {MaximumPageOperationSelection:N0} distinct pages within the document.");
    }

    static void CheckOrganizationDocumentSize(int count)
    {
        if (count < 1 || count > MaximumDocumentPagesForOrganization)
            throw new InvalidOperationException($"Page organization supports documents with up to {MaximumDocumentPagesForOrganization:N0} pages.");
    }

    static Dictionary<int, int> InsertPageMap(int oldCount, int destination, int insertedCount) =>
        Enumerable.Range(0, oldCount).ToDictionary(oldPage => oldPage,
            oldPage => oldPage < destination ? oldPage : oldPage + insertedCount);

    static (double Width, double Height, int Rotation) PageGeometry(Pdf pdf, int index)
    {
        using var page = pdf.Page(index);
        return (Width(page.Handle), Height(page.Handle), Rotation(page.Handle));
    }

    static void EnsureImportedGeometry(Pdf destination, int index, (double Width, double Height, int Rotation)[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = PageGeometry(destination, index + i);
            if (Math.Abs(actual.Width - expected[i].Width) > 0.01 || Math.Abs(actual.Height - expected[i].Height) > 0.01 || actual.Rotation != expected[i].Rotation)
                throw new InvalidOperationException("PDFium changed an imported page's size or rotation. The last committed checkpoint will be restored.");
        }
    }

    static void EnsurePagesSafeToRemove(Pdf pdf, int[] pageIndices)
    {
        foreach (var index in pageIndices)
        {
            using var page = pdf.Page(index);
            var annotations = Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle);
            if (annotations < 0 || annotations > 10_000) throw new InvalidOperationException("Page annotations exceed safe limits.");
            for (var i = 0; i < annotations; i++)
            {
                var annotation = Pdf.Objects.FPDFPage_GetAnnot(page.Handle, i);
                if (annotation == 0) throw new InvalidOperationException("Page annotations could not be inspected safely.");
                try
                {
                    if (Pdf.Objects.FPDFAnnot_GetSubtype(annotation) == 20)
                        throw new InvalidOperationException("Pages with form widgets cannot be deleted safely yet.");
                }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
            }
        }
    }

    static void EnsurePagesSafeToImport(Pdf pdf, int[] pageIndices)
    {
        foreach (var index in pageIndices)
        {
            using var page = pdf.Page(index);
            var annotations = Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle);
            if (annotations != 0)
                throw new InvalidOperationException("Pages with annotations or form widgets cannot be duplicated or imported safely yet.");
            var visited = 0;
            void CheckObject(nint obj, int depth)
            {
                if (++visited > 100_000 || depth > 64)
                    throw new InvalidOperationException("Imported page objects exceed safe inspection limits.");
                var marks = PageObjectCountMarks(obj);
                if (marks < 0 || marks > 10_000) throw new InvalidOperationException("Page marked content could not be inspected safely.");
                if (marks != 0) throw new InvalidOperationException("Tagged or marked page content cannot be duplicated or imported safely yet.");
                if (Pdf.Native.FPDFPageObj_GetType(obj) != 5) return;
                var children = FormObjectCount(obj);
                if (children < 0 || children > 100_000) throw new InvalidOperationException("Nested page objects exceed safe inspection limits.");
                for (var child = 0; child < children; child++)
                {
                    var nested = FormObjectGet(obj, checked((uint)child));
                    if (nested == 0) throw new InvalidOperationException("Nested page objects could not be inspected safely.");
                    CheckObject(nested, depth + 1);
                }
            }
            var count = Pdf.Native.FPDFPage_CountObjects(page.Handle);
            if (count < 0 || count > 100_000) throw new InvalidOperationException("Page objects exceed safe inspection limits.");
            for (var i = 0; i < count; i++)
            {
                var obj = Pdf.Native.FPDFPage_GetObject(page.Handle, i);
                if (obj == 0) throw new InvalidOperationException("Page objects could not be inspected safely.");
                CheckObject(obj, 0);
            }
        }
    }

    static void EnsureSimpleImportSource(Pdf source)
    {
        CheckOrganizationDocumentSize(source.PageCount);
        if (SecurityHandlerRevision(source.Handle) != -1 || SignatureCount(source.Handle) != 0)
            throw new InvalidOperationException("Encrypted or signature-field source PDFs cannot be inserted. Use an unsigned, unencrypted copy.");
        var attachments = AttachmentCount(source.Handle);
        if (attachments != 0) throw new InvalidOperationException("PDFs with embedded files cannot be inserted because those document-level files would not transfer.");
        if (FormType(source.Handle) != 0) throw new InvalidOperationException("PDFs with forms cannot be inserted because form fields cannot be remapped safely.");
        if (CatalogIsTagged(source.Handle) != 0) throw new InvalidOperationException("Tagged PDFs cannot be inserted because structure references cannot be remapped safely.");
        if (NamedDestinationCount(source.Handle) != 0) throw new InvalidOperationException("PDFs with named destinations cannot be inserted because destinations cannot be remapped safely.");
        EnsureNoCustomPageLabels(source);
        EnsureNoBookmarks(source);
    }

    static void EnsureNoBookmarks(Pdf pdf)
    {
        var visited = new HashSet<nint>();
        var total = 0;
        void Walk(nint parent, int depth)
        {
            if (depth > MaximumBookmarkDepth) throw new InvalidOperationException("The source bookmark tree exceeds safe inspection limits.");
            var bookmark = FirstBookmark(pdf.Handle, parent);
            var siblings = 0;
            while (bookmark != 0)
            {
                if (++siblings > MaximumBookmarksPerParent || ++total > MaximumNavigationEntries || !visited.Add(bookmark))
                    throw new InvalidOperationException("The source bookmark tree exceeds safe inspection limits.");
                Walk(bookmark, depth + 1);
                bookmark = NextBookmark(pdf.Handle, bookmark);
            }
        }
        Walk(0, 0);
        if (total != 0) throw new InvalidOperationException("PDFs with bookmarks cannot be inserted because bookmark targets cannot be remapped safely.");
    }

    static void EnsureNoCustomPageLabels(Pdf pdf)
    {
        for (var page = 0; page < pdf.PageCount; page++)
        {
            var length = GetPageLabel(pdf.Handle, page, 0, 0);
            if (length != 0)
                throw new InvalidOperationException("Page labels are present. Page operations are disabled until labels can be remapped safely.");
        }
    }

    [DllImport("pdfium", EntryPoint = "FPDF_MovePages", CallingConvention = CallingConvention.Cdecl)]
    static extern int MovePagesNative(nint document, [In] int[] pageIndices, uint pageIndicesLength, int destinationIndex);
    [DllImport("pdfium", EntryPoint = "FPDFPage_New", CallingConvention = CallingConvention.Cdecl)]
    static extern nint NewPage(nint document, int pageIndex, double width, double height);
    [DllImport("pdfium", EntryPoint = "FPDF_GetPageLabel", CallingConvention = CallingConvention.Cdecl)]
    static extern uint GetPageLabel(nint document, int pageIndex, nint buffer, uint bufferLength);
    [DllImport("pdfium", EntryPoint = "FPDFPage_Delete", CallingConvention = CallingConvention.Cdecl)]
    static extern void DeletePage(nint document, int pageIndex);
    [DllImport("pdfium", EntryPoint = "FPDF_ImportPagesByIndex", CallingConvention = CallingConvention.Cdecl)]
    static extern int ImportPagesByIndex(nint destination, nint source, [In] int[] pageIndices, uint length, int index);
    [DllImport("pdfium", EntryPoint = "FPDFFormObj_CountObjects", CallingConvention = CallingConvention.Cdecl)]
    static extern int FormObjectCount(nint formObject);
    [DllImport("pdfium", EntryPoint = "FPDFFormObj_GetObject", CallingConvention = CallingConvention.Cdecl)]
    static extern nint FormObjectGet(nint formObject, uint index);
    [DllImport("pdfium", EntryPoint = "FPDFDoc_GetAttachmentCount", CallingConvention = CallingConvention.Cdecl)]
    static extern int AttachmentCount(nint document);
    [DllImport("pdfium", EntryPoint = "FPDF_GetFormType", CallingConvention = CallingConvention.Cdecl)]
    static extern int FormType(nint document);
    [DllImport("pdfium", EntryPoint = "FPDFCatalog_IsTagged", CallingConvention = CallingConvention.Cdecl)]
    static extern int CatalogIsTagged(nint document);
}
