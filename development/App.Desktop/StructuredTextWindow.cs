using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class StructuredTextWindow : Window
{
    readonly DocumentSession session;
    readonly AdvancedEngineLocation engine;
    readonly int page;
    JsonElement? preselected;
    readonly ListBox runs = new() { MinHeight = 150, MaxHeight = 240, DisplayMemberPath = "Label" };
    readonly TextBox replacement = new() { MinHeight = 70, MaxHeight = 140, MaxLength = 4000, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly CheckBox wider = new() { Content = "Allow replacement wider than the original run", MinHeight = 44 };
    readonly CheckBox invisible = new() { Content = "Include invisible text (such as OCR text)", MinHeight = 44 };
    readonly CheckBox reviewed = new() { Content = "I reviewed the actual candidate page", MinHeight = 44 };
    readonly Button next = new() { Content = "Next text runs", MinHeight = 44 };
    readonly Button first = new() { Content = "First text runs", MinHeight = 44 };
    readonly Button prepare = new() { Content = "Preview replacement", MinHeight = 44 };
    readonly Button apply = new() { Content = "Apply reviewed replacement", MinHeight = 44 };
    readonly Button cancel = new() { Content = "Close", MinHeight = 44 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    readonly Image before = new() { Stretch = Stretch.Uniform };
    readonly Image after = new() { Stretch = Stretch.Uniform };
    readonly Slider zoom = new() { Minimum = 1, Maximum = 3, Value = 1, TickFrequency = .5, IsSnapToTickEnabled = true, MinHeight = 44 };
    StructuredTextPreview? preview;
    CancellationTokenSource? operation;
    long revision;
    string sourceHash = "";
    int? cursor = 0;
    int renderWidth = 1000, renderHeight = 1000;
    bool working, closeRequested;
    internal StructuredTextWindow(DocumentSession session, int page, AdvancedEngineLocation engine, JsonElement? preselected = null)
    {
        this.session = session; this.page = page; this.engine = engine; this.preselected = preselected?.Clone();
        Title = $"Mapped existing text — page {page + 1}"; Width = 1000; Height = 850; MinWidth = 640; MinHeight = 600; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status); footer.Children.Add(apply); footer.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "Edit text with its existing font", FontSize = 24, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Includes supported nested text and embedded fonts. Each item is a PDF text run, which may be part of a word or line. Keeps the existing font and following text positions; does not reflow paragraphs. Missing glyphs, tagged text and signed/encrypted documents are refused. Editing or deleting text is not permanent redaction.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        body.Children.Add(invisible); body.Children.Add(runs); body.Children.Add(next); body.Children.Add(first);
        body.Children.Add(new TextBlock { Text = "Replacement (empty text deletes this run)", Margin = new Thickness(0, 12, 0, 6) });
        body.Children.Add(replacement); body.Children.Add(wider); body.Children.Add(prepare);
        body.Children.Add(new TextBlock { Text = "Preview zoom · scroll within each page to inspect details", Margin = new Thickness(0, 12, 0, 0) });
        body.Children.Add(zoom);
        var images = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        images.ColumnDefinitions.Add(new ColumnDefinition()); images.ColumnDefinitions.Add(new ColumnDefinition());
        var original = new StackPanel(); original.Children.Add(new TextBlock { Text = "Current page" }); original.Children.Add(PageViewport(before));
        var candidate = new StackPanel(); candidate.Children.Add(new TextBlock { Text = "Actual candidate page" }); candidate.Children.Add(PageViewport(after));
        Grid.SetColumn(candidate, 1); images.Children.Add(original); images.Children.Add(candidate); body.Children.Add(images); body.Children.Add(reviewed);
        foreach (var text in body.Children.OfType<TextBlock>()) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var check in new[] { wider, invisible, reviewed }) check.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(runs, "Mapped existing PDF text runs");
        System.Windows.Automation.AutomationProperties.SetName(replacement, "Mapped text replacement");
        System.Windows.Automation.AutomationProperties.SetName(zoom, "Mapped text preview zoom");
        zoom.ValueChanged += (_, _) => ZoomPages();
        runs.SelectionChanged += (_, _) => { Invalidate(); if (runs.SelectedItem is Run item) { replacement.Text = item.Text; status.Text = item.CanEdit ? "Edit this run, then prepare and review the candidate." : item.Reason; } Update(); };
        replacement.TextChanged += (_, _) => Invalidate(); wider.Click += (_, _) => Invalidate();
        invisible.Click += async (_, _) => { cursor = 0; runs.ItemsSource = null; Invalidate(); await LoadAsync(); };
        reviewed.Click += (_, _) => Update(); next.Click += async (_, _) => await LoadAsync();
        first.Click += async (_, _) => { cursor = 0; await LoadAsync(); };
        prepare.Click += async (_, _) => await PrepareAsync(); apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => { if (working) { closeRequested = true; operation?.Cancel(); } else Close(); };
        Loaded += async (_, _) => { if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } revision = session.Revision; await LoadAsync(); };
        Closing += (_, e) => { if (working) { e.Cancel = true; closeRequested = true; operation?.Cancel(); } };
        Closed += (_, _) => { preview?.Dispose(); operation?.Dispose(); };
        Update();
    }
    sealed record Run(JsonElement Selection, string Text, bool CanEdit, string Reason, string Label);
    static ScrollViewer PageViewport(Image image) => new() { Content = image, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8) };
    void ZoomPages() { before.Width = after.Width = Math.Max(1, 420d * renderWidth / renderHeight * zoom.Value); }
    void Invalidate() { preview?.Dispose(); preview = null; after.Source = null; reviewed.IsChecked = false; Update(); }
    void Update() { prepare.IsEnabled = !working && runs.SelectedItem is Run { CanEdit: true }; apply.IsEnabled = !working && preview is not null && reviewed.IsChecked == true; reviewed.IsEnabled = !working && preview is not null; next.IsEnabled = !working && cursor is not null; first.IsEnabled = !working; }
    void Begin() { operation?.Dispose(); operation = new CancellationTokenSource(); working = true; runs.IsEnabled = replacement.IsEnabled = wider.IsEnabled = invisible.IsEnabled = false; cancel.Content = "Cancel operation"; Update(); }
    void End() { working = false; runs.IsEnabled = replacement.IsEnabled = wider.IsEnabled = invisible.IsEnabled = true; cancel.Content = "Close"; Update(); if (closeRequested && IsVisible) Close(); }
    async Task LoadAsync()
    {
        if (working || cursor is null) return;
        Invalidate(); Begin(); status.Text = "Inspecting mapped text runs…";
        try
        {
            do
            {
                var requestedCursor = cursor.Value;
                var data = await session.InspectStructuredTextAsync(engine, page, requestedCursor, invisible.IsChecked == true, revision, operation!.Token);
                sourceHash = data.GetProperty("SourceSHA256").GetString()!;
                var pageRuns = data.GetProperty("Entries").EnumerateArray().Select(item => new Run(item.Clone(), item.GetProperty("Text").GetString()!, item.GetProperty("CanEdit").GetBoolean(), item.GetProperty("Reason").GetString()!,
                    $"{item.GetProperty("Text").GetString()}  [{item.GetProperty("FontName").GetString()}, depth {item.GetProperty("FormPath").GetArrayLength()}]" )).ToArray();
                runs.ItemsSource = pageRuns;
                cursor = data.GetProperty("NextCursor").ValueKind == JsonValueKind.Null ? null : data.GetProperty("NextCursor").GetInt32();
                if (preselected is not JsonElement target) break;
                if (!string.Equals(target.GetProperty("SourceSHA256").GetString(), sourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The clicked text selection is stale. Inspect the page again.");
                var targetPath = target.GetProperty("FormPath").GetRawText();
                var targetIndex = target.GetProperty("TextIndex").GetInt32();
                var match = pageRuns.FirstOrDefault(run => run.Selection.GetProperty("FormPath").GetRawText() == targetPath && run.Selection.GetProperty("TextIndex").GetInt32() == targetIndex);
                if (match is not null)
                {
                    runs.SelectedItem = match;
                    preselected = null;
                    break;
                }
                if (cursor is null || cursor <= requestedCursor)
                    throw new InvalidOperationException("The clicked text run is no longer available. Inspect the page again.");
            } while (preselected is not null);
            if (before.Source is null)
            {
                var info = await session.ExtractAsync(page, operation.Token);
                var width = info.GetProperty("Width").GetDouble(); var height = info.GetProperty("Height").GetDouble();
                var scale = 1000 / Math.Max(width, height);
                renderWidth = Math.Max(1, (int)Math.Round(width * scale)); renderHeight = Math.Max(1, (int)Math.Round(height * scale));
                ZoomPages();
                before.Source = Bitmap(await session.RenderAsync(page, renderWidth, renderHeight, operation.Token));
            }
            status.Text = runs.Items.Count == 0 ? "No text runs were found." : "Select a text run. Use Next text runs to continue through the page.";
        }
        catch (OperationCanceledException) { status.Text = "Inspection cancelled."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { End(); }
    }
    async Task PrepareAsync()
    {
        if (working || runs.SelectedItem is not Run { CanEdit: true } item) return;
        Invalidate(); Begin(); status.Text = "Validating the mapped text candidate…";
        try
        {
            var selection = JsonSerializer.SerializeToElement(new { SourceSHA256 = sourceHash, FormPath = item.Selection.GetProperty("FormPath"), TextIndex = item.Selection.GetProperty("TextIndex"), Text = item.Text });
            preview = await session.PreviewStructuredTextAsync(engine, page, selection, replacement.Text, wider.IsChecked == true, invisible.IsChecked == true, renderWidth, renderHeight, revision, operation!.Token);
            after.Source = Bitmap(preview.Render); status.Text = "Review the actual page for placement and overlap, then confirm review to apply. Undo restores the committed edit.";
        }
        catch (OperationCanceledException) { status.Text = "Preview cancelled; the document is unchanged."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { End(); }
    }
    async Task ApplyAsync()
    {
        if (working || preview is null || reviewed.IsChecked != true) return;
        Begin();
        try { await session.ApplyStructuredTextPreviewAsync(preview, operation!.Token); working = false; DialogResult = true; }
        catch (OperationCanceledException) { status.Text = "Applying was cancelled."; Invalidate(); }
        catch (Exception error) { status.Text = error.Message; Invalidate(); }
        finally { End(); }
    }
    static BitmapSource Bitmap(JsonElement data)
    {
        var bitmap = BitmapSource.Create(data.GetProperty("Width").GetInt32(), data.GetProperty("Height").GetInt32(), 96, 96, PixelFormats.Bgr32, null,
            Convert.FromBase64String(data.GetProperty("Bgra").GetString()!), data.GetProperty("Stride").GetInt32()); bitmap.Freeze(); return bitmap;
    }
}
