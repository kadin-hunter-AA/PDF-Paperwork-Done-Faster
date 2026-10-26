using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HunterPdf.Core;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace HunterPdf.Desktop;

/// <summary>Editing tools that act directly on the rendered page instead of through coordinate dialogs.</summary>
internal enum PageTool { Select, EditText, AddText, Highlight, Underline, Strikeout, Note, Pen, Line, Arrow, Rectangle, Ellipse, TextBox }

public partial class MainWindow
{
    const string ToolAdornerTag = "page-tool-adorner";
    const string ToolHoverTag = "page-tool-hover";
    // The worker's AddText writes 18 pt Helvetica with its baseline at the given point; the on-page
    // editor shows Arial, whose WPF baseline sits .905 em below the top of the line.
    const double AddedTextPoints = 18, EditorBaseline = .905;
    const int MaxPenPoints = 2000;

    PageTool pageTool = PageTool.Select;
    string? toolMode;
    bool syncingTool;
    InlineTextEditor? inlineEditor;
    Canvas? dragLayer;
    int dragPage = -1;
    long dragRevision;
    readonly List<Point> dragPoints = new();
    Shape? dragPreview;
    (OpenDocumentTab Tab, int Page, long Revision, JsonElement Data)? toolObjects;
    bool toolObjectsLoading;

    // MappedReplace edits embedded/subset-font text through the companion engine, keeping the PDF's own font.
    // FreeText edits the text of an existing text-box annotation through the companion engine.
    internal enum InlineKind { Replace, MappedReplace, Add, Note, TextBox, FreeText }

    sealed class InlineTextEditor(InlineKind kind, OpenDocumentTab tab, int page, long revision, Canvas layer, TextBox box, Rect pdfBounds)
    {
        public InlineKind Kind { get; } = kind;
        public OpenDocumentTab Tab { get; } = tab;
        public int Page { get; } = page;
        public long Revision { get; set; } = revision;
        public Canvas Layer { get; } = layer;
        public TextBox Box { get; } = box;
        /// <summary>PDF-space bounds (bottom-left origin): the replaced object, the note/text-box rectangle, or the new text's top-left.</summary>
        public Rect PdfBounds { get; } = pdfBounds;
        public string? ObjectId { get; set; }
        /// <summary>Hash-bound companion-engine selection for MappedReplace.</summary>
        public JsonElement? Selection { get; init; }
        public int ObjectIndex { get; init; } = -1;
        public string Original { get; init; } = "";
        /// <summary>The existing text box being edited (FreeText only), used as the stale-edit guard.</summary>
        public FreeTextInfo? FreeText { get; init; }
    }

    internal PageTool CurrentPageTool => pageTool;
    internal TextBox? InlineEditorBox => inlineEditor?.Box;
    internal bool IsEditMode => EditModeToggle.IsChecked == true;

    void SelectMode_Click(object sender, RoutedEventArgs e) => SetPageTool(PageTool.Select, false);

    void EditMode_Click(object sender, RoutedEventArgs e) =>
        SetPageTool(EditModeToggle.IsChecked == true ? PageTool.EditText : PageTool.Select, EditModeToggle.IsChecked == true);

    void PageTool_Checked(object sender, RoutedEventArgs e)
    {
        if (!syncingTool && sender is RadioButton { Tag: string name } && Enum.TryParse<PageTool>(name, out var tool))
            SetPageTool(tool, true);
    }

    internal void SetPageTool(PageTool tool, bool editMode = true)
    {
        pendingStamp = null;
        handTool = false; HandToolToggle.IsChecked = false;
        ResetPageToolState();
        pageTool = editMode ? tool : PageTool.Select;
        EditModeToggle.IsChecked = editMode;
        SelectModeToggle.IsChecked = !editMode;
        EditToolbar.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;
        syncingTool = true;
        try
        {
            foreach (var button in ToolButtons.Children.OfType<RadioButton>())
                button.IsChecked = button.Tag is string name && name == pageTool.ToString();
        }
        finally { syncingTool = false; }
        // Choosing Select keeps the current task's tools in view; leaving edit mode forgets the task.
        if (!editMode) toolMode = null;
        else if (pageTool != PageTool.Select) toolMode = ModeOf(pageTool);
        ShowModeTools(toolMode ?? "Select");
        ToolHint.Text = pageTool switch
        {
            PageTool.EditText => "Click any outlined text on the page and type to change it. Press Enter or click elsewhere to apply, Esc to cancel. Images open the image editor.",
            PageTool.AddText => "Click where the new text should start, type it, then press Enter. Basic Latin letters, numbers and punctuation.",
            PageTool.Highlight => "Drag across text to highlight it.",
            PageTool.Underline => "Drag across text to underline it.",
            PageTool.Strikeout => "Drag across text to strike it out.",
            PageTool.Note => "Click where the sticky note should go, type the comment, then press Enter (Shift+Enter for a new line).",
            PageTool.Pen => "Hold the mouse button and draw on the page.",
            PageTool.Line => "Drag from where the line starts to where it ends.",
            PageTool.Arrow => "Drag from the tail to where the arrow should point.",
            PageTool.Rectangle or PageTool.Ellipse => "Drag across the page to draw the shape.",
            PageTool.TextBox => "Drag a box on the page, type one line of text, then press Enter.",
            _ => "Drag across text to select it, then press Ctrl+C to copy."
        } + (pageTool == PageTool.Select ? "" : " Every change can be undone with Ctrl+Z.");
        ModeNotice.ToolTip = ModeNotice.Text + Environment.NewLine + ToolHint.Text;
        ApplyPageToolCursor();
        ShowToolStyle();
        // Load the embedded-font text engine while the user picks text, not after the click.
        if (pageTool == PageTool.EditText && AdvancedEngineInstalled) DocumentSession.PrewarmAdvancedEngine(AdvancedEngine);
    }

    static string ModeOf(PageTool tool) => tool switch
    {
        PageTool.EditText or PageTool.AddText or PageTool.TextBox => "Edit content",
        PageTool.Highlight or PageTool.Underline or PageTool.Strikeout or PageTool.Note => "Annotate",
        PageTool.Pen or PageTool.Line or PageTool.Arrow or PageTool.Rectangle or PageTool.Ellipse => "Draw",
        _ => "Select"
    };

