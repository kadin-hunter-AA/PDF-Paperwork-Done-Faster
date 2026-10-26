using System.Runtime.InteropServices;

sealed partial class Pdf
{
    // Windows x64 ABI: unsigned long is 32-bit, size_t is 64-bit.
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Quad { public float X1,Y1,X2,Y2,X3,Y3,X4,Y4; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public float X,Y; }
    internal static class Objects
    {
        const string D = "pdfium";
        [DllImport(D)] internal static extern nint FPDFPageObj_NewImageObj(nint doc);
        [DllImport(D)] internal static extern int FPDFImageObj_SetBitmap(nint[] pages,int count,nint image,nint bitmap);
        [DllImport(D)] internal static extern int FPDFImageObj_SetMatrix(nint image,double a,double b,double c,double d,double e,double f);
        [DllImport(D)] internal static extern nint FPDFImageObj_GetBitmap(nint image);
        [DllImport(D)] internal static extern int FPDFPage_RemoveObject(nint page,nint obj);
        [DllImport(D)] internal static extern int FPDFPageObj_GetBounds(nint obj,out float left,out float bottom,out float right,out float top);
        [DllImport(D)] internal static extern nint FPDFPage_CreateAnnot(nint page,int subtype);
        [DllImport(D)] internal static extern void FPDFPage_CloseAnnot(nint annot);
        [DllImport(D)] internal static extern int FPDFPage_GetAnnotCount(nint page);
        [DllImport(D)] internal static extern nint FPDFPage_GetAnnot(nint page,int index);
        [DllImport(D)] internal static extern int FPDFAnnot_SetRect(nint annot,ref Rect rect);
        [DllImport(D)] internal static extern int FPDFAnnot_GetRect(nint annot,out Rect rect);
        [DllImport(D)] internal static extern int FPDFAnnot_GetSubtype(nint annot);
        [DllImport(D)] internal static extern int FPDFAnnot_SetColor(nint annot,int type,uint r,uint g,uint b,uint a);
        [DllImport(D)] internal static extern int FPDFAnnot_GetColor(nint annot,int type,out uint r,out uint g,out uint b,out uint a);
        [DllImport(D)] internal static extern int FPDFAnnot_AppendAttachmentPoints(nint annot,ref Quad quad);
        [DllImport(D)] internal static extern int FPDFAnnot_SetAttachmentPoints(nint annot,nuint index,ref Quad quad);
        [DllImport(D)] internal static extern int FPDFAnnot_GetAttachmentPoints(nint annot,nuint index,out Quad quad);
        [DllImport(D)] internal static extern int FPDFAnnot_AddInkStroke(nint annot,Point[] points,nuint count);
        [DllImport(D)] internal static extern int FPDFAnnot_RemoveInkList(nint annot);
        [DllImport(D)] internal static extern uint FPDFAnnot_GetInkListCount(nint annot);
        [DllImport(D)] internal static extern uint FPDFAnnot_GetInkListPath(nint annot,uint index,[Out] Point[] points,uint count);
    }
    internal void SetImage(nint page,nint image,uint color)
    {
        var bitmap=Native.FPDFBitmap_Create(16,16,0); Check(bitmap!=0,"image bitmap");
        try { Check(Native.FPDFBitmap_FillRect(bitmap,0,0,16,16,color),"image fill"); Check(Objects.FPDFImageObj_SetBitmap([page],1,image,bitmap),"image replacement"); }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }
}

