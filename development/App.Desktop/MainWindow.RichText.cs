using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HunterPdf.Core;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace HunterPdf.Desktop;

/// <summary>
/// Editable text boxes (FreeText annotations) and new text in installed fonts, through the companion engine.
/// Without the engine the original fixed-style text box and Helvetica text remain available.
/// </summary>
public partial class MainWindow
{
    const int FreeTextSubtype = 3;
    const double HandleSize = 10, EdgeGrip = 6;

    sealed class FreeTextDrag(Canvas layer, OpenDocumentTab tab, int page, long revision, Rect start, Point origin, bool resize, Rectangle preview)
    {
        public Canvas Layer { get; } = layer;
        public OpenDocumentTab Tab { get; } = tab;
        public int Page { get; } = page;
        public long Revision { get; } = revision;
        public Rect Start { get; } = start;
        public Point Origin { get; } = origin;
        public bool Resize { get; } = resize;
        public Rectangle Preview { get; } = preview;
        public Rect Current { get; set; } = start;
    }

    FreeTextDrag? freeTextDrag;

    internal bool RichTextAvailable => AdvancedEngineInstalled;

    // ---- Adding ----------------------------------------------------------------------------

    async Task AddFreeTextAsync(InlineTextEditor editor, string text)
    {
        var style = TextStyleFor(PageTool.TextBox);
        var b = editor.PdfBounds;
        var result = await editor.Tab.Session.AddFreeTextAsync(AdvancedEngine, new FreeTextOptions
        {
            Page = editor.Page, Left = b.Left, Bottom = b.Top, Right = b.Right, Top = b.Bottom, Text = text,
            Font = style.Font is "Helvetica" or "Times" or "Courier" ? style.Font : "Helvetica", Bold = style.Bold, Italic = style.Italic,
            FontSize = style.FontSize, Color = new FreeTextColor(style.Red, style.Green, style.Blue), Align = style.Align
        }, editor.Revision);
        notice = "Text box added" + (result.Grown ? " (made taller to fit the text)" : "") + ". Click it with Edit text to change it; drag its edge to move it or its corner to resize. Ctrl+Z undoes it.";
    }

