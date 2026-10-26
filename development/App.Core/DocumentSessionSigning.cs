using System.Text.Json;

namespace HunterPdf.Core;

public sealed record SignedCopyResult(
    string Destination,
    string Sha256,
    int SignatureCount,
    bool IntegrityValid,
    bool CryptographicallyValid,
    bool CertificateTrustEstablished,
    bool RevocationEstablished,
    bool TrustedTimestamp,
    bool TemporaryCandidateRetained);

internal enum SigningStage { BeforePublish }

public sealed partial class DocumentSession
{
    internal Action<SigningStage, string, string>? SigningProbe { get; set; }

    /// <summary>Creates a new signed copy without changing this session's checkpoint or save state.</summary>
    public Task<SignedCopyResult> CreateSignedCopyAsync(AdvancedEngineLocation location, string destination,
        string pfxPath, string certificatePassword, long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.PythonExecutable) || string.IsNullOrWhiteSpace(location.WorkerScript))
            throw new ArgumentException("The advanced PDF engine location is incomplete.", nameof(location));
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("A destination is required.", nameof(destination));
        if (string.IsNullOrWhiteSpace(pfxPath)) throw new ArgumentException("A certificate path is required.", nameof(pfxPath));
        ArgumentNullException.ThrowIfNull(certificatePassword);
        if (certificatePassword.Length > 4096)
            throw new ArgumentException("The certificate password exceeds the supported size.", nameof(certificatePassword));

        return RunAsync(async () =>
        {
            if (record.Revision != expectedRevision)
                throw new InvalidOperationException("The document changed. Refresh before creating a signed copy.");
            if (password is not null)
                throw new InvalidOperationException("Password-protected PDFs are not supported for signed-copy export.");
            cancellation.ThrowIfCancellationRequested();

            destination = Path.GetFullPath(destination);
            pfxPath = Path.GetFullPath(pfxPath);
            var directory = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The signed-copy folder does not exist.");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Choose a new destination; existing files are never overwritten.");
            if (SamePath(destination, OriginalPath) || SamePath(destination, CurrentSnapshot) || SamePath(destination, LastSavePath))
                throw new IOException("A signed copy must use a new path separate from the session sources.");
            if (destination.StartsWith(store.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a destination outside recovery storage.");
            if (!File.Exists(pfxPath)) throw new FileNotFoundException("The certificate file could not be found.");
            RecoveryStore.RejectReparse(pfxPath);
            RecoveryStore.RejectReparse(directory);
            RecoveryStore.RejectReparse(CurrentSnapshot);

            await EnsureWorkerAsync(cancellation);
            var currentHash = record.History[record.Index].Sha256;
            if (RecoveryStore.Fingerprint(CurrentSnapshot) != currentHash)
                throw new IOException("The committed checkpoint failed its integrity check.");
            var originalPathHash = File.Exists(OriginalPath) ? RecoveryStore.Fingerprint(OriginalPath) : null;
            bool OriginalPathUnchanged() => originalPathHash is null
                ? !File.Exists(OriginalPath)
                : File.Exists(OriginalPath) && RecoveryStore.Fingerprint(OriginalPath) == originalPathHash;
            var stagingDirectory = Path.Combine(directory, "." + Path.GetFileName(destination) + ".signing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagingPublished = false;
            try
            {
                RecoveryStore.RejectReparse(stagingDirectory);
                var input = Path.Combine(stagingDirectory, "input.pdf");
                var candidate = Path.Combine(stagingDirectory, "candidate.pdf");
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                if (RecoveryStore.Fingerprint(input) != currentHash)
                    throw new IOException("The signing input copy did not match the committed checkpoint.");

                var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
                var signed = await engine.RunAsync(new
                {
                    Operation = "SignPdf",
                    Input = AdvancedEngineClient.FilePath(input),
                    Output = AdvancedEngineClient.FilePath(candidate),
                    CertificatePath = AdvancedEngineClient.FilePath(pfxPath),
                    CertificatePassword = certificatePassword
                }, cancellation);

                static bool IsTrue(JsonElement value, string property) =>
                    value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;
                if (!IsTrue(signed, "Applied") || !IsTrue(signed, "Intact") ||
                    !IsTrue(signed, "CryptographicallyValid"))
                    throw new IOException("The signing engine did not validate the new signature.");
                if (!signed.TryGetProperty("CertificateTrustEstablished", out var trust) || trust.ValueKind != JsonValueKind.False ||
                    !signed.TryGetProperty("RevocationEstablished", out var revocation) || revocation.ValueKind != JsonValueKind.False ||
                    !signed.TryGetProperty("TrustedTimestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.False)
                    throw new IOException("The signing engine returned invalid trust-status metadata.");
                if (!signed.TryGetProperty("TemporaryCandidateRetained", out var retained) ||
                    retained.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new IOException("The signing engine omitted temporary-file status.");
                _ = retained.GetBoolean(); // The whole private staging directory is removed after publication.
                if (!signed.TryGetProperty("OutputSHA256", out var outputHashElement) ||
                    outputHashElement.ValueKind != JsonValueKind.String)
                    throw new IOException("The signing engine omitted the candidate hash.");
                var outputHash = outputHashElement.GetString();
                if (outputHash is null || outputHash.Length != 64 || !outputHash.All(Uri.IsHexDigit) ||
                    !File.Exists(candidate) || !string.Equals(RecoveryStore.Fingerprint(candidate), outputHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The signed candidate did not match its validated hash.");

                int nativeSignatureCount;
                using (var leaseCandidate = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (leaseCandidate.Length == 0) throw new IOException("The signing engine produced an empty candidate.");
                    var validated = await RequestAsync("ValidateCandidate", new { Path = candidate }, cancellation);
                    if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount)
                        throw new IOException("The signed candidate changed the page count.");
                    nativeSignatureCount = validated.Data.GetProperty("SignatureCount").GetInt32();
                    if (nativeSignatureCount < 1 ||
                        validated.Data.GetProperty("SecurityHandlerRevision").GetInt32() != -1)
                        throw new IOException("The signed candidate failed native signature or encryption checks.");
                    cancellation.ThrowIfCancellationRequested();
                    if (!string.Equals(RecoveryStore.Fingerprint(candidate), outputHash, StringComparison.OrdinalIgnoreCase) ||
                        RecoveryStore.Fingerprint(input) != currentHash ||
                        RecoveryStore.Fingerprint(CurrentSnapshot) != currentHash)
                        throw new IOException("A signing source or validated candidate changed before publication.");
                    if (!OriginalPathUnchanged())
                        throw new DocumentConflictException("The source PDF changed while the signed copy was being prepared.");
                }

                SigningProbe?.Invoke(SigningStage.BeforePublish, input, candidate);
                cancellation.ThrowIfCancellationRequested();
                if (!OriginalPathUnchanged())
                    throw new DocumentConflictException("The source PDF changed while the signed copy was being prepared.");
                if (!string.Equals(RecoveryStore.Fingerprint(candidate), outputHash, StringComparison.OrdinalIgnoreCase) ||
                    RecoveryStore.Fingerprint(input) != currentHash ||
                    RecoveryStore.Fingerprint(CurrentSnapshot) != currentHash)
                    throw new IOException("A signing source or validated candidate changed before publication.");
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException("The signed-copy destination appeared during processing; no file was replaced.");
                // File.Move without overwrite is the publication boundary. Candidate reads are closed first.
                File.Move(candidate, destination);
                stagingPublished = true;
                var temporaryCandidateRetained = false;
                try
                {
                    if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception) when (stagingPublished)
                {
                    // Publication succeeded. Report success and surface only that private cleanup remains.
                    temporaryCandidateRetained = true;
                }
                return new SignedCopyResult(destination, outputHash, nativeSignatureCount, true, true,
                    false, false, false, temporaryCandidateRetained);
            }
            finally
            {
                if (!stagingPublished)
                {
                    if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }, cancellation, isolated: true);
    }
}
