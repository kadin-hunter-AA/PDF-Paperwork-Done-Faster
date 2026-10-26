using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using Microsoft.Win32;

namespace HunterPdf.Desktop;

/// <summary>A refusal whose message is safe and plain enough to show to the user as-is.</summary>
internal sealed class QuickToolRefusal(string message) : Exception(message);

/// <summary>
/// Shared shell for the small "Quick tools" dialogs: collects options, validates them, runs the operation itself against the
/// session (busy state, cancel, plain refusal) and reports a plain result. Nothing is written when the user cancels.
/// <see cref="Changed"/> is true once an edit was committed to the open document; <see cref="ResultPath"/> is the created file or folder.
/// </summary>
public abstract class QuickToolWindow : Window
{
    protected readonly DocumentSession Session;
    protected readonly AdvancedEngineLocation Engine;
    protected readonly int CurrentPage;
    protected readonly StackPanel Body = new();
    protected readonly Button Apply = new() { MinHeight = 44, IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
    protected readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    protected readonly ProgressBar Progress = new() { Height = 8, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button close = new() { Content = "Close", MinHeight = 44, IsCancel = true, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(16, 6, 16, 6) };
    CancellationTokenSource? job;
    bool working, closeRequested, applyAllowed = true;

    /// <summary>The created file (exports) or folder (image tools) after a successful run.</summary>
    public string? ResultPath { get; protected set; }
    /// <summary>True once an edit was committed to the open document (the shell should refresh the view).</summary>
    public bool Changed { get; protected set; }
    /// <summary>Replaceable path chooser (tests). Receives the suggested file or folder name and returns the full new path or null.</summary>
    internal Func<string, string?>? PickPath { get; set; }
    internal bool Busy => working;
    internal string StatusText => Status.Text;

    protected QuickToolWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage, string title, string applyText, int width = 620, int height = 720)
    {
        Session = session; Engine = engine; CurrentPage = Math.Clamp(currentPage, 0, Math.Max(0, session.PageCount - 1));
        Title = title; Width = width; Height = height; MinWidth = 480; MinHeight = 420; FontSize = 16;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        Apply.Content = applyText; AutomationProperties.SetName(Apply, applyText);
        AutomationProperties.SetName(close, "Close or cancel this tool"); AutomationProperties.SetName(Status, "Operation status"); AutomationProperties.SetName(Progress, "Operation progress");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(Progress); footer.Children.Add(Status); footer.Children.Add(Apply); footer.Children.Add(close); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Apply.Click += async (_, _) => await ApplyClickedAsync();
        close.Click += (_, _) => { if (working) job?.Cancel(); };
        Loaded += async (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
            if (LoadsOnOpen) await RunAsync(LoadAsync, "Loading…");
        };
        Closing += (_, e) => { if (working) { e.Cancel = true; closeRequested = true; job?.Cancel(); } };
        Closed += (_, _) => { ClearSecrets(); job?.Dispose(); };
    }

    // ---- hooks

    protected virtual bool LoadsOnOpen => false;
    protected virtual Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult<string?>("");
    /// <summary>Returns a plain message when the options are not valid; otherwise null.</summary>
    protected abstract string? Validate();
    /// <summary>Runs the operation. Returns the result message, or null when the user cancelled a picker.</summary>
    protected abstract Task<string?> ExecuteAsync(CancellationToken ct);
    protected virtual void ClearSecrets() { }

    async Task ApplyClickedAsync()
    {
        if (working || !applyAllowed) return;
        string? problem;
        try { problem = Validate(); } catch (Exception e) { problem = Plain(e); }
        if (problem is not null) { Status.Text = problem; return; }
        await RunAsync(ExecuteAsync, "Working…");
    }

    protected async Task RunAsync(Func<CancellationToken, Task<string?>> operation, string busyText)
    {
        if (working) return;
        working = true; SetBusy(true); Status.Text = busyText;
        job?.Dispose(); job = new CancellationTokenSource();
        try { Status.Text = await operation(job.Token) ?? "Cancelled. Nothing was written or changed."; }
        catch (OperationCanceledException) { Status.Text = "Cancelled. Nothing was written or changed."; }
        catch (Exception e) { Status.Text = Plain(e); }
        finally
        {
            working = false; SetBusy(false);
            if (closeRequested) { closeRequested = false; try { Close(); } catch (InvalidOperationException) { } }
        }
    }

    void SetBusy(bool busy)
    {
        Body.IsEnabled = !busy; Apply.IsEnabled = !busy && applyAllowed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        close.Content = busy ? "Cancel" : "Close";
    }

    /// <summary>Enables or disables the main action button (for example once a one-shot operation has finished).</summary>
    protected void SetApplyAllowed(bool allowed) { applyAllowed = allowed; Apply.IsEnabled = allowed && !working; }

    static string Plain(Exception e) => e switch
    {
        QuickToolRefusal or SignedDocumentException or DocumentConflictException or DocumentPasswordRequiredException => e.Message,
        ArgumentOutOfRangeException => "A value is outside the allowed range.",
        ArgumentException a => Regex.Replace(a.Message, @"\s*\(Parameter '[^']*'\)", ""),
        _ => string.IsNullOrWhiteSpace(e.Message) ? "The operation failed. Nothing was changed." : e.Message
    };

    // ---- path pickers

    string Stem() { var s = Path.GetFileNameWithoutExtension(Session.LastSavePath); return string.IsNullOrWhiteSpace(s) ? "document" : s; }

    protected string StemName => Stem();

    /// <summary>Chooses a NEW pdf path named like the document plus <paramref name="suffix"/>; an existing file is refused, never replaced.</summary>
    protected string? ChooseNewFile(string suffix)
    {
        var path = (PickPath ?? DefaultPickFile)(Stem() + suffix + ".pdf");
        if (path is null) return null;
        if (File.Exists(path) || Directory.Exists(path)) throw new QuickToolRefusal("Choose a new filename. Existing files are never replaced.");
        return path;
    }

    protected string? ChooseNewFolder(string suffix)
    {
        var path = (PickPath ?? DefaultPickFolder)(Stem() + suffix);
        if (path is null) return null;
        if (File.Exists(path) || Directory.Exists(path)) throw new QuickToolRefusal("Choose a new folder that does not exist yet.");
        return path;
    }

    string? DefaultPickFile(string suggested)
    {
        var dialog = new SaveFileDialog { Filter = "PDF files (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = false, FileName = suggested };
        try { var dir = Path.GetDirectoryName(Session.LastSavePath); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dialog.InitialDirectory = dir; } catch (ArgumentException) { }
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? DefaultPickFolder(string suggested)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to create the new folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return null;
        var path = Path.Combine(dialog.FolderName, suggested);
        for (var i = 2; (File.Exists(path) || Directory.Exists(path)) && i < 1000; i++) path = Path.Combine(dialog.FolderName, $"{suggested} ({i})");
        return path;
    }

    // ---- layout helpers

    protected TextBlock AddLabel(string text, bool heading = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 24 : 16, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, heading ? 0 : 12, 0, heading ? 10 : 4) };
        Body.Children.Add(label); return label;
    }

