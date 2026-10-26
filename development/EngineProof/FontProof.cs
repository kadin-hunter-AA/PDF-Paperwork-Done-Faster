using System.Runtime.InteropServices;

partial class Pdf
{
    internal static class Fonts
    {
        [DllImport("pdfium")] internal static extern nint FPDFText_LoadFont(nint doc,byte[] data,uint size,int type,int cid);
        [DllImport("pdfium")] internal static extern nint FPDFPageObj_CreateTextObj(nint doc,nint font,float size);
        [DllImport("pdfium")] internal static extern void FPDFFont_Close(nint font);
        [DllImport("pdfium")] internal static extern nint FPDFTextObj_GetFont(nint text);
        [DllImport("pdfium")] internal static extern int FPDFFont_GetIsEmbedded(nint font);
        [DllImport("pdfium")] internal static extern int FPDFFont_GetFontData(nint font,byte[]? buffer,nuint length,out nuint size);
    }
}

static class FontProof
{
    public sealed record Results(bool EmbeddedEdit,bool RealSubset,bool SubsetEdit,bool MissingGlyphDirectEdit,bool MissingGlyphFontReplacement,ulong FullFontBytes,ulong SubsetFontBytes,string FontSha256);
    public static Results Run(string output)
    {
        var fontPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf");
        var data=File.ReadAllBytes(fontPath);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        var full=Path.Combine(output,"font-embedded-original.pdf");
        var subset=Path.Combine(output,"font-subset-original.pdf");
        Create(full,data,2);Create(subset,data,10);
        var fullSize=FontSize(full);var subsetSize=FontSize(subset);
        var realSubset=subsetSize<fullSize;
        var fullEdited=Path.Combine(output,"font-embedded-edited.pdf");
        Edit(full,fullEdited,"CABCABCAB",data,false);
        var fullPassed=Contains(fullEdited,"CABCABCAB");
        var subsetEdited=Path.Combine(output,"font-subset-edited.pdf");
        Edit(subset,subsetEdited,"CABCABCAB",data,false);
        var subsetPassed=Contains(subsetEdited,"CABCABCAB");
        var missing=Path.Combine(output,"font-missing-direct.pdf");
        bool direct=false;
        try{Edit(subset,missing,"ABCΩЖ",data,false);direct=Contains(missing,"ABCΩЖ");}
        catch(InvalidOperationException){direct=false;}
        var fallback=Path.Combine(output,"font-missing-reembedded.pdf");
        Edit(subset,fallback,"ABCΩЖ",data,true);
        var fallbackPassed=Contains(fallback,"ABCΩЖ");
        foreach(var path in new[]{fullEdited,subsetEdited,fallback})
        {using var doc=Pdf.Open(path);using var page=doc.Page(0);page.Render(Path.ChangeExtension(path,"ppm"));}
        Console.WriteLine($"Fonts: embedded={fullPassed}, actual subset={realSubset} ({fullSize} -> {subsetSize} bytes), subset edit={subsetPassed}, missing glyph direct={direct}, full-font replacement={fallbackPassed}.");
        return new(fullPassed,realSubset,subsetPassed,direct,fallbackPassed,(ulong)fullSize,(ulong)subsetSize,hash);
    }
    static void Create(string path,byte[] data,uint flags)
    {
        using var doc=Pdf.Create();var font=Load(doc,data);
        try
        {
            using(var page=doc.NewPage())
            {Insert(doc,page,font,"ABC");page.AddText("Unrelated font fixture text",72,650);page.Generate();}
            doc.Save(path,flags);
        }
        finally{Pdf.Fonts.FPDFFont_Close(font);}
    }
    static nint Load(Pdf doc,byte[] data)
    {var font=Pdf.Fonts.FPDFText_LoadFont(doc.Handle,data,(uint)data.Length,2,1);Pdf.Check(font!=0,"load TrueType font");return font;}
    static void Insert(Pdf doc,Pdf.PageHandle page,nint font,string text)
    {
        var obj=Pdf.Fonts.FPDFPageObj_CreateTextObj(doc.Handle,font,18);Pdf.Check(obj!=0,"embedded text");
        try{Pdf.Check(Pdf.Native.FPDFText_SetText(obj,text),"embedded text value");Pdf.Native.FPDFPageObj_Transform(obj,1,0,0,1,72,700);Pdf.Native.FPDFPage_InsertObject(page.Handle,obj);}
        catch{Pdf.Native.FPDFPageObj_Destroy(obj);throw;}
    }
    static nuint FontSize(string path)
    {
        using var doc=Pdf.Open(path);using var page=doc.Page(0);
        var font=Pdf.Fonts.FPDFTextObj_GetFont(Pdf.Native.FPDFPage_GetObject(page.Handle,0));
        Pdf.Check(Pdf.Fonts.FPDFFont_GetIsEmbedded(font)==1,"font embedded");
        Pdf.Check(Pdf.Fonts.FPDFFont_GetFontData(font,null,0,out var size),"embedded font data size");return size;
    }
    static void Edit(string original,string destination,string text,byte[] data,bool reembed)
    {
        using var doc=Pdf.Open(original);nint font=0;
        try
        {
            using(var page=doc.Page(0))
            {
                var obj=Pdf.Native.FPDFPage_GetObject(page.Handle,0);
                if(reembed)
                {
                    font=Load(doc,data);Insert(doc,page,font,text);
                    Pdf.Check(Pdf.Objects.FPDFPage_RemoveObject(page.Handle,obj),"remove subset text object");Pdf.Native.FPDFPageObj_Destroy(obj);
                }
                else Pdf.Check(Pdf.Native.FPDFText_SetText(obj,text),"edit existing embedded text");
                page.Generate();
            }
            doc.Save(destination);
        }
        finally{if(font!=0)Pdf.Fonts.FPDFFont_Close(font);}
    }
    static bool Contains(string path,string expected)
    {
        using var doc=Pdf.Open(path);using var page=doc.Page(0);var text=page.Text();
        Pdf.Check(text.Contains("Unrelated font fixture text"),"font fixture unrelated content");
        return text.Contains(expected);
    }
}
