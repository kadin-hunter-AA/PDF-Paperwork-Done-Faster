using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HunterPdf.Core;

public sealed record OcrPageInfo(int Page, string Kind, int TextChars, int Images, double Coverage, long Pixels, double WidthPoints, double HeightPoints);
public sealed record OcrInspection(int PageCount, bool Truncated, int HasText, int ImageOnly, int Empty, int Other,
    long ImageOnlyPixels, IReadOnlyList<int> ImageOnlyPages, IReadOnlyList<OcrPageInfo> Pages);
/// <summary>Pages are zero-based. Languages must be verified catalog codes (see <see cref="TessdataCatalog"/>).</summary>
public sealed record OcrPagesOptions(IReadOnlyList<int>? Pages, IReadOnlyList<string> Languages,
    IReadOnlyList<string> TessdataDirectories, bool Deskew = false, bool AutoRotate = true, int ChunkSize = 5);
public sealed record OcrPageResult(int Page, int Words, int Characters, int RotationDegrees, double SkewDegrees);
public sealed record OcrPagesResult(IReadOnlyList<OcrPageResult> Recognized, IReadOnlyList<int> SkippedHasText,
    IReadOnlyList<int> SkippedNoImage, IReadOnlyList<int> SkippedTooLarge, IReadOnlyList<int> NoTextFound,
    bool OsdAvailable, string OrientationMethod);
public sealed record OcrLanguageInfo(string Code, string Name, long SizeBytes);

public sealed partial class DocumentSession
{
    internal const int OcrMaxPages = 2000;

    /// <summary>Classifies every page (text / scanned image / empty / other) without changing the document.</summary>
    public async Task<OcrInspection> InspectOcrAsync(AdvancedEngineLocation location, CancellationToken cancellation = default)
    {
        var data = await QueryCompanionAsync(location, "OcrInspect", null, cancellation, AdvancedEngineClient.ScaledTimeout(0, PageCount * 4));
        return ParseInspection(data);
    }

    internal static OcrInspection ParseInspection(JsonElement data)
    {
        var totals = data.GetProperty("Totals");
        return new OcrInspection(data.GetProperty("PageCount").GetInt32(), data.GetProperty("Truncated").GetBoolean(),
            totals.GetProperty("HasText").GetInt32(), totals.GetProperty("ImageOnly").GetInt32(), totals.GetProperty("Empty").GetInt32(),
            totals.GetProperty("Other").GetInt32(), totals.GetProperty("ImageOnlyPixels").GetInt64(),
            data.GetProperty("ImageOnlyPages").EnumerateArray().Select(e => e.GetInt32()).ToList(),
            data.GetProperty("Pages").EnumerateArray().Select(p => new OcrPageInfo(p.GetProperty("Page").GetInt32(),
                p.GetProperty("Kind").GetString()!, p.GetProperty("TextChars").GetInt32(), p.GetProperty("Images").GetInt32(),
                p.GetProperty("Coverage").GetDouble(), p.GetProperty("Pixels").GetInt64(),
                p.GetProperty("WidthPoints").GetDouble(), p.GetProperty("HeightPoints").GetDouble())).ToList());
    }

    /// <summary>Lists language packs found by the engine in the given folders (not hash-verified; see TessdataCatalog.Installed).</summary>
    public async Task<IReadOnlyList<OcrLanguageInfo>> ListOcrLanguagesAsync(AdvancedEngineLocation location,
        IReadOnlyList<string> tessdataDirectories, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(tessdataDirectories);
        var data = await QueryCompanionAsync(location, "OcrLanguages", new Dictionary<string, object?>
            { ["TessdataDirectories"] = tessdataDirectories.Select(AdvancedEngineClient.FilePath).ToArray() }, cancellation);
        return data.GetProperty("Languages").EnumerateArray().Select(l => new OcrLanguageInfo(l.GetProperty("Code").GetString()!,
            l.GetProperty("Name").GetString()!, l.GetProperty("SizeBytes").GetInt64())).ToList();
    }

