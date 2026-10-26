using System.Globalization;
using System.Text.Json;

namespace HunterPdf.Core;

public sealed record FreeTextColor(int Red, int Green, int Blue);

/// <summary>
/// FreeText box properties. For adding, Page, the four edges and Text are required and unset fields use defaults
/// (Helvetica 12 pt black, left aligned, no border or fill). For updating, only the fields that are set change.
/// The edges are in PDF default user space (unrotated page coordinates, origin at the bottom-left of the visible page).
/// </summary>
public sealed record FreeTextOptions
{
    public int? Page { get; init; }
    public double? Left { get; init; }
    public double? Bottom { get; init; }
    public double? Right { get; init; }
    public double? Top { get; init; }
    public string? Text { get; init; }
    public string? Font { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public double? FontSize { get; init; }
    public FreeTextColor? Color { get; init; }
    public string? Align { get; init; }
    public double? BorderWidth { get; init; }
    public FreeTextColor? BorderColor { get; init; }
    public FreeTextColor? FillColor { get; init; }
    /// <summary>Update only: remove the background fill.</summary>
    public bool ClearFill { get; init; }
}

public sealed record FreeTextInfo(long Id, int Page, double Left, double Bottom, double Right, double Top, string Text,
    string Font, bool Bold, bool Italic, double FontSize, int Red, int Green, int Blue, string Align, bool Locked,
    double BorderWidth = 0, FreeTextColor? BorderColor = null, FreeTextColor? FillColor = null);

/// <summary>Final box after wrapping; <see cref="Grown"/> is true when the box was extended downward to fit the text.</summary>
public sealed record FreeTextEditResult(long Id, int Page, double Left, double Bottom, double Right, double Top, bool Grown);

public sealed record UnicodeTextOptions(int Page, double X, double Y, string Text, string FontFile, int FaceIndex,
    double FontSize, FreeTextColor? Color = null);

public sealed record UnicodeTextResult(int Page, int Lines, long FontBytes, long FontFileBytes, bool Subset, int GlyphsUsed);

public sealed partial class DocumentSession
{
    const long MaxEmbeddedFontFileBytes = 25L * 1024 * 1024;
    static readonly string[] FreeTextFonts = ["Helvetica", "Times", "Courier"];

    static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static double Finite(double? value, string name, double low, double high)
    {
        if (value is not { } v || !double.IsFinite(v) || v < low || v > high)
            throw new ArgumentException($"{name} is missing or outside the supported range.", name);
        return v;
    }

    static void ValidateColor(FreeTextColor? color, string name)
    {
        if (color is { } c && (c.Red is < 0 or > 255 || c.Green is < 0 or > 255 || c.Blue is < 0 or > 255))
            throw new ArgumentException("Color channels must be between 0 and 255.", name);
    }

    static void AddColor(Dictionary<string, object?> args, string prefix, FreeTextColor? color)
    {
        if (color is null) return;
        args[prefix + "Red"] = color.Red; args[prefix + "Green"] = color.Green; args[prefix + "Blue"] = color.Blue;
    }

    static void ValidateStyle(FreeTextOptions o)
    {
        if (o.Font is not null && !FreeTextFonts.Contains(o.Font, StringComparer.Ordinal))
            throw new ArgumentException("The font must be Helvetica, Times or Courier.", nameof(o));
        if (o.FontSize is { } size) Finite(size, "FontSize", 6, 144);
        if (o.Align is not null && o.Align is not ("Left" or "Center" or "Right"))
            throw new ArgumentException("Alignment must be Left, Center or Right.", nameof(o));
        if (o.BorderWidth is { } border) Finite(border, "BorderWidth", 0, 12);
        ValidateColor(o.Color, nameof(o.Color)); ValidateColor(o.BorderColor, nameof(o.BorderColor)); ValidateColor(o.FillColor, nameof(o.FillColor));
        if (o.Text is not null && (o.Text.Length is < 1 or > 4000 || string.IsNullOrWhiteSpace(o.Text)))
            throw new ArgumentException("The text must be between 1 and 4000 characters.", nameof(o));
    }

    static void AddStyle(Dictionary<string, object?> args, FreeTextOptions o)
    {
        if (o.Text is not null) args["Text"] = o.Text;
        if (o.Font is not null) args["Font"] = o.Font;
        if (o.Bold is { } bold) args["Bold"] = bold;
        if (o.Italic is { } italic) args["Italic"] = italic;
        if (o.FontSize is { } size) args["FontSize"] = size;
        AddColor(args, "", o.Color);
        if (o.Align is not null) args["Align"] = o.Align;
        if (o.BorderWidth is { } border) args["BorderWidth"] = border;
        AddColor(args, "Border", o.BorderColor); AddColor(args, "Fill", o.FillColor);
        if (o.ClearFill) args["ClearFill"] = true;
    }

