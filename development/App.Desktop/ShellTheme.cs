using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace HunterPdf.Desktop;

internal static class ShellThemeResources
{
    public static void Apply(ResourceDictionary resources, ShellTheme theme, bool highContrast)
    {
        if (highContrast)
        {
            resources["AppBackground"] = SystemColors.WindowBrush;
            resources["PanelBrush"] = SystemColors.ControlBrush;
            resources["CanvasBrush"] = SystemColors.ControlBrush;
            resources["TextBrush"] = SystemColors.WindowTextBrush;
            resources["MutedBrush"] = SystemColors.WindowTextBrush;
            resources["LineBrush"] = SystemColors.WindowTextBrush;
            resources["AccentBrush"] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
            resources["FocusBrush"] = SystemColors.HighlightBrush;
            resources["DisabledBrush"] = SystemColors.GrayTextBrush;
            resources["PrimaryBrush"] = SystemColors.HighlightBrush;
            resources["PrimaryTextBrush"] = SystemColors.HighlightTextBrush;
            resources["ChromeLineBrush"] = SystemColors.WindowTextBrush;
            resources["ModeBarBrush"] = SystemColors.WindowBrush;
            resources["PopupBrush"] = SystemColors.WindowBrush;
            resources["ToolBrush"] = SystemColors.HighlightBrush;
            resources["TintBrush"] = SystemColors.HighlightBrush;
            resources["SavedBrush"] = SystemColors.WindowTextBrush;
            resources["UnsavedBrush"] = SystemColors.WindowTextBrush;
            ApplyDensity(resources, compact: false);
            return;
        }
        var dark = theme == ShellTheme.Dark || (theme == ShellTheme.System && IsSystemDarkTheme());
        // Graphite is the user-requested look of the PDF Workspace design (variant 4a): its exact palette and its
        // compact density. The brand themes keep brand-token colors and 44 DIP targets.
        var graphite = theme == ShellTheme.Graphite;
        var colors = graphite
            ? new[] { "#16181B", "#202227", "#131417", "#E6E8EC", "#9AA0AA", "#3A3E46", "#7FA2FF", "#16181B", "#2B2E34", "#7FA2FF", "#6B717B", "#3D6DEB", "#FFFFFF",
                "#2F3238", "#1B1D21", "#25282E", "#2A3550", "#24304A", "#2E9E5B", "#D49A1A" }
            : dark
            ? new[] { "#171928", "#171928", "#171928", "#FFFFFF", "#B5ACD4", "#B5ACD4", "#9279FB", "#171928", "#5A5470", "#1FA6A8", "#B5ACD4", "#1FA6A8", "#171928",
                "#B5ACD4", "#171928", "#171928", "#5A5470", "#5A5470", "#1FA6A8", "#E8C078" }
            : new[] { "#F5F6FA", "#FFFFFF", "#F5F6FA", "#171928", "#5A5470", "#5A5470", "#33178B", "#FFFFFF", "#F5F6FA", "#33178B", "#5A5470", "#1FA6A8", "#171928",
                "#5A5470", "#F5F6FA", "#FFFFFF", "#F5F6FA", "#F5F6FA", "#1FA6A8", "#E8C078" };
        var keys = new[] { "AppBackground", "PanelBrush", "CanvasBrush", "TextBrush", "MutedBrush", "LineBrush",
            "AccentBrush", "AccentTextBrush", "HoverBrush", "FocusBrush", "DisabledBrush", "PrimaryBrush", "PrimaryTextBrush",
            "ChromeLineBrush", "ModeBarBrush", "PopupBrush", "ToolBrush", "TintBrush", "SavedBrush", "UnsavedBrush" };
        ApplyDensity(resources, graphite);
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            resources[keys[i]] = brush;
        }
    }

    /// <summary>Control sizes, text sizes and menu-label visibility; compact is the Graphite design's density.</summary>
    static void ApplyDensity(ResourceDictionary resources, bool compact)
    {
        resources["ControlHeight"] = compact ? 30d : 44d;
        resources["RowHeight"] = compact ? 32d : 44d;
        resources["ChipHeight"] = compact ? 26d : 44d;
        resources["TabHeight"] = compact ? 36d : 44d;
        resources["UiFontSize"] = compact ? 13d : 16d;
        resources["SmallFontSize"] = compact ? 12d : 14d;
        resources["MenuLabelVisibility"] = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    static bool IsSystemDarkTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