    /// <summary>The installed font to use for Add text, or null for the standard 18 pt black Helvetica path.</summary>
    internal InstalledFont? UseEmbeddedFont(string text)
    {
        if (!RichTextAvailable) return null;
        var style = TextStyleFor(PageTool.AddText);
        var standard = style.Font == "Helvetica" && style.FontSize == AddedTextPoints && style.Red == 0 && style.Green == 0 && style.Blue == 0;
        if (standard && text.All(c => c is >= ' ' and <= '~')) return null;
        var fonts = FontCatalog.List().Where(f => f.Embeddable).ToArray();
        if (style.Font.Split('|') is [var path, var face] && int.TryParse(face, out var index) &&
            fonts.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && f.FaceIndex == index) is { } chosen)
            return chosen;
        // Helvetica cannot show these characters or this style, so an installed sans-serif font is embedded instead.
        return fonts.FirstOrDefault(f => f.Family == "Arial" && f.Style == "Regular") ?? fonts.FirstOrDefault(f => f.Family == "Segoe UI" && f.Style == "Regular")
            ?? throw new InvalidOperationException("No installed font that allows embedding was found for this text.");
    }

    async Task AddUnicodeTextAsync(InlineTextEditor editor, string text, InstalledFont font)
    {
        var style = TextStyleFor(PageTool.AddText);
        var b = editor.PdfBounds;
        await editor.Tab.Session.AddUnicodeTextAsync(AdvancedEngine, new UnicodeTextOptions(editor.Page, b.Left,
            Math.Max(0, b.Top - style.FontSize * EditorBaseline), text, font.Path, font.FaceIndex, style.FontSize,
            new FreeTextColor(style.Red, style.Green, style.Blue)), editor.Revision);
        notice = $"Text added in {font.Family} (the font is embedded in the PDF). Ctrl+Z undoes it.";
    }

    // ---- Editing, moving and resizing existing text boxes ---------------------------------------

    /// <summary>With Edit text, a click inside a text box edits its text; a drag on its edge moves it and on its corner resizes it.</summary>
    async Task<bool> FreeTextMouseDownAsync(Canvas layer, OpenDocumentTab tab, int pageIndex, Point position)
    {
        if (!RichTextAvailable || pageIndex >= tab.Pages.Count) return false;
        var revision = tab.Session.Revision;
        var data = toolObjects is { } cached && cached.Tab == tab && cached.Page == pageIndex && cached.Revision == revision
            ? cached.Data : await tab.Session.ExtractAsync(pageIndex);
        if (active != tab || tab.Session.Revision != revision) return true;
        var page = tab.Pages[pageIndex];
        foreach (var annotation in data.GetProperty("Annotations").EnumerateArray().Reverse())
        {
            if (annotation.GetProperty("Subtype").GetInt32() != FreeTextSubtype || !annotation.GetProperty("HasBounds").GetBoolean()) continue;
            var view = PdfRectToView(page, annotation.GetProperty("Left").GetDouble(), annotation.GetProperty("Bottom").GetDouble(),
                annotation.GetProperty("Right").GetDouble(), annotation.GetProperty("Top").GetDouble(), layer.ActualWidth, layer.ActualHeight);
            var grip = view; grip.Inflate(EdgeGrip / 2, EdgeGrip / 2);
            if (!grip.Contains(position)) continue;
            if ((annotation.GetProperty("Flags").GetInt32() & (64 | 128 | 512)) != 0)
            {
                SetToolNotice(tab, "This text box is locked and cannot be changed. The PDF has not changed.");
                return true;
            }
            var corner = position.X >= view.Right - HandleSize && position.Y >= view.Bottom - HandleSize;
            var inner = view; inner.Inflate(-EdgeGrip, -EdgeGrip);
            if (corner || inner.IsEmpty || !inner.Contains(position))
            {
                BeginFreeTextDrag(layer, tab, pageIndex, view, position, corner);
                return true;
            }
            await RunUiAsync(() => BeginFreeTextEditAsync(tab, pageIndex, view, layer), "Opening text box…");
            FocusInlineEditor();
            return true;
        }
        return false;
    }

    async Task<FreeTextInfo?> FindFreeTextAsync(OpenDocumentTab tab, int pageIndex, Rect view, double viewWidth, double viewHeight)
    {
        var page = tab.Pages[pageIndex];
        var boxes = await tab.Session.ListFreeTextAsync(AdvancedEngine, pageIndex);
        return boxes.FirstOrDefault(b => SameView(PdfRectToView(page, b.Left, b.Bottom, b.Right, b.Top, viewWidth, viewHeight), view));
    }

    static bool SameView(Rect a, Rect b) => Math.Abs(a.Left - b.Left) < 1.5 && Math.Abs(a.Top - b.Top) < 1.5 && Math.Abs(a.Right - b.Right) < 1.5 && Math.Abs(a.Bottom - b.Bottom) < 1.5;

    internal async Task BeginFreeTextEditAsync(OpenDocumentTab tab, int pageIndex, Rect view, Canvas layer)
    {
        var revision = tab.Session.Revision;
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        var info = await FindFreeTextAsync(tab, pageIndex, view, layer.ActualWidth, layer.ActualHeight);
        if (info is null || active != tab || tab.Session.Revision != revision) { notice = "The text box changed. Click it again."; return; }
        var scale = layer.ActualWidth / tab.Pages[pageIndex].Width;
        var box = CreateInlineBox(info.Text, info.FontSize * scale, info.Font switch { "Times" => "Times New Roman", "Courier" => "Courier New", _ => "Arial" }, multiline: true);
        box.Width = Math.Max(40, view.Width); box.MinHeight = view.Height;
        box.TextAlignment = info.Align switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left };
        if (info.Bold) box.FontWeight = FontWeights.Bold;
        if (info.Italic) box.FontStyle = FontStyles.Italic;
        box.Foreground = new SolidColorBrush(Color.FromRgb((byte)info.Red, (byte)info.Green, (byte)info.Blue));
        AutomationProperties.SetName(box, "Text box text");
        PlaceInlineBox(layer, box, view.X, view.Y);
        box.SelectAll();
        tab.CurrentPage = pageIndex;
        inlineEditor = new InlineTextEditor(InlineKind.FreeText, tab, pageIndex, revision, layer, box, new Rect(info.Left, info.Bottom, info.Right - info.Left, info.Top - info.Bottom))
        { Original = info.Text, FreeText = info };
        notice = "Change the text, then press Enter (Shift+Enter adds a line). Clear it and press Enter to delete the box. Esc cancels.";
    }

    void BeginFreeTextDrag(Canvas layer, OpenDocumentTab tab, int pageIndex, Rect view, Point origin, bool resize)
    {
        var preview = new Rectangle { Width = view.Width, Height = view.Height, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 2 }, IsHitTestVisible = false, Tag = ToolAdornerTag };
        preview.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FocusBrush");
        Canvas.SetLeft(preview, view.X); Canvas.SetTop(preview, view.Y);
        layer.Children.Add(preview);
        freeTextDrag = new FreeTextDrag(layer, tab, pageIndex, tab.Session.Revision, view, origin, resize, preview);
        layer.Focus(); layer.CaptureMouse();
        SetToolNotice(tab, resize ? "Drag to resize the text box. Esc cancels." : "Drag to move the text box. Esc cancels.");
    }

    bool FreeTextDragMove(Canvas layer, MouseEventArgs e)
    {
        if (freeTextDrag is not { } drag || !ReferenceEquals(layer, drag.Layer)) return false;
        drag.Current = DraggedRect(drag, ClampToLayer(layer, e.GetPosition(layer)));
        drag.Preview.Width = drag.Current.Width; drag.Preview.Height = drag.Current.Height;
        Canvas.SetLeft(drag.Preview, drag.Current.X); Canvas.SetTop(drag.Preview, drag.Current.Y);
        e.Handled = true;
        return true;
    }

    static Rect DraggedRect(FreeTextDrag drag, Point now)
    {
        var dx = now.X - drag.Origin.X; var dy = now.Y - drag.Origin.Y;
        if (drag.Resize) return new Rect(drag.Start.X, drag.Start.Y, Math.Max(16, drag.Start.Width + dx), Math.Max(12, drag.Start.Height + dy));
        var x = Math.Clamp(drag.Start.X + dx, 0, Math.Max(0, drag.Layer.ActualWidth - drag.Start.Width));
        var y = Math.Clamp(drag.Start.Y + dy, 0, Math.Max(0, drag.Layer.ActualHeight - drag.Start.Height));
        return new Rect(x, y, drag.Start.Width, drag.Start.Height);
    }

    void CancelFreeTextDrag()
    {
        if (freeTextDrag is not { } drag) return;
        freeTextDrag = null;
        drag.Layer.Children.Remove(drag.Preview);
        if (drag.Layer.IsMouseCaptured) drag.Layer.ReleaseMouseCapture();
    }

    async Task<bool> FreeTextDragUpAsync(Canvas layer, MouseButtonEventArgs e)
    {
        if (freeTextDrag is not { } drag || !ReferenceEquals(layer, drag.Layer)) return false;
        e.Handled = true;
        drag.Current = DraggedRect(drag, ClampToLayer(layer, e.GetPosition(layer)));
        CancelFreeTextDrag();
        if (SameView(drag.Current, drag.Start))
        {
            // A click on the edge without moving edits the text instead.
            await RunUiAsync(() => BeginFreeTextEditAsync(drag.Tab, drag.Page, drag.Start, layer), "Opening text box…");
            FocusInlineEditor();
            return true;
        }
        await RunUiAsync(() => MoveFreeTextAsync(drag.Tab, drag.Page, drag.Revision, drag.Start, drag.Current, layer.ActualWidth, layer.ActualHeight),
            drag.Resize ? "Resizing text box…" : "Moving text box…");
        return true;
    }

    /// <summary>Moves or resizes the text box shown at <paramref name="from"/> to the view rectangle <paramref name="to"/>.</summary>
    internal async Task MoveFreeTextAsync(OpenDocumentTab tab, int pageIndex, long revision, Rect from, Rect to, double viewWidth, double viewHeight)
    {
        if (active != tab || tab.Session.Revision != revision) { notice = "The document changed. Try again."; return; }
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        var info = await FindFreeTextAsync(tab, pageIndex, from, viewWidth, viewHeight);
        if (info is null) { notice = "The text box changed. Try again."; return; }
        var page = tab.Pages[pageIndex];
        var (x1, y1) = ViewToPdf(page, to.TopLeft, viewWidth, viewHeight);
        var (x2, y2) = ViewToPdf(page, to.BottomRight, viewWidth, viewHeight);
        var result = await tab.Session.UpdateFreeTextAsync(AdvancedEngine, info, new FreeTextOptions
        {
            Left = Math.Min(x1, x2), Bottom = Math.Min(y1, y2), Right = Math.Max(x1, x2), Top = Math.Max(y1, y2)
        }, revision);
        notice = "Text box " + (Math.Abs(to.Width - from.Width) > .5 || Math.Abs(to.Height - from.Height) > .5 ? "resized" : "moved") +
            (result.Grown ? " and made taller to fit its text" : "") + ". Ctrl+Z undoes it.";
    }
}
