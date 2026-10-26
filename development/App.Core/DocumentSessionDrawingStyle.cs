namespace HunterPdf.Core;

/// <summary>Stroke color (0–255 channels) and width (0.5–12 pt) for a new drawing.</summary>
public sealed record DrawingStroke(int Red, int Green, int Blue, double Width)
{
    internal object Wire()
    {
        if (Red is < 0 or > 255 || Green is < 0 or > 255 || Blue is < 0 or > 255)
            throw new InvalidOperationException("Colors must use values from 0 to 255.");
        if (!double.IsFinite(Width) || Width < .5 || Width > 12)
            throw new InvalidOperationException("Line width must be between 0.5 and 12 points.");
        return new { Red, Green, Blue, Width };
    }
}

public sealed partial class DocumentSession
{
    public Task AddDrawingAsync(int page, string kind, double left, double bottom, double right, double top,
        DrawingStroke stroke, CancellationToken cancellation = default) =>
        EditAsync("AddDrawing", new { Page = page, Kind = kind, Left = left, Bottom = bottom, Right = right, Top = top, Stroke = stroke.Wire() }, cancellation);

    public Task AddInkAsync(int page, string kind, IEnumerable<(double X, double Y)> points, DrawingStroke stroke,
        CancellationToken cancellation = default)
    {
        var list = points.Select(p => new[] { p.X, p.Y }).ToArray();
        if (list.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
            throw new InvalidOperationException("Stroke points must be finite PDF page coordinates.");
        return EditAsync("AddInk", new { Page = page, Kind = kind, Points = list, Stroke = stroke.Wire() }, cancellation);
    }
}
