using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Proves the direct-edit route aborts when its target becomes stale or extraction fails.</summary>
static class DirectEditingSafetyUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-direct-edit-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var otherSource = Path.Combine(root, "fixtures", "corpus-v1", "engine", "objects-original.pdf");
        var checks = new List<string>();

        foreach (var checkpoint in new[] { "after-first-extraction", "after-second-extraction", "before-editor-open" })
        {
            await RunAbortCaseAsync(root, output, worker, source, otherSource, checkpoint, "revision", checks);
            await RunAbortCaseAsync(root, output, worker, source, otherSource, checkpoint, "page", checks);
            await RunAbortCaseAsync(root, output, worker, source, otherSource, checkpoint, "tab", checks);
            await RunAbortCaseAsync(root, output, worker,
                Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf"), otherSource,
                checkpoint, "navigation", checks);
        }
        var signedSource = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        foreach (var mutation in new[] { "revision", "page", "tab" })
            await RunAbortCaseAsync(root, output, worker, signedSource, otherSource,
                "after-authorization", mutation, checks, signed: true);
        await RunWorkerFailureCaseAsync(root, output, worker, source, checks);

        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
        {
            Passed = checks,
            SourcePreservation = "The original source hash remained unchanged in every stale-target and worker-failure case.",
            ModalEditorsOpened = 0
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS direct-edit stale-target and worker-failure proof. Evidence: {output}");
    }

    static async Task RunAbortCaseAsync(string root, string output, string worker, string source, string otherSource,
        string checkpoint, string mutation, List<string> checks, bool signed = false)
    {
        var label = $"{checkpoint}-{mutation}";
        var sourceHash = RecoveryStore.Fingerprint(source);
        var dialogs = new TestDialogs { SignedConsent = signed };
        var owner = NewOwner(worker, Path.Combine(output, label + "-recovery"), dialogs);
        var sourceTab = default(OpenDocumentTab)!;
        var otherTab = default(OpenDocumentTab)!;
        var changedDescriptor = default(PageDescriptor);
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, $"{label}: initial shell load timed out.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), $"Opening {label} fixture…");
            sourceTab = owner.ActiveTab ?? throw new Exception("The source tab did not open.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(otherSource), "Opening second tab for stale-target proof…");
            otherTab = owner.ActiveTab ?? throw new Exception("The second tab did not open.");
            await owner.SelectTabAsync(sourceTab);

            var session = sourceTab.Session;
            var revision = session.Revision;
            var dirty = session.IsDirty;
            var pageIndex = sourceTab.CurrentPage;
            var initial = await session.ExtractAsync(pageIndex);
            var (click, viewWidth, viewHeight) = FindClickableTarget(initial);
            var editorCount = OwnedEditors(owner).Count();
            var injected = false;
            owner.DirectEditCheckpointForTest = async actual =>
            {
                if (actual != checkpoint || injected) return;
                injected = true;
                switch (mutation)
                {
                    case "revision":
                        await session.AddTextAsync(pageIndex, "x", 12, 12);
                        await session.UndoAsync();
                        break;
                    case "page":
                        var old = sourceTab.Pages[pageIndex];
                        changedDescriptor = new PageDescriptor(old.Number, old.Width, old.Height, old.Rotation);
                        sourceTab.Pages[pageIndex] = changedDescriptor;
                        break;
                    case "tab":
                        await owner.SelectTabAsync(otherTab);
                        break;
                    case "navigation":
                        if (sourceTab.Pages.Count < 2) throw new Exception("Navigation proof requires a multipage fixture.");
                        sourceTab.CurrentPage = (pageIndex + 1) % sourceTab.Pages.Count;
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(mutation));
                }
            };

            await owner.RunUiAsync(() => InvokeDirectHitAsync(owner, sourceTab, pageIndex, click, viewWidth, viewHeight),
                $"Exercising {label} guard…");

            if (!injected) throw new Exception($"{label}: the expected route checkpoint was not reached.");
            if (OwnedEditors(owner).Count() != editorCount) throw new Exception($"{label}: stale target opened an editor.");
            if (RecoveryStore.Fingerprint(source) != sourceHash) throw new Exception($"{label}: source bytes changed.");
            if (mutation == "revision" && (session.Revision == revision || session.IsDirty != dirty))
                throw new Exception($"{label}: the test mutation did not advance revision and restore clean content.");
            if (mutation == "page" && !ReferenceEquals(sourceTab.Pages[pageIndex], changedDescriptor))
                throw new Exception($"{label}: the replacement page descriptor was unexpectedly restored.");
            if (mutation == "tab" && owner.ActiveTab != otherTab)
                throw new Exception($"{label}: the injected tab switch did not take effect.");
            if (mutation == "navigation" && sourceTab.CurrentPage == pageIndex)
                throw new Exception($"{label}: the route restored a stale current page.");
            if (dialogs.Messages.Count != 0) throw new Exception($"{label}: unexpected route error: {string.Join("; ", dialogs.Messages)}");
            checks.Add($"{checkpoint}: stale {mutation} state aborts without opening an editor or changing the source.");
        }
        finally
        {
            owner.DirectEditCheckpointForTest = null;
            if (changedDescriptor is not null && sourceTab is not null)
            {
                var index = sourceTab.Pages.FindIndex(page => ReferenceEquals(page, changedDescriptor));
                if (index >= 0)
                {
                    var old = changedDescriptor;
                    sourceTab.Pages[index] = new PageDescriptor(old.Number, old.Width, old.Height, old.Rotation);
                }
            }
            await CloseOwnerAsync(owner);
        }
    }

    static async Task RunWorkerFailureCaseAsync(string root, string output, string worker, string source, List<string> checks)
    {
        var label = "worker-failure-after-first-extraction";
        var sourceHash = RecoveryStore.Fingerprint(source);
        var dialogs = new TestDialogs();
        var owner = NewOwner(worker, Path.Combine(output, label + "-recovery"), dialogs);
        try
        {
            owner.Show();
            await WaitUntilAsync(() => !owner.IsBusy, "Worker-failure fixture shell load timed out.");
            await owner.RunUiAsync(() => owner.OpenPathAsync(source), "Opening worker-failure fixture…");
            var tab = owner.ActiveTab ?? throw new Exception("The source tab did not open.");
            var initial = await tab.Session.ExtractAsync(tab.CurrentPage);
            var (click, viewWidth, viewHeight) = FindClickableTarget(initial);
            var editorCount = OwnedEditors(owner).Count();
            var failed = false;
            owner.DirectEditCheckpointForTest = checkpoint =>
            {
                if (checkpoint == "after-first-extraction" && !failed)
                {
                    failed = true;
                    tab.Session.Dispose();
                }
                return Task.CompletedTask;
            };

            await owner.RunUiAsync(() => InvokeDirectHitAsync(owner, tab, tab.CurrentPage, click, viewWidth, viewHeight),
                "Exercising direct-edit worker failure…");

            if (!failed) throw new Exception("The direct-edit route did not reach the worker-failure injection point.");
            if (OwnedEditors(owner).Count() != editorCount) throw new Exception("A worker failure opened an object editor.");
            if (RecoveryStore.Fingerprint(source) != sourceHash) throw new Exception("A worker failure changed source bytes.");
            if (dialogs.Messages.Count != 1) throw new Exception("A worker failure was not reported once through the UI error path.");
            checks.Add("Worker failure during direct selection is reported and opens no editor or source mutation.");
        }
        finally
        {
            owner.DirectEditCheckpointForTest = null;
            await CloseOwnerAsync(owner);
        }
    }

    static (Point Click, double Width, double Height) FindClickableTarget(JsonElement data)
    {
        var width = data.GetProperty("Width").GetDouble();
        var height = data.GetProperty("Height").GetDouble();
        var rotation = data.GetProperty("Rotation").GetInt32();
        foreach (var item in data.GetProperty("Objects").EnumerateArray().Reverse())
        {
            if (item.GetProperty("Type").GetInt32() is not (1 or 3) ||
                !item.TryGetProperty("HasBounds", out var bounded) || !bounded.GetBoolean()) continue;
            var x = (item.GetProperty("Left").GetDouble() + item.GetProperty("Right").GetDouble()) / 2;
            var y = (item.GetProperty("Bottom").GetDouble() + item.GetProperty("Top").GetDouble()) / 2;
            var click = rotation switch
            {
                1 => new Point(y, x),
                2 => new Point(width - x, y),
                3 => new Point(width - y, height - x),
                _ => new Point(x, height - y)
            };
            var (pdfX, pdfY) = PageObjectHitTest.ToPdf(click.X, click.Y, width, height, width, height, rotation);
            var hit = PageObjectHitTest.Find(data, pdfX, pdfY);
            if (hit is JsonElement result && result.GetProperty("Id").GetString() == item.GetProperty("Id").GetString())
                return (click, width, height);
        }
        throw new Exception("The direct-edit safety fixture has no unobscured bounded text or image.");
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

    static async Task InvokeDirectHitAsync(MainWindow owner, OpenDocumentTab tab, int page, Point click,
        double viewWidth, double viewHeight)
    {
        var method = typeof(MainWindow).GetMethod("EditObjectAtAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "EditObjectAtAsync");
        var task = method.Invoke(owner, new object[] { tab, page, click, viewWidth, viewHeight }) as Task
            ?? throw new InvalidOperationException("Direct-edit route did not return a task.");
        await task;
    }

    static IEnumerable<Window> OwnedEditors(Window owner) => Application.Current.Windows.OfType<Window>()
        .Where(window => ReferenceEquals(window.Owner, owner) && window is ObjectEditorWindow or ImageEditorWindow);

    static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new TimeoutException(message);
    }

    static async Task CloseOwnerAsync(MainWindow owner)
    {
        if (owner.IsVisible)
        {
            var accepted = false;
            try { await owner.RunUiAsync(async () => { accepted = await owner.CloseAllAsync(); }, "Closing safety proof…"); }
            catch { }
            if (accepted) typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(owner, true);
        }
        owner.Close();
    }
}
