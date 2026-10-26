using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Exercises the viewer's real hit-test route into the existing object editors.</summary>
static class DirectEditingUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-direct-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var togglePath = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var toggleHash = RecoveryStore.Fingerprint(togglePath);
        var toggleOwner = NewOwner(worker, Path.Combine(output, "toolbar-recovery"), new TestDialogs());
        try
        {
            toggleOwner.Show();
            await WaitUntilAsync(() => !toggleOwner.IsBusy, "The shell did not finish its initial load.");
            await toggleOwner.RunUiAsync(() => toggleOwner.OpenPathAsync(togglePath), "Opening direct-edit toolbar proof…");
            var directToggle = Find<System.Windows.Controls.Primitives.ToggleButton>(toggleOwner, "Edit this PDF on the page");
            directToggle.IsChecked = true;
            directToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            toggleOwner.UpdateLayout();
            Capture(toggleOwner, Path.Combine(output, "direct-edit-toolbar-enabled.png"));
            if (toggleOwner.CurrentPageTool != PageTool.EditText) throw new Exception("Edit did not select the Edit text tool.");
            directToggle.IsChecked = false;
            directToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (RecoveryStore.Fingerprint(togglePath) != toggleHash || toggleOwner.CurrentSession?.IsDirty != false)
                throw new Exception("Changing the direct-edit toolbar toggle does not alter the source or document session.");
        }
        finally
        {
            await CloseOwnerAsync(toggleOwner);
        }

        await ProveTextHitAsync(root, output, worker, Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf"), signed: false);
        await ProveTextHitAsync(root, output, worker, Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf"), signed: true);
        var imagePath = Path.Combine(root, "fixtures", "corpus-v1", "engine", "objects-original.pdf");
        await ProveObjectHitAsync(root, output, worker, imagePath, type: 3, signed: false);

        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
        {
            Passed = new[]
            {
                "The Edit toolbar toggle is visible, labeled, and selects the Edit text tool.",
                "Empty page space opens no editor.",
                "A text-object hit opens ObjectEditorWindow with the freshly extracted object selected.",
                "Signed-copy consent preserves the original and resolves the text object again after worker reload.",
                "An image-object hit opens ImageEditorWindow with the freshly extracted object selected.",
                "The image editor's committed-page preview is populated before cancellation.",
                "Closing the selection editors without changes preserves source bytes and session revision/dirty state.",
                "Selected existing text has an isolated candidate preview before editing.",
                "Rejected overflow replacement refreshes to the same uniquely identified object while preserving the draft.",
                "Committed replacement passes undo, redo, recovery, save/reopen, and frozen-source preservation."
            },
            Screenshots = new[] { "direct-edit-toolbar-enabled.png" },
            Cancellation = "Selection dialogs were canceled; the lifecycle dialog performed one rejected edit followed by one accepted replacement."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS direct-edit UI selection proof. Evidence: {output}");
    }

    static MainWindow NewOwner(string worker, string recovery, TestDialogs dialogs) => new(worker,
        new RecoveryStore(recovery), dialogs)
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000,
        Width = 1180,
        Height = 820,
        ShowInTaskbar = false,
        ShowActivated = false
    };

    static async Task ProveTextHitAsync(string root, string output, string worker, string source, bool signed)
    {
        await ProveObjectHitAsync(root, output, worker, source, type: 1, signed: signed);
        if (!signed) await ProveTextEditLifecycleAsync(root, output, worker, source);
    }

    static async Task ProveObjectHitAsync(string root, string output, string worker, string source, int type, bool signed)
    {
        var label = Path.GetFileNameWithoutExtension(source);
        var sourceHash = RecoveryStore.Fingerprint(source);
        var dialogs = new TestDialogs { SignedConsent = true };
        var owner = NewOwner(worker, Path.Combine(output, label + "-recovery"), dialogs);
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, $"{label}: the shell did not finish its initial load.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), "Opening direct-edit selection fixture…");
            var tab = GetActiveTab(owner);
            var session = tab.Session;
            var originalRevision = session.Revision;
            var originalDirty = session.IsDirty;
            var pageIndex = tab.CurrentPage;
            var initialData = await session.ExtractAsync(pageIndex);
            var target = FindClickableTarget(initialData, type, out var clickPoint, out var viewWidth, out var viewHeight);

            var ownedWindowCount = OwnedWindows(owner).Count();
            var emptyPoint = new Point(1, 1);
            var (emptyX, emptyY) = PageObjectHitTest.ToPdf(emptyPoint.X, emptyPoint.Y,
                viewWidth, viewHeight, viewWidth, viewHeight, initialData.GetProperty("Rotation").GetInt32());
            if (PageObjectHitTest.Find(initialData, emptyX, emptyY) is not null)
                throw new Exception("The empty-click fixture point is covered by an object.");
            await owner.RunUiAsync(() => InvokeDirectHitAsync(owner, tab, pageIndex, emptyPoint, viewWidth, viewHeight),
                "Checking empty direct-edit space…");
            if (OwnedWindows(owner).Count() != ownedWindowCount)
                throw new Exception($"{label}: empty page space opened an editor.");

            var oldObjectId = target.GetProperty("Id").GetString() ?? throw new Exception("A target object has no ID.");
            var observation = await OpenEditorThroughHitAsync(owner, tab, pageIndex, clickPoint, viewWidth, viewHeight, output: output);
            if (observation.ObjectId.Length == 0)
                throw new Exception($"{label}: the direct-edit dialog did not select an object.");
            if (observation.ObjectId == oldObjectId && signed)
                throw new Exception($"{label}: the signed fixture did not demonstrate the expected fresh post-consent object ID.");
            if (observation.SelectedObjectId != observation.ObjectId)
                throw new Exception($"{label}: the editor's selected object did not match a fresh extraction from the authorized session.");
            if (signed && !session.SignedCopyAuthorized)
                throw new Exception($"{label}: the injected signed-copy consent was not consumed.");
            if (session.Revision != originalRevision || session.IsDirty != originalDirty || RecoveryStore.Fingerprint(source) != sourceHash)
                throw new Exception($"{label}: opening and closing the editor changed session state or source bytes.");
            if (dialogs.Messages.Count != 0)
                throw new Exception($"{label}: the host reported an unexpected direct-edit failure: {string.Join("; ", dialogs.Messages)}");
        }
        finally
        {
            await CloseOwnerAsync(owner);
        }
    }

    static async Task ProveTextEditLifecycleAsync(string root, string output, string worker, string source)
    {
        var label = Path.GetFileNameWithoutExtension(source);
        var sourceHash = RecoveryStore.Fingerprint(source);
        var recoveryPath = Path.Combine(output, label + "-lifecycle-recovery");
        var store = new RecoveryStore(recoveryPath);
        var owner = NewOwner(worker, recoveryPath, new TestDialogs());
        var ownerClosed = false;
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "The shell did not finish its lifecycle load.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), "Opening text editor lifecycle fixture…");
            var tab = GetActiveTab(owner);
            var session = tab.Session;
            var sessionId = session.Id;
            var page = tab.CurrentPage;
            var data = await session.ExtractAsync(page);
            var target = FindClickableTarget(data, 1, out var click, out var width, out var height, requireEditable: true);
            var oldText = target.GetProperty("Text").GetString() ?? "";
            var targetIndex = target.GetProperty("Id").GetString()!.Split(':').Last();
            var newText = "Lifecycle proof " + Guid.NewGuid().ToString("N")[..8];
            var revision = session.Revision;
            var observation = await OpenEditorThroughHitAsync(owner, tab, page, click, width, height, newText, output);
            if (!observation.PreviewPopulated) throw new Exception($"{label}: initial candidate preview was not populated.");
            if (session.Revision != revision + 1 || !session.IsDirty) throw new Exception($"{label}: edit did not create one dirty revision.");
            async Task<bool> HasTargetText(DocumentSession s, string value)
            {
                var extracted = await s.ExtractAsync(page);
                return extracted.GetProperty("Objects").EnumerateArray().Any(o => o.GetProperty("Type").GetInt32() == 1 &&
                    o.GetProperty("Id").GetString()!.Split(':').Last() == targetIndex &&
                    o.TryGetProperty("Text", out var text) && text.GetString() == value);
            }
            if (!await HasTargetText(session, newText)) throw new Exception($"{label}: selected object did not contain the committed replacement.");
            await session.UndoAsync();
            if (!await HasTargetText(session, oldText)) throw new Exception($"{label}: Undo did not restore original text on the selected object.");
            await session.RedoAsync();
            if (!await HasTargetText(session, newText)) throw new Exception($"{label}: Redo did not restore replacement text on the selected object.");
            // CloseAllAsync intentionally removes checkpoints after an explicit discard. Dispose
            // here to release the lease while retaining the recovery record for this proof.
            session.Dispose();
            typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true);
            owner.Close();
            ownerClosed = true;
            using var recovered = await DocumentSession.RecoverAsync(worker, store, sessionId);
            if (!await HasTargetText(recovered, newText)) throw new Exception($"{label}: recovery did not retain replacement text on the selected object.");
            var saved = Path.Combine(output, label + "-lifecycle-saved.pdf");
            await recovered.SaveAsync(saved);
            using var reopened = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(output, label + "-lifecycle-reopen")), saved);
            if (!await HasTargetText(reopened, newText)) throw new Exception($"{label}: save/reopen did not retain replacement text on the selected object.");
            if (RecoveryStore.Fingerprint(source) != sourceHash) throw new Exception($"{label}: frozen source bytes changed.");
            File.WriteAllText(Path.Combine(output, label + "-lifecycle.json"), JsonSerializer.Serialize(new
            {
                SelectedObjectId = observation.ObjectId, PreviewPopulatedBeforeApply = observation.PreviewPopulated,
                OriginalText = oldText, ReplacementText = newText, UndoRedo = "passed", Recovery = "passed",
                SaveReopen = "passed", FrozenSourcePreserved = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (!ownerClosed) await CloseOwnerAsync(owner); }
    }

    static async Task<EditorObservation> OpenEditorThroughHitAsync(MainWindow owner, OpenDocumentTab tab, int page,
        Point click, double viewWidth, double viewHeight, string? replacementText = null, string? output = null)
    {
        var session = tab.Session;
        var observationTask = ObserveEditorDuringModalAsync(owner, session, page, click, viewWidth, viewHeight, replacementText, output);
        await owner.RunUiAsync(() => InvokeDirectHitAsync(owner, tab, page, click, viewWidth, viewHeight),
            "Opening the clicked PDF object…");
        return await observationTask;
    }

    static async Task<EditorObservation> ObserveEditorDuringModalAsync(MainWindow owner, DocumentSession session,
        int page, Point click, double viewWidth, double viewHeight, string? replacementText, string? output)
    {
        var completion = new TaskCompletionSource<EditorObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        var inspecting = false;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        timer.Tick += async (_, _) =>
        {
            if (inspecting || completion.Task.IsCompleted) return;
            var editor = OwnedWindows(owner).FirstOrDefault(window => window is ObjectEditorWindow or ImageEditorWindow);
            if (DateTime.UtcNow >= deadline)
            {
                completion.TrySetException(new TimeoutException("The direct-edit modal editor did not load its object list."));
                if (editor?.IsVisible == true) editor.DialogResult = false;
                timer.Stop();
                return;
            }
            if (editor is null) return;
            var working = editor.GetType().GetField("working", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor);
            if (working is not false) return;
            var list = editor switch
            {
                ObjectEditorWindow => FindByAutomation<ListBox>(editor, "Existing text objects on the current page"),
                ImageEditorWindow => FindByAutomation<ListBox>(editor, "Top-level images on the current page"),
                _ => null
            };
            if (list is null || list.Items.Count == 0 || list.SelectedItem is null) return;

            inspecting = true;
            try
            {
                var freshData = await session.ExtractAsync(page);
                var activeTab = GetActiveTab(owner);
                var (pdfX, pdfY) = PageObjectHitTest.ToPdf(click.X, click.Y,
                    activeTab.Pages[page].Width, activeTab.Pages[page].Height,
                    viewWidth, viewHeight, activeTab.Pages[page].Rotation);
                var hit = PageObjectHitTest.Find(freshData, pdfX, pdfY);
                if (hit is null) throw new Exception("A fresh extraction found no object at the direct-edit hit point.");
                var hitType = hit.Value.GetProperty("Type").GetInt32();
                if (hitType == 3 && editor is not ImageEditorWindow || hitType != 3 && editor is not ObjectEditorWindow)
                    throw new Exception("The direct-edit hit opened the wrong object editor.");
                var expectedId = hit.Value.GetProperty("Id").GetString() ?? "";
                var selectedObject = list.SelectedItem;
                var selectedId = selectedObject.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(selectedObject)?.ToString() ?? "";
                if (selectedId != expectedId)
                    throw new Exception($"The editor selected '{selectedId}', while fresh post-consent extraction selected '{expectedId}'.");
                var previewPopulated = false;
                Image? candidateImage = null;
                if (editor is ObjectEditorWindow textEditor)
                {
                    var canEdit = (bool)(list.SelectedItem.GetType().GetProperty("CanEdit")?.GetValue(list.SelectedItem) ?? false);
                    candidateImage = typeof(ObjectEditorWindow).GetField("candidateImage", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(textEditor) as Image;
                    if (canEdit && (candidateImage?.Source is null || candidateImage.Visibility != Visibility.Visible)) return;
                    previewPopulated = candidateImage?.Source is not null && candidateImage.Visibility == Visibility.Visible;
                }
                else if (editor is ImageEditorWindow imageEditor)
                {
                    var committedPage = typeof(ImageEditorWindow).GetField("pageImage", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(imageEditor) as Image;
                    if (committedPage?.Source is null || committedPage.Visibility != Visibility.Visible) return;
                    previewPopulated = true;
                }
                if (replacementText is not null && editor is ObjectEditorWindow applyEditor)
                {
                    var replacement = typeof(ObjectEditorWindow).GetField("replacement", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(applyEditor) as TextBox ?? throw new Exception("Replacement field was not found.");
                    // Force the real worker's layout-overflow refusal, then verify the dialog
                    // refreshes to the same object index before applying a valid replacement.
                    replacement.Text = new string('W', 1000);
                    var commit = FindByAutomation<Button>(applyEditor, "Replace selected text")
                        ?? throw new Exception("The replacement button was not found.");
                    commit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var refreshDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < refreshDeadline &&
                           applyEditor.GetType().GetField("working", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(applyEditor) is not false)
                        await Task.Delay(30);
                    if (applyEditor.GetType().GetField("working", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(applyEditor) is not false)
                        throw new TimeoutException("Rejected replacement did not finish refreshing the editor.");
                    var refreshedList = FindByAutomation<ListBox>(applyEditor, "Existing text objects on the current page")!;
                    if (refreshedList.SelectedItem is null || !SameTextObjectSignature(selectedObject, refreshedList.SelectedItem))
                    {
                        var diagnostic = new
                        {
                            Before = DescribeTextObject(selectedObject),
                            After = refreshedList.SelectedItem is null ? null : DescribeTextObject(refreshedList.SelectedItem),
                            Items = refreshedList.Items.Cast<object>().Select(DescribeTextObject).ToArray(),
                            Status = (typeof(ObjectEditorWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?.GetValue(applyEditor) as TextBlock)?.Text,
                            Working = applyEditor.GetType().GetField("working", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(applyEditor),
                            PreviewVisible = candidateImage?.Visibility == Visibility.Visible,
                            PreviewHasSource = candidateImage?.Source is not null,
                            Draft = replacement.Text
                        };
                        var diagnosticPath = Path.Combine(output ?? Path.GetTempPath(), "rejected-refresh-diagnostic.json");
                        File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true }));
                        throw new Exception($"Rejected replacement refresh did not restore the selected object signature. Diagnostic: {diagnosticPath}");
                    }
                    var refreshedStatus = typeof(ObjectEditorWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(applyEditor) as TextBlock;
                    if (replacement.Text.Length != 1000 || refreshedStatus is null ||
                        !refreshedStatus.Text.Contains("extends beyond the page", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Rejected overflow did not preserve its draft and expose the worker's layout refusal.");
                    replacement.Text = replacementText;
                    var previewDeadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < previewDeadline && (candidateImage?.Source is null || candidateImage.Visibility != Visibility.Visible))
                        await Task.Delay(30);
                    if (candidateImage?.Source is null || candidateImage.Visibility != Visibility.Visible)
                        throw new TimeoutException("Candidate preview did not refresh after replacement text changed.");
                    commit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var applyDeadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < applyDeadline && applyEditor.IsVisible)
                        await Task.Delay(30);
                    if (applyEditor.IsVisible)
                        throw new InvalidOperationException("The valid replacement did not close the editor; the edit may have been rejected.");
                    completion.TrySetResult(new EditorObservation(expectedId, selectedId, previewPopulated));
                }
                else
                {
                    completion.TrySetResult(new EditorObservation(expectedId, selectedId, previewPopulated));
                    editor.DialogResult = false;
                }
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                if (editor.IsVisible) editor.DialogResult = false;
            }
            finally { inspecting = false; }
        };
        timer.Start();
        try { return await completion.Task; }
        finally { timer.Stop(); }
    }

    static async Task InvokeDirectHitAsync(MainWindow owner, OpenDocumentTab tab, int page, Point position,
        double viewWidth, double viewHeight)
    {
        var method = typeof(MainWindow).GetMethod("EditObjectAtAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "EditObjectAtAsync");
        var task = method.Invoke(owner, new object[] { tab, page, position, viewWidth, viewHeight }) as Task
            ?? throw new InvalidOperationException("The private direct-edit entry point did not return a Task.");
        await task;
    }

    static JsonElement FindClickableTarget(JsonElement data, int type, out Point click, out double viewWidth, out double viewHeight,
        bool requireEditable = false)
    {
        var width = data.GetProperty("Width").GetDouble();
        var height = data.GetProperty("Height").GetDouble();
        var rotation = data.GetProperty("Rotation").GetInt32();
        viewWidth = width;
        viewHeight = height;
        foreach (var item in data.GetProperty("Objects").EnumerateArray().Where(item =>
                     item.GetProperty("Type").GetInt32() == type &&
                     (!requireEditable || (item.TryGetProperty("CanEdit", out var editable) && editable.GetBoolean())) &&
                     item.TryGetProperty("HasBounds", out var bounded) && bounded.GetBoolean()).Reverse())
        {
            var x = (item.GetProperty("Left").GetDouble() + item.GetProperty("Right").GetDouble()) / 2;
            var y = (item.GetProperty("Bottom").GetDouble() + item.GetProperty("Top").GetDouble()) / 2;
            var view = ToView(x, y, width, height, rotation);
            var (pdfX, pdfY) = PageObjectHitTest.ToPdf(view.X, view.Y, width, height, viewWidth, viewHeight, rotation);
            var hit = PageObjectHitTest.Find(data, pdfX, pdfY);
            if (hit is JsonElement resolved && resolved.GetProperty("Id").GetString() == item.GetProperty("Id").GetString())
            {
                click = view;
                return item.Clone();
            }
        }
        throw new Exception($"No bounded type-{type} object could be clicked without another object covering its center.");
    }

    static Point ToView(double x, double y, double width, double height, int rotation) => rotation switch
    {
        1 => new Point(y, x),
        2 => new Point(width - x, y),
        3 => new Point(width - y, height - x),
        _ => new Point(x, height - y)
    };

    static IEnumerable<Window> OwnedWindows(Window owner) => Application.Current.Windows.OfType<Window>()
        .Where(window => ReferenceEquals(window.Owner, owner));

    static OpenDocumentTab GetActiveTab(MainWindow owner) =>
        typeof(MainWindow).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as OpenDocumentTab
        ?? throw new Exception("The MainWindow active document field is empty.");

    static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new TimeoutException(message);
    }

    static T Find<T>(Window window, string automationName) where T : FrameworkElement =>
        Descendants<T>(window).Single(control => AutomationProperties.GetName(control) == automationName);

    static T? FindByAutomation<T>(Window window, string automationName) where T : FrameworkElement =>
        Descendants<T>(window).FirstOrDefault(control => AutomationProperties.GetName(control) == automationName);

    static bool SameTextObjectSignature(object expected, object actual)
    {
        object? Read(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
        if (!Equals(Read(expected,"Text"),Read(actual,"Text")) || !Equals(Read(expected,"FontName"),Read(actual,"FontName")) ||
            !Equals(Read(expected,"FontSize"),Read(actual,"FontSize")) || !Equals(Read(expected,"HasBounds"),Read(actual,"HasBounds"))) return false;
        if (!Equals(Read(expected,"HasBounds"),true)) return true;
        return new[] { "Left", "Bottom", "Right", "Top" }.All(name =>
            Math.Abs(Convert.ToDouble(Read(expected,name),System.Globalization.CultureInfo.InvariantCulture) -
                     Convert.ToDouble(Read(actual,name),System.Globalization.CultureInfo.InvariantCulture)) < 0.01);
    }

    static object DescribeTextObject(object value)
    {
        object? Read(string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
        return new { Id=Read("Id"), Text=Read("Text"), FontName=Read("FontName"), FontSize=Read("FontSize"),
            HasBounds=Read("HasBounds"), Left=Read("Left"), Bottom=Read("Bottom"), Right=Read("Right"), Top=Read("Top"),
            CanEdit=Read("CanEdit"), Reason=Read("Reason") };
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
        if (parent is Window window && window.Content is DependencyObject content && VisualTreeHelper.GetChildrenCount(parent) == 0)
        {
            if (content is T match) yield return match;
            foreach (var nested in Descendants<T>(content)) yield return nested;
        }
    }

    static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static async Task CloseOwnerAsync(MainWindow owner)
    {
        if (!owner.IsVisible) { owner.Close(); return; }
        var accepted = false;
        try
        {
            await owner.RunUiAsync(async () => { accepted = await owner.CloseAllAsync(); }, "Closing direct-edit proof window…");
        }
        catch { }
        if (accepted)
            typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(owner, true);
        owner.Close();
    }

    readonly record struct EditorObservation(string ObjectId, string SelectedObjectId, bool PreviewPopulated = false);
}
