using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class AnnotationManagementUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-annotation-management-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var hash = RecoveryStore.Fingerprint(source);
        var dialogs = new TestDialogs();
        var owner = new MainWindow(Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe"),
            new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        owner.Show(); await Wait(() => !owner.IsBusy); await owner.OpenPathAsync(source);
        var session = owner.CurrentSession!;
        await session.AddCommentAsync(0, "UI note before edit", 220, 650, 244, 674);
        await session.AddMarkupAsync(0, "Highlight", [new { Left = 72, Bottom = 700, Right = 180, Top = 720 }]);
        await owner.RunUiAsync(() => Task.CompletedTask);
        owner.SetTheme(ShellTheme.Light);
        var edit = new AnnotationManagerWindow(session, 0) { Owner = owner };
        const string replacement = "Edited UI note — café\nLine 2.";
        var revision = session.Revision;
        Check(Interact(edit, async () =>
        {
            var list = Find<ListBox>(edit, "Existing annotations on the current page"); await Wait(() => list.IsEnabled && list.Items.Count == 2);
            list.SelectedIndex = 0;
            var text = Find<TextBox>(edit, "Edit selected sticky-note comment");
            Check(text.IsEnabled && text.Text == "UI note before edit", "Selecting an unlocked sticky note loads its editable original comment");
            var save = Find<Button>(edit, "Save the selected existing sticky-note comment");
            Check(!save.IsEnabled, "Unchanged comments cannot create a redundant edit checkpoint");
            text.Text = replacement; Check(save.IsEnabled, "A changed note comment enables Save");
            Capture(edit, Path.Combine(output, "annotation-manager-light.png"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => !edit.IsVisible);
        }) == true, "The real manager Save button commits the updated comment");
        Check(session.Revision == revision + 1 && (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray()
            .Any(a => a.GetProperty("Contents").GetString() == replacement), "The exact Unicode multiline comment is retained after the dialog closes");
        await owner.RunUiAsync(() => Task.CompletedTask);
        var undoRevision = session.Revision; ApplicationCommands.Undo.Execute(null, owner);
        await Wait(() => !owner.IsBusy && session.Revision > undoRevision);
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Any(a => a.GetProperty("Contents").GetString() == "UI note before edit"), "The real shell Undo command restores the original note contents");
        var redoRevision = session.Revision; ApplicationCommands.Redo.Execute(null, owner);
        await Wait(() => !owner.IsBusy && session.Revision > redoRevision);
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Any(a => a.GetProperty("Contents").GetString() == replacement), "The real shell Redo command restores the updated note");
        owner.SetTheme(ShellTheme.Dark);
        var remove = new AnnotationManagerWindow(session, 0) { Owner = owner };
        Check(Interact(remove, async () =>
        {
            var list = Find<ListBox>(remove, "Existing annotations on the current page"); await Wait(() => list.IsEnabled && list.Items.Count == 2);
            list.SelectedIndex = 1;
            Check(!Find<TextBox>(remove, "Edit selected sticky-note comment").IsEnabled, "Highlight annotations do not enable the sticky-note text editor");
            var delete = Find<Button>(remove, "Delete the selected existing annotation"); Check(delete.IsEnabled, "An unlocked highlight can be selected for deletion");
            Capture(remove, Path.Combine(output, "annotation-manager-dark.png"));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => !remove.IsVisible);
        }) == true, "The real manager Delete button removes the selected highlight");
        var remaining = (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().ToArray();
        Check(remaining.Length == 1 && remaining[0].GetProperty("Contents").GetString() == replacement, "Deletion preserves the unrelated updated sticky note");
        await owner.RunUiAsync(() => Task.CompletedTask);
        // Move and restyle drawings through the real manager controls, then undo back to the single surviving note.
        await session.AddDrawingAsync(0, "Rectangle", 100, 500, 220, 580);
        await session.AddInkAsync(0, "Pen", [(100, 300), (160, 340), (220, 300)]);
        var drawingRevision = session.Revision;
        owner.SetTheme(ShellTheme.Light);
        var movePanel = new AnnotationManagerWindow(session, 0) { Owner = owner };
        Check(Interact(movePanel, async () =>
        {
            var list = Find<ListBox>(movePanel, "Existing annotations on the current page"); await Wait(() => list.IsEnabled && list.Items.Count == 3);
            var moveButton = Find<Button>(movePanel, "Move the selected existing drawing");
            var restyleButton = Find<Button>(movePanel, "Apply color and width to the selected existing drawing");
            list.SelectedIndex = 0;
            Check(!moveButton.IsEnabled && !restyleButton.IsEnabled, "Sticky notes do not enable drawing move or restyle");
            list.SelectedIndex = 1;
            Check(restyleButton.IsEnabled && !moveButton.IsEnabled, "A rectangle enables restyle, and move waits for a nonzero distance");
            Find<TextBox>(movePanel, "Move selected drawing right in PDF points").Text = "abc";
            Check(!moveButton.IsEnabled, "A non-numeric move distance keeps Move disabled");
            Find<TextBox>(movePanel, "Move selected drawing right in PDF points").Text = "30";
            Find<TextBox>(movePanel, "Move selected drawing up in PDF points").Text = "-20";
            Check(moveButton.IsEnabled, "A valid distance enables Move");
            Capture(movePanel, Path.Combine(output, "annotation-manager-move.png"));
            moveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => !movePanel.IsVisible);
        }) == true, "The real manager Move button moves the selected rectangle");
        var movedRect = (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Single(a => a.GetProperty("Subtype").GetInt32() == 5);
        Check(movedRect.GetProperty("Left").GetDouble() == 130 && movedRect.GetProperty("Bottom").GetDouble() == 480, "The rectangle moved by exactly the entered distance");
        await owner.RunUiAsync(() => Task.CompletedTask);
        var stylePanel = new AnnotationManagerWindow(session, 0) { Owner = owner };
        var beforeStyle = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(Interact(stylePanel, async () =>
        {
            var list = Find<ListBox>(stylePanel, "Existing annotations on the current page"); await Wait(() => list.IsEnabled && list.Items.Count == 3);
            list.SelectedIndex = 2;
            Find<ComboBox>(stylePanel, "Selected drawing color").SelectedIndex = 1;
            Find<ComboBox>(stylePanel, "Selected drawing line width in points").SelectedIndex = 3;
            var apply = Find<Button>(stylePanel, "Apply color and width to the selected existing drawing");
            Check(apply.IsEnabled, "A pen stroke enables restyle");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => !stylePanel.IsVisible);
        }) == true, "The real manager style button restyles the selected pen stroke");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() != beforeStyle, "The restyled pen stroke renders differently");
        for (var i = 0; i < 4; i++) await session.UndoAsync();
        Check(session.Revision > drawingRevision && (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == 1,
            "Undo returns to the single surviving note after drawing management");
        await owner.RunUiAsync(() => Task.CompletedTask);
        var saved = Path.Combine(output, "managed.pdf"); dialogs.NextSave = saved; ApplicationCommands.SaveAs.Execute(null, owner);
        await Wait(() => !owner.IsBusy && File.Exists(saved) && !session.IsDirty);
        await owner.CloseTabAsync(owner.ActiveTab!); await owner.OpenPathAsync(saved);
        var reopened = (await owner.CurrentSession!.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().ToArray();
        Check(reopened.Length == 1 && reopened[0].GetProperty("Contents").GetString() == replacement && reopened[0].GetProperty("Right").GetDouble() == 244,
            "The shell saves and reopens the managed annotations with unchanged surviving note geometry");
        var locked = Path.Combine(output, "locked-note.pdf");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", "import sys; from pypdf import PdfReader,PdfWriter; from pypdf.generic import NameObject,NumberObject; w=PdfWriter(clone_from=PdfReader(sys.argv[1])); w.pages[0]['/Annots'][0].get_object()[NameObject('/F')]=NumberObject(64); w.write(sys.argv[2])", saved, locked }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Protected UI note fixture generation failed.");
        }
        await owner.OpenPathAsync(locked);
        var protectedDialog = new AnnotationManagerWindow(owner.CurrentSession!, 0) { Owner = owner };
        Interact(protectedDialog, async () =>
        {
            var list = Find<ListBox>(protectedDialog, "Existing annotations on the current page"); await Wait(() => list.IsEnabled && list.Items.Count == 1);
            var text = Find<TextBox>(protectedDialog, "Edit selected sticky-note comment");
            Check(text.IsEnabled && text.IsReadOnly && text.Text == replacement, "A protected note remains readable and selectable without becoming editable");
            Check(!Find<Button>(protectedDialog, "Save the selected existing sticky-note comment").IsEnabled && !Find<Button>(protectedDialog, "Delete the selected existing annotation").IsEnabled,
                "Protected notes disable both mutation actions in the real dialog");
            Children<Button>(protectedDialog).Single(b => b.Content?.ToString() == "Close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !protectedDialog.IsVisible);
        });
        Check(RecoveryStore.Fingerprint(source) == hash, "The complete manager UI workflow preserves frozen source bytes");
        await owner.CloseTabAsync(owner.ActiveTab!); owner.Close();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} annotation manager UI checks. Evidence: {output}");
    }
    static bool? Interact(Window dialog, Func<Task> action)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) => { try { await action(); } catch (Exception error) { failure = error; dialog.Close(); } };
        var result = dialog.ShowDialog(); if (failure is not null) throw new Exception("Annotation manager UI interaction failed.", failure); return result;
    }
    static async Task Wait(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew(); while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);
        if (!ready()) throw new TimeoutException("Annotation manager did not complete its bounded UI operation.");
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var item in Children<T>(child)) yield return item;
        }
    }
    static T Find<T>(DependencyObject root, string name) where T : DependencyObject => Children<T>(root).Single(c => AutomationProperties.GetName(c) == name);
    static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content; if (content is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        content.Measure(new Size(640, 740)); content.Arrange(new Rect(0, 0, 640, 740)); content.UpdateLayout();
        var image = new RenderTargetBitmap(640, 740, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
