using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>Color and line width for the drawing tools, remembered per tool across sessions.</summary>
public partial class MainWindow
{
    // The first entry matches the engine's default drawing color.
    static readonly (string Name, byte Red, byte Green, byte Blue)[] StrokeColors =
        [("Purple", 51, 23, 139), ("Black", 0, 0, 0), ("Blue", 0, 82, 204), ("Red", 204, 0, 0), ("Green", 0, 128, 64), ("Orange", 230, 120, 0)];
    static readonly double[] StrokeWidths = [1, 2, 3, 5, 8];
    bool syncingStyle;

    static bool IsTextTool(PageTool tool) => tool is PageTool.TextBox or PageTool.AddText;
    static readonly string[] BoxFonts = ["Helvetica", "Times", "Courier"];
    static readonly double[] TextSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72];
    IReadOnlyList<InstalledFont>? installedFonts;

    /// <summary>Fonts offered for Add text: the standard font, then installed fonts whose license allows embedding.</summary>
    IReadOnlyList<(string Label, string Value)> AddTextFonts()
    {
        installedFonts ??= FontCatalog.List().Where(f => f.Embeddable).ToArray();
        return [("Helvetica (standard)", "Helvetica"), .. installedFonts.Where(f => f.Style is "Regular" or "Normal" or "Book")
            .GroupBy(f => f.Family).Select(g => g.First()).OrderBy(f => f.Family, StringComparer.CurrentCultureIgnoreCase).Select(f => (f.Family, f.Path + "|" + f.FaceIndex))];
    }

    /// <summary>The text style for a tool: the remembered one, or black Helvetica at the tool's default size.</summary>
    internal ToolStyle TextStyleFor(PageTool tool) => preferences.ToolStyles.GetValueOrDefault(tool.ToString())
        ?? new ToolStyle(0, 0, 0, 1, tool == PageTool.AddText ? 18 : 12);

    static bool IsDrawTool(PageTool tool) => tool is PageTool.Pen or PageTool.Line or PageTool.Arrow or PageTool.Rectangle or PageTool.Ellipse;

    static double DefaultWidth(PageTool tool) => tool is PageTool.Rectangle or PageTool.Ellipse ? 2 : 1;

    internal DrawingStroke? StrokeFor(PageTool tool) =>
        preferences.ToolStyles.TryGetValue(tool.ToString(), out var style) ? new DrawingStroke(style.Red, style.Green, style.Blue, style.Width) : null;

    void InitializeToolStyles()
    {
        foreach (var (name, red, green, blue) in StrokeColors)
        {
            var swatch = new RadioButton
            {
                GroupName = "StrokeColor", Tag = name, Width = 22, Height = 22, Margin = new Thickness(2, 0, 2, 0), ToolTip = name,
                Template = SwatchTemplate(Color.FromRgb(red, green, blue))
            };
            AutomationProperties.SetName(swatch, name + " drawing color");
            swatch.Checked += (_, _) => StoreToolStyle();
            StrokeSwatches.Children.Add(swatch);
        }
        StrokeWidthChoice.ItemsSource = StrokeWidths.Select(w => w + " pt").ToArray();
        StrokeWidthChoice.SelectionChanged += (_, _) => StoreToolStyle();
        TextSizeChoice.ItemsSource = TextSizes.Select(s => s + " pt").ToArray();
        TextAlignChoice.ItemsSource = new[] { "Left", "Center", "Right" };
        TextFontChoice.DisplayMemberPath = "Label";
        foreach (var selector in new System.Windows.Controls.Primitives.Selector[] { TextFontChoice, TextSizeChoice, TextAlignChoice }) selector.SelectionChanged += (_, _) => StoreToolStyle();
        TextBoldToggle.Click += (_, _) => StoreToolStyle(); TextItalicToggle.Click += (_, _) => StoreToolStyle();
    }

    static ControlTemplate SwatchTemplate(Color color)
    {
        // A filled circle with an accent ring when selected or focused.
        var ring = new FrameworkElementFactory(typeof(Border), "Ring");
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        ring.SetValue(Border.PaddingProperty, new Thickness(2));
        var dot = new FrameworkElementFactory(typeof(Border));
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        dot.SetValue(Border.BackgroundProperty, new SolidColorBrush(color));
        ring.AppendChild(dot);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = ring };
        foreach (var property in new[] { RadioButton.IsCheckedProperty, UIElement.IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "Ring"));
            template.Triggers.Add(trigger);
        }
        return template;
    }

    sealed record FontItem(string Label, string Value);

    void ShowToolStyle()
    {
        var draw = IsDrawTool(pageTool);
        var text = IsTextTool(pageTool) && AdvancedEngineInstalled;
        StylePanel.Visibility = draw || text ? Visibility.Visible : Visibility.Collapsed;
        StrokeWidthChoice.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
        TextStylePanel.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        if (text) { ShowTextStyle(); return; }
        if (!draw) return;
        syncingStyle = true;
        try
        {
            var style = preferences.ToolStyles.GetValueOrDefault(pageTool.ToString());
            var (_, red, green, blue) = style is null ? StrokeColors[0]
                : StrokeColors.FirstOrDefault(c => c.Red == style.Red && c.Green == style.Green && c.Blue == style.Blue, StrokeColors[0]);
            foreach (var swatch in StrokeSwatches.Children.OfType<RadioButton>())
                swatch.IsChecked = StrokeColors.First(c => c.Name == (string)swatch.Tag) is var c && c.Red == red && c.Green == green && c.Blue == blue;
            var width = style?.Width ?? DefaultWidth(pageTool);
            StrokeWidthChoice.SelectedIndex = Math.Max(0, Array.IndexOf(StrokeWidths, StrokeWidths.MinBy(w => Math.Abs(w - width))));
        }
        finally { syncingStyle = false; }
    }

    void ShowTextStyle()
    {
        syncingStyle = true;
        try
        {
            var style = TextStyleFor(pageTool);
            var fonts = pageTool == PageTool.AddText ? AddTextFonts().Select(f => new FontItem(f.Label, f.Value)).ToArray()
                : BoxFonts.Select(f => new FontItem(f, f)).ToArray();
            TextFontChoice.ItemsSource = fonts;
            TextFontChoice.SelectedItem = fonts.FirstOrDefault(f => f.Value == style.Font) ?? fonts[0];
            TextSizeChoice.SelectedIndex = Math.Max(0, Array.IndexOf(TextSizes, TextSizes.MinBy(s => Math.Abs(s - style.FontSize))));
            TextBoldToggle.IsChecked = style.Bold; TextItalicToggle.IsChecked = style.Italic;
            // Bold/italic and alignment apply to text boxes; installed fonts are chosen by family for Add text.
            TextBoldToggle.Visibility = TextItalicToggle.Visibility = TextAlignChoice.Visibility = pageTool == PageTool.TextBox ? Visibility.Visible : Visibility.Collapsed;
            TextAlignChoice.SelectedItem = style.Align;
            foreach (var swatch in StrokeSwatches.Children.OfType<RadioButton>())
                swatch.IsChecked = StrokeColors.First(c => c.Name == (string)swatch.Tag) is var c && c.Red == style.Red && c.Green == style.Green && c.Blue == style.Blue;
        }
        finally { syncingStyle = false; }
    }

    void StoreToolStyle()
    {
        if (syncingStyle) return;
        if (IsTextTool(pageTool))
        {
            var current = TextStyleFor(pageTool);
            var color = StrokeSwatches.Children.OfType<RadioButton>().FirstOrDefault(s => s.IsChecked == true) is { Tag: string swatch }
                ? StrokeColors.First(c => c.Name == swatch) : ("", current.Red, current.Green, current.Blue);
            preferences.ToolStyles[pageTool.ToString()] = current with
            {
                Red = color.Item2, Green = color.Item3, Blue = color.Item4,
                FontSize = TextSizeChoice.SelectedIndex >= 0 ? TextSizes[TextSizeChoice.SelectedIndex] : current.FontSize,
                Font = (TextFontChoice.SelectedItem as FontItem)?.Value ?? current.Font,
                Bold = TextBoldToggle.IsChecked == true, Italic = TextItalicToggle.IsChecked == true,
                Align = TextAlignChoice.SelectedItem as string ?? current.Align
            };
            SavePreferences();
            return;
        }
        if (!IsDrawTool(pageTool) || StrokeWidthChoice.SelectedIndex < 0) return;
        if (StrokeSwatches.Children.OfType<RadioButton>().FirstOrDefault(s => s.IsChecked == true) is not { Tag: string name }) return;
        var (_, red, green, blue) = StrokeColors.First(c => c.Name == name);
        preferences.ToolStyles[pageTool.ToString()] = new ToolStyle(red, green, blue, StrokeWidths[StrokeWidthChoice.SelectedIndex], 12);
        SavePreferences();
    }
}