    protected TextBlock AddNote(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), Opacity = 0.85 };
        Body.Children.Add(note); return note;
    }

    protected TextBox AddText(string label, string name, string value = "", int max = 500)
    {
        AddLabel(label);
        var box = new TextBox { Text = value, MinHeight = 44, MaxLength = max, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(box, name); Body.Children.Add(box); return box;
    }

    protected PasswordBox AddPassword(string label, string name)
    {
        AddLabel(label);
        var box = new PasswordBox { MinHeight = 44, MaxLength = 128, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(box, name); Body.Children.Add(box); return box;
    }

    protected ComboBox AddCombo(string label, string name, int selected, params string[] items)
    {
        AddLabel(label);
        var combo = new ComboBox { MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = selected; AutomationProperties.SetName(combo, name); Body.Children.Add(combo); return combo;
    }

    protected CheckBox AddCheck(string content, string name, bool isChecked)
    {
        var box = new CheckBox { Content = content, IsChecked = isChecked, MinHeight = 32, Margin = new Thickness(0, 8, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name); Body.Children.Add(box); return box;
    }

    protected static Button MakeButton(string text, string name, RoutedEventHandler click)
    {
        var button = new Button { Content = text, MinHeight = 44, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(button, name); button.Click += click; return button;
    }

    protected WrapPanel AddButtonRow(params Button[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        Body.Children.Add(row); return row;
    }

    // ---- shared value helpers

    protected static bool TryNumber(string text, out double value) =>
        (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
         double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)) && double.IsFinite(value);

    protected static string? NumberError(string text, string what, double low, double high, out double value) =>
        !TryNumber(text, out value) || value < low || value > high
            ? $"{what} must be a number from {low.ToString(CultureInfo.CurrentCulture)} to {high.ToString(CultureInfo.CurrentCulture)}." : null;

    protected static bool IsLatin1(string text) => text.All(c => c <= 0xFF);

    protected static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:N2} MB"
    };

    protected static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    /// <summary>Resolves page text like 1,3-5 into zero-based page indexes (copied from the page organizer's import range rules).</summary>
    internal static bool TryResolveRange(string range, int pageCount, out int[] pages, out string error)
    {
        pages = []; error = "";
        if (pageCount < 1) { error = "The document has no pages."; return false; }
        if (string.IsNullOrWhiteSpace(range)) { error = "Enter pages such as 1,3-5; page numbers start at 1."; return false; }
        var selected = new List<int>(); var seen = new HashSet<int>();
        foreach (var raw in range.Split(','))
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
            for (var page = first; page <= last; page++)
            {
                if (!seen.Add(page)) { error = $"Page {page} appears more than once in this range."; return false; }
                selected.Add(page - 1);
            }
        }
        selected.Sort(); pages = selected.ToArray(); return true;
    }

    /// <summary>A "Pages: All / Current / Range" selector.</summary>
    protected sealed class PageScope
    {
        readonly QuickToolWindow owner;
        public ComboBox Mode { get; }
        public TextBox Range { get; }
        internal PageScope(QuickToolWindow owner, string label, int mode)
        {
            this.owner = owner;
            Mode = owner.AddCombo(label, "Pages to change", mode, "All pages", $"Current page ({owner.CurrentPage + 1})", "Page range");
            Range = new TextBox { MinHeight = 44, MaxLength = 200, IsEnabled = false, Margin = new Thickness(0, 6, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
            AutomationProperties.SetName(Range, "Page range such as 1,3-5"); AutomationProperties.SetHelpText(Range, "Pages such as 1,3-5; page numbers start at 1.");
            owner.Body.Children.Add(Range);
            Mode.SelectionChanged += (_, _) => Range.IsEnabled = Mode.SelectedIndex == 2;
            Range.IsEnabled = mode == 2;
        }
        /// <summary>Null pages means every page.</summary>
        public bool TryGet(out int[]? pages, out string error)
        {
            error = ""; pages = null;
            switch (Mode.SelectedIndex)
            {
                case 1: pages = [owner.CurrentPage]; return true;
                case 2:
                    if (!TryResolveRange(Range.Text, owner.Session.PageCount, out var resolved, out error)) return false;
                    pages = resolved; return true;
                default: return true;
            }
        }
        public int Count => Mode.SelectedIndex == 1 ? 1 : Mode.SelectedIndex == 2 && TryResolveRange(Range.Text, owner.Session.PageCount, out var p, out _) ? p.Length : owner.Session.PageCount;
    }

    protected static readonly string[] PositionNames = ["Top left", "Top center", "Top right", "Bottom left", "Bottom center", "Bottom right"];
}

public sealed class CompressWindow : QuickToolWindow
{
    readonly ComboBox dpi;
    public CompressWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Compress PDF", "Create compressed copy…", 620, 560)
    {
        AddLabel("Compress PDF", true);
        AddNote("Writes a smaller NEW copy and keeps your open document and its history unchanged. Unused data is removed and streams are recompressed; optionally images above the chosen resolution are downsampled (lossy).");
        dpi = AddCombo("Image downsampling", "Image downsampling resolution", 0, "Off (keep image resolution)", "150 dpi", "96 dpi");
        AddNote("The new file is checked before it is published. Existing files are never replaced.");
    }
    protected override string? Validate() => null;
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var destination = ChooseNewFile("-compressed"); if (destination is null) return null;
        int? target = dpi.SelectedIndex switch { 1 => 150, 2 => 96, _ => null };
        var result = await Session.CompressCopyAsync(Engine, destination, target, ct);
        ResultPath = destination; SetApplyAllowed(false);
        var change = result.InputBytes <= 0 ? 0 : (result.InputBytes - result.OutputBytes) * 100.0 / result.InputBytes;
        var verdict = result.OutputBytes < result.InputBytes ? $"{change:0.#}% smaller" : result.OutputBytes == result.InputBytes ? "no change in size" : $"{-change:0.#}% larger";
        return $"Saved the compressed copy. Before: {FormatSize(result.InputBytes)}. After: {FormatSize(result.OutputBytes)} ({verdict}).";
    }
}

public sealed class ProtectWindow : QuickToolWindow
{
    readonly PasswordBox open, openAgain, owner, ownerAgain;
    readonly CheckBox print, copy, edit;
    public ProtectWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Protect with passwords", "Create protected copy…", 620, 800)
    {
        AddLabel("Protect with passwords", true);
        AddNote("Writes a NEW copy encrypted with AES-256. Your open document stays unprotected. Passwords cannot be recovered: if you forget them the file cannot be opened. Passwords are never saved or logged.");
        open = AddPassword("Open password (needed to view the file)", "Open password");
        openAgain = AddPassword("Repeat open password", "Repeat open password");
        owner = AddPassword("Owner password (needed to change these permissions; must differ from the open password)", "Owner password");
        ownerAgain = AddPassword("Repeat owner password", "Repeat owner password");
        AddLabel("Allow people who open the file to");
        print = AddCheck("Print", "Allow printing", true); copy = AddCheck("Copy text and images", "Allow copying", true); edit = AddCheck("Edit, fill forms and rearrange pages", "Allow editing", false);
        AddNote("Permissions are honored by compliant PDF programs but are advisory, not a security barrier; the open password is what protects the content.");
    }
    protected override string? Validate()
    {
        if (open.Password.Length == 0) return "Enter an open password.";
        if (open.Password != openAgain.Password) return "The open passwords do not match.";
        if (owner.Password.Length == 0) return "Enter an owner password.";
        if (owner.Password != ownerAgain.Password) return "The owner passwords do not match.";
        if (owner.Password == open.Password) return "Use a different owner password; otherwise anyone who opens the file could change its permissions.";
        return null;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var destination = ChooseNewFile("-protected"); if (destination is null) return null;
        var user = open.Password; var admin = owner.Password; ClearSecrets();
        try { await Session.ProtectCopyAsync(Engine, destination, user, admin, print.IsChecked == true, copy.IsChecked == true, edit.IsChecked == true, ct); }
        finally { user = ""; admin = ""; }
        ResultPath = destination; SetApplyAllowed(false);
        return "Saved a password-protected copy (AES-256). The open document is unchanged.";
    }
    protected override void ClearSecrets() { open.Clear(); openAgain.Clear(); owner.Clear(); ownerAgain.Clear(); }
}

public sealed class UnprotectWindow : QuickToolWindow
{
    public UnprotectWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Remove password protection", "Create unprotected copy…", 620, 480)
    {
        AddLabel("Remove password protection", true);
        AddNote("Writes an unprotected NEW copy of the document that is open now. This only works for a document you opened with its password; the original file and your open document are unchanged.");
    }
    protected override string? Validate() => null;
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var destination = ChooseNewFile("-unprotected"); if (destination is null) return null;
        await Session.UnprotectCopyAsync(Engine, destination, ct);
        ResultPath = destination; SetApplyAllowed(false);
        return "Saved an unprotected copy. Keep it somewhere private: anyone can open it without a password.";
    }
}

public sealed class WatermarkWindow : QuickToolWindow
{
    static readonly (string Name, byte R, byte G, byte B)[] Colors = [("Gray", 128, 128, 128), ("Red", 192, 0, 0), ("Blue", 0, 80, 200), ("Green", 0, 130, 60), ("Black", 0, 0, 0)];
    static readonly int[] Rotations = [0, 45, 90, -45];
    readonly TextBox text, size;
    readonly List<RadioButton> swatches = new();
    readonly Slider opacity = new() { Minimum = 5, Maximum = 100, Value = 30, TickFrequency = 5, IsSnapToTickEnabled = true, MinHeight = 32 };
    readonly TextBlock opacityLabel = new();
    readonly ComboBox rotation;
    readonly PageScope scope;
    public WatermarkWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Add watermark", "Add watermark", 620, 860)
    {
        AddLabel("Add watermark", true);
        AddNote("Draws centered text on the pages as one undoable change. It is page content, not a removable layer, and does not hide or redact anything under it. Use Undo to remove it.");
        text = AddText("Watermark text (Western European characters)", "Watermark text", "CONFIDENTIAL", 200);
        size = AddText("Font size (8 to 200 points)", "Watermark font size", "60", 6);
        AddLabel("Color");
        var row = new WrapPanel();
        foreach (var (name, r, g, b) in Colors)
        {
            var swatch = new Border { Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(r, g, b)), BorderThickness = new Thickness(1), BorderBrush = Brushes.DimGray };
            var radio = new RadioButton { GroupName = "Watermark color", Tag = (r, g, b), MinHeight = 32, Margin = new Thickness(0, 0, 14, 4), IsChecked = name == "Gray",
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = name } } } };
            AutomationProperties.SetName(radio, "Watermark color: " + name);
            swatches.Add(radio); row.Children.Add(radio);
        }
        Body.Children.Add(row);
        Body.Children.Add(opacityLabel); AutomationProperties.SetName(opacity, "Watermark opacity percent");
        opacity.ValueChanged += (_, _) => opacityLabel.Text = $"Opacity: {opacity.Value:0}%";
        opacityLabel.Text = "Opacity: 30%"; opacityLabel.Margin = new Thickness(0, 12, 0, 4);
        Body.Children.Add(opacity);
        rotation = AddCombo("Rotation (counter-clockwise)", "Watermark rotation", 1, "0°", "45°", "90°", "-45° (clockwise)");
        scope = new PageScope(this, "Apply to", 0);
    }
    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(text.Text)) return "Enter the watermark text.";
        if (!IsLatin1(text.Text)) return "Only Western European (Latin-1) characters are supported in watermarks.";
        if (NumberError(size.Text, "Font size", 8, 200, out _) is { } error) return error;
        return scope.TryGet(out _, out var pagesError) ? null : pagesError;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        TryNumber(size.Text, out var fontSize); scope.TryGet(out var pages, out _);
        var (r, g, b) = ((byte, byte, byte))swatches.First(s => s.IsChecked == true).Tag!;
        await Session.AddWatermarkAsync(Engine, new WatermarkOptions(text.Text, fontSize, r, g, b, opacity.Value / 100, Rotations[Math.Max(0, rotation.SelectedIndex)], pages), Session.Revision, ct);
        Changed = true; SetApplyAllowed(false);
        return $"Watermark added to {Plural(pages?.Length ?? Session.PageCount, "page")}. Use Undo to remove it.";
    }
}

