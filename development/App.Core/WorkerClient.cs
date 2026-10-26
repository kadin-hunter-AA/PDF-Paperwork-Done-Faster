using System.Diagnostics;
using System.Text.Json;
namespace HunterPdf.Core;
public sealed class WorkerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class WorkerClient(string executable, string arguments = "") : IDisposable {
    readonly SemaphoreSlim gate = new(1,1);
    Process? process;
    public int? ProcessId => process?.Id;
    public void Start() {
        if (process is { HasExited: false }) return;
        process?.Dispose();
        process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true }) ?? throw new WorkerUnavailableException("Worker could not start.");
        process.ErrorDataReceived += (_, _) => {};
        process.BeginErrorReadLine();
    }
    public async Task<Response> SendAsync(Request request, CancellationToken cancellation = default, TimeSpan? requestTimeout = null) {
        if (requestTimeout is { } limit && (limit <= TimeSpan.Zero || limit > WorkerTimeouts.Max)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        await gate.WaitAsync(cancellation);
        try {
            cancellation.ThrowIfCancellationRequested();
            if (process is null || process.HasExited) throw new WorkerUnavailableException("Worker stopped. Reopen the document to continue.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(requestTimeout ?? WorkerTimeouts.Default);
            try {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException("Worker pipe closed.");
                var response = JsonSerializer.Deserialize<Response>(line) ?? throw new IOException("Invalid response.");
                if (response.RequestId != request.RequestId || response.DocumentId != request.DocumentId) throw new IOException("Mismatched response.");
                var expected = request.Operation is "AddText" or "AddMarkup" or "AddDrawing" or "AddInk" or "AddTextBox" or "AddComment" or "DeleteAnnotation" or "UpdateComment" or "MoveDrawing" or "RestyleDrawing" or "ReplaceText" or "RotatePage" or "RotatePages" or "AddImage" or "ReplaceImage" or "TransformImage" or "DeleteImage" or "MovePages" or "InsertBlankPage" or "DeletePages" or "DuplicatePages" or "InsertPages" ? request.Revision+1 : request.Revision;
                if (response.Success && response.Revision != expected) throw new IOException("Stale response.");
                return response;
            } catch (OperationCanceledException) { Stop(); throw; }
              catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { Stop(); throw new WorkerUnavailableException("Worker failed. Reopen the document to continue.", e); }
        } finally { gate.Release(); }
    }
    public void Stop() { if (process is { HasExited:false }) { process.Kill(true); process.WaitForExit(); } process?.Dispose(); process=null; }
    public void Dispose() => Stop();
}

// Request deadlines grow with the document so large files are not killed mid-operation; a stalled worker is still stopped.
internal static class WorkerTimeouts {
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Max = TimeSpan.FromMinutes(10);
    const double BytesPerUnit = 10d * 1024 * 1024;
    // 30 s + 15 s per 10 MB of the current snapshot + 1 s per 20 pages, capped at 10 minutes.
    internal static TimeSpan Scaled(long snapshotBytes, int pageCount) {
        var milliseconds = Default.TotalMilliseconds + Math.Max(0, snapshotBytes) / BytesPerUnit * 15_000d + Math.Max(0, pageCount) / 20d * 1_000d;
        return TimeSpan.FromMilliseconds(Math.Min(Max.TotalMilliseconds, Math.Round(milliseconds)));
    }
    // Render, text extraction and page info stay at the flat default; every other operation scales.
    internal static TimeSpan ForOperation(string operation, long snapshotBytes, int pageCount) =>
        operation is "Render" or "Extract" or "PageInfo" ? Default : Scaled(snapshotBytes, pageCount);
}
