using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class DrawingUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-drawing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "shell-recovery")), new TestDialogs())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        owner.Show();
        await WaitUntilAsync(() => !owner.IsBusy, "The desktop owner did not finish loading.");
        using var session = await DocumentSession.OpenAsync(worker,
            new RecoveryStore(Path.Combine(output, "document-recovery")), source);
        var before = await session.ExtractAsync(0);
        var originalAnnotationCount = before.GetProperty("Annotations").GetArrayLength();
        var originalText = before.GetProperty("Text").GetString();

        owner.SetTheme(ShellTheme.Light);
        var light = new DrawingWindow(session, 0) { Owner = owner, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        Check(ShowAndInteract(light, async dialog =>
        {
            var kinds = Find<ComboBox>(dialog, "Native PDF drawing annotation type");
            Check(kinds.Items.Cast<object>().Select(x => x.ToString()).SequenceEqual(["Line", "Arrow", "Rectangle", "Ellipse", "Pen", "Text box"]),
                "Drawing dialog exposes line, arrow, rectangle, ellipse, pen and text box choices");
            var left = Find<TextBox>(dialog, "Left coordinate in PDF points");
            var bottom = Find<TextBox>(dialog, "Bottom coordinate in PDF points");
            var right = Find<TextBox>(dialog, "Right coordinate in PDF points");
            var top = Find<TextBox>(dialog, "Top coordinate in PDF points");
            left.Text = "-1"; bottom.Text = "500"; right.Text = "200"; top.Text = "560";
            Find<Button>(dialog, "Add drawing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Find<TextBlock>(dialog, "Drawing validation and operation status").Text.Contains("inside the selected page", StringComparison.Ordinal),
                "Invalid drawing bounds were not reported.");
            Check(!session.IsDirty, "Invalid drawing coordinates leave the document unchanged");
            Capture(dialog, Path.Combine(output, "drawing-light.png"), 560, 620);
            dialog.Close();
            await WaitUntilAsync(() => !dialog.IsVisible, "The light drawing dialog did not close.");
        }) == false, "Light-theme coordinate validation leaves the drawing dialog open until user closes it");

        owner.SetTheme(ShellTheme.Dark);
        var dark = new DrawingWindow(session, 0) { Owner = owner, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        Check(ShowAndInteract(dark, async dialog =>
        {
            Find<ComboBox>(dialog, "Native PDF drawing annotation type").SelectedItem = "Arrow";
            await PlaceFromPreviewAsync(dialog, output, Check);
            Check(Find<TextBox>(dialog, "Left coordinate in PDF points").Text == "100" &&
                  Find<TextBox>(dialog, "Bottom coordinate in PDF points").Text == "500" &&
                  Find<TextBox>(dialog, "Right coordinate in PDF points").Text == "200" &&
                  Find<TextBox>(dialog, "Top coordinate in PDF points").Text == "560",
                "Page placement returns exact PDF bounds to the editable drawing fields");
            Check(!session.IsDirty, "Selecting and accepting page bounds does not commit an annotation");
            var apply = Find<Button>(dialog, "Add drawing");
            Check(apply.IsEnabled, "Valid coordinates enable the real Add drawing control");
            Capture(dialog, Path.Combine(output, "drawing-dark.png"), 560, 620);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = Find<TextBlock>(dialog, "Drawing validation and operation status");
            await WaitUntilAsync(() => !dialog.IsVisible || (apply.IsEnabled && status.Text != "Bounds are checked against the selected page before an annotation is added."),
                "The drawing action did not commit and close the dialog.", 30);
            if (dialog.IsVisible) throw new Exception("Drawing refused: " + status.Text);
        }) == true, "Dark-theme drawing commits through the real Add drawing control");

        Check(session.IsDirty && session.CanUndo, "The accepted drawing is a reversible document edit");
        var after = await session.ExtractAsync(0);
        var added = after.GetProperty("Annotations").EnumerateArray()
            .Single(annotation => annotation.GetProperty("Subtype").GetInt32() == 15 &&
                annotation.GetProperty("Left").GetDouble() == 100 && annotation.GetProperty("Bottom").GetDouble() == 500);
        Check(added.GetProperty("Left").GetDouble() == 100 && added.GetProperty("Bottom").GetDouble() == 500 &&
              added.GetProperty("Right").GetDouble() == 200 && added.GetProperty("Top").GetDouble() == 560,
            "The real controls commit the requested arrow bounds");
        Check(added.GetProperty("CanDelete").GetBoolean(), "The created arrow is eligible for the existing annotation manager");
        Check(after.GetProperty("Annotations").GetArrayLength() == originalAnnotationCount + 1 &&
              after.GetProperty("Text").GetString() == originalText,
            "Drawing preserves existing annotations and page text");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Drawing UI preserves the frozen source PDF");
        await ProveStrokePlacementAsync(owner, session, output, Check);
        await ProveTextBoxAsync(owner, session, output, Check);
        await ProvePlacementCancellationAsync(owner, session, Check);
        owner.Close();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} drawing UI checks. Evidence: {output}");
    }

    static async Task PlaceFromPreviewAsync(Window drawing, string output, Action<bool, string> check)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var deadline = DateTime.UtcNow.AddSeconds(25);
        timer.Tick += (_, _) =>
        {
            var placement = Application.Current.Windows.OfType<DrawingPlacementWindow>().FirstOrDefault(w => w.Owner == drawing);
            try
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Page placement did not render.");
                if (placement is null || Find<Image>(placement, "Unchanged PDF page for drawing placement").Source is null) return;
                timer.Stop();
                var canvas = Find<Canvas>(placement, "Drawing bounds on rendered PDF page");
                placement.Draw(new Rect(50, 50, 0, 0));
                check(true, "Arrow preview safely handles a zero-area drag before selection validation");
                check(!placement.SelectBounds(new Rect(-1, 10, 20, 20)), "Page placement refuses out-of-page bounds");
                check(!placement.SelectBounds(new Rect(10, 10, .1, 10)), "Page placement refuses negligible bounds");
                check(placement.SelectBounds(new Rect(100, canvas.Height - 560, 100, 60)), "Page placement resolves rendered top-left coordinates into PDF bounds");
                Capture(placement, Path.Combine(output, "drawing-page-placement.png"), 820, 760);
                Find<Button>(placement, "Use drawing bounds from page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                completion.TrySetResult();
            }
            catch (Exception error) { timer.Stop(); completion.TrySetException(error); placement?.Close(); }
        };
        timer.Start();
        try
        {
            Find<Button>(drawing, "Place drawing bounds on rendered page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await completion.Task;
        }
        finally { timer.Stop(); }
    }

    // Draws a freehand pen stroke and a reversed-direction arrow through the real placement and editor controls.
    static async Task ProveStrokePlacementAsync(Window owner, DocumentSession session, string output, Action<bool, string> check)
    {
        foreach (var (kind, points) in new (string, Point[])[]
        {
            ("Pen", Enumerable.Range(0, 40).Select(i => new Point(120 + i * 5, 300 + 30 * Math.Sin(i / 5d))).ToArray()),
            ("Arrow", [new Point(420, 200), new Point(260, 260)]),
        })
        {
            var before = (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength();
            var dialog = new DrawingWindow(session, 0) { Owner = owner, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            check(ShowAndInteract(dialog, async window =>
            {
                Find<ComboBox>(window, "Native PDF drawing annotation type").SelectedItem = kind;
                if (kind == "Pen")
                {
                    Find<Button>(window, "Add drawing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Find<TextBlock>(window, "Drawing validation and operation status").Text.Contains("draw the pen stroke first", StringComparison.Ordinal),
                        "Pen without a placed stroke was not refused.");
                    check(!session.IsDirty || (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == before,
                        "Pen without a stroke creates no annotation");
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                var deadline = DateTime.UtcNow.AddSeconds(25);
                timer.Tick += (_, _) =>
                {
                    var placement = Application.Current.Windows.OfType<DrawingPlacementWindow>().FirstOrDefault(w => w.Owner == window);
                    try
                    {
                        if (DateTime.UtcNow > deadline) throw new TimeoutException("Stroke placement did not render.");
                        if (placement is null || Find<Image>(placement, "Unchanged PDF page for drawing placement").Source is null) return;
                        timer.Stop();
                        check(!placement.SelectStroke([new Point(10, 10)]), $"{kind} placement refuses a single-point stroke");
                        check(!placement.SelectStroke([new Point(-5, 10), new Point(30, 30)]), $"{kind} placement refuses out-of-page points");
                        check(placement.SelectStroke(points), $"{kind} placement captures a directional stroke");
                        check(placement.HasStroke && placement.Stroke.Count == points.Length, $"{kind} stroke keeps its drawn points");
                        Capture(placement, Path.Combine(output, $"drawing-{kind.ToLowerInvariant()}-placement.png"), 820, 760);
                        Find<Button>(placement, "Use drawing bounds from page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        completion.TrySetResult();
                    }
                    catch (Exception error) { timer.Stop(); completion.TrySetException(error); placement?.Close(); }
                };
                timer.Start();
                Find<Button>(window, "Place drawing bounds on rendered page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try { await completion.Task; } finally { timer.Stop(); }
                var apply = Find<Button>(window, "Add drawing");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var status = Find<TextBlock>(window, "Drawing validation and operation status");
                await WaitUntilAsync(() => !window.IsVisible || (apply.IsEnabled && status.Text.Contains("refused", StringComparison.OrdinalIgnoreCase)),
                    $"The {kind} stroke did not commit.", 30);
                if (window.IsVisible) throw new Exception($"{kind} refused: " + status.Text);
            }) == true, $"{kind} stroke commits through the real placement and Add drawing controls");
            var annotations = (await session.ExtractAsync(0)).GetProperty("Annotations");
            check(annotations.GetArrayLength() == before + 1, $"{kind} stroke adds exactly one annotation");
            var ink = annotations.EnumerateArray().Last(a => a.GetProperty("Subtype").GetInt32() == 15);
            check(ink.GetProperty("Left").GetDouble() <= points.Min(p => p.X) && ink.GetProperty("Right").GetDouble() >= points.Max(p => p.X),
                $"{kind} annotation bounds enclose the drawn stroke");
        }
    }

    // Text box through the real dialog: empty text and overflowing text are refused, then a fitting box commits.
    static async Task ProveTextBoxAsync(Window owner, DocumentSession session, string output, Action<bool, string> check)
    {
        var before = await session.ExtractAsync(0);
        var count = before.GetProperty("Annotations").GetArrayLength();
        var dialog = new DrawingWindow(session, 0) { Owner = owner, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        check(ShowAndInteract(dialog, async window =>
        {
            Find<ComboBox>(window, "Native PDF drawing annotation type").SelectedItem = "Text box";
            var text = Find<TextBox>(window, "Text box text");
            check(text.IsVisible, "Text box choice reveals its text field");
            var status = Find<TextBlock>(window, "Drawing validation and operation status");
            var apply = Find<Button>(window, "Add drawing");
            Find<TextBox>(window, "Left coordinate in PDF points").Text = "100";
            Find<TextBox>(window, "Bottom coordinate in PDF points").Text = "100";
            Find<TextBox>(window, "Right coordinate in PDF points").Text = "140";
            Find<TextBox>(window, "Top coordinate in PDF points").Text = "140";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => status.Text.Contains("Enter the text", StringComparison.Ordinal), "Empty text box text was not refused.");
            text.Text = "This sentence cannot fit in forty points";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => status.Text.Contains("does not fit", StringComparison.Ordinal), "Overflowing text box text was not refused.");
            check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == count, "Refused text boxes add no annotation");
            Capture(window, Path.Combine(output, "drawing-textbox-refusal.png"), 560, 620);
            text.Text = "Approved";
            Find<TextBox>(window, "Right coordinate in PDF points").Text = "220";
            Find<TextBox>(window, "Top coordinate in PDF points").Text = "130";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !window.IsVisible || (apply.IsEnabled && status.Text.Contains("refused", StringComparison.OrdinalIgnoreCase)),
                "The text box did not commit.", 30);
            if (window.IsVisible) throw new Exception("Text box refused: " + status.Text);
        }) == true, "A fitting text box commits through the real Add drawing control");
        var after = await session.ExtractAsync(0);
        check(after.GetProperty("Annotations").GetArrayLength() == count + 1 && after.GetProperty("Text").GetString()!.Contains("Approved"),
            "The text box adds its border annotation and real page text");
    }

    static async Task ProvePlacementCancellationAsync(Window owner, DocumentSession session, Action<bool, string> check)
    {
        var revision = session.Revision;
        var drawing = new DrawingPlacementWindow(session, 0, "Ellipse") { Owner = owner, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        drawing.Show();
        await WaitUntilAsync(() => Find<Image>(drawing, "Unchanged PDF page for drawing placement").Source is not null,
            "Cancellation preview did not load.");
        check(drawing.SelectBounds(new Rect(50, 50, 80, 90)), "Ellipse page placement can prepare a reversible draft");
        drawing.Close();
        check(session.Revision == revision, "Closing a placement preview discards its draft without changing the session");

        var stale = new DrawingPlacementWindow(session, 0, "Line") { Owner = owner, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        stale.Show();
        await WaitUntilAsync(() => Find<Image>(stale, "Unchanged PDF page for drawing placement").Source is not null,
            "Stale placement preview did not load.");
        await session.RotatePageAsync(0, 1);
        check(!stale.SelectBounds(new Rect(50, 50, 80, 90)), "Page placement refuses a draft after the document revision changes");
        stale.Close();
        var rotatedRevision = session.Revision;
        var rotated = new DrawingPlacementWindow(session, 0, "Rectangle") { Owner = owner, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        rotated.Show();
        await WaitUntilAsync(() => Find<TextBlock>(rotated, "Drawing placement status").Text.Contains("unrotated pages only", StringComparison.Ordinal),
            "Rotated drawing placement did not report its supported-page boundary.");
        check(!Find<Button>(rotated, "Use drawing bounds from page").IsEnabled && session.Revision == rotatedRevision,
            "Rotated-page preview is refused without a document mutation");
        rotated.Close();
        await session.UndoAsync();
    }

    static bool? ShowAndInteract(Window dialog, Func<Window, Task> interaction)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try { await interaction(dialog); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        var result = dialog.ShowDialog();
        if (failure is not null) throw new Exception("Drawing dialog interaction failed.", failure);
        return result;
    }

    static async Task WaitUntilAsync(Func<bool> ready, string error, int seconds = 15)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(25);
        if (!ready()) throw new TimeoutException(error);
    }

    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    static T Find<T>(DependencyObject root, string automationName) where T : DependencyObject =>
        Children<T>(root).Single(item => AutomationProperties.GetName(item) == automationName);

    static void Capture(Window dialog, string path, int width, int height)
    {
        var root = (FrameworkElement)dialog.Content;
        if (root is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
