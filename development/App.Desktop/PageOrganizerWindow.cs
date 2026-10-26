using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

public sealed class PageOrganizerWindow : Window
{
    readonly DocumentSession session;
    readonly ObservableCollection<PageRow> pages = new();
    readonly ListBox list = new() { MinHeight = 180, SelectionMode = SelectionMode.Extended };
    readonly Image preview = new() { Stretch = System.Windows.Media.Stretch.Uniform, MaxWidth = 260, MaxHeight = 320, Margin = new Thickness(12) };
    readonly TextBlock previewStatus = new() { Text = "Select a page to preview it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    readonly Button moveUp;
    readonly Button moveDown;
    readonly Button insertBefore;
    readonly Button insertAfter;
    readonly Button duplicateAfter;
    readonly Button importAfter;
    readonly Button deletePage;
    readonly Button extractPages;
    readonly Button splitPages;
    readonly TextBox importRange = new() { Width = 220, MinHeight = 44, Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
    bool busy;
    CancellationTokenSource? previewCancellation;
    long previewGeneration;

    public int SelectedPage { get; private set; }

    public PageOrganizerWindow(DocumentSession session, int currentPage = 0)
    {
        this.session = session;
        SelectedPage = Math.Max(0, currentPage);
        Title = "Organize pages"; Width = 760; Height = 620; MinWidth = 600; MinHeight = 460;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 16;
        Loaded += async (_, _) =>
        {
            if (Owner is not null) Resources.MergedDictionaries.Add(Owner.Resources);
            await RefreshAsync(SelectedPage);
        };
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/App.Desktop;component/Assets/PdfEditor-256.png"));

        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Organize pages", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "Select up to 5,000 pages to extract them in document order into one new PDF. Split the whole document into one PDF per page in a new folder. These exports preserve the source. Page edits below apply to the first selected page and can be reversed with Undo in the main window.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12)
        });

        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        Button Action(string name, string automationName, RoutedEventHandler handler)
        {
            var button = new Button { Content = name, MinHeight = 44, MinWidth = 108, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
            AutomationProperties.SetName(button, automationName);
            button.Click += handler; actions.Children.Add(button); return button;
        }
        moveUp = Action("Move up", "Move selected page up", async (_, _) => await MoveAsync(-1));
        moveDown = Action("Move down", "Move selected page down", async (_, _) => await MoveAsync(1));
        insertBefore = Action("Insert blank before", "Insert a blank page before the selected page", async (_, _) => await InsertAsync(before: true));
        insertAfter = Action("Insert blank after", "Insert a blank page after the selected page", async (_, _) => await InsertAsync(before: false));
        duplicateAfter = Action("Duplicate page", "Duplicate the selected page after itself", async (_, _) => await DuplicateAsync());
        importAfter = Action("Import pages after", "Import selected pages from another PDF after the selected page", async (_, _) => await ImportAfterAsync());
        importAfter.MinWidth = 160;
        deletePage = Action("Delete page", "Delete the selected page", async (_, _) => await DeleteAsync());
        extractPages = Action("Extract selected pages…", "Extract selected pages to a new PDF", async (_, _) => await ExtractPagesAsync());
        extractPages.MinWidth = 190;
        splitPages = Action("Split document…", "Split the whole document into one PDF per page in a new folder", async (_, _) => await SplitPagesAsync());
        splitPages.MinWidth = 190;
        var importRangePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        importRangePanel.Children.Add(new TextBlock { Text = "Source pages", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        AutomationProperties.SetName(importRange, "Source PDF page range to import");
        AutomationProperties.SetHelpText(importRange, "Enter pages such as 1,3-5. Leave blank to import every page. Page numbers start at 1.");
        importRangePanel.Children.Add(importRange);
        importRangePanel.Children.Add(new TextBlock { Text = "Use 1,3-5; blank imports every page", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) });
        DockPanel.SetDock(importRangePanel, Dock.Top); root.Children.Add(importRangePanel);

        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(new TextBlock
        {
            Text = "Extraction creates one PDF containing selected pages in document order. Split creates page-0001.pdf, page-0002.pdf, and so on, in a new folder. Existing files and folders are never replaced; failed split output is removed. Exports leave the source unchanged. Select 1–5,000 pages for extraction; documents are limited to 50,000 pages. Page edits use the first selected page. Page labels are not changed. Unsupported tagged pages, form widgets, annotations, and encrypted files may be refused. The final page cannot be deleted.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        list.ItemsSource = pages; list.ItemTemplate = BuildPageTemplate();
        list.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(list, "Pages in current document order");
        AutomationProperties.SetHelpText(list, "Use Ctrl or Shift to select multiple pages for extraction. Page edit commands act on the first selected page.");
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is PageRow row) { SelectedPage = row.Index; _ = PreviewAsync(row); }
            UpdateActions();
        };
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(list);
        var previewPanel = new Border { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
        previewPanel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        var previewContent = new DockPanel(); previewPanel.Child = previewContent;
        AutomationProperties.SetName(preview, "Preview of the selected PDF page");
        DockPanel.SetDock(previewStatus, Dock.Bottom); previewContent.Children.Add(previewStatus); previewContent.Children.Add(preview);
        Grid.SetColumn(previewPanel, 1); content.Children.Add(previewPanel); root.Children.Add(content);
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            status.Text = "Wait for the page operation to finish before closing this window.";
        };
        Closed += (_, _) => { previewGeneration++; previewCancellation?.Cancel(); previewCancellation?.Dispose(); previewCancellation = null; };
    }

    DataTemplate BuildPageTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(PageRow.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MinHeightProperty, 44d);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 2, 8, 2));
        return new DataTemplate { VisualTree = text };
    }

    async Task RefreshAsync(int select)
    {
        previewGeneration++; previewCancellation?.Cancel();
        try
        {
            var info = await session.PageInfoAsync();
            var source = info.GetProperty("Pages").EnumerateArray().Select(item =>
            {
                var index = item.GetProperty("Page").GetInt32();
                var width = item.GetProperty("Width").GetDouble();
                var height = item.GetProperty("Height").GetDouble();
                var rotation = item.GetProperty("Rotation").GetInt32();
                return new PageRow(index, width, height, rotation);
            }).ToArray();
            pages.Clear(); foreach (var page in source) pages.Add(page);
            if (pages.Count > 0)
            {
                SelectedPage = Math.Clamp(select, 0, pages.Count - 1);
                list.SelectedIndex = SelectedPage;
                list.ScrollIntoView(list.SelectedItem);
            }
            status.Text = $"{pages.Count:N0} pages. Select a page, then choose an operation.";
            UpdateActions();
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    async Task PreviewAsync(PageRow row)
    {
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        var cancellation = previewCancellation = new CancellationTokenSource();
        var generation = ++previewGeneration;
        preview.Source = null;
        previewStatus.Text = $"Loading preview for page {row.Index + 1}…";
        try
        {
            await Task.Delay(100, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsVisible) return;
            var scale = Math.Min(260d / row.Width, 320d / row.Height);
            var width = Math.Clamp((int)Math.Round(row.Width * scale), 1, 320);
            var height = Math.Clamp((int)Math.Round(row.Height * scale), 1, 360);
            var rendered = await session.RenderAsync(row.Index, width, height);
            if (generation != previewGeneration || list.SelectedIndex != row.Index || !IsVisible) return;
            preview.Source = BitmapSource.Create(rendered.GetProperty("Width").GetInt32(), rendered.GetProperty("Height").GetInt32(), 96, 96,
                PixelFormats.Bgr32, null, Convert.FromBase64String(rendered.GetProperty("Bgra").GetString()!), rendered.GetProperty("Stride").GetInt32());
            previewStatus.Text = $"Page {row.Index + 1} preview";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested && generation == previewGeneration) previewStatus.Text = ex.Message; }
    }

    async Task MoveAsync(int offset)
    {
        var index = list.SelectedIndex;
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= pages.Count || busy) return;
        await RunOperationAsync(async () =>
        {
            await session.MovePagesAsync([index], destination);
            await RefreshAsync(destination);
        });
    }

    async Task InsertAsync(bool before)
    {
        var index = list.SelectedIndex;
        if (index < 0 || index >= pages.Count || busy) return;
        var selected = pages[index];
        var insertIndex = before ? index : index + 1;
        var keepSelected = before ? index + 1 : index;
        await RunOperationAsync(async () =>
        {
            await session.InsertBlankPageAsync(insertIndex, selected.Width, selected.Height);
            await RefreshAsync(keepSelected);
        });
    }

    async Task DeleteAsync()
    {
        var index = list.SelectedIndex;
        if (index < 0 || index >= pages.Count || pages.Count <= 1 || busy) return;
        await RunOperationAsync(async () =>
        {
            await session.DeletePagesAsync([index]);
            await RefreshAsync(Math.Min(index, pages.Count - 2));
        });
    }

    async Task DuplicateAsync()
    {
        var index = list.SelectedIndex;
        if (index < 0 || index >= pages.Count || busy) return;
        var destination = index + 1;
        await RunOperationAsync(async () =>
        {
            await session.DuplicatePagesAsync([index], destination);
            await RefreshAsync(destination);
        });
    }

    async Task ImportAfterAsync()
    {
        var index = list.SelectedIndex;
        if (index < 0 || index >= pages.Count || busy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf", Multiselect = false, Title = "Import pages from another PDF" };
        if (picker.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            using var worker = new WorkerClient(System.IO.Path.Combine(AppContext.BaseDirectory, "worker", "App.Worker.exe"));
            worker.Start();
            var inspected = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "InspectCombineInput", new { Path = picker.FileName }));
            if (!inspected.Success) throw new InvalidOperationException(inspected.Error);
            var count = inspected.Data.GetProperty("PageCount").GetInt32();
            if (count < 1) throw new InvalidOperationException("The source PDF has no pages to import.");
            if (!TryResolveImportPageRange(importRange.Text, count, out var selectedPages, out var rangeError))
                throw new InvalidOperationException(rangeError);
            if (selectedPages.Length > 5_000) throw new InvalidOperationException("Page import supports at most 5,000 selected pages from one PDF.");
            if ((long)session.PageCount + selectedPages.Length > 50_000)
                throw new InvalidOperationException("Page organization supports up to 50,000 resulting pages. Choose a smaller source page range.");
            status.Text = $"Importing {selectedPages.Length:N0} of {count:N0} source pages…";
            try { await session.InsertPagesAsync(picker.FileName, selectedPages, index + 1); }
            catch (InvalidOperationException refusal) when (DocumentSession.IsSourceStructureRefusal(refusal) && PageToolsEngine.Installed() is { } engine)
            {
                var choice = MessageBox.Show(this,
                    "This PDF has bookmarks, annotations, forms, tags, attachments or similar structures that cannot be copied exactly.\n\n" +
                    "Import only the page content instead? Form values and annotations are flattened into the pages; bookmarks, links, tags, labels, attachments and scripts are left out. " +
                    "The original file is not changed and you can undo the import.", "Import page content only", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) { status.Text = refusal.Message; return; }
                status.Text = "Preparing a plain copy of the selected pages…";
                var report = await session.InsertPagesBestEffortAsync(engine, picker.FileName, selectedPages, index + 1);
                await RefreshAsync(index + selectedPages.Length);
                status.Text = report.Describe();
                return;
            }
            await RefreshAsync(index + selectedPages.Length);
        });
    }

    async Task ExtractPagesAsync()
    {
        var selected = list.SelectedItems.Cast<PageRow>().Select(page => page.Index).Order().ToArray();
        if (selected.Length is < 1 or > 5_000 || busy) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PDF files|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = false, Title = $"Extract {selected.Length:N0} pages to a new PDF" };
        if (picker.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            var result = await session.ExtractPagesAsync(selected, picker.FileName);
            status.Text = $"Created a new PDF with {result.GetProperty("PageCount").GetInt32():N0} pages. The source document is unchanged.";
        });
    }

    async Task SplitPagesAsync()
    {
        if (pages.Count is < 1 or > 50_000 || busy) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Multiselect = false, Title = $"Choose the parent folder for {pages.Count:N0} page PDFs" };
        if (picker.ShowDialog(this) != true) return;
        var output = System.IO.Path.Combine(picker.FolderName, System.IO.Path.GetFileNameWithoutExtension(session.OriginalPath) + "-pages");
        await RunOperationAsync(async () =>
        {
            var result = await session.SplitPagesAsync(output);
            status.Text = $"Created {result.GetProperty("PageCount").GetInt32():N0} PDFs in {output}. The source document is unchanged.";
        });
    }

    async Task RunOperationAsync(Func<Task> operation)
    {
        busy = true; SetActions(false); status.Text = "Applying page operation and validating the checkpoint…";
        try { await operation(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; UpdateActions(); }
    }

    void SetActions(bool enabled)
    {
        moveUp.IsEnabled = enabled; moveDown.IsEnabled = enabled;
        insertBefore.IsEnabled = enabled; insertAfter.IsEnabled = enabled;
        duplicateAfter.IsEnabled = enabled;
        importAfter.IsEnabled = enabled;
        deletePage.IsEnabled = enabled;
        extractPages.IsEnabled = enabled; splitPages.IsEnabled = enabled;
        importRange.IsEnabled = enabled; list.IsEnabled = enabled;
    }

    void UpdateActions()
    {
        if (busy) { SetActions(false); return; }
        var index = list.SelectedIndex;
        moveUp.IsEnabled = index > 0;
        moveDown.IsEnabled = index >= 0 && index < pages.Count - 1;
        insertBefore.IsEnabled = index >= 0;
        insertAfter.IsEnabled = index >= 0;
        duplicateAfter.IsEnabled = index >= 0;
        importAfter.IsEnabled = index >= 0;
        deletePage.IsEnabled = index >= 0 && pages.Count > 1;
        extractPages.IsEnabled = list.SelectedItems.Count is >= 1 and <= 5_000;
        splitPages.IsEnabled = pages.Count is >= 1 and <= 50_000;
    }

    internal static bool TryResolveImportPageRange(string range, int pageCount, out int[] pages, out string error)
    {
        pages = [];
        error = "";
        if (pageCount < 1) { error = "The source PDF has no pages to import."; return false; }
        var selected = new List<int>();
        var seen = new HashSet<int>();
        var parts = string.IsNullOrWhiteSpace(range) ? new[] { $"1-{pageCount}" } : range.Split(',');
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) { error = "A page range is empty or malformed."; return false; }
            var bounds = part.Split('-');
            if (bounds.Length > 2 || !int.TryParse(bounds[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1)
            { error = "Use page numbers such as 1,3-5; page numbers start at 1."; return false; }
            var last = first;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
            { error = "Use ascending page ranges such as 2-5."; return false; }
            if (last > pageCount) { error = $"Page {last} is outside this PDF, which has {pageCount} pages."; return false; }
            if ((long)selected.Count + last - first + 1 > 5_000)
            { error = "Page import supports at most 5,000 selected pages from one PDF."; return false; }
            for (var page = first; page <= last; page++)
            {
                if (!seen.Add(page)) { error = $"Page {page} appears more than once in this range."; return false; }
                selected.Add(page - 1);
            }
        }
        pages = selected.ToArray();
        return true;
    }

    sealed class PageRow(int index, double width, double height, int rotation)
    {
        public int Index { get; } = index;
        public double Width { get; } = width;
        public double Height { get; } = height;
        public int Rotation { get; } = rotation;
        public string Label => string.Format(CultureInfo.CurrentCulture, "Page {0}  ·  {1:0.##} × {2:0.##} pt  ·  rotation {3}°", Index + 1, Width, Height, Rotation * 90);
    }
}