    /// <summary>The mode bar shows Select plus the chosen task's tools, with a note on what that task changes.</summary>
    void ShowModeTools(string mode)
    {
        ModeLabel.Text = mode;
        ModeNotice.Text = mode switch
        {
            "Edit content" => "Replacing text does not redact it. Use Redact to remove content permanently.",
            "Annotate" => "Annotations do not remove or hide page content.",
            "Draw" => "Drawings are added as annotations and do not change page content.",
            _ => "The original file stays intact until you save."
        };
        foreach (var (menu, menuMode) in new[] { (ContentMenuButton, "Edit content"), (AnnotateMenuButton, "Annotate"), (DrawMenuButton, "Draw") })
        {
            // The task whose tools are showing is tinted in the menu row, as in the design.
            if (mode == menuMode) { menu.SetResourceReference(BackgroundProperty, "ToolBrush"); menu.SetResourceReference(ForegroundProperty, "AccentBrush"); }
            else { menu.ClearValue(BackgroundProperty); menu.ClearValue(ForegroundProperty); }
        }
        var filtered = mode != "Select";
        var afterSelect = true;
        foreach (UIElement child in ToolButtons.Children)
        {
            var visible = child switch
            {
                RadioButton { Tag: string name } when Enum.TryParse<PageTool>(name, out var tool) => !filtered || tool == PageTool.Select || ModeOf(tool) == mode,
                Rectangle => !filtered || afterSelect,
                Button => !filtered || mode == "Annotate",
                _ => true
            };
            if (child is Rectangle) afterSelect = false;
            child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void ApplyPageToolCursor()
    {
        var cursor = handTool || spacePan ? Cursors.Hand : pageTool switch
        {
            PageTool.Select or PageTool.EditText or PageTool.AddText or PageTool.Highlight or PageTool.Underline or PageTool.Strikeout => Cursors.IBeam,
            PageTool.Note => Cursors.Hand,
            _ => Cursors.Cross
        };
        foreach (var layer in selectionLayers) layer.Cursor = cursor;
    }

    /// <summary>Discards any unfinished on-page edit, drag or hover outline. Nothing is applied.</summary>
    void ResetPageToolState()
    {
        CloseInlineEditor();
        CancelDrag(); CancelFreeTextDrag();
        toolObjects = null;
        foreach (var layer in selectionLayers) { ClearToolAdorners(layer); layer.ToolTip = null; }
    }

    static void ClearToolAdorners(Canvas layer)
    {
        foreach (var child in layer.Children.OfType<FrameworkElement>().Where(c => c.Tag is ToolAdornerTag or ToolHoverTag).ToArray())
            layer.Children.Remove(child);
    }

    void PageTool_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (dragLayer is null && freeTextDrag is null)) return;
        CancelDrag(); CancelFreeTextDrag();
        e.Handled = true;
    }

