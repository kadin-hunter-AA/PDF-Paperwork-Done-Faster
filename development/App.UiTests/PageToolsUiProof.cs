using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Exercises the on-page Edit toolbar: in-place text replacement, new text, notes, text boxes, drawings and markup.</summary>
static class PageToolsUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-page-tools-" + Guid.NewGuid().ToString("N"));
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
        var dialogs = new TestDialogs();
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            owner.SetTheme(ShellTheme.Light);
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), "Opening page-tools fixture…");
            var tab = owner.ActiveTab ?? throw new Exception("No active tab.");
            var session = tab.Session;
            await WaitUntilAsync(() => !owner.IsBusy, "The document did not finish opening.");

            // The Edit command shows the tool row and starts on Edit text.
            var toggle = (ToggleButton)owner.FindName("EditModeToggle");
            var toolbar = (FrameworkElement)owner.FindName("EditToolbar");
            Check(toggle.IsEnabled && toolbar.Visibility != Visibility.Visible, "Edit is available and its tools stay hidden until chosen");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            owner.UpdateLayout();
            Check(toolbar.Visibility == Visibility.Visible && owner.CurrentPageTool == PageTool.EditText && ((RadioButton)owner.FindName("EditTextTool")).IsChecked == true,
                "Choosing Edit shows the page tools with Edit text selected");
            var tools = ((Panel)owner.FindName("ToolButtons")).Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToArray();
            Check(tools.SequenceEqual(["Select", "EditText", "AddText", "Highlight", "Underline", "Strikeout", "Note", "Pen", "Line", "Arrow", "Rectangle", "Ellipse", "TextBox"]),
                "The toolbar offers select, edit/add text, markup, note, drawing and text-box tools");
            Capture(owner, Path.Combine(output, "edit-toolbar.png"));
            // The Graphite theme reproduces the PDF Workspace design; capture it with the Annotate tools showing.
            owner.SetTheme(ShellTheme.Graphite);
            owner.SetPageTool(PageTool.Highlight);
            await owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(owner, Path.Combine(output, "annotate-graphite.png"));
            owner.SetTheme(ShellTheme.Light);
            owner.SetPageTool(PageTool.EditText);

            var layer = (Canvas)owner.FindName("SelectionLayer0");
            await WaitUntilAsync(() => layer.ActualWidth > 0, "The page layer was not laid out.");
            var page = tab.Pages[0];
            double vw = layer.ActualWidth, vh = layer.ActualHeight;
            Point ToView(double x, double y) => new(x * vw / page.Width, (page.Height - y) * vh / page.Height);

            // ---- In-place replacement of existing text ----
            var data = await session.ExtractAsync(0);
            var target = data.GetProperty("Objects").EnumerateArray().First(o => o.GetProperty("Type").GetInt32() == 1 &&
                o.GetProperty("CanEdit").GetBoolean() && o.GetProperty("HasBounds").GetBoolean());
            var targetIndex = target.GetProperty("Id").GetString()!.Split(':').Last();
            var oldText = target.GetProperty("Text").GetString() ?? "";
            var click = ToView((target.GetProperty("Left").GetDouble() + target.GetProperty("Right").GetDouble()) / 2,
                (target.GetProperty("Bottom").GetDouble() + target.GetProperty("Top").GetDouble()) / 2);
            var revision = session.Revision;
            var windowsBefore = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToHashSet();
            await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Opening text…");
            var box = owner.InlineEditorBox ?? throw new Exception("Clicking editable text did not open an on-page editor.");
            Check(box.Text == oldText && ReferenceEquals(VisualTreeHelper.GetParent(box), layer),
                "Clicking existing text opens an editor on the page itself, prefilled with that text");
            Check(Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).All(windowsBefore.Contains), "No dialog window opens for in-place text editing");
            Check(session.Revision == revision && !session.IsDirty, "Opening the in-place editor changes nothing");
            var editorLeft = Canvas.GetLeft(box); var editorTop = Canvas.GetTop(box);
            var expected = ToView(target.GetProperty("Left").GetDouble(), target.GetProperty("Top").GetDouble());
            Check(Math.Abs(editorLeft - expected.X) < 6 && Math.Abs(editorTop - expected.Y) < Math.Max(12, box.FontSize),
                "The in-place editor sits over the clicked text's position");
            box.Text = "Edited on page " + Guid.NewGuid().ToString("N")[..6];
            owner.UpdateLayout();
            Capture(owner, Path.Combine(output, "inline-text-editor.png"));

            // Escape cancels without a change.
            PressKey(box, Key.Escape);
            Check(owner.InlineEditorBox is null && session.Revision == revision && !session.IsDirty, "Esc closes the on-page editor and leaves the PDF unchanged");

            await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Opening text…");
            box = owner.InlineEditorBox ?? throw new Exception("The editor did not reopen.");
            var newText = "Edited on page " + Guid.NewGuid().ToString("N")[..6];
            box.Text = newText;
            PressKey(box, Key.Enter);
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Enter did not apply the in-place text edit.");
            async Task<string?> TargetText(DocumentSession s) =>
                (await s.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Where(o => o.GetProperty("Type").GetInt32() == 1 &&
                    o.GetProperty("Id").GetString()!.Split(':').Last() == targetIndex).Select(o => o.GetProperty("Text").GetString()).FirstOrDefault();
            Check(await TargetText(session) == newText && session.Revision == revision + 1 && session.IsDirty,
                "Enter replaces the clicked object's text as one undoable revision");
            await session.UndoAsync();
            Check(await TargetText(session) == oldText, "Undo restores the original text");
            await session.RedoAsync();
            Check(await TargetText(session) == newText, "Redo reapplies the in-place edit");
            await owner.RunUiAsync(() => Task.CompletedTask, "Refreshing…");

            // Unchanged text closes without creating a revision.
            revision = session.Revision;
            await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Opening text…");
            Check(owner.InlineEditorBox is not null, "Edited text can be clicked and opened again");
            await owner.CommitInlineEditAsync();
            Check(owner.InlineEditorBox is null && session.Revision == revision, "Applying unchanged text closes the editor without a new revision");

            // Empty page space never opens an editor.
            await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, new Point(4, 4), vw, vh), "Opening text…");
            Check(owner.InlineEditorBox is null && session.Revision == revision, "Clicking empty page space with Edit text opens nothing");

            // ---- New text at a click ----
            owner.SetPageTool(PageTool.AddText);
            var addAt = ToView(72, 300);
            revision = session.Revision;
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.Add, new Rect(addAt, addAt), vw, vh), "Preparing text…");
            box = owner.InlineEditorBox ?? throw new Exception("Add text did not open an on-page editor.");
            box.Text = "Typed straight onto the page";
            owner.UpdateLayout();
            Capture(owner, Path.Combine(output, "inline-add-text.png"));
            PressKey(box, Key.Enter);
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Enter did not add the new text.");
            var afterAdd = await session.ExtractAsync(0);
            var added = afterAdd.GetProperty("Objects").EnumerateArray().FirstOrDefault(o => o.GetProperty("Type").GetInt32() == 1 &&
                o.GetProperty("Text").GetString() == "Typed straight onto the page");
            Check(added.ValueKind == JsonValueKind.Object && session.Revision == revision + 1, "Add text places typed text as one revision");
            Check(Math.Abs(added.GetProperty("Left").GetDouble() - 72) < 3 && added.GetProperty("Top").GetDouble() <= 301 && added.GetProperty("Top").GetDouble() > 280,
                "New text lands where it was clicked (top-left within a few points)");

            // Accented and other non-Latin text is added in an embedded installed font.
            revision = session.Revision;
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.Add, new Rect(addAt, addAt), vw, vh), "Preparing text…");
            box = owner.InlineEditorBox ?? throw new Exception("Add text did not reopen.");
            box.Text = "Café Ωμέγα";
            await owner.CommitInlineEditAsync();
            await WaitUntilAsync(() => !owner.IsBusy, "Unicode text did not finish.", 90);
            Check(owner.InlineEditorBox is null && session.Revision == revision + 1 && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Café Ωμέγα"),
                "Accented and Greek text is added with an embedded font as one revision");
            // A character no available font can show is refused in place, keeping the draft.
            revision = session.Revision;
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.Add, new Rect(ToView(72, 250), ToView(72, 250)), vw, vh), "Preparing text…");
            box = owner.InlineEditorBox ?? throw new Exception("Add text did not reopen.");
            box.Text = "\U0001F600 x";
            await owner.CommitInlineEditAsync();
            await WaitUntilAsync(() => !owner.IsBusy, "Refusal did not finish.", 90);
            Check(owner.InlineEditorBox is not null && owner.InlineEditorBox.Text == "\U0001F600 x" && session.Revision == revision,
                "Characters the font cannot show are refused without losing the draft or changing the PDF");
            PressKey(owner.InlineEditorBox!, Key.Escape);

            // ---- Sticky note ----
            owner.SetPageTool(PageTool.Note);
            var notesBefore = CountAnnotations(await session.ExtractAsync(0), 1);
            var noteAt = ToView(400, 700);
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.Note, new Rect(noteAt, noteAt), vw, vh), "Preparing note…");
            box = owner.InlineEditorBox ?? throw new Exception("Note did not open an on-page editor.");
            box.Text = "Please review this paragraph";
            owner.UpdateLayout();
            Capture(owner, Path.Combine(output, "inline-note.png"));
            PressKey(box, Key.Enter);
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Enter did not add the note.");
            var afterNote = await session.ExtractAsync(0);
            var note = afterNote.GetProperty("Annotations").EnumerateArray().LastOrDefault(a => a.GetProperty("Subtype").GetInt32() == 1);
            Check(CountAnnotations(afterNote, 1) == notesBefore + 1 && note.GetProperty("Contents").GetString() == "Please review this paragraph" &&
                Math.Abs(note.GetProperty("Left").GetDouble() - 400) < 2 && Math.Abs(note.GetProperty("Top").GetDouble() - 700) < 2,
                "Note places a sticky note with the typed comment at the click");

            // ---- Drawings dragged on the page ----
            owner.SetPageTool(PageTool.Rectangle);
            var squaresBefore = CountAnnotations(await session.ExtractAsync(0), 5);
            await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Rectangle, [ToView(100, 500), ToView(220, 440)], vw, vh), "Adding drawing…");
            var afterRect = await session.ExtractAsync(0);
            var square = afterRect.GetProperty("Annotations").EnumerateArray().LastOrDefault(a => a.GetProperty("Subtype").GetInt32() == 5);
            Check(CountAnnotations(afterRect, 5) == squaresBefore + 1 && Math.Abs(square.GetProperty("Left").GetDouble() - 100) < 3 &&
                Math.Abs(square.GetProperty("Top").GetDouble() - 500) < 3 && Math.Abs(square.GetProperty("Right").GetDouble() - 220) < 3,
                "A dragged rectangle is added where it was drawn");
            var inksBefore = CountAnnotations(afterRect, 15);
            await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Arrow, [ToView(300, 420), ToView(240, 480)], vw, vh), "Adding drawing…");
            await owner.RunUiAsync(() => owner.CommitDrawingAsync(tab, 0, PageTool.Pen,
                Enumerable.Range(0, 30).Select(i => ToView(320 + i * 4, 450 + 15 * Math.Sin(i / 3.0))).ToArray(), vw, vh), "Adding drawing…");
            Check(CountAnnotations(await session.ExtractAsync(0), 15) == inksBefore + 2, "Dragged arrows and pen strokes are added as native ink");

            // ---- Text box dragged then typed ----
            owner.SetPageTool(PageTool.TextBox);
            var freeTextBefore = CountAnnotations(await session.ExtractAsync(0), 3);
            var boxFrom = ToView(100, 400); var boxTo = ToView(330, 370);
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.TextBox, new Rect(boxFrom, boxTo), vw, vh), "Preparing text box…");
            box = owner.InlineEditorBox ?? throw new Exception("Text box did not open an on-page editor.");
            box.Text = "Boxed note";
            PressKey(box, Key.Enter);
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Enter did not add the text box.", 90);
            var afterBox = await session.ExtractAsync(0);
            var engine = new AdvancedEngineLocation(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"), Path.Combine(root, "App.Advanced", "worker.py"));
            Check(CountAnnotations(afterBox, 3) == freeTextBefore + 1 && (await session.ListFreeTextAsync(engine, 0)).Any(b => b.Text == "Boxed note"),
                "A dragged text box becomes an editable FreeText annotation with the typed text");

            // ---- Highlight across selected text ----
            owner.SetPageTool(PageTool.Highlight);
            var layout = await session.ExtractTextLayoutAsync(0);
            var characters = layout.GetProperty("Characters").EnumerateArray().Select(c => new PdfTextCharacter(c.GetProperty("Index").GetInt32(), c.GetProperty("Text").GetString() ?? "",
                c.GetProperty("HasBounds").GetBoolean(), c.GetProperty("Left").GetDouble(), c.GetProperty("Bottom").GetDouble(), c.GetProperty("Right").GetDouble(), c.GetProperty("Top").GetDouble())).ToList();
            tab.Pages[0].Characters = characters;
            var first = characters.First(c => c.HasBounds && !string.IsNullOrWhiteSpace(c.Text));
            SetField(owner, "selectionPage", 0); SetField(owner, "selectionAnchor", first.Index); SetField(owner, "selectionEnd", first.Index + 6);
            var highlightsBefore = CountAnnotations(await session.ExtractAsync(0), 9);
            await owner.RunUiAsync(() => owner.ApplyMarkupToSelectionAsync(tab, 0, "Highlight"), "Adding highlight…");
            Check(CountAnnotations(await session.ExtractAsync(0), 9) == highlightsBefore + 1, "Releasing a drag-selection with Highlight adds a highlight");

            await owner.RunUiAsync(() => Task.CompletedTask, "Refreshing…");
            owner.UpdateLayout();
            Capture(owner, Path.Combine(output, "after-page-tools.png"));

            // Save a copy and reopen it independently.
            var saved = Path.Combine(output, "page-tools-saved.pdf");
            await session.SaveAsync(saved);
            using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "reopen-recovery")), saved))
            {
                var check = await reopened.ExtractAsync(0);
                Check(check.GetProperty("Text").GetString()!.Contains(newText, StringComparison.Ordinal) &&
                    check.GetProperty("Text").GetString()!.Contains("Typed straight onto the page", StringComparison.Ordinal) &&
                    CountAnnotations(check, 1) == notesBefore + 1 && CountAnnotations(check, 9) == highlightsBefore + 1,
                    "Saved copy reopens with the in-place text edit, new text, note and highlight");
            }

            owner.SetPageTool(PageTool.Select, editMode: false);
            Check(toolbar.Visibility != Visibility.Visible && toggle.IsChecked == false, "Turning Edit off hides the page tools");
            Check(dialogs.Messages.Count == 0, "No error dialogs were raised: " + string.Join("; ", dialogs.Messages));
            Check(RecoveryStore.Fingerprint(source) == sourceHash, "The frozen source PDF is unchanged");
        }
        finally
        {
            if (owner.IsVisible)
            {
                try { await owner.RunUiAsync(async () => { if (await owner.CloseAllAsync()) typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true); }, "Closing…"); }
                catch { }
            }
            owner.Close();
        }
        await ProveEmbeddedFontAsync(root, output, worker, Check);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} page-tools UI checks. Evidence: {output}");
    }

    /// <summary>
    /// Real documents embed subset fonts and are often tagged (Word's default). The built-in editor refuses
    /// both, so Edit text must route through the companion engine while keeping marked content intact.
    /// </summary>
    static async Task ProveEmbeddedFontAsync(string root, string output, string worker, Action<bool, string> check)
    {
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var frozen = Path.Combine(root, "fixtures", "corpus-v1", "engine", "font-subset-original.pdf");
        var frozenHash = RecoveryStore.Fingerprint(frozen);
        var tagged = Path.Combine(output, "tagged-subset-font.pdf");
        var script = Path.Combine(output, "make-tagged.py");
        // Wrap the page in one marked-content sequence and declare a structure tree, like a tagged export.
        File.WriteAllText(script, """
            import sys
            from pypdf import PdfReader, PdfWriter
            from pypdf.generic import DictionaryObject, NameObject, BooleanObject, DecodedStreamObject
            w = PdfWriter(clone_from=PdfReader(sys.argv[1]))
            page = w.pages[0]
            data = page.get_contents().get_data()
            content = DecodedStreamObject(); content.set_data(b"/P <</MCID 0>> BDC\n" + data + b"\nEMC\n")
            page[NameObject("/Contents")] = w._add_object(content)
            w.root_object[NameObject("/StructTreeRoot")] = w._add_object(DictionaryObject({NameObject("/Type"): NameObject("/StructTreeRoot")}))
            w.root_object[NameObject("/MarkInfo")] = DictionaryObject({NameObject("/Marked"): BooleanObject(True)})
            with open(sys.argv[2], "xb") as f: w.write(f)
            """);
        var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", "-B", script, frozen, tagged }) start.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Tagged fixture generation failed."); }

        var dialogs = new TestDialogs();
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "embedded-recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(tagged), "Opening tagged embedded-font fixture…");
            var tab = owner.ActiveTab ?? throw new Exception("No active tab.");
            var session = tab.Session;
            owner.SetPageTool(PageTool.EditText);
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            await WaitUntilAsync(() => layer.ActualWidth > 0, "The page layer was not laid out.");
            var page = tab.Pages[0];
            double vw = layer.ActualWidth, vh = layer.ActualHeight;
            var data = await session.ExtractAsync(0);
            var target = data.GetProperty("Objects").EnumerateArray().First(o => o.GetProperty("Type").GetInt32() == 1 && o.GetProperty("Text").GetString() == "ABC");
            check(!target.GetProperty("CanEdit").GetBoolean(), "Embedded subset-font text is refused by the built-in standard-font editor (the case users hit)");
            var click = new Point((target.GetProperty("Left").GetDouble() + target.GetProperty("Right").GetDouble()) / 2 * vw / page.Width,
                (page.Height - (target.GetProperty("Bottom").GetDouble() + target.GetProperty("Top").GetDouble()) / 2) * vh / page.Height);
            async Task OpenAsync()
            {
                await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Opening text…");
            }
            async Task<string> PageText() => (await session.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";

            await OpenAsync();
            var box = owner.InlineEditorBox ?? throw new Exception("Embedded-font text did not open an on-page editor: " + ((TextBlock)owner.FindName("Status")).Text);
            check(box.Text == "ABC", "Clicking embedded-font text in a tagged PDF opens the on-page editor with its text");
            owner.UpdateLayout();
            Capture(owner, Path.Combine(output, "embedded-font-editor.png"));

            var revision = session.Revision;
            box.Text = "XYZ";
            await owner.CommitInlineEditAsync();
            var status = ((TextBlock)owner.FindName("Status")).Text;
            check(owner.InlineEditorBox?.Text == "XYZ" && session.Revision == revision && status.Contains("doesn't include some of the characters", StringComparison.Ordinal),
                "Characters missing from the embedded subset are refused with a plain explanation, keeping the draft");

            owner.InlineEditorBox!.Text = "ABCABC";
            await owner.CommitInlineEditAsync();
            status = ((TextBlock)owner.FindName("Status")).Text;
            check(owner.InlineEditorBox?.Text == "ABCABC" && session.Revision == revision && status.Contains("Ctrl+Enter", StringComparison.Ordinal),
                "Wider replacements are held back with a Ctrl+Enter override instead of silently overlapping text");

            owner.InlineEditorBox!.Text = "CAB";
            PressKey(owner.InlineEditorBox, Key.Enter);
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Enter did not apply the embedded-font edit.", 60);
            var text = await PageText();
            check(session.Revision == revision + 1 && text.Contains("CAB", StringComparison.Ordinal) && !text.Contains("ABC", StringComparison.Ordinal) &&
                text.Contains("Unrelated font fixture text", StringComparison.Ordinal), "Enter replaces the embedded-font text in place as one revision, leaving other text intact");
            await session.UndoAsync();
            check((await PageText()).Contains("ABC", StringComparison.Ordinal), "Undo restores the original embedded-font text");
            await session.RedoAsync();
            await owner.RunUiAsync(() => Task.CompletedTask, "Refreshing…");

            // Ctrl+Enter path: accept a wider replacement after the warning.
            await OpenAsync();
            box = owner.InlineEditorBox ?? throw new Exception("The edited text could not be reopened.");
            box.Text = "CABCAB";
            await owner.CommitInlineEditAsync(allowWider: true);
            await WaitUntilAsync(() => !owner.IsBusy, "The wider edit did not finish.", 60);
            check(owner.InlineEditorBox is null && (await PageText()).Contains("CABCAB", StringComparison.Ordinal), "Ctrl+Enter applies a wider replacement on request");

            var saved = Path.Combine(output, "tagged-edited.pdf");
            await session.SaveAsync(saved);
            var verify = Path.Combine(output, "verify-tagged.py");
            File.WriteAllText(verify, """
                import sys
                from pypdf import PdfReader
                from pypdf.generic import ContentStream
                r = PdfReader(sys.argv[1]); page = r.pages[0]
                ops = [op for _, op in ContentStream(page.get_contents(), r).operations if op in (b"BDC", b"BMC", b"EMC")]
                ok = "/StructTreeRoot" in r.root_object and r.root_object["/MarkInfo"]["/Marked"] and ops == [b"BDC", b"EMC"] and "CABCAB" in page.extract_text()
                print("OK" if ok else "FAIL", ops)
                sys.exit(0 if ok else 1)
                """);
            var verifyStart = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-I", "-B", verify, saved }) verifyStart.ArgumentList.Add(arg);
            using (var process = System.Diagnostics.Process.Start(verifyStart)!)
            {
                var result = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
                check(process.ExitCode == 0, "Independent pypdf check: saved copy keeps its structure tree, Marked flag and exact marked-content sequence (" + result.Trim() + ")");
            }
            check(dialogs.Messages.Count == 0, "No error dialogs were raised for embedded-font editing: " + string.Join("; ", dialogs.Messages));
            check(RecoveryStore.Fingerprint(frozen) == frozenHash, "The frozen subset-font fixture is unchanged");
        }
        finally
        {
            if (owner.IsVisible)
            {
                try { await owner.RunUiAsync(async () => { if (await owner.CloseAllAsync()) typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true); }, "Closing…"); }
                catch { }
            }
            owner.Close();
        }
    }

    /// <summary>Diagnostic: clicks every text object on page 1 of a PDF with Edit text and reports which open in place.
    /// Prints only counts and refusal reasons, never the document's text.</summary>
    internal static async Task ProbeAsync(string root, string pdf)
    {
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var output = Path.Combine(Path.GetTempPath(), "page-tools-probe-" + Guid.NewGuid().ToString("N"));
        var owner = new MainWindow(worker, new RecoveryStore(output), new TestDialogs())
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish loading.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(pdf), "Opening probe PDF…");
            var tab = owner.ActiveTab!;
            owner.SetPageTool(PageTool.EditText);
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            await WaitUntilAsync(() => layer.ActualWidth > 0, "The page layer was not laid out.");
            var page = tab.Pages[0];
            double vw = layer.ActualWidth, vh = layer.ActualHeight;
            var objects = (await tab.Session.ExtractAsync(0)).GetProperty("Objects").EnumerateArray()
                .Where(o => o.GetProperty("Type").GetInt32() == 1 && o.GetProperty("HasBounds").GetBoolean() && !string.IsNullOrWhiteSpace(o.GetProperty("Text").GetString())).ToArray();
            int opened = 0, native = 0; var reasons = new Dictionary<string, int>();
            foreach (var o in objects)
            {
                var click = new Point((o.GetProperty("Left").GetDouble() + o.GetProperty("Right").GetDouble()) / 2 * vw / page.Width,
                    (page.Height - (o.GetProperty("Bottom").GetDouble() + o.GetProperty("Top").GetDouble()) / 2) * vh / page.Height);
                await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, click, vw, vh), "Opening text…");
                if (owner.InlineEditorBox is { } box) { opened++; if (o.GetProperty("CanEdit").GetBoolean()) native++; PressKey(box, Key.Escape); }
                else { var status = ((TextBlock)owner.FindName("Status")).Text; var reason = status.Contains(" — ") ? status[(status.IndexOf(" — ") + 3)..] : status; reasons[reason] = reasons.GetValueOrDefault(reason) + 1; }
            }
            Console.WriteLine($"PROBE {Path.GetFileName(pdf)}: {objects.Length} text objects, {opened} open in place ({native} built-in, {opened - native} embedded-font engine), revision unchanged: {tab.Session.Revision == 0 || !tab.Session.IsDirty}");
            foreach (var (reason, count) in reasons) Console.WriteLine($"  refused x{count}: {reason}");

            // Timed edits the way a user does them: click, shorten the text by one character, Enter.
            var target = objects.First(o => (o.GetProperty("Text").GetString() ?? "").Trim().Length > 3);
            var at = new Point((target.GetProperty("Left").GetDouble() + target.GetProperty("Right").GetDouble()) / 2 * vw / page.Width,
                (page.Height - (target.GetProperty("Bottom").GetDouble() + target.GetProperty("Top").GetDouble()) / 2) * vh / page.Height);
            for (var round = 1; round <= 2; round++)
            {
                if (round == 2) await Task.Delay(TimeSpan.FromSeconds(8)); // a user reading the page while the engine preloads
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await owner.RunUiAsync(() => owner.BeginTextEditAtAsync(tab, 0, at, vw, vh), "Opening text…");
                var openSeconds = clock.Elapsed.TotalSeconds;
                if (owner.InlineEditorBox is not { } editor) { Console.WriteLine($"  timing round {round}: editor did not open ({((TextBlock)owner.FindName("Status")).Text})"); break; }
                editor.Text = editor.Text.TrimEnd()[..^1];
                clock.Restart();
                PressKey(editor, Key.Enter);
                await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "The timed edit did not finish.", 120);
                var saveSeconds = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"  timing round {round}{(round == 1 ? " (right after choosing Edit text)" : " (engine preloaded)")}: open {openSeconds:0.0} s, apply {saveSeconds:0.0} s, revision {tab.Session.Revision}");
                await owner.RunUiAsync(() => tab.Session.UndoAsync(), "Undoing…");
            }
        }
        finally
        {
            try { await owner.RunUiAsync(async () => { if (await owner.CloseAllAsync()) typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true); }, "Closing…"); } catch { }
            owner.Close();
        }
    }

    static int CountAnnotations(JsonElement data, int subtype) =>
        data.GetProperty("Annotations").EnumerateArray().Count(a => a.GetProperty("Subtype").GetInt32() == subtype);

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
