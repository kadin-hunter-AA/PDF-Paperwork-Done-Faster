using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Automation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Printing;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

public partial class MainWindow : Window
{
    readonly string workerExecutable;
    readonly RecoveryStore recovery;
    readonly IDocumentDialogs dialogs;
    readonly ShellPreferences preferences;
    readonly Func<bool> highContrast;
    readonly ObservableCollection<OpenDocumentTab> tabs = new();
    readonly List<Image> pageImages = new();
    readonly List<Border> pageCards = new();
    readonly List<Canvas> selectionLayers = new();
    readonly DispatcherTimer renderDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    CancellationTokenSource? renderCancellation;
    CancellationTokenSource? thumbnailCancellation;
    int visiblePage;
    int selectionAnchor = -1, selectionEnd = -1, selectionPage = -1;
    string selectedText = "";
    OpenDocumentTab? active;
    bool busy, closingAccepted, syncingSelection, initialized, syncingTheme;
    bool responsivePropertiesHidden, responsiveNavigationHidden;
    bool homePropertiesHidden, homeNavigationHidden;
    int? zoomRestorePage, navigationTarget;
    int navigationVersion;
    double navigationWidth = 240, propertiesWidth = 270;
    string? notice;

    public MainWindow() : this(
        Path.Combine(AppContext.BaseDirectory, "worker", "App.Worker.exe"),
        new RecoveryStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HunterOps", "PdfEditor", "Recovery")))
    {
        // PDFs passed on the command line (Explorer "Open with") open after the recovery check.
        startupFiles = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => string.Equals(Path.GetExtension(a), ".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a)).ToArray();
    }

    string[] startupFiles = [];

    internal MainWindow(string workerExecutable, RecoveryStore recovery, IDocumentDialogs? dialogs = null, Func<bool>? highContrast = null)
    {
        this.workerExecutable = workerExecutable;
        this.recovery = recovery;
        this.highContrast = highContrast ?? (() => SystemParameters.HighContrast);
        preferences = new ShellPreferences(Path.Combine(Path.GetDirectoryName(recovery.Root)!, "shell-settings.json"));
        HistoryPolicy.Default = new HistoryPolicy(preferences.HistorySteps);
        InitializeComponent();
        this.dialogs = dialogs ?? new DocumentDialogs(this);
        DocumentTabs.ItemsSource = tabs;
        OpenDocumentList.ItemsSource = tabs;
        renderDebounce.Tick += async (_, _) => { renderDebounce.Stop(); await RenderViewportAsync(); };
        ThemeChoice.ItemsSource = Enum.GetValues<ShellTheme>();
        ThemeChoice.SelectedItem = preferences.Theme;
        Bind(ApplicationCommands.Open, OpenAsync, () => true);
        Bind(ApplicationCommands.Save, async () => { await SaveAsync(false); }, () => active is not null);
        Bind(ApplicationCommands.SaveAs, async () => { await SaveAsync(true); }, () => active is not null);
        Bind(ApplicationCommands.Undo, async () => { if (active is not null) await active.Session.UndoAsync(); }, () => CurrentSession?.CanUndo == true);
        Bind(ApplicationCommands.Redo, async () => { if (active is not null) await active.Session.RedoAsync(); }, () => CurrentSession?.CanRedo == true);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find,
            (_, e) => { FindBar.Visibility = Visibility.Visible; UpdateLayout(); FocusManager.SetFocusedElement(this, SearchBox); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; },
            (_, e) => e.CanExecute = active is not null && !busy));
        Bind(ApplicationCommands.Close, async () => { await LeaveCurrentAsync(); }, () => active is not null);
        Bind(ShellCommands.NextTab, () => CycleTabAsync(1), () => tabs.Count > 1);
        Bind(ShellCommands.PreviousTab, () => CycleTabAsync(-1), () => tabs.Count > 1);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { if (selectedText.Length > 0) Clipboard.SetText(selectedText); e.Handled = true; }, (_, e) => e.CanExecute = selectedText.Length > 0));
        foreach (var name in new[] { "OpenButton", "SaveButton", "SaveAsButton", "UndoButton", "RedoButton", "StartOpenButton" })
            ((Button)FindName(name)).CommandTarget = this;
        OpenButton.IsEnabled = true;
        StartOpenButton.IsEnabled = true;
        SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Loaded += async (_, _) =>
        {
            await RunUiAsync(OfferRecoveryAsync, "Checking recovery…");
            if (startupFiles.Length > 0) await RunUiAsync(() => OpenDroppedPathsAsync(startupFiles), "Opening PDF…");
        };
        Closing += Window_Closing;
        PreviewKeyDown += PageTool_PreviewKeyDown;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            foreach (var tab in tabs) tab.Session.Dispose();
        };
        InitializeViewer();
        initialized = true;
        ApplyEffectiveTheme();
        RefreshRecent();
        UpdateShell();
        if (preferences.Error is not null) Status.Text = preferences.Error;
    }

    internal DocumentSession? CurrentSession => active?.Session;
    internal OpenDocumentTab? ActiveTab => active;
    internal IReadOnlyList<OpenDocumentTab> Documents => tabs;
    internal bool IsBusy => busy;
    internal ShellTheme PreferredTheme => preferences.Theme;
    internal Func<string, Task>? DirectEditCheckpointForTest { get; set; }

    Task DirectEditCheckpointAsync(string checkpoint) =>
        DirectEditCheckpointForTest?.Invoke(checkpoint) ?? Task.CompletedTask;

    void Bind(RoutedUICommand command, Func<Task> action, Func<bool> allowed) =>
        CommandBindings.Add(new CommandBinding(command,
            async (_, _) => await RunUiAsync(action, command.Text + "…"),
            (_, e) => e.CanExecute = !busy && allowed()));

    internal async Task RunUiAsync(Func<Task> action, string operation = "Working…")
    {
        if (busy) return;
        busy = true;
        notice = null;
        OperationStatus.Text = operation;
        BusyProgress.Visibility = Visibility.Visible;
        UpdateShell();
        try
        {
            await action();
            if (active is not null) await RefreshAsync();
            else Status.Text = notice ?? "Open a PDF. Original files are preserved by default.";
        }
        catch (Exception e)
        {
            Status.Text = e.Message;
            dialogs.Information(e.Message, e is SaveCompletedException ? "PDF saved — recovery needs attention" : "Document operation");
        }
        finally
        {
            busy = false;
            OperationStatus.Text = "Ready";
            BusyProgress.Visibility = Visibility.Collapsed;
            UpdateShell();
        }
    }

    void UpdateShell()
    {
        foreach (var tab in tabs) tab.Refresh();
        CommandBar.IsEnabled = !busy;
        OpenButton.IsEnabled = StartOpenButton.IsEnabled = !busy;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = active is not null && !busy;
        UndoButton.IsEnabled = !busy && CurrentSession?.CanUndo == true;
        RedoButton.IsEnabled = !busy && CurrentSession?.CanRedo == true;
        DocumentTabs.IsEnabled = !busy;
        OpenDocumentList.IsEnabled = !busy;
        TextEditorPanel.IsEnabled = active is not null && !busy;
        EditExistingTextButton.IsEnabled = active is not null && !busy;
        RotateButton.IsEnabled = RotateAllButton.IsEnabled = active is not null && !busy;
        LayoutChoice.IsEnabled = !busy;
        MarkupButton.IsEnabled = active is not null && !busy;
        FillFormButton.IsEnabled = active is not null && !busy && AdvancedEngineInstalled;
        OcrButton.IsEnabled = active is not null && !busy && AdvancedEngineInstalled &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "advanced", "tessdata", "eng.traineddata"));
        SignCopyButton.IsEnabled = active is not null && !busy && AdvancedEngineInstalled;
        AddSignatureButton.IsEnabled = AddInitialsButton.IsEnabled = SavedSignatureButton.IsEnabled = DateStampButton.IsEnabled =
            CheckStampButton.IsEnabled = CrossStampButton.IsEnabled = active is not null && !busy;
        RedactionButton.IsEnabled = active is not null && !busy && AdvancedEngineInstalled;
        ImageEditorButton.IsEnabled = active is not null && !busy;
        PageOrganizerButton.IsEnabled = active is not null && !busy;
        EditModeToggle.IsEnabled = active is not null && !busy;
        ToolButtons.IsEnabled = active is not null && !busy;
        ModeMenus.IsEnabled = active is not null && !busy;
        PrintButton.IsEnabled = active is not null && !busy;
        CompactHistoryButton.IsEnabled = active is not null && !busy && active.Session.HistoryCount > 2;
        StartCombineButton.IsEnabled = !busy;
        ZoomInButton.IsEnabled = active is not null && !busy && active.Zoom < 400;
        ZoomOutButton.IsEnabled = active is not null && !busy && active.Zoom > 25;
        FitWidthButton.IsEnabled = FitPageButton.IsEnabled = active is not null && !busy;
        PageNavigation.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        if (active is null) FindBar.Visibility = Visibility.Collapsed;
        SaveIndicator.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        SaveState.Text = active?.Session.IsDirty == true ? "Unsaved changes" : "All changes saved";
        SaveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active?.Session.IsDirty == true ? "UnsavedBrush" : "SavedBrush");
        ThemeChoice.IsEnabled = !busy && !highContrast();
        StartScreen.Visibility = active is null ? Visibility.Visible : Visibility.Collapsed;
        DocumentScroll.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        DocumentView.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyHomePanelVisibility();
        ThumbnailList.ItemsSource = active?.Pages;
        BookmarkList.ItemsSource = active?.Bookmarks;
        ZoomValue.Text = active is null ? "—" : active.Zoom + "%";
        Title = active is null ? "PDF - Paperwork Done Faster" : active.Header + " — PDF - Paperwork Done Faster";
        if (active is null)
        {
            PageImage.Source = null;
            PropertiesFile.Text = "Open a document to inspect it.";
            PropertyHistory.Text = "";
            PropertyLocation.Text = PropertyPages.Text = PropertyStatus.Text = PropertySignature.Text = PropertySize.Text = PropertyRotation.Text = "";
            PropertyPageHeader.Text = "Page";
            _ = RefreshSidePanelAsync();
        }
        CommandManager.InvalidateRequerySuggested();
    }

    void SaveDraft()
    {
        if (active is null) return;
        active.DraftText = NewText.Text;
        active.TextX = TextX.Text;
        active.TextY = TextY.Text;
    }

    void SelectCore(OpenDocumentTab? tab)
    {
        SaveDraft();
        selectionAnchor = selectionEnd = selectionPage = -1; selectedText = "";
        ResetPageToolState();
        active = tab;
        FieldsHost.Children.Clear(); fieldEditors.Clear(); loadedFields = null; ApplyFieldsButton.IsEnabled = false;
        FieldsStatus.Text = tab is null ? "Open a PDF form to fill its fields here." : "Choose Reload to read this document's form fields.";
        if (tab is not null && FieldsTab.IsSelected) Dispatcher.BeginInvoke(new Action(() => _ = RunUiAsync(LoadFieldsAsync, "Reading form fields…")), System.Windows.Threading.DispatcherPriority.Background);
        thumbnailCancellation?.Cancel();
        syncingSelection = true;
        try { DocumentTabs.SelectedItem = tab; OpenDocumentList.SelectedItem = tab; }
        finally { syncingSelection = false; }
        NewText.Text = tab?.DraftText ?? "";
        TextX.Text = tab?.TextX ?? "72";
        TextY.Text = tab?.TextY ?? "120";
        PageImage.Source = null;
        renderCancellation?.Cancel();
        if (active is not null)
        {
            BuildPageCards(active);
            var selectedTab = active;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (active == selectedTab) GoToPage(selectedTab.CurrentPage); }), DispatcherPriority.Loaded);
        }
        UpdateShell();
    }

    internal Task SelectTabAsync(OpenDocumentTab tab)
    {
        if (!tabs.Contains(tab)) throw new InvalidOperationException("This document is no longer open.");
        if (tab != active) SelectCore(tab);
        return Task.CompletedTask;
    }

    Task CycleTabAsync(int direction)
    {
        if (tabs.Count == 0) return Task.CompletedTask;
        var index = active is null ? 0 : tabs.IndexOf(active);
        return SelectTabAsync(tabs[(index + direction + tabs.Count) % tabs.Count]);
    }

    async void DocumentSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || syncingSelection || busy) return;
        var selected = sender is TabControl ? DocumentTabs.SelectedItem : OpenDocumentList.SelectedItem;
        if (selected is OpenDocumentTab tab && tab != active)
            await RunUiAsync(() => SelectTabAsync(tab), "Switching document…");
    }

    internal async Task OpenAsync()
    {
        var path = dialogs.OpenPath();
        if (path is not null) await OpenPathAsync(path);
    }

    internal async Task OpenPathAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a PDF file.");
        var existing = tabs.FirstOrDefault(t => SamePath(path, t.Session.OriginalPath) || SamePath(path, t.Session.LastSavePath));
        if (existing is not null)
        {
            await SelectTabAsync(existing);
            Remember(path);
            return;
        }
        DocumentSession session;
        try { session = await DocumentSession.OpenAsync(workerExecutable, recovery, path); }
        catch (DocumentPasswordRequiredException)
        {
            var password = dialogs.Password("If this PDF requires a password, enter it to open the document.");
            if (password is null) return;
            session = await DocumentSession.OpenAsync(workerExecutable, recovery, path, password);
        }
        var tab = new OpenDocumentTab(session);
        tabs.Add(tab);
        SelectCore(tab);
        Remember(path);
    }

    internal Task<bool> SaveAsync(bool forceSaveAs) =>
        active is null ? Task.FromResult(false) : SaveTabAsync(active, forceSaveAs);

    async Task<bool> SaveTabAsync(OpenDocumentTab tab, bool forceSaveAs)
    {
        var session = tab.Session;
        var destination = session.LastSavePath;
        if (forceSaveAs || SamePath(destination, session.OriginalPath))
        {
            var path = dialogs.SavePath(session);
            if (path is null) return false;
            destination = path;
        }
        else if (File.Exists(destination) && !dialogs.ConfirmOverwrite()) return false;
        // Another open tab must never be silently overwritten by this tab.
        if (tabs.Any(t => t != tab && (SamePath(destination, t.Session.OriginalPath) || SamePath(destination, t.Session.LastSavePath))))
            throw new DocumentConflictException("That PDF is open in another tab. Choose a different destination.");
        var result = await session.SaveAsync(destination, overwrite: File.Exists(destination));
        tab.Refresh();
        Remember(destination);
        notice = Path.GetFileName(destination) + (result.Backup is null ? " saved." : " saved; previous version retained as a backup.");
        return true;
    }

    async Task<bool> ApproveCloseAsync(OpenDocumentTab tab)
    {
        if (!tab.Session.IsDirty) return true;
        var choice = dialogs.UnsavedChoice(tab.Session);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes && !await SaveTabAsync(tab, false)) return false;
        return true;
    }

    internal Task<bool> LeaveCurrentAsync() => active is null ? Task.FromResult(true) : CloseTabAsync(active);

    internal async Task<bool> CloseTabAsync(OpenDocumentTab tab)
    {
        if (!tabs.Contains(tab)) return true;
        if (!await ApproveCloseAsync(tab)) return false;
        await tab.Session.CloseAsync(discardUnsaved: true);
        RemoveClosedTab(tab);
        return true;
    }

    void RemoveClosedTab(OpenDocumentTab tab)
    {
        var index = tabs.IndexOf(tab);
        var wasActive = tab == active;
        if (wasActive) SaveDraft();
        syncingSelection = true;
        try { tabs.Remove(tab); }
        finally { syncingSelection = false; }
        if (wasActive) SelectCore(tabs.Count == 0 ? null : tabs[Math.Min(index, tabs.Count - 1)]);
        else
        {
            syncingSelection = true;
            try { DocumentTabs.SelectedItem = active; OpenDocumentList.SelectedItem = active; }
            finally { syncingSelection = false; }
        }
        UpdateShell();
    }

    internal async Task<bool> CloseAllAsync()
    {
        var closing = tabs.ToArray();
        // No tab closes until every unsaved decision has been approved.
        foreach (var tab in closing) if (!await ApproveCloseAsync(tab)) return false;
        foreach (var tab in closing)
        {
            await tab.Session.CloseAsync(discardUnsaved: true);
            RemoveClosedTab(tab);
        }
        return true;
    }

    async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: OpenDocumentTab tab })
            await RunUiAsync(async () => { await CloseTabAsync(tab); }, "Closing document…");
    }

    async Task<bool> AllowEditingAsync()
    {
        if (active is null) return false;
        if (active.Session.SignatureCount == 0 || active.Session.SignedCopyAuthorized) return true;
        if (!dialogs.ConfirmSignedCopy()) return false;
        await active.Session.AuthorizeSignedCopyAsync();
        return true;
    }

    async Task<bool> AllowEditingAsync(OpenDocumentTab expectedTab)
    {
        if (active != expectedTab) return false;
        var session = expectedTab.Session;
        if (session.SignatureCount == 0 || session.SignedCopyAuthorized) return true;
        if (!dialogs.ConfirmSignedCopy()) return false;
        await session.AuthorizeSignedCopyAsync();
        return true;
    }

    async void Add_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (active is null || string.IsNullOrWhiteSpace(NewText.Text)) return;
        if (NewText.Text.Any(c => c < 32 || c > 126)) throw new InvalidOperationException("This text tool currently supports basic Latin characters only.");
        if (!double.TryParse(TextX.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var x) ||
            !double.TryParse(TextY.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var y) ||
            !double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("Enter valid text positions in PDF points.");
        if (!await AllowEditingAsync()) return;
        await active.Session.AddTextAsync(active.CurrentPage, NewText.Text, x, y);
    }, "Adding text…");

    async void EditExistingText_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        var editor = new ObjectEditorWindow(tab.Session, tab.CurrentPage, AdvancedEngineInstalled ? AdvancedEngine : null) { Owner = this };
        editor.ShowDialog();
    }, "Loading text objects…");

    async void Markup_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        object[] rectangles = [];
        if (selectionPage == tab.CurrentPage && selectionAnchor >= 0 && selectionEnd >= 0 &&
            tab.Pages[tab.CurrentPage].Characters is { } characters)
        {
            var from = Math.Min(selectionAnchor, selectionEnd);
            var to = Math.Max(selectionAnchor, selectionEnd);
            var selected = characters.Where(c => c.Index >= from && c.Index <= to && c.HasBounds &&
                c.Right > c.Left && c.Top > c.Bottom && !string.IsNullOrWhiteSpace(c.Text)).ToArray();
            if (selected.Length > 100) throw new InvalidOperationException("Select up to 100 characters per markup operation, or enter a page rectangle.");
            rectangles = selected.Select(c => (object)new { c.Left, c.Bottom, c.Right, c.Top }).ToArray();
        }
        var editor = new MarkupWindow(tab.Session, tab.CurrentPage, rectangles) { Owner = this };
        editor.ShowDialog();
    }, "Opening annotations…");

    async void ImageEditor_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        new ImageEditorWindow(tab.Session, tab.CurrentPage) { Owner = this }.ShowDialog();
    }, "Opening image editor…");

    static AdvancedEngineLocation AdvancedEngine => new(
        Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"),
        Path.Combine(AppContext.BaseDirectory, "advanced", "worker.py"));
    static bool AdvancedEngineInstalled => File.Exists(AdvancedEngine.PythonExecutable) && File.Exists(AdvancedEngine.WorkerScript);

    async void FillForm_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        new FormFillWindow(tab.Session, tab.CurrentPage, AdvancedEngine) { Owner = this }.ShowDialog();
    }, "Opening existing form fields…");

    async void Ocr_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null) return;
        var editor = CreateOcrWindow(tab);
        if (editor.ShowDialog() == true)
        {
            tab.Search = null;
            notice = (string.IsNullOrWhiteSpace(editor.Summary) ? "Text added locally." : editor.Summary) +
                " Check recognition using Search or text selection; Undo removes the layer.";
        }
        await Task.CompletedTask;
    }, "Opening local OCR…");

    async void SignCopy_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null) return;
        var editor = new SigningWindow(tab.Session, AdvancedEngine) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result)
        {
            await OpenPathAsync(result.Destination);
            notice = "Signed copy created and cryptographic integrity checked. Certificate trust, revocation and trusted time were not established.";
            if (result.TemporaryCandidateRetained) dialogs.Information("The signed PDF was created, but temporary signing files could not all be removed.", "Signed copy created");
        }
    }, "Creating a certificate-signed copy…");

    async void Redaction_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null) return;
        var editor = new RedactionWindow(tab.Session, tab.CurrentPage, AdvancedEngine) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result)
        {
            await OpenPathAsync(result.Destination);
            notice = "Redacted copy created from the reviewed candidate. Original files and their recovery history remain intact.";
        }
    }, "Opening permanent redaction preview…");

    async void PageOrganizer_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var tab = active;
        if (tab is null || !await AllowEditingAsync()) return;
        var organizer = new PageOrganizerWindow(tab.Session, tab.CurrentPage) { Owner = this };
        organizer.ShowDialog();
        tab.CurrentPage = Math.Clamp(organizer.SelectedPage, 0, tab.Session.PageCount - 1);
        tab.Search = null;
    }, "Opening page organizer…");

    async void Rotate_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (!await AllowEditingAsync() || active is null) return;
        var page = active.CurrentPage;
        var info = await active.Session.ExtractAsync(page);
        await active.Session.RotatePageAsync(page, (info.GetProperty("Rotation").GetInt32() + 1) % 4);
    }, "Rotating page…");

    async void Recover_Click(object sender, RoutedEventArgs e) => await RunUiAsync(OfferRecoveryAsync, "Checking recovery…");

    async void Combine_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var window = new CombineWindow { Owner = this };
        if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.OutputPath)) return;
        var outputPath = window.OutputPath;
        await RunUiAsync(() => OpenPathAsync(outputPath), "Opening combined PDF…");
    }

    async void Print_Click(object sender, RoutedEventArgs e)
    {
        var tab = active;
        if (busy || tab is null || tab.Pages.Count == 0) return;
        var dialog = new PrintDialog
        {
            MinPage = 1,
            MaxPage = (uint)tab.Pages.Count,
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            SelectedPagesEnabled = false,
            PageRangeSelection = PageRangeSelection.AllPages
        };
        bool printAccepted;
        try { printAccepted = dialog.ShowDialog() == true; }
        catch (Exception error)
        {
            dialogs.Information(error.Message, "Printer unavailable");
            return;
        }
        if (!printAccepted) return;

        int[] pages;
        try { pages = PrintPipeline.ResolvePages(tab.Pages.Count, tab.CurrentPage, dialog.PageRangeSelection, dialog.PageRange); }
        catch (InvalidOperationException error)
        {
            dialogs.Information(error.Message, "Print range unavailable");
            return;
        }
        var queue = dialog.PrintQueue;
        if (queue is null)
        {
            dialogs.Information("The selected printer is unavailable.", "Printer unavailable");
            return;
        }
        var sizes = tab.Pages.Select(page => new PrintPageSize(page.Width, page.Height)).ToArray();
        var preview = new PrintPreviewWindow(tab.Session, pages, sizes, queue, dialog.PrintTicket,
            Math.Max(1, dialog.PrintableAreaWidth), Math.Max(1, dialog.PrintableAreaHeight)) { Owner = this };
        // Vector output may leave out form field values, so pages with form fields default to image output.
        preview.PrintAsImage = await PagesHaveFormFieldsAsync(tab.Session, pages);
        await RunUiAsync(() =>
        {
            preview.ShowDialog();
            return Task.CompletedTask;
        }, "Opening print preview…");
    }

    internal OcrWindow CreateOcrWindow(OpenDocumentTab tab) => new(tab.Session, AdvancedEngine,
        Path.Combine(AppContext.BaseDirectory, "advanced", "tessdata", "eng.traineddata"),
        Path.Combine(Path.GetDirectoryName(workerExecutable)!, "pdfium.dll"), tab.CurrentPage) { Owner = this };

    /// <summary>True when any of the first 50 pages to print has a form widget (or the check itself fails).</summary>
    internal static async Task<bool> PagesHaveFormFieldsAsync(DocumentSession session, IEnumerable<int> pages)
    {
        try
        {
            foreach (var page in pages.Take(50))
                if ((await session.ExtractAsync(page)).GetProperty("Annotations").EnumerateArray().Any(a => a.GetProperty("Subtype").GetInt32() == 20))
                    return true;
            return false;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException) { return true; }
    }

    internal async Task OfferRecoveryAsync()
    {
        var scan = recovery.Scan();
        if (scan.Errors.Count > 0) dialogs.Information(string.Join(Environment.NewLine, scan.Errors), "Recovery files retained");
        foreach (var item in scan.Entries)
        {
            var choice = dialogs.RecoveryChoice(item);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice != MessageBoxResult.Yes) continue;
            var password = item.PasswordRequired ? dialogs.Password("Enter this PDF's password to recover it. Passwords are not stored in recovery files.") : null;
            if (item.PasswordRequired && password is null) continue;
            var session = await DocumentSession.RecoverAsync(workerExecutable, recovery, item.Id, password);
            var tab = new OpenDocumentTab(session, recovered: true);
            tabs.Add(tab);
            SelectCore(tab);
        }
    }

    async Task RefreshAsync()
    {
        var tab = active;
        if (tab is null) return;
        var revision = tab.Session.Revision;
        if (tab.Pages.Count != tab.Session.PageCount || tab.PageInfoRevision != revision)
        {
            var metadata = await tab.Session.PageInfoAsync();
            tab.Pages.Clear();
            foreach (var page in metadata.GetProperty("Pages").EnumerateArray())
            {
                var pageWidth = page.GetProperty("Width").GetSingle(); var pageHeight = page.GetProperty("Height").GetSingle();
                if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0) throw new InvalidOperationException("A page has invalid dimensions.");
                tab.Pages.Add(new PageDescriptor(page.GetProperty("Page").GetInt32(), pageWidth, pageHeight, page.GetProperty("Rotation").GetInt32())
                {
                    OriginX = page.TryGetProperty("OriginX", out var originX) && originX.TryGetDouble(out var ox) && double.IsFinite(ox) ? ox : 0,
                    OriginY = page.TryGetProperty("OriginY", out var originY) && originY.TryGetDouble(out var oy) && double.IsFinite(oy) ? oy : 0
                });
            }
            tab.RenderCache.Clear(); tab.RenderOrder.Clear(); tab.ThumbnailOrder.Clear();
            selectionAnchor = selectionEnd = selectionPage = -1; selectedText = "";
            foreach (var layer in selectionLayers) layer.Children.Clear();
            var nav = await tab.Session.BookmarksAsync();
            tab.Bookmarks.Clear();
            foreach (var bookmark in nav.GetProperty("Bookmarks").EnumerateArray())
                if (bookmark.GetProperty("Page").GetInt32() >= 0 && bookmark.GetProperty("Page").GetInt32() < tab.Pages.Count)
                    tab.Bookmarks.Add(new BookmarkDescriptor(bookmark.GetProperty("Title").GetString() ?? "Bookmark", bookmark.GetProperty("Page").GetInt32(), bookmark.GetProperty("Depth").GetInt32()));
            tab.PageInfoRevision = revision;
            BuildPageCards(tab);
        }
        tab.CurrentPage = Math.Clamp(tab.CurrentPage, 0, tab.Pages.Count - 1);
        PageCountLabel.Text = "/ " + tab.Session.PageCount;
        PageNumberBox.Text = (tab.CurrentPage + 1).ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < tab.Pages.Count && i < pageImages.Count; i++) SetPageDisplaySize(tab, i);
        if (pageImages.Count == tab.Pages.Count) await RenderPageAsync(tab, Math.Clamp(tab.CurrentPage, 0, tab.Pages.Count - 1), CancellationToken.None);
        thumbnailCancellation?.Cancel(); thumbnailCancellation = new CancellationTokenSource();
        _ = GenerateThumbnailsSafeAsync(tab, thumbnailCancellation.Token);
        if (tab != active || tab.Session.Revision != revision) return;
        var pageInfo = tab.Pages[tab.CurrentPage];
        var width = pageInfo.Width; var height = pageInfo.Height;
        var displayWidth = width * 96d / 72 * tab.Zoom / 100;
        var displayHeight = height * 96d / 72 * tab.Zoom / 100;
        var signature = SignatureStatus(tab.Session);
        UpdateDocumentStatus(tab);
        PropertiesFile.Text = tab.FileName;
        PropertiesFile.ToolTip = tab.FullPath;
        var pages = tab.Session.PageCount == 1 ? "1 page" : tab.Session.PageCount + " pages";
        PropertyLocation.Text = Path.GetDirectoryName(tab.FullPath) ?? "";
        PropertyLocation.ToolTip = tab.FullPath;
        PropertyPages.Text = tab.Session.PageCount.ToString(CultureInfo.InvariantCulture);
        PropertyStatus.Text = tab.Session.IsDirty ? "Unsaved changes" : "Saved";
        PropertySignature.Text = signature.Length == 0 ? "Not signed" : signature.TrimStart(' ', '—').Trim();
        PropertyPageHeader.Text = "Page " + (tab.CurrentPage + 1);
        PropertySize.Text = $"{width:0.#} × {height:0.#} pt";
        PropertyRotation.Text = pageInfo.Rotation * 90 + "°";
        PropertyHistory.Text = $"{tab.Session.HistoryCount - 1} step{(tab.Session.HistoryCount == 2 ? "" : "s")} · {FormatBytes(tab.Session.HistoryBytes)}";
        _ = RefreshSidePanelAsync();
        if (zoomRestorePage is int restorePage)
        {
            zoomRestorePage = null;
            // A navigation made after the zoom wins over restoring the pre-zoom page.
            var version = navigationVersion;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (tab == active && version == navigationVersion) GoToPage(Math.Clamp(restorePage, 0, tab.Pages.Count - 1)); }), DispatcherPriority.Loaded);
        }
    }

    void BuildPageCards(OpenDocumentTab tab)
    {
        ResetPageToolState();
        PageStack.Children.Clear(); pageImages.Clear(); pageCards.Clear(); selectionLayers.Clear();
        if (tab.Pages.Count == 0) return;
        PageImage.Source = null; SelectionLayer0.Tag = 0; pageImages.Add(PageImage); pageCards.Add(PageCard0); selectionLayers.Add(SelectionLayer0);
        for (var i = 1; i < tab.Pages.Count; i++)
        {
            var image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(image, "Rendered PDF page " + (i + 1));
            var layer = new Canvas { Background = Brushes.Transparent, Tag = i, Focusable = true };
            layer.PreviewMouseLeftButtonDown += Selection_MouseDown; layer.PreviewMouseMove += Selection_MouseMove; layer.PreviewMouseLeftButtonUp += Selection_MouseUp;
            var pageGrid = new Grid(); pageGrid.Children.Add(image); pageGrid.Children.Add(layer);
            var card = new Border { Background = Brushes.White, Margin = new Thickness(0, 0, 0, 16), BorderThickness = new Thickness(1), Child = pageGrid, Tag = i };
            card.SetResourceReference(Border.BorderBrushProperty, "ChromeLineBrush");
            pageImages.Add(image); pageCards.Add(card); selectionLayers.Add(layer);
        }
        ArrangePageCards();
        ThumbnailList.ItemsSource = tab.Pages; BookmarkList.ItemsSource = tab.Bookmarks;
        for (var i = 0; i < tab.Pages.Count; i++) SetPageDisplaySize(tab, i);
        ApplyPageToolCursor();
    }

    void SetPageDisplaySize(OpenDocumentTab tab, int index)
    {
        var page = tab.Pages[index]; var width = page.Width * 96d / 72 * tab.Zoom / 100;
        var height = page.Height * 96d / 72 * tab.Zoom / 100;
        pageImages[index].Width = width; pageImages[index].Height = height;
        selectionLayers[index].Width = width; selectionLayers[index].Height = height;
        if (selectionPage == index && selectionAnchor >= 0 && tab.Pages[index].Characters is not null)
            _ = Dispatcher.BeginInvoke(new Action(() => { if (index < selectionLayers.Count) DrawTextSelection(selectionLayers[index], tab.Pages[index]); }), DispatcherPriority.Loaded);
    }

    async Task RenderPageAsync(OpenDocumentTab tab, int index, CancellationToken cancellation)
    {
        if (index < 0 || index >= tab.Pages.Count || tab != active) return;
        var info = tab.Pages[index];
        var dw = info.Width * 96d / 72 * tab.Zoom / 100;
        var dh = info.Height * 96d / 72 * tab.Zoom / 100;
        var factor = Math.Min(1d, Math.Min(4096d / Math.Max(dw, dh), Math.Sqrt(7_900_000d / Math.Max(1, dw * dh))));
        BitmapSource bitmap;
        if (tab.RenderCache.TryGetValue(index, out var cached))
        {
            bitmap = cached;
            var recent = tab.RenderOrder.Where(page => page != index).ToArray(); tab.RenderOrder.Clear();
            foreach (var page in recent) tab.RenderOrder.Enqueue(page);
            tab.RenderOrder.Enqueue(index);
        }
        else
        {
            var revision = tab.Session.Revision;
            cancellation.ThrowIfCancellationRequested();
            // A bounded render is allowed to finish and is discarded if stale: cancelling an active worker request
            // stops and restarts the isolated worker, which made scrolling and prefetching much slower.
            var result = await tab.Session.RenderAsync(index, Math.Max(1, (int)(dw * factor)), Math.Max(1, (int)(dh * factor)));
            if (tab != active || tab.Session.Revision != revision || cancellation.IsCancellationRequested) return;
            bitmap = BitmapSource.Create(result.GetProperty("Width").GetInt32(), result.GetProperty("Height").GetInt32(), 96, 96, PixelFormats.Bgr32, null,
                Convert.FromBase64String(result.GetProperty("Bgra").GetString()!), result.GetProperty("Stride").GetInt32());
            bitmap.Freeze(); tab.RenderCache[index] = bitmap; tab.RenderOrder.Enqueue(index);
            TrimRenderCache(tab, index);
        }
        if (index < pageImages.Count)
        {
            pageImages[index].Source = bitmap; pageImages[index].Width = dw; pageImages[index].Height = dh;
            if (index < selectionLayers.Count) { selectionLayers[index].Width = dw; selectionLayers[index].Height = dh; }
            if (index == 0) PageImage.Source = bitmap;
        }
    }

    async Task GenerateThumbnailsAsync(OpenDocumentTab tab, CancellationToken cancellation)
    {
        // Generate a small neighborhood as pages are visited; this keeps thumbnail memory bounded for very large PDFs.
        var start = Math.Max(0, tab.CurrentPage - 4); var end = Math.Min(tab.Pages.Count, tab.CurrentPage + 5);
        for (var i = start; i < end; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (tab.Pages[i].Thumbnail is not null) continue;
            var page = tab.Pages[i]; var scale = Math.Min(120d / page.Width, 150d / page.Height);
            var image = await tab.Session.RenderAsync(i, Math.Max(1, (int)(page.Width * scale)), Math.Max(1, (int)(page.Height * scale)), cancellation);
            var bitmap = BitmapSource.Create(image.GetProperty("Width").GetInt32(), image.GetProperty("Height").GetInt32(), 96, 96, PixelFormats.Bgr32, null,
                Convert.FromBase64String(image.GetProperty("Bgra").GetString()!), image.GetProperty("Stride").GetInt32());
            bitmap.Freeze(); page.Thumbnail = bitmap; tab.ThumbnailOrder.Enqueue(i);
            while (tab.ThumbnailOrder.Count > 32)
            {
                var old = tab.ThumbnailOrder.Dequeue();
                if (Math.Abs(old - tab.CurrentPage) <= 4) { tab.ThumbnailOrder.Enqueue(old); continue; }
                tab.Pages[old].Thumbnail = null;
            }
        }
        ThumbnailList.Items.Refresh();
    }

    async Task GenerateThumbnailsSafeAsync(OpenDocumentTab tab, CancellationToken cancellation)
    {
        try { await GenerateThumbnailsAsync(tab, cancellation); }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (tab == active) Status.Text = "Thumbnail generation stopped: " + e.Message; }
    }

    int[] VisiblePages()
    {
        var visible = new List<int>(); var viewport = DocumentScroll.ActualHeight;
        for (var i = 0; i < pageCards.Count; i++)
        {
            var y = pageCards[i].TranslatePoint(new Point(0, 0), DocumentScroll).Y;
            if (pageCards[i].Visibility == Visibility.Visible && y < viewport && y + pageCards[i].ActualHeight > 0) visible.Add(i);
        }
        return visible.ToArray();
    }

    static string SignatureStatus(DocumentSession session) => session.SignatureCount == 0 ? "" : session.SignaturesInvalidated
        ? " — edited copy; any existing signatures invalidated" : " — contains signature fields; validity not verified";

    void UpdateDocumentStatus(OpenDocumentTab tab)
    {
        if (tab != active) return;
        Status.Text = $"Page {tab.CurrentPage + 1} of {tab.Session.PageCount} · {tab.Zoom}% · {(tab.Session.IsDirty ? "Unsaved changes" : "Saved")}{SignatureStatus(tab.Session)}";
        if (notice is not null) Status.Text += " — " + notice;
    }

    async Task RenderViewportAsync()
    {
        if (active is null || busy || pageCards.Count == 0) return;
        var tab = active;
        var revision = tab.Session.Revision;
        var visible = VisiblePages(); if (visible.Length == 0) return;
        var center = DocumentScroll.ViewportHeight / 2;
        var current = pageCards.FindIndex(card => { var y = card.TranslatePoint(new Point(0, 0), DocumentScroll).Y; return y <= center && y + card.ActualHeight >= center; });
        if (current < 0) current = visible[0];
        // A page chosen by navigation stays current while it is visible, even when it cannot be centered (the last page).
        if (navigationTarget is int target && visible.Contains(target)) current = target;
        navigationTarget = null;
        tab.CurrentPage = current; visiblePage = current; PageNumberBox.Text = (current + 1).ToString(CultureInfo.InvariantCulture);
        var cts = new CancellationTokenSource(); renderCancellation?.Cancel(); renderCancellation = cts;
        try
        {
            foreach (var page in visible.Concat(new[] { Math.Max(0, current - 1), Math.Min(tab.Pages.Count - 1, current + 1) }).Distinct())
                await RenderPageAsync(tab, page, cts.Token);
            if (tab != active || tab.Session.Revision != revision || cts.IsCancellationRequested) return;
            thumbnailCancellation?.Cancel(); thumbnailCancellation = new CancellationTokenSource();
            _ = GenerateThumbnailsSafeAsync(tab, thumbnailCancellation.Token);
            UpdateDocumentStatus(tab);
            _ = RefreshSidePanelAsync();
            await PrefetchAsync(tab, current, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (tab == active && tab.Session.Revision == revision && !cts.IsCancellationRequested) { UpdateDocumentStatus(tab); Status.Text += " — " + e.Message; } }
    }

    void DocumentScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
        renderCancellation?.Cancel(); thumbnailCancellation?.Cancel(); renderDebounce.Stop(); renderDebounce.Start();
    }

    internal void GoToPage(int page)
    {
        if (active is null || page < 0 || page >= pageCards.Count) return;
        renderCancellation?.Cancel(); thumbnailCancellation?.Cancel();
        active.CurrentPage = page;
        navigationTarget = page; navigationVersion++;
        if (pageLayout == PageLayout.SinglePage) { ShowSinglePage(page); DocumentScroll.ScrollToVerticalOffset(0); }
        var top = pageCards[page].TranslatePoint(new Point(0, 0), PageStack).Y;
        DocumentScroll.ScrollToVerticalOffset(Math.Max(0, top - Math.Max(0, (DocumentScroll.ViewportHeight - pageCards[page].ActualHeight) / 2)));
        PageNumberBox.Text = (page + 1).ToString(CultureInfo.InvariantCulture);
        renderDebounce.Stop(); renderDebounce.Start();
    }
    void PreviousPage_Click(object sender, RoutedEventArgs e) { if (active is not null) GoToPage(active.CurrentPage - 1); }
    void NextPage_Click(object sender, RoutedEventArgs e) { if (active is not null) GoToPage(active.CurrentPage + 1); }
    void CloseFind_Click(object sender, RoutedEventArgs e) { searchScan?.Cancel(); FindBar.Visibility = Visibility.Collapsed; DocumentScroll.Focus(); }
    void GoPage() { if (int.TryParse(PageNumberBox.Text, out var page)) GoToPage(page - 1); else PageNumberBox.Text = (active?.CurrentPage + 1 ?? 1).ToString(CultureInfo.InvariantCulture); }
    void PageNumber_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { GoPage(); e.Handled = true; } }
    void Thumbnail_DoubleClick(object sender, MouseButtonEventArgs e) { if (ThumbnailList.SelectedItem is PageDescriptor page) GoToPage(page.Number); }
    void Bookmark_DoubleClick(object sender, MouseButtonEventArgs e) { if (BookmarkList.SelectedItem is BookmarkDescriptor bookmark) GoToPage(bookmark.Page); }
    async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CloseFind_Click(sender, e); }
        else if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); }
    }
    async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    async Task EditObjectAtAsync(OpenDocumentTab tab, int pageIndex, Point position, double viewWidth, double viewHeight)
    {
        if (active != tab || pageIndex < 0 || pageIndex >= tab.Pages.Count) return;
        var page = tab.Pages[pageIndex];
        var session = tab.Session;
        var revision = session.Revision;
        var currentPage = tab.CurrentPage;
        var point = PageObjectHitTest.ToPdf(position.X, position.Y, page.Width, page.Height, viewWidth, viewHeight, page.Rotation, page.OriginX, page.OriginY);
        var data = await session.ExtractAsync(pageIndex);
        await DirectEditCheckpointAsync("after-first-extraction");
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document changed while selecting. Click the object again.";
            return;
        }
        var selected = PageObjectHitTest.Find(data, point.X, point.Y);
        if (selected is null) { notice = "No text or image object at this point. Nested content can be inspected through Edit text → More PDFs."; return; }
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision)) return;
        if (!await AllowEditingAsync(tab)) return;
        await DirectEditCheckpointAsync("after-authorization");
        // Authorization may reload worker object IDs, but it must not change the
        // document revision, selected tab, current page, or page descriptor.
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document or page changed while authorizing. Click the object again.";
            return;
        }
        // Signed-copy authorization can reload the worker and replace object IDs.
        // Resolve the hit again from the authorized document before opening an editor.
        data = await session.ExtractAsync(pageIndex);
        await DirectEditCheckpointAsync("after-second-extraction");
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document changed while selecting. Click the object again.";
            return;
        }
        selected = PageObjectHitTest.Find(data, point.X, point.Y);
        if (selected is null) { notice = "The selected object changed. Click it again to inspect the current page."; return; }
        await DirectEditCheckpointAsync("before-editor-open");
        if (!IsCurrentDirectEditTarget(tab, session, pageIndex, page, currentPage, revision))
        {
            notice = "The document changed while selecting. Click the object again.";
            return;
        }
        tab.CurrentPage = pageIndex;
        var item = selected.Value;
        var id = item.GetProperty("Id").GetString();
        Window editor = item.GetProperty("Type").GetInt32() == 3
            ? new ImageEditorWindow(tab.Session, pageIndex, id)
            : new ObjectEditorWindow(tab.Session, pageIndex, AdvancedEngineInstalled ? AdvancedEngine : null, id);
        editor.Owner = this;
        editor.ShowDialog();
    }

    bool IsCurrentDirectEditTarget(OpenDocumentTab tab, DocumentSession session, int pageIndex,
        PageDescriptor page, int currentPage, long revision) =>
        active == tab && ReferenceEquals(tab.Session, session) && pageIndex >= 0 &&
        pageIndex < tab.Pages.Count && ReferenceEquals(tab.Pages[pageIndex], page) && tab.CurrentPage == currentPage &&
        session.Revision == revision;

    async void Selection_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var tab = active;
        if (busy || tab is null || sender is not Canvas layer || layer.Tag is not int pageIndex || pageIndex >= tab.Pages.Count) return;
        // Typing and caret clicks inside an open on-page editor belong to that editor.
        if (IsInsideInlineEditor(e.OriginalSource as DependencyObject)) return;
        if (await PlacementMouseDownAsync(layer, tab, pageIndex, e)) return;
        if (pageTool == PageTool.Select && FocusFieldAt(tab, pageIndex, e.GetPosition(layer), layer.ActualWidth, layer.ActualHeight)) { e.Handled = true; return; }
        if (await PageToolMouseDownAsync(layer, tab, pageIndex, e)) return;
        var page = tab.Pages[pageIndex];
        if (page.Characters is null)
        {
            foreach (var other in tab.Pages) if (other.Number != pageIndex) other.Characters = null;
            var revision = tab.Session.Revision;
            System.Text.Json.JsonElement data;
            try { data = await tab.Session.ExtractTextLayoutAsync(pageIndex); }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                Status.Text = error.Message;
                dialogs.Information(error.Message, "Text selection");
                return;
            }
            if (busy || active != tab || tab.Session.Revision != revision || pageIndex >= tab.Pages.Count || !ReferenceEquals(tab.Pages[pageIndex], page)) return;
            page.Characters = data.GetProperty("Characters").EnumerateArray().Select(c => new PdfTextCharacter(c.GetProperty("Index").GetInt32(), c.GetProperty("Text").GetString() ?? "", c.GetProperty("HasBounds").GetBoolean(),
                c.GetProperty("Left").GetDouble(), c.GetProperty("Bottom").GetDouble(), c.GetProperty("Right").GetDouble(), c.GetProperty("Top").GetDouble())).ToList();
        }
        var point = e.GetPosition(layer); var hit = FindCharacter(page, point, layer.ActualWidth, layer.ActualHeight);
        if (hit < 0) return;
        selectionPage = pageIndex; selectionAnchor = selectionEnd = hit; selectedText = ""; layer.Focus(); layer.CaptureMouse(); DrawTextSelection(layer, page);
        if (e.LeftButton != MouseButtonState.Pressed) layer.ReleaseMouseCapture();
        e.Handled = true;
    }
    void Selection_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Canvas toolLayer && PageToolMouseMove(toolLayer, e)) return;
        if (selectionAnchor < 0 || sender is not Canvas layer || !layer.IsMouseCaptured || active is null || layer.Tag is not int pageIndex || pageIndex != selectionPage) return;
        var point = e.GetPosition(layer); selectionEnd = FindCharacter(active.Pages[pageIndex], point, layer.ActualWidth, layer.ActualHeight);
        if (selectionEnd >= 0) DrawTextSelection(layer, active.Pages[pageIndex]);
    }
    async void Selection_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas layer) return;
        if (await PageToolMouseUpAsync(layer, e)) return;
        if (layer.IsMouseCaptured)
        {
            layer.ReleaseMouseCapture(); CommandManager.InvalidateRequerySuggested(); e.Handled = true;
            await ApplyMarkupToolAsync(layer);
        }
    }
    int FindCharacter(PageDescriptor page, Point point, double viewWidth, double viewHeight)
    {
        if (page.Characters is null || page.Characters.Count == 0 || viewWidth <= 0 || viewHeight <= 0) return -1;
        var w = page.Width; var h = page.Height;
        var scale = Math.Min(viewWidth / w, viewHeight / h); var x = point.X / scale; var y = point.Y / scale;
        var (px, py) = page.Rotation switch { 1 => (y, x), 2 => (page.Width - x, y), 3 => (page.Height - y, page.Width - x), _ => (x, page.Height - y) };
        var nearest = -1; var distance = double.MaxValue;
        foreach (var c in page.Characters)
        {
            if (!c.HasBounds) continue;
            if (px >= c.Left && px <= c.Right && py >= c.Bottom && py <= c.Top) return c.Index;
            var dx = (c.Left + c.Right) / 2 - px; var dy = (c.Bottom + c.Top) / 2 - py; var d = dx * dx + dy * dy;
            if (d < distance) { distance = d; nearest = c.Index; }
        }
        return distance < Math.Pow(Math.Max(page.Width, page.Height) * .06, 2) ? nearest : -1;
    }
    void DrawTextSelection(Canvas layer, PageDescriptor page)
    {
        if (page.Characters is null || selectionAnchor < 0 || selectionEnd < 0) return;
        layer.Children.Clear(); var from = Math.Min(selectionAnchor, selectionEnd); var to = Math.Max(selectionAnchor, selectionEnd);
        var width = layer.ActualWidth; var height = layer.ActualHeight; var rotatedWidth = page.Width; var rotatedHeight = page.Height;
        var sx = width / rotatedWidth; var sy = height / rotatedHeight; var text = new System.Text.StringBuilder(); var count = 0;
        foreach (var c in page.Characters)
        {
            if (c.Index < from || c.Index > to || count++ >= 10000) continue;
            text.Append(c.Text);
            if (!c.HasBounds) continue;
            var rect = PdfRectToView(page, c.Left, c.Bottom, c.Right, c.Top, width, height);
            var shape = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = new SolidColorBrush(Color.FromArgb(95, 60, 135, 235)), IsHitTestVisible = false };
            Canvas.SetLeft(shape, rect.X); Canvas.SetTop(shape, rect.Y); layer.Children.Add(shape);
        }
        selectedText = text.ToString(); CommandManager.InvalidateRequerySuggested();
        SearchStatus.Text = selectedText.Length == 0 ? "" : selectedText.Length > 100 ? selectedText[..100] + "…" : selectedText;
    }

    async void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (active is null || active.Pages.Count == 0) return;
        await RunUiAsync(() => SetZoomAsync(FitWidthZoom()), "Fitting page width…");
    }
    async void FitPage_Click(object sender, RoutedEventArgs e)
    {
        if (active is null || active.Pages.Count == 0) return;
        await RunUiAsync(() => SetZoomAsync(FitPageZoom()), "Fitting page…");
    }

    async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closingAccepted) return;
        e.Cancel = true;
        if (busy) return;
        await RunUiAsync(async () =>
        {
            if (!await CloseAllAsync()) return;
            closingAccepted = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }, "Closing workspace…");
    }

    internal void ApplyEffectiveTheme()
    {
        var contrast = highContrast();
        ShellThemeResources.Apply(Resources, preferences.Theme, contrast);
        ThemeChoice.IsEnabled = !busy && !contrast;
        ContrastLabel.Visibility = contrast ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(UpdateCommandLayout), DispatcherPriority.Loaded);
    }

    internal void SetTheme(ShellTheme theme)
    {
        preferences.Theme = theme;
        syncingTheme = true;
        try { ThemeChoice.SelectedItem = theme; }
        finally { syncingTheme = false; }
        ApplyEffectiveTheme();
        SavePreferences();
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !syncingTheme && ThemeChoice.SelectedItem is ShellTheme theme) SetTheme(theme);
    }

    void SystemParameters_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            _ = Dispatcher.InvokeAsync(ApplyEffectiveTheme);
    }

    void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (preferences.Theme == ShellTheme.System)
            _ = Dispatcher.InvokeAsync(ApplyEffectiveTheme);
    }

    void Remember(string path)
    {
        preferences.Remember(path);
        SavePreferences();
        RefreshRecent();
    }

    void SavePreferences()
    {
        try { preferences.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notice = "The PDF is available, but recent history and theme settings could not be saved.";
        }
    }

    void RefreshRecent()
    {
        RecentList.ItemsSource = preferences.RecentFiles.Select(p => new RecentDocument(p)).ToArray();
        RecentEmpty.Visibility = preferences.RecentFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ClearRecentHistory()
    {
        preferences.RecentFiles.Clear();
        SavePreferences();
        RefreshRecent();
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e) => ClearRecentHistory();

    internal Task OpenRecentAsync() => RecentList.SelectedItem is RecentDocument item ? OpenPathAsync(item.FullPath) : Task.CompletedTask;
    async void OpenRecent_Click(object sender, RoutedEventArgs e) => await RunUiAsync(OpenRecentAsync, "Opening recent PDF…");
    async void Recent_DoubleClick(object sender, MouseButtonEventArgs e) => await RunUiAsync(OpenRecentAsync, "Opening recent PDF…");
    async void Recent_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunUiAsync(OpenRecentAsync, "Opening recent PDF…");
    }

    internal async Task OpenDroppedPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            try { await OpenPathAsync(path); }
            catch (Exception e) { dialogs.Information(e.Message, "PDF could not be opened"); }
        }
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Any(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await RunUiAsync(() => OpenDroppedPathsAsync(paths), "Opening dropped PDFs…");
    }

    internal void ApplyPanelVisibility()
    {
        if (NavigationToggle.IsChecked == true)
        {
            NavigationColumn.Width = new GridLength(navigationWidth);
            NavigationGripColumn.Width = new GridLength(5);
            NavigationPanel.Visibility = NavigationGrip.Visibility = Visibility.Visible;
        }
        else
        {
            if (NavigationColumn.ActualWidth > 0) navigationWidth = NavigationColumn.ActualWidth;
            NavigationColumn.Width = NavigationGripColumn.Width = new GridLength(0);
            NavigationPanel.Visibility = NavigationGrip.Visibility = Visibility.Collapsed;
            NavigationToggle.Focus();
        }
        if (PropertiesToggle.IsChecked == true)
        {
            PropertiesColumn.Width = new GridLength(propertiesWidth);
            PropertiesGripColumn.Width = new GridLength(5);
            PropertiesPanel.Visibility = PropertiesGrip.Visibility = Visibility.Visible;
        }
        else
        {
            if (PropertiesColumn.ActualWidth > 0) propertiesWidth = PropertiesColumn.ActualWidth;
            PropertiesColumn.Width = PropertiesGripColumn.Width = new GridLength(0);
            PropertiesPanel.Visibility = PropertiesGrip.Visibility = Visibility.Collapsed;
            PropertiesToggle.Focus();
        }
    }

    void ApplyHomePanelVisibility()
    {
        var availableWidth = ActualWidth > 0 ? ActualWidth : Width;
        if (active is null)
        {
            var changed = false;
            if (NavigationToggle.IsChecked == true)
            {
                NavigationToggle.IsChecked = false;
                homeNavigationHidden = true;
                changed = true;
            }
            if (PropertiesToggle.IsChecked == true)
            {
                PropertiesToggle.IsChecked = false;
                homePropertiesHidden = true;
                changed = true;
            }
            if (changed) ApplyPanelVisibility();
            return;
        }

        if (homeNavigationHidden)
        {
            homeNavigationHidden = false;
            if (availableWidth >= 920) NavigationToggle.IsChecked = true;
            else responsiveNavigationHidden = true;
        }
        if (homePropertiesHidden)
        {
            homePropertiesHidden = false;
            if (availableWidth >= 1120) PropertiesToggle.IsChecked = true;
            else responsivePropertiesHidden = true;
        }
        if (NavigationToggle.IsChecked == true || PropertiesToggle.IsChecked == true) ApplyPanelVisibility();
    }

    void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        var changed = false;
        if (e.NewSize.Width < 1080 && PropertiesToggle.IsChecked == true)
        {
            PropertiesToggle.IsChecked = false;
            responsivePropertiesHidden = true;
            changed = true;
        }
        else if (e.NewSize.Width >= 1120 && responsivePropertiesHidden)
        {
            PropertiesToggle.IsChecked = true;
            responsivePropertiesHidden = false;
            changed = true;
        }
        if (e.NewSize.Width < 880 && NavigationToggle.IsChecked == true)
        {
            NavigationToggle.IsChecked = false;
            responsiveNavigationHidden = true;
            changed = true;
        }
        else if (e.NewSize.Width >= 920 && responsiveNavigationHidden)
        {
            NavigationToggle.IsChecked = true;
            responsiveNavigationHidden = false;
            changed = true;
        }
        if (changed) ApplyPanelVisibility();
    }

    void CommandBar_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) UpdateCommandLayout(); }

    // When the three command groups do not fit on one row, history, page and panel controls move under the task
    // menus instead of clipping them. Group widths change with the theme's density, so this is re-run after a theme change.
    void UpdateCommandLayout()
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        CommandLeft.Measure(infinite); CommandCenter.Measure(infinite); CommandTools.Measure(infinite);
        var narrow = CommandLeft.DesiredSize.Width + CommandCenter.DesiredSize.Width + CommandTools.DesiredSize.Width > CommandBar.ActualWidth;
        Grid.SetRow(CommandTools, narrow ? 1 : 0);
        Grid.SetColumn(CommandTools, narrow ? 0 : 2);
        Grid.SetColumnSpan(CommandTools, narrow ? 3 : 1);
        CommandTools.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        CommandTools.Margin = new Thickness(0, narrow ? 4 : 0, 0, 0);
    }

    void Panels_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == PropertiesToggle) responsivePropertiesHidden = false;
        if (sender == NavigationToggle) responsiveNavigationHidden = false;
        ApplyPanelVisibility();
    }
    internal Task SetZoomAsync(int zoom)
    {
        if (active is not null)
        {
            ResetPageToolState();
            zoomRestorePage = active.CurrentPage;
            active.Zoom = Math.Clamp(zoom, 25, 400);
        }
        return Task.CompletedTask;
    }
    async void ZoomIn_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetZoomAsync(NextZoomStep(active?.Zoom ?? 100, 1)), "Changing zoom…");
    async void ZoomOut_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetZoomAsync(NextZoomStep(active?.Zoom ?? 100, -1)), "Changing zoom…");

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => (bytes / 1024d).ToString("0", CultureInfo.CurrentCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1048576d).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / 1073741824d).ToString("0.##", CultureInfo.CurrentCulture) + " GB"
    };

    async void CompactHistory_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (active is not { } tab) return;
        var before = tab.Session.HistoryBytes;
        if (!dialogs.Confirm($"Undo history for {tab.FileName} uses {FormatBytes(before)}. Free it by keeping only the original, the last saved and the current versions? Older undo steps and redo will no longer be available.",
            "Free undo storage")) return;
        await tab.Session.CompactHistoryAsync();
        notice = $"Undo storage reduced from {FormatBytes(before)} to {FormatBytes(tab.Session.HistoryBytes)}.";
    }, "Freeing undo storage…");

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