public sealed class HeaderFooterWindow : QuickToolWindow
{
    readonly TextBox template, size, margin, start;
    readonly ComboBox position;
    readonly PageScope scope;
    readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold };
    readonly string fileName, date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public HeaderFooterWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Add header or footer", "Add header or footer", 640, 900)
    {
        fileName = Path.GetFileName(session.OriginalPath);
        AddLabel("Add header or footer", true);
        AddNote("Adds one line of text near the page edge as one undoable change. {n} is the page number (plus the start number offset), {total} the page count, {date} today's date and {filename} the document name.");
        template = AddText("Text (Western European characters)", "Header or footer text", "Page {n} of {total}", 200);
        AddButtonRow(Token("{n}", "Insert page number"), Token("{total}", "Insert total pages"), Token("{date}", "Insert date"), Token("{filename}", "Insert file name"));
        position = AddCombo("Position", "Header or footer position", 4, PositionNames);
        size = AddText("Font size (6 to 72 points)", "Header or footer font size", "10", 4);
        margin = AddText("Margin from the page edge (9 to 144 points)", "Header or footer margin", "36", 5);
        start = AddText("Number the first page as", "Header or footer start number", "1", 8);
        scope = new PageScope(this, "Apply to", 0);
        AutomationProperties.SetName(preview, "Sample text for page 1"); Body.Children.Add(preview);
        foreach (var box in new[] { template, size, margin, start, scope.Range }) box.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }
    Button Token(string token, string name) => MakeButton(token, name, (_, _) =>
    {
        var at = template.SelectionStart;
        var next = template.Text.Remove(at, template.SelectionLength).Insert(at, token);
        if (next.Length > template.MaxLength) return;
        template.Text = next; template.SelectionStart = at + token.Length; template.Focus();
    });
    static readonly Regex Placeholders = new(@"\{(n|total|date|filename)\}", RegexOptions.Compiled);
    string Expand(string text, long first) => Placeholders.Replace(text, m => m.Groups[1].Value switch
    { "n" => first.ToString(CultureInfo.InvariantCulture), "total" => Session.PageCount.ToString(CultureInfo.InvariantCulture), "date" => date, _ => fileName });
    void UpdatePreview()
    {
        _ = long.TryParse(start.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first);
        preview.Text = "Page 1 will read: " + Expand(template.Text, Math.Max(1, first));
    }
    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(template.Text)) return "Enter the header or footer text.";
        if (!long.TryParse(start.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1 || first > 10_000_000) return "The start number must be a whole number from 1 to 10,000,000.";
        var expanded = Expand(template.Text, first + Session.PageCount);
        if (!IsLatin1(expanded)) return "Only Western European (Latin-1) characters are supported (this includes the document name).";
        if (expanded.Length > 600) return "The expanded text is too long.";
        if (NumberError(size.Text, "Font size", 6, 72, out _) is { } e1) return e1;
        if (NumberError(margin.Text, "Margin", 9, 144, out _) is { } e2) return e2;
        return scope.TryGet(out _, out var pagesError) ? null : pagesError;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        TryNumber(size.Text, out var fontSize); TryNumber(margin.Text, out var edge); scope.TryGet(out var pages, out _);
        var first = int.Parse(start.Text.Trim(), CultureInfo.InvariantCulture);
        await Session.AddHeaderFooterAsync(Engine, new HeaderFooterOptions(template.Text, fileName, date, (PageMarkPosition)position.SelectedIndex, fontSize, edge, first, pages), Session.Revision, ct);
        Changed = true; SetApplyAllowed(false);
        return $"Text added to {Plural(pages?.Length ?? Session.PageCount, "page")}. Use Undo to remove it.";
    }
}

