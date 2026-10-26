using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace HunterPdf.Desktop;

/// <summary>Captures a visual (image) signature or initials by drawing, typing or importing an image. This is not a digital signature.</summary>
internal sealed class SignatureCaptureWindow : Window
{
    internal const string HeaderText = "Visual signature (not a digital signature)";
    internal const string ExplanationText = "This is an image placed on the page; it does not cryptographically sign the document.";
    const double CanvasWidth = 640, CanvasHeight = 220, InkScale = 3;
    static readonly string[] ScriptFonts = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Freestyle Script", "Lucida Calligraphy", "Monotype Corsiva", "Mistral"];

    internal sealed record FontChoice(string Display, string Family, FontStyle Style)
    { public override string ToString() => Display; }

    readonly SignatureStore? store;
    readonly TabControl tabs = new() { MinHeight = 320 };
    readonly InkCanvas ink = new() { Width = CanvasWidth, Height = CanvasHeight, Background = Brushes.White, EditingMode = InkCanvasEditingMode.Ink, UseCustomCursor = false };
    readonly Slider thickness = new() { Minimum = 1, Maximum = 8, Value = 2.5, MinHeight = 44, MinWidth = 160, TickFrequency = 0.5, IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    readonly Button clear = new() { Content = "Clear", MinHeight = 44, MinWidth = 96, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBox typed = new() { MinHeight = 44, MaxLength = 40, FontSize = 20 };
    readonly ComboBox fonts = new() { MinHeight = 44 };
    readonly Image typePreview = new() { Stretch = Stretch.Uniform, Height = 150 };
    readonly TextBox imagePath = new() { IsReadOnly = true, MinHeight = 44 };
    readonly Button browse = new() { Content = "Choose image…", MinHeight = 44, MinWidth = 140 };
    readonly CheckBox removeWhite = new() { Content = "Remove white background", IsChecked = true, MinHeight = 32, Margin = new Thickness(0, 8, 0, 8) };
    readonly Image imagePreview = new() { Stretch = Stretch.Uniform, Height = 150 };
    readonly ComboBox kind = new() { MinHeight = 44, MinWidth = 160 };
    readonly ComboBox color = new() { MinHeight = 44, MinWidth = 140 };
    readonly CheckBox save = new() { Content = "Save for later", IsChecked = true, MinHeight = 32 };
    readonly Button ok = new() { Content = "Use signature", MinHeight = 44, IsDefault = true };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    DecodedImage? imported;

    internal SignatureImage? Result { get; private set; }
    internal SavedSignature? Saved { get; private set; }
    internal bool SaveForLater { get => save.IsChecked == true; set => save.IsChecked = value; }

    internal SignatureCaptureWindow(SignatureStore? store = null, SignatureKind initialKind = SignatureKind.Signature)
    {
        this.store = store;
        Title = "Add a visual signature"; Width = 760; Height = 860; MinWidth = 560; MinHeight = 640; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(ok); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        TextBlock Text(string text, double size = 16, string? name = null)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = size };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); if (name is not null) AutomationProperties.SetName(label, name);
            return label;
        }
        body.Children.Add(Text(HeaderText, 24, "Signature dialog heading"));
        body.Children.Add(Text(ExplanationText, 16, "Signature dialog explanation"));

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        void Option(string caption, Control control)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var label = new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(label); stack.Children.Add(control); options.Children.Add(stack);
        }
        kind.Items.Add("Signature"); kind.Items.Add("Initials"); kind.SelectedIndex = initialKind == SignatureKind.Initials ? 1 : 0;
        color.Items.Add("Black"); color.Items.Add("Dark blue"); color.SelectedIndex = 0;
        Option("Kind", kind); Option("Ink color", color); body.Children.Add(options);

        tabs.Items.Add(new TabItem { Header = "Draw", Content = DrawTab(), MinHeight = 40 });
        tabs.Items.Add(new TabItem { Header = "Type", Content = TypeTab(), MinHeight = 40 });
        tabs.Items.Add(new TabItem { Header = "Image", Content = ImageTab(), MinHeight = 40 });
        body.Children.Add(tabs); save.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(save);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        AutomationProperties.SetName(tabs, "Signature source"); AutomationProperties.SetName(ink, "Signature drawing area");
        AutomationProperties.SetName(thickness, "Stroke thickness"); AutomationProperties.SetName(clear, "Clear drawing");
        AutomationProperties.SetName(typed, "Typed signature text"); AutomationProperties.SetName(fonts, "Signature font");
        AutomationProperties.SetName(typePreview, "Typed signature preview"); AutomationProperties.SetName(imagePath, "Imported signature image file");
        AutomationProperties.SetName(browse, "Choose signature image file"); AutomationProperties.SetName(removeWhite, "Remove white background");
        AutomationProperties.SetName(imagePreview, "Imported signature preview"); AutomationProperties.SetName(kind, "Signature or initials");
        AutomationProperties.SetName(color, "Ink color"); AutomationProperties.SetName(save, "Save for later");
        AutomationProperties.SetName(ok, "Use this signature"); AutomationProperties.SetName(cancel, "Cancel signature");
        AutomationProperties.SetName(status, "Signature dialog status");

        SetInkAttributes();
        thickness.ValueChanged += (_, _) => { SetInkAttributes(); };
        color.SelectionChanged += (_, _) => { SetInkAttributes(); foreach (var stroke in ink.Strokes) stroke.DrawingAttributes.Color = InkColor(); UpdateTypePreview(); };
        clear.Click += (_, _) => { ink.Strokes.Clear(); status.Text = ""; };
        typed.TextChanged += (_, _) => UpdateTypePreview();
        fonts.SelectionChanged += (_, _) => UpdateTypePreview();
        kind.SelectionChanged += (_, _) => status.Text = "";
        removeWhite.Click += (_, _) => UpdateImagePreview();
        browse.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName);
        };
        ok.Click += (_, _) => Accept();
        Loaded += (_, _) => { if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } };
    }

    UIElement DrawTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Draw with a pen, finger or mouse in the white box.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = ink, HorizontalAlignment = HorizontalAlignment.Left });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(clear); row.Children.Add(new TextBlock { Text = "Thickness", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); row.Children.Add(thickness);
        panel.Children.Add(row); return panel;
    }

    UIElement TypeTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Name or initials", Margin = new Thickness(0, 0, 0, 4) }); panel.Children.Add(typed);
        panel.Children.Add(new TextBlock { Text = "Style", Margin = new Thickness(0, 12, 0, 4) }); panel.Children.Add(fonts);
        foreach (var choice in InstalledChoices()) fonts.Items.Add(choice);
        fonts.SelectedIndex = 0;
        panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0), Child = typePreview });
        return panel;
    }

    UIElement ImageTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Import a PNG or JPEG of your signature on a plain background.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(imagePath); panel.Children.Add(browse); panel.Children.Add(removeWhite);
        panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = imagePreview });
        return panel;
    }

    /// <summary>Script-style fonts that exist on this machine, always ending with Segoe UI Italic.</summary>
    internal static List<FontChoice> InstalledChoices()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies) installed.Add(family.Source);
        var list = ScriptFonts.Where(installed.Contains).Select(n => new FontChoice(n, n, FontStyles.Normal)).ToList();
        list.Add(new FontChoice("Segoe UI Italic", "Segoe UI", FontStyles.Italic)); return list;
    }

    Color InkColor() => color.SelectedIndex == 1 ? StampImages.DarkBlue : StampImages.Black;
    void SetInkAttributes() => ink.DefaultDrawingAttributes = new DrawingAttributes { Color = InkColor(), Width = thickness.Value, Height = thickness.Value, FitToCurve = true, IgnorePressure = false };
    SignatureKind SelectedKind => kind.SelectedIndex == 1 ? SignatureKind.Initials : SignatureKind.Signature;

    // ----- test and shell hooks -----
    internal InkCanvas InkSurface => ink;
    internal TabControl Tabs => tabs;
    internal TextBox TypedText => typed;
    internal ComboBox FontPicker => fonts;
    internal Slider StrokeThickness => thickness;
    internal CheckBox RemoveWhiteOption => removeWhite;
    internal ComboBox KindPicker => kind;
    internal ComboBox ColorPicker => color;
    internal Button OkButton => ok;
    internal string StatusText => status.Text;
    internal Image TypePreviewImage => typePreview;
    internal Image ImagePreviewImage => imagePreview;

    internal async Task ImportAsync(string path)
    {
        try
        {
            imported = await WicImageDecoder.DecodeAsync(path); imagePath.Text = Path.GetFileName(path); status.Text = "";
            UpdateImagePreview();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { imported = null; imagePath.Text = ""; imagePreview.Source = null; status.Text = error is InvalidOperationException ? error.Message : "That image could not be read. Choose a PNG or JPEG file."; }
    }

    void UpdateTypePreview() { var image = TryBuildType(); typePreview.Source = image is null ? null : StampImages.ToBitmap(image.Bgra, image.Width, image.Height); }
    void UpdateImagePreview() { var image = BuildImage(); imagePreview.Source = image is null ? null : StampImages.ToBitmap(image.Bgra, image.Width, image.Height); }

    SignatureImage? BuildDraw()
    {
        if (ink.Strokes.Count == 0) return null;
        var brush = new SolidColorBrush(InkColor()); brush.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(InkScale, InkScale));
            foreach (var stroke in ink.Strokes) dc.DrawGeometry(brush, null, stroke.GetGeometry());
            dc.Pop();
        }
        int w = (int)(CanvasWidth * InkScale), h = (int)(CanvasHeight * InkScale);
        return Finish(StampImages.Render(visual, w, h), w, h);
    }

    SignatureImage? TryBuildType()
    {
        var text = typed.Text.Trim(); if (text.Length == 0 || fonts.SelectedItem is not FontChoice choice) return null;
        var brush = new SolidColorBrush(InkColor()); brush.Freeze();
        var face = new Typeface(new FontFamily(choice.Family), choice.Style, FontWeights.Normal, FontStretches.Normal);
        double size = 140;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
        if (formatted.WidthIncludingTrailingWhitespace > 3900) { size = size * 3900 / formatted.WidthIncludingTrailingWhitespace; formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0); }
        int w = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 60, h = (int)Math.Ceiling(formatted.Height) + 60;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(formatted, new Point(30, 30));
        return Finish(StampImages.Render(visual, w, h), w, h);
    }

    SignatureImage? BuildImage()
    {
        if (imported is null) return null;
        var pixels = removeWhite.IsChecked == true ? StampImages.RemoveWhite(imported.Bgra) : imported.Bgra;
        return Finish(pixels, imported.Width, imported.Height);
    }

    SignatureImage? Finish(byte[] bgra, int w, int h) { var trimmed = StampImages.Trim(bgra, w, h, SelectedKind); return trimmed is null ? null : StampImages.Fit(trimmed); }

    /// <summary>Builds the image for the active source tab, or null when it has no visible ink.</summary>
    internal SignatureImage? BuildCurrent() => tabs.SelectedIndex switch { 0 => BuildDraw(), 1 => TryBuildType(), _ => BuildImage() };

    void Accept()
    {
        var image = BuildCurrent();
        if (image is null)
        {
            status.Text = tabs.SelectedIndex switch { 0 => "Draw your signature in the white box first.", 1 => "Type your name or initials first.", _ => "Choose an image with visible ink first. If everything disappeared, turn off Remove white background." };
            return;
        }
        Result = image;
        if (store is not null && SaveForLater)
        {
            try { Saved = store.Save(image); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Saved = null; }
        }
        DialogResult = true;
    }
}
