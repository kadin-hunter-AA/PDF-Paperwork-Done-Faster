using System.Text.Json;

namespace HunterPdf.Core;

public sealed partial class DocumentSession
{
    /// <summary>Fills one supported existing AcroForm text field or checkbox on a selected page.</summary>
    public Task FillFormAsync(AdvancedEngineLocation location, int page, string fieldName, string value,
        long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.PythonExecutable) || string.IsNullOrWhiteSpace(location.WorkerScript))
            throw new ArgumentException("The advanced PDF engine location is incomplete.", nameof(location));
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Length > 512)
            throw new ArgumentException("A fully qualified form field name is required.", nameof(fieldName));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 4096)
            throw new ArgumentException("The form field value exceeds the supported size.", nameof(value));

        return ApplyExternalCandidateAsync(ExternalEditKind.FillForm, expectedRevision, async (input, candidate, token) =>
        {
            if (page < 0 || page >= PageCount)
                throw new InvalidOperationException("The selected page is outside the current document.");
            var inspected = (await RequestAsync("InspectFormFields", new { Page = page }, token)).Data;
            if (inspected.GetProperty("Page").GetInt32() != page ||
                !inspected.TryGetProperty("Fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                throw new IOException("The selected page's form fields could not be verified.");
            var matches = fields.EnumerateArray()
                .Where(field => field.TryGetProperty("Name", out var name) &&
                                name.ValueKind == JsonValueKind.String && name.GetString() == fieldName)
                .ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("ReadOnly").GetBoolean())
                throw new InvalidOperationException("The selected field is not a supported editable field on this page.");
            var fieldType = matches[0].GetProperty("Type").GetString();
            var fieldValue = value;
            if (fieldType == "Text")
            {
                if (matches[0].GetProperty("Password").GetBoolean())
                    throw new InvalidOperationException("Password text fields are unsupported.");
            }
            else if (fieldType == "Checkbox")
            {
                if (value is not ("true" or "false"))
                    throw new ArgumentException("Checkbox values must use the lowercase string true or false.", nameof(value));
            }
            else
                throw new InvalidOperationException("Only plain text fields and single checkboxes are supported.");

            var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
            var result = await engine.RunAsync(new
            {
                Operation = "FillForm",
                Input = AdvancedEngineClient.FilePath(input),
                Output = AdvancedEngineClient.FilePath(candidate),
                FieldName = fieldName,
                Value = fieldValue
            }, token);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True ||
                !result.TryGetProperty("Page", out var appliedPage) || appliedPage.GetInt32() != page)
                throw new IOException("The advanced PDF engine did not confirm the form-field update.");
        }, cancellation);
    }
}
