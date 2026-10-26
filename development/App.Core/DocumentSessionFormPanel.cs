using System.Text.Json;

namespace HunterPdf.Core;

public sealed record FormWidget(int Page, double Left, double Bottom, double Right, double Top);
public sealed record FormOption(string Export, string Display);
public sealed record FormFieldInfo(string FullName, string Type, string Value, IReadOnlyList<string> Selected,
    IReadOnlyList<FormOption> Options, bool Multiline, bool Password, bool ReadOnly, bool Required, int MaxLength,
    bool Editable, bool MultiSelect, bool Calculated, bool HasJavaScript, bool ValueTruncated, bool OptionsTruncated,
    IReadOnlyList<FormWidget> Widgets);
public sealed record FormFieldList(IReadOnlyList<FormFieldInfo> Fields, int FieldCount, bool Truncated, bool HasXfa,
    bool XfaOnly, bool NeedAppearances);
public sealed record FormFillResult(int Applied, int Changed, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string[]> Values);

public sealed partial class DocumentSession
{
    const int MaxPanelFillFields = 500, MaxPanelFillValueLength = 4096, MaxPanelFieldNameLength = 512, MaxPanelValuesPerField = 64;

    /// <summary>Lists every terminal AcroForm field (all pages) from the committed checkpoint. Read-only.</summary>
    public async Task<FormFieldList> ListAllFormFieldsAsync(AdvancedEngineLocation location, CancellationToken cancellation = default)
    {
        var data = await QueryCompanionAsync(location, "ListFields", null, cancellation);
        return ParseFormFieldList(data);
    }

    /// <summary>
    /// Fills several existing AcroForm fields (text, checkbox, radio, combo, list) as one undoable edit.
    /// Each value array holds exactly one value, except multi-select list boxes. Nothing is applied if any value is refused.
    /// </summary>
    public async Task<FormFillResult> FillFormFieldsAsync(AdvancedEngineLocation location,
        IReadOnlyDictionary<string, string[]> values, long expectedRevision, CancellationToken cancellation = default)
    {
        RequireLocation(location);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("There are no form values to apply.", nameof(values));
        if (values.Count > MaxPanelFillFields) throw new ArgumentException("Too many form fields were supplied at once.", nameof(values));
        var wire = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, entries) in values)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxPanelFieldNameLength)
                throw new ArgumentException("A fully qualified form field name is required.", nameof(values));
            if (entries is null || entries.Length > MaxPanelValuesPerField || entries.Any(e => e is null || e.Length > MaxPanelFillValueLength))
                throw new ArgumentException("A form field value is missing or exceeds the supported size.", nameof(values));
            wire[name] = entries.Length == 1 ? entries[0] : entries;
        }

        JsonElement result = default;
        await ApplyExternalCandidateAsync(ExternalEditKind.FormFields, expectedRevision, async (input, candidate, token) =>
        {
            result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript)
                .RunAsync(CompanionRequest("FillFields", input, candidate, new Dictionary<string, object?> { ["Values"] = wire }), token);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True || !File.Exists(candidate))
                throw new IOException("The advanced PDF engine did not confirm the form update.");
        }, cancellation);

        var readBack = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var property in result.GetProperty("Values").EnumerateObject())
            readBack[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
                : [property.Value.GetString() ?? ""];
        return new FormFillResult(result.GetProperty("Count").GetInt32(), result.GetProperty("Changed").GetInt32(),
            result.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString() ?? "").ToArray(), readBack);
    }

    static FormFieldList ParseFormFieldList(JsonElement data)
    {
        static bool Flag(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        var fields = data.GetProperty("Fields").EnumerateArray().Select(f => new FormFieldInfo(
            f.GetProperty("FullName").GetString() ?? "", f.GetProperty("Type").GetString() ?? "",
            f.GetProperty("Value").GetString() ?? "",
            f.GetProperty("Selected").EnumerateArray().Select(v => v.GetString() ?? "").ToArray(),
            f.GetProperty("Options").EnumerateArray()
                .Select(o => new FormOption(o.GetProperty("Export").GetString() ?? "", o.GetProperty("Display").GetString() ?? "")).ToArray(),
            Flag(f, "Multiline"), Flag(f, "Password"), Flag(f, "ReadOnly"), Flag(f, "Required"),
            f.GetProperty("MaxLength").GetInt32(), Flag(f, "Editable"), Flag(f, "MultiSelect"), Flag(f, "Calculated"),
            Flag(f, "HasJavaScript"), Flag(f, "ValueTruncated"), Flag(f, "OptionsTruncated"),
            f.GetProperty("Widgets").EnumerateArray().Select(w => new FormWidget(w.GetProperty("Page").GetInt32(),
                w.GetProperty("Left").GetDouble(), w.GetProperty("Bottom").GetDouble(),
                w.GetProperty("Right").GetDouble(), w.GetProperty("Top").GetDouble())).ToArray())).ToArray();
        return new FormFieldList(fields, data.GetProperty("FieldCount").GetInt32(), Flag(data, "Truncated"),
            Flag(data, "HasXfa"), Flag(data, "XfaOnly"), Flag(data, "NeedAppearances"));
    }
}
