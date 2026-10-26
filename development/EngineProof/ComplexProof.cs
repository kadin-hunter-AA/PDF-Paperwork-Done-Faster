using System.Runtime.InteropServices;
using System.Text;

partial class Pdf
{
    internal static class Complex
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Matrix { public float A,B,C,D,E,F; }
        [DllImport("pdfium")] internal static extern int FPDFPageObj_GetMatrix(nint obj,out Matrix matrix);
        [DllImport("pdfium")] internal static extern int FPDFTextObj_GetFontSize(nint obj,out float size);
        [DllImport("pdfium")] internal static extern int FPDFPage_GetRotation(nint page);
        [DllImport("pdfium")] internal static extern uint FPDFFormObj_CountObjects(nint form);
        [DllImport("pdfium")] internal static extern nint FPDFFormObj_GetObject(nint form,uint index);
    }
}

static class ComplexProof
{
    // Both pages reference image object 7. Outer form 8 calls nested form 9.
    // Building the raw fixture makes actual resource sharing and nesting known.
    public static bool Run(string output)
    {
        var original=Path.Combine(output,"complex-original.pdf");
        var edited=Path.Combine(output,"complex-direct-edit.pdf");
        WriteFixture(original);
        using(var doc=Pdf.Open(original))
        {
            using(var page=doc.Page(0))
            {
                Pdf.Check(Pdf.Complex.FPDFPage_GetRotation(page.Handle)==1,"90 degree page rotation");
                var image=Find(page.Handle,3);
                doc.SetImage(page.Handle,image,0xff00ff00);
                Pdf.Check(Pdf.Objects.FPDFImageObj_SetMatrix(image,140,0,0,100,300,450),"rotated page image edit");
                var form=Find(page.Handle,5);
                Pdf.Check(Pdf.Complex.FPDFFormObj_CountObjects(form)==1,"outer reusable form");
                var inner=Pdf.Complex.FPDFFormObj_GetObject(form,0);
                Pdf.Check(Pdf.Native.FPDFPageObj_GetType(inner)==5,"nested inner form");
                var nested=Pdf.Complex.FPDFFormObj_GetObject(inner,0);
                Pdf.Check(Pdf.Native.FPDFPageObj_GetType(nested)==1,"nested text object");
                Pdf.Check(Pdf.Native.FPDFText_SetText(nested,"Nested text edited"),"edit nested existing text");
                Pdf.Native.FPDFPageObj_Transform(inner,1,0,0,1,0,0);
                Pdf.Native.FPDFPageObj_Transform(form,1,0,0,1,0,0);
                page.Generate();
            }
            doc.Save(edited);
        }
        bool nestedPersisted;
        using(var doc=Pdf.Open(edited))
        {
            using(var first=doc.Page(0))
            {
                Pdf.Check(Pdf.Complex.FPDFPage_GetRotation(first.Handle)==1,"rotation persisted");
                nestedPersisted=first.Text().Contains("Nested text edited")&&!first.Text().Contains("Nested original");
                Pdf.Check(nestedPersisted||first.Text().Contains("Nested original"),"nested content preserved when edit unsupported");
                Pdf.Check(first.Text().Contains("Rotated page unchanged text"),"rotated unrelated text intact");
                CheckImage(Find(first.Handle,3),0,255,0);
                Pdf.Check(Pdf.Objects.FPDFPageObj_GetBounds(Find(first.Handle,3),out var l,out var b,out var r,out var t),"rotated image bounds");
                Pdf.Check(l==300&&b==450&&r==440&&t==550,"rotated page geometry persisted");
                first.Render(Path.Combine(output,"complex-page-1.ppm"));
            }
            using(var second=doc.Page(1))
            {
                CheckImage(Find(second.Handle,3),0,0,255);
                Pdf.Check(second.Text().Contains("Shared image must remain red"),"shared page text intact");
                second.Render(Path.Combine(output,"complex-page-2.ppm"));
            }
        }
        Console.WriteLine("PASS: rotated page and shared-image isolation persist after reopen.");
        Console.WriteLine(nestedPersisted?"PASS: nested text persists.":"UNSUPPORTED: nested text edit did not persist; original nested text remains.");
        var resolved=Path.Combine(output,"complex-edited.pdf");
        Pdf.ReplaceNestedText(edited,resolved,new(0,[1,0],0,"Nested original",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(edited)))),
            "Nested text edited");
        using(var doc=Pdf.Open(resolved))
        {
            using(var page=doc.Page(0))
            {
                Pdf.Check(page.Text().Contains("Nested text edited")&&!page.Text().Contains("Nested original"),"nested replacement persisted");
                Pdf.Check(page.Text().Contains("Rotated page unchanged text"),"unrelated nested page text");
                Pdf.Check(Pdf.Complex.FPDFPage_GetRotation(page.Handle)==1,"nested page rotation");
                var outer=Find(page.Handle,5);
                var inner=Pdf.Complex.FPDFFormObj_GetObject(outer,0);
                Pdf.Check(Pdf.Complex.FPDFFormObj_CountObjects(outer)==1&&Pdf.Complex.FPDFFormObj_CountObjects(inner)==1,"form hierarchy retained");
                CheckImage(Find(page.Handle,3),0,255,0);
                page.Render(Path.Combine(output,"complex-resolved.ppm"));
            }
            using(var page=doc.Page(1))
            {
                CheckImage(Find(page.Handle,3),0,0,255);
                Pdf.Check(page.Text().Contains("Shared image must remain red"),"second page preserved");
            }
        }
        Console.WriteLine("PASS: nested replacement persists inside the original form hierarchy.");
        return nestedPersisted;
    }
    static nint Find(nint page,int type)
    {
        for(int i=0;i<Pdf.Native.FPDFPage_CountObjects(page);i++)
        {var obj=Pdf.Native.FPDFPage_GetObject(page,i);if(Pdf.Native.FPDFPageObj_GetType(obj)==type)return obj;}
        throw new InvalidOperationException($"Missing object type {type}");
    }
    static void CheckImage(nint image,byte blue,byte green,byte red)
    {
        var bitmap=Pdf.Objects.FPDFImageObj_GetBitmap(image);Pdf.Check(bitmap!=0,"shared image bitmap");
        try{var bytes=new byte[3];Marshal.Copy(Pdf.Native.FPDFBitmap_GetBuffer(bitmap),bytes,0,3);Pdf.Check(bytes[0]==blue&&bytes[1]==green&&bytes[2]==red,"shared image pixels");}
        finally{Pdf.Native.FPDFBitmap_Destroy(bitmap);}
    }
    static string Stream(string dictionary,string data)=>$"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(data)} >>\nstream\n{data}\nendstream";
    static void WriteFixture(string path)
    {
        string[] objects=[
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 /Resources << /Font << /F1 5 0 R >> /XObject << /Im 7 0 R /Outer 8 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /Im 7 0 R >> >> /Contents 10 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("","BT /F1 18 Tf 72 700 Td (Rotated page unchanged text) Tj ET q 100 0 0 80 72 500 cm /Im Do Q q 1 0 0 1 72 600 cm /Outer Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode","FF0000>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 60] /Resources << /XObject << /Inner 9 0 R >> >>","q 1 0 0 1 10 10 cm /Inner Do Q"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 380 40] /Resources << /Font << /F1 5 0 R >> >>","BT /F1 18 Tf 0 10 Td (Nested original) Tj ET"),
            Stream("","BT /F1 18 Tf 72 700 Td (Shared image must remain red) Tj ET q 100 0 0 80 72 500 cm /Im Do Q")];
        using var file=new FileStream(path,FileMode.CreateNew);
        void Write(string text)=>file.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");var offsets=new List<long>{0};
        for(int i=0;i<objects.Length;i++){offsets.Add(file.Position);Write($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
        var xref=file.Position;Write($"xref\n0 {objects.Length+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets.Skip(1))Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
