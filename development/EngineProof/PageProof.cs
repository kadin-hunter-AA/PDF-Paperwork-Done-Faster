using System.Runtime.InteropServices;

// Page import belongs to the PDF adapter alongside the other PDFium calls.
// This partial declaration keeps the native entry point isolated from the proof.
partial class Pdf
{
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int FPDF_ImportPages(nint destDoc, nint srcDoc, string? pageRange, int insertIndex);

    public static void ImportPages(Pdf destination, Pdf source, string? pageRange, int insertIndex)
        => Check(FPDF_ImportPages(destination.Handle, source.Handle, pageRange, insertIndex), "import pages");
}

static class PageProof
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var sourcePath = Path.Combine(output, "page-source.pdf");
        var mergePath = Path.Combine(output, "page-merge-source.pdf");
        var resultPath = Path.Combine(output, "page-reordered-merged.pdf");

        using (var source = Pdf.Create())
        {
            using (var first = source.NewPage())
            {
                first.AddText("Source page one", 72, 700);
                first.Generate();
            }
            using (var second = source.NewPage())
            {
                second.AddText("Source page two", 72, 700);
                second.Generate();
            }
            source.Save(sourcePath);
        }

        using (var mergeSource = Pdf.Create())
        {
            using (var page = mergeSource.NewPage())
            {
                page.AddText("Merged source page", 72, 700);
                page.Generate();
            }
            mergeSource.Save(mergePath);
        }

        using (var reopenedSource = Pdf.Open(sourcePath))
        using (var reopenedMerge = Pdf.Open(mergePath))
        using (var result = Pdf.Create())
        {
            Pdf.Check(reopenedSource.PageCount == 2, "reopened two-page source");
            Pdf.Check(reopenedMerge.PageCount == 1, "reopened merge source");
            Pdf.ImportPages(result, reopenedSource, "2,1", 0);
            Pdf.ImportPages(result, reopenedMerge, null, result.PageCount);
            Pdf.Check(result.PageCount == 3, "reordered and merged page count");
            result.Save(resultPath);
        }

        using (var reopenedResult = Pdf.Open(resultPath))
        {
            Pdf.Check(reopenedResult.PageCount == 3, "saved/reopened merged page count");
            AssertPageText(reopenedResult, 0, "Source page two");
            AssertPageText(reopenedResult, 1, "Source page one");
            AssertPageText(reopenedResult, 2, "Merged source page");
        }
    }

    private static void AssertPageText(Pdf document, int index, string expected)
    {
        string text;
        using (var page = document.Page(index))
            text = page.Text();
        Pdf.Check(text.Contains(expected, StringComparison.Ordinal), $"page {index + 1} contains {expected}");
    }
}