    async void ManageAnnotations_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        new AnnotationManagerWindow(tab.Session, tab.CurrentPage) { Owner = this }.ShowDialog();
    }, "Opening annotations…");

    bool IsInsideInlineEditor(DependencyObject? element)
    {
        if (inlineEditor is null) return false;
        for (var node = element; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, inlineEditor.Box)) return true;
        return false;
    }

    // ---- Pointer routing -------------------------------------------------------------------

    /// <summary>Returns true when the active tool consumed the press; otherwise ordinary text selection continues.</summary>
    async Task<bool> PageToolMouseDownAsync(Canvas layer, OpenDocumentTab tab, int pageIndex, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(layer);
        if (inlineEditor is not null)
        {
            // A click outside the open editor finishes that edit, like other Windows editors.
            e.Handled = true;
            await CommitInlineEditAsync();
            return true;
        }
        switch (pageTool)
        {
            case PageTool.Select or PageTool.Highlight or PageTool.Underline or PageTool.Strikeout:
                return false;
            case PageTool.EditText:
                e.Handled = true;
                if (await FreeTextMouseDownAsync(layer, tab, pageIndex, position)) return true;
                await RunUiAsync(() => BeginTextEditAtAsync(tab, pageIndex, position, layer.ActualWidth, layer.ActualHeight), "Opening text…");
                FocusInlineEditor();
                return true;
            case PageTool.AddText or PageTool.Note:
                e.Handled = true;
                await RunUiAsync(() => BeginNewTextAsync(tab, pageIndex, pageTool == PageTool.Note ? InlineKind.Note : InlineKind.Add,
                    new Rect(position, position), layer.ActualWidth, layer.ActualHeight), "Preparing text…");
                FocusInlineEditor();
                return true;
            default:
                e.Handled = true;
                if (position.X < 0 || position.Y < 0 || position.X > layer.ActualWidth || position.Y > layer.ActualHeight) return true;
                dragLayer = layer; dragPage = pageIndex; dragRevision = tab.Session.Revision;
                dragPoints.Clear(); dragPoints.Add(position);
                dragPreview = pageTool switch
                {
                    PageTool.Rectangle or PageTool.TextBox => new Rectangle(),
                    PageTool.Ellipse => new Ellipse(),
                    _ => new Polyline { StrokeLineJoin = PenLineJoin.Round }
                };
                dragPreview.StrokeThickness = 2; dragPreview.IsHitTestVisible = false; dragPreview.Tag = ToolAdornerTag;
                dragPreview.SetResourceReference(Shape.StrokeProperty, "FocusBrush");
                layer.Children.Add(dragPreview);
                layer.Focus(); layer.CaptureMouse();
                return true;
        }
    }

    bool PageToolMouseMove(Canvas layer, MouseEventArgs e)
    {
        if (FreeTextDragMove(layer, e)) return true;
        if (dragLayer is not null)
        {
            if (!ReferenceEquals(layer, dragLayer) || !layer.IsMouseCaptured) return true;
            var now = ClampToLayer(layer, e.GetPosition(layer));
            if (pageTool == PageTool.Pen)
            {
                if (dragPoints.Count < MaxPenPoints && (now - dragPoints[^1]).Length >= 1.5) dragPoints.Add(now);
            }
            else if (dragPoints.Count == 1) dragPoints.Add(now);
            else dragPoints[^1] = now;
            UpdateDragPreview();
            e.Handled = true;
            return true;
        }
        if (pageTool == PageTool.EditText && inlineEditor is null && !busy && active is { } tab && layer.Tag is int pageIndex && pageIndex < tab.Pages.Count)
        {
            UpdateTextHover(layer, tab, pageIndex, e.GetPosition(layer));
            return true;
        }
        return false;
    }

    async Task<bool> PageToolMouseUpAsync(Canvas layer, MouseButtonEventArgs e)
    {
        if (await FreeTextDragUpAsync(layer, e)) return true;
        if (dragLayer is null || !ReferenceEquals(layer, dragLayer)) return false;
        e.Handled = true;
        var end = ClampToLayer(layer, e.GetPosition(layer));
        if (pageTool == PageTool.Pen) { if (dragPoints[^1] != end && dragPoints.Count < MaxPenPoints) dragPoints.Add(end); }
        else if (dragPoints.Count == 1) dragPoints.Add(end);
        else dragPoints[^1] = end;
        var points = dragPoints.ToArray();
        var tool = pageTool; var pageIndex = dragPage; var revision = dragRevision;
        var width = layer.ActualWidth; var height = layer.ActualHeight;
        CancelDrag();
        var tab = active;
        if (tab is null || tab.Session.Revision != revision) return true;
        var extent = new Rect(points[0], points[^1]);
        var moved = tool == PageTool.Pen ? points.Zip(points.Skip(1)).Sum(pair => (pair.Second - pair.First).Length) : (points[^1] - points[0]).Length;
        var tooSmall = tool is PageTool.Rectangle or PageTool.Ellipse or PageTool.TextBox ? extent.Width < 4 || extent.Height < 4 : moved < 4;
        if (tooSmall)
        {
            SetToolNotice(tab, tool == PageTool.TextBox ? "Drag a larger box for the text." : "Drag a little farther to draw. Nothing was added.");
            return true;
        }
        if (tool == PageTool.TextBox)
        {
            await RunUiAsync(() => BeginNewTextAsync(tab, pageIndex, InlineKind.TextBox, extent, width, height), "Preparing text box…");
            FocusInlineEditor();
        }
        else await RunUiAsync(() => CommitDrawingAsync(tab, pageIndex, tool, points, width, height), "Adding drawing…");
        return true;
    }

    void CancelDrag()
    {
        if (dragLayer is { } layer)
        {
            if (dragPreview is not null) layer.Children.Remove(dragPreview);
            dragLayer = null;
            if (layer.IsMouseCaptured) layer.ReleaseMouseCapture();
        }
        dragPreview = null; dragPoints.Clear(); dragPage = -1;
    }

    void UpdateDragPreview()
    {
        if (dragPreview is null || dragPoints.Count == 0) return;
        if (dragPreview is Polyline line)
        {
            var points = new PointCollection(dragPoints);
            if (pageTool == PageTool.Arrow && dragPoints.Count >= 2)
                foreach (var wing in ArrowWings(dragPoints[0], dragPoints[^1])) { points.Add(wing); points.Add(dragPoints[^1]); }
            line.Points = points;
            return;
        }
        var bounds = new Rect(dragPoints[0], dragPoints[^1]);
        dragPreview.Width = bounds.Width; dragPreview.Height = bounds.Height;
        Canvas.SetLeft(dragPreview, bounds.Left); Canvas.SetTop(dragPreview, bounds.Top);
    }

    static IEnumerable<Point> ArrowWings(Point from, Point tip)
    {
        var dx = tip.X - from.X; var dy = tip.Y - from.Y; var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length)) yield break;
        var head = Math.Min(14, length * .35); var ux = dx / length; var uy = dy / length;
        foreach (var sin in new[] { .5, -.5 })
            yield return new Point(tip.X - head * (ux * .8660254037844386 - uy * sin), tip.Y - head * (uy * .8660254037844386 + ux * sin));
    }

    static Point ClampToLayer(Canvas layer, Point p) =>
        new(Math.Clamp(p.X, 0, layer.ActualWidth), Math.Clamp(p.Y, 0, layer.ActualHeight));

    void SetToolNotice(OpenDocumentTab tab, string message)
    {
        notice = message;
        UpdateDocumentStatus(tab);
    }

    // ---- Coordinates ------------------------------------------------------------------------

    // View coordinates are top-left origin on the displayed (rotated) page; PDF default user space is unrotated with a
    // bottom-left origin at the visible box (page.OriginX/Y). The mapping is the one verified against FPDF_DeviceToPage.
    static (double X, double Y) ViewToPdf(PageDescriptor page, Point p, double viewWidth, double viewHeight) =>
        PageObjectHitTest.ToPdf(Math.Clamp(p.X, 0, viewWidth), Math.Clamp(p.Y, 0, viewHeight), page.Width, page.Height, viewWidth, viewHeight,
            page.Rotation, page.OriginX, page.OriginY);

    /// <summary>Maps PDF user-space bounds to the viewer, matching the text-selection transform for each page rotation and visible-box origin.</summary>
    static Rect PdfRectToView(PageDescriptor page, double left, double bottom, double right, double top, double viewWidth, double viewHeight)
    {
        left -= page.OriginX; right -= page.OriginX; bottom -= page.OriginY; top -= page.OriginY;
        var sx = viewWidth / page.Width; var sy = viewHeight / page.Height;
        return page.Rotation switch
        {
            1 => new Rect(bottom * sx, left * sy, Math.Max(1, (top - bottom) * sx), Math.Max(1, (right - left) * sy)),
            2 => new Rect((page.Width - right) * sx, bottom * sy, Math.Max(1, (right - left) * sx), Math.Max(1, (top - bottom) * sy)),
            3 => new Rect((page.Width - top) * sx, (page.Height - right) * sy, Math.Max(1, (top - bottom) * sx), Math.Max(1, (right - left) * sy)),
            _ => new Rect(left * sx, (page.Height - top) * sy, Math.Max(1, (right - left) * sx), Math.Max(1, (top - bottom) * sy))
        };
    }

    /// <summary>Converts a rectangle in display points (top-left origin, rotated page) to a normalized user-space Rect whose Top is the lower y (PDF bottom).</summary>
    static Rect DisplayRectToPdf(PageDescriptor page, double x0, double y0, double x1, double y1)
    {
        var a = PageObjectHitTest.ToPdf(x0, y0, page.Width, page.Height, page.Width, page.Height, page.Rotation, page.OriginX, page.OriginY);
        var b = PageObjectHitTest.ToPdf(x1, y1, page.Width, page.Height, page.Width, page.Height, page.Rotation, page.OriginX, page.OriginY);
        return new Rect(new Point(a.X, a.Y), new Point(b.X, b.Y));
    }

    static Rect ObjectBounds(JsonElement item) => new(
        new Point(item.GetProperty("Left").GetDouble(), item.GetProperty("Bottom").GetDouble()),
        new Point(item.GetProperty("Right").GetDouble(), item.GetProperty("Top").GetDouble()));

    // ---- Hover outlines for the Edit text tool ---------------------------------------------

    void UpdateTextHover(Canvas layer, OpenDocumentTab tab, int pageIndex, Point position)
    {
        if (toolObjects is not { } cached || cached.Tab != tab || cached.Page != pageIndex || cached.Revision != tab.Session.Revision)
        {
            _ = LoadToolObjectsAsync(layer, tab, pageIndex);
            return;
        }
        var page = tab.Pages[pageIndex];
        var hover = layer.Children.OfType<Rectangle>().FirstOrDefault(r => ToolHoverTag.Equals(r.Tag));
        JsonElement? hit = null;
        try
        {
            var point = PageObjectHitTest.ToPdf(position.X, position.Y, page.Width, page.Height, layer.ActualWidth, layer.ActualHeight, page.Rotation, page.OriginX, page.OriginY);
            hit = PageObjectHitTest.Find(cached.Data, point.X, point.Y);
        }
        catch (InvalidOperationException) { }
        if (hit is not { } item)
        {
            if (hover is not null) layer.Children.Remove(hover);
            layer.ToolTip = null;
            return;
        }
        if (hover is null)
        {
            hover = new Rectangle { StrokeThickness = 2, IsHitTestVisible = false, Tag = ToolHoverTag, Fill = new SolidColorBrush(Color.FromArgb(28, 60, 135, 235)) };
            layer.Children.Add(hover);
        }
        // Embedded-font text is offered too; the companion engine decides on click and explains any refusal.
        var editable = item.GetProperty("Type").GetInt32() == 3 || item.GetProperty("CanEdit").GetBoolean() || AdvancedEngineInstalled;
        hover.SetResourceReference(Shape.StrokeProperty, editable ? "FocusBrush" : "DisabledBrush");
        var bounds = ObjectBounds(item);
        var view = PdfRectToView(page, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, layer.ActualWidth, layer.ActualHeight);
        view.Inflate(2, 2);
        hover.Width = view.Width; hover.Height = view.Height; Canvas.SetLeft(hover, view.X); Canvas.SetTop(hover, view.Y);
        layer.ToolTip = editable ? null : "This text can't be edited here: " + item.GetProperty("EditReason").GetString();
    }

    async Task LoadToolObjectsAsync(Canvas layer, OpenDocumentTab tab, int pageIndex)
    {
        if (toolObjectsLoading || busy) return;
        toolObjectsLoading = true;
        try
        {
            var revision = tab.Session.Revision;
            var data = await tab.Session.ExtractAsync(pageIndex);
            if (busy || active != tab || tab.Session.Revision != revision || pageTool != PageTool.EditText || inlineEditor is not null ||
                pageIndex >= selectionLayers.Count || !ReferenceEquals(selectionLayers[pageIndex], layer)) return;
            foreach (var other in selectionLayers) ClearToolAdorners(other);
            toolObjects = (tab, pageIndex, revision, data);
            var page = tab.Pages[pageIndex];
            // Faint outlines show which text can be changed before anything is clicked.
            foreach (var item in data.GetProperty("Objects").EnumerateArray())
            {
                if (item.GetProperty("Type").GetInt32() != 1 || !(item.GetProperty("CanEdit").GetBoolean() || AdvancedEngineInstalled) ||
                    !item.TryGetProperty("HasBounds", out var bounded) || !bounded.GetBoolean()) continue;
                var bounds = ObjectBounds(item);
                var view = PdfRectToView(page, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, layer.ActualWidth, layer.ActualHeight);
                view.Inflate(2, 2);
                var outline = new Rectangle { Width = view.Width, Height = view.Height, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false, Tag = ToolAdornerTag, Opacity = .7 };
                outline.SetResourceReference(Shape.StrokeProperty, "FocusBrush");
                Canvas.SetLeft(outline, view.X); Canvas.SetTop(outline, view.Y);
                layer.Children.Add(outline);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or System.IO.IOException) { }
        finally { toolObjectsLoading = false; }
    }

    // ---- Existing text, edited in place -----------------------------------------------------

    /// <summary>Opens an on-page editor over the clicked text object. Images and unsupported pages use the existing editors.</summary>
    internal async Task BeginTextEditAtAsync(OpenDocumentTab tab, int pageIndex, Point position, double viewWidth, double viewHeight)
    {
        if (active != tab || pageIndex < 0 || pageIndex >= tab.Pages.Count || pageIndex >= selectionLayers.Count) return;
        var page = tab.Pages[pageIndex];
        var session = tab.Session;
        var revision = session.Revision;
        var currentPage = tab.CurrentPage;
        if (page.Rotation != 0)
        {
            // In-place text boxes are laid out for upright text; rotated pages keep the reviewed editor window (its hit-test is origin-aware).
            await EditObjectAtAsync(tab, pageIndex, position, viewWidth, viewHeight);
            return;
        }
        var point = PageObjectHitTest.ToPdf(position.X, position.Y, page.Width, page.Height, viewWidth, viewHeight, page.Rotation, page.OriginX, page.OriginY);
        var data = await session.ExtractAsync(pageIndex);
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document changed while selecting. Click the text again.";
            return;
        }
        var hit = PageObjectHitTest.Find(data, point.X, point.Y);
        if (hit is null) { notice = "There is no editable text at that spot. To place new text, choose Add text."; return; }
        if (hit.Value.GetProperty("Type").GetInt32() == 3)
        {
            await EditObjectAtAsync(tab, pageIndex, position, viewWidth, viewHeight);
            return;
        }
        if (!hit.Value.GetProperty("CanEdit").GetBoolean())
        {
            // The built-in editor handles only standard PDF fonts; most real documents embed their own.
            if (AdvancedEngineInstalled) await OpenMappedEditorAsync(tab, pageIndex, page, currentPage, revision, data, hit.Value);
            else notice = "This text can't be edited here: " + hit.Value.GetProperty("EditReason").GetString();
            return;
        }
        if (!await AllowEditingAsync(tab)) return;
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document or page changed while authorizing. Click the text again.";
            return;
        }
        // Signed-copy authorization can reload the worker and replace object IDs; resolve the hit again.
        data = await session.ExtractAsync(pageIndex);
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision)) return;
        hit = PageObjectHitTest.Find(data, point.X, point.Y);
        if (hit is not { } item || item.GetProperty("Type").GetInt32() != 1 || !item.GetProperty("CanEdit").GetBoolean())
        {
            notice = "The text changed while opening. Click it again.";
            return;
        }
        tab.CurrentPage = pageIndex;
        OpenReplaceEditor(tab, pageIndex, revision, item);
    }

    /// <summary>Matches the clicked object to the companion engine's text run and opens the same on-page editor for it.</summary>
    async Task OpenMappedEditorAsync(OpenDocumentTab tab, int pageIndex, PageDescriptor page, int currentPage, long revision, JsonElement data, JsonElement hit)
    {
        var session = tab.Session;
        var text = hit.GetProperty("Text").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) { notice = "This text can't be read, so it can't be edited here."; return; }
        // Identical runs are told apart by order: both engines read page-level text in content order.
        var wanted = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hitIndex = ParseObjectIndex(hit.GetProperty("Id").GetString());
        var occurrence = data.GetProperty("Objects").EnumerateArray().Count(o => o.GetProperty("Type").GetInt32() == 1 &&
            ParseObjectIndex(o.GetProperty("Id").GetString()) < hitIndex &&
            string.Join(' ', (o.GetProperty("Text").GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) == wanted);
        JsonElement found;
        try { found = await session.FindStructuredTextAsync(AdvancedEngine, pageIndex, text, occurrence, revision); }
        catch (InvalidOperationException error) { notice = FriendlyTextRefusal(error.Message); return; }
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document changed while opening the text. Click it again.";
            return;
        }
        var entry = found.GetProperty("Entry");
        if (!entry.GetProperty("CanEdit").GetBoolean())
        {
            notice = FriendlyTextRefusal(entry.GetProperty("Reason").GetString() ?? "");
            return;
        }
        var selection = JsonSerializer.SerializeToElement(new
        {
            SourceSHA256 = found.GetProperty("SourceSHA256").GetString(),
            FormPath = entry.GetProperty("FormPath"),
            TextIndex = entry.GetProperty("TextIndex").GetInt32(),
            Text = entry.GetProperty("Text").GetString()
        });
        tab.CurrentPage = pageIndex;
        OpenReplaceEditor(tab, pageIndex, revision, hit, selection, entry.GetProperty("Text").GetString() ?? "");
    }

    static string FriendlyTextRefusal(string message) =>
        message.Contains("absent or ambiguous glyphs", StringComparison.Ordinal)
            ? "The font inside this PDF doesn't include some of the characters you typed. PDFs often carry only the letters the document already uses, so try wording made of letters that already appear in this text."
        : message.Contains("wider than the selected text run", StringComparison.Ordinal)
            ? "The new text is wider than the original and may overlap what follows it. Press Ctrl+Enter to apply anyway, or shorten it."
        : message.Contains("could not be matched", StringComparison.Ordinal)
            ? "This text couldn't be matched safely for editing. Try Properties → Precise placement and lists → Text object list."
        : message.Contains("for nested text", StringComparison.Ordinal) || message.Contains("Nested text in tagged", StringComparison.Ordinal)
            ? "This text sits inside an embedded graphic in a tagged PDF and can't be edited safely yet."
        : "This text can't be edited here: " + message;

    void OpenReplaceEditor(OpenDocumentTab tab, int pageIndex, long revision, JsonElement item, JsonElement? mappedSelection = null, string? mappedText = null)
    {
        var page = tab.Pages[pageIndex];
        var layer = selectionLayers[pageIndex];
        var bounds = ObjectBounds(item);
        var view = PdfRectToView(page, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, layer.ActualWidth, layer.ActualHeight);
        var scale = layer.ActualWidth / page.Width;
        var text = mappedText ?? item.GetProperty("Text").GetString() ?? "";
        // FontSize is reported in text space; when a text matrix scales it, fall back to the object's height.
        var points = item.TryGetProperty("FontSize", out var size) ? size.GetDouble() : 0;
        if (!(points > 0) || points * 1.8 < bounds.Height || points > bounds.Height * 3) points = Math.Max(4, bounds.Height / 1.1);
        var fontName = item.TryGetProperty("FontName", out var font) ? font.GetString() ?? "" : "";
        var box = CreateInlineBox(text, points * scale, "Arial", multiline: false);
        ApplyPdfFontLook(box, fontName);
        box.MinWidth = view.Width + 8; box.MinHeight = view.Height + 4;
        AutomationProperties.SetName(box, "Edit text on page");
        var lineHeight = points * scale * 1.15;
        PlaceInlineBox(layer, box, view.X - 3, view.Y - Math.Max(2, (lineHeight - view.Height) / 2 + 2));
        inlineEditor = new InlineTextEditor(mappedSelection is null ? InlineKind.Replace : InlineKind.MappedReplace, tab, pageIndex, revision, layer, box, bounds)
        {
            ObjectId = item.GetProperty("Id").GetString(),
            ObjectIndex = ParseObjectIndex(item.GetProperty("Id").GetString()),
            Original = text,
            Selection = mappedSelection
        };
        box.SelectAll();
        notice = "Type the new text. Press Enter or click elsewhere to apply, Esc to cancel.";
    }

    /// <summary>Approximates the PDF font on screen, e.g. "ABCDEF+Calibri-Light" → Calibri Light.</summary>
    static void ApplyPdfFontLook(TextBox box, string pdfFontName)
    {
        var name = pdfFontName.TrimStart('/');
        var plus = name.IndexOf('+');
        if (plus is > 0 and <= 7) name = name[(plus + 1)..];
        var parts = name.Split(new[] { '-', ',' }, 2);
        var family = System.Text.RegularExpressions.Regex.Replace(parts[0].Replace("PSMT", "").Replace("MT", ""), "(?<=[a-z])(?=[A-Z])", " ");
        var style = parts.Length > 1 ? parts[1] : "";
        if (family.Contains("Times", StringComparison.OrdinalIgnoreCase)) family = "Times New Roman";
        else if (family.Contains("Courier", StringComparison.OrdinalIgnoreCase)) family = "Courier New";
        else if (family.Contains("Helvetica", StringComparison.OrdinalIgnoreCase)) family = "Arial";
        var weightless = System.Text.RegularExpressions.Regex.Replace(style, "(?i)bold|italic|oblique", "").Trim();
        var candidates = new List<string>();
        if (weightless.Length > 0) candidates.Add(family + " " + System.Text.RegularExpressions.Regex.Replace(weightless, "(?<=[a-z])(?=[A-Z])", " "));
        candidates.Add(family);
        candidates.Add("Arial");
        box.FontFamily = new FontFamily(string.Join(", ", candidates.Where(c => c.Length > 0).Distinct()));
        if (style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Bold", StringComparison.OrdinalIgnoreCase)) box.FontWeight = FontWeights.Bold;
        if (style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase)) box.FontStyle = FontStyles.Italic;
    }

    // ---- New text, notes and text boxes -----------------------------------------------------

    /// <summary>Opens an on-page editor for new text at a click, a sticky note, or inside a dragged text box.</summary>
    internal async Task BeginNewTextAsync(OpenDocumentTab tab, int pageIndex, InlineKind kind, Rect viewBounds, double viewWidth, double viewHeight)
    {
        if (active != tab || pageIndex < 0 || pageIndex >= tab.Pages.Count || pageIndex >= selectionLayers.Count) return;
        var page = tab.Pages[pageIndex];
        var revision = tab.Session.Revision;
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        var layer = selectionLayers[pageIndex];
        var scale = viewWidth / page.Width;
        var (left, top) = ViewToPdf(page, viewBounds.TopLeft, viewWidth, viewHeight);
        var (right, bottom) = ViewToPdf(page, viewBounds.BottomRight, viewWidth, viewHeight);
        TextBox box;
        Rect pdf;
        switch (kind)
        {
            case InlineKind.Add:
                box = CreateInlineBox("", (RichTextAvailable ? TextStyleFor(PageTool.AddText).FontSize : AddedTextPoints) * scale, "Arial", multiline: false);
                box.MinWidth = 120;
                AutomationProperties.SetName(box, "New text on page");
                PlaceInlineBox(layer, box, viewBounds.X - 2, viewBounds.Y - 2);
                // The editor's top-left is the top of the text in the displayed page; the baseline sits below it there. On a rotated
                // page that offset is turned into user space, and commit subtracts the same offset along y.
                var baseline = ViewToPdf(page, new Point(viewBounds.X, viewBounds.Y + AddedTextPoints * EditorBaseline * scale), viewWidth, viewHeight);
                pdf = new Rect(baseline.X, baseline.Y + AddedTextPoints * EditorBaseline, 0, 0);
                notice = "Type the new text, then press Enter. Esc cancels.";
                break;
            case InlineKind.Note:
                box = CreateInlineBox("", Math.Max(13, 11 * scale), "Segoe UI", multiline: true);
                box.Width = 240; box.MinHeight = 64;
                box.Background = new SolidColorBrush(Color.FromRgb(255, 246, 196));
                AutomationProperties.SetName(box, "Sticky note comment");
                PlaceInlineBox(layer, box, Math.Min(viewBounds.X, Math.Max(0, viewWidth - 244)), viewBounds.Y);
                // The note icon is 20 pt square with its top-left at the click, kept on the page.
                // Corners are mapped through the page rotation, so the square stays 20 pt in the displayed page.
                var noteX = Math.Clamp(viewBounds.X / scale, 0, Math.Max(0, page.Width - 20));
                var noteY = Math.Clamp(viewBounds.Y / scale, 0, Math.Max(0, page.Height - 20));
                pdf = DisplayRectToPdf(page, noteX, noteY, noteX + 20, noteY + 20);
                notice = "Type the note, then press Enter (Shift+Enter adds a line). Esc cancels.";
                break;
            default:
                var rich = RichTextAvailable;
                box = CreateInlineBox("", (rich ? TextStyleFor(PageTool.TextBox).FontSize : 12) * scale, "Arial", multiline: rich);
                box.Width = viewBounds.Width; box.MinHeight = viewBounds.Height;
                box.VerticalContentAlignment = rich ? VerticalAlignment.Top : VerticalAlignment.Center;
                AutomationProperties.SetName(box, "Text box text");
                PlaceInlineBox(layer, box, viewBounds.X, viewBounds.Y);
                pdf = new Rect(new Point(left, bottom), new Point(right, top));
                notice = rich ? "Type the text box text, then press Enter (Shift+Enter adds a line). Esc cancels."
                    : "Type one line for the text box, then press Enter. Esc cancels.";
                break;
        }
        tab.CurrentPage = pageIndex;
        inlineEditor = new InlineTextEditor(kind, tab, pageIndex, revision, layer, box, pdf);
    }

    /// <summary>Checks page support and signed-copy consent before a new annotation is drawn or typed.</summary>
    async Task<bool> PrepareDirectAnnotationAsync(OpenDocumentTab tab, int pageIndex, long revision)
    {
        var page = tab.Pages[pageIndex];
        await tab.Session.ExtractAsync(pageIndex);
        if (active != tab || tab.Session.Revision != revision || pageIndex >= tab.Pages.Count || !ReferenceEquals(tab.Pages[pageIndex], page))
        {
            notice = "The document changed. Try again.";
            return false;
        }
        if (!await AllowEditingAsync(tab)) return false;
        if (active != tab || tab.Session.Revision != revision)
        {
            notice = "The document changed while authorizing. Try again.";
            return false;
        }
        return true;
    }

    TextBox CreateInlineBox(string text, double fontSize, string family, bool multiline)
    {
        // Style is cleared so the shell's 44 px form-field padding does not displace text on the page.
        var box = new TextBox
        {
            Style = null, Text = text, FontSize = Math.Max(6, fontSize), FontFamily = new FontFamily(family),
            Background = Brushes.White, Foreground = Brushes.Black, CaretBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5), Padding = new Thickness(1, 0, 1, 0), MinHeight = 0,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        box.SetResourceReference(Control.BorderBrushProperty, "FocusBrush");
        box.PreviewKeyDown += InlineBox_PreviewKeyDown;
        return box;
    }

    static void PlaceInlineBox(Canvas layer, TextBox box, double x, double y)
    {
        Canvas.SetLeft(box, Math.Max(0, x)); Canvas.SetTop(box, Math.Max(0, y));
        Panel.SetZIndex(box, 10);
        layer.Children.Add(box);
    }

    void FocusInlineEditor()
    {
        if (inlineEditor?.Box is not { } box) return;
        // Run after the shell finishes refreshing so panel focus changes cannot take the caret away.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(inlineEditor?.Box, box)) return;
            box.Focus(); Keyboard.Focus(box);
            if (box.SelectionLength == 0) box.CaretIndex = box.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    async void InlineBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (inlineEditor is not { } editor || !ReferenceEquals(sender, editor.Box)) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseInlineEditor();
            notice = "Edit canceled. The PDF has not changed.";
            UpdateDocumentStatus(editor.Tab);
        }
        else if (e.Key == Key.Enter && !(editor.Box.AcceptsReturn && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            await CommitInlineEditAsync(allowWider: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    void CloseInlineEditor()
    {
        if (inlineEditor is not { } editor) return;
        inlineEditor = null;
        editor.Box.PreviewKeyDown -= InlineBox_PreviewKeyDown;
        editor.Layer.Children.Remove(editor.Box);
    }

    /// <summary>Applies the open on-page editor as one undoable change, or closes it when nothing changed.</summary>
    /// <param name="allowWider">For embedded-font text, accept a replacement wider than the original run (it may overlap what follows).</param>
    internal async Task CommitInlineEditAsync(bool allowWider = false)
    {
        if (inlineEditor is not { } editor || busy) return;
        var text = editor.Box.Text;
        var tab = editor.Tab;
        if (editor.Kind is InlineKind.Replace or InlineKind.MappedReplace or InlineKind.FreeText ? text == editor.Original : string.IsNullOrWhiteSpace(text))
        {
            CloseInlineEditor();
            SetToolNotice(tab, "No change was made.");
            return;
        }
        if (editor.Kind is InlineKind.Add or InlineKind.TextBox && !RichTextAvailable && text.Any(c => c < 32 || c > 126))
        {
            SetToolNotice(tab, "New text currently supports basic Latin letters, numbers and punctuation only.");
            FocusInlineEditor();
            return;
        }
        if (editor.Kind == InlineKind.TextBox && !RichTextAvailable && text.Length > 200)
        {
            SetToolNotice(tab, "A text box holds up to 200 characters.");
            FocusInlineEditor();
            return;
        }
        await RunUiAsync(async () =>
        {
            if (!ReferenceEquals(inlineEditor, editor)) return;
            var session = tab.Session;
            if (active != tab || session.Revision != editor.Revision)
            {
                CloseInlineEditor();
                notice = "The document changed while editing, so nothing was applied. Try again.";
                return;
            }
            var b = editor.PdfBounds;
            try
            {
                switch (editor.Kind)
                {
                    case InlineKind.Replace:
                        await session.ReplaceTextAsync(editor.Page, editor.ObjectId!, text);
                        notice = "Text updated. Ctrl+Z undoes it.";
                        break;
                    case InlineKind.MappedReplace:
                        // Built and natively validated in one external-edit commit. There is no separate preview:
                        // the committed page is shown at once and Undo restores it.
                        await session.ApplyStructuredTextAsync(AdvancedEngine, editor.Page, editor.Selection!.Value, text, allowWider, editor.Revision);
                        notice = "Text updated in the PDF's own font. Ctrl+Z undoes it.";
                        break;
                    case InlineKind.Add when UseEmbeddedFont(text) is { } font:
                        await AddUnicodeTextAsync(editor, text, font);
                        break;
                    case InlineKind.Add:
                        // The editor's top-left marks the top of the text; the worker positions by baseline.
                        await session.AddTextAsync(editor.Page, text, b.Left, Math.Max(0, b.Top - AddedTextPoints * EditorBaseline));
                        notice = "Text added. Ctrl+Z undoes it.";
                        break;
                    case InlineKind.TextBox when RichTextAvailable:
                        await AddFreeTextAsync(editor, text);
                        break;
                    case InlineKind.FreeText when string.IsNullOrWhiteSpace(text):
                        await session.DeleteFreeTextAsync(AdvancedEngine, editor.FreeText!, editor.Revision);
                        notice = "Text box deleted. Ctrl+Z restores it.";
                        break;
                    case InlineKind.FreeText:
                        await session.UpdateFreeTextAsync(AdvancedEngine, editor.FreeText!, new FreeTextOptions { Text = text }, editor.Revision);
                        notice = "Text box updated. Ctrl+Z undoes it.";
                        break;
                    case InlineKind.Note:
                        await session.AddCommentAsync(editor.Page, text, b.Left, b.Top, b.Right, b.Bottom);
                        notice = "Sticky note added. Ctrl+Z undoes it.";
                        break;
                    default:
                        await session.AddTextBoxAsync(editor.Page, text, b.Left, b.Top, b.Right, b.Bottom);
                        notice = "Text box added. Ctrl+Z undoes it.";
                        break;
                }
                CloseInlineEditor();
            }
            catch (Exception error) when (error is InvalidOperationException || (error is ArgumentException && editor.Kind is InlineKind.MappedReplace or InlineKind.FreeText or InlineKind.TextBox or InlineKind.Add))
            {
                // The draft stays open so the user can adjust it; the document is unchanged.
                notice = editor.Kind == InlineKind.MappedReplace ? FriendlyTextRefusal(error.Message) : error.Message;
                if (editor.Kind == InlineKind.Replace) await RefreshReplaceTargetAsync(editor);
            }
        }, "Applying edit…");
        FocusInlineEditor();
    }

    // A rejected edit restarts the worker, which issues new object IDs. Keep the draft only
    // when the same object is identified again by index and bounds.
    async Task RefreshReplaceTargetAsync(InlineTextEditor editor)
    {
        try
        {
            var data = await editor.Tab.Session.ExtractAsync(editor.Page);
            var match = data.GetProperty("Objects").EnumerateArray().FirstOrDefault(o =>
                ParseObjectIndex(o.GetProperty("Id").GetString()) == editor.ObjectIndex && o.GetProperty("Type").GetInt32() == 1 &&
                o.TryGetProperty("HasBounds", out var bounded) && bounded.GetBoolean() && SameBounds(ObjectBounds(o), editor.PdfBounds));
            if (match.ValueKind == JsonValueKind.Object && ReferenceEquals(inlineEditor, editor))
            {
                editor.ObjectId = match.GetProperty("Id").GetString();
                editor.Revision = editor.Tab.Session.Revision;
                return;
            }
        }
        catch (InvalidOperationException) { }
        CloseInlineEditor();
        notice += " Click the text again to retry.";
    }

    static bool SameBounds(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) < .01 && Math.Abs(a.Top - b.Top) < .01 && Math.Abs(a.Right - b.Right) < .01 && Math.Abs(a.Bottom - b.Bottom) < .01;

    static int ParseObjectIndex(string? id) =>
        int.TryParse(id?.Split(':').LastOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) ? index : -1;

    // ---- Drawings and markup ---------------------------------------------------------------

    /// <summary>Adds a pen stroke, line, arrow, rectangle or ellipse from points dragged on the page view.</summary>
    internal async Task CommitDrawingAsync(OpenDocumentTab tab, int pageIndex, PageTool tool, IReadOnlyList<Point> viewPoints, double viewWidth, double viewHeight)
    {
        if (active != tab || pageIndex < 0 || pageIndex >= tab.Pages.Count || viewPoints.Count < 2) return;
        var revision = tab.Session.Revision;
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        var page = tab.Pages[pageIndex];
        var pdf = viewPoints.Select(p => ViewToPdf(page, p, viewWidth, viewHeight)).ToArray();
        try
        {
            switch (tool)
            {
                // A remembered color and width for the tool is applied; otherwise the default style is used.
                case PageTool.Pen or PageTool.Line or PageTool.Arrow:
                    var points = tool == PageTool.Pen ? pdf : new[] { pdf[0], pdf[^1] };
                    await (StrokeFor(tool) is { } inkStroke ? tab.Session.AddInkAsync(pageIndex, tool.ToString(), points, inkStroke)
                        : tab.Session.AddInkAsync(pageIndex, tool.ToString(), points));
                    break;
                case PageTool.Rectangle or PageTool.Ellipse:
                    double l = Math.Min(pdf[0].X, pdf[^1].X), b = Math.Min(pdf[0].Y, pdf[^1].Y), r = Math.Max(pdf[0].X, pdf[^1].X), t = Math.Max(pdf[0].Y, pdf[^1].Y);
                    await (StrokeFor(tool) is { } shapeStroke ? tab.Session.AddDrawingAsync(pageIndex, tool.ToString(), l, b, r, t, shapeStroke)
                        : tab.Session.AddDrawingAsync(pageIndex, tool.ToString(), l, b, r, t));
                    break;
                default:
                    return;
            }
            tab.CurrentPage = pageIndex;
            notice = (tool == PageTool.Pen ? "Pen stroke" : tool.ToString()) + " added. Ctrl+Z undoes it; More… moves or restyles it.";
        }
        catch (InvalidOperationException error) { notice = error.Message; }
    }

    async Task ApplyMarkupToolAsync(Canvas layer)
    {
        if (pageTool is not (PageTool.Highlight or PageTool.Underline or PageTool.Strikeout) || active is not { } tab ||
            layer.Tag is not int pageIndex || pageIndex != selectionPage || selectionAnchor < 0 || selectionEnd < 0) return;
        var kind = pageTool.ToString();
        await RunUiAsync(() => ApplyMarkupToSelectionAsync(tab, pageIndex, kind), "Adding " + kind.ToLowerInvariant() + "…");
    }

    /// <summary>Marks the selected characters, merged into one rectangle per line.</summary>
    internal async Task ApplyMarkupToSelectionAsync(OpenDocumentTab tab, int pageIndex, string kind)
    {
        if (active != tab || pageIndex >= tab.Pages.Count || tab.Pages[pageIndex].Characters is not { } characters) return;
        var from = Math.Min(selectionAnchor, selectionEnd); var to = Math.Max(selectionAnchor, selectionEnd);
        var lines = new List<Rect>();
        foreach (var c in characters.Where(c => c.Index >= from && c.Index <= to && c.HasBounds && c.Right > c.Left && c.Top > c.Bottom && !string.IsNullOrWhiteSpace(c.Text)))
        {
            var rect = new Rect(new Point(c.Left, c.Bottom), new Point(c.Right, c.Top));
            if (lines.Count > 0)
            {
                var last = lines[^1];
                var overlap = Math.Min(last.Bottom, rect.Bottom) - Math.Max(last.Top, rect.Top);
                if (overlap > Math.Min(last.Height, rect.Height) * .5 && rect.Left >= last.Left - 1) { last.Union(rect); lines[^1] = last; continue; }
            }
            lines.Add(rect);
        }
        if (lines.Count == 0) { notice = "Drag across text characters to mark them."; return; }
        if (lines.Count > 100) { notice = "Select up to 100 lines at a time."; return; }
        var revision = tab.Session.Revision;
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        try
        {
            // Rect here holds PDF space: Top is the lower y and Bottom the higher y.
            await tab.Session.AddMarkupAsync(pageIndex, kind, lines.Select(r => (object)new { Left = r.Left, Bottom = r.Top, Right = r.Right, Top = r.Bottom }).ToArray());
            selectionAnchor = selectionEnd = selectionPage = -1; selectedText = "";
            tab.CurrentPage = pageIndex;
            notice = kind + " added. Ctrl+Z undoes it.";
        }
        catch (InvalidOperationException error) { notice = error.Message; }
    }
}
