using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HunterPdf.Core;

static class Utf8PathProof
{
    static string Worker(string root) => Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");

    public static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "utf8-paths");
        Directory.CreateDirectory(work);
        var fixtureFirst = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        var fixtureSecond = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-merge-source.pdf");
        var fixtureHashes = new[] { Hash(fixtureFirst), Hash(fixtureSecond) };
        var first = Path.Combine(work, "Café ü 日本.pdf");
        var second = Path.Combine(work, "Résumé 東京.pdf");
        File.Copy(fixtureFirst, first);
        File.Copy(fixtureSecond, second);
        var copiedHashes = new[] { Hash(first), Hash(second) };
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks.Add(name);
        }

        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        var workerPath = Worker(root);
        var editedPath = Path.Combine(work, "edited Café ü 日本.pdf");
        using (var session = await DocumentSession.OpenAsync(workerPath, recovery, first))
        {
            Check(session.PageCount == 2, "PDF opens from a path containing accented and Japanese characters");
            await session.AddTextAsync(0, "UTF8 PATH CHECK", 72, 120);
            await session.SaveAsync(editedPath);
            await session.CloseAsync();
        }
        using (var reopened = await DocumentSession.OpenAsync(workerPath, recovery, editedPath))
        {
            var text = (await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(text.Contains("UTF8 PATH CHECK", StringComparison.Ordinal), "Edited PDF saves and reopens through a UTF-8 path with its edit intact");
            await reopened.CloseAsync();
        }

        var encryptedPath = Path.Combine(work, "保護された.pdf");
        const string password = "pässwörd日本";
        await CreateEncryptedCopy(fixtureFirst, encryptedPath, password, root);
        var encryptedHash = Hash(encryptedPath);
        var encryptedEdited = Path.Combine(work, "保護された編集.pdf");
        using (var encrypted = await DocumentSession.OpenAsync(workerPath, recovery, encryptedPath, password))
        {
            Check(encrypted.PageCount == 2, "PDF opens with a non-ASCII password");
            var text = (await encrypted.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(text.Contains("Source page one", StringComparison.Ordinal), "Non-ASCII password unlocks the expected page text");
            await encrypted.AddTextAsync(0, "UTF8 PASSWORD CHECK", 72, 120);
            await encrypted.SaveAsync(encryptedEdited);
            await encrypted.CloseAsync();
        }
        using (var reopenedEncrypted = await DocumentSession.OpenAsync(workerPath, recovery, encryptedEdited, password))
        {
            var text = (await reopenedEncrypted.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(text.Contains("UTF8 PASSWORD CHECK", StringComparison.Ordinal), "Non-ASCII-password PDF saves and reopens with its edit intact");
            await reopenedEncrypted.CloseAsync();
        }

        using var worker = new WorkerClient(workerPath);
        worker.Start();
        var combinedPath = Path.Combine(work, "合併 café.pdf");
        var combined = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "Combine", new
        {
            Inputs = new[] { new { Path = first, Range = "" }, new { Path = second, Range = "" } },
            Path = combinedPath
        }));
        Check(combined.Success, "Combine accepts UTF-8 input and output paths" + (combined.Error is null ? "" : ": " + combined.Error));
        using (var reopenedCombined = await DocumentSession.OpenAsync(workerPath, recovery, combinedPath))
        {
            Check(reopenedCombined.PageCount == 3, "UTF-8 combined output reopens with the expected page count");
            var firstPage = (await reopenedCombined.ExtractAsync(0)).GetProperty("Text").GetString()!;
            var lastPage = (await reopenedCombined.ExtractAsync(2)).GetProperty("Text").GetString()!;
            Check(firstPage.Contains("Source page one", StringComparison.Ordinal) && lastPage.Contains("Merged source page", StringComparison.Ordinal),
                "UTF-8 combined output preserves source page order and text");
            await reopenedCombined.CloseAsync();
        }

        Check(Hash(first).SequenceEqual(copiedHashes[0]) && Hash(second).SequenceEqual(copiedHashes[1]),
            "Combine leaves both copied inputs byte-for-byte unchanged");
        Check(Hash(encryptedPath).SequenceEqual(encryptedHash), "Editing an encrypted copy leaves its source bytes unchanged");
        Check(Hash(fixtureFirst).SequenceEqual(fixtureHashes[0]) && Hash(fixtureSecond).SequenceEqual(fixtureHashes[1]),
            "Frozen fixture PDFs remain byte-for-byte unchanged");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} UTF-8 path checks. Evidence: {work}");
    }

    static async Task CreateEncryptedCopy(string source, string destination, string password, string root)
    {
        var python = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        python.ArgumentList.Add("-c");
        python.ArgumentList.Add("import sys; sys.path.insert(0, sys.argv[1]); import pypdf; from pypdf import PdfReader, PdfWriter; assert pypdf.__version__ == '6.1.3', pypdf.__version__; r=PdfReader(sys.argv[2]); w=PdfWriter(); w.append_pages_from_reader(r); w.encrypt(sys.argv[4], algorithm='AES-256'); f=open(sys.argv[3], 'xb'); w.write(f); f.close()");
        python.ArgumentList.Add(Path.Combine(root, "tools", "python-packages"));
        python.ArgumentList.Add(source);
        python.ArgumentList.Add(destination);
        python.ArgumentList.Add(password);
        using var process = Process.Start(python) ?? throw new Exception("Could not start Python for the pinned pypdf encrypted-copy proof.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"Pinned pypdf 6.1.3 could not create the encrypted test copy (exit {process.ExitCode}): {stderr}{stdout}");
    }

    static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
}
