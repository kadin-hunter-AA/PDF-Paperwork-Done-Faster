using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HunterPdf.Desktop;

internal sealed record DecodedImage(int Width,int Height,byte[] Bgra);

internal static class WicImageDecoder
{
    const long MaxCompressedBytes=16*1024*1024;
    const int MaxPixels=4_000_000;
    const int MaxEdge=4096;
    public static Task<DecodedImage> DecodeAsync(string path,CancellationToken cancellation=default)=>Task.Run(()=>Decode(path,cancellation),cancellation);
    static DecodedImage Decode(string path,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info=new FileInfo(path);
        if(!info.Exists||info.Length<1||info.Length>MaxCompressedBytes)
            throw new InvalidOperationException("Choose an image file no larger than 16 MB.");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<1||stream.Length>MaxCompressedBytes)
            throw new InvalidOperationException("Choose an image file no larger than 16 MB.");
        var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
        if(decoder.Frames.Count!=1) throw new InvalidOperationException("Choose a single-frame image; animated or multi-page images are not supported yet.");
        var frame=decoder.Frames[0]; var width=frame.PixelWidth; var height=frame.PixelHeight;
        if(width<1||height<1||width>MaxEdge||height>MaxEdge||(long)width*height>MaxPixels)
            throw new InvalidOperationException("Decoded images are limited to 4 million pixels and 4096 pixels on either edge.");
        cancellation.ThrowIfCancellationRequested();
        var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
        var pixels=new byte[checked(width*height*4)];
        converted.CopyPixels(pixels,width*4,0);
        cancellation.ThrowIfCancellationRequested();
        return new DecodedImage(width,height,pixels);
    }
}
