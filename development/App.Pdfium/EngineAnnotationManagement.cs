using System.Globalization;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    string AnnotationManagementRefusal(Pdf pdf)
    {
        if (pdf.PageCount > 1000) return "Annotation management currently supports documents of at most 1,000 pages.";
        var total = 0;
        for (var pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
        {
            using var page = pdf.Page(pageIndex);
            var count = Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle);
            if (count < 0 || count > 10_000 || (total += count) > 10_000)
                return "Annotation relationships exceed the supported inspection limit.";
            for (var index = 0; index < count; index++)
            {
                var annotation = Pdf.Objects.FPDFPage_GetAnnot(page.Handle, index);
                if (annotation == 0) return "Annotation relationships could not be inspected safely.";
                try
                {
                    var subtype = Pdf.Objects.FPDFAnnot_GetSubtype(annotation);
                    if (new[] { "Popup", "IRT", "RT" }.Any(key => Pdf.Objects.FPDFAnnot_HasKey(annotation, key) != 0) ||
                        subtype != 20 && Pdf.Objects.FPDFAnnot_HasKey(annotation, "Parent") != 0)
                        return "Documents with linked annotation popups or replies are protected in this workflow.";
                }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
            }
        }
        return "";
    }
    public object InspectAnnotationManagement(Guid id, long revision)
    {
        var reason = AnnotationManagementRefusal(Get(id, revision));
        return new { Supported = reason.Length == 0, Reason = reason };
    }
    static bool HasAnnotationRelationships(nint annotation) =>
        new[] { "Popup", "IRT", "RT", "Parent" }.Any(key => Pdf.Objects.FPDFAnnot_HasKey(annotation, key) != 0);
    static int AnnotationIndex(Guid id, long revision, int page, string reference)
    {
        if (reference.Length > 180) throw new InvalidOperationException("The selected annotation is stale. Reload the annotation list.");
        var parts = reference.Split(':');
        if (parts.Length != 5 || parts[0] != id.ToString("N") || parts[1] != revision.ToString(CultureInfo.InvariantCulture) ||
            parts[2] != page.ToString(CultureInfo.InvariantCulture) || parts[3] != "annotation" ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            throw new InvalidOperationException("The selected annotation is stale. Reload the annotation list.");
        return index;
    }
    static nint EditableAnnotation(nint page, int index)
    {
        var count = Pdf.Objects.FPDFPage_GetAnnotCount(page);
        if (count < 0 || count > 10_000 || index >= count) throw new InvalidOperationException("The selected annotation is unavailable. Reload the annotation list.");
        var annotation = Pdf.Objects.FPDFPage_GetAnnot(page, index);
        Pdf.Check(annotation != 0, "open selected annotation");
        var subtype = Pdf.Objects.FPDFAnnot_GetSubtype(annotation);
        var flags = Pdf.Objects.FPDFAnnot_GetFlags(annotation);
        if (subtype is not (1 or 5 or 6 or 9 or 10 or 12 or 15) || (flags & (64 | 128 | 512)) != 0 || HasAnnotationRelationships(annotation))
        {
            Pdf.Objects.FPDFPage_CloseAnnot(annotation);
            throw new InvalidOperationException("Only unlocked sticky notes, line strokes, rectangles, ellipses, highlights, underlines and strikeouts without linked popups or replies can be managed here. Links and form fields are protected.");
        }
        return annotation;
    }
    public void DeleteAnnotation(Guid id, long revision, int pageIndex, string reference)
    {
        var pdf = Get(id, revision); CheckEditable(id, pdf); CheckPage(pdf, pageIndex);
        var refusal = AnnotationManagementRefusal(pdf);
        if (refusal.Length > 0) throw new InvalidOperationException(refusal);
        var index = AnnotationIndex(id, revision, pageIndex, reference);
        using var page = pdf.Page(pageIndex);
        var annotation = EditableAnnotation(page.Handle, index);
        Pdf.Objects.FPDFPage_CloseAnnot(annotation);
        Pdf.Check(Pdf.Objects.FPDFPage_RemoveAnnot(page.Handle, index), "remove selected annotation");
        documents[id] = (pdf, revision + 1);
    }
    const int MaxMovedInkPoints = 20_000;

    /// <summary>Opens a managed Square, Circle or Ink drawing for geometry or style changes.</summary>
    nint EditableDrawing(Guid id, long revision, int pageIndex, string reference, nint page)
    {
        var index = AnnotationIndex(id, revision, pageIndex, reference);
        var annotation = EditableAnnotation(page, index);
        if (Pdf.Objects.FPDFAnnot_GetSubtype(annotation) is not (5 or 6 or 15))
        {
            Pdf.Objects.FPDFPage_CloseAnnot(annotation);
            throw new InvalidOperationException("Moving and restyling are available for rectangles, ellipses and line or pen strokes only.");
        }
        return annotation;
    }

    public void MoveDrawing(Guid id, long revision, int pageIndex, string reference, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || Math.Abs(dx) > 1e5 || Math.Abs(dy) > 1e5 || (dx == 0 && dy == 0))
            throw new InvalidOperationException("Enter a finite, nonzero move distance in PDF points.");
        var pdf = Get(id, revision); CheckEditable(id, pdf); CheckPage(pdf, pageIndex);
        var refusal = AnnotationManagementRefusal(pdf);
        if (refusal.Length > 0) throw new InvalidOperationException(refusal);
        using var page = pdf.Page(pageIndex);
        var box = GetVisibleBox(page.Handle);
        var annotation = EditableDrawing(id, revision, pageIndex, reference, page.Handle);
        try
        {
            Pdf.Check(Pdf.Objects.FPDFAnnot_GetRect(annotation, out var rect), "read drawing bounds");
            var moved = new Pdf.Rect { Left = (float)(rect.Left + dx), Right = (float)(rect.Right + dx),
                Bottom = (float)(rect.Bottom + dy), Top = (float)(rect.Top + dy) };
            if (!box.Contains(moved.Left, moved.Bottom, moved.Right, moved.Top))
                throw new InvalidOperationException("The move would push the drawing off the page. Choose a smaller distance.");
            var shiftX = moved.Left - rect.Left; var shiftY = moved.Bottom - rect.Bottom;
            if (Pdf.Objects.FPDFAnnot_GetSubtype(annotation) == 15)
            {
                var count = (int)PdfDrawing.InkListCount(annotation);
                var paths = new List<PdfDrawing.Point[]>();
                var total = 0;
                for (var i = 0; i < count; i++)
                {
                    var length = (int)PdfDrawing.InkListPath(annotation, (nuint)i, null, 0);
                    if (length < 2 || (total += length) > MaxMovedInkPoints)
                        throw new InvalidOperationException("This stroke is too complex to move safely.");
                    var points = new PdfDrawing.Point[length];
                    if ((int)PdfDrawing.InkListPath(annotation, (nuint)i, points, (nuint)length) != length)
                        throw new InvalidOperationException("The stroke could not be read completely.");
                    paths.Add(points);
                }
                if (paths.Count == 0) throw new InvalidOperationException("This stroke has no points to move.");
                Pdf.Check(PdfDrawing.RemoveInkList(annotation), "clear stroke before moving");
                Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annotation, ref moved), "set moved bounds");
                foreach (var path in paths)
                {
                    for (var i = 0; i < path.Length; i++) { path[i].X += shiftX; path[i].Y += shiftY; }
                    if (PdfDrawing.AddInkStroke(annotation, path, (nuint)path.Length) < 0)
                        throw new InvalidOperationException("PDFium could not rewrite the moved stroke.");
                }
            }
            else Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annotation, ref moved), "set moved bounds");
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }

    public void RestyleDrawing(Guid id, long revision, int pageIndex, string reference, int red, int green, int blue, double width)
    {
        if (red is < 0 or > 255 || green is < 0 or > 255 || blue is < 0 or > 255)
            throw new InvalidOperationException("Colors must use values from 0 to 255.");
        if (!double.IsFinite(width) || width < .5 || width > 12)
            throw new InvalidOperationException("Line width must be between 0.5 and 12 points.");
        var pdf = Get(id, revision); CheckEditable(id, pdf); CheckPage(pdf, pageIndex);
        var refusal = AnnotationManagementRefusal(pdf);
        if (refusal.Length > 0) throw new InvalidOperationException(refusal);
        using var page = pdf.Page(pageIndex);
        var annotation = EditableDrawing(id, revision, pageIndex, reference, page.Handle);
        try
        {
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(annotation, 0, (uint)red, (uint)green, (uint)blue, 255), "set drawing color");
            Pdf.Check(PdfDrawing.SetBorder(annotation, 0, 0, (float)width), "set drawing line width");
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }

    public void UpdateComment(Guid id, long revision, int pageIndex, string reference, string contents)
    {
        if (string.IsNullOrWhiteSpace(contents) || contents.Length > 4000 || contents.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidOperationException("Enter a comment of 1 to 4,000 printable characters.");
        var pdf = Get(id, revision); CheckEditable(id, pdf); CheckPage(pdf, pageIndex);
        var refusal = AnnotationManagementRefusal(pdf);
        if (refusal.Length > 0) throw new InvalidOperationException(refusal);
        var index = AnnotationIndex(id, revision, pageIndex, reference);
        using var page = pdf.Page(pageIndex);
        var annotation = EditableAnnotation(page.Handle, index);
        try
        {
            if (Pdf.Objects.FPDFAnnot_GetSubtype(annotation) != 1)
                throw new InvalidOperationException("Comment editing is available for sticky notes only.");
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetStringValue(annotation, "Contents", contents), "update sticky note contents");
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }
}