    static bool HasRect(FreeTextOptions o) => o.Left.HasValue || o.Bottom.HasValue || o.Right.HasValue || o.Top.HasValue;

    static void AddRect(Dictionary<string, object?> args, FreeTextOptions o)
    {
        var left = Finite(o.Left, "Left", -1e7, 1e7); var bottom = Finite(o.Bottom, "Bottom", -1e7, 1e7);
        var right = Finite(o.Right, "Right", -1e7, 1e7); var top = Finite(o.Top, "Top", -1e7, 1e7);
        if (right - left < 12 || top - bottom < 8) throw new ArgumentException("The text box is too small.", nameof(o));
        args["Left"] = left; args["Bottom"] = bottom; args["Right"] = right; args["Top"] = top;
    }

    static Dictionary<string, object?> GuardArguments(FreeTextInfo expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Id < 1) throw new ArgumentException("The text box identity is invalid.", nameof(expected));
        return new Dictionary<string, object?>
        {
            ["Id"] = expected.Id, ["Page"] = expected.Page, ["ExpectedText"] = expected.Text,
            ["ExpectedLeft"] = expected.Left, ["ExpectedBottom"] = expected.Bottom,
            ["ExpectedRight"] = expected.Right, ["ExpectedTop"] = expected.Top
        };
    }

    static FreeTextEditResult ParseEdit(JsonElement r) => new(r.GetProperty("Id").GetInt64(), r.GetProperty("Page").GetInt32(),
        r.GetProperty("Left").GetDouble(), r.GetProperty("Bottom").GetDouble(), r.GetProperty("Right").GetDouble(),
        r.GetProperty("Top").GetDouble(), r.GetProperty("Grown").GetBoolean());

