using System.Diagnostics;
using System.Security.Cryptography;
using HunterPdf.Core;
// A deterministic stalled peer exercises cancellation during an active pipe read.
if(args is ["--stall"]) { Console.ReadLine(); await Task.Delay(Timeout.Infinite); return; }
if(args is ["--abandon-session", var projectRoot, var recoveryRoot, var marker]) { await SessionProof.AbandonAsync(projectRoot, recoveryRoot, marker); return; }
var root=Path.GetFullPath(args[0]);
var output=Path.Combine(root,"artifacts","worker-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
var source=Path.Combine(root,"fixtures","corpus-v1","engine","original.pdf");var hash=SHA256.HashData(File.ReadAllBytes(source));
if (args.Contains("--markup-only")) { await MarkupProof.RunAsync(root, output); return; }
if (args.Contains("--drawing-only")) { await DrawingProof.RunAsync(root, output); return; }
if (args.Contains("--ink-only")) { await InkProof.RunAsync(root, output); return; }
if (args.Contains("--drawing-management-only")) { await DrawingManagementProof.RunAsync(root, output); return; }
if (args.Contains("--rotated-pages-only")) { await RotatedPagesProof.RunAsync(root, output); return; }
if (args.Contains("--coordinate-safety-only")) { await PageCoordinateSafetyProof.RunAsync(root, output); return; }
if (args.Contains("--open-errors-only")) { await PdfOpenErrorProof.RunAsync(root, Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe")); return; }
if (args.Contains("--forms-only")) { await FormEditingProof.RunAsync(root, output); return; }
if (args.Contains("--external-only")) { await ExternalCandidateProof.RunAsync(root, output); return; }
if (args.Contains("--advanced-client-only")) { await AdvancedEngineProof.RunAsync(root, output); return; }
if (args.Contains("--fill-form-only")) { await FormFillIntegrationProof.RunAsync(root, output); return; }
if (args.Contains("--rich-text-only")) { await RichTextProof.RunAsync(root, output); return; }
if (args.Contains("--rich-text-only")) { await RichTextProof.RunAsync(root, output); return; }
if (args.Contains("--form-panel-only")) { await FormPanelProof.RunAsync(root, output); return; }
if (args.Contains("--signing-only")) { await SigningIntegrationProof.RunAsync(root, output); return; }
if (args.Contains("--ocr-only")) { await OcrIntegrationProof.RunAsync(root, output); return; }
if (args.Contains("--ocr-pages-only")) { await OcrPagesProof.RunAsync(root, output); return; }
if (args.Contains("--redaction-only")) { await RedactionIntegrationProof.RunAsync(root, output); return; }
if (args.Contains("--quick-tools-only")) { await QuickToolsProof.RunAsync(root, output); return; }
if (args.Contains("--import-tier2-only")) { await ImportTier2Proof.RunAsync(root, output); return; }
if (args.Contains("--annotations-only")) { await AnnotationManagementProof.RunAsync(root, output); return; }
if (args.Contains("--structured-text-only")) { await StructuredTextProof.RunAsync(root, output); return; }
if (args.Contains("--history-only")) { await HistoryLimitProof.RunAsync(root, output); return; }
if (args.Contains("--advanced-only")) { await AdvancedChecks(); return; }
using var client=new WorkerClient(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"));
client.Start();var id=Guid.NewGuid();var checks=new List<string>();
async Task<Response> Send(string op,object a,long rev=0){var result=await client.SendAsync(Wire.Create(id,rev,op,a));if(!result.Success)throw new Exception("Unexpected failure: "+op);return result;}
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
await Send("Open",new {Path=source});
var before=await Send("Extract",new {Page=0});
Check(before.Data.GetProperty("Objects").GetArrayLength()>0,"Objects have serializable IDs and geometry");
var repeat=await Send("Extract",new {Page=0});
Check(before.Data.GetProperty("Objects").GetRawText()==repeat.Data.GetProperty("Objects").GetRawText(),"Object IDs stable within a revision");
var concurrent=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Send("Extract",new {Page=0})));
Check(concurrent.All(r=>r.Success),"Concurrent callers serialize native requests");
var secondId=Guid.NewGuid();
var second=await client.SendAsync(Wire.Create(secondId,0,"Open",new {Path=source}));
Check(second.Success,"Separate document sessions open");
var render=await Send("Render",new {Page=0,Width=306,Height=396});
Check(Convert.FromBase64String(render.Data.GetProperty("Bgra").GetString()!).Length==306*396*4,"Worker renders bounded bitmap");
var bad=await client.SendAsync(Wire.Create(id,0,"Render",new {Page=0,Width=100000,Height=100000}));Check(!bad.Success,"Oversized render rejected");
var edit=await Send("AddText",new {Page=0,Text="WORKER CHECKPOINT",X=72,Y=120});Check(edit.Revision==1,"Edits advance revision");
var other=await client.SendAsync(Wire.Create(secondId,0,"Extract",new {Page=0}));
Check(other.Success && !other.Data.GetProperty("Text").GetString()!.Contains("WORKER CHECKPOINT"),"Edit isolated to selected document");
await client.SendAsync(Wire.Create(secondId,0,"Close",new {}));
var stale=await client.SendAsync(Wire.Create(id,0,"Extract",new {Page=0}));Check(!stale.Success,"Stale request rejected");
var candidate=Path.Combine(output,"candidate.pdf");await Send("SaveCandidate",new {Path=candidate},1);await Send("ValidateCandidate",new {Path=candidate},1);
var overwrite=await client.SendAsync(Wire.Create(id,1,"SaveCandidate",new {Path=source}));Check(!overwrite.Success,"Existing file cannot be overwritten");
await Send("Close",new {},1);await Send("Open",new {Path=candidate});
var emptyCandidate=Path.Combine(output,"zero-page-candidate.pdf");CreateZeroPagePdf(emptyCandidate);
var emptyValidation=await client.SendAsync(Wire.Create(id,0,"ValidateCandidate",new {Path=emptyCandidate}));
Check(!emptyValidation.Success,"Saved candidate with zero pages is rejected before checkpoint publication");
var after=await Send("Extract",new {Page=0});Check(after.Data.GetProperty("Text").GetString()!.Contains("WORKER CHECKPOINT"),"Edit persists after save/reopen");
using(var crashed=Process.GetProcessById(client.ProcessId!.Value)){crashed.Kill();crashed.WaitForExit();}
try{await Send("Extract",new {Page=0});throw new Exception("Crash was not surfaced");}catch(WorkerUnavailableException){checks.Add("Worker crash produces recoverable error");}
client.Start();await Send("Open",new {Path=source});await Send("Extract",new {Page=0});checks.Add("Client reopens after worker crash");
using(var canceled=new CancellationTokenSource()){canceled.Cancel();try{await client.SendAsync(Wire.Create(id,0,"Extract",new {Page=0}),canceled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){checks.Add("Queued cancellation respected");}}
await Send("Extract",new {Page=0});
Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Source fixture remains unchanged");
await Send("Close",new {});
await CombineProof.RunAsync(root,output,client,checks);
var selfArguments = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
    ? "\"" + typeof(SessionProof).Assembly.Location + "\" --stall" : "--stall";
using(var stalled=new WorkerClient(Environment.ProcessPath!,selfArguments)){
    stalled.Start();
    using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var watch=Stopwatch.StartNew();
    try{await stalled.SendAsync(Wire.Create(Guid.NewGuid(),0,"Extract",new {Page=0}),cancel.Token);throw new Exception("Active cancellation ignored");}
    catch(OperationCanceledException){Check(watch.Elapsed<TimeSpan.FromSeconds(5) && stalled.ProcessId is null,"Active cancellation terminates stalled worker");}
}
var desktop=Path.Combine(root,"App.Desktop","bin","Debug","net10.0-windows");
Check(File.Exists(Path.Combine(desktop,"worker","App.Worker.exe")) && File.Exists(Path.Combine(desktop,"worker","pdfium.dll")),"Desktop output contains isolated worker and native engine");
File.WriteAllText(Path.Combine(output,"results.json"),System.Text.Json.JsonSerializer.Serialize(new {Passed=checks},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks.Count} checks. Evidence: {output}");
await FormRenderingProof.RunAsync(root, output);
await PdfOpenErrorProof.RunAsync(root, Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"));
await FormEditingProof.RunAsync(root, output);
await ExternalCandidateProof.RunAsync(root, output);
await SessionProof.RunAsync(root, output);
await HistoryLimitProof.RunAsync(root, output);
await TextReplacementProof.RunAsync(root, output);
await PreviewTextProof.RunAsync(root, output);
await MarkupProof.RunAsync(root, output);
await DrawingProof.RunAsync(root, output);
await InkProof.RunAsync(root, output);
await DrawingManagementProof.RunAsync(root, output);
await PageCoordinateSafetyProof.RunAsync(root, output);
await RotatedPagesProof.RunAsync(root, output);
await AnnotationManagementProof.RunAsync(root, output);
await ImageEditingProof.RunAsync(root, output);
await PageOrganizationProof.RunAsync(root, output);
await Utf8PathProof.RunAsync(root, output);
if (args.Contains("--with-advanced")) await AdvancedChecks();

async Task AdvancedChecks()
{
    await StructuredTextProof.RunAsync(root, output);
    await AdvancedEngineProof.RunAsync(root, output);
    await FormFillIntegrationProof.RunAsync(root, output);
    await RichTextProof.RunAsync(root, output);
    await FormPanelProof.RunAsync(root, output);
    await OcrIntegrationProof.RunAsync(root, output);
    await OcrPagesProof.RunAsync(root, output);
    await SigningIntegrationProof.RunAsync(root, output);
    await RedactionIntegrationProof.RunAsync(root, output);
    await QuickToolsProof.RunAsync(root, output);
    await ImportTier2Proof.RunAsync(root, output);
}

void CreateZeroPagePdf(string path)
{
    var objects=new[]{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [] /Count 0 >>"};
    var pdf=new System.Text.StringBuilder("%PDF-1.7\n");var offsets=new List<int>{0};
    for(var i=0;i<objects.Length;i++){offsets.Add(System.Text.Encoding.ASCII.GetByteCount(pdf.ToString()));pdf.Append(i+1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");}
    var xref=System.Text.Encoding.ASCII.GetByteCount(pdf.ToString());pdf.Append("xref\n0 ").Append(objects.Length+1).Append("\n0000000000 65535 f \n");
    foreach(var offset in offsets.Skip(1))pdf.Append(offset.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
    pdf.Append("trailer\n<< /Size ").Append(objects.Length+1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
    File.WriteAllText(path,pdf.ToString(),System.Text.Encoding.ASCII);
}