public sealed class BatesWindow : QuickToolWindow
{
    /// <summary>The next Bates number for this app session, so numbering can continue across documents.</summary>
    internal static long? NextNumber { get; set; }
    static string lastPrefix = "", lastSuffix = ""; static int lastDigits = 6;
    readonly TextBox prefix, suffix, start, digits, size;
    readonly ComboBox position;
    readonly PageScope scope;
    public BatesWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Bates numbering", "Apply Bates numbers", 620, 900)
    {
        AddLabel("Bates numbering", true);
        AddNote("Stamps a sequential label such as ABC-000001 on each chosen page, in page order, as one undoable change. The next number is remembered until you close the app so you can continue in the next document.");
        prefix = AddText("Prefix (basic ASCII, up to 40 characters)", "Bates prefix", lastPrefix, 40);
        start = AddText(NextNumber is null ? "Start number" : "Start number (continues after the last batch)", "Bates start number", (NextNumber ?? 1).ToString(CultureInfo.InvariantCulture), 12);
        digits = AddText("Digits (1 to 12, padded with zeros)", "Bates digits", lastDigits.ToString(CultureInfo.InvariantCulture), 2);
        suffix = AddText("Suffix (basic ASCII, up to 40 characters)", "Bates suffix", lastSuffix, 40);
        position = AddCombo("Position", "Bates position", 5, PositionNames);
        size = AddText("Font size (6 to 72 points)", "Bates font size", "10", 4);
        scope = new PageScope(this, "Apply to", 0);
    }
    static bool Ascii(string s) => s.All(c => c >= 0x20 && c <= 0x7E);
    protected override string? Validate()
    {
        if (!Ascii(prefix.Text) || !Ascii(suffix.Text)) return "The prefix and suffix may only use basic ASCII characters.";
        if (!int.TryParse(digits.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 1 or > 12) return "Digits must be a whole number from 1 to 12.";
        if (!long.TryParse(start.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first)) return "The start number must be a whole number, zero or higher.";
        if (NumberError(size.Text, "Font size", 6, 72, out _) is { } error) return error;
        if (!scope.TryGet(out var pages, out var pagesError)) return pagesError;
        if (first + (pages?.Length ?? Session.PageCount) > (long)Math.Pow(10, d)) return $"{d} digits are not enough for these numbers; use more digits or a lower start number.";
        return null;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        TryNumber(size.Text, out var fontSize); scope.TryGet(out var pages, out _);
        var result = await Session.AddBatesAsync(Engine, new BatesOptions(prefix.Text, suffix.Text, long.Parse(start.Text.Trim(), CultureInfo.InvariantCulture),
            int.Parse(digits.Text.Trim(), CultureInfo.InvariantCulture), (PageMarkPosition)position.SelectedIndex, fontSize, 36, pages), Session.Revision, ct);
        NextNumber = result.LastNumber + 1; lastPrefix = prefix.Text; lastSuffix = suffix.Text; lastDigits = int.Parse(digits.Text.Trim(), CultureInfo.InvariantCulture);
        Changed = true; SetApplyAllowed(false);
        return $"Numbered {Plural(result.Pages, "page")}: {result.FirstLabel} to {result.LastLabel}. The next number will be {NextNumber}. Use Undo to remove the labels.";
    }
}

public sealed class CropWindow : QuickToolWindow
{
    readonly TextBox left, top, right, bottom;
    readonly PageScope scope;
    public CropWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Crop pages", "Crop pages", 620, 840)
    {
        AddLabel($"Crop pages (from page {CurrentPage + 1})", true);
        AddNote("Trims the visible area by the margins below, measured in points (72 points = 1 inch) on the page as you see it. Cropping only hides the outer area: the content is still in the file and can be recovered. It is NOT redaction; use Redact to remove content permanently.");
        left = AddText("Trim from the left (points)", "Crop left margin points", "0", 8); top = AddText("Trim from the top (points)", "Crop top margin points", "0", 8);
        right = AddText("Trim from the right (points)", "Crop right margin points", "0", 8); bottom = AddText("Trim from the bottom (points)", "Crop bottom margin points", "0", 8);
        scope = new PageScope(this, "Apply to", 1);
    }
    protected override string? Validate()
    {
        foreach (var (box, name) in new[] { (left, "Left"), (top, "Top"), (right, "Right"), (bottom, "Bottom") })
            if (NumberError(box.Text, $"{name} margin", 0, 100000, out _) is { } error) return error;
        TryNumber(left.Text, out var l); TryNumber(top.Text, out var t); TryNumber(right.Text, out var r); TryNumber(bottom.Text, out var b);
        if (l + t + r + b <= 0) return "Enter at least one margin to trim.";
        return scope.TryGet(out _, out var pagesError) ? null : pagesError;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        TryNumber(left.Text, out var l); TryNumber(top.Text, out var t); TryNumber(right.Text, out var r); TryNumber(bottom.Text, out var b); scope.TryGet(out var pages, out _);
        var page = await Session.ExtractAsync(CurrentPage, ct);
        double width = page.GetProperty("Width").GetDouble(), height = page.GetProperty("Height").GetDouble();
        var quarter = page.GetProperty("Rotation").GetInt32(); quarter = (quarter >= 90 ? quarter / 90 : quarter) % 4;
        if (!page.TryGetProperty("SupportsZeroOriginCoordinates", out var zero) || !zero.GetBoolean())
            throw new QuickToolRefusal("This page's boundaries are offset from the origin, so crop margins cannot be computed safely. Nothing was changed.");
        var infos = (await Session.PageInfoAsync(ct)).GetProperty("Pages").EnumerateArray().ToArray();
        foreach (var index in pages ?? Enumerable.Range(0, Session.PageCount).ToArray())
        {
            var other = infos[index];
            if (Math.Abs(other.GetProperty("Width").GetDouble() - width) > 0.01 || Math.Abs(other.GetProperty("Height").GetDouble() - height) > 0.01 ||
                other.GetProperty("Rotation").GetInt32() != page.GetProperty("Rotation").GetInt32())
                throw new QuickToolRefusal("The chosen pages differ in size or rotation. Crop them in separate steps.");
        }
        if (l + r >= width - 36 || t + b >= height - 36) throw new QuickToolRefusal("The margins leave less than 36 by 36 points of the page.");
        // Displayed margins -> absolute unrotated user-space rectangle (origin bottom-left, box origin zero).
        var (uw, uh) = quarter % 2 == 1 ? (height, width) : (width, height);
        var (cl, cb, cr, ct2) = quarter switch
        {
            1 => (t, l, uw - b, uh - r),
            2 => (r, t, uw - l, uh - b),
            3 => (b, r, uw - t, uh - l),
            _ => (l, b, uw - r, uh - t)
        };
        await Session.CropPagesAsync(Engine, new CropOptions(cl, cb, cr, ct2, pages), Session.Revision, ct);
        Changed = true; SetApplyAllowed(false);
        return $"Cropped {Plural(pages?.Length ?? Session.PageCount, "page")}. The hidden content is still in the file. Use Undo to restore the pages.";
    }
}

public sealed class FlattenWindow : QuickToolWindow
{
    readonly CheckBox annotations, forms;
    public FlattenWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Flatten annotations and forms", "Create flattened copy…", 620, 520)
    {
        AddLabel("Flatten annotations and forms", true);
        AddNote("Writes a NEW copy in which the chosen items become part of the page and can no longer be edited or moved. Your open document stays unchanged.");
        annotations = AddCheck("Annotations (comments, drawings, highlights)", "Flatten annotations", true);
        forms = AddCheck("Form fields (typed values become fixed text)", "Flatten form fields", true);
    }
    protected override string? Validate() => annotations.IsChecked == true || forms.IsChecked == true ? null : "Choose annotations, form fields or both.";
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var destination = ChooseNewFile("-flattened"); if (destination is null) return null;
        var r = await Session.FlattenCopyAsync(Engine, destination, annotations.IsChecked == true, forms.IsChecked == true, ct);
        ResultPath = destination; SetApplyAllowed(false);
        return $"Saved the flattened copy: {Plural(r.AnnotationsFlattened, "annotation")} and {Plural(r.FormFieldsFlattened, "form field")} flattened.";
    }
}

