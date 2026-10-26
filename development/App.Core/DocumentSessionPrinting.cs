using System.Text.Json;

namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    public Task<JsonElement> RenderForPrintAsync(int page, int width, int height, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("RenderForPrint", new { Page = page, Width = width, Height = height }, cancellation)).Data;
        }, cancellation);
}