    async Task<JsonElement> ApplyRichTextAsync(AdvancedEngineLocation location, string operation, Dictionary<string, object?> args,
        long expectedRevision, CancellationToken cancellation)
    {
        RequireLocation(location);
        var captured = default(JsonElement);
        await ApplyExternalCandidateAsync(ExternalEditKind.RichText, expectedRevision, async (input, candidate, token) =>
        {
            var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript)
                .RunAsync(CompanionRequest(operation, input, candidate, args), token);
            if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True)
                throw new IOException("The advanced PDF engine did not confirm the change.");
            if (!File.Exists(candidate)) throw new IOException("The advanced PDF engine did not write its candidate.");
            captured = result.Clone();
        }, cancellation);
        return captured;
    }

    /// <summary>Adds an editable FreeText annotation (real /FreeText with /DA, /Contents and an appearance stream, Print flag set).</summary>
    public async Task<FreeTextEditResult> AddFreeTextAsync(AdvancedEngineLocation location, FreeTextOptions options,
        long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Page is not { } page || page < 0 || page >= PageCount)
            throw new ArgumentException("The selected page is outside the current document.", nameof(options));
        if (options.Text is null) throw new ArgumentException("Text is required.", nameof(options));
        ValidateStyle(options);
        var args = new Dictionary<string, object?> { ["Page"] = page };
        AddRect(args, options);
        AddStyle(args, options);
        return ParseEdit(await ApplyRichTextAsync(location, "AddFreeText", args, expectedRevision, cancellation));
    }

    /// <summary>Lists FreeText annotations on one page, or on every page when <paramref name="page"/> is null.</summary>
    public async Task<IReadOnlyList<FreeTextInfo>> ListFreeTextAsync(AdvancedEngineLocation location, int? page,
        CancellationToken cancellation = default)
    {
        if (page is { } p && (p < 0 || p >= PageCount))
            throw new ArgumentException("The selected page is outside the current document.", nameof(page));
        var result = await QueryCompanionAsync(location, "ListFreeText", new Dictionary<string, object?> { ["Page"] = page }, cancellation);
        if (result.TryGetProperty("Truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("There are too many text boxes to list at once; list a single page instead.");
        FreeTextColor? Color(JsonElement item, string prefix) =>
            item.GetProperty(prefix + "Red").ValueKind == JsonValueKind.Number
                ? new FreeTextColor(item.GetProperty(prefix + "Red").GetInt32(), item.GetProperty(prefix + "Green").GetInt32(), item.GetProperty(prefix + "Blue").GetInt32())
                : null;
        return result.GetProperty("Items").EnumerateArray().Select(item => new FreeTextInfo(item.GetProperty("Id").GetInt64(),
            item.GetProperty("Page").GetInt32(), item.GetProperty("Left").GetDouble(), item.GetProperty("Bottom").GetDouble(),
            item.GetProperty("Right").GetDouble(), item.GetProperty("Top").GetDouble(), item.GetProperty("Text").GetString() ?? "",
            item.GetProperty("Font").GetString() ?? "Helvetica", item.GetProperty("Bold").GetBoolean(), item.GetProperty("Italic").GetBoolean(),
            item.GetProperty("FontSize").GetDouble(), item.GetProperty("Red").GetInt32(), item.GetProperty("Green").GetInt32(),
            item.GetProperty("Blue").GetInt32(), item.GetProperty("Align").GetString() ?? "Left", item.GetProperty("Locked").GetBoolean(),
            item.GetProperty("BorderWidth").GetDouble(), Color(item, "Border"), Color(item, "Fill"))).ToArray();
    }

    /// <summary>Changes a listed FreeText box. The edit is refused if the box's text or rectangle no longer match <paramref name="expected"/>.</summary>
    public async Task<FreeTextEditResult> UpdateFreeTextAsync(AdvancedEngineLocation location, FreeTextInfo expected,
        FreeTextOptions changes, long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var args = GuardArguments(expected);
        if (expected.Locked) throw new InvalidOperationException("This text box is locked and cannot be changed.");
        ValidateStyle(changes);
        if (HasRect(changes)) AddRect(args, changes);
        AddStyle(args, changes);
        return ParseEdit(await ApplyRichTextAsync(location, "UpdateFreeText", args, expectedRevision, cancellation));
    }

    public async Task DeleteFreeTextAsync(AdvancedEngineLocation location, FreeTextInfo expected, long expectedRevision,
        CancellationToken cancellation = default)
    {
        var args = GuardArguments(expected);
        if (expected.Locked) throw new InvalidOperationException("This text box is locked and cannot be deleted.");
        await ApplyRichTextAsync(location, "DeleteFreeText", args, expectedRevision, cancellation);
    }

    /// <summary>
    /// Adds page text (not an annotation) in an installed font, embedded as a Unicode (Identity-H) font with a ToUnicode map.
    /// The font must come from <see cref="FontCatalog"/> and be embeddable. X and Y are the baseline origin in PDF user space.
    /// </summary>
    public async Task<UnicodeTextResult> AddUnicodeTextAsync(AdvancedEngineLocation location, UnicodeTextOptions options,
        long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Page < 0 || options.Page >= PageCount)
            throw new ArgumentException("The selected page is outside the current document.", nameof(options));
        Finite(options.X, "X", -1e7, 1e7); Finite(options.Y, "Y", -1e7, 1e7); Finite(options.FontSize, "FontSize", 4, 200);
        if (options.Text is null || options.Text.Length is < 1 or > 2000 || string.IsNullOrWhiteSpace(options.Text))
            throw new ArgumentException("The text must be between 1 and 2000 characters.", nameof(options));
        ValidateColor(options.Color, nameof(options.Color));
        if (string.IsNullOrWhiteSpace(options.FontFile) || options.FaceIndex < 0)
            throw new ArgumentException("An installed font is required.", nameof(options));
        var font = FontCatalog.List().FirstOrDefault(f => f.FaceIndex == options.FaceIndex &&
            string.Equals(f.Path, options.FontFile, StringComparison.OrdinalIgnoreCase));
        if (font is null) throw new ArgumentException("The font must be an installed font listed by the font catalog.", nameof(options));
        if (!font.Embeddable) throw new InvalidOperationException("This font's license does not permit embedding.");
        long size;
        try { size = new FileInfo(font.Path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("The font file could not be read."); }
        if (size > MaxEmbeddedFontFileBytes)
            throw new InvalidOperationException($"The font file is {size / (1024 * 1024)} MB, which exceeds the 25 MB embedding limit.");
        var args = new Dictionary<string, object?>
        {
            ["Page"] = options.Page, ["X"] = options.X, ["Y"] = options.Y, ["Text"] = options.Text, ["FontFile"] = font.Path,
            ["FaceIndex"] = font.FaceIndex, ["FontSize"] = options.FontSize
        };
        AddColor(args, "", options.Color);
        var r = await ApplyRichTextAsync(location, "AddUnicodeText", args, expectedRevision, cancellation);
        return new UnicodeTextResult(r.GetProperty("Page").GetInt32(), r.GetProperty("Lines").GetInt32(), r.GetProperty("FontBytes").GetInt64(),
            r.GetProperty("FontFileBytes").GetInt64(), r.GetProperty("Subset").GetBoolean(), r.GetProperty("GlyphsUsed").GetInt32());
    }
}
