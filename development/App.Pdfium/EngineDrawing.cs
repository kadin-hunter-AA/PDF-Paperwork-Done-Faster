namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    /// <summary>Optional stroke color and width for new drawings; null keeps the default purple at the default width.</summary>
    public readonly record struct StrokeStyle(int Red, int Green, int Blue, double Width)
    {
        public void Validate()
        {
            if (Red is < 0 or > 255 || Green is < 0 or > 255 || Blue is < 0 or > 255)
                throw new InvalidOperationException("Colors must use values from 0 to 255.");
            if (!double.IsFinite(Width) || Width < .5 || Width > 12)
                throw new InvalidOperationException("Line width must be between 0.5 and 12 points.");
        }
    }

    static void ApplyStroke(nint annotation, StrokeStyle? style, float defaultWidth)
    {
        var (red, green, blue) = style is { } s ? ((uint)s.Red, (uint)s.Green, (uint)s.Blue) : (51u, 23u, 139u);
        Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(annotation, 0, red, green, blue, 255), "set drawing color");
        var width = style is { } w ? (float)w.Width : defaultWidth;
        if (width > 0) Pdf.Check(PdfDrawing.SetBorder(annotation, 0, 0, width), "set drawing line width");
    }

    public void AddDrawing(Guid id, long revision, int pageIndex, string kind,
        double left, double bottom, double right, double top, StrokeStyle? style = null)
    {
        style?.Validate();
        // Line and Arrow use Ink strokes because PDFium does not expose a Line endpoint setter.
        // Coordinates are PDF default user space (unrotated, MediaBox origin); the caller maps rotated or offset views into it.
        var subtype = kind switch
        {
            "Line" => 15,
            "Arrow" => 15,
            "Rectangle" => 5,
            "Ellipse" => 6,
            _ => throw new InvalidOperationException("Choose Line, Arrow, Rectangle, or Ellipse.")
        };
        if (!double.IsFinite(left) || !double.IsFinite(bottom) ||
            !double.IsFinite(right) || !double.IsFinite(top))
            throw new InvalidOperationException("Drawing bounds must be finite PDF page coordinates.");

        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        CheckPage(pdf, pageIndex);
        using var page = pdf.Page(pageIndex);
        var box = GetVisibleBox(page.Handle);
        if (right <= left || top <= bottom || !box.Contains(left, bottom, right, top))
            throw new InvalidOperationException("Drawing bounds must be a nonempty rectangle inside the selected page.");
        var l = (float)left; var b = (float)bottom; var r = (float)right; var t = (float)top;
        if (!float.IsFinite(l) || !float.IsFinite(b) || !float.IsFinite(r) || !float.IsFinite(t) || r <= l || t <= b)
            throw new InvalidOperationException("Drawing bounds are too small to represent safely in PDF page coordinates.");

        var annotation = Pdf.Objects.FPDFPage_CreateAnnot(page.Handle, subtype);
        Pdf.Check(annotation != 0, "create drawing annotation");
        try
        {
            var rect = new Pdf.Rect { Left = l, Top = t, Right = r, Bottom = b };
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annotation, ref rect), "set drawing bounds");
            Pdf.Check(PdfDrawing.SetFlags(annotation, 4), "enable drawing annotation for print");
            ApplyStroke(annotation, style, kind is "Line" or "Arrow" ? 0 : 2);
            if (kind is "Line" or "Arrow")
            {
                var points = new[] { new PdfDrawing.Point { X = l, Y = b }, new PdfDrawing.Point { X = r, Y = t } };
                if (PdfDrawing.AddInkStroke(annotation, points, (nuint)points.Length) < 0)
                    throw new InvalidOperationException("PDFium could not create the line stroke annotation.");
                if (kind == "Arrow")
                {
                    var dx = r - l; var dy = t - b;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    var head = Math.Min(12, length * .35);
                    var ux = dx / length; var uy = dy / length;
                    const double cos = .8660254037844386, sin = .5;
                    var wing1 = new[] { new PdfDrawing.Point { X = r, Y = t }, new PdfDrawing.Point {
                        X = Math.Clamp((float)(r - head * (ux * cos - uy * sin)), l, r),
                        Y = Math.Clamp((float)(t - head * (uy * cos + ux * sin)), b, t) } };
                    var wing2 = new[] { new PdfDrawing.Point { X = r, Y = t }, new PdfDrawing.Point {
                        X = Math.Clamp((float)(r - head * (ux * cos + uy * sin)), l, r),
                        Y = Math.Clamp((float)(t - head * (uy * cos - ux * sin)), b, t) } };
                    if (PdfDrawing.AddInkStroke(annotation, wing1, (nuint)wing1.Length) < 0 ||
                        PdfDrawing.AddInkStroke(annotation, wing2, (nuint)wing2.Length) < 0)
                        throw new InvalidOperationException("PDFium could not create the arrowhead strokes.");
                }
            }
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }

    const int MaxInkPoints = 5000;

    /// <summary>Adds a native Ink annotation from arbitrary page-space points: a freehand pen stroke, or a
    /// line/arrow running from the first point to the last in any direction.</summary>
    public void AddInk(Guid id, long revision, int pageIndex, string kind, IReadOnlyList<(double X, double Y)> points, StrokeStyle? style = null)
    {
        style?.Validate();
        if (kind is not ("Pen" or "Line" or "Arrow"))
            throw new InvalidOperationException("Choose Pen, Line, or Arrow.");
        if (points.Count < 2 || points.Count > MaxInkPoints)
            throw new InvalidOperationException($"A stroke needs between 2 and {MaxInkPoints} points.");
        if (kind != "Pen" && points.Count != 2)
            throw new InvalidOperationException("Lines and arrows need exactly a start and an end point.");
        foreach (var (x, y) in points)
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e7 || Math.Abs(y) > 1e7)
                throw new InvalidOperationException("Stroke points must be finite PDF page coordinates.");

        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        CheckPage(pdf, pageIndex);
        using var page = pdf.Page(pageIndex);
        var box = GetVisibleBox(page.Handle);
        if (points.Any(p => !box.Contains(p.X, p.Y)))
            throw new InvalidOperationException("Stroke points must be inside the selected page.");

        var path = points.Select(p => new PdfDrawing.Point { X = (float)p.X, Y = (float)p.Y }).ToArray();
        var strokes = new List<PdfDrawing.Point[]> { path };
        var start = path[0]; var end = path[^1];
        var dx = end.X - start.X; var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || (kind == "Pen" && path.Zip(path.Skip(1)).All(pair => pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y)))
            throw new InvalidOperationException("The stroke is too short to draw.");
        if (kind == "Arrow")
        {
            var head = Math.Min(12, length * .35);
            var ux = dx / length; var uy = dy / length;
            const double cos = .8660254037844386, sin = .5;
            foreach (var s in new[] { sin, -sin })
                strokes.Add(new[] { end, new PdfDrawing.Point {
                    X = Math.Clamp((float)(end.X - head * (ux * cos - uy * s)), (float)box.Left, (float)box.Right),
                    Y = Math.Clamp((float)(end.Y - head * (uy * cos + ux * s)), (float)box.Bottom, (float)box.Top) } });
        }
        var all = strokes.SelectMany(s => s).ToArray();
        var l = Math.Max((float)box.Left, all.Min(p => p.X) - 1); var r = Math.Min((float)box.Right, all.Max(p => p.X) + 1);
        var b = Math.Max((float)box.Bottom, all.Min(p => p.Y) - 1); var t = Math.Min((float)box.Top, all.Max(p => p.Y) + 1);
        if (!(r > l) || !(t > b))
            throw new InvalidOperationException("The stroke is too small to represent safely in PDF page coordinates.");

        var annotation = Pdf.Objects.FPDFPage_CreateAnnot(page.Handle, 15);
        Pdf.Check(annotation != 0, "create ink annotation");
        try
        {
            var rect = new Pdf.Rect { Left = l, Top = t, Right = r, Bottom = b };
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annotation, ref rect), "set ink bounds");
            Pdf.Check(PdfDrawing.SetFlags(annotation, 4), "enable ink annotation for print");
            ApplyStroke(annotation, style, 0);
            foreach (var stroke in strokes)
                if (PdfDrawing.AddInkStroke(annotation, stroke, (nuint)stroke.Length) < 0)
                    throw new InvalidOperationException("PDFium could not create the ink stroke.");
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }

    /// <summary>Adds one line of fixed 12 pt Helvetica text inside a bordered rectangle: page text plus a native
    /// Square annotation, committed as a single revision. Text that does not fit is refused, never clipped.</summary>
    public void AddTextBox(Guid id, long revision, int pageIndex, string text,
        double left, double bottom, double right, double top)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200 || text.Any(c => c < ' ' || c > '~'))
            throw new InvalidOperationException("Enter 1 to 200 characters of printable basic ASCII text on one line.");
        if (!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top))
            throw new InvalidOperationException("Text box bounds must be finite PDF page coordinates.");

        var pdf = Get(id, revision);
        CheckEditable(id, pdf);
        CheckPage(pdf, pageIndex);
        using var page = pdf.Page(pageIndex);
        var box = GetVisibleBox(page.Handle);
        if (right <= left || top <= bottom || !box.Contains(left, bottom, right, top))
            throw new InvalidOperationException("Text box bounds must be a nonempty rectangle inside the selected page.");

        if (box.Rotation == 0) page.AddTextInBox(text, left, bottom, right, top, 4, 12);
        else AddRotatedTextInBox(pdf.Handle, page.Handle, box.Rotation, text, left, bottom, right, top, 4, 12);
        page.Generate();
        var annotation = Pdf.Objects.FPDFPage_CreateAnnot(page.Handle, 5);
        Pdf.Check(annotation != 0, "create text box border");
        try
        {
            var rect = new Pdf.Rect { Left = (float)left, Top = (float)top, Right = (float)right, Bottom = (float)bottom };
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annotation, ref rect), "set text box bounds");
            Pdf.Check(PdfDrawing.SetFlags(annotation, 4), "enable text box border for print");
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(annotation, 0, 51, 23, 139, 255), "set text box border color");
            Pdf.Check(PdfDrawing.SetBorder(annotation, 0, 0, 1), "set text box border");
        }
        finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        documents[id] = (pdf, revision + 1);
    }

    /// <summary>Like Pdf.PageHandle.AddTextInBox, but the text is rotated with a content matrix so it reads upright on a page with /Rotate.</summary>
    static void AddRotatedTextInBox(nint document, nint page, int rotation, string text, double left, double bottom, double right, double top, double padding, double size)
    {
        var obj = Pdf.Native.FPDFPageObj_NewTextObj(document, "Helvetica", (float)size);
        Pdf.Check(obj != 0, "new text object");
        try
        {
            Pdf.Check(Pdf.Native.FPDFText_SetText(obj, text), "set text");
            Pdf.Check(Pdf.Native.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t), "measure text");
            double width = r - l, height = t - b;
            var odd = rotation % 2 == 1;
            double dw = odd ? top - bottom : right - left, dh = odd ? right - left : top - bottom;
            if (!(width > 0) || !(height > 0)) throw new InvalidOperationException("The text has no measurable width. Use printable characters.");
            if (width + 2 * padding > dw || height + 2 * padding > dh)
                throw new InvalidOperationException("The text does not fit the box. Enlarge the box or shorten the text.");
            var (ux, uy, vx, vy) = UprightAxes(rotation);
            var (e, f) = DisplayOrigin(rotation, left, bottom, right, top);
            double ox = padding - l, oy = (dh - height) / 2 - b;
            Pdf.Native.FPDFPageObj_Transform(obj, ux, uy, vx, vy, e + ux * ox + vx * oy, f + uy * ox + vy * oy);
            InsertImageObject(page, obj);
        }
        catch { DestroyImageObject(obj); throw; }
    }
}
