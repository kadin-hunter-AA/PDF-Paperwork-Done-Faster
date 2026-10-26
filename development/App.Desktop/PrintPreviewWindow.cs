using System.Globalization;
using System.Printing;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class PrintPreviewWindow : Window
{
    readonly DocumentSession session;
    readonly int[] pages;
    readonly IReadOnlyList<PrintPageSize> pageSizes;
    readonly PrintQueue queue;
    readonly PrintTicket? baseTicket;
    readonly double printableWidth, printableHeight; // portrait points
    readonly bool ticketLandscape;
    readonly Image preview = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock sheetLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    readonly Button previous = new() { Content = "Previous sheet", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5) };
    readonly Button next = new() { Content = "Next sheet", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
    readonly Button print = new() { Content = "Print", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
    readonly Button cancel = new() { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
    readonly ComboBox scaling = new() { MinWidth = 150 };
    readonly TextBox customPercent = new() { Text = "100", Width = 56, Margin = new Thickness(6, 0, 0, 0) };
    readonly ComboBox perSheet = new() { MinWidth = 150 };
    readonly CheckBox autoOrientation = new() { Content = "Automatic orientation", IsChecked = true };
    readonly ComboBox duplex = new() { MinWidth = 220 };
    readonly CheckBox asImage = new() { Content = "Print as image (300 DPI)" };
    /// <summary>Preselects image output, e.g. for pages with form fields whose values vector output may omit.</summary>
    internal bool PrintAsImage { get => asImage.IsChecked == true; set => asImage.IsChecked = value; }
    readonly List<Duplexing?> duplexChoices = new();
    CancellationTokenSource? operationCancellation;
    int sheetIndex;
    bool working, closeAfterCancel, allowClose;

    internal PrintPreviewWindow(DocumentSession session, int[] pages, IReadOnlyList<PrintPageSize> pageSizes,
        PrintQueue queue, PrintTicket? ticket, double printableWidthDips, double printableHeightDips)
    {
        this.session = session;
        this.pages = pages.ToArray();
        this.pageSizes = pageSizes;
        this.queue = queue;
        baseTicket = ticket;
        ticketLandscape = ticket?.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
        // The dialog reports the printable area in 1/96 inch for the ticket's orientation; plan in portrait points.
        var w = printableWidthDips * 0.75; var h = printableHeightDips * 0.75;
        (printableWidth, printableHeight) = ticketLandscape ? (h, w) : (w, h);
        Title = "Print preview";
        AutomationProperties.SetName(this, "Print preview for selected PDF pages");
        Width = 980; Height = 800; MinWidth = 700; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var item in new[] { "Fit to page", "Actual size (100%, large pages are cropped)", "Custom scale %" }) scaling.Items.Add(item);
        scaling.SelectedIndex = 0;
        foreach (var item in new[] { "1 page per sheet", "2 pages per sheet", "4 pages per sheet" }) perSheet.Items.Add(item);
        perSheet.SelectedIndex = 0;
        BuildDuplexChoices();
        Content = CreateLayout();
        previous.Click += async (_, _) => await ShowSheetAsync(sheetIndex - 1);
        next.Click += async (_, _) => await ShowSheetAsync(sheetIndex + 1);
        print.Click += async (_, _) => await PrintAsync();
        cancel.Click += (_, _) => CancelOrClose();
        scaling.SelectionChanged += async (_, _) => await OptionsChangedAsync();
        perSheet.SelectionChanged += async (_, _) => await OptionsChangedAsync();
        customPercent.LostFocus += async (_, _) => await OptionsChangedAsync();
        autoOrientation.Click += async (_, _) => await OptionsChangedAsync();
        Loaded += async (_, _) => await ShowSheetAsync(0);
        Closing += Window_Closing;
        UpdateNavigation();
    }

    void BuildDuplexChoices()
    {
        duplexChoices.Add(null); duplex.Items.Add("Printer default (from the Print dialog)");
        try
        {
            var caps = queue.GetPrintCapabilities(baseTicket);
            var supported = caps.DuplexingCapability;
            if (supported.Contains(Duplexing.OneSided)) { duplexChoices.Add(Duplexing.OneSided); duplex.Items.Add("One-sided"); }
            if (supported.Contains(Duplexing.TwoSidedLongEdge)) { duplexChoices.Add(Duplexing.TwoSidedLongEdge); duplex.Items.Add("Two-sided, flip on long edge"); }
            if (supported.Contains(Duplexing.TwoSidedShortEdge)) { duplexChoices.Add(Duplexing.TwoSidedShortEdge); duplex.Items.Add("Two-sided, flip on short edge"); }
        }
        catch (Exception e) when (e is PrintQueueException or PrintSystemException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        duplex.SelectedIndex = 0;
        duplex.IsEnabled = duplexChoices.Count > 2;
        duplex.ToolTip = duplex.IsEnabled ? null : "This printer does not report two-sided printing.";
    }

    UIElement CreateLayout()
    {
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "Review the selected pages", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        header.Children.Add(new TextBlock
        {
            Text = "Pages print one at a time as vector output (sharp text and lines) unless you choose Print as image. The preview is an approximate image of each sheet. Copies and the printer come from the Print dialog"
                + (baseTicket?.CopyCount is > 1 ? $" ({baseTicket.CopyCount} copies)." : "."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray
        });
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        options.Children.Add(Labeled("Scaling", new StackPanel { Orientation = Orientation.Horizontal, Children = { scaling, customPercent } }));
        options.Children.Add(Labeled("Layout", perSheet));
        options.Children.Add(Labeled("Orientation", autoOrientation));
        options.Children.Add(Labeled("Two-sided", duplex));
        options.Children.Add(Labeled("Output", asImage));
        header.Children.Add(options);
        AutomationProperties.SetName(scaling, "Page scaling"); AutomationProperties.SetName(customPercent, "Custom scale percent");
        AutomationProperties.SetName(perSheet, "Pages per sheet"); AutomationProperties.SetName(duplex, "Two-sided printing");
        AutomationProperties.SetName(autoOrientation, "Automatic orientation"); AutomationProperties.SetName(asImage, "Print as image");
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(status);
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right); buttons.Children.Add(cancel);
        DockPanel.SetDock(print, Dock.Right); buttons.Children.Add(print);
        DockPanel.SetDock(next, Dock.Right); buttons.Children.Add(next);
        DockPanel.SetDock(sheetLabel, Dock.Right); buttons.Children.Add(sheetLabel);
        buttons.Children.Add(previous);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(preview, "Rendered print preview sheet");
        scroll.Content = new Border { Background = Brushes.Gainsboro, Padding = new Thickness(10), Child = preview };
        root.Children.Add(scroll);
        return root;
    }

    static UIElement Labeled(string label, UIElement control) => new StackPanel
    {
        Margin = new Thickness(0, 0, 18, 6),
        Children = { new TextBlock { Text = label, Foreground = Brushes.DimGray, FontSize = 11 }, control }
    };

    PrintOptions CurrentOptions()
    {
        var percent = 100d;
        if (scaling.SelectedIndex == 2 && !double.TryParse(customPercent.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
            throw new InvalidOperationException("Enter a custom scale from 1% to 1000%.");
        var options = new PrintOptions(scaling.SelectedIndex switch { 1 => PrintScaling.ActualSize, 2 => PrintScaling.Custom, _ => PrintScaling.FitToPage },
            percent, perSheet.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 }, autoOrientation.IsChecked == true, asImage.IsChecked == true, baseTicket?.CopyCount ?? 1);
        PrintLayout.Validate(options);
        return options;
    }

    PrintTicket? EffectiveTicket()
    {
        if (baseTicket is null) return null;
        var clone = baseTicket.Clone();
        if (duplex.SelectedIndex >= 0 && duplexChoices[duplex.SelectedIndex] is { } d) clone.Duplexing = d;
        return clone;
    }

    int SheetCount(PrintOptions options) => (pages.Length + options.PagesPerSheet - 1) / options.PagesPerSheet;

    async Task OptionsChangedAsync()
    {
        if (!IsLoaded || working) return;
        customPercent.IsEnabled = scaling.SelectedIndex == 2 && perSheet.SelectedIndex == 0;
        scaling.IsEnabled = perSheet.SelectedIndex == 0;
        if (perSheet.SelectedIndex != 0) status.Text = "Several pages per sheet always fit each page to its cell.";
        sheetIndex = 0;
        await ShowSheetAsync(0);
    }

    async Task ShowSheetAsync(int index)
    {
        if (working) return;
        PrintOptions options;
        try { options = CurrentOptions(); }
        catch (InvalidOperationException error) { status.Text = error.Message; print.IsEnabled = false; return; }
        if (index < 0 || index >= SheetCount(options)) return;
        sheetIndex = index;
        UpdateNavigation();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var token = operationCancellation.Token;
        working = true; SetControls();
        status.Text = $"Rendering preview for sheet {index + 1}…";
        try
        {
            preview.Source = await RenderSheetPreviewAsync(options, index, token);
            status.Text = "Preview ready. Choose Print to send every selected page to the printer.";
            print.IsEnabled = true;
        }
        catch (OperationCanceledException) { status.Text = "Preview rendering was canceled. No print job was sent."; }
        catch (Exception error) { preview.Source = null; status.Text = error.Message; print.IsEnabled = false; }
        finally
        {
            working = false; SetControls();
            if (closeAfterCancel) CloseAfterOperation();
        }
    }

    async Task<BitmapSource> RenderSheetPreviewAsync(PrintOptions options, int index, CancellationToken cancellation)
    {
        var group = pages.Skip(index * options.PagesPerSheet).Take(options.PagesPerSheet).ToArray();
        var plan = PrintLayout.PlanSheet(group, pageSizes, options, printableWidth, printableHeight, ticketLandscape);
        var zoom = Math.Min(760 / plan.AreaWidth, 560 / plan.AreaHeight);
        var visual = new DrawingVisual();
        var images = new List<(PrintPlacement Placement, BitmapSource Bitmap)>();
        foreach (var placement in plan.Placements)
        {
            var longest = Math.Clamp(Math.Max(placement.Target.Width, placement.Target.Height) * zoom * 1.5, 200, 1600);
            var size = pageSizes[placement.PageIndex];
            var ratio = size.Width / size.Height;
            var (pw, ph) = ratio >= 1 ? ((int)longest, Math.Max(1, (int)(longest / ratio))) : (Math.Max(1, (int)(longest * ratio)), (int)longest);
            var data = await session.RenderForPrintAsync(placement.PageIndex, pw, ph, cancellation);
            cancellation.ThrowIfCancellationRequested();
            images.Add((placement, ToBitmap(data)));
        }
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new Rect(0, 0, plan.AreaWidth * zoom, plan.AreaHeight * zoom));
            foreach (var (p, bitmap) in images)
            {
                dc.PushClip(new RectangleGeometry(new Rect(p.Clip.X * zoom, p.Clip.Y * zoom, p.Clip.Width * zoom, p.Clip.Height * zoom)));
                dc.DrawImage(bitmap, new Rect(p.Target.X * zoom, p.Target.Y * zoom, p.Target.Width * zoom, p.Target.Height * zoom));
                dc.Pop();
            }
        }
        var target = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(plan.AreaWidth * zoom)) + 1, Math.Max(1, (int)Math.Ceiling(plan.AreaHeight * zoom)) + 1, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    async Task PrintAsync()
    {
        if (working || preview.Source is null) return;
        PrintOptions options;
        try { options = CurrentOptions(); }
        catch (InvalidOperationException error) { status.Text = error.Message; return; }
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var token = operationCancellation.Token;
        working = true; print.IsEnabled = false; SetControls();
        GdiPrintSink? sink = null;
        SessionPrintPageSource? source = null;
        try
        {
            status.Text = "Opening the printer…";
            sink = new GdiPrintSink(queue, EffectiveTicket());
            source = new SessionPrintPageSource(session);
            var summary = await PrintPipeline.RunJobAsync(sink, source, pages, pageSizes, options, "PDF document",
                p => status.Text = p.ToString() + " Cancel stops the job.", token);
            var extra = summary.Notes.Count > 0 ? " " + string.Join(" ", summary.Notes.Take(3)) : "";
            status.Text = $"The printer accepted {summary.Pages} page(s) on {summary.Sheets} sheet(s) ({summary.VectorPages} vector, {summary.ImagePages} image).{extra}";
            allowClose = true; DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            status.Text = "Printing was canceled and the print job was aborted. The printer may still print pages it had already received.";
            allowClose = true; DialogResult = false;
        }
        catch (Exception error)
        {
            status.Text = "Printing stopped and the job was aborted: " + error.Message;
            allowClose = true; DialogResult = false;
        }
        finally
        {
            working = false;
            sink?.Dispose(); source?.Dispose();
            operationCancellation?.Dispose(); operationCancellation = null;
            if (closeAfterCancel) CloseAfterOperation();
        }
    }

    static BitmapSource ToBitmap(JsonElement rendered)
    {
        var width = rendered.GetProperty("Width").GetInt32();
        var height = rendered.GetProperty("Height").GetInt32();
        var stride = rendered.GetProperty("Stride").GetInt32();
        var bytes = Convert.FromBase64String(rendered.GetProperty("Bgra").GetString()!);
        if (width < 1 || height < 1 || stride < width * 4 || bytes.Length != checked(stride * height))
            throw new InvalidOperationException("The printer returned an invalid raster page.");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }

    void CancelOrClose()
    {
        if (working)
        {
            closeAfterCancel = true;
            status.Text = "Stopping the current operation…";
            operationCancellation?.Cancel();
        }
        else DialogResult = false;
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (working && !allowClose)
        {
            e.Cancel = true;
            closeAfterCancel = true;
            operationCancellation?.Cancel();
        }
        else operationCancellation?.Cancel();
    }

    void CloseAfterOperation()
    {
        closeAfterCancel = false;
        allowClose = true;
        DialogResult = false;
    }

    void UpdateNavigation()
    {
        int count;
        try { count = SheetCount(CurrentOptions()); } catch (InvalidOperationException) { count = 1; }
        sheetLabel.Text = pages.Length == 0 ? "No pages" : $"Sheet {sheetIndex + 1} of {count} ({pages.Length} page(s) selected)";
        previous.IsEnabled = !working && sheetIndex > 0;
        next.IsEnabled = !working && sheetIndex + 1 < count;
        AutomationProperties.SetName(sheetLabel, sheetLabel.Text);
    }

    void SetControls()
    {
        UpdateNavigation();
        if (working) print.IsEnabled = false;
        cancel.Content = working ? "Cancel operation" : "Cancel";
        scaling.IsEnabled = !working && perSheet.SelectedIndex == 0;
        customPercent.IsEnabled = !working && scaling.SelectedIndex == 2 && perSheet.SelectedIndex == 0;
        perSheet.IsEnabled = autoOrientation.IsEnabled = asImage.IsEnabled = !working;
        duplex.IsEnabled = !working && duplexChoices.Count > 2;
    }
}
