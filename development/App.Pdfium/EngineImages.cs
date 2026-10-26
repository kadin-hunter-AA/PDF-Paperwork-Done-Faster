using System.Globalization;
using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    const int MaxImagePixels=4_000_000;
    const int MaxImageEdge=4096;
    const int MaxImageBytes=MaxImagePixels*4;
    const int BitmapFormatBgra=4;

    public object AddImage(Guid id,long revision,int index,string path,int width,int height,double left,double bottom,double right,double top)
    {
        ValidateRaster(path,width,height);
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var box=GetVisibleBox(page.Handle);
        ValidatePlacement(left,bottom,right,top,box);
        if(Math.Abs(DisplayAspect(box.Rotation,left,bottom,right,top)-(double)width/height)>0.01)
            throw new InvalidOperationException("Image placement must preserve the source image's aspect ratio.");
        var image=NewImageObject(pdf.Handle);
        Pdf.Check(image!=0,"create image object");
        var inserted=false;
        try
        {
            using var raster=ReadRaster(path,width,height);
            Pdf.Check(SetImageBitmap([page.Handle],1,image,raster.Handle),"set image pixels");
            Pdf.Check(SetUprightMatrix(image,box.Rotation,left,bottom,right,top),"set image placement");
            InsertImageObject(page.Handle,image);
            inserted=true;
            page.Generate();
            EnsureImageBounds(image,left,bottom,right,top);
            documents[id]=(pdf,revision+1);
            return new { Applied=true };
        }
        catch { if(!inserted)DestroyImageObject(image); throw; }
    }

    public object ReplaceImage(Guid id,long revision,int index,string objectId,string path,int width,int height)
    {
        ValidateRaster(path,width,height);
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var (obj,objIndex)=GetEditableImage(id,revision,index,objectId,page.Handle);
        if(Bounds(obj,out var l,out var b,out var r,out var t)==0)
            throw new InvalidOperationException("The selected image bounds cannot be verified safely.");
        if(r<=l||t<=b||Math.Abs(DisplayAspect(Rotation(page.Handle),l,b,r,t)-(double)width/height)>0.01)
            throw new InvalidOperationException("Replacement images must match the selected image's aspect ratio so its placement is preserved.");
        using(var raster=ReadRaster(path,width,height))
            Pdf.Check(SetImageBitmap([page.Handle],1,obj,raster.Handle),"replace selected image pixels");
        page.Generate();
        EnsureImageBounds(obj,l,b,r,t);
        if(Pdf.Native.FPDFPage_GetObject(page.Handle,objIndex)!=obj)
            throw new InvalidOperationException("The selected image changed while replacing it. Reopen the last committed checkpoint.");
        documents[id]=(pdf,revision+1);
        return new { Applied=true };
    }

    public object TransformImage(Guid id,long revision,int index,string objectId,double left,double bottom,double right,double top)
    {
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var (obj,_)=GetEditableImage(id,revision,index,objectId,page.Handle);
        var box=GetVisibleBox(page.Handle);
        ValidatePlacement(left,bottom,right,top,box);
        if(Bounds(obj,out var oldLeft,out var oldBottom,out var oldRight,out var oldTop)==0)
            throw new InvalidOperationException("The selected image bounds cannot be verified safely.");
        var oldWidth=oldRight-oldLeft; var oldHeight=oldTop-oldBottom;
        var newWidth=right-left; var newHeight=top-bottom;
        if(oldWidth<=0 || oldHeight<=0 || Math.Abs(newWidth/oldWidth-newHeight/oldHeight)>0.01)
            throw new InvalidOperationException("Resize the image with its original aspect ratio.");
        Pdf.Check(SetUprightMatrix(obj,box.Rotation,left,bottom,right,top),"transform image placement");
        page.Generate(); EnsureImageBounds(obj,left,bottom,right,top);
        documents[id]=(pdf,revision+1);
        return new { Applied=true };
    }

    public object DeleteImage(Guid id,long revision,int index,string objectId)
    {
        var pdf=Get(id,revision); CheckEditable(id,pdf); CheckPage(pdf,index);
        using var page=pdf.Page(index);
        var (obj,_)=GetEditableImage(id,revision,index,objectId,page.Handle);
        Pdf.Check(RemoveImageObject(page.Handle,obj),"remove selected image");
        DestroyImageObject(obj);
        page.Generate();
        documents[id]=(pdf,revision+1);
        return new { Applied=true };
    }

    static (nint Object,int Index) GetEditableImage(Guid id,long revision,int pageIndex,string objectId,nint page)
    {
        var parts=objectId.Split(':');
        if(parts.Length!=4 || parts[0]!=id.ToString("N") ||
            parts[1]!=revision.ToString(CultureInfo.InvariantCulture) ||
            parts[2]!=pageIndex.ToString(CultureInfo.InvariantCulture) ||
            !int.TryParse(parts[3],NumberStyles.None,CultureInfo.InvariantCulture,out var objectIndex) || objectIndex<0 ||
            objectIndex>=Pdf.Native.FPDFPage_CountObjects(page))
            throw new InvalidOperationException("The selected image is stale. Refresh the page and select it again.");
        var obj=Pdf.Native.FPDFPage_GetObject(page,objectIndex);
        if(obj==0 || Pdf.Native.FPDFPageObj_GetType(obj)!=3)
            throw new InvalidOperationException("Only a top-level image object can be edited in this workflow.");
        if(ImageObjectCountMarks(obj)!=0)
            throw new InvalidOperationException("Tagged or marked image objects are not supported by this image editor yet.");
        var clip=GetImageClipPath(obj);
        if(clip!=0 && ImageClipPathCount(clip)>=0)
            throw new InvalidOperationException("Clipped image objects are not supported by this image editor yet.");
        if(Bounds(obj,out var left,out var bottom,out var right,out var top)==0 ||
           !float.IsFinite(left)||!float.IsFinite(bottom)||!float.IsFinite(right)||!float.IsFinite(top)||right<=left||top<=bottom)
            throw new InvalidOperationException("The selected image bounds cannot be verified safely.");
        Pdf.Check(GetImageObjectMatrix(obj,out var matrix),"read selected image placement");
        if(!IsUprightOnDisplay(matrix,Rotation(page)))
            throw new InvalidOperationException("Images that are not upright on the displayed page (rotated, mirrored, or sheared) are not supported by this workflow yet.");
        return (obj,objectIndex);
    }

    internal static string GetImageEditReason(nint image,nint page)
    {
        if(image==0||Pdf.Native.FPDFPageObj_GetType(image)!=3)return "Only top-level image objects can be edited.";
        if(ImageObjectCountMarks(image)!=0)return "Tagged or marked image objects are not supported by this image editor yet.";
        var clip=GetImageClipPath(image);
        if(clip!=0&&ImageClipPathCount(clip)>=0)return "Clipped image objects are not supported by this image editor yet.";
        if(Bounds(image,out var l,out var b,out var r,out var t)==0||!float.IsFinite(l)||!float.IsFinite(b)||!float.IsFinite(r)||!float.IsFinite(t)||r<=l||t<=b)
            return "The image bounds cannot be verified safely.";
        if(GetImageObjectMatrix(image,out var matrix)==0||!IsUprightOnDisplay(matrix,Rotation(page)))
            return "Images that are not upright on the displayed page (rotated, mirrored, or sheared) are not supported by this workflow yet.";
        return "";
    }

    /// <summary>Width/height of the image as the reader sees it, given the user-space footprint and the page rotation.</summary>
    static double DisplayAspect(int rotation,double l,double b,double r,double t)=>rotation%2==1?(t-b)/(r-l):(r-l)/(t-b);
    static int SetUprightMatrix(nint image,int rotation,double l,double b,double r,double t)
    {
        var m=UprightMatrix(rotation,l,b,r,t);
        return ImageObjSetMatrix(image,m.A,m.B,m.C,m.D,m.E,m.F);
    }
    /// <summary>True when the image matrix draws the image upright, unmirrored and unsheared on the displayed (rotated) page.</summary>
    static bool IsUprightOnDisplay(ImageMatrix m,int rotation)
    {
        if(!float.IsFinite(m.A)||!float.IsFinite(m.B)||!float.IsFinite(m.C)||!float.IsFinite(m.D)||!float.IsFinite(m.E)||!float.IsFinite(m.F)) return false;
        var (ux,uy,vx,vy)=UprightAxes(((rotation%4)+4)%4);
        // Signs must match the expected axes; the off-axis terms must vanish.
        bool Ok(double actual,double axis,double off)=>axis==0?Math.Abs(actual)<=0.001:(Math.Sign(actual)==Math.Sign(axis)&&Math.Abs(actual)>0);
        return Ok(m.A,ux,0)&&Ok(m.B,uy,0)&&Ok(m.C,vx,0)&&Ok(m.D,vy,0);
    }
    static void ValidatePlacement(double left,double bottom,double right,double top,VisibleBox box)
    {
        if(!double.IsFinite(left)||!double.IsFinite(bottom)||!double.IsFinite(right)||!double.IsFinite(top)||
           right<=left||top<=bottom||!box.Contains(left,bottom,right,top)||
           right-left>box.UserWidth+.01||top-bottom>box.UserHeight+.01||
           !float.IsFinite((float)left)||!float.IsFinite((float)bottom)||!float.IsFinite((float)right)||!float.IsFinite((float)top)||
           (float)right<=(float)left||(float)top<=(float)bottom)
            throw new InvalidOperationException("Image placement must be a finite, nonempty rectangle inside the current page.");
    }
    static void EnsureImageBounds(nint image,double left,double bottom,double right,double top)
    {
        if(Bounds(image,out var l,out var b,out var r,out var t)==0||
            !float.IsFinite(l)||!float.IsFinite(b)||!float.IsFinite(r)||!float.IsFinite(t)||r<=l||t<=b||
            Math.Abs(l-left)>0.05||Math.Abs(b-bottom)>0.05||Math.Abs(r-right)>0.05||Math.Abs(t-top)>0.05)
            throw new InvalidOperationException("The image placement could not be verified. The edit was discarded.");
    }
    static void ValidateRaster(string path,int width,int height)
    {
        if(width<1||height<1||width>MaxImageEdge||height>MaxImageEdge||(long)width*height>MaxImagePixels)
            throw new InvalidOperationException("Images are limited to 4 million pixels and 4096 pixels on either edge.");
        var expected=checked((long)width*height*4);
        var info=new FileInfo(path);
        if(!Path.IsPathFullyQualified(path)||!info.Exists||info.Length!=expected||info.Length>MaxImageBytes)
            throw new InvalidOperationException("The temporary decoded image is invalid or exceeds its size limit.");
    }
    static RasterBitmap ReadRaster(string path,int width,int height)
    {
        var bytes=new byte[checked(width*height*4)];
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(file.Length!=bytes.Length) throw new InvalidOperationException("The temporary decoded image changed or is truncated.");
            file.ReadExactly(bytes);
        }
        return new RasterBitmap(width,height,bytes);
    }
    sealed class RasterBitmap : IDisposable
    {
        readonly byte[] bytes;
        readonly GCHandle pin;
        public nint Handle { get; }
        public RasterBitmap(int width,int height,byte[] bytes)
        {
            this.bytes=bytes; pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try { Handle=BitmapCreateEx(width,height,BitmapFormatBgra,pin.AddrOfPinnedObject(),checked(width*4)); Pdf.Check(Handle!=0,"create image bitmap"); }
            catch { pin.Free(); throw; }
        }
        public void Dispose() { if(Handle!=0) Pdf.Native.FPDFBitmap_Destroy(Handle); if(pin.IsAllocated)pin.Free(); GC.KeepAlive(bytes); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ImageMatrix { public float A,B,C,D,E,F; }
    [DllImport("pdfium",EntryPoint="FPDFImageObj_SetMatrix",CallingConvention=CallingConvention.Cdecl)] static extern int ImageObjSetMatrix(nint image,double a,double b,double c,double d,double e,double f);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_GetMatrix",CallingConvention=CallingConvention.Cdecl)] static extern int GetImageObjectMatrix(nint image,out ImageMatrix matrix);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_NewImageObj",CallingConvention=CallingConvention.Cdecl)] static extern nint NewImageObject(nint document);
    [DllImport("pdfium",EntryPoint="FPDFImageObj_SetBitmap",CallingConvention=CallingConvention.Cdecl)] static extern int SetImageBitmap(nint[] pages,int count,nint image,nint bitmap);
    [DllImport("pdfium",EntryPoint="FPDFPage_InsertObject",CallingConvention=CallingConvention.Cdecl)] static extern void InsertImageObject(nint page,nint image);
    [DllImport("pdfium",EntryPoint="FPDFPage_RemoveObject",CallingConvention=CallingConvention.Cdecl)] static extern int RemoveImageObject(nint page,nint image);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_Destroy",CallingConvention=CallingConvention.Cdecl)] static extern void DestroyImageObject(nint image);
    [DllImport("pdfium",EntryPoint="FPDFBitmap_CreateEx",CallingConvention=CallingConvention.Cdecl)] static extern nint BitmapCreateEx(int width,int height,int format,nint buffer,int stride);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_GetClipPath",CallingConvention=CallingConvention.Cdecl)] static extern nint GetImageClipPath(nint image);
    [DllImport("pdfium",EntryPoint="FPDFClipPath_CountPaths",CallingConvention=CallingConvention.Cdecl)] static extern int ImageClipPathCount(nint clipPath);
    [DllImport("pdfium",EntryPoint="FPDFPageObj_CountMarks",CallingConvention=CallingConvention.Cdecl)] static extern int ImageObjectCountMarks(nint image);
}