public sealed class PropertiesWindow : QuickToolWindow
{
    readonly TextBox title, author, subject, keywords;
    readonly CheckBox strip;
    PdfProperties? loaded;
    public PropertiesWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Document properties", "Apply properties", 640, 900)
    {
        AddLabel("Document properties", true);
        AddNote("Changes are applied to the open document as one undoable change. Hidden information inside pages (such as text under a crop) is not affected.");
        title = AddText("Title", "Document title", "", 1000); author = AddText("Author", "Document author", "", 1000);
        subject = AddText("Subject", "Document subject", "", 1000); keywords = AddText("Keywords", "Document keywords", "", 1000);
        strip = AddCheck("Remove all metadata (title, author, subject, keywords, creator, producer, dates and XMP)", "Remove all metadata", false);
        strip.Checked += (_, _) => SetFields(false); strip.Unchecked += (_, _) => SetFields(true);
        AddLabel("Read-only details");
        SetApplyAllowed(false);
    }
    void SetFields(bool on) { foreach (var box in new[] { title, author, subject, keywords }) box.IsEnabled = on; }
    TextBox AddReadOnly(string label, string value)
    {
        var box = AddText(label, label, value, 1000); box.IsReadOnly = true; return box;
    }
    protected override bool LoadsOnOpen => true;
    protected override async Task<string?> LoadAsync(CancellationToken ct)
    {
        var p = await Session.GetPropertiesAsync(Engine, ct); loaded = p;
        title.Text = p.Title; author.Text = p.Author; subject.Text = p.Subject; keywords.Text = p.Keywords;
        AddReadOnly("Creator", p.Creator); AddReadOnly("Producer", p.Producer); AddReadOnly("Created", p.CreationDate); AddReadOnly("Modified", p.ModDate);
        AddReadOnly("PDF version", p.PdfVersion); AddReadOnly("Pages", p.PageCount.ToString(CultureInfo.InvariantCulture));
        if (p.Encrypted) return "This document is password-protected, so its properties cannot be edited.";
        SetApplyAllowed(true);
        return p.HasXmp ? "This document also has XMP metadata; it is kept unless you choose Remove all metadata." : "";
    }
    protected override string? Validate()
    {
        if (loaded is null) return "The properties are not loaded.";
        if (strip.IsChecked == true) return null;
        foreach (var value in new[] { title.Text, author.Text, subject.Text, keywords.Text })
            if (value.Any(c => c < 0x20 || (c >= 0x7F && c <= 0x9F))) return "Properties cannot contain control characters.";
        return title.Text == loaded.Title && author.Text == loaded.Author && subject.Text == loaded.Subject && keywords.Text == loaded.Keywords
            ? "Nothing to change. Edit a field or choose Remove all metadata." : null;
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var all = strip.IsChecked == true;
        string? Diff(string now, string was) => now == was ? null : now;
        await Session.SetPropertiesAsync(Engine, Diff(title.Text, loaded!.Title), Diff(author.Text, loaded.Author), Diff(subject.Text, loaded.Subject), Diff(keywords.Text, loaded.Keywords), all, Session.Revision, ct);
        Changed = true; SetApplyAllowed(false);
        return all ? "All metadata was removed. Use Undo to restore it." : "Properties updated. Use Undo to restore the previous values.";
    }
}

