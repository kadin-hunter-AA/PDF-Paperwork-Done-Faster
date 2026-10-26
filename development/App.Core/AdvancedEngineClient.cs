using System.Diagnostics;
using System.Text.Json;

namespace HunterPdf.Core;

// One isolated companion process per job. Document data and credentials travel
// through stdin, never shell syntax, command-line arguments or application logs.
internal sealed class AdvancedEngineClient(string pythonExecutable, string scriptPath, TimeSpan? timeout = null)
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    // Same size/page scaling the worker uses; callers pass the result as the constructor timeout for long jobs such as OCR.
    internal static TimeSpan ScaledTimeout(long snapshotBytes, int pageCount) => WorkerTimeouts.Scaled(snapshotBytes, pageCount);
    internal static string FilePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
    internal int? LastProcessId { get; private set; }
    internal async Task<JsonElement> RunAsync(object request, CancellationToken cancellation = default)
    {
        var payload = JsonSerializer.Serialize(request);
        if (payload.Length > 262_144) throw new InvalidOperationException("The operation request is too large.");
        var python = Path.GetFullPath(pythonExecutable);
        var script = Path.GetFullPath(scriptPath);
        if (!File.Exists(python) || !File.Exists(script))
            throw new InvalidOperationException("The companion PDF engine is not installed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        using var process = TakeWarm(python, script) ?? StartProcess(python, script);
        LastProcessId = process.Id;
        if (KeepsWarm(python, script)) Prewarm(python, script);
        Task<string>? output = null, diagnostics = null;
        try
        {
            output = ReadBoundedAsync(process.StandardOutput, process, token);
            diagnostics = ReadBoundedAsync(process.StandardError, process, token);
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            process.StandardInput.Close();
            await Task.WhenAll(output, diagnostics, process.WaitForExitAsync(token));
            if (process.ExitCode != 0) throw new IOException("The companion PDF engine stopped before completing the operation.");
            using var document = JsonDocument.Parse(await output, new JsonDocumentOptions { MaxDepth = 16 });
            var response = document.RootElement;
            if (!response.TryGetProperty("Success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new IOException("The companion PDF engine returned an invalid response.");
            if (!success.GetBoolean())
            {
                var error = response.TryGetProperty("Error", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString() : "The PDF operation was refused.";
                if (error is null || error.Length > 512) error = "The PDF operation was refused.";
                throw new InvalidOperationException(error);
            }
            if (!response.TryGetProperty("Data", out var data))
                throw new IOException("The companion PDF engine omitted its result.");
            return data.Clone();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The PDF operation timed out. The last committed document remains available.");
        }
        catch (JsonException exception)
        {
            throw new IOException("The companion PDF engine returned invalid JSON.", exception);
        }
        finally
        {
            Stop(process);
            if (output is not null && diagnostics is not null)
            {
                try { await Task.WhenAll(output, diagnostics); }
                catch (Exception) { /* Observe failed reads after process termination. */ }
            }
        }
    }

    // Loading the bundled PDF libraries takes several seconds per launch, longer under antivirus
    // scanning. A pre-started process does that loading ahead of time and then waits for its one
    // request on stdin, so each job is still isolated in its own short-lived process.
    static readonly object warmGate = new();
    static (string Python, string Script, Process Process)? warm;
    static (string Python, string Script)? keepWarm;
    static bool exitHooked;

    /// <summary>Starts a companion process in the background and keeps one ready for later requests.</summary>
    internal static void Prewarm(string pythonExecutable, string scriptPath)
    {
        var python = Path.GetFullPath(pythonExecutable);
        var script = Path.GetFullPath(scriptPath);
        if (!File.Exists(python) || !File.Exists(script)) return;
        lock (warmGate)
        {
            keepWarm = (python, script);
            if (!exitHooked) { AppDomain.CurrentDomain.ProcessExit += (_, _) => DiscardWarm(); exitHooked = true; }
            if (warm is { } current)
            {
                if (current.Python == python && current.Script == script && !current.Process.HasExited) return;
                Stop(current.Process); current.Process.Dispose(); warm = null;
            }
        }
        _ = Task.Run(() =>
        {
            Process started;
            try { started = StartProcess(python, script); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return; }
            lock (warmGate)
            {
                if (warm is null && keepWarm == (python, script)) { warm = (python, script, started); return; }
            }
            Stop(started); started.Dispose();
        });
    }

    internal static void DiscardWarm()
    {
        lock (warmGate)
        {
            keepWarm = null;
            if (warm is { } current) { Stop(current.Process); current.Process.Dispose(); warm = null; }
        }
    }

    static bool KeepsWarm(string python, string script) { lock (warmGate) return keepWarm == (python, script); }

    static Process? TakeWarm(string python, string script)
    {
        lock (warmGate)
        {
            if (warm is not { } current || current.Python != python || current.Script != script) return null;
            warm = null;
            if (!current.Process.HasExited) return current.Process;
            current.Process.Dispose();
            return null;
        }
    }

    static Process StartProcess(string python, string script)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(FilePath(script));
        var process = new Process { StartInfo = start };
        if (!process.Start()) { process.Dispose(); throw new IOException("The companion PDF engine could not start."); }
        return process;
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken cancellation)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
            {
                if (result.Length + count > 65_536)
                    throw new IOException("The companion PDF engine exceeded its response limit.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
