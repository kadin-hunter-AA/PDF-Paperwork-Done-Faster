using System.Text.Json;

namespace HunterPdf.Core;

/// <summary>Generic routes for companion-engine (App.Advanced) feature modules registered in worker.py.</summary>
public sealed partial class DocumentSession
{
    static Dictionary<string, object?> CompanionRequest(string operation, string input, string? output,
        IReadOnlyDictionary<string, object?>? arguments)
    {
        var request = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not null)
            foreach (var (key, value) in arguments) request[key] = value;
        request["Operation"] = operation;
        request["Input"] = AdvancedEngineClient.FilePath(input);
        request["Output"] = output is null ? null : AdvancedEngineClient.FilePath(output);
        return request;
    }

    static void RequireLocation(AdvancedEngineLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.PythonExecutable) || string.IsNullOrWhiteSpace(location.WorkerScript))
            throw new ArgumentException("The advanced PDF engine location is incomplete.", nameof(location));
    }

    /// <summary>
    /// Runs a read-only companion query against a disposable copy of the committed checkpoint.
    /// The session, its history and the source PDF are never changed.
    /// </summary>
    internal Task<JsonElement> QueryCompanionAsync(AdvancedEngineLocation location, string operation,
        IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        RequireLocation(location);
        return RunAsync(async () =>
        {
            var expectedHash = record.History[record.Index].Sha256;
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var request = CompanionRequest(operation, input, null, arguments);
                if (password is not null) request["Password"] = password;
                var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, timeout)
                    .RunAsync(request, cancellation);
                if (RecoveryStore.Fingerprint(CurrentSnapshot) != expectedHash)
                    throw new IOException("The committed checkpoint changed during the query.");
                return result;
            }
            finally { TryRemoveTemporary(input); }
        }, cancellation, isolated: true);
    }

    /// <summary>
    /// Applies a companion transform of the committed checkpoint as one undoable, recoverable edit.
    /// The transform writes a new candidate that PDFium validates before it becomes history.
    /// </summary>
    internal Task ApplyCompanionEditAsync(AdvancedEngineLocation location, ExternalEditKind kind, string operation,
        IReadOnlyDictionary<string, object?>? arguments, long expectedRevision, CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        RequireLocation(location);
        return ApplyExternalCandidateAsync(kind, expectedRevision, async (input, candidate, token) =>
        {
            var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, timeout)
                .RunAsync(CompanionRequest(operation, input, candidate, arguments), token);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True)
                throw new IOException("The advanced PDF engine did not confirm the change.");
            if (!File.Exists(candidate)) throw new IOException("The advanced PDF engine did not write its candidate.");
        }, cancellation);
    }

    /// <summary>
    /// Writes a companion transform of the committed checkpoint to a new file. Existing files are never
    /// replaced; the open document, its history and the source PDF are unchanged. <paramref name="outputPassword"/>
    /// is the password needed to validate an encrypted result.
    /// </summary>
    internal Task<JsonElement> ExportCompanionAsync(AdvancedEngineLocation location, string operation,
        IReadOnlyDictionary<string, object?>? arguments, string destination, string? outputPassword = null,
        bool pageCountMayChange = false, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        RequireLocation(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return RunAsync(async () =>
        {
            destination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(directory) || File.Exists(destination) || Directory.Exists(destination) ||
                SamePath(destination, OriginalPath) || SamePath(destination, LastSavePath) ||
                destination.StartsWith(store.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a new PDF path outside recovery storage; existing files are never replaced.");
            RecoveryStore.RejectReparse(directory);
            await EnsureWorkerAsync(cancellation);
            var expectedHash = record.History[record.Index].Sha256;
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var candidate = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".candidate.pdf");
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var request = CompanionRequest(operation, input, candidate, arguments);
                if (password is not null) request["Password"] = password;
                var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, timeout)
                    .RunAsync(request, cancellation);
                if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True || !File.Exists(candidate))
                    throw new IOException("The advanced PDF engine did not write its result.");
                if (RecoveryStore.Fingerprint(input) != expectedHash || RecoveryStore.Fingerprint(CurrentSnapshot) != expectedHash)
                    throw new IOException("The transform changed its input. No file was written.");
                RecoveryStore.RejectReparse(candidate);
                var validated = await RequestAsync("ValidateCandidate", new { Path = candidate, Password = outputPassword }, cancellation);
                if (!pageCountMayChange && validated.Data.GetProperty("PageCount").GetInt32() != PageCount)
                    throw new IOException("The result failed native validation.");
                cancellation.ThrowIfCancellationRequested();
                File.Move(candidate, destination); // Exclusive creation: never replaces a late competing file.
                return result;
            }
            finally
            {
                TryRemoveTemporary(input);
                TryRemoveTemporary(candidate);
            }
        }, cancellation, isolated: true);
    }
}
