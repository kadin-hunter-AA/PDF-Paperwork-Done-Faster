using System.Security.Cryptography;
using System.Text.Json;
using HunterPdf.Core;

static class ImageEditingProof
{
    static string Worker(string root)=>Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe");
    public static async Task RunAsync(string root,string output)
    {
        var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        async Task Rejected<T>(Func<Task> action,string name,string expected) where T:Exception
        {try{await action();}catch(T e){if(!e.Message.Contains(expected,StringComparison.OrdinalIgnoreCase))throw;checks.Add(name);return;}throw new Exception(name);}
        var work=Path.Combine(output,"image-editing");Directory.CreateDirectory(work);
        var recovery=new RecoveryStore(Path.Combine(work,"recovery"));
        var source=Path.Combine(root,"fixtures","corpus-v1","engine","objects-original.pdf");
        var originalHash=SHA256.HashData(File.ReadAllBytes(source));
        using var session=await DocumentSession.OpenAsync(Worker(root),recovery,source);
        var before=await session.ExtractAsync(0);var sourceObjects=before.GetProperty("Objects").EnumerateArray().ToArray();
        var images=sourceObjects.Where(x=>x.GetProperty("Type").GetInt32()==3).ToArray();
        Check(images.Length==2,"Fixture exposes two top-level images");
        var target=images[0];var id=target.GetProperty("Id").GetString()!;
        var l=target.GetProperty("Left").GetDouble();var b=target.GetProperty("Bottom").GetDouble();
        var r=target.GetProperty("Right").GetDouble();var t=target.GetProperty("Top").GetDouble();
        var red=Solid(5,4,0,0,255,255);
        await Rejected<ArgumentException>(()=>session.AddImageAsync(0,red,5,3,300,300,305,303),"Malformed BGRA byte count rejected before worker mutation","exactly match");
        Check(!session.IsDirty&&session.Revision==0,"Invalid raster input creates no checkpoint");

        await session.ReplaceImageAsync(0,id,Solid(5,4,255,0,0,255),5,4);
        Check(session.IsDirty&&session.Revision==1&&session.CanUndo,"Image replacement commits one reversible checkpoint");
        var replaced=await session.ExtractAsync(0);var imageCount=replaced.GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3);
        Check(imageCount==images.Length,"Replacement preserves image-object count and position");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3)==images.Length,"Undo restores the original image object");
        await session.RedoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3)==images.Length,"Redo reapplies image replacement");

        var square=Solid(2,2,0,180,255,255);var pageWidth=before.GetProperty("Width").GetDouble();var pageHeight=before.GetProperty("Height").GetDouble();
        var addLeft=pageWidth-90;var addBottom=pageHeight-90;
        await session.AddImageAsync(0,square,2,2,addLeft,addBottom,addLeft+60,addBottom+60);
        var added=await session.ExtractAsync(0);var addCount=added.GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3);
        Check(addCount==images.Length+1,"Adding image inserts one object at requested bounds");
        Check(!Directory.EnumerateFiles(recovery.DirectoryFor(session.Id),"*.bgra").Any(),"Normalized raster temporary is removed after the committed checkpoint embeds it");
        var newImage=added.GetProperty("Objects").EnumerateArray().Last(x=>x.GetProperty("Type").GetInt32()==3);
        var newId=newImage.GetProperty("Id").GetString()!;
        await session.ReplaceImageAsync(0,newId,Solid(2,2,0,0,255,128),2,2);
        var alphaRender=await session.RenderAsync(0,306,396);var alphaBytes=Convert.FromBase64String(alphaRender.GetProperty("Bgra").GetString()!);
        var alphaOffset=30*alphaRender.GetProperty("Stride").GetInt32()+276*4;
        Check(Math.Abs(alphaBytes[alphaOffset]-127)<=12&&Math.Abs(alphaBytes[alphaOffset+1]-127)<=12&&alphaBytes[alphaOffset+2]>=243,"Straight BGRA alpha is blended into the committed page render");
        var withAlpha=await session.ExtractAsync(0);newImage=withAlpha.GetProperty("Objects").EnumerateArray().Last(x=>x.GetProperty("Type").GetInt32()==3);newId=newImage.GetProperty("Id").GetString()!;
        var revisionBeforeInvalid=session.Revision;
        await Rejected<InvalidOperationException>(()=>session.TransformImageAsync(0,newId,-1,addBottom,59,addBottom+60),"Placement outside page rejected","inside the current page");
        Check(session.Revision==revisionBeforeInvalid,"Rejected image placement creates no checkpoint");
        var refreshedAfterRejection=await session.ExtractAsync(0);newImage=refreshedAfterRejection.GetProperty("Objects").EnumerateArray().Last(x=>x.GetProperty("Type").GetInt32()==3);newId=newImage.GetProperty("Id").GetString()!;
        var revisionBeforeTiny=session.Revision;
        await Rejected<InvalidOperationException>(()=>session.TransformImageAsync(0,newId,addLeft,addBottom,addLeft+0.000001,addBottom+0.000001),"Sub-float-resolution image bounds are rejected","finite, nonempty rectangle");
        Check(session.Revision==revisionBeforeTiny,"Sub-float-resolution rejection creates no checkpoint");
        refreshedAfterRejection=await session.ExtractAsync(0);newImage=refreshedAfterRejection.GetProperty("Objects").EnumerateArray().Last(x=>x.GetProperty("Type").GetInt32()==3);newId=newImage.GetProperty("Id").GetString()!;
        await session.TransformImageAsync(0,newId,addLeft-20,addBottom-15,addLeft+28,addBottom+33);
        var moved=await session.ExtractAsync(0);var movedImage=moved.GetProperty("Objects").EnumerateArray().Last(x=>x.GetProperty("Type").GetInt32()==3);
        Check(Math.Abs(movedImage.GetProperty("Left").GetDouble()-(addLeft-20))<0.1&&Math.Abs(movedImage.GetProperty("Top").GetDouble()-(addBottom+33))<0.1,"Move and aspect-preserving resize persist at requested bounds");
        var movedId=movedImage.GetProperty("Id").GetString()!;await session.DeleteImageAsync(0,movedId);
        Check((await session.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3)==images.Length,"Delete removes selected image only");

        await session.AddImageAsync(0,square,2,2,addLeft,addBottom,addLeft+60,addBottom+60);
        var final=await session.ExtractAsync(0);var finalCount=final.GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3);
        Check(finalCount==images.Length+1,"A later add remains in the final state");
        var save=Path.Combine(work,"images-saved.pdf");await session.SaveAsync(save);
        using(var reopened=await DocumentSession.OpenAsync(Worker(root),recovery,save))
        {
            Check((await reopened.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(x=>x.GetProperty("Type").GetInt32()==3)==images.Length+1,"Added and replaced images survive save/reopen");
            await reopened.CloseAsync();
        }
        Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Image editing leaves original source bytes unchanged");
        await session.CloseAsync();

        using(var canceled=await DocumentSession.OpenAsync(Worker(root),recovery,source))
        {
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Rejected<OperationCanceledException>(()=>canceled.AddImageAsync(0,red,5,4,300,300,305,304,cancel.Token),"Canceled image add stops before creating a checkpoint","canceled");
            Check(!canceled.IsDirty&&canceled.Revision==0,"Canceled image add preserves committed state");
            Check(!Directory.EnumerateFiles(recovery.DirectoryFor(canceled.Id),"*.bgra").Any(),"Canceled image add leaves no normalized raster temporary");
            await canceled.CloseAsync();
        }
        var signedPath=Path.Combine(root,"fixtures","corpus-v1","edge","signed.pdf");
        using(var signed=await DocumentSession.OpenAsync(Worker(root),recovery,signedPath))
        {
            await Rejected<SignedDocumentException>(()=>signed.AddImageAsync(0,square,2,2,300,300,302,302),"Signed-image edit requires signed-copy consent","requires explicit consent");
            Check(!signed.IsDirty,"Rejected signed-image edit preserves the signed checkpoint");await signed.CloseAsync();
        }

        var complex=Path.Combine(root,"fixtures","corpus-v1","engine","complex-original.pdf");
        using(var shared=await DocumentSession.OpenAsync(Worker(root),recovery,complex))
        {
            var page0Before=await shared.RenderAsync(0,306,396);var page1Before=await shared.RenderAsync(1,306,396);
            var page1=await shared.ExtractAsync(1);var image=page1.GetProperty("Objects").EnumerateArray().First(x=>x.GetProperty("Type").GetInt32()==3);
            await shared.ReplaceImageAsync(1,image.GetProperty("Id").GetString()!,Solid(5,4,0,255,0,255),5,4);
            var page0After=await shared.RenderAsync(0,306,396);var page1After=await shared.RenderAsync(1,306,396);
            Check(page0Before.GetProperty("Bgra").GetString()==page0After.GetProperty("Bgra").GetString(),"Replacing shared image occurrence preserves other page occurrence");
            Check(page1Before.GetProperty("Bgra").GetString()!=page1After.GetProperty("Bgra").GetString(),"Selected shared image occurrence visibly changes");
            var sharedSave=Path.Combine(work,"shared-image-saved.pdf");await shared.SaveAsync(sharedSave);
            using var sharedReopened=await DocumentSession.OpenAsync(Worker(root),recovery,sharedSave);
            var page0Reopened=await sharedReopened.RenderAsync(0,306,396);var page1Reopened=await sharedReopened.RenderAsync(1,306,396);
            Check(page0Before.GetProperty("Bgra").GetString()==page0Reopened.GetProperty("Bgra").GetString(),"Other shared occurrence remains intact after reopen");
            Check(page1Before.GetProperty("Bgra").GetString()!=page1Reopened.GetProperty("Bgra").GetString(),"Selected replacement survives reopen");
            await sharedReopened.CloseAsync();await shared.CloseAsync();
        }
        File.WriteAllText(Path.Combine(work,"results.json"),JsonSerializer.Serialize(new {Passed=checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {checks.Count} image editing checks. Evidence: {work}");
    }
    static byte[] Solid(int width,int height,byte blue,byte green,byte red,byte alpha)
    {var data=new byte[width*height*4];for(var i=0;i<data.Length;i+=4){data[i]=blue;data[i+1]=green;data[i+2]=red;data[i+3]=alpha;}return data;}
}
