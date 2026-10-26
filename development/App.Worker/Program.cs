using System.Text.Json;
using HunterPdf.Core;
using HunterPdf.Pdfium;
using var engine=new Engine();
while(Console.ReadLine() is { } line) {
    Request? request=null;
    try {
        request=JsonSerializer.Deserialize<Request>(line) ?? throw new InvalidOperationException();
        var a=request.Arguments; var id=request.DocumentId; var revision=request.Revision;
        string S(string key)=>a.GetProperty(key).GetString() ?? "";
        string? Password()=>a.TryGetProperty("Password",out var p)?p.GetString():null;
        int Page()=>a.GetProperty("Page").GetInt32();
        if(request.Operation=="Open" && revision!=0) throw new InvalidOperationException();
        object data=request.Operation switch {
            "InspectCombineInput" => engine.InspectCombineInput(S("Path")),
            "Combine" => engine.Combine(a.GetProperty("Inputs"),S("Path")),
            "Open" => engine.Open(id,S("Path"),Password(),a.TryGetProperty("AllowSignedCopy",out var consent) && consent.GetBoolean()),
            "Extract" => engine.Extract(id,revision,Page(),a.TryGetProperty("IncludeCharacters",out var characters) && characters.GetBoolean()),
            "PageInfo" => engine.PageInfo(id,revision),
            "Bookmarks" => engine.Bookmarks(id,revision),
            "Render" => engine.Render(id,revision,Page(),a.GetProperty("Width").GetInt32(),a.GetProperty("Height").GetInt32()),
            "RenderForPrint" => engine.RenderForPrint(id,revision,Page(),a.GetProperty("Width").GetInt32(),a.GetProperty("Height").GetInt32()),
            "RenderPrintPage" => engine.RenderPrintPage(id,revision,Page(),S("Mode"),a.TryGetProperty("Width",out var pw)?pw.GetInt32():0,a.TryGetProperty("Height",out var ph)?ph.GetInt32():0,a.TryGetProperty("Dpi",out var pd)?pd.GetInt32():0,S("OutputPath")),
            "InspectFormFields" => engine.InspectFormFields(id,revision,Page()),
            "InspectAnnotationManagement" => engine.InspectAnnotationManagement(id,revision),
            "SaveCandidate" => engine.Save(id,revision,S("Path")),
            "ValidateCandidate" => Validate(),
            "AddText" => Edit(),
            "AddMarkup" => Markup(),
            "AddDrawing" => Drawing(),
            "AddInk" => Ink(),
            "AddTextBox" => TextBox(),
            "AddComment" => Comment(),
            "DeleteAnnotation" => DeleteAnnotation(),
            "UpdateComment" => UpdateComment(),
            "MoveDrawing" => MoveDrawing(),
            "RestyleDrawing" => RestyleDrawing(),
            "ReplaceText" => ReplaceText(),
            "AddImage" => AddImage(),
            "ReplaceImage" => ReplaceImage(),
            "TransformImage" => TransformImage(),
            "DeleteImage" => DeleteImage(),
            "MovePages" => MovePages(),
            "InsertBlankPage" => InsertBlankPage(),
            "DeletePages" => engine.DeletePages(id,revision,Pages()),
            "DuplicatePages" => engine.DuplicatePages(id,revision,Pages(),a.GetProperty("Destination").GetInt32()),
            "InsertPages" => engine.InsertPages(id,revision,S("SourcePath"),Pages(),a.GetProperty("Destination").GetInt32()),
            "ExtractPages" => engine.ExtractPages(id,revision,Pages(),S("OutputPath")),
            "SplitPages" => engine.SplitPages(id,revision,S("OutputDirectory")),
            "RotatePage" => Rotate(),
            "RotatePages" => RotatePages(),
            "Close" => Close(),
            _ => throw new InvalidOperationException()
        };
        object Validate()=>engine.ValidateCandidate(id,revision,S("Path"),Password());
        object Edit(){engine.AddText(id,revision,Page(),S("Text"),a.GetProperty("X").GetDouble(),a.GetProperty("Y").GetDouble());return new { Applied=true };}
        object Markup(){engine.AddMarkup(id,revision,Page(),S("Kind"),a.GetProperty("Quads"));return new { Applied=true };}
        object Drawing(){engine.AddDrawing(id,revision,Page(),S("Kind"),a.GetProperty("Left").GetDouble(),a.GetProperty("Bottom").GetDouble(),a.GetProperty("Right").GetDouble(),a.GetProperty("Top").GetDouble(),Stroke());return new { Applied=true };}
        Engine.StrokeStyle? Stroke()=>a.TryGetProperty("Stroke",out var s) && s.ValueKind==JsonValueKind.Object
            ? new Engine.StrokeStyle(s.GetProperty("Red").GetInt32(),s.GetProperty("Green").GetInt32(),s.GetProperty("Blue").GetInt32(),s.GetProperty("Width").GetDouble()) : null;
        object Ink(){engine.AddInk(id,revision,Page(),S("Kind"),a.GetProperty("Points").EnumerateArray().Select(p=>(p[0].GetDouble(),p[1].GetDouble())).ToList(),Stroke());return new { Applied=true };}
        object TextBox(){engine.AddTextBox(id,revision,Page(),S("Text"),a.GetProperty("Left").GetDouble(),a.GetProperty("Bottom").GetDouble(),a.GetProperty("Right").GetDouble(),a.GetProperty("Top").GetDouble());return new { Applied=true };}
        object Comment(){engine.AddComment(id,revision,Page(),S("Contents"),a.GetProperty("Left").GetDouble(),a.GetProperty("Bottom").GetDouble(),a.GetProperty("Right").GetDouble(),a.GetProperty("Top").GetDouble());return new { Applied=true };}
        object DeleteAnnotation(){engine.DeleteAnnotation(id,revision,Page(),S("AnnotationId"));return new { Applied=true };}
        object MoveDrawing(){engine.MoveDrawing(id,revision,Page(),S("AnnotationId"),a.GetProperty("Dx").GetDouble(),a.GetProperty("Dy").GetDouble());return new { Applied=true };}
        object RestyleDrawing(){engine.RestyleDrawing(id,revision,Page(),S("AnnotationId"),a.GetProperty("Red").GetInt32(),a.GetProperty("Green").GetInt32(),a.GetProperty("Blue").GetInt32(),a.GetProperty("Width").GetDouble());return new { Applied=true };}
        object UpdateComment(){engine.UpdateComment(id,revision,Page(),S("AnnotationId"),S("Contents"));return new { Applied=true };}
        object ReplaceText(){engine.ReplaceText(id,revision,Page(),S("ObjectId"),S("Text"));return new { Applied=true };}
        object AddImage(){engine.AddImage(id,revision,Page(),S("Path"),a.GetProperty("Width").GetInt32(),a.GetProperty("Height").GetInt32(),a.GetProperty("Left").GetDouble(),a.GetProperty("Bottom").GetDouble(),a.GetProperty("Right").GetDouble(),a.GetProperty("Top").GetDouble());return new { Applied=true };}
        object ReplaceImage(){engine.ReplaceImage(id,revision,Page(),S("ObjectId"),S("Path"),a.GetProperty("Width").GetInt32(),a.GetProperty("Height").GetInt32());return new { Applied=true };}
        object TransformImage(){engine.TransformImage(id,revision,Page(),S("ObjectId"),a.GetProperty("Left").GetDouble(),a.GetProperty("Bottom").GetDouble(),a.GetProperty("Right").GetDouble(),a.GetProperty("Top").GetDouble());return new { Applied=true };}
        object DeleteImage(){engine.DeleteImage(id,revision,Page(),S("ObjectId"));return new { Applied=true };}
        object MovePages()=>engine.MovePages(id,revision,a.GetProperty("Pages").EnumerateArray().Select(p=>p.GetInt32()).ToArray(),a.GetProperty("Destination").GetInt32());
        int[] Pages()=>a.GetProperty("Pages").EnumerateArray().Select(p=>p.GetInt32()).ToArray();
        object InsertBlankPage()=>engine.InsertBlankPage(id,revision,Page(),a.GetProperty("Width").GetDouble(),a.GetProperty("Height").GetDouble());
        object Rotate(){engine.RotatePage(id,revision,Page(),a.GetProperty("Rotation").GetInt32());return new { Applied=true };}
        object RotatePages(){engine.RotatePages(id,revision,Pages(),a.GetProperty("QuarterTurns").GetInt32());return new { Applied=true };}
        object Close(){engine.Close(id,revision);return new { Closed=true };}
        var stateless=request.Operation is "InspectCombineInput" or "Combine";
        var current=stateless || request.Operation=="Close" ? revision : engine.Revision(id);
        Console.WriteLine(JsonSerializer.Serialize(new Response(request.RequestId,id,current,true,Wire.Data(data),null)));
    } catch(Exception e) {
        if(request is null) break;
        var passwordRequired = request.Operation == "Open" && e is PdfPasswordRequiredException;
        var error=request.Operation == "Open" || request.Operation is "InspectCombineInput" or "Combine" or "InspectFormFields" or "ReplaceText" or "AddMarkup" or "AddDrawing" or "AddInk" or "AddTextBox" or "AddComment" or "DeleteAnnotation" or "UpdateComment" or "MoveDrawing" or "RestyleDrawing" or "AddImage" or "ReplaceImage" or "TransformImage" or "DeleteImage" or "MovePages" or "InsertBlankPage" or "DeletePages" or "DuplicatePages" or "InsertPages" or "ExtractPages" or "SplitPages" or "RotatePages" or "Render" or "RenderForPrint" or "RenderPrintPage" ? e.Message : "Operation failed. Check the file, page, parameters, and document revision.";
        var errorCode = passwordRequired ? WireErrorCodes.PasswordRequired : null;
        Console.WriteLine(JsonSerializer.Serialize(new Response(request.RequestId,request.DocumentId,request.Revision,false,Wire.Data(new {}),error,errorCode)));
    }
}
