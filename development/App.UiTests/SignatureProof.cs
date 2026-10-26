using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class SignatureProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-signature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var storeRoot = Path.Combine(output, "signatures");

        static (int Opaque, int Dark, bool CornersClear) Ink(SignatureImage s)
        {
            int opaque = 0, dark = 0;
            for (var i = 0; i < s.Bgra.Length; i += 4) { if (s.Bgra[i + 3] > 128) opaque++; if (s.Bgra[i + 3] > 128 && s.Bgra[i] < 90 && s.Bgra[i + 1] < 90 && s.Bgra[i + 2] < 120) dark++; }
            var corners = s.Bgra[3] == 0 && s.Bgra[(s.Width - 1) * 4 + 3] == 0 && s.Bgra[((s.Height - 1) * s.Width) * 4 + 3] == 0 && s.Bgra[(s.Height * s.Width - 1) * 4 + 3] == 0;
            return (opaque, dark, corners);
        }
        static StylusPointCollection Points(params (double X, double Y)[] p) => new(p.Select(q => new StylusPoint(q.X, q.Y)));

        // ---- Dialog on screen: header, tabs, draw, type, image, OK ----
        var store = new SignatureStore(storeRoot);
        var window = new SignatureCaptureWindow(store) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        SignatureImage? drawn = null, typedImage = null; string? headerText = null, explanation = null;
        var tabNames = new List<string>(); var fontNames = new List<string>();
        var result = Interact(window, async () =>
        {
            headerText = Find<TextBlock>(window, "Signature dialog heading").Text; explanation = Find<TextBlock>(window, "Signature dialog explanation").Text;
            foreach (TabItem t in window.Tabs.Items) tabNames.Add((string)t.Header);
            Check(window.Tabs.SelectedIndex == 0 && window.BuildCurrent() is null, "Empty Draw tab produces nothing");
            window.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.StatusText.Contains("Draw your signature") && window.Result is null, "OK on an empty drawing is refused with a plain message");
            window.InkSurface.Strokes.Add(new Stroke(Points((100, 150), (160, 70), (220, 160), (280, 80), (340, 150), (400, 90))));
            window.InkSurface.Strokes.Add(new Stroke(Points((90, 175), (420, 170))));
            drawn = window.BuildCurrent();
            window.Tabs.SelectedIndex = 1;
            window.TypedText.Text = "Alex Example";
            foreach (var item in window.FontPicker.Items) fontNames.Add(item.ToString()!);
            typedImage = window.BuildCurrent();
            Check(window.TypePreviewImage.Source is not null, "Typed signature shows a live preview");
            // OK on the Type tab returns, saves and closes
            window.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(10);
        });
        Check(headerText == "Visual signature (not a digital signature)", "Header says visual signature, not a digital signature");
        Check(explanation!.Contains("image placed on the page") && explanation.Contains("does not cryptographically sign"), "One plain sentence explains it does not cryptographically sign");
        Check(tabNames.SequenceEqual(["Draw", "Type", "Image"]), "Draw, Type and Image sources are tabs");
        Check(result == true && window.Result is not null && window.Result.Kind == SignatureKind.Signature, "OK returns a SignatureImage of the chosen kind");
        Check(window.Saved is not null && store.List().Count == 1, "Save for later (default on) stores the signature on OK");
        var d = Ink(drawn!);
        Check(drawn!.Width < 1920 && drawn.Height < 660 && drawn.Width > 400 && d.Opaque > 500 && d.Dark > 500 && d.CornersClear, "Drawn signature is trimmed, high-resolution, dark and transparent around the ink");
        Check(drawn.Bgra.Length == drawn.Width * drawn.Height * 4, "Drawn BGRA length matches dimensions");
        Check(fontNames.Count >= 2 && fontNames[^1] == "Segoe UI Italic" && fontNames.All(n => n == "Segoe UI Italic" || SignatureCaptureWindow.InstalledChoices().Any(c => c.Display == n)), "Font picker lists installed script fonts and ends with Segoe UI Italic: " + string.Join(", ", fontNames));
        var t = Ink(typedImage!);
        Check(t.Opaque > 500 && t.Dark > 300 && t.CornersClear && typedImage!.Height < 400, "Typed signature has ink, alpha and trimmed margins");

        // ---- Each font renders; initials kind and dark blue; thickness; cap ----
        var window2 = new SignatureCaptureWindow(null, SignatureKind.Initials) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        var png = Path.Combine(output, "scan.png"); WriteScan(png);
        var scanHash = SHA256.HashData(File.ReadAllBytes(png));
        SignatureImage? imgKeep = null, imgRaw = null, wide = null, thin = null, thick = null, blue = null; var perFont = new List<bool>(); string? badStatus = null; SignatureImage? afterBad = null; bool? okWithoutStore = null;
        var kindName = ""; bool saveDefault = false;
        Interact(window2, async () =>
        {
            kindName = window2.KindPicker.SelectedItem?.ToString() ?? ""; saveDefault = window2.SaveForLater;
            window2.Tabs.SelectedIndex = 1; window2.TypedText.Text = "AE";
            for (var i = 0; i < window2.FontPicker.Items.Count; i++) { window2.FontPicker.SelectedIndex = i; var s = window2.BuildCurrent(); perFont.Add(s is not null && Ink(s).Opaque > 100 && s.Kind == SignatureKind.Initials); }
            window2.ColorPicker.SelectedIndex = 1; blue = window2.BuildCurrent(); window2.ColorPicker.SelectedIndex = 0;
            window2.TypedText.Text = new string('W', 40); window2.FontPicker.SelectedIndex = 0; wide = window2.BuildCurrent();
            window2.Tabs.SelectedIndex = 0;
            window2.StrokeThickness.Value = 1; window2.InkSurface.Strokes.Add(new Stroke(Points((50, 100), (500, 100)), window2.InkSurface.DefaultDrawingAttributes)); thin = window2.BuildCurrent();
            window2.InkSurface.Strokes.Clear(); window2.StrokeThickness.Value = 8; window2.InkSurface.Strokes.Add(new Stroke(Points((50, 100), (500, 100)), window2.InkSurface.DefaultDrawingAttributes)); thick = window2.BuildCurrent();
            window2.InkSurface.Strokes.Clear();
            window2.InkSurface.Strokes.Add(new Stroke(Points((0, 5), (640, 215)), new DrawingAttributes { Width = 4, Height = 4 })); // spans the whole canvas
            window2.Tabs.SelectedIndex = 2;
            await window2.ImportAsync(Path.Combine(output, "does-not-exist.png")); badStatus = window2.StatusText; afterBad = window2.BuildCurrent();
            await window2.ImportAsync(png);
            window2.RemoveWhiteOption.IsChecked = false; imgKeep = window2.BuildCurrent();
            window2.RemoveWhiteOption.IsChecked = true; imgRaw = window2.BuildCurrent();
            Check(window2.ImagePreviewImage.Source is not null, "Imported image shows a preview");
            window2.Tabs.SelectedIndex = 0; wide = window2.BuildCurrent() is { } full ? full : wide; // full-canvas stroke fits the cap
            window2.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        okWithoutStore = window2.Result is not null && window2.Saved is null;
        Check(kindName == "Initials" && saveDefault, "Initial kind is honored and Save for later defaults on");
        Check(perFont.Count >= 2 && perFont.All(x => x), "Every listed font renders an initials image");
        var b = Ink(blue!); var bluePx = blue!.Bgra.Where((_, i) => i % 4 == 0).Max();
        Check(b.Opaque > 100 && bluePx > 80, "Dark blue ink color is applied");
        Check(thin is not null && thick is not null && thick.Height > thin.Height + 6, "Stroke thickness changes the rendered ink");
        Check(imgKeep!.Width == 400 && imgKeep.Height == 150 && !Ink(imgKeep).CornersClear, "Image without removal keeps its opaque white background");
        var r = Ink(imgRaw!);
        Check(r.CornersClear && imgRaw!.Width < 400 && imgRaw.Height < 150 && r.Dark > 200, "Remove white background makes near-white transparent, trims margins and keeps ink");
        Check(imgRaw!.Bgra.Where((_, i) => i % 4 == 3).Any(a => a is > 0 and < 255), "Near-white removal keeps soft anti-aliased edges");
        Check(badStatus!.Length > 0 && afterBad is null, "A missing or unreadable image is refused with a message");
        Check(scanHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(png))), "The imported source image is never modified");
        Check(okWithoutStore == true, "Without a store, OK returns the image and saves nothing");
        Check(wide!.Width <= 1600 && wide.Height <= 600 && wide.Width >= 1500, $"Output is capped at 1600x600 ({wide.Width}x{wide.Height})");

        // ---- Store ----
        var store2 = new SignatureStore(Path.Combine(output, "store2"));
        Check(store2.List().Count == 0 && !Directory.Exists(store2.Root), "Listing a missing store is empty and creates nothing");
        var sig = typedImage!; var ini = new SignatureImage(sig.Bgra, sig.Width, sig.Height, SignatureKind.Initials);
        var s1 = store2.Save(sig); var s2 = store2.Save(ini);
        Check(s1.Id != s2.Id && s1.Id.StartsWith("s-") && s2.Id.StartsWith("i-") && File.Exists(Path.Combine(store2.Root, s1.Id + ".png")), "Saves use create-only GUID PNG names");
        var list = store2.List();
        Check(list.Count == 2 && list[0].Id == s2.Id && list.Any(x => x.Id == s1.Id && x.Kind == SignatureKind.Signature && x.Width == sig.Width && x.Height == sig.Height), "List returns kind, size and creation order");
        var loaded = store2.Load(s1.Id);
        Check(loaded is not null && loaded.Width == sig.Width && loaded.Height == sig.Height && loaded.Kind == SignatureKind.Signature && loaded.Bgra.Zip(sig.Bgra).All(p => Math.Abs(p.First - p.Second) <= 3 || false),
            "Load round-trips the PNG with alpha");
        Check(store2.Load(s2.Id)!.Kind == SignatureKind.Initials && store2.Load("..\\..\\x") is null && store2.Load("s-" + new string('0', 32)) is null && !store2.Delete("..\\index"), "Kind survives, unknown or traversal ids are refused");
        Check(Directory.GetFiles(store2.Root).All(f => Path.GetExtension(f) is ".png" or ".json"), "Only images and the index live in the store");
        var indexText = File.ReadAllText(Path.Combine(store2.Root, "index.json"));
        Check(!indexText.Contains("Bgra", StringComparison.OrdinalIgnoreCase) && indexText.Contains(s1.Id), "Index holds only id, kind, created and size");
        File.WriteAllText(Path.Combine(store2.Root, "index.json"), "{ not json \u0000");
        var rebuilt = new SignatureStore(store2.Root).List();
        Check(rebuilt.Count == 2 && rebuilt.Any(x => x.Id == s2.Id && x.Kind == SignatureKind.Initials && x.Width == sig.Width), "A corrupt index is rebuilt from the PNG files");
        Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(store2.Root, "index.json"))).RootElement.GetArrayLength() == 2, "The rebuilt index is written back as valid JSON");
        File.WriteAllBytes(Path.Combine(store2.Root, "s-" + Guid.NewGuid().ToString("N") + ".png"), [1, 2, 3]);
        File.Delete(Path.Combine(store2.Root, "index.json"));
        Check(new SignatureStore(store2.Root).List().Count == 2, "A missing index and an unreadable PNG are tolerated");
        Check(store2.Delete(s1.Id) && !File.Exists(Path.Combine(store2.Root, s1.Id + ".png")) && store2.List().Count == 1 && store2.Load(s1.Id) is null, "Delete removes the image and index entry");
        Check(!Directory.GetFiles(store2.Root, "*.tmp").Any(), "No temporary index files are left behind");

        // ---- Stamps ----
        var date = StampImages.Date("10/22/2026", 48); var check = StampImages.Check(64); var cross = StampImages.Cross(32);
        Check(date.Height == 48 && date.Width > 60 && Ink(date).Dark > 150 && Ink(date).CornersClear, "Date stamp is transparent with ink at the requested height");
        Check(check.Height == 64 && check.Width == 64 && Ink(check).Dark > 100 && Ink(check).CornersClear, "Check mark is a drawn transparent path");
        Check(cross.Height == 32 && Ink(cross).Dark > 40 && Ink(cross).CornersClear, "Cross mark is a drawn transparent path");
        var pngBack = StampImages.PngToBgra(StampImages.BgraToPng(check.Bgra, check.Width, check.Height));
        Check(pngBack.Width == 64 && pngBack.Bgra[3] == 0, "PNG to BGRA conversion keeps alpha");
        try { StampImages.Date(" ", 48); Check(false, "Empty date text is refused"); } catch (ArgumentException) { Check(true, "Empty date text is refused"); }

        // ---- Placement on a PDF: alpha becomes an SMask and the page stays white around the ink ----
        var pdf = Path.Combine(output, "blank.pdf"); WriteBlankPdf(pdf);
        var pdfHash = SHA256.HashData(File.ReadAllBytes(pdf));
        using var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "recovery")), pdf);
        var before = await Page(session);
        Check(before.All(v => v == 255), "Synthetic page starts fully white");
        var placed = drawn!; double wPts = 240, hPts = wPts * placed.Height / placed.Width, left = 100, bottom = 400;
        await session.AddImageAsync(0, placed.Bgra, placed.Width, placed.Height, left, bottom, left + wPts, bottom + hPts);
        var after = await Page(session);
        var (dark, whiteInBox, boxPx, leaked) = Measure(after, 612, 792, left, bottom, wPts, hPts);
        Check(dark > 300, $"Placed signature ink renders on the page ({dark} dark pixels)");
        Check(boxPx > 0 && whiteInBox > 0.4 * boxPx && leaked == 0, $"Pixels outside the ink stay white inside and around the box ({whiteInBox}/{boxPx} white in box, {leaked} outside)");
        var saved = Path.Combine(output, "signed-visual.pdf"); await session.SaveAsync(saved);
        var smask = RunPython(python, output, saved);
        Check(smask.GetProperty("Images").GetInt32() == 1 && smask.GetProperty("SMask").GetBoolean(), "pypdf finds the image XObject with an /SMask");
        await session.UndoAsync();
        Check((await Page(session)).All(v => v == 255), "Undo removes the placed signature");
        await session.RedoAsync();
        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "recovery-2")), saved))
        {
            var (d2, w2, p2, l2) = Measure(await Page(reopened), 612, 792, left, bottom, wPts, hPts);
            Check(d2 > 300 && w2 > 0.4 * p2 && l2 == 0, "Saved and reopened document keeps the transparent signature");
        }
        Check(pdfHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pdf))), "The original PDF is unchanged");

        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} visual signature checks. Evidence: {output}");
    }

    static async Task<byte[]> PageBytes(DocumentSession s) => Convert.FromBase64String((await s.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString()!);
    static Task<byte[]> Page(DocumentSession s) => PageBytes(s);

    // Counts dark pixels in the image box, white pixels in the box, and non-white pixels outside it.
    static (int Dark, int WhiteInBox, int BoxPx, int Leaked) Measure(byte[] bgra, int w, int h, double left, double bottom, double bw, double bh)
    {
        int x0 = (int)Math.Floor(left), x1 = (int)Math.Ceiling(left + bw), y0 = (int)Math.Floor(h - bottom - bh), y1 = (int)Math.Ceiling(h - bottom);
        int dark = 0, white = 0, box = 0, leaked = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4; bool isWhite = bgra[i] > 250 && bgra[i + 1] > 250 && bgra[i + 2] > 250;
                if (x >= x0 && x < x1 && y >= y0 && y < y1) { box++; if (isWhite) white++; if (bgra[i] < 90 && bgra[i + 1] < 90 && bgra[i + 2] < 120) dark++; }
                else if (!isWhite) leaked++;
            }
        return (dark, white, box, leaked);
    }

    static void WriteBlankPdf(string path)
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>", "<< /Length 0 >>\nstream\n\nendstream" };
        var sb = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = sb.Length; sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    // 400x150 opaque white image with an anti-aliased dark diagonal and blob
    static void WriteScan(string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 150));
            dc.DrawLine(new Pen(Brushes.Black, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(60, 110), new Point(340, 40));
            dc.DrawEllipse(Brushes.Black, null, new Point(120, 60), 18, 14);
        }
        var bitmap = new RenderTargetBitmap(400, 150, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); encoder.Save(stream);
    }

    static JsonElement RunPython(string python, string folder, string pdf)
    {
        var script = Path.Combine(folder, "check_smask.py");
        File.WriteAllText(script, """
            import sys, json
            from pypdf import PdfReader
            r = PdfReader(sys.argv[1]); n = 0; sm = False
            for p in r.pages:
                xo = p["/Resources"].get("/XObject", {}) if "/Resources" in p else {}
                for k in xo:
                    o = xo[k].get_object()
                    if o.get("/Subtype") == "/Image":
                        n += 1; sm = sm or ("/SMask" in o)
            print(json.dumps({"Images": n, "SMask": sm}))
            """);
        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-I", "-B", script, pdf }) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!; var text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("pypdf check failed: " + process.StandardError.ReadToEnd());
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    static bool? Interact(Window dialog, Func<Task> action)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) { try { dialog.Close(); } catch (InvalidOperationException) { } } }
        };
        var result = dialog.ShowDialog();
        if (failure is not null) throw new Exception("Signature dialog interaction failed: " + failure.Message, failure);
        return result;
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T result) yield return result;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    static T Find<T>(DependencyObject root, string name) where T : DependencyObject => Children<T>(root).Single(c => AutomationProperties.GetName(c) == name);
}
