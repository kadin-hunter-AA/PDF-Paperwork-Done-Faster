using System.Text.Json;

// Run this process separately from the future UI: it alone owns native handles.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts");
Directory.CreateDirectory(output);
Pdf.Initialize();
try
{
    if(args.Contains("--corpus")) { CorpusProof.Run(output); return; }
    if(args.Contains("--compatibility")) { CompatibilityProof.Run(output); return; }
    if(args.Contains("--edge")) { EdgeProof.Run(output); return; }
    if(args.Contains("--product")) { ProductProof.Run(output); return; }
    if(args.Contains("--nested")) { NestedProof.Run(output); return; }
    var original = Path.Combine(output, "original.pdf");
    var edited = Path.Combine(output, "edited.pdf");
    using (var doc = Pdf.Create())
    {
        using var page = doc.NewPage();
        page.AddText("Original text", 72, 700);
        page.AddText("Unrelated content must survive", 72, 650);
        page.Generate();
        doc.Save(original);
    }
    using (var doc = Pdf.Open(original))
    {
        using var page = doc.Page(0);
        Pdf.Check(Pdf.Native.FPDFPage_CountObjects(page.Handle) == 2, "fixture object count");
        var text = Pdf.Native.FPDFPage_GetObject(page.Handle, 0);
        Pdf.Check(Pdf.Native.FPDFPageObj_GetType(text) == 1, "existing text object");
        Pdf.Check(Pdf.Native.FPDFText_SetText(text, "A longer replacement of the existing text"), "replace text");
        page.Generate();
        doc.Save(edited);
    }
    string extracted;
    using (var doc = Pdf.Open(edited))
    {
        Pdf.Check(doc.PageCount == 1, "reopened page count");
        using var page = doc.Page(0);
        extracted = page.Text();
        Pdf.Check(extracted.Contains("A longer replacement of the existing text"), "replacement persisted");
        Pdf.Check(extracted.Contains("Unrelated content must survive"), "unrelated text preserved");
        Pdf.Check(!extracted.Contains("Original text"), "original text removed");
        page.Render(Path.Combine(output, "edited.ppm"));
    }
    ObjectProof.Run(output);
    PageProof.Run(output);
    var nestedPersisted=ComplexProof.Run(output);
    var fonts=FontProof.Run(output);
    Pdf.Check(fonts.EmbeddedEdit && fonts.RealSubset && fonts.SubsetEdit && fonts.MissingGlyphFontReplacement, "required font capabilities");
    var hashes=Directory.GetFiles(output,"*.pdf").ToDictionary(path=>Path.GetFileName(path)!, path=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    File.WriteAllText(Path.Combine(output, "proof.json"), JsonSerializer.Serialize(new
    {
        engine = "PDFium chromium/8086", runtime = Environment.Version.ToString(),
        passed = new[] { "create/save", "open", "enumerate text and images", "replace existing text", "close/reopen", "text persistence", "render", "image move/resize/replace/delete", "highlight and ink creation/modification", "page reorder/merge" },
        complex = new { rotatedPage=true, sharedImageIsolation=true, nestedDirectTextPersistence=nestedPersisted, nestedAdapterTextPersistence=true, formHierarchyPreserved=true },
        fonts,
        fixtureSha256=hashes,
        pending = new[] { "run run-nested-checks.ps1 for the general nested-edit regression gate" },
        acceptanceGate = "REQUIRES_NESTED_REGRESSION_GATE", original, edited
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Engine checks passed. Run run-nested-checks.ps1 for the independent nested-edit acceptance gate.");
}
finally { Pdf.Native.FPDF_DestroyLibrary(); }