static class ObjectProof
{
    public static void Run(string output)
    {
        var original=Path.Combine(output,"objects-original.pdf");
        var edited=Path.Combine(output,"objects-edited.pdf");
        using(var doc=Pdf.Create())
        {
            using(var page=doc.NewPage())
            {
                page.AddText("Image and annotation test",72,700);
                for(int i=0;i<2;i++)
                {
                    var image=Pdf.Objects.FPDFPageObj_NewImageObj(doc.Handle); Pdf.Check(image!=0,"new image");
                    try { doc.SetImage(page.Handle,image,0xffff0000); Pdf.Check(Pdf.Objects.FPDFImageObj_SetMatrix(image,100,0,0,80,72+i*140,500),"image placement"); Pdf.Native.FPDFPage_InsertObject(page.Handle,image); }
                    catch { Pdf.Native.FPDFPageObj_Destroy(image); throw; }
                }
                var rect=new Pdf.Rect{Left=70,Top=725,Right=330,Bottom=690};
                var quad=new Pdf.Quad{X1=72,Y1=718,X2=320,Y2=718,X3=72,Y3=698,X4=320,Y4=698};
                var highlight=Pdf.Objects.FPDFPage_CreateAnnot(page.Handle,9); Pdf.Check(highlight!=0,"highlight");
                try { Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(highlight,ref rect),"highlight rect"); Pdf.Check(Pdf.Objects.FPDFAnnot_AppendAttachmentPoints(highlight,ref quad),"highlight quad"); Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(highlight,0,255,255,0,100),"highlight color"); }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(highlight); }
                var ink=Pdf.Objects.FPDFPage_CreateAnnot(page.Handle,15); Pdf.Check(ink!=0,"ink");
                try { rect=new Pdf.Rect{Left=70,Top=450,Right=300,Bottom=350}; Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(ink,ref rect),"ink rect"); Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(ink,0,0,0,255,255),"ink color"); Pdf.Check(Pdf.Objects.FPDFAnnot_AddInkStroke(ink,[new(){X=80,Y=380},new(){X=180,Y=420},new(){X=280,Y=380}],3)>=0,"ink stroke"); }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(ink); }
                page.Generate();
            }
            doc.Save(original);
        }
        using(var doc=Pdf.Open(original))
        {
            using(var page=doc.Page(0))
            {
                Pdf.Check(Pdf.Native.FPDFPage_CountObjects(page.Handle)==3,"text and two images");
                var image=Pdf.Native.FPDFPage_GetObject(page.Handle,1); Pdf.Check(Pdf.Native.FPDFPageObj_GetType(image)==3,"existing image");
                doc.SetImage(page.Handle,image,0xff00ff00);
                Pdf.Check(Pdf.Objects.FPDFImageObj_SetMatrix(image,180,0,0,100,300,500),"move and resize");
                var removed=Pdf.Native.FPDFPage_GetObject(page.Handle,2); Pdf.Check(Pdf.Objects.FPDFPage_RemoveObject(page.Handle,removed),"delete image"); Pdf.Native.FPDFPageObj_Destroy(removed);
                var highlight=Pdf.Objects.FPDFPage_GetAnnot(page.Handle,0);
                try { Pdf.Check(Pdf.Objects.FPDFAnnot_GetAttachmentPoints(highlight,0,out var quad),"read highlight"); quad.X2=290;quad.X4=290;Pdf.Check(Pdf.Objects.FPDFAnnot_SetAttachmentPoints(highlight,0,ref quad),"modify highlight"); Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(highlight,0,0,255,0,100),"modified highlight color"); }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(highlight); }
                var ink=Pdf.Objects.FPDFPage_GetAnnot(page.Handle,1);
                try { Pdf.Check(Pdf.Objects.FPDFAnnot_RemoveInkList(ink),"remove old ink"); Pdf.Check(Pdf.Objects.FPDFAnnot_AddInkStroke(ink,[new(){X=80,Y=400},new(){X=180,Y=360},new(){X=280,Y=400}],3)>=0,"replace ink stroke"); }
                finally { Pdf.Objects.FPDFPage_CloseAnnot(ink); }
                page.Generate();
            }
            doc.Save(edited);
        }
        using(var doc=Pdf.Open(edited))
        using(var page=doc.Page(0))
        {
            Pdf.Check(page.Text().Contains("Image and annotation test"),"unrelated text preserved");
            Pdf.Check(Pdf.Native.FPDFPage_CountObjects(page.Handle)==2,"deletion persisted");
            var image=Pdf.Native.FPDFPage_GetObject(page.Handle,1);
            Pdf.Check(Pdf.Objects.FPDFPageObj_GetBounds(image,out var l,out var b,out var r,out var t),"image bounds");
            Pdf.Check(Math.Abs(l-300)<.01&&Math.Abs(b-500)<.01&&Math.Abs(r-480)<.01&&Math.Abs(t-600)<.01,"image move/resize persisted");
            var bitmap=Pdf.Objects.FPDFImageObj_GetBitmap(image); Pdf.Check(bitmap!=0,"reopened image bitmap");
            try { var pixel=new byte[3];Marshal.Copy(Pdf.Native.FPDFBitmap_GetBuffer(bitmap),pixel,0,3); Pdf.Check(pixel[0]==0&&pixel[1]==255&&pixel[2]==0,"green replacement persisted"); }
            finally { Pdf.Native.FPDFBitmap_Destroy(bitmap); }
            Pdf.Check(Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle)==2,"annotation count");
            var highlight=Pdf.Objects.FPDFPage_GetAnnot(page.Handle,0);
            try { Pdf.Check(Pdf.Objects.FPDFAnnot_GetAttachmentPoints(highlight,0,out var quad),"reopened quad");Pdf.Check(quad.X2==290,"highlight modification persisted");Pdf.Check(Pdf.Objects.FPDFAnnot_GetColor(highlight,0,out var red,out var green,out var blue,out var alpha),"highlight color");Pdf.Check(red==0&&green==255&&blue==0&&alpha==100,"highlight color persisted"); }
            finally { Pdf.Objects.FPDFPage_CloseAnnot(highlight); }
            var ink=Pdf.Objects.FPDFPage_GetAnnot(page.Handle,1);
            try { Pdf.Check(Pdf.Objects.FPDFAnnot_GetInkListCount(ink)==1,"ink count");var points=new Pdf.Point[3];Pdf.Check(Pdf.Objects.FPDFAnnot_GetInkListPath(ink,0,points,3)==3&&points[1].Y==360,"ink modification persisted"); }
            finally { Pdf.Objects.FPDFPage_CloseAnnot(ink); }
            page.Render(Path.Combine(output,"objects-edited.ppm"));
        }
        Console.WriteLine("PASS: image move/resize/replace/delete and highlight/ink modification persist after reopen.");
    }
}
