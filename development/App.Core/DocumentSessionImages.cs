namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    const int MaxImagePixels=4_000_000;
    const int MaxImageEdge=4096;
    const int MaxImageBytes=MaxImagePixels*4;

    public Task AddImageAsync(int page,byte[] bgra,int width,int height,double left,double bottom,double right,double top,CancellationToken cancellation=default) =>
        WithRasterAsync(bgra,width,height,(path)=>EditAsync("AddImage",new { Page=page,Path=path,Width=width,Height=height,Left=left,Bottom=bottom,Right=right,Top=top },cancellation),cancellation);

    public Task ReplaceImageAsync(int page,string objectId,byte[] bgra,int width,int height,CancellationToken cancellation=default) =>
        WithRasterAsync(bgra,width,height,(path)=>EditAsync("ReplaceImage",new { Page=page,ObjectId=objectId,Path=path,Width=width,Height=height },cancellation),cancellation);

    public Task TransformImageAsync(int page,string objectId,double left,double bottom,double right,double top,CancellationToken cancellation=default) =>
        EditAsync("TransformImage",new { Page=page,ObjectId=objectId,Left=left,Bottom=bottom,Right=right,Top=top },cancellation);

    public Task DeleteImageAsync(int page,string objectId,CancellationToken cancellation=default) =>
        EditAsync("DeleteImage",new { Page=page,ObjectId=objectId },cancellation);

    async Task WithRasterAsync(byte[] bgra,int width,int height,Func<string,Task> edit,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bgra);
        if(width<1||height<1||width>MaxImageEdge||height>MaxImageEdge||(long)width*height>MaxImagePixels)
            throw new ArgumentOutOfRangeException(nameof(width),"Images are limited to 4 million pixels and 4096 pixels on either edge.");
        var expected=checked(width*height*4);
        if(bgra.Length!=expected||bgra.Length>MaxImageBytes)
            throw new ArgumentException("Decoded BGRA data must exactly match the bounded image dimensions.",nameof(bgra));
        var directory=store.DirectoryFor(Id);RecoveryStore.RejectReparse(directory);
        var path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".bgra");
        try
        {
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,64*1024,FileOptions.WriteThrough))
            { await file.WriteAsync(bgra,cancellation); file.Flush(true); }
            await edit(path);
        }
        finally { try { if(File.Exists(path)) File.Delete(path); } catch(IOException) { } }
    }
}
