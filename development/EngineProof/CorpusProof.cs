using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

partial class Pdf
{
    internal static class Drawing
    {
        [DllImport("pdfium")] internal static extern nint FPDFPageObj_CreateNewRect(float x,float y,float w,float h);
        [DllImport("pdfium")] internal static extern int FPDFPageObj_SetFillColor(nint obj,uint r,uint g,uint b,uint a);
        [DllImport("pdfium")] internal static extern int FPDFPath_SetDrawMode(nint obj,int fill,int stroke);
    }
}

static class CorpusProof
{
    record Fixture(string File,int? Pages,string Purpose,string Expected,string Sha256,string License="Project-generated synthetic fixture");
    public static void Run(string output)
    {
        var fixtures=new List<Fixture>();
        var layout=Path.Combine(output,"layout-columns-table.pdf");
        using(var doc=Pdf.Create())
        {
            using(var page=doc.NewPage())
            {
                Rectangle(page,0,0,612,792,240,232,250);
                page.AddText("Columns, table and overlap",45,745);
                for(int row=0;row<8;row++)
                {page.AddText($"Left column line {row+1}",45,690-row*27);page.AddText($"Right column line {row+1}",325,690-row*27);}
                for(int row=0;row<4;row++)for(int col=0;col<3;col++)
                {Rectangle(page,45+col*160,310-row*45,155,40,215,225,240);page.AddText($"R{row+1} C{col+1}",55+col*160,322-row*45);}
                page.AddText("Overlapping foreground",45,100);
                Rectangle(page,200,90,130,30,160,225,215);
                page.Generate();
            }
            doc.Save(layout);
        }
        using(var doc=Pdf.Open(layout))using(var page=doc.Page(0))
        {Pdf.Check(doc.PageCount==1,"layout page count");var text=page.Text();for(int r=1;r<=4;r++)for(int c=1;c<=3;c++)Pdf.Check(text.Contains($"R{r} C{c}"),"table cell retained");Pdf.Check(text.Contains("Left column line 8")&&text.Contains("Right column line 8"),"both columns retained");page.Render(Path.Combine(output,"layout.ppm"));}
        Add(layout,1,"Columns, table, nonwhite background, overlapping content","All 16 column lines and 12 cell labels extracted; visual overlap intentional.");

        var scan=Path.Combine(output,"scan-image-only.pdf");
        using(var doc=Pdf.Create())
        {
            using(var page=doc.NewPage())
            {
                const int w=256,h=256;
                var bitmap=Pdf.Native.FPDFBitmap_Create(w,h,0);Pdf.Check(bitmap!=0,"synthetic scan bitmap");
                try
                {
                    Pdf.Check(Pdf.Native.FPDFBitmap_FillRect(bitmap,0,0,w,h,0xffffffff),"scan background");
                    for(int line=0;line<8;line++)Pdf.Check(Pdf.Native.FPDFBitmap_FillRect(bitmap,25,25+line*25,140+line*7,7,0xff202020),"scan raster lines");
                    var obj=Pdf.Objects.FPDFPageObj_NewImageObj(doc.Handle);Pdf.Check(obj!=0,"scan image object");
                    try{Pdf.Check(Pdf.Objects.FPDFImageObj_SetBitmap([page.Handle],1,obj,bitmap),"scan pixels");Pdf.Check(Pdf.Objects.FPDFImageObj_SetMatrix(obj,450,0,0,450,72,240),"scan placement");Pdf.Native.FPDFPage_InsertObject(page.Handle,obj);}
                    catch{Pdf.Native.FPDFPageObj_Destroy(obj);throw;}
                }
                finally{Pdf.Native.FPDFBitmap_Destroy(bitmap);}
                page.Generate();
            }
            doc.Save(scan);
        }
        using(var doc=Pdf.Open(scan))using(var page=doc.Page(0))
        {Pdf.Check(string.IsNullOrWhiteSpace(page.Text()),"image-only scan has no hidden text");Pdf.Check(Pdf.Native.FPDFPage_CountObjects(page.Handle)==1&&Pdf.Native.FPDFPageObj_GetType(Pdf.Native.FPDFPage_GetObject(page.Handle,0))==3,"scan is image only");page.Render(Path.Combine(output,"scan.ppm"));}
        Add(scan,1,"Synthetic scan representation (raster bars, not real OCR sample)","One image; no extractable text. OCR disabled until P1.");

        var mixed=Path.Combine(output,"mixed-digital-scan.pdf");
        using(var doc=Pdf.Create())using(var digital=Pdf.Open(layout))using(var raster=Pdf.Open(scan))
        {Pdf.ImportPages(doc,digital,null,0);Pdf.ImportPages(doc,raster,null,1);doc.Save(mixed);}
        using(var doc=Pdf.Open(mixed)){Pdf.Check(doc.PageCount==2,"mixed pages");using(var page=doc.Page(0))Pdf.Check(page.Text().Contains("Columns, table"),"mixed digital page");using(var page=doc.Page(1))Pdf.Check(string.IsNullOrWhiteSpace(page.Text()),"mixed scan page");}
        Add(mixed,2,"Mixed digital and image-only pages","Page 1 searchable, page 2 image-only.");

        var large=Path.Combine(output,"large-500-pages.pdf");
        var timer=Stopwatch.StartNew();
        using(var doc=Pdf.Create())
        {for(int i=1;i<=500;i++){using var page=doc.NewPage();page.AddText($"Corpus page {i:D3} of 500",72,700);page.Generate();}doc.Save(large);}
        var creationMs=timer.ElapsedMilliseconds;timer.Restart();
        using(var doc=Pdf.Open(large))
        {
            Pdf.Check(doc.PageCount==500,"large corpus page count");
            foreach(int index in new[]{0,249,499}){using var page=doc.Page(index);Pdf.Check(page.Text().Contains($"Corpus page {index+1:D3} of 500"),"large random access page");}
            using var visible=doc.Page(499);visible.Render(Path.Combine(output,"large-last-page.ppm"));
        }
        var accessMs=timer.ElapsedMilliseconds;
        Add(large,500,"Large-file navigation fixture","500 pages; pages 1, 250, 500 readable; render only requested last page.");

        var malformed=Path.Combine(output,"malformed.pdf");File.WriteAllText(malformed,"%PDF-1.7\nDeliberately invalid: no objects, catalog, xref or trailer.\n");
        var handle=Pdf.Native.FPDF_LoadDocument(malformed,null);
        if(handle!=0){Pdf.Native.FPDF_CloseDocument(handle);throw new InvalidOperationException("Malformed fixture unexpectedly accepted");}
        Add(malformed,null,"Invalid-file rejection","Open must fail safely; deliberately not opened in the viewer.");

        // Existing engine-proof cases give this corpus font, rotation, sharing and nesting coverage.
        var engineCases=Path.Combine(output,"engine-cases");Directory.CreateDirectory(engineCases);
        ObjectProof.Run(engineCases);PageProof.Run(engineCases);var directNested=ComplexProof.Run(engineCases);var fontResults=FontProof.Run(engineCases);
        foreach(var path in Directory.GetFiles(engineCases,"*.pdf"))
            fixtures.Add(new(Path.GetRelativePath(output,path),null,"Engine capability fixture","See FONT-COMPLEX-RESULTS.md and NESTED-REPLACEMENT.md; direct negative cases are deliberate.",Hash(path),Path.GetFileName(path).StartsWith("font-")?"Local Windows font embedded: redistribution review required":"Project-generated synthetic fixture"));
        var report=new{status="PARTIAL_CORPUS",passed=new[]{"layout extraction","image-only scan classification","mixed pages","500-page random access","malformed rejection","existing engine cases"},fixtures,benchmark=new{creationMs,accessMs,workingSetBytes=Process.GetCurrentProcess().WorkingSet64,note="Single-run harness measurements; not UI performance acceptance"},fontResults,directNested,pending=new[]{"real OCR scan","password-protected documents","forms/signatures/bookmarks/links/attachments/layers","ligatures and absent Unicode maps","transparency/clipping/page-box variants","licensed public fixtures","complete per-P0 acceptance mapping","independent viewing of new corpus cases"}};
        File.WriteAllText(Path.Combine(output,"corpus.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS: corpus checks completed; {fixtures.Count} fixtures recorded. Milestone 2 remains in progress.");
        void Add(string path,int? count,string purpose,string expected)=>fixtures.Add(new(Path.GetFileName(path),count,purpose,expected,Hash(path)));
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Rectangle(Pdf.PageHandle page,float x,float y,float w,float h,uint r,uint g,uint b)
    {
        var obj=Pdf.Drawing.FPDFPageObj_CreateNewRect(x,y,w,h);Pdf.Check(obj!=0,"rectangle");
        try{Pdf.Check(Pdf.Drawing.FPDFPageObj_SetFillColor(obj,r,g,b,255),"rectangle color");Pdf.Check(Pdf.Drawing.FPDFPath_SetDrawMode(obj,2,0),"rectangle fill");Pdf.Native.FPDFPage_InsertObject(page.Handle,obj);}
        catch{Pdf.Native.FPDFPageObj_Destroy(obj);throw;}
    }
}
