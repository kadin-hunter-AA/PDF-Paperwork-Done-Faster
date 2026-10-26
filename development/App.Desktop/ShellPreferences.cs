using System.IO;
using System.Text.Json;

namespace HunterPdf.Desktop;

internal enum ShellTheme { Dark = 0, Light = 1, System = 2, Graphite = 3 }
internal enum PageLayout { Continuous = 0, SinglePage = 1, TwoPage = 2, TwoPageCover = 3 }
/// <summary>The last color, line width and text settings used with one tool. Font is a base font name or an installed font file path.</summary>
internal sealed record ToolStyle(byte Red, byte Green, byte Blue, double Width, double FontSize,
    string Font = "Helvetica", bool Bold = false, bool Italic = false, string Align = "Left");
internal sealed record RecentDocument(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
    public string AutomationName => Name + (File.Exists(FullPath) ? "" : ", unavailable");
    public string Availability => File.Exists(FullPath) ? FullPath : "Unavailable — " + FullPath;
}
internal sealed class ShellPreferences
{
    readonly string path;
    public ShellTheme Theme { get; set; } = ShellTheme.System;
    public List<string> RecentFiles { get; } = new();
    public PageLayout Layout { get; set; } = PageLayout.Continuous;
    public Dictionary<string, ToolStyle> ToolStyles { get; } = new(StringComparer.Ordinal);
    /// <summary>Undo steps kept per document; older checkpoints are pruned (the opened and saved versions are always kept).</summary>
    public int HistorySteps { get; set; } = 50;
    public string? Error { get; private set; }

    public ShellPreferences(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            if (data is null || !Enum.IsDefined(data.Theme) || data.RecentFiles is null) throw new JsonException();
            Theme = data.Theme;
            RecentFiles.AddRange(data.RecentFiles.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
            if (data.Layout is { } layout && Enum.IsDefined(layout)) Layout = layout;
            if (data.HistorySteps is >= 5 and <= 500) HistorySteps = data.HistorySteps.Value;
            foreach (var (tool, style) in data.ToolStyles ?? new())
                if (tool.Length <= 32 && style is not null && double.IsFinite(style.Width) && style.Width is >= 0.5 and <= 12 &&
                    double.IsFinite(style.FontSize) && style.FontSize is >= 6 and <= 144)
                    ToolStyles[tool] = style;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Error = "Recent history and theme settings could not be loaded.";
        }
    }

    public void Remember(string source)
    {
        source = Path.GetFullPath(source);
        RecentFiles.RemoveAll(p => string.Equals(p, source, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, source);
        if (RecentFiles.Count > 20) RecentFiles.RemoveRange(20, RecentFiles.Count - 20);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".settings.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(new Settings(Theme, RecentFiles.ToArray(), Layout, ToolStyles, HistorySteps)));
                file.Flush(true);
            }
            File.Move(temporary, path, true);
            Error = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Fields added after the first release are optional so older settings files still load.
    sealed record Settings(ShellTheme Theme, string[] RecentFiles, PageLayout? Layout = null,
        Dictionary<string, ToolStyle>? ToolStyles = null, int? HistorySteps = null);
}

