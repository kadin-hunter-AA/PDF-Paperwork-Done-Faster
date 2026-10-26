using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>The Fields panel, on-page signature/stamp placement and the undo-storage command in the real shell.</summary>
static class FillSignUiProof
{
    const string FormScript = """
import sys, pymupdf
T = pymupdf
doc = pymupdf.open()
page = doc.new_page(width=612, height=792)
def add(kind, name, rect, **kw):
    w = pymupdf.Widget(); w.field_type = kind; w.field_name = name; w.rect = pymupdf.Rect(*rect)
    for k, v in kw.items(): setattr(w, k, v)
    return page.add_widget(w)
add(T.PDF_WIDGET_TYPE_TEXT, "name", (60, 60, 300, 84), text_fontsize=12)
add(T.PDF_WIDGET_TYPE_TEXT, "notes", (60, 100, 300, 170), field_flags=T.PDF_TX_FIELD_IS_MULTILINE, text_fontsize=11)
add(T.PDF_WIDGET_TYPE_CHECKBOX, "agree", (340, 60, 360, 80))
add(T.PDF_WIDGET_TYPE_COMBOBOX, "plan", (60, 190, 300, 212), choice_values=["Basic", "Plus", "Pro"], text_fontsize=12)
add(T.PDF_WIDGET_TYPE_LISTBOX, "tags", (340, 190, 500, 250), choice_values=["red", "green", "blue"], field_flags=T.PDF_CH_FIELD_IS_MULTI_SELECT)
add(T.PDF_WIDGET_TYPE_TEXT, "locked", (60, 270, 300, 292), field_value="fixed", field_flags=T.PDF_FIELD_IS_READ_ONLY, text_fontsize=12)
page.insert_text((60, 400), "Sign here:", fontsize=12)
doc.save(sys.argv[1])
""";

    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-fill-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var script = Path.Combine(output, "make_form.py");
        File.WriteAllText(script, FormScript);
        var form = Path.Combine(output, "form.pdf");
        Run(python, script, form);
        var formHash = RecoveryStore.Fingerprint(form);

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var dialogs = new TestDialogs();
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not load.");
            owner.SetTheme(ShellTheme.Light);
            await owner.RunUiAsync(() => owner.OpenPathAsync(form), "Opening form…");
            await WaitUntilAsync(() => !owner.IsBusy, "The form did not open.");
            var tab = owner.ActiveTab!;
            var session = tab.Session;

            // ---- Fields panel ----
            var fieldsTab = (TabItem)owner.FindName("FieldsTab");
            fieldsTab.IsSelected = true;
            await WaitUntilAsync(() => owner.FieldEditorNames.Count > 0 && !owner.IsBusy, "The Fields panel did not load.", 90);
            Check(owner.FieldEditorNames.Order().SequenceEqual(new[] { "agree", "locked", "name", "notes", "plan", "tags" }), "The Fields panel lists every field in the document");
            var status = ((TextBlock)owner.FindName("FieldsStatus")).Text;
            Check(status.StartsWith("6 fields"), "The panel reports the field count and that scripts are not run");
            var host = (Panel)owner.FindName("FieldsHost");
            Check(host.Children.OfType<TextBlock>().Any(t => t.Text == "Page 1"), "Fields are grouped by page");
            var lockedControl = host.Children.OfType<StackPanel>().First(p => (string)p.Tag == "locked").Children.OfType<TextBox>().Single();
            Check(!lockedControl.IsEnabled, "Read-only fields are shown but cannot be edited");
            Capture(owner, Path.Combine(output, "fields-panel.png"));

            // A click on a widget on the page focuses its entry.
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            await WaitUntilAsync(() => layer.ActualWidth > 0, "The page was not laid out.");
            double vw = layer.ActualWidth, vh = layer.ActualHeight;
            Point View(double x, double y) => new(x * vw / 612, (792 - y) * vh / 792);
            // pymupdf rects are top-left based: "plan" spans y 190–212 from the top, so PDF y is 792-201.
            Check(owner.FocusFieldForTest(tab, 0, View(150, 792 - 201), vw, vh) && fieldsTab.IsSelected, "Clicking a field on the page selects it in the Fields panel");
            Check(!owner.FocusFieldForTest(tab, 0, View(560, 100), vw, vh), "Clicking empty page space does not jump to a field");

