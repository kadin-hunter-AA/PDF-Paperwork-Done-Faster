using System.Text.Json;

namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    public Task<JsonElement> GetAnnotationManagementStatusAsync(CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("InspectAnnotationManagement", new { }, cancellation)).Data;
        }, cancellation, isolated: true);
    /// <summary>Returns read-only AcroForm field metadata for one page. Field writing is not supported.</summary>
    public Task<JsonElement> GetFormFieldsAsync(int page, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            if (page < 0 || page >= PageCount) throw new InvalidOperationException("The selected page is outside the current document.");
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("InspectFormFields", new { Page = page }, cancellation)).Data;
        }, cancellation);
}
