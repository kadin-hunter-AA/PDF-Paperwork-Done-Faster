using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>Selects drawing bounds on an unchanged, rendered page; publication stays in DrawingWindow.</summary>
internal sealed class DrawingPlacementWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    readonly string kind;
    readonly long revision;
    readonly CancellationTokenSource cancellation = new();
    readonly Grid pageSurface = new();
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Canvas overlay = new() { Background = Brushes.Transparent, Focusable = true };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    readonly Button accept = new() { Content = "Use these bounds", MinHeight = 44, IsEnabled = false, IsDefault = true };
    Point? anchor;
    Rect? selection;
    bool ready;
    readonly List<Point> penPoints = new();
    List<(double X, double Y)>? stroke;
    bool IsStroke => kind is "Pen" or "Line" or "Arrow";
    public bool HasStroke => stroke is not null;
    public Rect Bounds => selection ?? throw new InvalidOperationException("Select an area on the page first.");
    /// <summary>PDF-space stroke points for Pen, or the dragged start and end (direction preserved) for Line and Arrow.</summary>
    public IReadOnlyList<(double X, double Y)> Stroke => stroke ?? throw new InvalidOperationException("Draw on the page first.");

    public DrawingPlacementWindow(DocumentSession session, int page, string kind)
    {
        this.session = session; this.page = page; this.kind = kind; revision = session.Revision;
        Title = $"Place {kind.ToLowerInvariant()} · page {page + 1}";
        Width = 820; Height = 760; MinWidth = 480; MinHeight = 420; FontSize = 16;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new TextBlock
        {
            Text = "Drag over the page to choose bounds. Review the outline, then use these bounds in the drawing editor. " +
                (kind == "Pen" ? "Hold the mouse button and draw a freehand stroke." :
                 kind is "Line" or "Arrow" ? $"The {kind.ToLowerInvariant()} runs from where you press to where you release, in any direction." : "The shape fits the selected area."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "Cancel", MinHeight = 44, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        footer.Children.Add(buttons);
        pageSurface.Children.Add(image); pageSurface.Children.Add(overlay);
        var viewport = new Border { Child = new Viewbox { Child = pageSurface, Stretch = Stretch.Uniform }, Padding = new Thickness(8), ClipToBounds = true };
        viewport.SetResourceReference(BackgroundProperty, "CanvasBrush"); root.Children.Add(viewport);
        System.Windows.Automation.AutomationProperties.SetName(overlay, "Drawing bounds on rendered PDF page");
        System.Windows.Automation.AutomationProperties.SetHelpText(overlay, "Drag to choose an area. Coordinate fields remain available in the drawing editor.");
        System.Windows.Automation.AutomationProperties.SetName(image, "Unchanged PDF page for drawing placement");
        System.Windows.Automation.AutomationProperties.SetName(accept, "Use drawing bounds from page");
        System.Windows.Automation.AutomationProperties.SetName(status, "Drawing placement status");
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (!ready) return;
            var point = e.GetPosition(overlay);
            if (!Inside(point)) return;
            anchor = point; penPoints.Clear(); penPoints.Add(point); overlay.Focus(); overlay.CaptureMouse(); e.Handled = true;
        };
        overlay.MouseMove += (_, e) =>
        {
            if (anchor is not Point start || !overlay.IsMouseCaptured) return;
            var now = Clamp(e.GetPosition(overlay));
            if (kind == "Pen")
            {
                if (penPoints.Count < MaxPenPoints && (now - penPoints[^1]).Length >= 1.5) penPoints.Add(now);
                DrawStroke(penPoints);
            }
            else if (IsStroke) DrawStroke(new[] { start, now });
            else Draw(new Rect(start, now));
            e.Handled = true;
        };
        overlay.MouseLeftButtonUp += (_, e) =>
        {
            if (anchor is not Point start || !overlay.IsMouseCaptured) return;
            var end = Clamp(e.GetPosition(overlay));
            anchor = null; overlay.ReleaseMouseCapture();
            if (kind == "Pen") { if (penPoints.Count > 0 && penPoints[^1] != end && penPoints.Count < MaxPenPoints) penPoints.Add(end); SelectStroke(penPoints.ToArray()); }
            else if (IsStroke) SelectStroke(new[] { start, end });
            else SelectBounds(new Rect(start, end));
            e.Handled = true;
        };
        overlay.LostMouseCapture += (_, _) => { if (anchor is not null) { anchor = null; RestoreOutline(); } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || anchor is null) return;
            anchor = null; overlay.ReleaseMouseCapture(); RestoreOutline(); e.Handled = true;
        };
        accept.Click += (_, _) =>
        {
            if (session.Revision != revision) { ready = false; accept.IsEnabled = false; status.Text = "The document changed. Close this preview and place the drawing again."; return; }
            if (selection is not null) DialogResult = true;
        };
        Loaded += async (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
            await LoadPageAsync();
        };
        Closed += (_, _) => { cancellation.Cancel(); overlay.ReleaseMouseCapture(); };
    }

    async Task LoadPageAsync()
    {
        status.Text = "Loading the unchanged page…";
        try
        {
            var data = await session.ExtractAsync(page, cancellation.Token);
            if (data.GetProperty("Rotation").GetInt32() != 0)
                throw new InvalidOperationException("Drawing placement currently supports unrotated pages only.");
            if (!data.TryGetProperty("SupportsZeroOriginCoordinates", out var supported) || !supported.GetBoolean())
                throw new InvalidOperationException("Drawing placement does not support this page's offset boundaries. The PDF has not changed.");
            var width = data.GetProperty("Width").GetDouble(); var height = data.GetProperty("Height").GetDouble();
            var scale = Math.Min(1200 / width, Math.Min(1200 / height, Math.Sqrt(1_500_000 / (width * height))));
            var pixels = await session.RenderAsync(page, Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            if (session.Revision != revision) { status.Text = "The document changed. Close this preview and place the drawing again."; return; }
            var bitmap = BitmapSource.Create(pixels.GetProperty("Width").GetInt32(), pixels.GetProperty("Height").GetInt32(),
                96, 96, PixelFormats.Bgr32, null, Convert.FromBase64String(pixels.GetProperty("Bgra").GetString()!), pixels.GetProperty("Stride").GetInt32());
            bitmap.Freeze(); image.Source = bitmap;
            pageSurface.Width = overlay.Width = width; pageSurface.Height = overlay.Height = height;
            ready = true; status.Text = "Drag on the page. Nothing is added until you apply it in the drawing editor.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { status.Text = error.Message; }
    }

    bool Inside(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.Y >= 0 && p.X <= overlay.Width && p.Y <= overlay.Height;
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, overlay.Width), Math.Clamp(p.Y, 0, overlay.Height));

    // WPF positions are relative to the fixed page surface, so Viewbox zoom and letterboxing do not change PDF units.
    internal bool SelectBounds(Rect viewBounds)
    {
        if (!ready || session.Revision != revision || viewBounds.IsEmpty || !Inside(viewBounds.TopLeft) || !Inside(viewBounds.BottomRight) || viewBounds.Width < .5 || viewBounds.Height < .5)
        { RestoreOutline(); return false; }
        stroke = null;
        selection = new Rect(viewBounds.Left, overlay.Height - viewBounds.Bottom, viewBounds.Width, viewBounds.Height);
        Draw(viewBounds); accept.IsEnabled = true;
        var value = selection.Value;
        status.Text = $"PDF bounds: {value.Left:0.##}, {value.Top:0.##} to {value.Right:0.##}, {value.Bottom:0.##} pt. Review before applying.";
        return true;
    }

    const int MaxPenPoints = 2000;

    // View points are top-left origin; the stroke is stored in PDF space (bottom-left origin).
    internal bool SelectStroke(IReadOnlyList<Point> viewPoints)
    {
        if (!ready || session.Revision != revision || viewPoints.Count < 2 || viewPoints.Any(p => !Inside(p)) ||
            viewPoints.Zip(viewPoints.Skip(1)).All(pair => (pair.Second - pair.First).Length < .5))
        { RestoreOutline(); return false; }
        stroke = viewPoints.Select(p => (p.X, overlay.Height - p.Y)).ToList();
        var left = viewPoints.Min(p => p.X); var right = viewPoints.Max(p => p.X);
        var topView = viewPoints.Min(p => p.Y); var bottomView = viewPoints.Max(p => p.Y);
        selection = new Rect(left, overlay.Height - bottomView, Math.Max(right - left, .5), Math.Max(bottomView - topView, .5));
        DrawStroke(viewPoints); accept.IsEnabled = true;
        status.Text = $"{viewPoints.Count} point stroke captured. Review it, then use it in the drawing editor.";
        return true;
    }

    void DrawStroke(IReadOnlyList<Point> viewPoints)
    {
        overlay.Children.Clear();
        if (viewPoints.Count < 2) return;
        var line = new Polyline { Points = new PointCollection(viewPoints), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, "FocusBrush"); overlay.Children.Add(line);
        if (kind != "Arrow") return;
        var tip = viewPoints[^1]; var from = viewPoints[0];
        var dx = tip.X - from.X; var dy = tip.Y - from.Y; var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length)) return;
        var head = Math.Min(12, length * .35); var ux = dx / length; var uy = dy / length;
        foreach (var sin in new[] { .5, -.5 })
        {
            var wing = new Line { X1 = tip.X, Y1 = tip.Y,
                X2 = tip.X - head * (ux * .8660254037844386 - uy * sin), Y2 = tip.Y - head * (uy * .8660254037844386 + ux * sin),
                StrokeThickness = 2, IsHitTestVisible = false };
            wing.SetResourceReference(Shape.StrokeProperty, "FocusBrush"); overlay.Children.Add(wing);
        }
    }

    void RestoreOutline()
    {
        overlay.Children.Clear();
        if (stroke is not null) { DrawStroke(stroke.Select(p => new Point(p.X, overlay.Height - p.Y)).ToArray()); return; }
        if (selection is Rect saved) Draw(new Rect(saved.Left, overlay.Height - saved.Bottom, saved.Width, saved.Height));
    }

    internal void Draw(Rect bounds)
    {
        overlay.Children.Clear();
        Shape outline;
        if (kind is "Line" or "Arrow") outline = new Line { X1 = bounds.Left, Y1 = bounds.Bottom, X2 = bounds.Right, Y2 = bounds.Top };
        else
        {
            outline = kind == "Ellipse" ? new Ellipse() : new Rectangle();
            outline.Width = bounds.Width; outline.Height = bounds.Height;
            Canvas.SetLeft(outline, bounds.Left); Canvas.SetTop(outline, bounds.Top);
        }
        outline.StrokeThickness = 2; outline.IsHitTestVisible = false;
        outline.SetResourceReference(Shape.StrokeProperty, "FocusBrush"); overlay.Children.Add(outline);
        if (kind == "Arrow")
        {
            var dx = bounds.Right - bounds.Left; var dy = bounds.Top - bounds.Bottom;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > 0) || !double.IsFinite(length)) return;
            var head = Math.Min(12, length * .35);
            var ux = dx / length; var uy = dy / length;
            AddArrowWing(bounds.Right, bounds.Top, head, ux, uy, -.5, .8660254037844386);
            AddArrowWing(bounds.Right, bounds.Top, head, ux, uy, .5, .8660254037844386);
        }

        void AddArrowWing(double x, double y, double size, double ux, double uy, double sin, double cos)
        {
            var wing = new Line { X1 = x, Y1 = y,
                X2 = Math.Clamp(x - size * (ux * cos - uy * sin), bounds.Left, bounds.Right),
                Y2 = Math.Clamp(y - size * (uy * cos + ux * sin), bounds.Top, bounds.Bottom),
                StrokeThickness = 2, IsHitTestVisible = false };
            wing.SetResourceReference(Shape.StrokeProperty, "FocusBrush"); overlay.Children.Add(wing);
        }
    }
}
