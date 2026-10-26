using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>One page of a comparison. <see cref="Page"/> is zero-based. <see cref="OnlyIn"/> names the document that has the page when the other does not.</summary>
internal sealed record ComparePageResult(int Page, string Status, int TextAdded, int TextRemoved, double PixelDifferencePercent, string? OnlyIn,
    IReadOnlyList<string>? DiffLines = null)
{
    public bool IsIdentical => OnlyIn is null && TextAdded == 0 && TextRemoved == 0 && PixelDifferencePercent <= 0;
    public string Label => $"Page {Page + 1} — {Status}";
}

/// <summary>Read-only comparison of the open document with another PDF: per-page text diff and rendered-pixel difference.
/// Neither document is modified; the other file is opened as a private recovery copy that is discarded on close.</summary>
internal sealed class CompareWindow : Window
{
    const int ScanWidth = 150, DetailWidth = 360, MaxDiffLines = 200, ChannelThreshold = 32;
    readonly DocumentSession current;
    readonly string workerExecutable, recoveryRoot;
    readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8), FontWeight = FontWeights.SemiBold };
    readonly TextBlock fileLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock progress = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Button choose = new() { Content = "Choose PDF to compare…", MinHeight = 44, Padding = new Thickness(16, 0, 16, 0) };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, MinWidth = 96, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button close = new() { Content = "Close", MinHeight = 44, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
    readonly CheckBox showIdentical = new() { Content = "Show identical pages", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly ListBox list = new() { Width = 280 };

    static string Pages(int count) => count == 1 ? "1 page" : $"{count:N0} pages";
    readonly Image imageA = new() { Stretch = Stretch.Uniform }, imageB = new() { Stretch = Stretch.Uniform }, imageOverlay = new() { Stretch = Stretch.Uniform };
    readonly TextBlock captionA = new() { TextWrapping = TextWrapping.Wrap }, captionB = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox diffText = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), MinHeight = 120,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
    readonly List<ComparePageResult> results = [];
    DocumentSession? other;
    string? otherFolder, otherName;
    JsonElement[] pagesA = [], pagesB = [];
    int pageCountA, pageCountB, scanned, total;
    bool running, cancelRequested, closeWhenIdle, cleaned;
    int detailToken, busy;
    Task detailTask = Task.CompletedTask;

    /// <summary>Lets tests supply the second file instead of showing the file dialog.</summary>
    internal Func<string?>? PickPath { get; set; }
    internal IReadOnlyList<ComparePageResult> Results => results;
    internal string SummaryText => summary.Text;
    /// <summary>Completes once the other document is closed and its temporary recovery folder removed (after the window closes).</summary>
    internal Task CleanupTask { get; private set; } = Task.CompletedTask;
    internal string? TemporaryFolder => otherFolder;

    internal CompareWindow(DocumentSession current, string workerExecutable, string recoveryRoot)
    {
        this.current = current; this.workerExecutable = workerExecutable; this.recoveryRoot = recoveryRoot;
        Title = "Compare PDFs"; Width = 1240; Height = 860; MinWidth = 900; MinHeight = 600; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;

        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Compare PDFs", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Compares this document, as it is open now, with another PDF page by page: extracted text lines and the rendered appearance. Nothing is changed in either file; the other file is read from a private temporary copy.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var pick = new StackPanel { Orientation = Orientation.Horizontal }; pick.Children.Add(choose); pick.Children.Add(showIdentical); header.Children.Add(pick);
        header.Children.Add(fileLine); header.Children.Add(summary);
        var progressRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(cancel, Dock.Right); progressRow.Children.Add(cancel); progressRow.Children.Add(progress); header.Children.Add(progressRow);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(close); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var body = new Grid(); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        list.ItemTemplate = new DataTemplate { VisualTree = ListItem() };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.Margin = new Thickness(0, 0, 16, 0); body.Children.Add(list);

        var detail = new DockPanel(); Grid.SetColumn(detail, 1); body.Children.Add(detail);
        var images = new UniformGrid { Columns = 3 }; DockPanel.SetDock(images, Dock.Top); detail.Children.Add(images);
        images.Children.Add(Pane(captionA, imageA)); images.Children.Add(Pane(captionB, imageB));
        images.Children.Add(Pane(new TextBlock { Text = "Differences (red)" }, imageOverlay));
        var diffLabel = new TextBlock { Text = "Text differences (+ added in the other file, − only in this document)", Margin = new Thickness(0, 12, 0, 4) };
        DockPanel.SetDock(diffLabel, Dock.Top); detail.Children.Add(diffLabel); detail.Children.Add(diffText);

        foreach (var text in new[] { summary, fileLine, progress, captionA, captionB, diffLabel }) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var text in header.Children.OfType<TextBlock>()) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(choose, "Choose a PDF to compare with this document"); AutomationProperties.SetName(cancel, "Stop comparing after the current page");
        AutomationProperties.SetName(showIdentical, "Show identical pages in the list"); AutomationProperties.SetName(list, "Compared pages");
        AutomationProperties.SetName(imageA, "This document's page"); AutomationProperties.SetName(imageB, "The other document's page");
        AutomationProperties.SetName(imageOverlay, "Overlay of differences in red"); AutomationProperties.SetName(diffText, "Text differences for the selected page");
        AutomationProperties.SetName(close, "Close the comparison");

        choose.Click += async (_, _) => await ChooseAsync();
        cancel.Click += (_, _) => { cancelRequested = true; cancel.IsEnabled = false; progress.Text = "Stopping after the current page…"; };
        close.Click += (_, _) => Close();
        showIdentical.Click += (_, _) => Refill();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ComparePageResult page) detailTask = ShowDetailAsync(page); };
        Loaded += (_, _) => { if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } };
        Closing += (_, args) =>
        {
            // Wait for the page in flight: a worker request is allowed to finish rather than being cancelled.
            if (running || busy > 0) { args.Cancel = true; cancelRequested = true; closeWhenIdle = true; progress.Text = "Closing after the current page…"; }
        };
        Closed += (_, _) => CleanupTask = CleanupAsync();
        summary.Text = "Choose a PDF to compare with " + Path.GetFileName(current.OriginalPath) + ".";
    }

    static FrameworkElementFactory ListItem()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ComparePageResult.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 6, 2, 6));
        return text;
    }

    static StackPanel Pane(TextBlock caption, Image image)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        caption.Margin = new Thickness(0, 0, 0, 4); caption.TextTrimming = TextTrimming.CharacterEllipsis; caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        image.MaxWidth = DetailWidth;
        panel.Children.Add(caption); panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = image });
        return panel;
    }

    async Task ChooseAsync()
    {
        if (running) return;
        string? path;
        if (PickPath is not null) path = PickPath();
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose PDF to compare", Filter = "PDF documents (*.pdf)|*.pdf", CheckFileExists = true };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (!string.IsNullOrWhiteSpace(path)) await CompareAsync(path);
    }

    /// <summary>Opens <paramref name="otherPath"/> as side B and compares every page. Failures are reported in the summary line; nothing is thrown for an unreadable or encrypted file.</summary>
    internal async Task CompareAsync(string otherPath)
    {
        if (running) return;
        running = true; cancelRequested = false; choose.IsEnabled = false; showIdentical.IsEnabled = false;
        results.Clear(); list.ItemsSource = null; ClearDetail(); fileLine.Text = ""; scanned = 0;
        try
        {
            await ReleaseOtherAsync();
            otherName = Path.GetFileName(otherPath);
            progress.Text = "Opening " + otherName + "…";
            otherFolder = Path.Combine(recoveryRoot, "compare-" + Guid.NewGuid().ToString("N"));
            try { other = await DocumentSession.OpenAsync(workerExecutable, new RecoveryStore(otherFolder), otherPath); }
            catch (DocumentPasswordRequiredException)
            {
                await ReleaseOtherAsync(); summary.Text = $"{otherName} is password-protected and cannot be compared."; progress.Text = ""; return;
            }
            catch (Exception error)
            {
                await ReleaseOtherAsync(); summary.Text = $"{otherName} could not be opened for comparison: {error.Message}"; progress.Text = ""; return;
            }
            pageCountA = current.PageCount; pageCountB = other.PageCount; total = Math.Max(pageCountA, pageCountB);
            pagesA = (await current.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            pagesB = (await other.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            fileLine.Text = $"This document: {Path.GetFileName(current.OriginalPath)} ({Pages(pageCountA)})   ·   Other: {otherName} ({Pages(pageCountB)})";
            cancel.IsEnabled = true;
            for (var page = 0; page < total; page++)
            {
                if (cancelRequested) break;
                progress.Text = $"Comparing page {page + 1} of {total}…";
                results.Add(await ComparePageAsync(page));
                scanned = page + 1; UpdateSummary(); Refill();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
            progress.Text = cancelRequested ? $"Stopped after {scanned} of {total} pages." : "";
            UpdateSummary();
        }
        catch (Exception error) { summary.Text = "The comparison stopped: " + error.Message; progress.Text = ""; }
        finally
        {
            running = false; cancel.IsEnabled = false; choose.IsEnabled = true; showIdentical.IsEnabled = true;
            if (closeWhenIdle && busy == 0) Close();
        }
    }

    async Task<ComparePageResult> ComparePageAsync(int page)
    {
        if (page >= pageCountB) return new ComparePageResult(page, "Only in this document", 0, 0, 0, "this document");
        if (page >= pageCountA) return new ComparePageResult(page, "Only in " + otherName, 0, 0, 0, otherName);
        var linesA = Lines(await current.ExtractAsync(page)); var linesB = Lines(await other!.ExtractAsync(page));
        var diff = Diff(linesA, linesB, out var added, out var removed);
        var (w, h) = ScanSize(page);
        var a = Pixels(await current.RenderAsync(page, w, h)); var b = Pixels(await other.RenderAsync(page, w, h));
        var percent = PixelPercent(a, b, null);
        var parts = new List<string>();
        if (added + removed > 0) parts.Add($"Text differs (+{added} −{removed} lines)");
        if (percent > 0) parts.Add($"Appearance differs ({percent.ToString("0.0", CultureInfo.InvariantCulture)}% of pixels)");
        return new ComparePageResult(page, parts.Count == 0 ? "Identical" : string.Join("; ", parts), added, removed, percent, null, diff);
    }

    (int Width, int Height) ScanSize(int page) => SizeFor(page, ScanWidth);
    (int Width, int Height) SizeFor(int page, int width)
    {
        var info = page < pagesA.Length ? pagesA[page] : pagesB[page];
        double pw = info.GetProperty("Width").GetDouble(), ph = info.GetProperty("Height").GetDouble();
        return (width, Math.Max(1, (int)Math.Round(width * (pw > 0 ? ph / pw : 1.3))));
    }

    static string[] Lines(JsonElement extracted) =>
        (extracted.GetProperty("Text").GetString() ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

    /// <summary>Line-level longest-common-subsequence diff. Very large pages fall back to comparing line counts so memory stays bounded.</summary>
    static List<string> Diff(string[] a, string[] b, out int added, out int removed)
    {
        var lines = new List<string>(); added = removed = 0;
        if ((long)(a.Length + 1) * (b.Length + 1) > 4_000_000)
        {
            var left = a.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()); var right = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var (line, count) in left) { var extra = count - right.GetValueOrDefault(line); if (extra > 0) { removed += extra; for (var i = 0; i < extra; i++) lines.Add("− " + line); } }
            foreach (var (line, count) in right) { var extra = count - left.GetValueOrDefault(line); if (extra > 0) { added += extra; for (var i = 0; i < extra; i++) lines.Add("+ " + line); } }
            return lines;
        }
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) { lines.Add("+ " + b[y++]); added++; }
            else { lines.Add("− " + a[x++]); removed++; }
        }
        return lines;
    }

    sealed record Raster(int Width, int Height, int Stride, byte[] Bgra);
    static Raster Pixels(JsonElement data) => new(data.GetProperty("Width").GetInt32(), data.GetProperty("Height").GetInt32(),
        data.GetProperty("Stride").GetInt32(), Convert.FromBase64String(data.GetProperty("Bgra").GetString()!));

    /// <summary>Percentage of pixels where any colour channel differs by more than the threshold; optionally paints a grayscale-plus-red overlay of A.</summary>
    static double PixelPercent(Raster a, Raster b, byte[]? overlay)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 100;
        long differing = 0;
        for (var row = 0; row < a.Height; row++)
            for (var column = 0; column < a.Width; column++)
            {
                var ia = row * a.Stride + column * 4; var ib = row * b.Stride + column * 4;
                var different = Math.Abs(a.Bgra[ia] - b.Bgra[ib]) > ChannelThreshold || Math.Abs(a.Bgra[ia + 1] - b.Bgra[ib + 1]) > ChannelThreshold
                    || Math.Abs(a.Bgra[ia + 2] - b.Bgra[ib + 2]) > ChannelThreshold;
                if (different) differing++;
                if (overlay is null) continue;
                var io = row * a.Width * 4 + column * 4;
                if (different) { overlay[io] = 0; overlay[io + 1] = 0; overlay[io + 2] = 230; }
                else { var gray = (byte)Math.Min(255, (a.Bgra[ia] * 114 + a.Bgra[ia + 1] * 587 + a.Bgra[ia + 2] * 299) / 1000 / 2 + 128); overlay[io] = overlay[io + 1] = overlay[io + 2] = gray; }
                overlay[io + 3] = 255;
            }
        return 100d * differing / ((long)a.Width * a.Height);
    }

    static BitmapSource Bitmap(Raster raster)
    {
        var bitmap = BitmapSource.Create(raster.Width, raster.Height, 96, 96, PixelFormats.Bgr32, null, raster.Bgra, raster.Stride); bitmap.Freeze(); return bitmap;
    }

    void UpdateSummary()
    {
        var different = results.Count(r => !r.IsIdentical);
        var stopped = scanned < total ? $"Stopped after {scanned} of {total} pages: " : $"{total} {(total == 1 ? "page" : "pages")} compared: ";
        summary.Text = stopped +$"{results.Count - different} identical, {different} different";
    }

    void Refill()
    {
        var selected = (list.SelectedItem as ComparePageResult)?.Page;
        var shown = results.Where(r => showIdentical.IsChecked == true || !r.IsIdentical).ToArray();
        list.ItemsSource = shown;
        if (selected is not null) list.SelectedItem = shown.FirstOrDefault(r => r.Page == selected);
    }

    void ClearDetail() { detailToken++; imageA.Source = imageB.Source = imageOverlay.Source = null; diffText.Clear(); captionA.Text = captionB.Text = ""; }

    /// <summary>Selects a page (showing it in the list if it is identical) and waits for its detail view. For tests.</summary>
    internal async Task SelectPageAsync(int page)
    {
        var result = results.FirstOrDefault(r => r.Page == page) ?? throw new InvalidOperationException("No result for page " + (page + 1));
        if (result.IsIdentical && showIdentical.IsChecked != true) { showIdentical.IsChecked = true; Refill(); }
        list.SelectedItem = list.Items.OfType<ComparePageResult>().First(r => r.Page == page);
        await detailTask;
    }

    async Task ShowDetailAsync(ComparePageResult page)
    {
        var token = ++detailToken; busy++;
        try
        {
            captionA.Text = "This document"; captionB.Text = otherName ?? "Other document";
            imageA.Source = imageB.Source = imageOverlay.Source = null;
            var lines = new List<string>(page.DiffLines ?? []);
            if (lines.Count > MaxDiffLines) { var more = lines.Count - MaxDiffLines; lines.RemoveRange(MaxDiffLines, more); lines.Add($"… {more} more lines not shown"); }
            diffText.Text = page.OnlyIn is not null ? $"This page exists only in {page.OnlyIn}." : lines.Count == 0 ? "No text differences." : string.Join(Environment.NewLine, lines);
            var (w, h) = SizeFor(page.Page, DetailWidth);
            Raster? a = null, b = null;
            if (page.Page < pageCountA) a = Pixels(await current.RenderAsync(page.Page, w, h));
            if (other is not null && page.Page < pageCountB) b = Pixels(await other.RenderAsync(page.Page, w, h));
            if (token != detailToken) return;
            if (a is not null) imageA.Source = Bitmap(a);
            if (b is not null) imageB.Source = Bitmap(b);
            if (a is not null && b is not null)
            {
                var overlay = new byte[a.Width * a.Height * 4]; PixelPercent(a, b, overlay);
                var source = BitmapSource.Create(a.Width, a.Height, 96, 96, PixelFormats.Bgr32, null, overlay, a.Width * 4); source.Freeze(); imageOverlay.Source = source;
            }
        }
        catch (Exception error) { if (token == detailToken) diffText.Text = "The page could not be shown: " + error.Message; }
        finally { busy--; if (closeWhenIdle && !running && busy == 0) Close(); }
    }

    async Task ReleaseOtherAsync()
    {
        var session = other; var folder = otherFolder; other = null; otherFolder = null;
        if (session is not null)
        {
            try { await session.CloseAsync(discardUnsaved: true); } catch { }
            session.Dispose();
        }
        DeleteFolder(folder);
    }

    static void DeleteFolder(string? folder)
    {
        if (folder is null) return;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    async Task CleanupAsync()
    {
        if (cleaned) return;
        cleaned = true; ClearDetail();
        await ReleaseOtherAsync();
    }
}
