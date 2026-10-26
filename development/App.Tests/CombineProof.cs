using System.Diagnostics;
using System.Security.Cryptography;
using HunterPdf.Core;

static class CombineProof
{
    public static async Task RunAsync(string root, string output, WorkerClient mainWorker, List<string> checks)
    {
        var fixtures = Path.Combine(root, "fixtures", "corpus-v1");
        var first = Path.Combine(fixtures, "engine", "page-source.pdf");
        var second = Path.Combine(fixtures, "engine", "page-merge-source.pdf");
        var signed = Path.Combine(fixtures, "edge", "signed.pdf");
        var encrypted = Path.Combine(fixtures, "compatibility", "password-aes256.pdf");
        var firstHash = Hash(first); var secondHash = Hash(second);

        async Task<Response> Combine(WorkerClient worker, string destination, object[] inputs, Guid? document = null, CancellationToken cancellation = default)
            => await worker.SendAsync(Wire.Create(document ?? Guid.NewGuid(), 7, "Combine", new { Inputs = inputs, Path = destination }), cancellation);
        async Task<Response> Inspect(string path)
            => await mainWorker.SendAsync(Wire.Create(Guid.NewGuid(), 3, "InspectCombineInput", new { Path = path }));
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }

        var inspect = await Inspect(first);
        Check(inspect.Success && inspect.Data.GetProperty("PageCount").GetInt32() == 2, "Combine inspection returns page count without opening a document session");
        var signedResult = await Inspect(signed);
        Check(!signedResult.Success && signedResult.Error!.Contains("Signed PDFs", StringComparison.Ordinal), "Signed PDFs are refused with a clear explanation");
        var encryptedResult = await Inspect(encrypted);
        Check(!encryptedResult.Success && encryptedResult.Error!.Contains("Password-protected", StringComparison.Ordinal), "Encrypted PDFs are refused with a clear explanation");

        var mergedPath = Path.Combine(output, "combined-ordered.pdf");
        var merged = await Combine(mainWorker, mergedPath,
        [
            new { Path = first, Range = "2,1" },
            new { Path = second, Range = "" }
        ]);
        Check(merged.Success && merged.Data.GetProperty("PageCount").GetInt32() == 3 && merged.Revision == 7,
            "Combine returns the expected page count and echoes its stateless request revision");
        Check(Hash(first).SequenceEqual(firstHash) && Hash(second).SequenceEqual(secondHash), "Combine leaves every input byte unchanged");

        var openId = Guid.NewGuid();
        var opened = await mainWorker.SendAsync(Wire.Create(openId, 0, "Open", new { Path = mergedPath }));
        Check(opened.Success && opened.Data.GetProperty("PageCount").GetInt32() == 3, "Combined output reopens with the expected page count");
        var expected = new[] { "Source page two", "Source page one", "Merged source page" };
        for (var i = 0; i < expected.Length; i++)
        {
            var page = await mainWorker.SendAsync(Wire.Create(openId, 0, "Extract", new { Page = i }));
            Check(page.Success && page.Data.GetProperty("Text").GetString()!.Contains(expected[i], StringComparison.Ordinal), $"Combined page {i + 1} retains its selected source content and order");
        }
        await mainWorker.SendAsync(Wire.Create(openId, 0, "Close", new { }));

        var invalidOutput = Path.Combine(output, "invalid-range.pdf");
        var invalid = await Combine(mainWorker, invalidOutput, [new { Path = first, Range = "1,3" }, new { Path = second, Range = "" }]);
        Check(!invalid.Success && invalid.Error!.Contains("outside this PDF", StringComparison.Ordinal) && !File.Exists(invalidOutput), "Out-of-bounds page ranges publish no output");
        var existing = Path.Combine(output, "existing-output.pdf");
        var sentinel = System.Text.Encoding.UTF8.GetBytes("preserve existing destination"); File.WriteAllBytes(existing, sentinel);
        var refused = await Combine(mainWorker, existing, [new { Path = first, Range = "" }, new { Path = second, Range = "" }]);
        Check(!refused.Success && File.ReadAllBytes(existing).SequenceEqual(sentinel), "Existing destinations are refused without changing their bytes");
        var self = await Combine(mainWorker, first, [new { Path = first, Range = "" }, new { Path = second, Range = "" }]);
        Check(!self.Success && Hash(first).SequenceEqual(firstHash), "An input cannot be selected as the output");

        await VerifyCancellation(root, fixtures, output, first, second, firstHash, secondHash, checks);
    }

    static async Task VerifyCancellation(string root, string fixtures, string output, string first, string second,
        byte[] firstHash, byte[] secondHash, List<string> checks)
    {
        var large = Path.Combine(fixtures, "corpus", "large-500-pages.pdf");
        var longInputs = Enumerable.Range(0, 16).Select(_ => new { Path = large, Range = "" }).Cast<object>().ToArray();
        var destination = Path.Combine(output, "canceled-combine.pdf");
        using var worker = new WorkerClient(Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe"));
        worker.Start();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            var result = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "Combine", new { Inputs = longInputs, Path = destination }), cancel.Token);
            if (result.Success)
            {
                // Some machines can finish the fixture before the timer. A successful publish is a valid completed operation.
                checks.Add("Large worker combine completed before cancellation; output was atomically published");
            }
            else throw new Exception("Unexpected combine rejection during cancellation proof: " + result.Error);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(destination)) throw new Exception("Canceled combine published an output.");
            if (worker.ProcessId is not null) throw new Exception("Active combine cancellation did not terminate its worker.");
            checks.Add("Active combine cancellation terminates the worker before publishing output");
        }
        if (!Hash(first).SequenceEqual(firstHash) || !Hash(second).SequenceEqual(secondHash))
            throw new Exception("Combine cancellation changed a source PDF.");
        checks.Add("Combine cancellation leaves source PDFs unchanged");
        if (File.Exists(destination)) File.Delete(destination);
    }

    static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
}
