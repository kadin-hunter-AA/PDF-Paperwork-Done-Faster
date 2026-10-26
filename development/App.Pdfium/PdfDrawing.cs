using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

internal static class PdfDrawing
{
    const string Pdfium = "pdfium";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public float X, Y; }

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_SetBorder")]
    internal static extern int SetBorder(nint annotation, float horizontalRadius,
        float verticalRadius, float width);

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_SetFlags")]
    internal static extern int SetFlags(nint annotation, int flags);

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_GetInkListCount")]
    internal static extern nuint InkListCount(nint annotation);

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_GetInkListPath")]
    internal static extern nuint InkListPath(nint annotation, nuint pathIndex,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] Point[]? points, nuint length);

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_RemoveInkList")]
    internal static extern int RemoveInkList(nint annotation);

    [DllImport(Pdfium, EntryPoint = "FPDFAnnot_AddInkStroke")]
    internal static extern int AddInkStroke(nint annotation,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] Point[] points, nuint pointCount);
}
