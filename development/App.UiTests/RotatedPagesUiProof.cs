using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Drives the real MainWindow's drawing, note, image and markup entry points on rotated and offset-box pages and checks
/// that the rendered pixels land under the dragged view rectangle, and that click-to-edit finds text at its displayed position.</summary>
static class RotatedPagesUiProof
{
    sealed record Variant(string Name, int Rotate, bool Crop);

    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-rotated-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var variants = new[] { new Variant("rot90", 90, false), new Variant("rot180", 180, false), new Variant("rot270", 270, false),
            new Variant("crop0", 0, true), new Variant("crop90", 90, true) };
        var dialogs = new TestDialogs();
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false
        };
        var editorOpened = false;
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        watchdog.Tick += (_, _) =>
        {
            foreach (var window in Application.Current.Windows.OfType<Window>().Where(w => w.Owner == owner && w is ObjectEditorWindow or ImageEditorWindow).ToArray())
            { editorOpened = true; window.Close(); }
        };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            owner.SetTheme(ShellTheme.Light);
            foreach (var variant in variants)
            {
                var path = Path.Combine(output, variant.Name + ".pdf");
                CreateSource(path, variant.Rotate, variant.Crop);
                var hash = RecoveryStore.Fingerprint(path);
                await owner.RunUiAsync(() => owner.OpenPathAsync(path), "Opening " + variant.Name);
                await WaitUntilAsync(() => !owner.IsBusy, "The document did not open.");
                var tab = owner.ActiveTab ?? throw new Exception("No active tab for " + variant.Name);
                var session = tab.Session;
                var page = tab.Pages[0];
                owner.SetPageTool(PageTool.Rectangle);
                var layer = (Canvas)owner.FindName("SelectionLayer0");
                await WaitUntilAsync(() => layer.ActualWidth > 0 && layer.ActualHeight > 0, "The page layer was not laid out.");
                double vw = layer.ActualWidth, vh = layer.ActualHeight;
                Check(page.Rotation == variant.Rotate / 90 && (variant.Crop ? page.OriginX == 100 && page.OriginY == 200 : page.OriginX == 0 && page.OriginY == 0),
                    $"{variant.Name}: the viewer knows the page rotation and visible-box origin");
                var renderWidth = (int)Math.Round(vw * 2); var renderHeight = (int)Math.Round(vh * 2);
                var k = renderWidth / vw; var ky = renderHeight / vh;
                async Task<byte[]> Render() => Convert.FromBase64String((await session.RenderAsync(0, renderWidth, renderHeight)).GetProperty("Bgra").GetString()!);
                var original = await Render();
                var textBox = Dark(original, renderWidth, renderHeight) ?? throw new Exception("Fixture text did not render.");
                var previous = original;
                var commits = 0;
                async Task<byte[]> Landed(string what, Point a, Point b, double tolerance, bool exact = true)
                {
                    var now = await Render();
                    var box = Diff(previous, now, renderWidth, renderHeight) ?? throw new Exception($"{variant.Name}: {what} did not change the page");
                    var (l, t, r, bt) = box;
                    double el = Math.Min(a.X, b.X) * k, et = Math.Min(a.Y, b.Y) * ky, er = Math.Max(a.X, b.X) * k, eb = Math.Max(a.Y, b.Y) * ky;
                    var ok = exact
                        ? Math.Abs(l - el) <= tolerance && Math.Abs(t - et) <= tolerance && Math.Abs(r - er) <= tolerance && Math.Abs(bt - eb) <= tolerance
                        : l >= el - tolerance && t >= et - tolerance && r <= er + tolerance && bt <= eb + tolerance;
                    Check(ok, $"{variant.Name}: {what} lands under the dragged view rectangle (pixels {l},{t},{r},{bt} vs {el:0},{et:0},{er:0},{eb:0})");
                    previous = now; commits++;
                    return now;
                }
                Point P(double fx, double fy) => new(vw * fx, vh * fy);

                // Highlight over the page text selected through the viewer's selection state
                owner.SetPageTool(PageTool.Highlight);
                var layout = await session.ExtractTextLayoutAsync(0);
                var characters = layout.GetProperty("Characters").EnumerateArray().Select(c => new PdfTextCharacter(c.GetProperty("Index").GetInt32(), c.GetProperty("Text").GetString() ?? "",
                    c.GetProperty("HasBounds").GetBoolean(), c.GetProperty("Left").GetDouble(), c.GetProperty("Bottom").GetDouble(), c.GetProperty("Right").GetDouble(), c.GetProperty("Top").GetDouble())).ToList();
                page.Characters = characters;
                var first = characters.First(c => c.HasBounds && !string.IsNullOrWhiteSpace(c.Text));
                var last = characters.Last(c => c.HasBounds && !string.IsNullOrWhiteSpace(c.Text));
                SetField(owner, "selectionPage", 0); SetField(owner, "selectionAnchor", first.Index); SetField(owner, "selectionEnd", last.Index);
                await owner.RunUiAsync(() => owner.ApplyMarkupToSelectionAsync(tab, 0, "Highlight"), "Highlight");
                var afterHighlight = await Render();
                Check(Diff(previous, afterHighlight, renderWidth, renderHeight) is { } hb &&
                      hb.Left <= textBox.Left + 2 && hb.Top <= textBox.Top + 2 && hb.Right >= textBox.Right - 2 && hb.Bottom >= textBox.Bottom - 2 &&
                      hb.Left >= textBox.Left - 12 && hb.Top >= textBox.Top - 12 && hb.Right <= textBox.Right + 12 && hb.Bottom <= textBox.Bottom + 12,
                    $"{variant.Name}: the highlight covers the displayed text and nothing else");
                previous = afterHighlight; commits++;

                // Click to edit the existing text at its displayed position
                owner.SetPageTool(PageTool.EditText);
                var click = new Point((textBox.Left + textBox.Right) / 2 / k, (textBox.Top + textBox.Bottom) / 2 / ky);
                editorOpened = false;
                watchdog.Start();
                await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Click to edit");
                await Task.Delay(120);
                watchdog.Stop();
                if (page.Rotation == 0)
                {
                    var inline = owner.InlineEditorBox ?? throw new Exception($"{variant.Name}: click-to-edit did not open the on-page editor on the offset page.");
                    Check(inline.Text.Contains("HELLO", StringComparison.Ordinal), $"{variant.Name}: click-to-edit on the offset page opens the clicked text");
                    var left = Canvas.GetLeft(inline); var top = Canvas.GetTop(inline);
                    Check(Math.Abs(left - textBox.Left / k) < 12 && Math.Abs(top - textBox.Top / ky) < 24, $"{variant.Name}: the on-page editor sits over the displayed text");
                    PressKey(inline, Key.Escape);
                }
                else Check(editorOpened, $"{variant.Name}: click-to-edit on the rotated page finds the text at its displayed position and opens the editor");
                owner.SetPageTool(PageTool.Select, editMode: false);

                // Rectangle and ellipse through the same entry point the mouse-up handler uses
                var r0 = P(.15, .08); var r1 = P(.50, .22);
                await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Rectangle, [r0, r1], vw, vh), "Rectangle");
                await Landed("rectangle", r0, r1, 6);
                var e0 = P(.55, .08); var e1 = P(.88, .22);
                await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Ellipse, [e0, e1], vw, vh), "Ellipse");
                await Landed("ellipse", e0, e1, 6);
                var pen = new[] { P(.15, .30), P(.25, .36), P(.35, .30), P(.45, .36), P(.55, .30) };
                await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Pen, pen, vw, vh), "Pen");
                await Landed("pen stroke", pen[0], new Point(pen[4].X, pen[1].Y), 6);
                var a0 = P(.15, .46); var a1 = P(.55, .42);
                await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Arrow, [a0, a1], vw, vh), "Arrow");
                await Landed("arrow", new Point(a0.X, a1.Y - 8), new Point(a1.X, a0.Y + 8), 16, exact: false);

                // Sticky note: the 20 pt icon sits at the clicked point
                var noteAt = P(.70, .45);
                owner.SetPageTool(PageTool.Note);
                await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.Note, new Rect(noteAt, noteAt), vw, vh), "Note");
                var box = owner.InlineEditorBox ?? throw new Exception($"{variant.Name}: the note editor did not open.");
                box.Text = "Rotated page note";
                PressKey(box, Key.Enter);
                await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "The note was not applied.");
                var scale = vw / page.Width;
                await Landed("sticky note", noteAt, new Point(noteAt.X + 20 * scale, noteAt.Y + 20 * scale), 6, exact: false);

                // Image placed through the signature placement path, centred on the click
                var stampPixels = new byte[80 * 40 * 4];
                for (var y = 0; y < 40; y++)
                    for (var x = 0; x < 80; x++)
                    {
                        var o = (y * 80 + x) * 4;
                        (byte B, byte G, byte R) c = x >= 40 ? ((byte)255, (byte)0, (byte)0) : y < 20 ? ((byte)0, (byte)160, (byte)0) : ((byte)0, (byte)0, (byte)255);
                        stampPixels[o] = c.B; stampPixels[o + 1] = c.G; stampPixels[o + 2] = c.R; stampPixels[o + 3] = 255;
                    }
                owner.SetPageTool(PageTool.Select, editMode: false);
                owner.BeginPlacement(new MainWindow.PendingStamp(new SignatureImage(stampPixels, 80, 40, SignatureKind.Signature), "signature", 80));
                var center = P(.45, .62);
                await owner.RunUiAsync(() => owner.PlacePendingAsync(tab, 0, center, vw, vh), "Image");
                var withImage = await Landed("placed image", new Point(center.X - 40 * scale, center.Y - 20 * scale), new Point(center.X + 40 * scale, center.Y + 20 * scale), 4);
                Check(Dominant(withImage, renderWidth, center.X * k - 40, center.Y * ky - 20) == 'G' &&
                      Dominant(withImage, renderWidth, center.X * k - 40, center.Y * ky + 20) == 'R' &&
                      Dominant(withImage, renderWidth, center.X * k + 40, center.Y * ky) == 'B',
                    $"{variant.Name}: the placed image reads upright for the viewer (green top-left, red bottom-left, blue right)");

                var annotations = (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength();
                Check(annotations == 6, $"{variant.Name}: six annotations were added through the UI entry points");
                for (var i = 0; i < commits; i++) await session.UndoAsync();
                Check(Diff(original, await Render(), renderWidth, renderHeight) is null, $"{variant.Name}: undoing every UI edit restores the original rendering");
                Check(RecoveryStore.Fingerprint(path) == hash, $"{variant.Name}: the source file is unchanged");
                await owner.RunUiAsync(async () => { await owner.CloseAllAsync(); }, "Closing");
            }
            Check(dialogs.Messages.Count == 0, "No error dialogs were raised: " + string.Join("; ", dialogs.Messages));
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} rotated/offset page UI checks. Evidence: {output}");
        }
        finally
        {
            watchdog.Stop();
            await owner.RunUiAsync(async () => { await owner.CloseAllAsync(); });
            typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
            owner.Close();
        }
    }

    static (int Left, int Top, int Right, int Bottom)? Diff(byte[] a, byte[] b, int width, int height)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, bt = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (Math.Abs(a[o] - b[o]) > 24 || Math.Abs(a[o + 1] - b[o + 1]) > 24 || Math.Abs(a[o + 2] - b[o + 2]) > 24)
                { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); bt = Math.Max(bt, y + 1); }
            }
        return r < 0 ? null : (l, t, r, bt);
    }

    static (int Left, int Top, int Right, int Bottom)? Dark(byte[] bgra, int width, int height)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (bgra[o] < 110 && bgra[o + 1] < 110 && bgra[o + 2] < 110) { l = Math.Min(l, x); r = Math.Max(r, x + 1); t = Math.Min(t, y); b = Math.Max(b, y + 1); }
            }
        return r < 0 ? null : (l, t, r, b);
    }

    static char Dominant(byte[] bgra, int width, double x, double y)
    {
        var o = ((int)y * width + (int)x) * 4;
        int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
        return r > 150 && g < 100 && b < 100 ? 'R' : g > 100 && r < 100 && b < 100 ? 'G' : b > 150 && r < 100 && g < 100 ? 'B' : '?';
    }

    static void CreateSource(string path, int rotate, bool crop)
    {
        const string stream = "BT /F1 36 Tf 200 450 Td (HELLO WORLD) Tj ET\n";
        var pageDictionary = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + (crop ? " /CropBox [100 200 500 600]" : "") +
            (rotate != 0 ? $" /Rotate {rotate}" : "") + " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", pageDictionary,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream" };
        var content = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(content.Length); content.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = content.Length; content.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) content.Append($"{offset:0000000000} 00000 n \n");
        content.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, content.ToString(), Encoding.ASCII);
    }

    static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name)).SetValue(target, value);

    static void PressKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element) ?? throw new Exception("The editor is not in a presentation source.");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
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
}
