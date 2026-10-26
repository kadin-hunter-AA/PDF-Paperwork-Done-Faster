using System.Text.Json;

namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    /// <summary>Adds local English text layers to a supported scan-only document of up to 50 pages.</summary>
    public Task RecognizeScanAsync(AdvancedEngineLocation location, string trainedDataPath,
        string pdfiumLibrary, long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainedDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfiumLibrary);
        return ApplyExternalCandidateAsync(ExternalEditKind.Ocr, expectedRevision, async (input, candidate, token) =>
        {
            var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
            var result = await engine.RunAsync(new
            {
                Operation = "OcrScan",
                Input = AdvancedEngineClient.FilePath(input),
                Output = AdvancedEngineClient.FilePath(candidate),
                TrainedDataPath = AdvancedEngineClient.FilePath(trainedDataPath),
                PdfiumPath = AdvancedEngineClient.FilePath(pdfiumLibrary)
            }, token);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True ||
                !result.TryGetProperty("OutputSHA256", out var hash) || hash.ValueKind != JsonValueKind.String ||
                !string.Equals(hash.GetString(), RecoveryStore.Fingerprint(candidate), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The OCR engine did not validate its candidate.");
        }, cancellation);
    }
}