            owner.SetFieldValueForTest("name", "Placeholder Person");
            owner.SetFieldValueForTest("notes", "Line one\nLine two");
            owner.SetFieldValueForTest("agree", true);
            owner.SetFieldValueForTest("plan", "Pro");
            owner.SetFieldValueForTest("tags", new[] { "red", "blue" });
            var before = session.Revision;
            await owner.RunUiAsync(() => owner.ApplyFieldsAsync(), "Filling…");
            await WaitUntilAsync(() => !owner.IsBusy && owner.FieldEditorNames.Count == 6, "Fill did not finish.", 90);
            Check(session.Revision == before + 1 && session.CanUndo, "Apply fills all changed fields as one undoable change");
            var list = await session.ListAllFormFieldsAsync(new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py")));
            string Value(string name) => list.Fields.First(f => f.FullName == name).Value;
            Check(Value("name") == "Placeholder Person" && Value("agree") == "true" && Value("plan") == "Pro" &&
                list.Fields.First(f => f.FullName == "tags").Selected.Order().SequenceEqual(new[] { "blue", "red" }), "Text, checkbox, combo and multi-select list values are written");
            Check(Value("notes").Replace("\r", "") == "Line one\nLine two", "Multiline text keeps its line breaks");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            list = await session.ListAllFormFieldsAsync(new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py")));
            Check(Value("name") == "" && Value("agree") == "false", "Undo restores the empty form");
            await owner.RunUiAsync(() => session.RedoAsync(), "Redo…");

            // ---- Visual signature and stamps placed by clicking ----
            var signature = StampImages.Check(120) with { Kind = SignatureKind.Signature };
            owner.BeginPlacement(new MainWindow.PendingStamp(signature, "signature", 120));
            Check(owner.PendingPlacement is not null, "A captured signature waits for a click on the page");
            var revision = session.Revision;
            await owner.RunUiAsync(() => owner.PlacePendingAsync(tab, 0, View(150, 380), vw, vh), "Placing…");
            Check(session.Revision == revision + 1 && owner.PendingPlacement is null, "Clicking places the signature as one undoable change");
            var objects = await session.ExtractAsync(0);
            var image = objects.GetProperty("Objects").EnumerateArray().FirstOrDefault(o => o.GetProperty("Type").GetInt32() == 3);
            Check(image.ValueKind != System.Text.Json.JsonValueKind.Undefined, "The signature is an image object on the page");
            var width = image.GetProperty("Right").GetDouble() - image.GetProperty("Left").GetDouble();
            var height = image.GetProperty("Top").GetDouble() - image.GetProperty("Bottom").GetDouble();
            Check(Math.Abs(width - 120) < 1 && Math.Abs(width / height - (double)signature.Width / signature.Height) < .02, "The signature keeps its width and aspect ratio");
            Check(Math.Abs((image.GetProperty("Left").GetDouble() + width / 2) - 150) < 1, "The signature is centered where the page was clicked");
            owner.BeginPlacement(new MainWindow.PendingStamp(StampImages.Date("2026-01-01", 96), "date", 90));
            await owner.RunUiAsync(() => owner.PlacePendingAsync(tab, 0, View(580, 20), vw, vh), "Placing…");
            objects = await session.ExtractAsync(0);
            var images = objects.GetProperty("Objects").EnumerateArray().Where(o => o.GetProperty("Type").GetInt32() == 3).ToArray();
            Check(images.Length == 2 && images.All(o => o.GetProperty("Right").GetDouble() <= 612.01 && o.GetProperty("Bottom").GetDouble() >= -.01),
                "A stamp clicked near the corner is kept inside the page");
            owner.BeginPlacement(new MainWindow.PendingStamp(StampImages.Cross(96), "cross mark", 14));
            owner.SetPageTool(PageTool.Select, false);
            Check(owner.PendingPlacement is null, "Choosing another tool cancels a pending placement");
            var formsMenuText = ((Button)owner.FindName("AddSignatureButton")).ToolTip?.ToString() ?? "";
            Check(formsMenuText.Contains("not a digital signature"), "The signature command says it is not a digital signature");

            // ---- Shell wiring: Tools menu dialogs, form-aware print default, OCR current page ----
            foreach (var name in new[] { "Compress", "Protect", "Unprotect", "Watermark", "HeaderFooter", "Bates", "Crop", "Bookmarks", "Links", "Properties", "Flatten", "ExportImages", "ExtractImages" })
            {
                var tool = owner.CreateQuickTool(name) ?? throw new Exception("No dialog for Tools → " + name);
                Check(tool.GetType().Name == name + "Window", "Tools menu item " + name + " opens its dialog");
                tool.Close();
            }
            Check(await MainWindow.PagesHaveFormFieldsAsync(session, [0]), "Pages with form fields default to image printing");
            var plainSource = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
            using (var plain = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "plain-recovery")), plainSource))
                Check(!await MainWindow.PagesHaveFormFieldsAsync(plain, [0]), "Pages without form fields keep vector printing");
            var ocrWindow = owner.CreateOcrWindow(tab);
            var ocrPage = (int)typeof(OcrWindow).GetField("currentPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ocrWindow)!;
            Check(ocrPage == tab.CurrentPage, "The OCR dialog receives the viewer's current page");
            ocrWindow.Close();

            // ---- Undo storage ----
            var history = ((TextBlock)owner.FindName("PropertyHistory")).Text;
            Check(history.Contains("step") && (history.Contains("KB") || history.Contains("MB")), "Properties show undo steps and their disk space");
            var compact = (Button)owner.FindName("CompactHistoryButton");
            Check(compact.IsEnabled, "Free undo storage is available after several edits");
            var bytes = session.HistoryBytes;
            compact.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitUntilAsync(() => !owner.IsBusy && dialogs.Confirmations.Contains("Free undo storage"), "Compaction did not run.");
            await WaitUntilAsync(() => !owner.IsBusy, "Compaction did not finish.");
            Check(session.HistoryBytes < bytes && session.HistoryCount <= 2, "Freeing undo storage keeps only the original and current versions");
            var saved = Path.Combine(output, "filled-signed.pdf");
            dialogs.NextSave = saved;
            await owner.RunUiAsync(async () => { await owner.SaveAsync(true); }, "Saving…");
            Check(File.Exists(saved), "The filled and signed copy saves after compaction");
            Check(dialogs.Messages.Count == 0, "No error dialogs: " + string.Join(" | ", dialogs.Messages));
        }
        finally { owner.Close(); }
        Check(RecoveryStore.Fingerprint(form) == formHash, "The original form is unchanged");
        File.WriteAllText(Path.Combine(output, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} fill and sign UI checks. Evidence: {output}");
    }

    static void Run(string python, params string[] args)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Fixture script failed: " + error);
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
