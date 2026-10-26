using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
namespace HunterPdf.Pdfium;
public sealed partial class Engine : IDisposable {
    const int MaxCombinedPages=50_000;
    readonly Dictionary<Guid,(Pdf Pdf,long Revision)> documents = new();
    readonly HashSet<Guid> signedCopyConsent = new();
    public Engine() => Pdf.Initialize();
    public object Open(Guid id,string path,string? password,bool allowSignedCopy=false) {
        if(documents.ContainsKey(id)) throw new InvalidOperationException("Document already open.");
        var pdf=Pdf.Open(path,password);
        var signatures=SignatureCount(pdf.Handle);
        if(signatures<0){pdf.Dispose();throw new InvalidOperationException("Signature detection failed.");}
        documents.Add(id,(pdf,0));
        SetRenderSnapshot(id,0,path,password);
        if(allowSignedCopy) signedCopyConsent.Add(id);
        return new { PageCount=pdf.PageCount, SignatureCount=signatures };
    }
    public object InspectCombineInput(string path) {
        using var pdf=OpenCombineSource(path);
        EnsureCombineSupported(pdf);
        return new { PageCount=pdf.PageCount };
    }
    public object Combine(JsonElement inputs,string outputPath) {
        if(inputs.ValueKind!=JsonValueKind.Array || inputs.GetArrayLength()<2)
            throw new InvalidOperationException("Select at least two PDF files to combine.");
        var output=Path.GetFullPath(outputPath);
        if(File.Exists(output) || Directory.Exists(output))
            throw new InvalidOperationException("The output path already exists. Choose a new output path; existing files are never overwritten.");
        var directory=Path.GetDirectoryName(output)!;
        if(!Directory.Exists(directory)) throw new InvalidOperationException("The output folder does not exist or is unavailable.");