public sealed class BookmarksWindow : QuickToolWindow
{
    List<BookmarkEntry> items = new();
    bool truncated, dirty;
    readonly ListBox list = new() { MinHeight = 200, MaxHeight = 320 };
    readonly TextBox titleBox, pageBox;
    public BookmarksWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Bookmarks", "Apply bookmarks", 680, 900)
    {
        AddLabel("Bookmarks", true);
        AddNote("Edit the outline, then choose Apply to replace the document's bookmarks as one undoable change. Deleting a bookmark also deletes the ones nested under it. Move keeps nested bookmarks together.");
        AutomationProperties.SetName(list, "Bookmarks in outline order"); Body.Children.Add(list);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex is >= 0 and var i && i < items.Count) { titleBox!.Text = items[i].Title; pageBox!.Text = items[i].Page >= 0 ? (items[i].Page + 1).ToString(CultureInfo.InvariantCulture) : ""; }
        };
        titleBox = AddText("Title of the selected bookmark", "Bookmark title", "", 500);
        pageBox = AddText($"Page of the selected bookmark (1 to {Session.PageCount})", "Bookmark page", "", 6);
        AddButtonRow(MakeButton("Add at current page", "Add bookmark at current page", (_, _) => Add()), MakeButton("Update selected", "Update selected bookmark from fields", (_, _) => Update()),
            MakeButton("Delete", "Delete selected bookmark", (_, _) => Delete()));
        AddButtonRow(MakeButton("Move up", "Move bookmark up", (_, _) => Move(-1)), MakeButton("Move down", "Move bookmark down", (_, _) => Move(1)),
            MakeButton("Indent", "Indent bookmark", (_, _) => Shift(1)), MakeButton("Outdent", "Outdent bookmark", (_, _) => Shift(-1)));
        SetApplyAllowed(false);
    }
    protected override bool LoadsOnOpen => true;
    protected override async Task<string?> LoadAsync(CancellationToken ct)
    {
        var result = await Session.GetBookmarksListAsync(Engine, ct);
        items = result.Bookmarks.ToList(); truncated = result.Truncated; Refresh(-1);
        if (truncated) { Body.IsEnabled = false; return "This document has more bookmarks than can be edited here. Editing is turned off so the existing outline is not damaged."; }
        return items.Count == 0 ? "This document has no bookmarks yet." : "";
    }
    static string Display(BookmarkEntry b) => $"{new string(' ', (b.Level - 1) * 4)}{b.Title}   ·   {(b.Page >= 0 ? "page " + (b.Page + 1) : "no page")}";
    void Refresh(int select)
    {
        list.Items.Clear(); foreach (var b in items) list.Items.Add(Display(b));
        if (select >= 0 && select < items.Count) list.SelectedIndex = select;
        SetApplyAllowed(dirty && !truncated);
    }
    void Mark(int select) { dirty = true; Refresh(select); Status.Text = ""; }
    int Selected => list.SelectedIndex is >= 0 and var i && i < items.Count ? i : -1;
    int BlockEnd(int i) { var j = i + 1; while (j < items.Count && items[j].Level > items[i].Level) j++; return j; }
    bool NeedSelection() { if (Selected >= 0) return false; Status.Text = "Select a bookmark first."; return true; }
    bool TryPage(out int page)
    {
        page = -1; var text = pageBox.Text.Trim();
        if (text.Length == 0) return true;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= Session.PageCount) { page = p - 1; return true; }
        Status.Text = $"Enter a page from 1 to {Session.PageCount}, or leave it empty for no destination."; return false;
    }
    bool TryTitle(out string title)
    {
        title = titleBox.Text.Trim();
        if (title.Length == 0 || title.Any(c => c < 0x20 || (c >= 0x7F && c <= 0x9F))) { Status.Text = "Enter a bookmark title (no control characters)."; return false; }
        return true;
    }
    void Add()
    {
        if (items.Count >= 5000) { Status.Text = "At most 5000 bookmarks are supported."; return; }
        var s = Selected; var title = titleBox.Text.Trim(); if (title.Length == 0 || title.Any(c => c < 0x20)) title = "New bookmark";
        var entry = new BookmarkEntry(title, CurrentPage, s >= 0 ? items[s].Level : 1);
        var at = s >= 0 ? BlockEnd(s) : items.Count; items.Insert(at, entry); Mark(at);
    }
    void Update()
    {
        if (NeedSelection() || !TryTitle(out var title) || !TryPage(out var page)) return;
        var i = Selected; items[i] = items[i] with { Title = title, Page = page }; Mark(i);
    }
    void Delete()
    {
        if (NeedSelection()) return;
        var i = Selected; items.RemoveRange(i, BlockEnd(i) - i); Mark(Math.Min(i, items.Count - 1));
    }
    void Move(int direction)
    {
        if (NeedSelection()) return;
        var i = Selected; var end = BlockEnd(i); var level = items[i].Level;
        if (direction < 0)
        {
            var p = i - 1; while (p >= 0 && items[p].Level > level) p--;
            if (p < 0 || items[p].Level != level) { Status.Text = "It is already the first bookmark at this level."; return; }
            var block = items.GetRange(i, end - i); items.RemoveRange(i, end - i); items.InsertRange(p, block); Mark(p);
        }
        else
        {
            if (end >= items.Count || items[end].Level != level) { Status.Text = "It is already the last bookmark at this level."; return; }
            var nextEnd = BlockEnd(end); var block = items.GetRange(i, end - i); items.RemoveRange(i, end - i);
            items.InsertRange(nextEnd - (end - i), block); Mark(nextEnd - (end - i));
        }
    }
    void Shift(int delta)
    {
        if (NeedSelection()) return;
        var i = Selected; var end = BlockEnd(i); var level = items[i].Level;
        if (delta > 0 && (i == 0 || level > items[i - 1].Level)) { Status.Text = "A bookmark can only be indented under the one above it."; return; }
        if (delta < 0 && level <= 1) { Status.Text = "It is already at the top level."; return; }
        if (delta < 0 && end < items.Count && items[end].Level >= level) { Status.Text = "Move it below its later siblings first; outdenting it here would adopt them as children."; return; }
        for (var k = i; k < end; k++) items[k] = items[k] with { Level = items[k].Level + delta };
        Mark(i);
    }
    protected override string? Validate() => truncated ? "Editing is turned off because the outline was too large to load completely." : dirty ? null : "Nothing to apply yet.";
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        await Session.SetBookmarksAsync(Engine, items, Session.Revision, ct);
        Changed = true; dirty = false; SetApplyAllowed(false);
        return $"Bookmarks updated ({Plural(items.Count, "bookmark")}). Use Undo to restore the previous outline.";
    }
}

