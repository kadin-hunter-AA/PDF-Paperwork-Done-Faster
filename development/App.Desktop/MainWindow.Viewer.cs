using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>Viewer navigation, zoom, page layouts, the render cache and thumbnail page actions.</summary>
public partial class MainWindow
{
    static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 100, 125, 150, 175, 200, 250, 300, 400];
    // Rendered pages are kept per tab by decoded size, not page count; visible pages are never evicted.
    internal const long RenderCacheBytes = 320L * 1024 * 1024;
    const int PrefetchDistance = 2;
    const string PageDragFormat = "HunterPdf.ThumbnailPages";

    PageLayout pageLayout;
    int wheelDelta;
    readonly DispatcherTimer wheelZoom = new() { Interval = TimeSpan.FromMilliseconds(90) };
    Point? thumbnailDragStart;

    internal PageLayout CurrentLayout => pageLayout;

    void InitializeViewer()
    {
        pageLayout = preferences.Layout;
        Bind(ShellCommands.ZoomIn, () => SetZoomAsync(NextZoomStep(active?.Zoom ?? 100, 1)), () => active is not null && active.Zoom < 400);
        Bind(ShellCommands.ZoomOut, () => SetZoomAsync(NextZoomStep(active?.Zoom ?? 100, -1)), () => active is not null && active.Zoom > 25);
        Bind(ShellCommands.FitPage, () => SetZoomAsync(FitPageZoom()), () => active is { Pages.Count: > 0 });
        Bind(ShellCommands.FitWidth, () => SetZoomAsync(FitWidthZoom()), () => active is { Pages.Count: > 0 });
        Bind(ShellCommands.ActualSize, () => SetZoomAsync(100), () => active is not null);
        Bind(ShellCommands.RotateClockwise, () => RotateSelectionAsync(1), () => active is not null);
        Bind(ShellCommands.RotateCounterclockwise, () => RotateSelectionAsync(3), () => active is not null);
        CommandBindings.Add(new CommandBinding(ShellCommands.GoToPage,
            (_, e) => { PageNumberBox.Focus(); PageNumberBox.SelectAll(); e.Handled = true; },
            (_, e) => e.CanExecute = active is not null && !busy));
        CommandBindings.Add(new CommandBinding(ShellCommands.Print, (_, e) => Print_Click(this, e),
            (_, e) => e.CanExecute = active is not null && !busy));
        wheelZoom.Tick += (_, _) => ApplyWheelZoom();
        DocumentScroll.PreviewMouseWheel += DocumentScroll_PreviewMouseWheel;
        PreviewKeyDown += Viewer_PreviewKeyDown;
        LayoutChoice.ItemsSource = new[] { "Continuous", "Single page", "Two pages", "Two pages, cover" };
        LayoutChoice.SelectedIndex = (int)pageLayout;
        LayoutChoice.SelectionChanged += (_, _) => { if (LayoutChoice.SelectedIndex >= 0) SetPageLayout((PageLayout)LayoutChoice.SelectedIndex); };
        InitializeThumbnailActions();
        InitializePageContextMenu();
        InitializeToolStyles();
        InitializeHandTool();
        foreach (var (command, toggle) in new[] { (ShellCommands.ToggleNavigation, NavigationToggle), (ShellCommands.ToggleSidePanel, PropertiesToggle) })
            CommandBindings.Add(new CommandBinding(command,
                (_, e) => { toggle.IsChecked = toggle.IsChecked != true; Panels_Changed(toggle, e); e.Handled = true; },
                (_, e) => e.CanExecute = !busy));
        PreviewKeyDown += Find_PreviewKeyDown;
    }

    // ---- Zoom ------------------------------------------------------------------------------

    internal static int NextZoomStep(int zoom, int direction) => direction > 0
        ? ZoomSteps.FirstOrDefault(step => step > zoom, 400)
        : ZoomSteps.LastOrDefault(step => step < zoom, 25);

    int Columns => pageLayout is PageLayout.TwoPage or PageLayout.TwoPageCover ? 2 : 1;

    int FitWidthZoom()
    {
        if (active is not { Pages.Count: > 0 } tab) return 100;
        var page = tab.Pages[tab.CurrentPage];
        var available = DocumentScroll.ActualWidth - 72 - (Columns - 1) * 12;
        return (int)(available * 72 / 96 * 100 / (page.Width * Columns));
    }

    int FitPageZoom()
    {
        if (active is not { Pages.Count: > 0 } tab) return 100;
        var page = tab.Pages[tab.CurrentPage];
        var scale = Math.Min((DocumentScroll.ActualWidth - 72 - (Columns - 1) * 12) * 72 / 96 / (page.Width * Columns),
            (DocumentScroll.ActualHeight - 72) * 72 / 96 / page.Height);
        return (int)(scale * 100);
    }

    // Precision touchpads report pinch as Ctrl+wheel, so this also handles pinch-to-zoom.
    void DocumentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (active is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            QueueWheelZoom(e.Delta);
            return;
        }
        // Single-page layout turns the page when the wheel reaches the top or bottom of it.
        if (pageLayout != PageLayout.SinglePage) return;
        if (e.Delta < 0 && DocumentScroll.VerticalOffset >= DocumentScroll.ScrollableHeight - 1 && active.CurrentPage + 1 < active.Pages.Count)
        { e.Handled = true; GoToPage(active.CurrentPage + 1); }
        else if (e.Delta > 0 && DocumentScroll.VerticalOffset <= 0 && active.CurrentPage > 0)
        { e.Handled = true; GoToPage(active.CurrentPage - 1); }
    }

    /// <summary>Accumulates wheel notches briefly so a fast spin re-renders once.</summary>
    internal void QueueWheelZoom(int delta)
    {
        if (busy) return;
        wheelDelta += delta;
        wheelZoom.Stop(); wheelZoom.Start();
    }

    void ApplyWheelZoom()
    {
        wheelZoom.Stop();
        if (active is null || wheelDelta == 0 || busy) { wheelDelta = 0; return; }
        var notches = wheelDelta / 120d; wheelDelta = 0;
        var target = (int)Math.Round(active.Zoom * Math.Pow(1.1, notches));
        if (target == active.Zoom) target += Math.Sign(notches);
        target = Math.Clamp(target, 25, 400);
        if (target != active.Zoom) _ = RunUiAsync(() => SetZoomAsync(target), "Changing zoom…");
    }

    // ---- Keyboard ----------------------------------------------------------------------------

    void Viewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || active is not { } tab || busy) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (pendingStamp is not null) { CancelPlacement(); e.Handled = true; }
            else if (FindBar.Visibility == Visibility.Visible) { CloseFind_Click(this, e); e.Handled = true; }
            else if (IsEditMode && inlineEditor is null && dragLayer is null) { SetPageTool(PageTool.Select, false); e.Handled = true; }
            return;
        }
        if (ThumbnailList.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Delete && modifiers == ModifierKeys.None && ThumbnailList.SelectedItems.Count > 0)
            {
                e.Handled = true;
                _ = RunUiAsync(() => DeletePagesAsync(SelectedThumbnailPages()), "Deleting pages…");
            }
            return; // The thumbnail list keeps its own Home/End/arrow handling.
        }
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Control)) return;
        var step = Columns;
        int? target = e.Key switch
        {
            Key.Home => 0,
            Key.End => tab.Pages.Count - 1,
            Key.PageDown => tab.CurrentPage + step,
            Key.PageUp => tab.CurrentPage - step,
            Key.Right when pageLayout != PageLayout.Continuous && modifiers == ModifierKeys.None => tab.CurrentPage + step,
            Key.Left when pageLayout != PageLayout.Continuous && modifiers == ModifierKeys.None => tab.CurrentPage - step,
            _ => null
        };
        if (target is not int page) return;
        e.Handled = true;
        GoToPage(Math.Clamp(page, 0, tab.Pages.Count - 1));
    }

    // ---- Layouts -----------------------------------------------------------------------------

    internal void SetPageLayout(PageLayout layout)
    {
        if (layout == pageLayout) return;
        pageLayout = layout;
        preferences.Layout = layout;
        SavePreferences();
        if (LayoutChoice.SelectedIndex != (int)layout) LayoutChoice.SelectedIndex = (int)layout;
        if (active is null) return;
        var page = active.CurrentPage;
        ArrangePageCards();
        _ = Dispatcher.BeginInvoke(new Action(() => { if (active is not null) GoToPage(Math.Clamp(page, 0, active.Pages.Count - 1)); }), DispatcherPriority.Loaded);
    }

    /// <summary>Places the page cards for the current layout: one column, one page at a time, or side-by-side spreads.</summary>
    void ArrangePageCards()
    {
        foreach (var card in pageCards) if (card.Parent is Panel parent) parent.Children.Remove(card);
        PageStack.Children.Clear();
        var columns = Columns;
        for (var i = 0; i < pageCards.Count;)
        {
            var card = pageCards[i];
            card.Visibility = pageLayout == PageLayout.SinglePage && active is not null && i != active.CurrentPage ? Visibility.Collapsed : Visibility.Visible;
            if (columns == 1)
            {
                card.Margin = new Thickness(0, 0, 0, 16);
                card.HorizontalAlignment = HorizontalAlignment.Center;
                PageStack.Children.Add(card); i++;
                continue;
            }
            // Cover layout shows the first page alone on the right, then facing pairs.
            var count = pageLayout == PageLayout.TwoPageCover && i == 0 ? 1 : 2;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
            if (count == 1 && pageCards.Count > 1) row.HorizontalAlignment = HorizontalAlignment.Right;
            for (var j = 0; j < count && i < pageCards.Count; j++, i++)
            {
                pageCards[i].Margin = new Thickness(j == 0 ? 0 : 12, 0, 0, 0);
                pageCards[i].VerticalAlignment = VerticalAlignment.Top;
                row.Children.Add(pageCards[i]);
            }
            PageStack.Children.Add(row);
        }
    }

    void ShowSinglePage(int page)
    {
        for (var i = 0; i < pageCards.Count; i++) pageCards[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        PageStack.UpdateLayout();
    }

    // ---- Render cache ------------------------------------------------------------------------

    static long BitmapBytes(System.Windows.Media.Imaging.BitmapSource bitmap) => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

    void TrimRenderCache(OpenDocumentTab tab, int keep)
    {
        var total = tab.RenderCache.Values.Sum(BitmapBytes);
        if (total <= RenderCacheBytes && tab.RenderCache.Count <= 200) return;
        var protectedPages = VisiblePages().Append(keep).ToHashSet();
        for (var attempts = tab.RenderOrder.Count; attempts > 0 && (total > RenderCacheBytes || tab.RenderCache.Count > 200) && tab.RenderOrder.TryDequeue(out var old); attempts--)
        {
            if (!tab.RenderCache.TryGetValue(old, out var bitmap)) continue;
            if (protectedPages.Contains(old)) { tab.RenderOrder.Enqueue(old); continue; }
            tab.RenderCache.Remove(old); total -= BitmapBytes(bitmap);
            if (old < pageImages.Count) pageImages[old].Source = null;
        }
    }

    /// <summary>Renders pages around the current one after the visible pages are done, so paging is immediate.</summary>
    async Task PrefetchAsync(OpenDocumentTab tab, int current, CancellationToken cancellation)
    {
        for (var distance = 1; distance <= PrefetchDistance * Columns; distance++)
            foreach (var page in new[] { current + distance, current - distance })
            {
                if (cancellation.IsCancellationRequested || tab != active) return;
                if (page < 0 || page >= tab.Pages.Count || tab.RenderCache.ContainsKey(page)) continue;
                await RenderPageAsync(tab, page, cancellation);
            }
    }

    // ---- Page actions on the thumbnail selection -----------------------------------------------

    int[] SelectedThumbnailPages() => ThumbnailList.SelectedItems.OfType<PageDescriptor>().Select(p => p.Number).Distinct().Order().ToArray();

    int[] ActionPages()
    {
        var selected = SelectedThumbnailPages();
        return selected.Length > 1 || (selected.Length == 1 && ThumbnailList.IsKeyboardFocusWithin) ? selected
            : active is null ? [] : [active.CurrentPage];
    }

    async Task RotateSelectionAsync(int quarterTurns)
    {
        if (active is not { } tab || !await AllowEditingAsync(tab)) return;
        var pages = ActionPages();
        if (pages.Length == 0) return;
        await tab.Session.RotatePagesAsync(pages, quarterTurns);
        notice = (pages.Length == 1 ? "Page " + (pages[0] + 1) : pages.Length + " pages") + " rotated. Ctrl+Z undoes it.";
    }

    internal async Task DeletePagesAsync(int[] pages)
    {
        if (active is not { } tab || pages.Length == 0 || !await AllowEditingAsync(tab)) return;
        if (pages.Length >= tab.Pages.Count) { notice = "At least one page must remain. Select fewer pages."; return; }
        await tab.Session.DeletePagesAsync(pages);
        tab.CurrentPage = Math.Min(pages[0], tab.Session.PageCount - 1);
        notice = (pages.Length == 1 ? "Page " + (pages[0] + 1) + " deleted." : pages.Length + " pages deleted.") + " Ctrl+Z restores them.";
    }

    async Task DuplicatePagesAsync(int[] pages)
    {
        if (active is not { } tab || pages.Length == 0 || !await AllowEditingAsync(tab)) return;
        await tab.Session.DuplicatePagesAsync(pages, pages[^1] + 1);
        notice = "Duplicated after page " + (pages[^1] + 1) + ".";
    }

    async Task ExtractPagesAsync(int[] pages)
    {
        if (active is not { } tab || pages.Length == 0) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PDF files|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(tab.FileName) + "-pages.pdf" };
        if (picker.ShowDialog(this) != true) return;
        if (!await AllowEditingAsync(tab)) return;
        var result = await tab.Session.ExtractPagesAsync(pages, picker.FileName);
        notice = $"Created {Path.GetFileName(picker.FileName)} with {result.GetProperty("PageCount").GetInt32():N0} pages. This document is unchanged.";
    }

    async Task InsertBlankAsync(int destination)
    {
        if (active is not { } tab || !await AllowEditingAsync(tab)) return;
        var reference = tab.Pages[Math.Clamp(destination - 1, 0, tab.Pages.Count - 1)];
        await tab.Session.InsertBlankPageAsync(destination, reference.Width, reference.Height);
        tab.CurrentPage = destination;
    }

    /// <summary>Inserts every page of another PDF at a position in this document.</summary>
    internal async Task InsertFileAsync(string path, int destination)
    {
        if (active is not { } tab || !await AllowEditingAsync(tab)) return;
        using var worker = new WorkerClient(workerExecutable);
        worker.Start();
        var inspected = await worker.SendAsync(Wire.Create(Guid.NewGuid(), 0, "InspectCombineInput", new { Path = path }));
        if (!inspected.Success) throw new InvalidOperationException(inspected.Error);
        var count = inspected.Data.GetProperty("PageCount").GetInt32();
        if (count < 1) throw new InvalidOperationException("The source PDF has no pages to import.");
        if (count > 5_000) throw new InvalidOperationException("Insert at most 5,000 pages from one PDF at a time.");
        var pages = Enumerable.Range(0, count).ToArray();
        tab.CurrentPage = destination;
        if (!AdvancedEngineInstalled)
        {
            await tab.Session.InsertPagesAsync(path, pages, destination);
            notice = $"Inserted {count:N0} page{(count == 1 ? "" : "s")} from {Path.GetFileName(path)}. Ctrl+Z removes them.";
            return;
        }
        // Strict import first; a source with bookmarks, forms or annotations falls back to importing page content only.
        var report = await tab.Session.InsertPagesAutoAsync(AdvancedEngine, path, pages, destination);
        notice = $"Inserted {count:N0} page{(count == 1 ? "" : "s")} from {Path.GetFileName(path)}. " +
            (report.Tier == ImportReport.BestEffort ? "Best effort: review the result. " + report.Describe() + " " : "") + "Ctrl+Z removes them.";
    }

    async Task PickAndInsertFileAsync(int destination)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf" };
        if (picker.ShowDialog(this) == true) await InsertFileAsync(picker.FileName, destination);
    }

    async Task RotateAllAsync(int quarterTurns)
    {
        if (active is not { } tab || !await AllowEditingAsync(tab)) return;
        await tab.Session.RotatePagesAsync(Enumerable.Range(0, tab.Pages.Count).ToArray(), quarterTurns);
        notice = "All pages rotated. Ctrl+Z undoes it.";
    }

    async void RotateAll_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => RotateAllAsync(1), "Rotating all pages…");

    MenuItem ActionItem(string header, Func<Task> action, string status, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += async (_, _) => await RunUiAsync(action, status);
        return item;
    }

    void InitializeThumbnailActions()
    {
        ThumbnailList.SelectionMode = SelectionMode.Extended;
        ThumbnailList.AllowDrop = true;
        ThumbnailList.SelectionChanged += (_, _) =>
        {
            // A single click on one thumbnail goes to that page; Ctrl/Shift-click builds a selection.
            if (!busy && ThumbnailList.SelectedItems.Count == 1 && ThumbnailList.SelectedItem is PageDescriptor page &&
                Mouse.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.None && active?.CurrentPage != page.Number)
                GoToPage(page.Number);
        };
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var pages = SelectedThumbnailPages();
            if (pages.Length == 0 && active is not null) pages = [active.CurrentPage];
            var label = pages.Length == 1 ? "page" : pages.Length + " pages";
            var after = pages.Length == 0 ? 0 : pages[^1] + 1;
            var before = pages.Length == 0 ? 0 : pages[0];
            menu.Items.Add(ActionItem("Rotate " + label + " clockwise", () => RotateSelectionAsync(1), "Rotating…", "Ctrl+R"));
            menu.Items.Add(ActionItem("Rotate " + label + " counterclockwise", () => RotateSelectionAsync(3), "Rotating…", "Ctrl+Shift+R"));
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("Duplicate " + label, () => DuplicatePagesAsync(pages), "Duplicating pages…"));
            menu.Items.Add(ActionItem("Extract " + label + " to a new PDF…", () => ExtractPagesAsync(pages), "Extracting pages…"));
            menu.Items.Add(ActionItem("Delete " + label, () => DeletePagesAsync(pages), "Deleting pages…", "Del"));
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("Insert blank page after", () => InsertBlankAsync(after), "Inserting a blank page…"));
            menu.Items.Add(ActionItem("Insert pages from a file before…", () => PickAndInsertFileAsync(before), "Inserting pages…"));
            menu.Items.Add(ActionItem("Insert pages from a file after…", () => PickAndInsertFileAsync(after), "Inserting pages…"));
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = !busy && active is not null;
        };
        menu.Items.Add(new MenuItem { Header = "Page actions" }); // Replaced when the menu opens.
        ThumbnailList.ContextMenu = menu;
        ThumbnailList.PreviewMouseLeftButtonDown += (_, e) => thumbnailDragStart = ThumbnailAt(e.GetPosition(ThumbnailList)) is null ? null : e.GetPosition(ThumbnailList);
        ThumbnailList.PreviewMouseMove += Thumbnail_PreviewMouseMove;
        ThumbnailList.DragOver += Thumbnail_DragOver;
        ThumbnailList.Drop += Thumbnail_Drop;
    }

    PageDescriptor? ThumbnailAt(Point point) =>
        (ThumbnailList.InputHitTest(point) as DependencyObject) is { } hit && ItemsControl.ContainerFromElement(ThumbnailList, hit) is ListBoxItem { DataContext: PageDescriptor page } ? page : null;

    void Thumbnail_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (thumbnailDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || busy || active is null) return;
        var delta = e.GetPosition(ThumbnailList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        thumbnailDragStart = null;
        var pages = SelectedThumbnailPages();
        if (pages.Length == 0 || pages.Length >= active.Pages.Count) return;
        DragDrop.DoDragDrop(ThumbnailList, new DataObject(PageDragFormat, pages), DragDropEffects.Move);
    }

    /// <summary>The insertion index for a drop: before the thumbnail under the pointer, or after it on its right half.</summary>
    int DropIndex(DragEventArgs e)
    {
        if (active is null) return 0;
        var point = e.GetPosition(ThumbnailList);
        if (ThumbnailAt(point) is not { } page) return active.Pages.Count;
        var container = (ListBoxItem)ThumbnailList.ItemContainerGenerator.ContainerFromItem(page);
        var local = e.GetPosition(container);
        return page.Number + (local.X > container.ActualWidth / 2 ? 1 : 0);
    }

    void Thumbnail_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = busy || active is null ? DragDropEffects.None
            : e.Data.GetDataPresent(PageDragFormat) ? DragDropEffects.Move
            : e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsPdf) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    async void Thumbnail_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (busy || active is not { } tab) return;
        var index = DropIndex(e);
        if (e.Data.GetData(PageDragFormat) is int[] pages)
            await RunUiAsync(() => MoveThumbnailPagesAsync(pages, index), "Moving pages…");
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await RunUiAsync(async () =>
            {
                // Insert in the dropped order, each file after the previous one.
                foreach (var file in files.Where(IsPdf))
                {
                    var before = tab.Session.PageCount;
                    await InsertFileAsync(file, index);
                    index += tab.Session.PageCount - before;
                }
            }, "Inserting dropped PDFs…");
    }

    /// <summary>Moves the given pages (in document order) so they start at insertion index <paramref name="index"/> of the current list.</summary>
    internal async Task MoveThumbnailPagesAsync(int[] pages, int index)
    {
        if (active is not { } tab || pages.Length == 0) return;
        pages = pages.Distinct().Order().ToArray();
        var destination = index - pages.Count(p => p < index);
        if (pages.Select((p, i) => p == destination + i).All(same => same)) return; // Dropped in place.
        if (!await AllowEditingAsync(tab)) return;
        await tab.Session.MovePagesAsync(pages, destination);
        tab.CurrentPage = destination;
        notice = (pages.Length == 1 ? "Page" : pages.Length + " pages") + " moved. Ctrl+Z undoes it.";
    }

    // ---- Page context menu -------------------------------------------------------------------

    void InitializePageContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var hasSelection = selectedText.Length > 0 && selectionPage >= 0;
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", IsEnabled = selectedText.Length > 0 };
            copy.Click += (_, _) => Clipboard.SetText(selectedText);
            menu.Items.Add(copy);
            foreach (var kind in new[] { "Highlight", "Underline", "Strikeout" })
            {
                var item = ActionItem(kind + " selected text", () => active is { } tab ? ApplyMarkupToSelectionAsync(tab, selectionPage, kind) : Task.CompletedTask,
                    "Adding " + kind.ToLowerInvariant() + "…");
                item.IsEnabled = hasSelection && !busy;
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = "Edit or delete annotations on this page…", IsEnabled = active is not null && !busy };
            manage.Click += ManageAnnotations_Click;
            menu.Items.Add(manage);
            menu.Items.Add(ActionItem("Rotate this page clockwise", () => RotateCurrentAsync(1), "Rotating…"));
        };
        menu.Items.Add(new MenuItem { Header = "Page" });
        DocumentScroll.ContextMenu = menu;
    }

    async Task RotateCurrentAsync(int quarterTurns)
    {
        if (active is not { } tab || !await AllowEditingAsync(tab)) return;
        await tab.Session.RotatePagesAsync([tab.CurrentPage], quarterTurns);
    }
}