        var plans=new List<(string Path,string Range,int[] Pages)>(); long selectedPages=0;
        foreach(var item in inputs.EnumerateArray()) {
            if(item.ValueKind!=JsonValueKind.Object || !item.TryGetProperty("Path",out var pathValue) ||
               !item.TryGetProperty("Range",out var rangeValue) || pathValue.ValueKind!=JsonValueKind.String || rangeValue.ValueKind!=JsonValueKind.String)
                throw new InvalidOperationException("Each input must include a PDF path and page range.");
            var path=Path.GetFullPath(pathValue.GetString()!);
            if(string.Equals(path,output,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output path must not be one of the input files.");
            var range=rangeValue.GetString()!;
            using var source=OpenCombineSource(path);
            EnsureCombineSupported(source);
            var pages=ParseRange(range,source.PageCount,MaxCombinedPages);
            selectedPages+=pages.Length;
            if(selectedPages>MaxCombinedPages) throw new InvalidOperationException($"Combine supports up to {MaxCombinedPages:N0} selected pages per output.");
            plans.Add((path,range,pages));
        }
        if(selectedPages<1) throw new InvalidOperationException("The selected ranges contain no pages.");

        var hashes=plans.Select(x=>HashFile(x.Path)).ToArray();
        var temp=Path.Combine(directory,$".pdfeditor-combine-{Guid.NewGuid():N}.tmp");
        try {
            using(var merged=Pdf.Create()) {
                for(var planIndex=0;planIndex<plans.Count;planIndex++) {
                    var plan=plans[planIndex];
                    using var source=OpenCombineSource(plan.Path);
                    EnsureCombineSupported(source);
                    var current=HashFile(plan.Path);
                    if(!hashes[planIndex].SequenceEqual(current))
                        throw new InvalidOperationException("An input PDF changed during combine. No output was published; check the input files and try again.");
                    var range=plan.Range.Length==0 ? null : string.Join(",",plan.Pages.Select(x=>(x+1).ToString(CultureInfo.InvariantCulture)));
                    if(ImportPages(merged.Handle,source.Handle,range,merged.PageCount)==0)
                        throw new InvalidOperationException("PDFium could not import one of the selected page ranges. No output was published.");
                }
                var pageCount=merged.PageCount;
                if(pageCount!=selectedPages || pageCount<1)
                    throw new InvalidOperationException("The combined page count did not match the selected ranges. No output was published.");
                merged.Save(temp);
                using var reopened=Pdf.Open(temp);
                if(reopened.PageCount!=pageCount) throw new InvalidOperationException("The saved combined PDF failed page-count validation. No output was published.");
                for(var i=0;i<reopened.PageCount;i++) { using var page=reopened.Page(i); }
            }
            for(var i=0;i<plans.Count;i++)
                if(!hashes[i].SequenceEqual(HashFile(plans[i].Path)))
                    throw new InvalidOperationException("An input PDF changed during combine. No output was published; check the input files and try again.");
            try { File.Move(temp,output,false); }
            catch(IOException) when(File.Exists(output))
            { throw new InvalidOperationException("The output path became occupied. The existing file was preserved; choose another output path."); }
            return new { PageCount=checked((int)selectedPages) };
        } finally {
            try { if(File.Exists(temp)) File.Delete(temp); } catch(IOException) { }
        }
    }
    Pdf OpenCombineSource(string path) {
        var full=Path.GetFullPath(path);
        if(!File.Exists(full)) throw new InvalidOperationException("An input PDF cannot be found. Locate the file and try again.");
        try { return Pdf.Open(full); }
        catch(InvalidOperationException) {
            var error=LastError();
            if(error==4) throw new InvalidOperationException("Password-protected PDFs are not supported in Combine yet. Remove encryption or open an unprotected copy.");
            throw new InvalidOperationException("An input could not be opened as a supported PDF. Check that the file is valid and accessible.");
        }
    }
    static void EnsureCombineSupported(Pdf pdf) {
        if(SecurityHandlerRevision(pdf.Handle)!=-1)
            throw new InvalidOperationException("Password-protected PDFs are not supported in Combine yet. Remove encryption or open an unprotected copy.");
        var signatures=SignatureCount(pdf.Handle);
        if(signatures<0) throw new InvalidOperationException("PDF signature information could not be read; the file was not combined.");
        if(signatures>0) throw new InvalidOperationException("Signed PDFs are not supported in Combine. Keep the signed original and choose unsigned copies.");
        if(pdf.PageCount<1) throw new InvalidOperationException("An input PDF has no pages.");
    }
    static int[] ParseRange(string range,int pageCount,int maxPages) {
        if(string.IsNullOrWhiteSpace(range)) {
            if(pageCount>maxPages) throw new InvalidOperationException($"This input exceeds the {maxPages:N0}-page Combine limit.");
            return Enumerable.Range(0,pageCount).ToArray();
        }
        var pages=new List<int>(); var seen=new HashSet<int>();
        foreach(var raw in range.Split(',')) {
            var part=raw.Trim(); if(part.Length==0) throw new InvalidOperationException("A page range is empty or malformed.");
            var ends=part.Split('-');
            if(ends.Length>2 || !int.TryParse(ends[0],NumberStyles.None,CultureInfo.InvariantCulture,out var first) || first<1)
                throw new InvalidOperationException("Use page numbers such as 1,3-5; page numbers start at 1.");
            var last=first;
            if(ends.Length==2 && (!int.TryParse(ends[1],NumberStyles.None,CultureInfo.InvariantCulture,out last) || last<first))
                throw new InvalidOperationException("A page range must use ascending page numbers, such as 2-5.");
            if(last>pageCount) throw new InvalidOperationException($"Page {last} is outside this PDF, which has {pageCount} pages.");
            if((long)pages.Count+last-first+1>maxPages) throw new InvalidOperationException($"A range exceeds the {maxPages:N0}-page Combine limit.");
            for(var page=first;page<=last;page++) {
                if(!seen.Add(page)) throw new InvalidOperationException("A page appears more than once in the selected ranges.");
                pages.Add(page-1);
            }
        }
        return pages.ToArray();
    }
    static byte[] HashFile(string path) { using var input=File.OpenRead(path); return SHA256.HashData(input); }
    public long Revision(Guid id) => documents.TryGetValue(id,out var entry) ? entry.Revision : throw new InvalidOperationException("Document is not open.");
    Pdf Get(Guid id,long revision) { if(Revision(id)!=revision) throw new InvalidOperationException("Stale document revision."); return documents[id].Pdf; }
    public object Extract(Guid id,long revision,int index,bool includeCharacters=false) {
        var pdf=Get(id,revision); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var objects=new List<object>();
        var pageText=Pdf.Native.FPDFText_LoadPage(page.Handle);
        for(int i=0;i<Pdf.Native.FPDFPage_CountObjects(page.Handle);i++) {
            var obj=Pdf.Native.FPDFPage_GetObject(page.Handle,i);
            var bounded=Bounds(obj,out var left,out var bottom,out var right,out var top)!=0;
            var type=Pdf.Native.FPDFPageObj_GetType(obj);
            var text=type==1 && pageText!=0 ? ReadTextObject(obj,pageText) : "";
            var font=""; float fontSize=0; var canEdit=false; var editReason="Only top-level text objects can be replaced.";
            if(type==1) {
                var fontHandle=TextObjGetFont(obj);
                font=fontHandle==0 ? "" : ReadFontName(fontHandle);
                _=TextObjGetFontSize(obj,out fontSize);
                editReason=pageText==0 ? "The page text could not be read safely." : TextEditReason(obj,fontHandle,font,text);
                canEdit=editReason.Length==0;
            }
            var imageReason="Only top-level images can be edited.";
            if(type==3) {
                try { _=GetEditableImage(id,revision,index,$"{id:N}:{revision}:{index}:{i}",page.Handle); imageReason=""; }
                catch(InvalidOperationException ex) { imageReason=ex.Message; }
            }
            objects.Add(new { Id=$"{id:N}:{revision}:{index}:{i}", Type=type, HasBounds=bounded, Left=left, Bottom=bottom, Right=right, Top=top, Text=text, FontName=font, FontSize=fontSize, CanEdit=canEdit, EditReason=editReason, ImageEditReason=imageReason });
        }
        if(pageText!=0) Pdf.Native.FPDFText_ClosePage(pageText);
        var characters=new List<object>();
        if(includeCharacters) {
            var textPage=Pdf.Native.FPDFText_LoadPage(page.Handle);
            if(textPage!=0) try {
                var count=Math.Min(Pdf.Native.FPDFText_CountChars(textPage),10_000);
                for(var i=0;i<count;i++) {
                    var code=Pdf.Native.FPDFText_GetUnicode(textPage,i);
                    if(code>0x10ffff || (code>=0xd800 && code<=0xdfff)) code=0xfffd;
                    var bounded=Pdf.Native.FPDFText_GetCharBox(textPage,i,out var left,out var right,out var bottom,out var top)!=0;
                    characters.Add(new { Index=i, Text=code==0 ? "" : char.ConvertFromUtf32((int)code), HasBounds=bounded, Left=left, Bottom=bottom, Right=right, Top=top });
                }
            } finally { Pdf.Native.FPDFText_ClosePage(textPage); }
        }
        var annotations=new List<object>();
        var annotationCount=Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle);
        if(annotationCount<0 || annotationCount>10000) throw new InvalidOperationException("Annotation data could not be read safely.");
        var annotationTextCharacters=0;
        for(var i=0;i<Math.Min(annotationCount,1000);i++) {
            var annot=Pdf.Objects.FPDFPage_GetAnnot(page.Handle,i);
            if(annot==0) throw new InvalidOperationException("Annotation data could not be read safely.");
            try {
                var hasRect=Pdf.Objects.FPDFAnnot_GetRect(annot,out var rect);
                var subtype=Pdf.Objects.FPDFAnnot_GetSubtype(annot);
                var flags=Pdf.Objects.FPDFAnnot_GetFlags(annot);
                var contentsSize=subtype==1?Pdf.Objects.FPDFAnnot_GetStringValue(annot,"Contents",0,0):0;
                var contentsAvailable=subtype!=1 || (contentsSize<=16_384 && annotationTextCharacters+contentsSize/2<=524_288);
                var contents=subtype==1 && contentsAvailable ? AnnotationString(annot,"Contents") : null;
                annotationTextCharacters+=contents?.Length??0;
                annotations.Add(new { Id=$"{id:N}:{revision}:{index}:annotation:{i}", Index=i, Subtype=subtype, Flags=flags,
                    CanDelete=(subtype is 1 or 5 or 6 or 9 or 10 or 12 or 15) && (flags&(64|128|512))==0 && !HasAnnotationRelationships(annot),
                    ContentsAvailable=contentsAvailable,
                    HasBounds=hasRect!=0,
                    Left=rect.Left, Bottom=rect.Bottom, Right=rect.Right, Top=rect.Top,
                    Contents=contents });
            } finally { Pdf.Objects.FPDFPage_CloseAnnot(annot); }
        }
        return new { Text=page.Text(), Characters=characters, Objects=objects, Annotations=annotations,
            AnnotationCount=annotationCount, AnnotationsTruncated=annotationCount>1000,
            Width=Width(page.Handle), Height=Height(page.Handle), Rotation=Rotation(page.Handle),
            SupportsZeroOriginCoordinates=SupportsZeroOriginCoordinates(page.Handle), VisibleBox=VisibleBoxInfo(page.Handle) };
    }
    public object PageInfo(Guid id,long revision) {
        var pdf=Get(id,revision); var pages=new List<object>(pdf.PageCount);
        for(var i=0;i<pdf.PageCount;i++) { using var page=pdf.Page(i); pages.Add(new { Page=i, Width=Width(page.Handle), Height=Height(page.Handle), Rotation=Rotation(page.Handle), OriginX=VisibleOrigin(page.Handle).X, OriginY=VisibleOrigin(page.Handle).Y }); }
        return new { Pages=pages };
    }
    public object Bookmarks(Guid id,long revision) {
        var pdf=Get(id,revision); var result=new List<object>(); var count=0;
        void Walk(nint parent,int depth) {
            if(depth>16) return;
            var item=FirstBookmark(pdf.Handle,parent);
            while(item!=0 && count++<2000) {
                var title=new byte[8192]; var length=BookmarkTitle(item,title,(uint)title.Length);
                var name=length>2 && length<=title.Length && length%2==0
                    ? System.Text.Encoding.Unicode.GetString(title,0,checked((int)length)-2)
                    : "Bookmark";
                var dest=BookmarkDest(pdf.Handle,item); var page=dest==0 ? -1 : DestPageIndex(pdf.Handle,dest);
                result.Add(new { Title=name, Page=page, Depth=depth });
                Walk(item,depth+1); item=NextBookmark(pdf.Handle,item);
            }
        }
        Walk(0,0); return new { Bookmarks=result };
    }
    public object Render(Guid id,long revision,int index,int width,int height) {
        if(width<1 || height<1 || width>4096 || height>4096 || (long)width*height>8_000_000) throw new InvalidOperationException("Render size exceeds limit.");
        return RenderPage(IsolatedRenderDocument(id,revision),index,width,height,printing:false);
    }
    public void AddText(Guid id,long revision,int index,string text,double x,double y) {
        if(text.Length>10000 || !double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("Invalid text edit.");
        var pdf=Get(id,revision); CheckPage(pdf,index); using var page=pdf.Page(index);
        CheckEditable(id,pdf);
        var rotation=((Rotation(page.Handle)%4)+4)%4;
        if(rotation==0) page.AddText(text,x,y); else AddRotatedText(pdf.Handle,page.Handle,rotation,text,x,y);
        page.Generate(); documents[id]=(pdf,revision+1);
    }
    public void AddMarkup(Guid id,long revision,int index,string kind,JsonElement quads) {
        var subType=kind switch { "Highlight"=>9, "Underline"=>10, "Strikeout"=>12, _=>throw new InvalidOperationException("Supported markup types are Highlight, Underline, and Strikeout.") };
        if(quads.ValueKind!=JsonValueKind.Array || quads.GetArrayLength()<1 || quads.GetArrayLength()>100)
            throw new InvalidOperationException("Markup requires between 1 and 100 page rectangles.");
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var box=GetVisibleBox(page.Handle);
        var parsed=new List<(Pdf.Rect Rect,Pdf.Quad Quad)>();
        foreach(var item in quads.EnumerateArray()) {
            if(item.ValueKind!=JsonValueKind.Object || !TryNumber(item,"Left",out var left) || !TryNumber(item,"Bottom",out var bottom) ||
               !TryNumber(item,"Right",out var right) || !TryNumber(item,"Top",out var top) ||
               !double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top) ||
               right<=left || top<=bottom || !box.Contains(left,bottom,right,top))
                throw new InvalidOperationException("Markup bounds must be finite, nonempty rectangles inside the selected page.");
            var l=(float)left; var b=(float)bottom; var r=(float)right; var t=(float)top;
            if(!float.IsFinite(l) || !float.IsFinite(b) || !float.IsFinite(r) || !float.IsFinite(t) || r<=l || t<=b)
                throw new InvalidOperationException("Markup bounds are too small to represent safely in PDF page coordinates.");
            parsed.Add((new Pdf.Rect{Left=l,Bottom=b,Right=r,Top=t},new Pdf.Quad{X1=l,Y1=t,X2=r,Y2=t,X3=l,Y3=b,X4=r,Y4=b}));
        }
        foreach(var shape in parsed) {
            var annot=Pdf.Objects.FPDFPage_CreateAnnot(page.Handle,subType); Pdf.Check(annot!=0,"create markup annotation");
            try {
                var rect=shape.Rect; var quad=shape.Quad;
                Pdf.Check(PdfDrawing.SetFlags(annot,4),"make markup printable");
                Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annot,ref rect),"set markup bounds");
                Pdf.Check(Pdf.Objects.FPDFAnnot_AppendAttachmentPoints(annot,ref quad),"set markup quad");
                Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(annot,0,255,230,0,110),"set markup color");
            } finally { Pdf.Objects.FPDFPage_CloseAnnot(annot); }
        }
        documents[id]=(pdf,revision+1);
    }
    public void AddComment(Guid id,long revision,int index,string contents,double left,double bottom,double right,double top) {
        if(string.IsNullOrWhiteSpace(contents) || contents.Length>4000 || contents.Any(c=>char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidOperationException("Enter a comment of 1 to 4,000 printable characters.");
        if(!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top))
            throw new InvalidOperationException("Comment bounds must be finite PDF page coordinates.");
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var box=GetVisibleBox(page.Handle);
        if(right<=left || top<=bottom || !box.Contains(left,bottom,right,top))
            throw new InvalidOperationException("Comment bounds must be a nonempty rectangle inside the selected page.");
        var l=(float)left; var b=(float)bottom; var r=(float)right; var t=(float)top;
        if(!float.IsFinite(l) || !float.IsFinite(b) || !float.IsFinite(r) || !float.IsFinite(t) || r<=l || t<=b)
            throw new InvalidOperationException("Comment bounds are too small to represent safely in PDF page coordinates.");
        var annot=Pdf.Objects.FPDFPage_CreateAnnot(page.Handle,1); Pdf.Check(annot!=0,"create comment annotation");
        try {
            var rect=new Pdf.Rect{Left=l,Bottom=b,Right=r,Top=t};
            // NoRotate (16) is deliberately not set: PDFium then anchors the icon at the unrotated rectangle's top-left, which
            // lands it away from the clicked spot and is not honored identically by other readers. The icon turns with the page.
            Pdf.Check(PdfDrawing.SetFlags(annot,4),"make comment icon printable");
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetRect(annot,ref rect),"set comment bounds");
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetColor(annot,0,146,121,251,255),"set comment color");
            Pdf.Check(Pdf.Objects.FPDFAnnot_SetStringValue(annot,"Contents",contents),"set comment contents");
        } finally { Pdf.Objects.FPDFPage_CloseAnnot(annot); }
        documents[id]=(pdf,revision+1);
    }
    static string AnnotationString(nint annot,string key) {
        var size=Pdf.Objects.FPDFAnnot_GetStringValue(annot,key,0,0);
        if(size<2 || size>16_384) return "";
        var buffer=Marshal.AllocHGlobal(checked((int)size));
        try {
            var copied=Pdf.Objects.FPDFAnnot_GetStringValue(annot,key,buffer,size);
            if(copied<2 || copied>size) return "";
            return Marshal.PtrToStringUni(buffer,checked((int)(copied/2)-1)) ?? "";
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    static bool TryNumber(JsonElement element,string name,out double value) {
        value=0; return element.TryGetProperty(name,out var property) && property.ValueKind==JsonValueKind.Number && property.TryGetDouble(out value);
    }
    public void RotatePage(Guid id,long revision,int index,int rotation) {
        if(rotation<0 || rotation>3) throw new InvalidOperationException("Invalid rotation.");
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index); SetRotation(page.Handle,rotation); documents[id]=(pdf,revision+1);
    }
    /// <summary>Turns each listed page by the same number of quarter turns clockwise, as one edit.</summary>
    public void RotatePages(Guid id,long revision,int[] pages,int quarterTurns) {
        if(quarterTurns is < 1 or > 3) throw new InvalidOperationException("Choose a rotation of 90, 180 or 270 degrees.");
        var pdf=Get(id,revision); CheckEditable(id,pdf);
        if(pages is null || pages.Length<1 || pages.Length>50_000 || pages.Distinct().Count()!=pages.Length)
            throw new InvalidOperationException("Select one or more distinct pages to rotate.");
        foreach(var index in pages) CheckPage(pdf,index);
        foreach(var index in pages) { using var page=pdf.Page(index); SetRotation(page.Handle,(Rotation(page.Handle)+quarterTurns)%4); }
        documents[id]=(pdf,revision+1);
    }
    public void ReplaceText(Guid id,long revision,int index,string objectId,string replacement) {
        if(string.IsNullOrWhiteSpace(replacement) || replacement.Length>10000)
            throw new InvalidOperationException("Enter replacement text of 1 to 10,000 characters.");
        if(replacement.Any(c=>c>0x7e || (c<0x20 && c!='\n' && c!='\r' && c!='\t')))
            throw new InvalidOperationException("This first text-editing workflow supports printable basic ASCII only. Other characters are rejected to avoid font substitution.");
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        var parts=objectId.Split(':');
        if(parts.Length!=4 || !Guid.TryParseExact(parts[0],"N",out var objectDocument) || objectDocument!=id ||
           !long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var objectRevision) || objectRevision!=revision ||
           !int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out var objectPage) || objectPage!=index ||
           !int.TryParse(parts[3],NumberStyles.None,CultureInfo.InvariantCulture,out var objectIndex) || objectIndex<0)
            throw new InvalidOperationException("This text selection is stale or belongs to another page. Refresh the page and select the text again.");
        using var page=pdf.Page(index);
        if(objectIndex>=Pdf.Native.FPDFPage_CountObjects(page.Handle)) throw new InvalidOperationException("This text selection is stale. Refresh the page and select the text again.");
        var obj=Pdf.Native.FPDFPage_GetObject(page.Handle,objectIndex);
        if(Pdf.Native.FPDFPageObj_GetType(obj)!=1)
            throw new InvalidOperationException("Only a top-level existing text object can be replaced. Nested text and other objects are not supported by this workflow.");
        var fontHandle=TextObjGetFont(obj); var fontName=fontHandle==0 ? "" : ReadFontName(fontHandle);
        var textPage=Pdf.Native.FPDFText_LoadPage(page.Handle);
        if(textPage==0) throw new InvalidOperationException("The selected page's text could not be read safely.");
        try {
            var before=ReadTextObject(obj,textPage);
            if(string.IsNullOrEmpty(before)) throw new InvalidOperationException("The selected text object is empty or cannot be mapped to text.");
            var reason=TextEditReason(obj,fontHandle,fontName,before);
            if(reason.Length!=0) throw new InvalidOperationException(reason);
            if(Bounds(obj,out var oldLeft,out var oldBottom,out var oldRight,out var oldTop)==0)
                throw new InvalidOperationException("The selected text object's bounds cannot be verified safely.");
            var neighbors=new List<(float Left,float Bottom,float Right,float Top)>();
            for(var i=0;i<Pdf.Native.FPDFPage_CountObjects(page.Handle);i++) if(i!=objectIndex) {
                var other=Pdf.Native.FPDFPage_GetObject(page.Handle,i);
                if(Bounds(other,out var l,out var b,out var r,out var t)!=0) neighbors.Add((l,b,r,t));
            }
            var changed=false;
            try {
                if(Pdf.Native.FPDFText_SetText(obj,replacement)==0)
                    throw new InvalidOperationException("PDFium could not map the replacement through this object's existing font. The document was not changed.");
                changed=true;
                Pdf.Native.FPDFText_ClosePage(textPage); textPage=0;
                page.Generate();
                if(Bounds(obj,out var left,out var bottom,out var right,out var top)==0)
                    throw new InvalidOperationException("The replacement bounds cannot be verified safely. The edit was discarded.");
                var pageWidth=Width(page.Handle); var pageHeight=Height(page.Handle);
                if(left < -0.01 || bottom < -0.01 || right > pageWidth+0.01 || top > pageHeight+0.01)
                    throw new InvalidOperationException("The replacement extends beyond the page. Shorten it or move the text in a supported workflow; the edit was discarded.");
                if(neighbors.Any(n=>left<n.Right && right>n.Left && bottom<n.Top && top>n.Bottom))
                    throw new InvalidOperationException("The replacement overlaps other page content. Shorten it to fit the original space; the edit was discarded.");
                textPage=Pdf.Native.FPDFText_LoadPage(page.Handle);
                if(textPage==0) throw new InvalidOperationException("The replacement text could not be reopened for verification. The edit was discarded.");
                var verified=ReadTextObject(obj,textPage);
                if(!string.Equals(verified,replacement,StringComparison.Ordinal))
                    throw new InvalidOperationException("The existing font did not preserve every replacement character. The edit was discarded.");
            } catch {
                if(changed) {
                    try {
                        if(textPage!=0) { Pdf.Native.FPDFText_ClosePage(textPage); textPage=0; }
                        if(Pdf.Native.FPDFText_SetText(obj,before)==0) throw new InvalidOperationException();
                        page.Generate();
                        if(Bounds(obj,out _,out _,out _,out _)==0) throw new InvalidOperationException();
                        textPage=Pdf.Native.FPDFText_LoadPage(page.Handle);
                        if(textPage==0 || !string.Equals(ReadTextObject(obj,textPage),before,StringComparison.Ordinal)) throw new InvalidOperationException();
                    } catch {
                        if(textPage!=0) { Pdf.Native.FPDFText_ClosePage(textPage); textPage=0; }
                        // Invalidate every caller holding the old revision; the worker/session must reopen its checkpoint.
                        documents[id]=(pdf,revision+1);
                        throw new InvalidOperationException("This edit could not be reverted safely. Reopen the document from its last saved checkpoint.");
                    }
                }
                throw;
            }
        } finally { if(textPage!=0) Pdf.Native.FPDFText_ClosePage(textPage); }
        documents[id]=(pdf,revision+1);
    }
    static string TextEditReason(nint obj,nint fontHandle,string fontName,string text) {
        var mode=TextObjGetRenderMode(obj);
        if(mode<0 || mode>=4) return "Text used as a clipping path cannot be replaced safely.";
        if(PageObjectCountMarks(obj)!=0) return "Tagged or marked-content text is not supported by this text editor yet.";
        var clip=PdfObjectGetClipPath(obj);
        // GetClipPath returns the live object's clip wrapper even when it has no backing
        // reference. PDFium reports -1 for that absent state; any referenced clip is rejected.
        if(clip!=0 && ClipPathCountPaths(clip)>=0) return "Text with an attached clipping path cannot be replaced safely.";
        if(fontHandle==0 || string.IsNullOrWhiteSpace(fontName)) return "The text object's font cannot be read safely.";
        if(!IsStandardTextFont(fontName)) return "This first text-editing workflow supports standard PDF fonts only. This font representation is not yet supported, so the document was left unchanged.";
        if(text.Any(c=>c>0x7e || (c<0x20 && c!='\n' && c!='\r' && c!='\t'))) return "This first text-editing workflow supports printable basic ASCII only. Other characters are rejected to avoid font substitution.";
        if(TextObjectMatrix(obj,out var matrix)==0 || Math.Abs(matrix.A-1)>0.001 || Math.Abs(matrix.D-1)>0.001 || Math.Abs(matrix.B)>0.001 || Math.Abs(matrix.C)>0.001)
            return "Rotated or scaled text objects cannot be replaced safely yet.";
        return "";
    }
    static bool IsStandardTextFont(string name) => name is "Helvetica" or "Helvetica-Bold" or "Helvetica-Oblique" or "Helvetica-BoldOblique"
        or "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic"
        or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique";
    static string ReadTextObject(nint obj,nint textPage) {
        var bytes=TextObjGetText(obj,textPage,null,0);
        if(bytes==0 || bytes>1_000_000) return "";
        var data=new ushort[checked((int)((bytes+1)/2))];
        var copied=TextObjGetText(obj,textPage,data,bytes);
        if(copied==0) return "";
        var length=Array.IndexOf(data,(ushort)0); if(length<0) length=data.Length;
        return new string(data.Take(length).Select(x=>(char)x).ToArray());
    }
    static string ReadFontName(nint font) {
        var size=FontGetBaseName(font,null,0);
        if(size==0 || size>4096) return "";
        var bytes=new byte[checked((int)size)]; var copied=FontGetBaseName(font,bytes,(nuint)bytes.Length);
        return copied==0 ? "" : System.Text.Encoding.UTF8.GetString(bytes,0,Math.Min(checked((int)copied)-1,bytes.Length));
    }
    void CheckEditable(Guid id,Pdf pdf) {
        var count=SignatureCount(pdf.Handle);
        if(count<0 || (count>0 && !signedCopyConsent.Contains(id))) throw new InvalidOperationException("Explicit signed-copy consent is required.");
    }
    public object Save(Guid id,long revision,string path) {
        Get(id,revision).Save(path);
        renderSnapshots.TryGetValue(id,out var previous);
        SetRenderSnapshot(id,revision,path,previous.Password);
        return new { Saved=true };
    }
    public object Validate(string path,string? password) { using var pdf=Pdf.Open(path,password); if(pdf.PageCount<1) throw new InvalidOperationException("No pages."); for(int i=0;i<pdf.PageCount;i++){using var page=pdf.Page(i);} return new { PageCount=pdf.PageCount }; }
    public void Close(Guid id,long revision) { var pdf=Get(id,revision); DisposeRenderCopy(id); renderSnapshots.Remove(id); documents.Remove(id); signedCopyConsent.Remove(id); navigationValidatedDocuments.Remove(id); pdf.Dispose(); }
    static void CheckPage(Pdf pdf,int page) { if(page<0 || page>=pdf.PageCount) throw new InvalidOperationException("Invalid page index."); }
    [DllImport("pdfium",EntryPoint="FPDFPageObj_GetBounds")] static extern int Bounds(nint obj,out float left,out float bottom,out float right,out float top);
    [DllImport("pdfium",EntryPoint="FPDF_GetPageWidthF")] static extern float Width(nint page);
    [DllImport("pdfium",EntryPoint="FPDF_GetPageHeightF")] static extern float Height(nint page);
    [DllImport("pdfium",EntryPoint="FPDF_GetSignatureCount")] static extern int SignatureCount(nint document);
    [DllImport("pdfium",EntryPoint="FPDF_GetSecurityHandlerRevision")] static extern int SecurityHandlerRevision(nint document);
    [DllImport("pdfium",EntryPoint="FPDF_GetLastError")] static extern uint LastError();
    [DllImport("pdfium",EntryPoint="FPDF_ImportPages",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] static extern int ImportPages(nint destination,nint source,string? pageRange,int insertIndex);
    [DllImport("pdfium",EntryPoint="FPDFTextObj_GetText",CallingConvention=CallingConvention.Cdecl)] static extern uint TextObjGetText(nint textObject,nint textPage,[Out] ushort[]? buffer,uint length);
    [DllImport("pdfium",EntryPoint="FPDFTextObj_GetFont",CallingConvention=CallingConvention.Cdecl)] static extern nint TextObjGetFont(nint textObject);
    [DllImport("pdfium",EntryPoint="FPDFTextObj_GetFontSize",CallingConvention=CallingConvention.Cdecl)] static extern int TextObjGetFontSize(nint textObject,out float size);
    [DllImport("pdfium",EntryPoint="FPDFTextObj_GetTextRenderMode",CallingConvention=CallingConvention.Cdecl)] static extern int TextObjGetRenderMode(nint textObject);
    [DllImport("pdfium",EntryPoint="FPDFFont_GetBaseFontName",CallingConvention=CallingConvention.Cdecl)] static extern nuint FontGetBaseName(nint font,[Out] byte[]? buffer,nuint length);
    [StructLayout(LayoutKind.Sequential)] struct TextMatrix { public float A,B,C,D,E,F; }
    [DllImport("pdfium",EntryPoint="FPDFPageObj_GetMatrix",CallingConvention=CallingConvention.Cdecl)] static extern int TextObjectMatrix(nint obj,out TextMatrix matrix);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_GetClipPath",CallingConvention=CallingConvention.Cdecl)] static extern nint PdfObjectGetClipPath(nint obj);
    [DllImport("pdfium",EntryPoint="FPDFClipPath_CountPaths",CallingConvention=CallingConvention.Cdecl)] static extern int ClipPathCountPaths(nint clipPath);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_CountMarks",CallingConvention=CallingConvention.Cdecl)] static extern int PageObjectCountMarks(nint obj);
    [DllImport("pdfium",EntryPoint="FPDFPage_GetRotation")] static extern int Rotation(nint page);
    [DllImport("pdfium",EntryPoint="FPDFPage_SetRotation")] static extern void SetRotation(nint page,int rotation);
    [DllImport("pdfium",EntryPoint="FPDFBookmark_GetFirstChild")] static extern nint FirstBookmark(nint document,nint bookmark);
    [DllImport("pdfium",EntryPoint="FPDFBookmark_GetNextSibling")] static extern nint NextBookmark(nint document,nint bookmark);
    [DllImport("pdfium",EntryPoint="FPDFBookmark_GetTitle")] static extern uint BookmarkTitle(nint bookmark,[Out] byte[] buffer,uint length);
    [DllImport("pdfium",EntryPoint="FPDFBookmark_GetDest")] static extern nint BookmarkDest(nint document,nint bookmark);
    [DllImport("pdfium",EntryPoint="FPDFDest_GetDestPageIndex")] static extern int DestPageIndex(nint document,nint destination);
    public void Dispose(){foreach(var id in renderCopies.Keys.ToArray()) DisposeRenderCopy(id); renderSnapshots.Clear(); foreach(var entry in documents.Values) entry.Pdf.Dispose(); documents.Clear(); Pdf.Native.FPDF_DestroyLibrary();}
}
