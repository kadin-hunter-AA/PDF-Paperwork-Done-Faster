using System.Globalization;
using System.Security.Cryptography;

namespace HunterPdf.Core;

/// <summary>One combine input. <see cref="Range"/> uses 1-based pages such as "1,3-5"; null or blank means every page.</summary>
public sealed record CombineInput(string Path, string? Range = null);

public sealed record CombineResult(string Path, int PageCount);

/// <summary>Where the bundled companion engine lives next to the application, or null when it is not installed.</summary>
public static class PageToolsEngine
{
    public static AdvancedEngineLocation? Installed()
    {
        var location = new AdvancedEngineLocation(System.IO.Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "advanced", "worker.py"));
        return File.Exists(location.PythonExecutable) && File.Exists(location.WorkerScript) ? location : null;
    }
}

/// <summary>
/// Combine that keeps bookmarks. The companion engine merges with pypdf and nests each file's outline under a top-level bookmark
/// named after the file; the result is written to a hidden candidate beside the destination, checked with PDFium (opens, expected
/// page count), then moved into place without replacing anything. Inputs are never modified. Needs no document session.
/// </summary>
public static class CombineWithBookmarks
{
    const int MaximumPages = 50_000;

    public static async Task<CombineResult> RunAsync(AdvancedEngineLocation location, string workerExecutable,
        IReadOnlyList<CombineInput> inputs, string destination, bool groupByFile = true, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (inputs.Count is < 2 or > 200) throw new InvalidOperationException("Select between 2 and 200 PDF files to combine.");
        destination = System.IO.Path.GetFullPath(destination);
        var directory = System.IO.Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("The output folder does not exist or is unavailable.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException("The output path already exists. Choose a new output path; existing files are never overwritten.");
        RecoveryStore.RejectReparse(directory);
        var plans = new List<(string Path, string? Range, int Pages, string Hash)>();
        long expected = 0;
        using var worker = new WorkerClient(workerExecutable);
        worker.Start();
        foreach (var input in inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Path) || !File.Exists(input.Path)) throw new InvalidOperationException("Choose accessible PDF files.");
            var path = System.IO.Path.GetFullPath(input.Path);
            if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The output path must not be one of the input files.");
            var inspected = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "InspectCombineInput", new { Path = path }), cancellation);
            if (!inspected.Success) throw new InvalidOperationException(inspected.Error);
            var pageCount = inspected.Data.GetProperty("PageCount").GetInt32();
            var selected = CountRange(input.Range, pageCount);
            expected += selected;
            if (expected > MaximumPages) throw new InvalidOperationException($"Combine supports up to {MaximumPages:N0} selected pages per output.");
            plans.Add((path, string.IsNullOrWhiteSpace(input.Range) ? null : input.Range.Trim(), selected, Hash(path)));
        }
        var candidate = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".candidate.pdf");
        try
        {
            var request = new Dictionary<string, object?>
            {
                ["Operation"] = "CombineKeepBookmarks", ["Input"] = AdvancedEngineClient.FilePath(plans[0].Path), ["Output"] = AdvancedEngineClient.FilePath(candidate),
                ["Inputs"] = plans.Select(p => new { Path = AdvancedEngineClient.FilePath(p.Path), Range = p.Range }).ToArray(), ["GroupByFile"] = groupByFile,
            };
            var timeout = AdvancedEngineClient.ScaledTimeout(plans.Sum(p => new FileInfo(p.Path).Length), (int)expected);
            var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, timeout).RunAsync(request, cancellation);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != System.Text.Json.JsonValueKind.True || !File.Exists(candidate) ||
                result.GetProperty("PageCount").GetInt32() != expected)
                throw new IOException("The advanced PDF engine did not write its result.");
            RecoveryStore.RejectReparse(candidate);
            var validated = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "InspectCombineInput", new { Path = candidate }), cancellation);
            if (!validated.Success || validated.Data.GetProperty("PageCount").GetInt32() != expected)
                throw new IOException("The combined PDF failed native validation. No output was published.");
            foreach (var plan in plans)
                if (Hash(plan.Path) != plan.Hash) throw new IOException("An input PDF changed during combine. No output was published.");
            cancellation.ThrowIfCancellationRequested();
            File.Move(candidate, destination); // Exclusive: never replaces a late competing file.
            return new CombineResult(destination, (int)expected);
        }
        finally { try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    // Same rules as the Combine window: ascending 1-based ranges, no repeated page, blank = all pages.
    static int CountRange(string? range, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(range)) return pageCount;
        var seen = new HashSet<int>();
        foreach (var raw in range.Split(','))
        {
            var ends = raw.Trim().Split('-');
            if (ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1)
                throw new InvalidOperationException("Use page numbers such as 1,3-5; page numbers start at 1.");
            var last = first;
            if (ends.Length == 2 && (!int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
                throw new InvalidOperationException("Use ascending page ranges such as 2-5.");
            if (last > pageCount) throw new InvalidOperationException($"Page {last} is outside this PDF, which has {pageCount} pages.");
            for (var page = first; page <= last; page++)
                if (!seen.Add(page)) throw new InvalidOperationException($"Page {page} appears more than once in this range.");
        }
        return seen.Count;
    }
}
