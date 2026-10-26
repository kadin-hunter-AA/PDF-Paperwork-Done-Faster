using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Editable text boxes in the shell: styled creation, text edit, move, resize, delete and remembered style.</summary>
static class RichTextUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-rich-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var fixtureHash = RecoveryStore.Fingerprint(fixture);
        var source = Path.Combine(output, "source.pdf");
        File.Copy(fixture, source);
        var engine = new AdvancedEngineLocation(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"), Path.Combine(root, "App.Advanced", "worker.py"));
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
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), "Opening…");
            await WaitUntilAsync(() => !owner.IsBusy, "The document did not open.");
            var tab = owner.ActiveTab!;
            var session = tab.Session;
            var layer = (Canvas)owner.FindName("SelectionLayer0");
            await WaitUntilAsync(() => layer.ActualWidth > 0, "The page was not laid out.");
            double vw = layer.ActualWidth, vh = layer.ActualHeight;
            var page = tab.Pages[0];
            Point View(double x, double y) => new(x * vw / page.Width, (page.Height - y) * vh / page.Height);

            // ---- Style choices for the text box tool are offered and remembered ----
            owner.SetPageTool(PageTool.TextBox);
            Check(((FrameworkElement)owner.FindName("TextStylePanel")).Visibility == Visibility.Visible, "The text box tool shows font, size, bold, italic and alignment");
            ((ComboBox)owner.FindName("TextFontChoice")).SelectedIndex = 1; // Times
            ((ComboBox)owner.FindName("TextSizeChoice")).SelectedItem = "16 pt";
            ((ComboBox)owner.FindName("TextAlignChoice")).SelectedItem = "Center";
            var bold = (System.Windows.Controls.Primitives.ToggleButton)owner.FindName("TextBoldToggle");
            bold.IsChecked = true; bold.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ((Panel)owner.FindName("StrokeSwatches")).Children.OfType<RadioButton>().First(s => (string)s.Tag == "Blue").IsChecked = true;
            var style = owner.TextStyleFor(PageTool.TextBox);
            Check(style is { Font: "Times", FontSize: 16, Bold: true, Align: "Center", Red: 0, Green: 82, Blue: 204 }, "The chosen text style is remembered for the tool");

            // ---- Create a multi-line box ----
            await owner.RunUiAsync(() => owner.BeginNewTextAsync(tab, 0, MainWindow.InlineKind.TextBox, new Rect(View(100, 500), View(320, 440)), vw, vh), "Preparing…");
            var box = owner.InlineEditorBox ?? throw new Exception("The text box editor did not open.");
            Check(box.AcceptsReturn, "Text boxes accept several lines");
            box.Text = "First line\nSecond line";
            await owner.CommitInlineEditAsync();
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "The text box was not added.", 90);
            var boxes = await session.ListFreeTextAsync(engine, 0);
            var created = boxes.Single();
            Check(created.Text.Replace("\r", "") == "First line\nSecond line" && created.Font == "Times" && created.Bold && created.FontSize == 16 && created.Align == "Center" &&
                created.Blue == 204, "The text box is a FreeText annotation with the chosen font, size, bold, alignment and color");

            // ---- Edit its text by clicking inside it with Edit text ----
            owner.SetPageTool(PageTool.EditText);
            Rect ViewOf(FreeTextInfo b) => new(View(b.Left, b.Top), View(b.Right, b.Bottom));
            var view = ViewOf(created);
            await owner.RunUiAsync(() => owner.BeginFreeTextEditAsync(tab, 0, view, layer), "Opening…");
            box = owner.InlineEditorBox ?? throw new Exception("Clicking a text box did not open its editor.");
            Check(box.Text.Replace("\r", "") == "First line\nSecond line", "Clicking a text box opens its current text on the page");
            box.Text = "Changed text";
            await owner.CommitInlineEditAsync();
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "The edit was not applied.", 90);
            var edited = (await session.ListFreeTextAsync(engine, 0)).Single();
            Check(edited.Text == "Changed text" && edited.Font == "Times", "Editing changes the text and keeps the style");

            // ---- Move and resize ----
            view = ViewOf(edited);
            var moved = view; moved.Offset(60, 40);
            var revision = session.Revision;
            await owner.RunUiAsync(() => owner.MoveFreeTextAsync(tab, 0, revision, view, moved, vw, vh), "Moving…");
            var afterMove = (await session.ListFreeTextAsync(engine, 0)).Single();
            var scale = vw / page.Width;
            Check(Math.Abs(afterMove.Left - (edited.Left + 60 / scale)) < 1.5 && Math.Abs(afterMove.Top - (edited.Top - 40 / scale)) < 1.5 && afterMove.Text == "Changed text",
                "Dragging the edge moves the box by the dragged distance");
            view = ViewOf(afterMove);
            var resized = new Rect(view.X, view.Y, view.Width + 80, view.Height + 30);
            revision = session.Revision;
            await owner.RunUiAsync(() => owner.MoveFreeTextAsync(tab, 0, revision, view, resized, vw, vh), "Resizing…");
            var afterResize = (await session.ListFreeTextAsync(engine, 0)).Single();
            Check(afterResize.Right - afterResize.Left > afterMove.Right - afterMove.Left + 50, "Dragging the corner resizes the box");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            var afterUndo = (await session.ListFreeTextAsync(engine, 0)).Single();
            Check(Math.Abs(afterUndo.Right - afterMove.Right) < .5, "Undo restores the previous size");
            await owner.RunUiAsync(() => session.RedoAsync(), "Redo…");

            // ---- Delete by clearing the text ----
            view = ViewOf((await session.ListFreeTextAsync(engine, 0)).Single());
            await owner.RunUiAsync(() => owner.BeginFreeTextEditAsync(tab, 0, view, layer), "Opening…");
            owner.InlineEditorBox!.Text = "";
            await owner.CommitInlineEditAsync();
            await WaitUntilAsync(() => !owner.IsBusy && owner.InlineEditorBox is null, "Delete did not finish.", 90);
            Check((await session.ListFreeTextAsync(engine, 0)).Count == 0, "Clearing a text box and pressing Enter deletes it");
            await owner.RunUiAsync(() => session.UndoAsync(), "Undo…");
            Check((await session.ListFreeTextAsync(engine, 0)).Count == 1, "Undo restores the deleted text box");

            // ---- Save and reopen keeps the box ----
            var saved = Path.Combine(output, "saved.pdf");
            dialogs.NextSave = saved;
            await owner.RunUiAsync(async () => { await owner.SaveAsync(true); }, "Saving…");
            var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "reopen")), saved);
            try { Check((await reopened.ListFreeTextAsync(engine, 0)).Single().Text == "Changed text", "The text box survives save and reopen"); }
            finally { await reopened.CloseAsync(true); }
            Check(dialogs.Messages.Count == 0, "No error dialogs: " + string.Join(" | ", dialogs.Messages));
        }
        finally { owner.Close(); }
        Check(RecoveryStore.Fingerprint(fixture) == fixtureHash, "The frozen fixture is unchanged");
        File.WriteAllText(Path.Combine(output, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} text box UI checks. Evidence: {output}");
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