    /// <summary>
    /// Adds an invisible text layer to scanned pages (all image-only pages when Pages is null). Pages that already have text
    /// are skipped and reported. Work runs in chunks inside one undoable checkpoint; cancellation or any failure discards everything.
    /// </summary>
    public async Task<OcrPagesResult> RecognizePagesAsync(AdvancedEngineLocation location, OcrPagesOptions options,
        long expectedRevision, IProgress<(int done, int total)>? progress = null, CancellationToken cancellation = default)
    {
        RequireLocation(location);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Languages is null || options.Languages.Count is < 1 or > 4 || options.Languages.Distinct().Count() != options.Languages.Count)
            throw new ArgumentException("Choose between one and four OCR languages.", nameof(options));
        if (options.TessdataDirectories is null || options.TessdataDirectories.Count is < 1 or > 4)
            throw new ArgumentException("Language folders are required.", nameof(options));
        if (options.ChunkSize is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(options), "Chunk size must be 1 to 10.");
        var installed = TessdataCatalog.Installed(options.TessdataDirectories);
        foreach (var code in options.Languages)
            if (!installed.Any(i => i.Code == code && i.Verified))
                throw new ArgumentException("Only installed, integrity-checked OCR languages can be used.", nameof(options));
        if (options.Pages is not null)
        {
            if (options.Pages.Count == 0 || options.Pages.Count > OcrMaxPages) throw new ArgumentException("Choose between 1 and 2000 pages.", nameof(options));
            if (options.Pages.Any(p => p < 0 || p >= PageCount)) throw new ArgumentOutOfRangeException(nameof(options), "A page number is outside the document.");
        }
        var dirs = options.TessdataDirectories.Select(AdvancedEngineClient.FilePath).ToArray();
        OcrPagesResult? summary = null;
        await ApplyExternalCandidateAsync(ExternalEditKind.Ocr, expectedRevision, async (input, candidate, token) =>
        {
            List<int> targets;
            if (options.Pages is not null) targets = options.Pages.Distinct().ToList();
            else
            {
                var inspected = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript,
                    AdvancedEngineClient.ScaledTimeout(new FileInfo(input).Length, PageCount * 4))
                    .RunAsync(CompanionRequest("OcrInspect", input, null, null), token);
                var info = ParseInspection(inspected);
                if (info.Truncated || info.ImageOnly > info.ImageOnlyPages.Count)
                    throw new InvalidOperationException("This document has too many pages to recognize in one run. Choose specific pages.");
                targets = info.ImageOnlyPages.ToList();
            }
            if (targets.Count == 0) throw new InvalidOperationException("No scanned pages without text were found.");
            var total = targets.Count;
            var recognized = new List<OcrPageResult>();
            List<int> hasText = [], noImage = [], tooLarge = [], noText = [];
            var osd = false; var method = options.AutoRotate ? "trial" : "off";
            progress?.Report((0, total));
            var current = input; var done = 0; var temporaries = new List<string>();
            try
            {
                var chunks = targets.Chunk(options.ChunkSize).ToList();
                for (var c = 0; c < chunks.Count; c++)
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = chunks[c];
                    var output = c == chunks.Count - 1 ? candidate : candidate + ".part" + c;
                    if (output != candidate) temporaries.Add(output);
                    var timeout = TimeSpan.FromSeconds(Math.Min(900, 45 + 60 * chunk.Length * options.Languages.Count));
                    var request = CompanionRequest("OcrPages", current, output, new Dictionary<string, object?>
                    {
                        ["Pages"] = chunk, ["Languages"] = options.Languages, ["TessdataDirectories"] = dirs,
                        ["Deskew"] = options.Deskew, ["AutoRotate"] = options.AutoRotate
                    });
                    var result = await new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript, timeout).RunAsync(request, token);
                    if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True || !File.Exists(output))
                        throw new IOException("The OCR engine did not confirm its result.");
                    foreach (var r in result.GetProperty("Recognized").EnumerateArray())
                        recognized.Add(new OcrPageResult(r.GetProperty("Page").GetInt32(), r.GetProperty("Words").GetInt32(),
                            r.GetProperty("Characters").GetInt32(), r.GetProperty("RotationDegrees").GetInt32(), r.GetProperty("SkewDegrees").GetDouble()));
                    void Add(string name, List<int> into) { foreach (var e in result.GetProperty(name).EnumerateArray()) into.Add(e.GetInt32()); }
                    Add("SkippedHasText", hasText); Add("SkippedNoImage", noImage); Add("SkippedTooLarge", tooLarge); Add("NoTextFound", noText);
                    osd = result.GetProperty("OsdAvailable").GetBoolean();
                    done += chunk.Length; progress?.Report((done, total));
                    current = output;
                }
                if (recognized.Count == 0) throw new InvalidOperationException("No text could be recognized on the chosen pages.");
                summary = new OcrPagesResult(recognized, hasText, noImage, tooLarge, noText, osd, method);
            }
            finally { foreach (var t in temporaries) TryRemoveTemporary(t); }
        }, cancellation);
        return summary!;
    }
}

