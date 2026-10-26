using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    static bool SupportsZeroOriginCoordinates(nint page)
    {
        if (GetPageBoundingBox(page, out var bounds) == 0) return false;
        var width = (double)bounds.Right - bounds.Left;
        var height = (double)bounds.Top - bounds.Bottom;
        return float.IsFinite(bounds.Left) && float.IsFinite(bounds.Bottom) &&
            float.IsFinite(bounds.Right) && float.IsFinite(bounds.Top) &&
            double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 &&
            bounds.Left == 0 && bounds.Bottom == 0;
    }

    /// <summary>The visible page box (CropBox intersected with MediaBox) in default user space, plus the page /Rotate quarter turns.</summary>
    readonly record struct VisibleBox(double Left, double Bottom, double Right, double Top, int Rotation)
    {
        public double UserWidth => Right - Left;
        public double UserHeight => Top - Bottom;
        const double Tolerance = .01;
        public bool Contains(double l, double b, double r, double t) =>
            l >= Left - Tolerance && b >= Bottom - Tolerance && r <= Right + Tolerance && t <= Top + Tolerance;
        public bool Contains(double x, double y) => Contains(x, y, x, y);
        public double ClampX(double x) => Math.Clamp(x, Left, Right);
        public double ClampY(double y) => Math.Clamp(y, Bottom, Top);
    }

    static VisibleBox GetVisibleBox(nint page)
    {
        if (GetPageBoundingBox(page, out var bounds) == 0 || !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Bottom) ||
            !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Top) || bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom)
            throw new InvalidOperationException("The page's visible bounds could not be read safely. The PDF has not changed.");
        return new VisibleBox(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top, ((Rotation(page) % 4) + 4) % 4);
    }

    /// <summary>Unit-scale matrix (a,b,c,d) taking display-upright axes (right, up) to default user space for a page /Rotate of 0-3 quarter turns clockwise.</summary>
    static (double A, double B, double C, double D) UprightAxes(int rotation) => rotation switch
    {
        1 => (0, 1, -1, 0), 2 => (-1, 0, 0, -1), 3 => (0, -1, 1, 0), _ => (1, 0, 0, 1)
    };

    /// <summary>User-space position of the bottom-left corner of the displayed rectangle (l,b,r,t).</summary>
    static (double X, double Y) DisplayOrigin(int rotation, double l, double b, double r, double t) => rotation switch
    {
        1 => (r, b), 2 => (r, t), 3 => (l, t), _ => (l, b)
    };

    /// <summary>Full matrix placing a unit square (or a displayed-upright object) so it fills the user-space rectangle upright on screen.</summary>
    static (double A, double B, double C, double D, double E, double F) UprightMatrix(int rotation, double l, double b, double r, double t)
    {
        var (ux, uy, vx, vy) = UprightAxes(rotation);
        var odd = rotation % 2 == 1;
        double dw = odd ? t - b : r - l, dh = odd ? r - l : t - b;
        var (e, f) = DisplayOrigin(rotation, l, b, r, t);
        return (ux * dw, uy * dw, vx * dh, vy * dh, e, f);
    }

    static (double X, double Y) VisibleOrigin(nint page) => GetPageBoundingBox(page, out var b) != 0 && float.IsFinite(b.Left) && float.IsFinite(b.Bottom) ? (b.Left, b.Bottom) : (0, 0);

    static object VisibleBoxInfo(nint page)
    {
        try { var box = GetVisibleBox(page); return new { box.Left, box.Bottom, box.Right, box.Top }; }
        catch (InvalidOperationException) { return new { Left = 0.0, Bottom = 0.0, Right = 0.0, Top = 0.0 }; }
    }

    /// <summary>Adds one line of 18 pt Helvetica whose baseline starts at (x,y) in user space, rotated to read upright on the displayed page.</summary>
    static void AddRotatedText(nint document, nint page, int rotation, string text, double x, double y)
    {
        var obj = Pdf.Native.FPDFPageObj_NewTextObj(document, "Helvetica", 18);
        Pdf.Check(obj != 0, "new text object");
        try
        {
            Pdf.Check(Pdf.Native.FPDFText_SetText(obj, text), "set text");
            var (a, b, c, d) = UprightAxes(rotation);
            Pdf.Native.FPDFPageObj_Transform(obj, a, b, c, d, x, y);
            InsertImageObject(page, obj);
        }
        catch { DestroyImageObject(obj); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PageBoundingBox
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [DllImport("pdfium", EntryPoint = "FPDF_GetPageBoundingBox")]
    static extern int GetPageBoundingBox(nint page, out PageBoundingBox rect);
}
