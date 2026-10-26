using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace HunterPdf.Desktop;

internal sealed class RedactionWindow : Window
{
    readonly DocumentSession session;
    readonly AdvancedEngineLocation engine;
    readonly int page;
    readonly TextBox left = Box("72"), top = Box("95"), right = Box("350"), bottom = Box("130");
    readonly Image sourceImage = new(), candidateImage = new();
    readonly Grid sourceCanvas = new();
    readonly Canvas overlay = new() { IsHitTestVisible = false };
    readonly Rectangle selection = new() { StrokeThickness = 2, Fill = Brushes.Transparent };
    readonly Button prepare = new() { Content = "Preview permanent redaction", MinHeight = 44, IsEnabled = false };
    readonly Button publish = new() { Content = "Create new redacted PDF…", MinHeight = 44, IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
    readonly CheckBox reviewed = new() { Content = "I reviewed the result and the content removed beyond the rectangle.", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    CancellationTokenSource? operation;
    RedactionPreview? preview;
    bool working, loaded, closeRequested;
    long revision;
    double pageWidth, pageHeight;
    int renderWidth, renderHeight;
    internal RedactedCopyResult? Result { get; private set; }

    internal RedactionWindow(DocumentSession session, int page, AdvancedEngineLocation engine)
    {
        this.session = session; this.page = page; this.engine = engine;
        Title = $"Permanent redaction — page {page + 1}"; Width = 1020; Height = 900; MinWidth = 760; MinHeight = 620; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(reviewed); footer.Children.Add(prepare); footer.Children.Add(publish); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Text(string value, bool heading = false)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 24 : 16, Margin = new Thickness(0, 0, 0, 12) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); body.Children.Add(text);
        }
        Text($"Review permanent redaction · page {page + 1}", true);
        Text("Creates a new PDF with selected text and image pixels removed. A vector path touching the rectangle is removed in full, which can affect content outside it. Unrelated text and vectors remain editable; pages are not flattened. Standard document metadata and XMP are removed.");
        Text("Supports standard Latin PDF fonts on unrotated pages with matching crop/media boxes. Forms, signatures, bookmarks, tags, layers, shared images and other complex structures are refused. Original PDFs, open-document history and recovery copies remain intact; this does not erase those files.");
        Text("Rectangle in PDF points from the page’s top-left (72 points = 1 inch). Preview before exporting.");
        var coordinates = new Grid(); for (var i = 0; i < 4; i++) coordinates.ColumnDefinitions.Add(new ColumnDefinition());
        var labels = new[] { "Left", "Top", "Right", "Bottom" }; var boxes = new[] { left, top, right, bottom };
        for (var i = 0; i < boxes.Length; i++)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 10, 12) };
            var label = new TextBlock { Text = labels[i] }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); column.Children.Add(label); column.Children.Add(boxes[i]);
            Grid.SetColumn(column, i); coordinates.Children.Add(column); AutomationProperties.SetName(boxes[i], "Redaction " + labels[i].ToLowerInvariant() + " in PDF points from top-left");
            boxes[i].TextChanged += (_, _) => InvalidatePreview();
        }
        body.Children.Add(coordinates);
        var comparison = new Grid(); comparison.ColumnDefinitions.Add(new ColumnDefinition()); comparison.ColumnDefinitions.Add(new ColumnDefinition());
        var original = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; var proposed = new StackPanel();
        var originalLabel = new TextBlock { Text = "Current page · rectangle outline", Margin = new Thickness(0, 0, 0, 8) };
        var candidateLabel = new TextBlock { Text = "Actual redacted result", Margin = new Thickness(0, 0, 0, 8) };
        originalLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); candidateLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        original.Children.Add(originalLabel); proposed.Children.Add(candidateLabel);
        sourceCanvas.Children.Add(sourceImage); sourceCanvas.Children.Add(overlay); overlay.Children.Add(selection); selection.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FocusBrush");
        original.Children.Add(sourceCanvas); proposed.Children.Add(candidateImage); Grid.SetColumn(proposed, 1); comparison.Children.Add(original); comparison.Children.Add(proposed); body.Children.Add(comparison);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); reviewed.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var reviewLabel = new TextBlock { Text = "I reviewed the result and the content removed beyond the rectangle.", TextWrapping = TextWrapping.Wrap };
        reviewLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); reviewed.Content = reviewLabel;
        AutomationProperties.SetName(prepare, "Prepare a permanent redaction preview"); AutomationProperties.SetName(publish, "Export the reviewed permanent redaction to a new PDF");
        AutomationProperties.SetName(candidateImage, "Actual PDFium-rendered redaction candidate"); AutomationProperties.SetName(reviewed, "Confirm review of the redaction result");
        prepare.Click += async (_, _) => await PrepareAsync(); publish.Click += async (_, _) => await PublishAsync();
        reviewed.Checked += (_, _) => UpdateButtons(); reviewed.Unchecked += (_, _) => UpdateButtons();
        cancel.Click += (_, _) => { if (working) { closeRequested = true; operation?.Cancel(); } else DialogResult = false; };
        Loaded += async (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
            Begin(); status.Text = "Reading the current page…";
            try
            {
                var info = await session.ExtractAsync(page, operation!.Token); revision = session.Revision;
                if (info.GetProperty("Rotation").GetInt32() != 0) throw new InvalidOperationException("This redaction workflow requires an unrotated page.");
                pageWidth = info.GetProperty("Width").GetDouble(); pageHeight = info.GetProperty("Height").GetDouble();
                var scale = Math.Min(420 / pageWidth, 580 / pageHeight);
                renderWidth = Math.Max(1, (int)Math.Round(pageWidth * scale)); renderHeight = Math.Max(1, (int)Math.Round(pageHeight * scale));
                sourceCanvas.Width = candidateImage.Width = renderWidth; sourceCanvas.Height = candidateImage.Height = renderHeight;
                sourceImage.Source = Bitmap(await session.RenderAsync(page, renderWidth, renderHeight, operation.Token));
                loaded = true; Outline(); status.Text = "Enter the rectangle, then prepare and review the actual redacted result.";
            }
            catch (OperationCanceledException) { status.Text = "Loading was cancelled."; }
            catch (Exception error) { status.Text = error.Message; }
            finally { End(); }
        };
        Closing += (_, args) => { if (working) { args.Cancel = true; closeRequested = true; operation?.Cancel(); } };
        Closed += (_, _) => { operation?.Dispose(); preview?.Dispose(); };
    }
    static TextBox Box(string value) => new() { Text = value, MinHeight = 44 };
    static BitmapSource Bitmap(JsonElement data)
    {
        var bitmap = BitmapSource.Create(data.GetProperty("Width").GetInt32(), data.GetProperty("Height").GetInt32(), 96, 96, PixelFormats.Bgr32,
            null, Convert.FromBase64String(data.GetProperty("Bgra").GetString()!), data.GetProperty("Stride").GetInt32()); bitmap.Freeze(); return bitmap;
    }
    RedactionRegion Region()
    {
        var values = new[] { left, top, right, bottom }.Select(b => double.TryParse(b.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
            ? value : throw new InvalidOperationException("Enter finite rectangle coordinates in PDF points.")).ToArray();
        if (values[0] < 0 || values[1] < 0 || values[2] > pageWidth || values[3] > pageHeight || values[2] <= values[0] || values[3] <= values[1])
            throw new InvalidOperationException("The rectangle must be nonempty and inside this page.");
        return new RedactionRegion(page, values[0], values[1], values[2], values[3]);
    }
    void Outline()
    {
        try { var r = Region(); Canvas.SetLeft(selection, r.Left / pageWidth * renderWidth); Canvas.SetTop(selection, r.Top / pageHeight * renderHeight);
            selection.Width = (r.Right - r.Left) / pageWidth * renderWidth; selection.Height = (r.Bottom - r.Top) / pageHeight * renderHeight; selection.Visibility = Visibility.Visible; }
        catch (InvalidOperationException) { selection.Visibility = Visibility.Collapsed; }
    }
    void InvalidatePreview() { preview?.Dispose(); preview = null; candidateImage.Source = null; reviewed.IsChecked = false; if (loaded) Outline(); UpdateButtons(); }
    void UpdateButtons() { prepare.IsEnabled = loaded && !working; publish.IsEnabled = !working && preview is not null && reviewed.IsChecked == true; reviewed.IsEnabled = !working && preview is not null; }
    void Begin() { operation?.Dispose(); operation = new CancellationTokenSource(); working = true; foreach (var box in new[] { left, top, right, bottom }) box.IsEnabled = false; UpdateButtons(); cancel.Content = "Cancel operation"; }
    void End() { working = false; foreach (var box in new[] { left, top, right, bottom }) box.IsEnabled = true; UpdateButtons(); cancel.Content = "Close"; if (closeRequested && IsVisible) DialogResult = false; }
    async Task PrepareAsync()
    {
        if (working || !loaded) return;
        InvalidatePreview(); Begin(); status.Text = "Removing selected content and validating the private preview…";
        try { var region = Region(); preview = await session.PreviewRedactionAsync(engine, [region], page, renderWidth, renderHeight, revision, operation!.Token);
            candidateImage.Source = Bitmap(preview.Render); status.Text = "Review the actual result, including whole vector paths removed outside the rectangle. Confirm review to enable export."; }
        catch (OperationCanceledException) { status.Text = "Preview was cancelled. No output was published."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { End(); }
    }
    async Task PublishAsync()
    {
        if (working || preview is null || reviewed.IsChecked != true) return;
        var dialog = new SaveFileDialog { Filter = "PDF files (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(session.LastSavePath) + "-redacted.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        await PublishToAsync(dialog.FileName);
    }
    internal async Task PublishToAsync(string destination)
    {
        if (working || preview is null || reviewed.IsChecked != true) return;
        Begin(); status.Text = "Publishing the exact reviewed PDF to a new file…";
        try { Result = await session.PublishRedactedCopyAsync(preview, destination, operation!.Token); working = false; DialogResult = true; }
        catch (OperationCanceledException) { status.Text = "Export was cancelled before publication."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { End(); }
    }
}