public sealed record TessdataLanguage(string Code, string Name, long SizeBytes, string Sha256, string Url);
public sealed record InstalledOcrLanguage(string Code, string Name, string Directory, bool Verified, bool Bundled);

/// <summary>Pinned tessdata_fast language packs. Downloads need explicit consent and are never started automatically.</summary>
public static class TessdataCatalog
{
    public const string SourceCommit = "87416418657359cb625c412a48b6e1d6d41c29bd";
    static string Url(string code) => $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{SourceCommit}/{code}.traineddata";
    static TessdataLanguage L(string code, string name, long size, string sha) => new(code, name, size, sha, Url(code));

    public static IReadOnlyList<TessdataLanguage> Languages { get; } =
    [
        L("eng", "English", 4113088, "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"),
        L("deu", "German", 1525436, "19d219bbb6672c869d20a9636c6816a81eb9a71796cb93ebe0cb1530e2cdb22d"),
        L("fra", "French", 1130365, "ced037562e8c80c13122dece28dd477d399af80911a28791a66a63ac1e3445ca"),
        L("spa", "Spanish", 2294433, "6f2e04d02774a18f01bed44b1111f2cd7f3ba7ac9dc4373cd3f898a40ea6b464"),
        L("ita", "Italian", 2701314, "b8f89e1e785118dac4d51ae042c029a64edb5c3ee42ef73027a6d412748d8827"),
        L("por", "Portuguese", 1982756, "c4932b937207a9514b7514d518b931a99938c02a28a5a5a553f8599ed58b7deb"),
        L("nld", "Dutch", 6050296, "ced0e5e046a84c908a6aa7accbef9a232c4a5d9a8276691b81c6ee64d02963f6"),
    ];

    /// <summary>Per-user folder for downloaded packs.</summary>
    public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HunterOps", "PdfEditor", "tessdata");

    /// <summary>Catalog packs found in the folders; Verified means the file size and SHA-256 match the pin.</summary>
    public static IReadOnlyList<InstalledOcrLanguage> Installed(IEnumerable<string> directories)
    {
        var found = new List<InstalledOcrLanguage>();
        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lang in Languages)
            {
                if (found.Any(f => f.Code == lang.Code && f.Verified)) continue;
                var file = Path.Combine(dir, lang.Code + ".traineddata");
                var info = new FileInfo(file);
                if (!info.Exists || info.Length != lang.SizeBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                found.Add(new InstalledOcrLanguage(lang.Code, lang.Name, dir, HashFile(file) == lang.Sha256, lang.Code == "eng"));
            }
        }
        return found.Where(f => f.Verified).ToList();
    }

    static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Downloads one pinned pack into <paramref name="folder"/> (default <see cref="UserFolder"/>). Requires explicit consent,
    /// refuses redirects and oversize or mismatching content, and publishes create-only after the hash matches.
    /// Returns the installed file path. <paramref name="handler"/> exists for tests.
    /// </summary>
    public static async Task<string> DownloadAsync(TessdataLanguage language, bool consent, IProgress<long>? progress = null,
        CancellationToken cancellation = default, string? folder = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!consent) throw new InvalidOperationException("Downloading a language pack needs your explicit permission.");
        if (!Languages.Contains(language)) throw new ArgumentException("Only catalog language packs can be downloaded.", nameof(language));
        folder ??= UserFolder;
        Directory.CreateDirectory(folder);
        RecoveryStore.RejectReparse(folder);
        var target = Path.Combine(folder, language.Code + ".traineddata");
        if (File.Exists(target)) return target;
        var temp = Path.Combine(folder, ".download-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromMinutes(10);
            using var response = await http.GetAsync(language.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("The language pack could not be downloaded.");
            if (response.Content.Headers.ContentLength is { } length && length != language.SizeBytes) throw new IOException("The language pack has an unexpected size.");
            using var hash = SHA256.Create();
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    received += read;
                    if (received > language.SizeBytes) throw new IOException("The language pack has an unexpected size.");
                    hash.TransformBlock(buffer, 0, read, null, 0); await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress?.Report(received);
                }
                hash.TransformFinalBlock([], 0, 0);
                await output.FlushAsync(cancellation);
            }
            if (received != language.SizeBytes || Convert.ToHexStringLower(hash.Hash!) != language.Sha256)
                throw new IOException("The downloaded language pack failed its integrity check and was discarded.");
            File.Move(temp, target, overwrite: false);
            return target;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
