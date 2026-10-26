using System.Runtime.InteropServices;

sealed partial class Pdf
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Quad { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }
    internal static class Objects
    {
        const string D = "pdfium";
        [DllImport(D)] internal static extern nint FPDFPage_CreateAnnot(nint page, int subtype);
        [DllImport(D)] internal static extern void FPDFPage_CloseAnnot(nint annot);
        [DllImport(D)] internal static extern int FPDFPage_GetAnnotCount(nint page);
        [DllImport(D)] internal static extern int FPDFPage_RemoveAnnot(nint page, int index);
        [DllImport(D)] internal static extern nint FPDFPage_GetAnnot(nint page, int index);
        [DllImport(D)] internal static extern int FPDFAnnot_SetRect(nint annot, ref Rect rect);
        [DllImport(D)] internal static extern int FPDFAnnot_GetRect(nint annot, out Rect rect);
        [DllImport(D)] internal static extern int FPDFAnnot_GetSubtype(nint annot);
        [DllImport(D)] internal static extern int FPDFAnnot_GetFlags(nint annot);
        [DllImport(D, CharSet=CharSet.Ansi)] internal static extern int FPDFAnnot_HasKey(nint annot, [MarshalAs(UnmanagedType.LPStr)] string key);
        [DllImport(D)] internal static extern int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
        [DllImport(D)] internal static extern int FPDFAnnot_AppendAttachmentPoints(nint annot, ref Quad quad);
        [DllImport(D, CharSet=CharSet.Unicode)] internal static extern int FPDFAnnot_SetStringValue(nint annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [DllImport(D, CharSet=CharSet.Ansi)] internal static extern uint FPDFAnnot_GetStringValue(nint annot, [MarshalAs(UnmanagedType.LPStr)] string key, nint buffer, uint buflen);
    }
}