public sealed class LinksWindow : QuickToolWindow
{
    List<PdfLinkInfo> links = new();
    readonly ListBox list = new() { MinHeight = 150, MaxHeight = 260 };
    readonly TextBox left, bottom, right, top, target;
    readonly ComboBox kind;
    public LinksWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Links", "Add link", 680, 960)
    {
        AddLabel($"Links on page {CurrentPage + 1}", true);
        AddNote("Each Remove or Add is one undoable change. Coordinates are PDF points from the bottom-left corner of the unrotated page (72 points = 1 inch).");
        AutomationProperties.SetName(list, "Links on this page"); Body.Children.Add(list);
        AddButtonRow(MakeButton("Remove selected link", "Remove selected link", async (_, _) => await RemoveAsync()));
        AddLabel("Add a link", true);
        left = AddText("Left (points)", "Link left", "72", 9); bottom = AddText("Bottom (points)", "Link bottom", "72", 9);
        right = AddText("Right (points)", "Link right", "172", 9); top = AddText("Top (points)", "Link top", "92", 9);
        kind = AddCombo("Link to", "Link type", 0, "Web or email address", "Page in this document");
        target = AddText("Address (https://, http:// or mailto:) or page number", "Link target", "https://", 2000);
    }
    protected override bool LoadsOnOpen => true;
    protected override async Task<string?> LoadAsync(CancellationToken ct) => await ReloadAsync(ct);
    async Task<string?> ReloadAsync(CancellationToken ct)
    {
        var result = await Session.ListLinksAsync(Engine, CurrentPage, ct);
        links = result.Links.ToList(); list.Items.Clear();
        foreach (var l in links)
        {
            var what = l.Uri.Length > 0 ? l.Uri : l.TargetPage >= 0 ? $"page {l.TargetPage + 1}" : l.Kind;
            list.Items.Add($"{l.Index + 1}. {what}   [{l.Left:0.#}, {l.Bottom:0.#}, {l.Right:0.#}, {l.Top:0.#}]");
        }
        return result.Truncated ? $"Showing the first {links.Count} of {result.Count} links." : links.Count == 0 ? "There are no links on this page." : "";
    }
    async Task RemoveAsync()
    {
        if (list.SelectedIndex is not (>= 0 and var i) || i >= links.Count) { Status.Text = "Select a link to remove."; return; }
        var link = links[i];
        await RunAsync(async ct =>
        {
            await Session.RemoveLinkAsync(Engine, link, CurrentPage, Session.Revision, ct);
            Changed = true; var rest = await ReloadAsync(ct);
            return "Link removed. Use Undo to bring it back." + (string.IsNullOrEmpty(rest) ? "" : " " + rest);
        }, "Removing the link…");
    }
    protected override string? Validate()
    {
        var pages = Session.PageCount;
        var values = new double[4]; var boxes = new[] { left, bottom, right, top };
        for (var k = 0; k < 4; k++) if (!TryNumber(boxes[k].Text, out values[k]) || Math.Abs(values[k]) > 1e6) return "Enter the link rectangle as numbers of points.";
        if (values[2] - values[0] < 1 || values[3] - values[1] < 1) return "The link area must be at least 1 by 1 point (Right greater than Left, Top greater than Bottom).";
        var text = target.Text.Trim();
        if (kind.SelectedIndex == 1) return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= pages ? null : $"Enter a page number from 1 to {pages}.";
        if (text.Length == 0 || text.Length > 2000 || text.Any(c => c < 0x21 || c > 0x7E)) return "Enter a web or email address using plain characters without spaces.";
        var ok = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        return ok ? null : "Only http, https and mailto links are allowed.";
    }
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        TryNumber(left.Text, out var l); TryNumber(bottom.Text, out var b); TryNumber(right.Text, out var r); TryNumber(top.Text, out var t);
        var text = target.Text.Trim();
        await Session.AddLinkAsync(Engine, CurrentPage, l, b, r, t, kind.SelectedIndex == 0 ? text : null,
            kind.SelectedIndex == 1 ? int.Parse(text, CultureInfo.InvariantCulture) - 1 : null, Session.Revision, ct);
        Changed = true; var rest = await ReloadAsync(ct);
        return "Link added. Use Undo to remove it." + (string.IsNullOrEmpty(rest) ? "" : " " + rest);
    }
}

