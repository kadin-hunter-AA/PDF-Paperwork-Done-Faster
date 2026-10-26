using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Design-mock workspace features: find with match case, whole word and match counts; the Annotations and
/// On this page panels; the Hand tool; and the panel shortcuts.</summary>
static class WorkspaceUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var layoutFixture = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "layout-columns-table.pdf");
        var largeFixture = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "large-500-pages.pdf");
        var hashes = new[] { RecoveryStore.Fingerprint(layoutFixture), RecoveryStore.Fingerprint(largeFixture) };
        var layout = Path.Combine(output, "layout.pdf"); File.Copy(layoutFixture, layout);
        var large = Path.Combine(output, "large.pdf"); File.Copy(largeFixture, large);
        var recovery = new RecoveryStore(Path.Combine(output, "recovery"));
        var owner = new MainWindow(worker, recovery, new TestDialogs()) { Width = 1280, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            owner.SetTheme(ShellTheme.Graphite);
            await owner.RunUiAsync(() => owner.OpenPathAsync(layout), "Opening layout fixture…");
            await WaitUntilAsync(() => !owner.IsBusy, "The layout fixture did not open.");
            var tab = owner.ActiveTab ?? throw new Exception("No active tab.");

            // ---- Find: counts, options, next and previous ----
            var search = Control<TextBox>(owner, "SearchBox");
            var matchCase = Control<ToggleButton>(owner, "MatchCaseToggle");
            var wholeWord = Control<ToggleButton>(owner, "WholeWordToggle");
            ApplicationCommands.Find.Execute(null, owner);
            Check(Control<Border>(owner, "FindBar").Visibility == Visibility.Visible, "Ctrl+F opens the find bar");
            async Task Find(string query, int direction = 1)
            {
                if (search.Text != query) search.Text = query;
                await owner.SearchAsync(direction);
                await Idle(owner);
            }
            await Find("column");
            Check(owner.SearchMatchLabel == "1 of 17", $"Find counts every match on the page (\"column\" → {owner.SearchMatchLabel})");
            Check(Control<TextBlock>(owner, "MatchPages").Text == "on page 1", "Find names the pages that have matches");
            await Find("column");
            Check(owner.SearchMatchLabel == "2 of 17", "Enter moves to the next match");
            await Find("column", -1); await Find("column", -1);
            Check(owner.SearchMatchLabel == "17 of 17", "Shift+Enter moves back and wraps to the last match");
            await Find("column");
            Check(owner.SearchMatchLabel == "1 of 17", "Next after the last match wraps to the first");
            Check(owner.SelectedTextForTest.Equals("column", StringComparison.OrdinalIgnoreCase), $"The current match is highlighted as the selection (\"{owner.SelectedTextForTest}\")");

            await Find("left");
            Check(owner.SearchMatchLabel == "1 of 8", "Find ignores capitalization by default");
            matchCase.IsChecked = true; matchCase.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Find("left");
            Check(owner.SearchMatchLabel == "No matches", "Match case finds only the same capitalization");
            await Find("Left");
            Check(owner.SearchMatchLabel == "1 of 8", "Match case still finds exact capitalization");
            matchCase.IsChecked = false; matchCase.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Find("Lef");
            Check(owner.SearchMatchLabel == "1 of 8", "Without Whole word, part of a word matches");
            await PressAsync(owner, Key.W, ModifierKeys.Alt);
            Check(wholeWord.IsChecked == true, "Alt+W turns on Whole word");
            await Find("Lef");
            Check(owner.SearchMatchLabel == "No matches", "Whole word ignores matches inside longer words");
            await Find("Left");
            Check(owner.SearchMatchLabel == "1 of 8", "Whole word still finds the whole word");
            await Find("column");
            Check(owner.SearchMatchLabel == "1 of 16", "Whole word skips \"Columns\" in the heading");
            await PressAsync(owner, Key.W, ModifierKeys.Alt);
            Check(wholeWord.IsChecked == false, "Alt+W turns Whole word off again");
            Capture(owner, Path.Combine(output, "find-bar.png"));

            // ---- On this page and Annotations panels ----
            var sideTabs = Control<TabControl>(owner, "SidePanelTabs");
            Check(ReferenceEquals(sideTabs.SelectedItem, owner.FindName("PropertiesTab")), "The side panel opens on Properties");
            sideTabs.SelectedItem = owner.FindName("PageContentsTab");
            await WaitUntilAsync(() => owner.PageContentEntries.Count > 0, "On this page did not list the page contents.");
            Check(owner.PageContentEntries.Any(e => e.Kind.StartsWith("Text", StringComparison.Ordinal) && e.Detail.Contains("Left column", StringComparison.Ordinal)),
                "On this page lists the page's text objects");
            sideTabs.SelectedItem = owner.FindName("AnnotationsTab");
            await WaitUntilAsync(() => Control<TextBlock>(owner, "AnnotationsStatus").Text.StartsWith("No annotations", StringComparison.Ordinal), "The Annotations panel did not load.");
            Check(owner.AnnotationEntries.Count == 0, "The Annotations panel reports a document without annotations");
            await owner.RunUiAsync(() => tab.Session.AddCommentAsync(0, "Check the table totals", 400, 700, 420, 720), "Adding note…");
            await WaitUntilAsync(() => owner.AnnotationEntries.Count == 1, "The Annotations panel did not pick up the new note.");
            Check(owner.AnnotationEntries[0] is { Kind: "Note", Detail: "Check the table totals", Page: 0 }, "The Annotations panel lists a new note with its comment and page");
            sideTabs.SelectedItem = owner.FindName("PageContentsTab");
            await WaitUntilAsync(() => owner.PageContentEntries.Any(e => e.Kind == "Note"), "On this page did not list the new note.");
            Check(Control<TextBlock>(owner, "PageContentsStatus").Text.Contains("1 annotation", StringComparison.Ordinal), "On this page counts the page's annotations");
            Capture(owner, Path.Combine(output, "page-contents-panel.png"));
            await owner.RunUiAsync(() => tab.Session.UndoAsync(), "Undoing…");

            // ---- Hand tool ----
            var hand = Control<ToggleButton>(owner, "HandToolToggle");
            var select = Control<ToggleButton>(owner, "SelectModeToggle");
            var scroll = Control<ScrollViewer>(owner, "DocumentScroll");
            scroll.Focus();
            await PressAsync(owner, Key.H);
            Check(owner.IsHandTool && hand.IsChecked == true && select.IsChecked == false, "H chooses the Hand tool");
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            Check(layer.Cursor == Cursors.Hand, "The page shows the hand cursor");
            var source = PresentationSource.FromVisual(owner) ?? throw new Exception("No presentation source.");
            layer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = layer });
            Check(owner.IsPanning, "Pressing on the page with the Hand tool starts panning instead of selecting text");
            layer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = layer });
            Check(!owner.IsPanning && Mouse.OverrideCursor is null, "Releasing ends the pan and restores the cursor");
            await PressAsync(owner, Key.V);
            Check(!owner.IsHandTool && select.IsChecked == true && layer.Cursor == Cursors.IBeam, "V returns to Select");
            owner.SetHandTool(true);
            Control<ToggleButton>(owner, "EditModeToggle").IsChecked = true;
            Control<ToggleButton>(owner, "EditModeToggle").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!owner.IsHandTool && hand.IsChecked == false, "Choosing Edit leaves the Hand tool");
            owner.SetPageTool(PageTool.Select, false);
            scroll.Focus();
            await PressAsync(owner, Key.Space);
            Check(layer.Cursor == Cursors.Hand, "Holding Space shows the hand cursor for a temporary pan");
            layer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = layer });
            Check(owner.IsPanning, "Space plus a press pans in Select");
            layer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = layer });
            owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Check(layer.Cursor == Cursors.IBeam, "Releasing Space returns to the Select cursor");

            // ---- Panel shortcuts ----
            var navigation = Control<ToggleButton>(owner, "NavigationToggle");
            var side = Control<ToggleButton>(owner, "PropertiesToggle");
            // A narrow screen hides the side panel on its own, so each shortcut is checked from the panel's current state.
            foreach (var (command, toggle, panel, name) in new[]
            {
                (ShellCommands.ToggleNavigation, navigation, "NavigationPanel", "Ctrl+B page sidebar"),
                (ShellCommands.ToggleSidePanel, side, "PropertiesPanel", "Ctrl+Shift+B side panel")
            })
            {
                var shown = toggle.IsChecked == true;
                for (var press = 0; press < 2; press++)
                {
                    command.Execute(null, owner); await Idle(owner);
                    shown = !shown;
                    Check(toggle.IsChecked == shown && (Control<Border>(owner, panel).Visibility == Visibility.Visible) == shown,
                        $"{name} {(shown ? "shows" : "hides")} the panel (press {press + 1})");
                }
            }

            // ---- Find across a long document ----
            await owner.RunUiAsync(() => owner.OpenPathAsync(large), "Opening 500-page fixture…");
            await WaitUntilAsync(() => !owner.IsBusy && owner.ActiveTab?.Pages.Count == 500, "The 500-page fixture did not open.");
            var largeTab = owner.ActiveTab!;
            ApplicationCommands.Find.Execute(null, owner);
            await Find("page 250 of");
            await WaitUntilAsync(() => largeTab.CurrentPage == 249, $"Find did not go to page 250 (current {largeTab.CurrentPage + 1}).", 60);
            Check(owner.SearchMatchLabel == "1 of 1" && Control<TextBlock>(owner, "MatchPages").Text == "on page 250", "Find scans every page of a 500-page document and goes to the only match");
            await Find("Corpus page");
            Check(owner.SearchMatchLabel == "250 of 500", $"Find starts at the current page and counts all 500 matches ({owner.SearchMatchLabel})");
            Check(Control<TextBlock>(owner, "MatchPages").Text == "on 500 pages", "Find reports how many pages have matches");

            foreach (var (fixture, hash) in new[] { layoutFixture, largeFixture }.Zip(hashes))
                Check(RecoveryStore.Fingerprint(fixture) == hash, "Frozen fixture unchanged: " + Path.GetFileName(fixture));
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            Console.WriteLine($"WORKSPACE UI PROOF PASS {checks.Count} {output}");
        }
        finally
        {
            try { await owner.RunUiAsync(async () => { if (await owner.CloseAllAsync()) typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true); }, "Closing…"); } catch { }
            owner.Close();
        }
    }

    static T Control<T>(FrameworkElement owner, string name) where T : class =>
        owner.FindName(name) as T ?? throw new Exception("Missing control " + name);

    static async Task Idle(Window owner)
    {
        await owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await WaitUntilAsync(() => owner is not MainWindow { IsBusy: true }, "The shell stayed busy.");
    }

    static async Task PressAsync(MainWindow owner, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var source = PresentationSource.FromVisual(owner) ?? throw new Exception("No presentation source.");
        owner.ModifiersForTest = modifiers;
        try { owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
        finally { owner.ModifiersForTest = null; }
        await Idle(owner);
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
