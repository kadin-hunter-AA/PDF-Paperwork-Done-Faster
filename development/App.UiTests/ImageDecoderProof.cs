using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class ImageDecoderProof
{
    public static async Task RunAsync(string root)
    {
        var output=Path.Combine(root,"artifacts","image-decoder-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        async Task Rejected<T>(Func<Task> action,string name,string contains) where T:Exception
        {try{await action();}catch(T e){if(!e.Message.Contains(contains,StringComparison.OrdinalIgnoreCase))throw;checks.Add(name);return;}throw new Exception(name);}
        var transparent=Path.Combine(output,"transparent.png");
        WritePng(transparent,2,1,[0,0,255,128,0,255,0,255]);
        var sourceHash=SHA256.HashData(File.ReadAllBytes(transparent));
        var decoded=await WicImageDecoder.DecodeAsync(transparent);
        Check(decoded.Width==2&&decoded.Height==1&&decoded.Bgra.Length==8,"PNG is decoded to bounded BGRA dimensions");
        Check(decoded.Bgra[0]==0&&decoded.Bgra[1]==0&&decoded.Bgra[2]==255&&decoded.Bgra[3]==128,"Decoder preserves straight alpha without premultiplying color");
        Check(decoded.Bgra[4]==0&&decoded.Bgra[5]==255&&decoded.Bgra[6]==0&&decoded.Bgra[7]==255,"Decoder preserves opaque BGRA channels");
        Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(transparent))),"Decoder leaves the selected source image unchanged");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            await Rejected<OperationCanceledException>(()=>WicImageDecoder.DecodeAsync(transparent,canceled.Token),"Pre-canceled image decode stops before decoding","canceled");
        }
        var edge=Path.Combine(output,"too-wide.png");WritePng(edge,4097,1,new byte[4097*4]);
        await Rejected<InvalidOperationException>(()=>WicImageDecoder.DecodeAsync(edge),"Oversized image dimensions are rejected before pixel allocation","4 million pixels");
        var multiFrame=Path.Combine(output,"multi-frame.tiff");WriteMultiFrameTiff(multiFrame);
        await Rejected<InvalidOperationException>(()=>WicImageDecoder.DecodeAsync(multiFrame),"Multi-frame TIFF is rejected instead of silently dropping pages","single-frame image");
        var malformed=Path.Combine(output,"malformed.png");File.WriteAllBytes(malformed,[0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a]);
        await Rejected<Exception>(()=>WicImageDecoder.DecodeAsync(malformed),"Malformed PNG is refused safely","");
        await Rejected<InvalidOperationException>(()=>WicImageDecoder.DecodeAsync(Path.Combine(output,"missing.png")),"Unavailable image path is refused","16 MB");
        var tooLarge=Path.Combine(output,"too-large.bin");using(var file=new FileStream(tooLarge,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.SetLength(16L*1024*1024+1);
        await Rejected<InvalidOperationException>(()=>WicImageDecoder.DecodeAsync(tooLarge),"Compressed input above 16 MB is rejected before decode","16 MB");
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {Passed=checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {checks.Count} image decoder checks. Evidence: {output}");
    }

    static void WritePng(string path,int width,int height,byte[] pixels)
    {
        if(pixels.Length!=checked(width*height*4))throw new ArgumentException(nameof(pixels));
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);encoder.Save(stream);
    }
    static void WriteMultiFrameTiff(string path)
    {
        var first=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255,0,0,255,255},8);first.Freeze();
        var second=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,0,0,255,255,0,0,255},8);second.Freeze();
        var encoder=new TiffBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(first));encoder.Frames.Add(BitmapFrame.Create(second));
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);encoder.Save(stream);
    }
}
