using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class OffsetPageCoordinatesUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-offset-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var source = Path.Combine(output, "offset-crop.pdf");
        CreateSource(source);
        var hash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker/bin/Debug/net10.0/App.Worker.exe");
        var dialogs = new TestDialogs();
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false
        };
        var opened = false;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        watchdog.Tick += (_, _) =>
        {
            foreach (var editor in Application.Current.Windows.OfType<Window>().Where(w => w.Owner == owner && w is ObjectEditorWindow or ImageEditorWindow).ToArray())
            { opened = true; editor.Close(); }
        };
        try
        {
            owner.Show();
            await WaitAsync(() => !owner.IsBusy);
            await owner.RunUiAsync(() => owner.OpenPathAsync(source));
            var tab = owner.ActiveTab ?? throw new Exception("Offset fixture failed to open: " + string.Join("; ", dialogs.Messages));
            var data = await tab.Session.ExtractAsync(0);
            if (data.GetProperty("SupportsZeroOriginCoordinates").GetBoolean()) throw new Exception("Offset page incorrectly reported a zero origin.");
            var target = data.GetProperty("Objects").EnumerateArray().First(item => item.GetProperty("Type").GetInt32() == 1);
            var x = (target.GetProperty("Left").GetDouble() + target.GetProperty("Right").GetDouble()) / 2;
            var y = (target.GetProperty("Bottom").GetDouble() + target.GetProperty("Top").GetDouble()) / 2;
            var width = data.GetProperty("Width").GetDouble(); var height = data.GetProperty("Height").GetDouble();
            watchdog.Start();
            var method = typeof(MainWindow).GetMethod("EditObjectAtAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // These coordinates would select the text under a zero-origin assumption; on an offset page they miss it and must open nothing.
            await owner.RunUiAsync(async () => await (Task)method.Invoke(owner, new object[] { tab, 0, new Point(x, height - y), width, height })!);
            await Task.Delay(150);
            if (opened || dialogs.Messages.Count != 0) throw new Exception("A zero-origin click on an offset page opened an editor.");
            // The displayed position (user space minus the visible-box origin) now selects the text.
            var origin = tab.Pages[0];
            await owner.RunUiAsync(async () => await (Task)method.Invoke(owner, new object[] { tab, 0, new Point(x - origin.OriginX, height - (y - origin.OriginY)), width, height })!);
            await Task.Delay(150);
            watchdog.Stop();
            if (!opened || dialogs.Messages.Count != 0) throw new Exception("Clicking text at its displayed position on an offset page did not open the editor.");
            var preview = new DrawingPlacementWindow(tab.Session, 0, "Rectangle") { Owner = owner, ShowInTaskbar = false,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            preview.Show();
            await WaitAsync(() => Children<TextBlock>(preview).Any(t => AutomationProperties.GetName(t) == "Drawing placement status" && t.Text.Contains("offset boundaries", StringComparison.Ordinal)));
            if (Children<Button>(preview).Single(b => AutomationProperties.GetName(b) == "Use drawing bounds from page").IsEnabled)
                throw new Exception("Offset-page drawing placement was enabled.");
            preview.Close();
            if (tab.Session.IsDirty || tab.Session.Revision != 0 || RecoveryStore.Fingerprint(source) != hash)
                throw new Exception("Offset-page refusals changed the session or source.");
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                Passed = new[] { "Offset CropBox is disclosed by extraction.", "A zero-origin click misses the text and opens no editor; a click at the displayed position opens the editor.",
                    "The coordinate-dialog drawing preview still refuses offset pages (the on-page tools support them).", "UI refusals preserve source bytes, revision and dirty state." },
                PhysicalInput = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS 4 offset-page UI checks. Evidence: {output}");
        }
        finally
        {
            watchdog.Stop();
            await owner.RunUiAsync(async () => { await owner.CloseAllAsync(); });
            typeof(MainWindow).GetField("closingAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
            owner.Close();
        }
    }

    static void CreateSource(string path)
    {
        const string stream = "BT /F1 18 Tf 150 400 Td (Offset source) Tj ET\n";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [100 200 500 700] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream" };
        var content = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(content.Length); content.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = content.Length; content.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) content.Append($"{offset:0000000000} 00000 n \n");
        content.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, content.ToString(), Encoding.ASCII);
    }

    static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!ready()) throw new TimeoutException("Offset-page UI did not reach its expected state.");
    }
    static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
}
