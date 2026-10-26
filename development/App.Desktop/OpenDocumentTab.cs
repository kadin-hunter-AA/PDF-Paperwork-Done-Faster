using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HunterPdf.Core;
using System.Windows.Media.Imaging;

namespace HunterPdf.Desktop;

internal sealed class OpenDocumentTab(DocumentSession session, bool recovered = false) : INotifyPropertyChanged
{
    public DocumentSession Session { get; } = session;
    public string FileName => Path.GetFileName(Session.LastSavePath);
    public string Header => (Session.IsDirty ? "* " : "") + FileName + (recovered ? " (recovered)" : "");
    public string AutomationName => FileName + (Session.IsDirty ? ", unsaved changes" : ", saved") + (recovered ? ", recovered" : "");
    public string FullPath => Session.LastSavePath;
    public string DraftText { get; set; } = "";
    public string TextX { get; set; } = "72";
    public string TextY { get; set; } = "120";
    public int Zoom { get; set; } = 100;
    public int CurrentPage { get; set; }
    public long PageInfoRevision { get; set; } = -1;
    public List<PageDescriptor> Pages { get; } = new();
    public Dictionary<int, BitmapSource> RenderCache { get; } = new();
    public Queue<int> RenderOrder { get; } = new();
    public Queue<int> ThumbnailOrder { get; } = new();
    public List<BookmarkDescriptor> Bookmarks { get; } = new();
    /// <summary>The last completed find; cleared when the query, options or document revision change.</summary>
    public SearchResults? Search { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        foreach (var property in new[] { nameof(FileName), nameof(Header), nameof(FullPath), nameof(AutomationName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

internal sealed class PageDescriptor(int number, float width, float height, int rotation)
{
    public int Number { get; } = number;
    public float Width { get; } = width;
    public float Height { get; } = height;
    public int Rotation { get; } = rotation;
    /// <summary>User-space origin (left, bottom) of the visible page box; non-zero when the CropBox/MediaBox does not start at (0,0).</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }
    public BitmapSource? Thumbnail { get; set; }
    public List<PdfTextCharacter>? Characters { get; set; }
    public string Label => "Page " + (Number + 1);
    public string DisplayNumber => (Number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
internal sealed record BookmarkDescriptor(string Title, int Page, int Depth);
internal sealed record PdfTextCharacter(int Index, string Text, bool HasBounds, double Left, double Bottom, double Right, double Top);

public static class ShellCommands
{
    public static RoutedUICommand NextTab { get; } = new("Next document", "NextTab", typeof(ShellCommands));
    public static RoutedUICommand PreviousTab { get; } = new("Previous document", "PreviousTab", typeof(ShellCommands));
    public static RoutedUICommand ZoomIn { get; } = new("Zoom in", "ZoomIn", typeof(ShellCommands));
    public static RoutedUICommand ZoomOut { get; } = new("Zoom out", "ZoomOut", typeof(ShellCommands));
    public static RoutedUICommand FitPage { get; } = new("Fit page", "FitPage", typeof(ShellCommands));
    public static RoutedUICommand FitWidth { get; } = new("Fit width", "FitWidth", typeof(ShellCommands));
    public static RoutedUICommand ActualSize { get; } = new("Actual size", "ActualSize", typeof(ShellCommands));
    public static RoutedUICommand GoToPage { get; } = new("Go to page", "GoToPage", typeof(ShellCommands));
    public static RoutedUICommand Print { get; } = new("Print", "Print", typeof(ShellCommands));
    public static RoutedUICommand RotateClockwise { get; } = new("Rotate clockwise", "RotateClockwise", typeof(ShellCommands));
    public static RoutedUICommand RotateCounterclockwise { get; } = new("Rotate counterclockwise", "RotateCounterclockwise", typeof(ShellCommands));
    public static RoutedUICommand ToggleNavigation { get; } = new("Page sidebar", "ToggleNavigation", typeof(ShellCommands));
    public static RoutedUICommand ToggleSidePanel { get; } = new("Side panel", "ToggleSidePanel", typeof(ShellCommands));
}

