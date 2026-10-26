using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HunterPdf.Desktop;

/// <summary>The Tools menu: dialogs that run the companion engine's quick tools on the open document.</summary>
public partial class MainWindow
{
    internal QuickToolWindow? CreateQuickTool(string name)
    {
        if (active is not { } tab) return null;
        var (session, page) = (tab.Session, tab.CurrentPage);
        return name switch
        {
            "Compress" => new CompressWindow(session, AdvancedEngine, page),
            "Protect" => new ProtectWindow(session, AdvancedEngine, page),
            "Unprotect" => new UnprotectWindow(session, AdvancedEngine, page),
            "Watermark" => new WatermarkWindow(session, AdvancedEngine, page),
            "HeaderFooter" => new HeaderFooterWindow(session, AdvancedEngine, page),
            "Bates" => new BatesWindow(session, AdvancedEngine, page),
            "Crop" => new CropWindow(session, AdvancedEngine, page),
            "Bookmarks" => new BookmarksWindow(session, AdvancedEngine, page),
            "Links" => new LinksWindow(session, AdvancedEngine, page),
            "Properties" => new PropertiesWindow(session, AdvancedEngine, page),
            "Flatten" => new FlattenWindow(session, AdvancedEngine, page),
            "ExportImages" => new ExportImagesWindow(session, AdvancedEngine, page),
            "ExtractImages" => new ExtractImagesWindow(session, AdvancedEngine, page),
            _ => null
        };
    }

    /// <summary>Read-only comparison with another PDF, opened in its own temporary session beside the recovery store.</summary>
    internal CompareWindow? CreateCompareWindow() => active is { } tab
        ? new CompareWindow(tab.Session, workerExecutable, Path.GetDirectoryName(recovery.Root)!) { Owner = this }
        : null;

    void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CreateCompareWindow() is not { } window) return;
        window.ShowDialog();
    }

    async void QuickTool_Click(object sender, RoutedEventArgs e)
    {
        if (busy || active is not { } tab || sender is not Button { CommandParameter: string name }) return;
        if (name != "ExportImages" && !AdvancedEngineInstalled)
        {
            dialogs.Information("This tool needs the companion engine, which is not installed with this copy of the app.", "Tool unavailable");
            return;
        }
        // Edits change the document, so signed documents need the usual copy consent first.
        if (name is "Watermark" or "HeaderFooter" or "Bates" or "Crop" or "Bookmarks" or "Links" or "Properties" && !await AllowEditingAsync(tab)) return;
        if (CreateQuickTool(name) is not { } window) return;
        window.Owner = this;
        window.ShowDialog();
        await RunUiAsync(async () =>
        {
            if (window.Changed) tab.Search = null;
            if (window.ResultPath is { } path && File.Exists(path) && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) &&
                dialogs.Confirm($"{Path.GetFileName(path)} was created. Open it in a new tab?", "New PDF created"))
                await OpenPathAsync(path);
            else if (window.ResultPath is { } created) notice = "Created " + created + ". This document is unchanged.";
        }, "Refreshing…");
    }
}
