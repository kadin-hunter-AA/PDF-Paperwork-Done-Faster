using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Compare PDFs: identical copy, text and appearance differences, differing page counts, source preservation and temporary-folder cleanup.</summary>
static class CompareUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "layout-columns-table.pdf");
        var fixtureHash = RecoveryStore.Fingerprint(fixture);
        var original = Path.Combine(output, "original.pdf"); File.Copy(fixture, original);
        var same = Path.Combine(output, "identical-copy.pdf"); File.Copy(fixture, same);
        var editSource = Path.Combine(output, "edit-source.pdf"); File.Copy(fixture, editSource);
        var modified = Path.Combine(output, "modified.pdf");
        var twoPage = Path.Combine(output, "two-page.pdf");
        var recoveryRoot = Path.Combine(output, "recovery");
        var compareRoot = Path.Combine(output, "compare-temp");
        Directory.CreateDirectory(compareRoot);

        async Task Derive(string source, string destination, Func<DocumentSession, Task> edit)
        {
            using var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(recoveryRoot, "derive-" + Guid.NewGuid().ToString("N"))), source);
            await edit(session);
            await session.SaveAsync(destination);
            await session.CloseAsync(discardUnsaved: true);
        }
        await Derive(editSource, modified, s => s.AddTextAsync(0, "Compare proof added line", 72, 500));
        await Derive(editSource, twoPage, s => s.InsertBlankPageAsync(1, 612, 792));
        var hashes = new[] { original, same, modified, twoPage }.Select(RecoveryStore.Fingerprint).ToArray();

        DocumentSession? sessionA = null, sessionTwo = null;
        CompareWindow? window = null;
        CompareWindow Open(DocumentSession session)
        {
            var created = new CompareWindow(session, worker, compareRoot) { Width = 1240, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            ShellThemeResources.Apply(created.Resources, ShellTheme.Light, false);
            created.Show();
            return created;
        }
        async Task CloseAndCheck(CompareWindow w, string message)
        {
            var folder = w.TemporaryFolder;
            w.Close();
            await w.CleanupTask;
            Check(folder is not null && !Directory.Exists(folder) && !Directory.EnumerateDirectories(compareRoot).Any(), message);
        }
        try
        {
            sessionA = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(recoveryRoot, "a")), original);
            Check(sessionA.PageCount >= 1, $"The current document opens ({sessionA.PageCount} page)");

            // ---- Identical copy ----
            window = Open(sessionA);
            Check(window.Results.Count == 0 && window.SummaryText.Contains("Choose a PDF", StringComparison.Ordinal), "A new Compare window waits for a second file");
            window.PickPath = () => same;
            await window.CompareAsync(same);
            Check(window.Results.Count == sessionA.PageCount && window.Results.All(r => r.IsIdentical && r.Status == "Identical"), "An identical copy reports every page as Identical");
            Check(window.SummaryText == $"{sessionA.PageCount} page compared: {sessionA.PageCount} identical, 0 different" ||
                  window.SummaryText == $"{sessionA.PageCount} pages compared: {sessionA.PageCount} identical, 0 different", "The summary line counts identical pages: " + window.SummaryText);

            // ---- Added text ----
            await window.CompareAsync(modified);
            var first = window.Results[0];
            Check(first.TextAdded >= 1 && first.TextRemoved == 0 && first.Status.Contains("Text differs", StringComparison.Ordinal), "Added text is reported as added lines: " + first.Status);
            Check(first.PixelDifferencePercent > 0 && first.Status.Contains("Appearance differs", StringComparison.Ordinal), $"Added text changes the rendered appearance ({first.PixelDifferencePercent:0.0}%)");
            Check(first.DiffLines?.Any(l => l.StartsWith("+ ", StringComparison.Ordinal) && l.Contains("Compare proof added line", StringComparison.Ordinal)) == true, "The text diff lists the added line with a + prefix");
            Check(window.SummaryText.EndsWith("0 identical, 1 different", StringComparison.Ordinal), "The summary line counts the different page: " + window.SummaryText);
            await window.SelectPageAsync(0);
            await Task.Delay(100);
            Capture(window, Path.Combine(output, "compare-differing-page.png"));
            await CloseAndCheck(window, "Closing the window removes the other document's temporary recovery folder");
            window = null;

            // ---- Different page counts ----
            window = Open(sessionA);
            await window.CompareAsync(twoPage);
            Check(window.Results.Count == 2 && window.Results[1].OnlyIn == "two-page.pdf" && window.Results[1].Status == "Only in two-page.pdf", "A page that only the other file has is reported as \"Only in <file>\"");
            Check(window.SummaryText.EndsWith("1 identical, 1 different", StringComparison.Ordinal), "Pages missing from one side count as different: " + window.SummaryText);
            await window.SelectPageAsync(1);
            await CloseAndCheck(window, "Temporary folder removed after the page-count comparison");
            window = null;

            sessionTwo = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(recoveryRoot, "two")), twoPage);
            window = Open(sessionTwo);
            await window.CompareAsync(original);
            Check(window.Results.Count == 2 && window.Results[1].OnlyIn == "this document" && window.Results[1].Status == "Only in this document", "A page that only this document has is reported as \"Only in this document\"");
            await CloseAndCheck(window, "Temporary folder removed after the reverse comparison");
            window = null;

            // ---- Unreadable file ----
            window = Open(sessionA);
            var malformed = Path.Combine(output, "malformed.pdf"); File.Copy(Path.Combine(root, "fixtures", "corpus-v1", "corpus", "malformed.pdf"), malformed);
            await window.CompareAsync(malformed);
            Check(window.Results.Count == 0 && window.SummaryText.Contains("malformed.pdf", StringComparison.Ordinal), "An unreadable file is refused with a plain message: " + window.SummaryText);
            await window.CompareAsync(same);
            Check(window.Results.Count == sessionA.PageCount, "Comparing works again after a refused file");
            window.Close(); await window.CleanupTask;
            window = null;

            // ---- Source preservation ----
            Check(!sessionA.IsDirty && !sessionTwo.IsDirty, "Comparing leaves both open documents unmodified");
            var after = new[] { original, same, modified, twoPage }.Select(RecoveryStore.Fingerprint).ToArray();
            Check(hashes.SequenceEqual(after), "The compared files are byte-for-byte unchanged");
            Check(RecoveryStore.Fingerprint(fixture) == fixtureHash, "Frozen fixture unchanged: " + Path.GetFileName(fixture));
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            Console.WriteLine($"COMPARE UI PROOF PASS {checks.Count} {output}");
        }
        finally
        {
            if (window is not null) { try { window.Close(); await window.CleanupTask; } catch { } }
            if (sessionA is not null) { try { await sessionA.CloseAsync(discardUnsaved: true); } catch { } sessionA.Dispose(); }
            if (sessionTwo is not null) { try { await sessionTwo.CloseAsync(discardUnsaved: true); } catch { } sessionTwo.Dispose(); }
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
