using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Viewer shortcuts, wheel zoom, page layouts, the render cache, thumbnail page actions and remembered tool styles.</summary>
static class ViewerUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "large-500-pages.pdf");
        var fixtureHash = RecoveryStore.Fingerprint(fixture);
        var large = Path.Combine(output, "large.pdf");
        File.Copy(fixture, large);
        var recovery = new RecoveryStore(Path.Combine(output, "recovery"));
        var dialogs = new TestDialogs();
        var owner = NewWindow(worker, recovery, dialogs);
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            owner.SetTheme(ShellTheme.Light);
            await owner.RunUiAsync(() => owner.OpenPathAsync(large), "Opening viewer fixture…");
            await WaitUntilAsync(() => !owner.IsBusy, "The document did not open.");
            var tab = owner.ActiveTab ?? throw new Exception("No active tab.");
            Check(tab.Pages.Count == 500, "The 500-page fixture opens");

            // ---- Zoom commands and wheel ----
            async Task Execute(RoutedUICommand command)
            {
                Check(command.CanExecute(null, owner), command.Text + " is available");
                command.Execute(null, owner);
                await WaitUntilAsync(() => !owner.IsBusy, command.Text + " did not finish.");
            }
            await Execute(ShellCommands.ZoomIn);
            Check(tab.Zoom == 125, "Ctrl+Plus zooms to the next step (125%)");
            await Execute(ShellCommands.ZoomOut); await Execute(ShellCommands.ZoomOut);
            Check(tab.Zoom == 75, "Ctrl+Minus zooms out by steps (75%)");
            await Execute(ShellCommands.ActualSize);
            Check(tab.Zoom == 100, "Ctrl+1 shows actual size");
            await Execute(ShellCommands.FitPage);
            var scroll = (ScrollViewer)owner.FindName("DocumentScroll");
            var page = tab.Pages[tab.CurrentPage];
            Check(page.Height * 96d / 72 * tab.Zoom / 100 <= scroll.ActualHeight, "Ctrl+0 fits the whole page height in the view");
            await Execute(ShellCommands.FitWidth);
            Check(page.Width * 96d / 72 * tab.Zoom / 100 <= scroll.ActualWidth, "Ctrl+2 fits the page width");
            await Execute(ShellCommands.ActualSize);
            owner.QueueWheelZoom(240);
            await WaitUntilAsync(() => tab.Zoom != 100 && !owner.IsBusy, "Ctrl+wheel did not zoom.");
            Check(tab.Zoom == 121, "Two Ctrl+wheel notches zoom by 10% each (121%)");
            owner.QueueWheelZoom(-240);
            await WaitUntilAsync(() => tab.Zoom != 121 && !owner.IsBusy, "Ctrl+wheel did not zoom out.");
            Check(tab.Zoom == 100, "Ctrl+wheel back two notches returns to 100%");

            // ---- Keyboard navigation ----
            scroll.Focus();
            async Task Press(Key key)
            {
                var source = PresentationSource.FromVisual(owner) ?? throw new Exception("No presentation source.");
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                owner.RaiseEvent(args);
                await owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
            }
            await Press(Key.End);
            Check(tab.CurrentPage == 499, $"End goes to the last page (current {tab.CurrentPage}, focus {Keyboard.FocusedElement?.GetType().Name ?? "none"}, busy {owner.IsBusy})");
            await Press(Key.Home);
            Check(tab.CurrentPage == 0, "Home goes to the first page");
            await Press(Key.PageDown); await Press(Key.PageDown);
            Check(tab.CurrentPage == 2, "Page Down moves one page at a time in continuous layout");
            await Press(Key.PageUp);
            Check(tab.CurrentPage == 1, "Page Up moves back one page");
            ShellCommands.GoToPage.Execute(null, owner);
            var pageBox = (TextBox)owner.FindName("PageNumberBox");
            Check(pageBox.IsKeyboardFocused, "Ctrl+G focuses the page number box");
            scroll.Focus();

            // ---- Layouts ----
            var stack = (StackPanel)owner.FindName("PageStack");
            owner.SetPageLayout(PageLayout.TwoPage);
            await Settle(owner);
            Check(stack.Children.Count == 250 && stack.Children.OfType<StackPanel>().All(row => row.Children.Count == 2), "Two-page layout shows 250 spreads of two pages");
            await Press(Key.PageDown);
            Check(tab.CurrentPage % 2 == 1 || tab.CurrentPage == 3, "Page Down in two-page layout moves by a spread");
            owner.SetPageLayout(PageLayout.TwoPageCover);
            await Settle(owner);
            Check(((StackPanel)stack.Children[0]).Children.Count == 1 && stack.Children.Count == 251, "Cover layout shows the first page alone, then facing pairs");
            owner.SetPageLayout(PageLayout.SinglePage);
            await Settle(owner);
            owner.GoToPage(41);
            await Settle(owner);
            var visibleCards = stack.Children.OfType<FrameworkElement>().Count(c => c.Visibility == Visibility.Visible);
            Check(visibleCards == 1 && tab.CurrentPage == 41, "Single-page layout shows only the current page");
            Capture(owner, Path.Combine(output, "single-page.png"));
            await Press(Key.Right);
            Check(tab.CurrentPage == 42, "Right arrow turns the page in single-page layout");
            owner.SetPageLayout(PageLayout.Continuous);
            await Settle(owner);
            Check(stack.Children.Count == 500 && stack.Children.OfType<FrameworkElement>().All(c => c.Visibility == Visibility.Visible), "Continuous layout restores every page");
            Check(File.ReadAllText(Path.Combine(output, "shell-settings.json")).Contains("\"Layout\":0"), "The chosen layout is saved in settings");

            // ---- Render cache stays within its memory budget while keeping visible pages ----
            for (var p = 0; p < 120; p += 7) { owner.GoToPage(p); await Task.Delay(120); }
            await Settle(owner, 900);
            await WaitUntilAsync(() => tab.RenderCache.ContainsKey(tab.CurrentPage + 2), "Prefetch did not finish.", 30);
            var cachedBytes = tab.RenderCache.Values.Sum(b => (long)b.PixelWidth * b.PixelHeight * 4);
            Check(cachedBytes <= MainWindow.RenderCacheBytes, $"Render cache stays within its {MainWindow.RenderCacheBytes / 1048576} MB budget ({cachedBytes / 1048576} MB used)");
            Check(tab.RenderCache.ContainsKey(tab.CurrentPage), $"The current page stays rendered (page {tab.CurrentPage}; cached {string.Join(",", tab.RenderCache.Keys.Order())}; status {((TextBlock)owner.FindName("Status")).Text})");
            Check(tab.RenderCache.ContainsKey(tab.CurrentPage + 1) && tab.RenderCache.ContainsKey(tab.CurrentPage + 2), "Pages after the current one are prefetched");

            // ---- Page actions on a short copy ----
            var small = Path.Combine(output, "four-pages.pdf");
            await tab.Session.ExtractPagesAsync([0, 1, 2, 3], small);
            await owner.RunUiAsync(() => owner.OpenPathAsync(small), "Opening four pages…");
            await WaitUntilAsync(() => !owner.IsBusy, "The short document did not open.");
            var shortTab = owner.ActiveTab!;
            var session = shortTab.Session;
            var thumbnails = (ListBox)owner.FindName("ThumbnailList");
            Check(thumbnails.SelectionMode == SelectionMode.Extended, "Thumbnails allow Ctrl/Shift multi-select");
            thumbnails.SelectedItems.Clear();
            thumbnails.SelectedItems.Add(shortTab.Pages[1]); thumbnails.SelectedItems.Add(shortTab.Pages[2]);
            thumbnails.Focus();
            await Execute(ShellCommands.RotateClockwise);
            var info = await session.PageInfoAsync();
            var rotations = info.GetProperty("Pages").EnumerateArray().Select(p => p.GetProperty("Rotation").GetInt32()).ToArray();
            Check(rotations.SequenceEqual([0, 1, 1, 0]), $"Ctrl+R rotates the selected thumbnails together ({string.Join(",", rotations)}; selected {thumbnails.SelectedItems.Count}; {string.Join(" | ", dialogs.Messages)}; status {((TextBlock)owner.FindName("Status")).Text})");
            Check(session.CanUndo, "Multi-page rotation is one undoable step");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            rotations = (await session.PageInfoAsync()).GetProperty("Pages").EnumerateArray().Select(p => p.GetProperty("Rotation").GetInt32()).ToArray();
            Check(rotations.All(r => r == 0) && !session.CanUndo, "One Undo restores every rotated page");
            var firstText = (await session.ExtractAsync(0)).GetProperty("Text").GetString();
            await owner.RunUiAsync(() => owner.MoveThumbnailPagesAsync([0], 4), "Moving…");
            Check((await session.ExtractAsync(3)).GetProperty("Text").GetString() == firstText, "Dragging page 1 to the end moves it there");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            await WaitUntilAsync(() => !owner.IsBusy && shortTab.Pages.Count == 4, "Undo did not refresh.");
            thumbnails.SelectedItems.Clear(); thumbnails.SelectedItems.Add(shortTab.Pages[3]); thumbnails.Focus();
            await Press(Key.Delete);
            await WaitUntilAsync(() => !owner.IsBusy && session.PageCount == 3, "Delete did not remove the selected page.");
            Check(session.PageCount == 3, "Delete removes the selected thumbnail page");
            await owner.RunUiAsync(() => owner.InsertFileAsync(fixture, 1), "Inserting…");
            Check(session.PageCount == 503, "A PDF dropped or chosen from a file inserts at the chosen position");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            await WaitUntilAsync(() => !owner.IsBusy, "Undo did not finish.");
            Check(session.PageCount == 4, "Undo restores the deleted page and removes inserted pages");
            Check(thumbnails.ContextMenu is not null && ((ScrollViewer)owner.FindName("DocumentScroll")).ContextMenu is not null,
                "Thumbnails and pages have right-click menus");
            thumbnails.ContextMenu!.IsOpen = true; await Settle(owner);
            var thumbItems = thumbnails.ContextMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();
            thumbnails.ContextMenu.IsOpen = false;
            Check(thumbItems.Any(h => h.StartsWith("Rotate")) && thumbItems.Any(h => h.StartsWith("Delete")) && thumbItems.Any(h => h.StartsWith("Extract")),
                "The thumbnail menu offers rotate, delete and extract");

            // ---- Esc leaves the current tool; a chosen drawing color and width are remembered ----
            owner.SetPageTool(PageTool.Pen);
            await Press(Key.Escape);
            Check(!owner.IsEditMode && owner.CurrentPageTool == PageTool.Select, "Esc leaves the current editing tool");
            owner.SetPageTool(PageTool.Rectangle);
            var stylePanel = (FrameworkElement)owner.FindName("StylePanel");
            Check(stylePanel.Visibility == Visibility.Visible, "Drawing tools show color and width choices");
            var swatches = ((Panel)owner.FindName("StrokeSwatches")).Children.OfType<RadioButton>().ToArray();
            swatches.First(s => (string)s.Tag == "Red").IsChecked = true;
            ((ComboBox)owner.FindName("StrokeWidthChoice")).SelectedIndex = 3;
            Check(owner.StrokeFor(PageTool.Rectangle) is { Red: 204, Green: 0, Blue: 0, Width: 5 }, "The rectangle tool remembers red at 5 pt");
            Check(owner.StrokeFor(PageTool.Pen) is null, "Other tools keep their own style");
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            owner.GoToPage(0); await Settle(owner);
            await owner.RunUiAsync(() => owner.CommitDrawingAsync(shortTab, 0, PageTool.Rectangle,
                [new Point(layer.ActualWidth * .3, layer.ActualHeight * .3), new Point(layer.ActualWidth * .6, layer.ActualHeight * .5)], layer.ActualWidth, layer.ActualHeight), "Drawing…");
            var render = await session.RenderAsync(0, 306, 396);
            var pixels = Convert.FromBase64String(render.GetProperty("Bgra").GetString()!);
            var redPixels = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4) if (pixels[i + 2] > 150 && pixels[i + 1] < 80 && pixels[i] < 80) redPixels++;
            Check(redPixels > 200, "The new rectangle is drawn in the remembered color");
            owner.SetPageTool(PageTool.Select, false);
            Check(File.ReadAllText(Path.Combine(output, "shell-settings.json")).Contains("Rectangle"), "Tool styles are saved with the settings");
            Check(dialogs.Messages.Count == 0, "No error dialogs were shown: " + string.Join(" | ", dialogs.Messages));
        }
        finally
        {
            owner.Close();
            await WaitUntilAsync(() => !owner.IsVisible, "The window did not close.", 60);
        }

        // A new window reads the remembered layout and tool style from the same settings file.
        var reopened = NewWindow(worker, recovery, new TestDialogs());
        try
        {
            reopened.Show();
            await WaitUntilAsync(() => !reopened.IsBusy, "The reopened shell did not load.");
            Check(reopened.StrokeFor(PageTool.Rectangle) is { Red: 204, Width: 5 } && reopened.CurrentLayout == PageLayout.Continuous,
                "A new window remembers the tool style and layout");
        }
        finally { reopened.Close(); }
        Check(RecoveryStore.Fingerprint(fixture) == fixtureHash, "The frozen fixture is unchanged");
        File.WriteAllText(Path.Combine(output, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} viewer UI checks. Evidence: {output}");
    }

    static MainWindow NewWindow(string worker, RecoveryStore recovery, TestDialogs dialogs) => new(worker, recovery, dialogs)
    {
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false
    };

    static async Task Settle(MainWindow owner, int milliseconds = 400)
    {
        await owner.Dispatcher.InvokeAsync(() => owner.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await WaitUntilAsync(() => !owner.IsBusy, "The shell stayed busy.");
    }

    static async Task WaitUntilAsync(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(message);
            await Task.Delay(30);
        }
    }

    static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
