using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class ShellProof
{
    public static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var a = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var b = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        var aHash = RecoveryStore.Fingerprint(a);
        var bHash = RecoveryStore.Fingerprint(b);
        var recovery = new RecoveryStore(Path.Combine(output, "recovery"));
        var defaultPreferences = new ShellPreferences(Path.Combine(output, "default-settings.json"));
        Check(defaultPreferences.Theme == ShellTheme.System, "New shell settings follow the Windows theme by default");
        var dialogs = new TestDialogs { NextOpen = a };
        var contrast = false;
        var window = new MainWindow(worker, recovery, dialogs, () => contrast);
        window.SetTheme(ShellTheme.Dark);

        T Control<T>(MainWindow w, string name) where T : FrameworkElement => (T)w.FindName(name);
        async Task Idle(MainWindow w)
        {
            var watch = Stopwatch.StartNew();
            while (w.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
            if (w.IsBusy) throw new Exception("Shell operation did not finish.");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        async Task Command(MainWindow w, RoutedCommand command)
        {
            command.Execute(null, w);
            await Idle(w);
        }
        async Task Add(MainWindow w, string text)
        {
            Control<TextBox>(w, "NewText").Text = text;
            Control<Button>(w, "AddButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(w);
        }
        async Task Select(MainWindow w, OpenDocumentTab tab)
        {
            Control<TabControl>(w, "DocumentTabs").SelectedItem = tab;
            await Idle(w);
        }
        async Task Capture(MainWindow w, string name, int width = 1280, int height = 800)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var panel = (FrameworkElement)w.Content;
            panel.Measure(new Size(width, height));
            panel.Arrange(new Rect(0, 0, width, height));
            panel.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name));
            encoder.Save(stream);
        }

        await Idle(window); // Let recovery startup and WPF command requery settle before inspecting Home.
        var startControls = $"Open={Control<Button>(window, "OpenButton").IsEnabled}, StartOpen={Control<Button>(window, "StartOpenButton").IsEnabled}, " +
            $"Save={Control<Button>(window, "SaveButton").IsEnabled}, SaveAs={Control<Button>(window, "SaveAsButton").IsEnabled}, " +
            $"Undo={Control<Button>(window, "UndoButton").IsEnabled}, Redo={Control<Button>(window, "RedoButton").IsEnabled}, " +
            $"Print={Control<Button>(window, "PrintButton").IsEnabled}, Edit={Control<Button>(window, "EditExistingTextButton").IsEnabled}, " +
            $"Combine={Control<Button>(window, "CombineButton").IsEnabled}, " +
            $"OpenCanExecute={ApplicationCommands.Open.CanExecute(null, window)}, WindowEnabled={window.IsEnabled}, " +
            $"OpenTargetIsWindow={ReferenceEquals(Control<Button>(window, "OpenButton").CommandTarget, window)}, " +
            $"StartTargetIsWindow={ReferenceEquals(Control<Button>(window, "StartOpenButton").CommandTarget, window)}, " +
            $"WindowBusy={window.IsBusy}";
        Check(Control<Button>(window, "OpenButton").IsEnabled && Control<Button>(window, "StartOpenButton").IsEnabled &&
            !Control<Button>(window, "SaveButton").IsEnabled && !Control<Button>(window, "SaveAsButton").IsEnabled &&
            !Control<Button>(window, "UndoButton").IsEnabled && !Control<Button>(window, "RedoButton").IsEnabled &&
            !Control<Button>(window, "PrintButton").IsEnabled && !Control<Button>(window, "EditExistingTextButton").IsEnabled &&
            Control<Button>(window, "CombineButton").IsEnabled,
            "Home enables Open and Combine, disables document-only actions: " + startControls);
        Check(Control<ScrollViewer>(window, "StartScreen").Visibility == Visibility.Visible, "Empty workspace displays Open/Recent start screen");
        Check(Control<ScrollViewer>(window, "DocumentScroll").Visibility == Visibility.Collapsed, "Empty workspace has no stale document canvas");
        Check(Control<Border>(window, "NavigationPanel").Visibility == Visibility.Collapsed &&
            Control<Border>(window, "PropertiesPanel").Visibility == Visibility.Collapsed,
            "Home hides empty navigation and properties panels to keep the opening workflow spacious");
        await Capture(window, "start-dark.png");
        await Command(window, ApplicationCommands.Open);
        var tabA = window.ActiveTab!;
        await Add(window, "TAB A EDIT");
        Check(Control<Button>(window, "UndoButton").IsEnabled && Control<Button>(window, "SaveButton").IsEnabled,
            "Real toolbar command buttons become available after an edit");
        Control<TextBox>(window, "NewText").Text = "DRAFT FOR A";
        Control<TextBox>(window, "TextX").Text = "100";
        Control<TextBox>(window, "TextY").Text = "200";
        await window.RunUiAsync(() => window.SetZoomAsync(125));
        dialogs.NextOpen = b;
        await Command(window, ApplicationCommands.Open);
        var tabB = window.ActiveTab!;
        Check(window.Documents.Count == 2 && tabA.Session.IsDirty && !tabB.Session.IsDirty, "Opening another PDF adds a clean tab without closing dirty document");
        Check(dialogs.CloseQuestions.Count == 0, "Opening another tab does not prompt to discard current edits");
        Check(Control<TextBox>(window, "NewText").Text == "" && tabB.Zoom == 100, "New tab has its own text draft and zoom");
        Check(Control<TextBlock>(window, "PropertiesFile").Text == Path.GetFileName(b) &&
            Control<TextBlock>(window, "Status").Text.Contains("Page 1 of " + tabB.Session.PageCount), "Properties and status track the active PDF");
        Check(tabB.Pages.Count > 1, "Page-source fixture provides multiple pages for current-page editing");
        window.GoToPage(1);
        Control<TextBox>(window, "NewText").Text = "TAB B EDIT";
        Control<Button>(window, "AddButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(window);
        var tabBFirstPageText = (await tabB.Session.ExtractAsync(0)).GetProperty("Text").GetString()!;
        var tabBSecondPage = await tabB.Session.ExtractAsync(1);
        Check(!tabBFirstPageText.Contains("TAB B EDIT") && tabBSecondPage.GetProperty("Text").GetString()!.Contains("TAB B EDIT"),
            "Add text targets the page currently in view rather than always changing page one");
        var priorRotation = tabBSecondPage.GetProperty("Rotation").GetInt32();
        var firstPageRotation = (await tabB.Session.ExtractAsync(0)).GetProperty("Rotation").GetInt32();
        Control<Button>(window, "RotateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(window);
        Check((await tabB.Session.ExtractAsync(1)).GetProperty("Rotation").GetInt32() == (priorRotation + 1) % 4,
            "Rotate current page applies the operation to the visible page");
        Check((await tabB.Session.ExtractAsync(0)).GetProperty("Rotation").GetInt32() == firstPageRotation,
            "Rotating the current page leaves other pages unchanged");
        Check(Control<TextBox>(window, "PageNumberBox").Text == "2", "Current page status follows page navigation");
        await Select(window, tabA);
        Check(window.CurrentSession == tabA.Session && Control<TextBox>(window, "NewText").Text == "DRAFT FOR A" &&
            Control<TextBox>(window, "TextX").Text == "100", "Selecting a tab restores its document and draft positions");
        Check(Control<TextBlock>(window, "ZoomValue").Text == "125%", "Tab selection restores its own zoom");
        var width = (await tabA.Session.ExtractAsync(0)).GetProperty("Width").GetSingle();
        Check(Math.Abs(Control<Image>(window, "PageImage").Width - width * 96d / 72 * 1.25) < 0.1, "Zoom display matches actual canvas scale");
        await Command(window, ShellCommands.NextTab);
        Check(window.ActiveTab == tabB, "Ctrl+Tab command cycles to next document");
        await Command(window, ShellCommands.PreviousTab);
        Check(window.ActiveTab == tabA, "Ctrl+Shift+Tab command cycles to previous document");
        Control<ListBox>(window, "OpenDocumentList").SelectedItem = tabB;
        await Idle(window);
        Check(window.ActiveTab == tabB, "Navigation list selects the corresponding document tab");
        dialogs.NextOpen = a;
        await Command(window, ApplicationCommands.Open);
        Check(window.Documents.Count == 2 && window.ActiveTab == tabA && tabA.Session.IsDirty, "Duplicate Open selects existing tab without losing edits/history");
        Check(Control<ListBox>(window, "RecentList").Items.Cast<RecentDocument>().Count(r => r.FullPath == a) == 1,
            "Recent history deduplicates a repeatedly opened path");
        var notifications = dialogs.Messages.Count;
        await window.RunUiAsync(() => window.OpenPathAsync(Path.Combine(output, "missing.pdf")));
        Check(window.Documents.Count == 2 && window.ActiveTab == tabA && dialogs.Messages.Count == notifications + 1,
            "Failed Open keeps existing tabs and displays the failure");

        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Cancel);
        await window.RunUiAsync(async () => { await window.CloseTabAsync(tabB); });
        Check(window.Documents.Count == 2 && window.ActiveTab == tabA && dialogs.CloseQuestions.Last() == tabB.Session.Id,
            "Closing inactive dirty tab prompts for that tab; Cancel preserves both");
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Yes);
        dialogs.NextSave = null;
        await window.RunUiAsync(async () => { await window.CloseTabAsync(tabB); });
        Check(window.Documents.Count == 2, "Cancel in inactive-tab Save As prevents its close");
        var savedB = Path.Combine(output, "inactive-b-saved.pdf");
        dialogs.NextSave = savedB;
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Yes);
        await window.RunUiAsync(async () => { await window.CloseTabAsync(tabB); });
        Check(window.Documents.Count == 1 && window.ActiveTab == tabA && tabA.Session.IsDirty, "Saving/closing inactive tab preserves the active tab and its dirty state");
        using (var reopened = await DocumentSession.OpenAsync(worker, recovery, savedB))
        {
            Check((await reopened.ExtractAsync(1)).GetProperty("Text").GetString()!.Contains("TAB B EDIT") &&
                !(await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("TAB B EDIT"),
                "Inactive-tab save persists that PDF's edits rather than the active PDF");
            await reopened.CloseAsync();
        }
        dialogs.NextOpen = b;
        await Command(window, ApplicationCommands.Open);
        tabB = window.ActiveTab!;
        await Select(window, tabA);
        dialogs.NextSave = b;
        notifications = dialogs.Messages.Count;
        await Command(window, ApplicationCommands.SaveAs);
        Check(tabA.Session.IsDirty && RecoveryStore.Fingerprint(b) == bHash && dialogs.Messages.Count == notifications + 1,
            "Save As cannot overwrite another open tab's PDF");
        var savedA = Path.Combine(output, "tab-a-saved.pdf");
        dialogs.NextSave = savedA;
        await Command(window, ApplicationCommands.Save);
        Check(!tabA.Session.IsDirty && tabA.Header.Contains("tab-a-saved.pdf") && !tabA.Header.StartsWith("* "),
            "Save As updates tab filename and removes dirty marker");
        Check(Control<ListBox>(window, "RecentList").Items.Cast<RecentDocument>().Any(r => r.FullPath == savedA),
            "Successful Save As adds the resulting file to recent history");

        await Add(window, "A CLOSE TEST");
        await Select(window, tabB);
        await Add(window, "B CLOSE TEST");
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.No);
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Cancel);
        var closeAccepted = true;
        await window.RunUiAsync(async () => { closeAccepted = await window.CloseAllAsync(); });
        Check(!closeAccepted && window.Documents.Count == 2 && tabA.Session.IsDirty && tabB.Session.IsDirty,
            "Cancel during window-close preflight preserves every open document");
        Check(recovery.Scan().Entries.Count == 0, "Canceled window close keeps both recovery leases live");
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Yes);
        dialogs.Overwrite = false;
        await window.RunUiAsync(async () => { closeAccepted = await window.CloseAllAsync(); });
        Check(!closeAccepted && window.Documents.Count == 2, "Declined save overwrite prevents closing the workspace");
        dialogs.Overwrite = true;
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.Yes);
        dialogs.UnsavedResponses.Enqueue(MessageBoxResult.No);
        await window.RunUiAsync(async () => { closeAccepted = await window.CloseAllAsync(); });
        Check(closeAccepted && window.Documents.Count == 0 && window.CurrentSession is null, "Approved save/discard decisions close all tabs");
        Check(Control<ScrollViewer>(window, "StartScreen").Visibility == Visibility.Visible &&
            Control<Image>(window, "PageImage").Source is null, "Closing last tab returns to start screen and clears stale pixels");
        Check(RecoveryStore.Fingerprint(a) == aHash && RecoveryStore.Fingerprint(b) == bHash, "All multi-document operations preserve original fixture bytes");
        window.SetTheme(ShellTheme.Light);

        var nextDialogs = new TestDialogs();
        var next = new MainWindow(worker, recovery, nextDialogs, () => contrast);
        Check(next.PreferredTheme == ShellTheme.Light && Control<ListBox>(next, "RecentList").Items.Count >= 3,
            "Theme preference and recent history survive a new shell instance");
        var recent = Control<ListBox>(next, "RecentList").Items.Cast<RecentDocument>().First(r => r.FullPath == savedA);
        Control<ListBox>(next, "RecentList").SelectedItem = recent;
        await next.RunUiAsync(next.OpenRecentAsync);
        Check(next.Documents.Count == 1 && next.CurrentSession!.OriginalPath == savedA,
            "Start-screen recent entry opens the actual saved PDF");
        await next.RunUiAsync(() => next.OpenDroppedPathsAsync(new[] { "ignored.txt", b, a, a, Path.Combine(output, "unavailable.pdf") }));
        Check(next.Documents.Count == 3 && next.ActiveTab!.Session.OriginalPath == a,
            "Drop path batch opens PDFs, ignores non-PDFs, deduplicates and retains successes after a failed file");
        Check(nextDialogs.Messages.Count == 1, "Failed file in a dropped batch is reported without removing successful tabs");
        Check(Control<TabControl>(next, "DocumentTabs").SelectedItem == next.ActiveTab &&
            Control<ListBox>(next, "OpenDocumentList").SelectedItem == next.ActiveTab, "Tab and navigation selections stay synchronized");

        Control<ToggleButton>(next, "NavigationToggle").IsChecked = false;
        Control<ToggleButton>(next, "PropertiesToggle").IsChecked = false;
        next.ApplyPanelVisibility();
        Check(Control<Border>(next, "NavigationPanel").Visibility == Visibility.Collapsed &&
            Control<Border>(next, "PropertiesPanel").Visibility == Visibility.Collapsed, "Both side panels collapse to free the canvas");
        Check(Control<Image>(next, "PageImage").Source is not null && next.Documents.Count == 3,
            "Collapsing panels preserves active rendering and open sessions");
        await Capture(next, "compact-light.png", 760, 600);
        Control<ToggleButton>(next, "NavigationToggle").IsChecked = true;
        Control<ToggleButton>(next, "PropertiesToggle").IsChecked = true;
        next.ApplyPanelVisibility();
        Check(Control<Border>(next, "PropertiesPanel").Visibility == Visibility.Visible, "Side panels can be restored");
        next.SetTheme(ShellTheme.Light);
        await Capture(next, "documents-light.png");
        var rendered = Control<Image>(next, "PageImage").Source;
        var selectedPages = PrintPipeline.ResolvePages(12, 4, PageRangeSelection.UserPages, new PageRange(3, 6));
        Check(selectedPages.SequenceEqual(new[] { 2, 3, 4, 5 }) &&
            PrintPipeline.ResolvePages(12, 4, PageRangeSelection.CurrentPage, new PageRange(1, 1)).SequenceEqual(new[] { 4 }),
            "Print range helper maps native 1-based range and current-page selections to PDF page indexes");
        var rasterSize = PrintLayout.GetRasterSize(612, 792);
        Check(rasterSize.Width == 2550 && rasterSize.Height == 3300 && (long)rasterSize.Width * rasterSize.Height <= 40_000_000,
            "Print raster dimensions use 300 DPI within the bounded pixel budget");
        Check(PrintPipeline.ResolvePages(20, 0, PageRangeSelection.AllPages, new PageRange(1, 20)).Length == 20,
            "Print selections are no longer limited to ten pages");
        next.SetTheme(ShellTheme.Graphite);
        await Capture(next, "documents-graphite.png");
        Check(ReferenceEquals(rendered, Control<Image>(next, "PageImage").Source) && next.PreferredTheme == ShellTheme.Graphite,
            "Graphite theme applies without tinting or rewriting rendered PDF pixels");
        next.SetTheme(ShellTheme.Dark);
        await Capture(next, "documents-dark.png");
        Check(ReferenceEquals(rendered, Control<Image>(next, "PageImage").Source), "Theme changes do not tint or rewrite rendered PDF pixels");
        var sample = new byte[4];
        ((BitmapSource)rendered!).CopyPixels(new Int32Rect(0, 0, 1, 1), sample, 4, 0);
        Check(sample[0] == 255 && sample[1] == 255 && sample[2] == 255, "White PDF page remains white in dark theme");
        Check(((SolidColorBrush)next.Resources["AppBackground"]).Color == (Color)ColorConverter.ConvertFromString("#171928") &&
            ((SolidColorBrush)next.Resources["AccentBrush"]).Color == (Color)ColorConverter.ConvertFromString("#9279FB") &&
            ((SolidColorBrush)next.Resources["PrimaryBrush"]).Color == (Color)ColorConverter.ConvertFromString("#1FA6A8") &&
            ((SolidColorBrush)next.Resources["PrimaryTextBrush"]).Color == (Color)ColorConverter.ConvertFromString("#171928"),
            "Dark shell uses authoritative Ink, reverse purple selection and Ink-on-Teal primary tokens");
        contrast = true;
        next.ApplyEffectiveTheme();
        Check(Equals(next.Resources["AppBackground"], SystemColors.WindowBrush) &&
            Equals(next.Resources["TextBrush"], SystemColors.WindowTextBrush), "High-contrast override uses Windows system color brushes");
        Check(!Control<ComboBox>(next, "ThemeChoice").IsEnabled && Control<TextBlock>(next, "ContrastLabel").Visibility == Visibility.Visible,
            "High contrast overrides theme selector and communicates current mode");
        contrast = false;
        next.ApplyEffectiveTheme();
        Check(next.PreferredTheme == ShellTheme.Dark && Control<ComboBox>(next, "ThemeChoice").IsEnabled,
            "Leaving high contrast restores user's chosen theme");
        var addButton = Control<Button>(next, "AddButton");
        Check(AutomationProperties.GetName(addButton) == "Add text to current page" && addButton.FocusVisualStyle is not null && addButton.MinHeight >= 44 && addButton.FontSize >= 16,
            "Add-text control exposes current-page scope, 16px text, a 44-DIP target and keyboard focus visual");
        var combineButton = Control<Button>(next, "CombineButton");
        Check(AutomationProperties.GetName(combineButton) == "Combine PDF files" && combineButton.MinHeight >= 44 &&
            Control<Button>(next, "StartCombineButton").IsEnabled,
            "Toolbar and home screen expose the working combine workflow with accessible target sizing");
        ApplicationCommands.Find.Execute(null, next);
        Check(ReferenceEquals(FocusManager.GetFocusedElement(next), Control<TextBox>(next, "SearchBox")) &&
            next.InputBindings.Cast<KeyBinding>().Any(k => k.Key == Key.F && k.Modifiers == ModifierKeys.Control),
            "Ctrl+F routes logical focus to document search; physical keyboard focus requires a visible window");
        Check(FrameworkElementAutomationPeer.CreatePeerForElement(Control<TabControl>(next, "DocumentTabs"))!.GetAutomationControlType() == AutomationControlType.Tab,
            "Document strip exposes native tab semantics to accessibility tools");
        Check(next.InputBindings.Cast<KeyBinding>().Any(k => k.Key == Key.W && k.Modifiers == ModifierKeys.Control) &&
            next.InputBindings.Cast<KeyBinding>().Any(k => k.Key == Key.Tab && k.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)),
            "Close and reverse-tab shortcuts are available");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = next.RunUiAsync(() => release.Task, "Opening PDF…");
        Check(next.IsBusy && !Control<TabControl>(next, "DocumentTabs").IsEnabled &&
            Control<ProgressBar>(next, "BusyProgress").Visibility == Visibility.Visible &&
            Control<TextBlock>(next, "OperationStatus").Text == "Opening PDF…",
            "Active operation status and progress appear while tab switching is disabled");
        release.SetResult();
        await operation;
        Check(!next.IsBusy && Control<ProgressBar>(next, "BusyProgress").Visibility == Visibility.Collapsed &&
            Control<TextBlock>(next, "OperationStatus").Text == "Ready", "Operation completion restores ready state");

        // Recovery adds every accepted session instead of replacing the currently open one.
        var recoveringA = next.ActiveTab!;
        await Add(next, "MULTI RECOVERY A");
        var item = (TabItem)Control<TabControl>(next, "DocumentTabs").ItemContainerGenerator.ContainerFromItem(recoveringA);
        Check(FrameworkElementAutomationPeer.CreatePeerForElement(item)!.GetName().Contains("unsaved changes"),
            "Dirty document tabs expose explicit unsaved state to screen readers");
        var recoveringB = next.Documents.First(t => t.Session.OriginalPath == b);
        await Select(next, recoveringB);
        await Add(next, "MULTI RECOVERY B");
        foreach (var tab in next.Documents) tab.Session.Dispose();
        var recoveryDialogs = new TestDialogs { Recover = MessageBoxResult.Yes };
        var restored = new MainWindow(worker, recovery, recoveryDialogs);
        await restored.RunUiAsync(restored.OfferRecoveryAsync);
        Check(restored.Documents.Count == 3 && recoveryDialogs.RecoveryQuestions.Count == 3,
            "Startup recovery accepts multiple sessions into separate tabs");
        Check((await restored.Documents.First(t => t.Session.OriginalPath == a).Session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("MULTI RECOVERY A") &&
            (await restored.Documents.First(t => t.Session.OriginalPath == b).Session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("MULTI RECOVERY B"),
            "Multiple recovered tabs retain their own unsaved PDF edits");
        await restored.RunUiAsync(async () => { await restored.CloseAllAsync(); });
        Check(restored.Documents.Count == 0, "Explicit discard closes all recovered tabs");

        restored.ClearRecentHistory();
        var afterClear = new MainWindow(worker, recovery, new TestDialogs());
        Check(Control<ListBox>(afterClear, "RecentList").Items.Count == 0 &&
            Control<TextBlock>(afterClear, "RecentEmpty").Visibility == Visibility.Visible, "Clear history persists and restores recent-file empty state");
        var brokenSettings = Path.Combine(output, "broken-settings.json");
        File.WriteAllText(brokenSettings, "broken settings");
        var fallback = new ShellPreferences(brokenSettings);
        Check(fallback.Theme == ShellTheme.System && fallback.Error is not null, "Malformed shell settings fall back to System without blocking startup");
        Check(RecoveryStore.Fingerprint(a) == aHash && RecoveryStore.Fingerprint(b) == bHash, "Shell recovery/theme/recent tests leave corpus fixtures unchanged");
        File.WriteAllText(Path.Combine(output, "shell-results.json"),
            JsonSerializer.Serialize(new { Passed = checks, Scope = "Real WPF controls, document workers and rendered themes. Native dialogs use injected choices; Windows high contrast uses an injected signal with actual system brushes." },
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} milestone 5 shell checks. Evidence: {output}");
    }
}