public sealed class ExtractImagesWindow : QuickToolWindow
{
    readonly PageScope scope;
    public ExtractImagesWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Extract images", "Extract images…", 620, 520)
    {
        AddLabel("Extract images", true);
        AddNote("Saves the embedded pictures of the chosen pages as files in a NEW folder. Your document is unchanged. Vector drawings and text are not pictures and are not extracted.");
        scope = new PageScope(this, "Pages", 0);
    }
    protected override string? Validate() => scope.TryGet(out _, out var error) ? null : error;
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        scope.TryGet(out var pages, out _);
        var folder = ChooseNewFolder("-images"); if (folder is null) return null;
        var result = await Session.ExtractImagesAsync(Engine, folder, pages, ct);
        ResultPath = folder; SetApplyAllowed(false);
        return result.Count == 0 ? "No embedded images were found on those pages. An empty folder may have been created."
            : $"Extracted {Plural(result.Count, "image")} into the new folder." + (result.Truncated ? " The list shown is shortened, but every image was written." : "");
    }
}

public sealed class ExportImagesWindow : QuickToolWindow
{
    const int MaxEdge = 4096; const double MaxPixels = 8_000_000;
    readonly ComboBox format, dpi;
    readonly PageScope scope;
    public ExportImagesWindow(DocumentSession session, AdvancedEngineLocation engine, int currentPage)
        : base(session, engine, currentPage, "Export pages as images", "Export images…", 620, 640)
    {
        AddLabel("Export pages as images", true);
        AddNote("Renders each chosen page to a PNG or JPEG file in a NEW folder. Very large pages are rendered at a lower resolution to stay within 4096 pixels per side and about 8 megapixels; you are told when that happens. Your document is unchanged.");
        format = AddCombo("Format", "Image format", 0, "PNG (lossless)", "JPEG (smaller)");
        dpi = AddCombo("Resolution", "Image resolution", 1, "72 dpi", "150 dpi", "300 dpi");
        scope = new PageScope(this, "Pages", 0);
    }
    protected override string? Validate() => scope.TryGet(out _, out var error) ? null : error;
    protected override async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        scope.TryGet(out var chosen, out _);
        var pages = chosen ?? Enumerable.Range(0, Session.PageCount).ToArray();
        var png = format.SelectedIndex == 0; var wanted = new[] { 72, 150, 300 }[Math.Max(0, dpi.SelectedIndex)];
        var folder = ChooseNewFolder("-images"); if (folder is null) return null;
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null || !Directory.Exists(parent)) throw new QuickToolRefusal("The parent folder does not exist.");
        var sizes = (await Session.PageInfoAsync(ct)).GetProperty("Pages").EnumerateArray().ToArray();
        var stem = StemName; var extension = png ? "png" : "jpg"; var reduced = 0; var lowest = double.MaxValue;
        Directory.CreateDirectory(folder);
        Progress.IsIndeterminate = false; Progress.Maximum = pages.Length; Progress.Value = 0;
        try
        {
            for (var k = 0; k < pages.Length; k++)
            {
                ct.ThrowIfCancellationRequested();
                Status.Text = $"Exporting page {pages[k] + 1} ({k + 1} of {pages.Length})…";
                double pw = sizes[pages[k]].GetProperty("Width").GetDouble(), ph = sizes[pages[k]].GetProperty("Height").GetDouble();
                var scale = Math.Min(wanted / 72.0, Math.Min(MaxEdge / pw, Math.Min(MaxEdge / ph, Math.Sqrt(MaxPixels / (pw * ph)))));
                int w = Math.Max(1, (int)Math.Floor(pw * scale)), h = Math.Max(1, (int)Math.Floor(ph * scale));
                if (scale < wanted / 72.0 - 1e-6) { reduced++; lowest = Math.Min(lowest, scale * 72); }
                var data = await Session.RenderAsync(pages[k], w, h, ct);
                int rw = data.GetProperty("Width").GetInt32(), rh = data.GetProperty("Height").GetInt32(), stride = data.GetProperty("Stride").GetInt32();
                var pixels = Convert.FromBase64String(data.GetProperty("Bgra").GetString()!);
                var path = Path.Combine(folder, $"{stem}-page-{pages[k] + 1:D4}.{extension}"); var effective = Math.Max(1, Math.Min(wanted, scale * 72));
                await Task.Run(() =>
                {
                    var bitmap = BitmapSource.Create(rw, rh, effective, effective, PixelFormats.Bgr32, null, pixels, stride); bitmap.Freeze();
                    BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write); encoder.Save(stream);
                }, CancellationToken.None);
                Progress.Value = k + 1;
            }
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { Progress.IsIndeterminate = true; }
        ResultPath = folder; SetApplyAllowed(false);
        var note = reduced == 0 ? "" : $" {Plural(reduced, "page")} {(reduced == 1 ? "was" : "were")} rendered below {wanted} dpi (lowest {lowest:0} dpi) to stay within the size limit.";
        return $"Exported {Plural(pages.Length, "page")} as {(png ? "PNG" : "JPEG")} files into the new folder.{note}";
    }
}
