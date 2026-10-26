using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var exit = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--print-only"))
                {
                    await PrintCancellationProof.RunAsync(Path.GetFullPath(args[0]));
                    await PrintOutputProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--drawing-only"))
                {
                    await DrawingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Length >= 3 && args[1] == "--probe-text")
                {
                    await PageToolsUiProof.ProbeAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[2]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--rotated-pages-only"))
                {
                    await RotatedPagesUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--page-tools-only"))
                {
                    await PageToolsUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--direct-edit-only"))
                {
                    await DirectEditingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--continuation-only"))
                {
                    await PageObjectHitTestProof.RunAsync(Path.GetFullPath(args[0]));
                    await OffsetPageCoordinatesUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DirectEditingSafetyUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DirectEditingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DrawingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await PdfOpenErrorUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--structured-text-only"))
                {
                    await StructuredTextUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--annotations-only"))
                {
                    await AnnotationManagementUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--advanced-dialogs-only"))
                {
                    await AdvancedDialogsProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--rich-text-only"))
                {
                    await RichTextUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--quick-tools-ui-only"))
                {
                    await QuickToolsUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--quick-tools-ui-only"))
                {
                    await QuickToolsUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--fill-sign-only"))
                {
                    await FillSignUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--compare-only"))
                {
                    await CompareUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--workspace-only"))
                {
                    await WorkspaceUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--viewer-only"))
                {
                    await ViewerUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--signature-only"))
                {
                    await SignatureProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (args.Contains("--form-fill-only"))
                {
                    await FormFillUiProof.RunAsync(Path.GetFullPath(args[0]));
                    exit = 0;
                    return;
                }
                if (!args.Contains("--workflow-only")) {
                    if (!args.Contains("--shell-only")) await RunAsync(Path.GetFullPath(args[0]));
                    await ShellProof.RunAsync(Path.GetFullPath(args[0]));
                }
                await WorkflowProof.RunAsync(Path.GetFullPath(args[0]));
                await ImageDecoderProof.RunAsync(Path.GetFullPath(args[0]));
                await PrintCancellationProof.RunAsync(Path.GetFullPath(args[0]));
                await PrintOutputProof.RunAsync(Path.GetFullPath(args[0]));
                if (!args.Contains("--workflow-only") && !args.Contains("--shell-only"))
                {
                    await PageObjectHitTestProof.RunAsync(Path.GetFullPath(args[0]));
                    await OffsetPageCoordinatesUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await RotatedPagesUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DirectEditingSafetyUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await FormFillUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await SignatureProof.RunAsync(Path.GetFullPath(args[0]));
                    await ViewerUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await WorkspaceUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await CompareUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await FillSignUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await RichTextUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await AdvancedDialogsProof.RunAsync(Path.GetFullPath(args[0]));
                    await QuickToolsUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await AnnotationManagementUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await StructuredTextUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DirectEditingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await DrawingUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await PageToolsUiProof.RunAsync(Path.GetFullPath(args[0]));
                    await PdfOpenErrorUiProof.RunAsync(Path.GetFullPath(args[0]));
                }
                exit = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var originalHash = RecoveryStore.Fingerprint(source);
        var store = new RecoveryStore(Path.Combine(output, "recovery"));
        var dialogs = new TestDialogs { NextOpen = source };
        var window = new MainWindow(worker, store, dialogs);
        async Task Idle(MainWindow w)
        {
            var watch = Stopwatch.StartNew();
            while (w.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            Check(!w.IsBusy, "UI operation finishes without blocking dispatcher");
        }
        async Task Command(RoutedCommand command)
        {
            command.Execute(null, window);
            await Idle(window);
        }
        async Task Click(MainWindow w, string button)
        {
            ((Button)w.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(w);
        }
        async Task RenderViewport(MainWindow w)
        {
            if (!w.IsVisible)
            {
                w.ShowActivated = false; w.ShowInTaskbar = false;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -10000; w.Top = -10000; w.Show();
                await Idle(w);
            }
            var content = (FrameworkElement)w.Content;
            content.Measure(new Size(1280, 860));
            content.Arrange(new Rect(0, 0, 1280, 860));
            w.UpdateLayout();
            var render = (Task)typeof(MainWindow).GetMethod("RenderViewportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(w, null)!;
            await render;
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Check(window.InputBindings.Count >= 8, "Open/save/save-as/undo/redo/close/tab keyboard bindings are present");
        Check(!ApplicationCommands.Undo.CanExecute(null, window), "Undo disabled before a document is open");
        await Command(ApplicationCommands.Open);
        Check(window.CurrentSession is not null && ((Image)window.FindName("PageImage")).Source is not null, "Routed Open loads actual worker-rendered page");
        ((TextBox)window.FindName("NewText")).Text = "DESKTOP SAVE AND RECOVERY";
        await Click(window, "AddButton");
        Check(window.CurrentSession!.IsDirty && window.Title.StartsWith("* "), "Add text button marks document dirty in title");
        var id = window.CurrentSession.Id;
        dialogs.Unsaved = MessageBoxResult.Cancel;
        await Command(ApplicationCommands.Close);
        Check(window.CurrentSession!.Id == id && window.CurrentSession.IsDirty, "Cancel at unsaved prompt keeps the document and its edits");
        dialogs.Unsaved = MessageBoxResult.Yes;
        dialogs.NextSave = null;
        await Command(ApplicationCommands.Close);
        Check(window.CurrentSession!.Id == id && window.CurrentSession.IsDirty, "Cancel in Save As prevents the pending document close");

        await Command(ApplicationCommands.Undo);
        Check(!window.CurrentSession.IsDirty && ApplicationCommands.Redo.CanExecute(null, window), "Routed Undo restores clean state and enables Redo");
        await Command(ApplicationCommands.Redo);
        Check(window.CurrentSession.IsDirty, "Routed Redo restores dirty edited state");
        var saved = Path.Combine(output, "desktop-saved.pdf");
        dialogs.NextSave = saved;
        await Command(ApplicationCommands.Save);
        Check(File.Exists(saved) && !window.CurrentSession.IsDirty && RecoveryStore.Fingerprint(source) == originalHash,
            "First routed Save uses Save As and preserves original PDF");
        dialogs.Overwrite = false;
        await Click(window, "RotateButton");
        Check(window.CurrentSession.IsDirty, "Rotate button creates undoable structural checkpoint");
        await Command(ApplicationCommands.Save);
        Check(window.CurrentSession.IsDirty, "Declining overwrite leaves edits unsaved");
        dialogs.Overwrite = true;
        await Command(ApplicationCommands.Save);
        Check(!window.CurrentSession.IsDirty && Directory.GetFiles(output, "*.backup.pdf").Length == 1, "Confirmed overwrite saves and retains a backup");
        await Click(window, "AddButton");
        File.AppendAllText(saved, "\n% external UI test\n");
        var externallyChanged = RecoveryStore.Fingerprint(saved);
        var errorsBefore = dialogs.Messages.Count;
        await Command(ApplicationCommands.Save);
        Check(window.CurrentSession.IsDirty && dialogs.Messages.Count == errorsBefore + 1 &&
            RecoveryStore.Fingerprint(saved) == externallyChanged, "Save conflict displays error while preserving external PDF and unsaved changes");

        using (var process = Process.GetProcessById(window.CurrentSession.WorkerProcessId!.Value))
        {
            process.Kill(); process.WaitForExit();
        }
        await window.RunUiAsync(async () => { await window.CurrentSession.ExtractAsync(0); });
        Check(window.CurrentSession.IsDirty, "Worker failure leaves UI document and dirty state intact");
        await window.RunUiAsync(async () => { await window.CurrentSession.ExtractAsync(0); });
        Check(((Image)window.FindName("PageImage")).Source is not null, "Subsequent UI operation restarts worker from durable edits");
        var panel = (FrameworkElement)window.Content;
        panel.Measure(new Size(1050, 760));
        panel.Arrange(new Rect(0, 0, 1050, 760));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1050, 760, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "desktop-checkpoint.png"))) encoder.Save(stream);
        Check(File.Exists(Path.Combine(output, "desktop-checkpoint.png")), "Actual WPF control tree renders to a reviewable PNG");

        window.CurrentSession.Dispose(); // Model an abandoned desktop session without cleanup.
        var recoveredDialogs = new TestDialogs { Recover = MessageBoxResult.No };
        var recoveredWindow = new MainWindow(worker, store, recoveredDialogs);
        await recoveredWindow.RunUiAsync(recoveredWindow.OfferRecoveryAsync);
        Check(recoveredWindow.CurrentSession is null && store.Scan().Entries.Any(e => e.Id == id),
            "Declining recovery retains files for a later decision");
        recoveredDialogs.Recover = MessageBoxResult.Yes;
        await recoveredWindow.RunUiAsync(recoveredWindow.OfferRecoveryAsync);
        Check(recoveredWindow.CurrentSession is { IsDirty: true } &&
            (await recoveredWindow.CurrentSession.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("DESKTOP SAVE AND RECOVERY"),
            "Recovery choice restores actual unsaved document into desktop UI");
        recoveredDialogs.Unsaved = MessageBoxResult.Cancel;
        Check(!await recoveredWindow.LeaveCurrentAsync(), "Cancel protects recovered unsaved changes on close");
        recoveredDialogs.Unsaved = MessageBoxResult.No;
        Check(await recoveredWindow.LeaveCurrentAsync() && !Directory.Exists(store.DirectoryFor(id)),
            "Explicit discard closes recovered session and removes its checkpoints");

        dialogs.Unsaved = MessageBoxResult.No;
        await window.LeaveCurrentAsync();
        dialogs.NextOpen = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        await Command(ApplicationCommands.Open);
        await RenderViewport(window);
        var signedStatus = ((TextBlock)window.FindName("Status")).Text;
        Check(signedStatus.Contains("validity not verified"), "Signed PDF status makes no validity claim: " + signedStatus +
            $"; signatureCount={window.CurrentSession?.SignatureCount}; path={window.CurrentSession?.OriginalPath}");
        ((TextBox)window.FindName("NewText")).Text = "SIGNED COPY DESKTOP";
        dialogs.SignedConsent = false;
        await Click(window, "AddButton");
        Check(!window.CurrentSession!.IsDirty, "Declining signed-copy consent prevents editing");
        dialogs.SignedConsent = true;
        await Click(window, "AddButton");
        await RenderViewport(window);
        Check(window.CurrentSession.SignaturesInvalidated &&
            ((TextBlock)window.FindName("Status")).Text.Contains("invalidated"), "Explicit signed-copy editing labels signatures invalidated");
        dialogs.NextSave = Path.Combine(output, "desktop-signed-copy.pdf");
        await Command(ApplicationCommands.SaveAs);
        Check(!window.CurrentSession.IsDirty, "Signed edited copy can be saved to a separate PDF");
        await window.LeaveCurrentAsync();

        var report = new { Passed = checks, Scope = "Real WPF controls and worker operations; native dialogs use injected choices. No visible windows or desktop automation." };
        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} desktop state/command checks. Evidence: {output}");
    }
}

sealed class TestDialogs : IDocumentDialogs
{
    public string? NextOpen { get; set; }
    public string? NextSave { get; set; }
    public MessageBoxResult Unsaved { get; set; } = MessageBoxResult.No;
    public MessageBoxResult Recover { get; set; } = MessageBoxResult.Yes;
    public bool Overwrite { get; set; } = true;
    public bool SignedConsent { get; set; }
    public List<string> Messages { get; } = new();
    public Queue<MessageBoxResult> UnsavedResponses { get; } = new();
    public List<Guid> CloseQuestions { get; } = new();
    public List<Guid> RecoveryQuestions { get; } = new();
    public string? OpenPath() => NextOpen;
    public string? SavePath(DocumentSession session) => NextSave;
    public MessageBoxResult UnsavedChoice(DocumentSession session)
    {
        CloseQuestions.Add(session.Id);
        return UnsavedResponses.TryDequeue(out var choice) ? choice : Unsaved;
    }
    public bool ConfirmOverwrite() => Overwrite;
    public bool ConfirmSignedCopy() => SignedConsent;
    public MessageBoxResult RecoveryChoice(RecoveryEntry entry) { RecoveryQuestions.Add(entry.Id); return Recover; }
    public string? Password(string message) => null;
    public void Information(string message, string title) => Messages.Add(title + ": " + message);
    public bool ConfirmResult { get; set; } = true;
    public List<string> Confirmations { get; } = new();
    public bool Confirm(string message, string title) { Confirmations.Add(title); return ConfirmResult; }
}
