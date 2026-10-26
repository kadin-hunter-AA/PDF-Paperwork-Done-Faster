using System.Text.Json;

namespace HunterPdf.Desktop;

internal static class PageObjectHitTest
{
    // Match the viewer's PDF-to-display transform, including clockwise page rotation.
    internal static (double X, double Y) ToPdf(double x, double y, double width, double height,
        double viewWidth, double viewHeight, int rotation, double originX = 0, double originY = 0)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0)
            throw new InvalidOperationException("The page is not ready for selection.");
        // Dimensions supplied by PageInfo are already rotated by PDFium for quarter turns.
        // Scale into that display coordinate system, then invert PDFium's page rotation.
        // Returning a non-hit point for letterbox/outside coordinates also prevents an
        // object with an overhanging bound from being selected beyond the rendered page.
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > viewWidth || y > viewHeight)
            return (double.NaN, double.NaN);
        x *= width / viewWidth; y *= height / viewHeight;
        // The rotation inverse yields page-local coordinates (bottom-left of the visible box); adding the
        // box origin gives default user space, which is what objects, annotations and the engine use.
        var (px, py) = rotation switch
        {
            1 => (y, x), 2 => (width - x, y),
            3 => (height - y, width - x), _ => (x, height - y)
        };
        return (px + originX, py + originY);
    }

    /// <summary>Inverse of ToPdf for a user-space point: its position in the displayed page, in display points.</summary>
    internal static (double X, double Y) ToDisplay(double ux, double uy, double width, double height, int rotation, double originX = 0, double originY = 0)
    {
        double px = ux - originX, py = uy - originY;
        return rotation switch
        {
            1 => (py, px), 2 => (width - px, py),
            3 => (width - py, height - px), _ => (px, height - py)
        };
    }

    internal static JsonElement? Find(JsonElement data, double x, double y)
    {
        // The last painted eligible object wins when bounds overlap.
        foreach (var item in data.GetProperty("Objects").EnumerateArray().Reverse())
        {
            var type = item.GetProperty("Type").GetInt32();
            if (type is not (1 or 3) || !item.TryGetProperty("HasBounds", out var bounded) || !bounded.GetBoolean()) continue;
            if (x >= item.GetProperty("Left").GetDouble() && x <= item.GetProperty("Right").GetDouble() &&
                y >= item.GetProperty("Bottom").GetDouble() && y <= item.GetProperty("Top").GetDouble()) return item.Clone();
        }
        return null;
    }
}
