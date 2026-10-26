using System.Diagnostics;
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

/// <summary>Drives each Quick tools dialog against a session on a synthetic PDF and checks the effect with the session and independent pypdf.</summary>
static class QuickToolsUiProof
{
    const string MakeScript = """
        import sys, os, pymupdf
        out, locked = sys.argv[1], sys.argv[2]
        doc = pymupdf.open()
        for i in range(3):
            p = doc.new_page(width=612, height=792)
            p.insert_text((72, 100), f"Quick tools page {i+1}", fontsize=18)
            p.insert_text((72, 140), "Body text for checks", fontsize=12)
        pix = pymupdf.Pixmap(pymupdf.csRGB, 800, 800, bytes(os.urandom(800 * 800 * 3)), False)
        doc[0].insert_image(pymupdf.Rect(72, 200, 272, 400), pixmap=pix)
        doc[0].insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(72, 80, 200, 110), "uri": "https://example.com/"})
        w = pymupdf.Widget(); w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT; w.field_name = "f1"; w.rect = pymupdf.Rect(72, 450, 272, 480); w.field_value = "hello"
        doc[1].add_widget(w)
        doc[1].add_freetext_annot(pymupdf.Rect(72, 520, 300, 560), "note")
        doc.set_toc([[1, "Chapter A", 1], [2, "Section A1", 2], [1, "Chapter B", 3]])
        doc.set_metadata({"title": "Proof title", "author": "Proof Author"})
        doc.save(out, garbage=3, deflate=True)
        doc.save(locked, encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw="LockedPw-1", owner_pw="LockedOwner-2")
        """;
    const string InspectScript = """
        import sys, json, pypdf
        r = pypdf.PdfReader(sys.argv[1]); out = {"encrypted": r.is_encrypted}
        if r.is_encrypted:
            out["decrypt"] = int(r.decrypt(sys.argv[2] if len(sys.argv) > 2 else ""))
            if not out["decrypt"]:
                print(json.dumps(out)); sys.exit(0)
        out["pages"] = len(r.pages)
        out["text"] = [(p.extract_text() or "") for p in r.pages]
        out["crop"] = [float(v) for v in r.pages[0].cropbox]
        f = r.get_fields(); out["fields"] = len(f) if f else 0
        out["annots"] = [len(p.get("/Annots") or []) for p in r.pages]
        out["title"] = str((r.metadata or {}).get("/Title"))
        print(json.dumps(out))
        """;

    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-quick-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var engine = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAILED: " + message); checks.Add(message); }
        var makePath = Path.Combine(output, "make.py"); var inspectPath = Path.Combine(output, "inspect.py");
        File.WriteAllText(makePath, MakeScript); File.WriteAllText(inspectPath, InspectScript);
        var source = Path.Combine(output, "source.pdf"); var locked = Path.Combine(output, "locked.pdf");
        Py(python, makePath, source, locked);
        var sourceHash = RecoveryStore.Fingerprint(source);
        var sourceInfo = Py(python, inspectPath, source);
        Check(sourceInfo.GetProperty("pages").GetInt32() == 3 && sourceInfo.GetProperty("fields").GetInt32() == 1, "Synthetic source has 3 pages and a form field");
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "shell")), new TestDialogs())
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        owner.Show(); await WaitAsync(() => !owner.IsBusy);
        var sessions = new List<DocumentSession>();
        async Task<DocumentSession> Fresh(string name, string? path = null, string? password = null)
        {
            var copy = Path.Combine(output, name + ".pdf"); File.Copy(path ?? source, copy);
            var s = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "rec-" + name)), copy, password); sessions.Add(s); return s;
        }
        async Task<T> Dialog<T>(T dialog, Func<T, Task> script) where T : QuickToolWindow
        {
            dialog.Owner = owner;
            Exception? failure = null;
            dialog.Loaded += async (_, _) =>
            {
                try { await WaitAsync(() => !dialog.Busy); await script(dialog); }
                catch (Exception e) { failure = e; }
                finally { dialog.Close(); }
            };
            dialog.ShowDialog();
            if (failure is not null) throw new Exception("Quick tool dialog interaction failed: " + failure.Message, failure);
            await Task.CompletedTask; return dialog;
        }
        async Task Click(QuickToolWindow dialog, string name) { Find<Button>(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitAsync(() => !dialog.Busy, 120); }
        string Status(QuickToolWindow dialog) => dialog.StatusText;
        TextBox Box(QuickToolWindow d, string name) => Find<TextBox>(d, name);
        ComboBox Combo(QuickToolWindow d, string name) => Find<ComboBox>(d, name);

        owner.SetTheme(ShellTheme.Light);
        // ---- Compress
        var s1 = await Fresh("compress");
        var compressed = Path.Combine(output, "compressed.pdf");
        var compress = await Dialog(new CompressWindow(s1, engine, 0) { PickPath = _ => compressed }, async d =>
        {
            Combo(d, "Image downsampling resolution").SelectedIndex = 2;
            await Click(d, "Create compressed copy…");
        });
        Check(compress.ResultPath == compressed && File.Exists(compressed) && Status(compress).Contains("smaller") && !s1.IsDirty, "Compress writes a smaller new file and reports before/after size: " + Status(compress));
        Check(Py(python, inspectPath, compressed).GetProperty("pages").GetInt32() == 3 && new FileInfo(compressed).Length < new FileInfo(source).Length, "Compressed output reads in pypdf with 3 pages and is smaller");
        var existing = Path.Combine(output, "existing.pdf"); File.Copy(source, existing); var existingHash = RecoveryStore.Fingerprint(existing);
        var refuseCompress = await Dialog(new CompressWindow(s1, engine, 0) { PickPath = _ => existing }, async d => await Click(d, "Create compressed copy…"));
        Check(Status(refuseCompress).Contains("new filename") && RecoveryStore.Fingerprint(existing) == existingHash, "Compress refuses to replace an existing file");

        // ---- Protect
        var s2 = await Fresh("protect"); var protectedPath = Path.Combine(output, "protected.pdf");
        PasswordBox? pwBox = null;
        var protect = await Dialog(new ProtectWindow(s2, engine, 0) { PickPath = _ => protectedPath }, async d =>
        {
            Find<PasswordBox>(d, "Open password").Password = "OpenPw-1"; Find<PasswordBox>(d, "Repeat open password").Password = "OpenPw-X";
            Find<PasswordBox>(d, "Owner password").Password = "OwnerPw-2"; Find<PasswordBox>(d, "Repeat owner password").Password = "OwnerPw-2";
            await Click(d, "Create protected copy…");
            Check(Status(d).Contains("do not match") && !File.Exists(protectedPath), "Protect refuses mismatched open passwords and writes nothing");
            Find<PasswordBox>(d, "Repeat open password").Password = "OpenPw-1";
            pwBox = Find<PasswordBox>(d, "Open password");
            await Click(d, "Create protected copy…");
        });
        Check(File.Exists(protectedPath) && pwBox?.Password.Length == 0, "Protect writes the copy and clears the password boxes");
        var good = Py(python, inspectPath, protectedPath, "OpenPw-1"); var bad = Py(python, inspectPath, protectedPath, "wrong");
        Check(good.GetProperty("encrypted").GetBoolean() && good.GetProperty("pages").GetInt32() == 3 && bad.GetProperty("decrypt").GetInt32() == 0, "Protected copy needs the open password (pypdf)");

        // ---- Unprotect
        var s3 = await Fresh("plain-unprotect");
        var unprotectRefused = await Dialog(new UnprotectWindow(s3, engine, 0) { PickPath = _ => Path.Combine(output, "never.pdf") }, async d => { await Click(d, "Create unprotected copy…"); });
        Check(Status(unprotectRefused).Contains("password") && unprotectRefused.ResultPath is null, "Unprotect shows the engine refusal for an unprotected document: " + Status(unprotectRefused));
        var s4 = await Fresh("unlock", locked, "LockedPw-1"); var unlocked = Path.Combine(output, "unlocked.pdf");
        var unprotect = await Dialog(new UnprotectWindow(s4, engine, 0) { PickPath = _ => unlocked }, async d => await Click(d, "Create unprotected copy…"));
        Check(File.Exists(unlocked), "Unprotect produced a file: " + Status(unprotect));
        var unlockedInfo = Py(python, inspectPath, unlocked);
        Check(!unlockedInfo.GetProperty("encrypted").GetBoolean() && unlockedInfo.GetProperty("pages").GetInt32() == 3, "Unprotect writes an unencrypted copy of a password-opened document");

        // ---- Watermark
        var s5 = await Fresh("watermark");
        var wm = new WatermarkWindow(s5, engine, 0);
        var wmSnapshot = false;
        await Dialog(wm, async d =>
        {
            Box(d, "Watermark text").Text = "";
            await Click(d, "Add watermark");
            Check(Status(d).Contains("Enter the watermark text") && !s5.IsDirty, "Watermark refuses empty text");
            Box(d, "Watermark text").Text = "DRAFTMARK"; Combo(d, "Watermark rotation").SelectedIndex = 0; Find<Slider>(d, "Watermark opacity percent").Value = 50;
            Find<RadioButton>(d, "Watermark color: Blue").IsChecked = true;
            Capture(d, Path.Combine(output, "watermark-light.png"), 620, 860); wmSnapshot = true;
            await Click(d, "Add watermark");
        });
        Check(wmSnapshot && wm.Changed && s5.CanUndo && (await s5.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("DRAFTMARK"), "Watermark is applied as one change visible to PDFium");
        await s5.UndoAsync();
        Check(!(await s5.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("DRAFTMARK"), "Undo removes the watermark");

        // ---- Header/footer
        owner.SetTheme(ShellTheme.Dark);
        var s6 = await Fresh("header");
        var hf = new HeaderFooterWindow(s6, engine, 0);
        await Dialog(hf, async d =>
        {
            Box(d, "Header or footer text").Text = "Hdr {n}/{total} {filename}";
            var preview = Find<TextBlock>(d, "Sample text for page 1");
            Check(preview.Text.Contains("Hdr 1/3 header.pdf"), "Header preview shows page 1 sample: " + preview.Text);
            Box(d, "Header or footer text").Text = "中 {n}";
            await Click(d, "Add header or footer");
            Check(Status(d).Contains("Western European") && !s6.IsDirty, "Header refuses characters outside Latin-1");
            Box(d, "Header or footer text").Text = "Hdr {n}/{total} {filename}"; Combo(d, "Header or footer position").SelectedIndex = 0;
            Combo(d, "Pages to change").SelectedIndex = 2; Box(d, "Page range such as 1,3-5").Text = "2-3";
            Capture(d, Path.Combine(output, "header-dark.png"), 640, 900);
            await Click(d, "Add header or footer");
        });
        var h2 = (await s6.ExtractAsync(1)).GetProperty("Text").GetString()!; var h1 = (await s6.ExtractAsync(0)).GetProperty("Text").GetString()!;
        Check(hf.Changed && h2.Contains("Hdr 2/3 header.pdf") && !h1.Contains("Hdr"), "Header applies only to pages 2-3 with placeholders expanded");

        // ---- Bates
        owner.SetTheme(ShellTheme.Light);
        BatesWindow.NextNumber = null;
        var s7 = await Fresh("bates");
        var bates = new BatesWindow(s7, engine, 0);
        await Dialog(bates, async d =>
        {
            Box(d, "Bates prefix").Text = "ABC-"; Box(d, "Bates start number").Text = "9999"; Box(d, "Bates digits").Text = "4";
            await Click(d, "Apply Bates numbers");
            Check(Status(d).Contains("digits") && !s7.IsDirty, "Bates refuses numbers that need more digits");
            Box(d, "Bates start number").Text = "7";
            await Click(d, "Apply Bates numbers");
        });
        Check(bates.Changed && Status(bates).Contains("ABC-0007 to ABC-0009") && BatesWindow.NextNumber == 10 && (await s7.ExtractAsync(2)).GetProperty("Text").GetString()!.Contains("ABC-0009"), "Bates labels are applied and the next number is remembered: " + Status(bates));
        var s7b = await Fresh("bates-next");
        await Dialog(new BatesWindow(s7b, engine, 0), d => { Check(Box(d, "Bates start number").Text == "10" && Box(d, "Bates prefix").Text == "ABC-", "A second Bates dialog continues from the remembered number"); return Task.CompletedTask; });

        // ---- Crop
        var s8 = await Fresh("crop");
        await Dialog(new CropWindow(s8, engine, 0), async d =>
        {
            await Click(d, "Crop pages");
            Check(Status(d).Contains("at least one margin"), "Crop requires a margin");
            Box(d, "Crop left margin points").Text = "400"; Box(d, "Crop right margin points").Text = "400";
            await Click(d, "Crop pages");
            Check(Status(d).Contains("less than 36") && !s8.IsDirty, "Crop refuses margins that leave no page");
            Box(d, "Crop left margin points").Text = "10"; Box(d, "Crop top margin points").Text = "20"; Box(d, "Crop right margin points").Text = "30"; Box(d, "Crop bottom margin points").Text = "40";
            await Click(d, "Crop pages");
            Check(Status(d).Contains("still in the file"), "Crop states that content is hidden, not deleted: " + Status(d));
        });
        var croppedPath = Path.Combine(output, "cropped-saved.pdf"); await s8.SaveAsync(croppedPath);
        var crop = Py(python, inspectPath, croppedPath).GetProperty("crop").EnumerateArray().Select(v => Math.Round(v.GetDouble(), 1)).ToArray();
        Check(crop.SequenceEqual(new[] { 10.0, 40.0, 582.0, 772.0 }), "Crop margins map to the expected absolute CropBox (pypdf): " + string.Join(",", crop));

        // ---- Flatten
        var s9 = await Fresh("flatten"); var flat = Path.Combine(output, "flat.pdf");
        var flatten = await Dialog(new FlattenWindow(s9, engine, 1) { PickPath = _ => flat }, async d =>
        {
            Find<CheckBox>(d, "Flatten annotations").IsChecked = false; Find<CheckBox>(d, "Flatten form fields").IsChecked = false;
            await Click(d, "Create flattened copy…");
            Check(Status(d).Contains("Choose") && !File.Exists(flat), "Flatten refuses when nothing is chosen");
            Find<CheckBox>(d, "Flatten annotations").IsChecked = true; Find<CheckBox>(d, "Flatten form fields").IsChecked = true;
            await Click(d, "Create flattened copy…");
        });
        var flatInfo = Py(python, inspectPath, flat);
        Check(flatten.ResultPath == flat && flatInfo.GetProperty("fields").GetInt32() == 0 && flatInfo.GetProperty("annots")[1].GetInt32() == 0 && sourceInfo.GetProperty("annots")[1].GetInt32() > 0, "Flatten removes form fields and annotations in the new copy (pypdf)");

        // ---- Properties
        owner.SetTheme(ShellTheme.Light);
        var s10 = await Fresh("props");
        var props = new PropertiesWindow(s10, engine, 0);
        await Dialog(props, async d =>
        {
            Check(Box(d, "Document title").Text == "Proof title" && Box(d, "Document author").Text == "Proof Author", "Properties loads the current title and author");
            await Click(d, "Apply properties");
            Check(Status(d).Contains("Nothing to change"), "Properties refuses an empty change");
            Box(d, "Document title").Text = "Changed title"; Box(d, "Document keywords").Text = "alpha, beta";
            Capture(d, Path.Combine(output, "properties-light.png"), 640, 900);
            await Click(d, "Apply properties");
        });
        var after = await s10.GetPropertiesAsync(engine);
        Check(props.Changed && after.Title == "Changed title" && after.Keywords == "alpha, beta" && after.Author == "Proof Author" && s10.CanUndo, "Properties apply as one undoable change that keeps untouched fields");
        await s10.UndoAsync();
        Check((await s10.GetPropertiesAsync(engine)).Title == "Proof title", "Undo restores the previous properties");

        // ---- Bookmarks
        var s11 = await Fresh("bookmarks");
        var bookmarks = new BookmarksWindow(s11, engine, 1);
        await Dialog(bookmarks, async d =>
        {
            var list = Find<ListBox>(d, "Bookmarks in outline order");
            Check(list.Items.Count == 3 && !Find<Button>(d, "Apply bookmarks").IsEnabled, "Bookmarks loads three entries and Apply waits for an edit");
            Find<Button>(d, "Delete selected bookmark").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Status(d).Contains("Select a bookmark"), "Bookmarks refuses delete without a selection");
            Find<Button>(d, "Add bookmark at current page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(list.Items.Count == 4 && list.SelectedIndex == 3, "Add appends a bookmark at the current page");
            Box(d, "Bookmark title").Text = "Chapter C";
            Find<Button>(d, "Update selected bookmark from fields").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Click(d, "Apply bookmarks");
        });
        var outline = await s11.GetBookmarksListAsync(engine);
        Check(bookmarks.Changed && outline.Count == 4 && outline.Bookmarks[3] is { Title: "Chapter C", Page: 1, Level: 1 } && outline.Bookmarks[1].Level == 2, "Bookmark edits are written back with levels and pages");

        // ---- Links
        var s12 = await Fresh("links");
        var links = new LinksWindow(s12, engine, 0);
        await Dialog(links, async d =>
        {
            var list = Find<ListBox>(d, "Links on this page");
            Check(list.Items.Count == 1, "Links lists the link on the current page");
            Box(d, "Link target").Text = "ftp://example.com/";
            await Click(d, "Add link");
            Check(Status(d).Contains("Only http") && (await s12.ListLinksAsync(engine, 0)).Count == 1, "Links refuses unsupported schemes");
            Box(d, "Link target").Text = "https://example.org/";
            await Click(d, "Add link");
            var linkCount = (await s12.ListLinksAsync(engine, 0)).Count; Check(list.Items.Count == 2 && linkCount == 2, $"Links adds a web link (list {list.Items.Count}, engine {linkCount}): " + Status(d));
            list.SelectedIndex = list.Items.Count - 1;
            await Click(d, "Remove selected link");
        });
        Check(links.Changed && (await s12.ListLinksAsync(engine, 0)).Count == 1, "Links removes the selected link");

        // ---- Extract images
        var s13 = await Fresh("extract"); var imageFolder = Path.Combine(output, "extracted");
        var extract = await Dialog(new ExtractImagesWindow(s13, engine, 0) { PickPath = _ => imageFolder }, async d => await Click(d, "Extract images…"));
        Check(extract.ResultPath == imageFolder && Directory.GetFiles(imageFolder).Length >= 1 && !s13.IsDirty, "Extract images writes the picture into a new folder: " + Status(extract));
        var extractRefused = await Dialog(new ExtractImagesWindow(s13, engine, 0) { PickPath = _ => output }, async d => await Click(d, "Extract images…"));
        Check(Status(extractRefused).Contains("new folder"), "Extract images refuses an existing folder");

        // ---- Export images
        var s14 = await Fresh("export"); var pngFolder = Path.Combine(output, "png"); var jpgFolder = Path.Combine(output, "jpg");
        var png = await Dialog(new ExportImagesWindow(s14, engine, 0) { PickPath = _ => pngFolder }, async d =>
        {
            Combo(d, "Image resolution").SelectedIndex = 0;
            Combo(d, "Pages to change").SelectedIndex = 2; Box(d, "Page range such as 1,3-5").Text = "9";
            await Click(d, "Export images…");
            Check(Status(d).Contains("outside this PDF") && !Directory.Exists(pngFolder), "Export refuses a page range outside the document");
            Combo(d, "Pages to change").SelectedIndex = 0;
            await Click(d, "Export images…");
        });
        var pngs = Directory.GetFiles(pngFolder, "*.png");
        var frame = BitmapDecoder.Create(new Uri(pngs[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(pngs.Length == 3 && frame.PixelWidth == 612 && frame.PixelHeight == 792, "Export writes one 72 dpi PNG per page at the page size");
        var jpg = await Dialog(new ExportImagesWindow(s14, engine, 0) { PickPath = _ => jpgFolder }, async d =>
        {
            Combo(d, "Image format").SelectedIndex = 1; Combo(d, "Image resolution").SelectedIndex = 2; Combo(d, "Pages to change").SelectedIndex = 1;
            await Click(d, "Export images…");
        });
        var jpgs = Directory.GetFiles(jpgFolder, "*.jpg"); var jframe = BitmapDecoder.Create(new Uri(jpgs[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(jpgs.Length == 1 && jframe.PixelWidth * (long)jframe.PixelHeight <= 8_000_000 && Status(jpg).Contains("below 300 dpi"), "Export reduces an oversized 300 dpi JPEG and says so: " + Status(jpg));
        var cancelFolder = Path.Combine(output, "cancelled");
        var cancelled = await Dialog(new ExportImagesWindow(s14, engine, 0) { PickPath = _ => null }, async d => await Click(d, "Export images…"));
        Check(Status(cancelled).Contains("Cancelled") && !Directory.Exists(cancelFolder), "Cancelling the folder picker writes nothing");

        Check(RecoveryStore.Fingerprint(source) == sourceHash, "The synthetic source file is unchanged");
        foreach (var s in sessions) s.Dispose();
        owner.Close();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} quick tools UI checks. Evidence: {output}");
    }

    static JsonElement Py(string python, params string[] args)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new Exception("Python helper did not start.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Python helper failed: " + stderr);
        return stdout.Trim().Length == 0 ? default : JsonDocument.Parse(stdout.Trim().Split('\n').Last()).RootElement.Clone();
    }
    static async Task WaitAsync(Func<bool> ready, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(25);
        if (!ready()) throw new TimeoutException("Quick tool dialog did not complete in its expected time.");
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var item in Children<T>(child)) yield return item;
        }
    }
    static T Find<T>(DependencyObject root, string name) where T : DependencyObject => Children<T>(root).Single(c => AutomationProperties.GetName(c) == name);
    static void Capture(Window window, string path, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        if (root is Panel surface) surface.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
