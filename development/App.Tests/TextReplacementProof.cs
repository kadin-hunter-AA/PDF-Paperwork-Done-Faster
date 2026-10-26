using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using HunterPdf.Core;

static class TextReplacementProof
{
    static string Worker(string root) => Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");

    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        async Task Rejected<T>(Func<Task> action, string name, string? messagePart = null) where T : Exception
        {
            try { await action(); }
            catch (T e) { if (messagePart is not null && !e.Message.Contains(messagePart, StringComparison.Ordinal)) throw; checks.Add(name); return; }
            throw new Exception(name);
        }

        var work = Path.Combine(output, "text-replacement"); Directory.CreateDirectory(work);
        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        using var session = await DocumentSession.OpenAsync(Worker(root), recovery, source);
        var page = await session.ExtractAsync(0);
        var objects = page.GetProperty("Objects").EnumerateArray().ToArray();
        var form = objects.FirstOrDefault(x => x.GetProperty("Type").GetInt32() == 5);
        Check(form.ValueKind == JsonValueKind.Undefined || !form.GetProperty("CanEdit").GetBoolean(), "Nested form objects are not offered as editable top-level text");
        var text = objects.First(x => x.GetProperty("Type").GetInt32() == 1);
        var textId = text.GetProperty("Id").GetString()!;
        var original = text.GetProperty("Text").GetString()!;
        Check(text.GetProperty("CanEdit").GetBoolean() && text.GetProperty("FontName").GetString() == "Helvetica" && original.Contains("Source page one", StringComparison.Ordinal),
            "Text extraction exposes a supported existing object and its font details");

        await Rejected<InvalidOperationException>(() => session.ReplaceTextAsync(0, textId, "Edited Ω"), "Unsupported glyphs are rejected", "basic ASCII");
        Check(!session.IsDirty, "Rejected glyphs do not create a committed edit");
        var currentPage = await session.ExtractAsync(0);
        var currentId = currentPage.GetProperty("Objects").EnumerateArray().First(x => x.GetProperty("Type").GetInt32() == 1).GetProperty("Id").GetString()!;
        var replacement = "Edited longer source page one";
        await session.ReplaceTextAsync(0, currentId, replacement);
        Check(session.IsDirty && session.CanUndo, "Replacing existing text commits a reversible document checkpoint");
        var changed = await session.ExtractAsync(0);
        var changedText = changed.GetProperty("Text").GetString()!;
        Check(changedText.Contains(replacement, StringComparison.Ordinal) && !changedText.Contains(original, StringComparison.Ordinal),
            "Replacement changes the actual text object and removes its original text");
        var otherPageText = (await session.ExtractAsync(1)).GetProperty("Text").GetString()!;
        Check(otherPageText.Contains("Source page two", StringComparison.Ordinal), "Unrelated page content remains intact");
        await Rejected<InvalidOperationException>(() => session.ReplaceTextAsync(0, currentId, "Stale replacement"), "Object IDs expire after a revision change", "stale");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains(replacement, StringComparison.Ordinal), "Stale object rejection leaves the committed replacement intact");
        var refreshed = await session.ExtractAsync(0);
        var refreshedId = refreshed.GetProperty("Objects").EnumerateArray().First(x => x.GetProperty("Type").GetInt32() == 1).GetProperty("Id").GetString()!;
        await Rejected<InvalidOperationException>(() => session.ReplaceTextAsync(0, refreshedId, new string('W', 1000)), "Text that exceeds the original page layout is rejected", "extends beyond the page");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains(replacement, StringComparison.Ordinal), "Rejected overflow restores the current text without changing session content");

        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains(original, StringComparison.Ordinal), "Undo restores the original text object");
        await session.RedoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains(replacement, StringComparison.Ordinal), "Redo reapplies the actual text replacement");
        var saved = Path.Combine(work, "replacement-saved.pdf"); await session.SaveAsync(saved);
        using (var reopened = await DocumentSession.OpenAsync(Worker(root), recovery, saved))
        {
            var reopenedText = (await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(reopenedText.Contains(replacement, StringComparison.Ordinal) && !reopenedText.Contains(original, StringComparison.Ordinal), "Saved replacement reopens with old text absent");
            Check((await reopened.ExtractAsync(1)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal), "Saved replacement retains unrelated content");
            await reopened.CloseAsync();
        }
        Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Existing text replacement leaves the original PDF bytes unchanged");
        await session.CloseAsync();
        var clippedPath = Path.Combine(work, "clipped-text.pdf");
        WriteTextFixture(clippedPath,"q 0 0 100 100 re W n BT /F1 18 Tf 10 50 Td (Clipped text) Tj ET Q\n");
        using (var clipped = await DocumentSession.OpenAsync(Worker(root), recovery, clippedPath)) {
            var extracted = await clipped.ExtractAsync(0);
            var clippedObject = extracted.GetProperty("Objects").EnumerateArray().First(o=>o.GetProperty("Type").GetInt32()==1);
            Check(!clippedObject.GetProperty("CanEdit").GetBoolean() && clippedObject.GetProperty("EditReason").GetString()!.Contains("clipping path"), "Actual attached clipping is rejected while ordinary unclipped text remains editable");
            await Rejected<InvalidOperationException>(()=>clipped.ReplaceTextAsync(0,clippedObject.GetProperty("Id").GetString()!,"Changed"),"Clipped text replacement fails with an actionable reason","clipping path");
            Check(!clipped.IsDirty && (await clipped.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Clipped text"),"Rejected clipped text preserves the committed content");
            await clipped.CloseAsync();
        }
        var markedPath=Path.Combine(work,"marked-text.pdf");
        WriteTextFixture(markedPath,"/Span << /ActualText (Semantic text) >> BDC BT /F1 18 Tf 10 50 Td (Visible text) Tj ET EMC\n");
        using(var marked=await DocumentSession.OpenAsync(Worker(root),recovery,markedPath)) {
            var markObject=(await marked.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().First(o=>o.GetProperty("Type").GetInt32()==1);
            Check(!markObject.GetProperty("CanEdit").GetBoolean() && markObject.GetProperty("EditReason").GetString()!.Contains("marked-content"),"ActualText/marked-content objects are not offered for unsupported semantic edits");
            await Rejected<InvalidOperationException>(()=>marked.ReplaceTextAsync(0,markObject.GetProperty("Id").GetString()!,"Changed"),"Marked-content edits are refused without changing semantic text","marked-content");
            Check(!marked.IsDirty,"Rejected marked-content edits never create a committed checkpoint");
            await marked.CloseAsync();
        }
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} existing-text replacement checks. Evidence: {work}");
    }
    static void WriteTextFixture(string path,string content)
    {
        var bodies = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream" };
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for(var i=0;i<bodies.Length;i++) { offsets.Add(pdf.Length); pdf.Append($"{i+1} 0 obj\n{bodies[i]}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {bodies.Length+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets) pdf.Append(offset.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)+" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {bodies.Length+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path,Encoding.ASCII.GetBytes(pdf.ToString()));
    }
}
