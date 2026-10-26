using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

partial class Pdf
{
    // Occurrences refer to local content operators, not PDFium's flattened object indices.
    // A formPath item selects the zero-based Do (image or form) invocation at that level.
    // TextIndex selects a local Tj/TJ/'/" operation. Selections are invalid after any edit.
    public sealed record NestedTextSelection(int PageIndex, int[] FormPath, int TextIndex,
        string ExpectedText, string SourceSha256);

    public static void ReplaceNestedText(string source, string destination,
        NestedTextSelection selection, string replacement)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        Check(!File.Exists(destination) && !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase),
            "nested edit requires a new destination");
        var parent = Path.GetDirectoryName(destination)!;
        var work = Path.GetFullPath(Path.Combine(parent, ".nested-" + Guid.NewGuid().ToString("N")));
        Check(string.Equals(Path.GetDirectoryName(work), parent, StringComparison.OrdinalIgnoreCase),
            "private nested work directory stays within destination directory");
        Directory.CreateDirectory(work);
        try
        {
            var candidate = Path.Combine(work, "candidate.pdf");
            var bridge = Path.Combine(AppContext.BaseDirectory, "nested_text.py");
            // Source-tree dependency directory is passed explicitly, so published paths cannot
            // accidentally resolve a different pypdf installation.
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PDF_EDITOR_PYTHON") ?? "python")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(bridge);
            start.Environment["PYTHONPATH"] = Path.Combine(AppContext.BaseDirectory, "python-packages");
            using var process = Process.Start(start) ?? throw new IOException("Could not start nested editor");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(JsonSerializer.Serialize(new
            {
                source, destination = candidate, pageIndex = selection.PageIndex,
                formPath = selection.FormPath, textIndex = selection.TextIndex,
                expectedText = selection.ExpectedText, sourceSha256 = selection.SourceSha256, replacement
            }));
            process.StandardInput.Close();
            if (!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Nested edit exceeded 60 seconds");
            }
            var diagnostic = stderr.GetAwaiter().GetResult();
            var report = stdout.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Nested edit rejected: " + diagnostic.Trim());
            using var result = JsonDocument.Parse(report);
            Check(result.RootElement.GetProperty("nestedTextPersistence").GetBoolean(), "nested bridge persistence");
            // A separate engine must also be able to open and extract the saved result.
            using (var doc = Open(candidate))
            using (var page = doc.Page(selection.PageIndex))
                Check(replacement.Length == 0 || page.Text().Contains(replacement), "PDFium nested replacement extraction");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))
                .Equals(selection.SourceSha256, StringComparison.OrdinalIgnoreCase), "nested source unchanged");
            File.Move(candidate, destination); // Exclusive publish; never replace an existing file.
        }
        finally
        {
            Directory.Delete(work, recursive: true); // Only the private directory created above.
        }
    }
}
