using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class StructuredTextUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-structured-text-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var source = Path.Combine(output, "nested.pdf");
        var location = new AdvancedEngineLocation(Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"), Path.Combine(AppContext.BaseDirectory, "advanced", "worker.py"));
        var start = new ProcessStartInfo(location.PythonExecutable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", "-B", "-c", "import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); import nested_checks; nested_checks.fixture(Path(sys.argv[2]))", Path.Combine(root, "tools"), source }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Text UI fixture generation failed"); }
        var hash = RecoveryStore.Fingerprint(source);
        var owner = new MainWindow(Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe"), new RecoveryStore(Path.Combine(output, "recovery")), new TestDialogs())
        { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        owner.Show(); await Wait(() => !owner.IsBusy); await owner.OpenPathAsync(source);
        var session = owner.CurrentSession!; var revision = session.Revision;
        var dialog = new StructuredTextWindow(session, 0, location) { Owner = owner };
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var list = Children<ListBox>(dialog).Single(); await Wait(() => list.IsEnabled && list.Items.Count > 0);
                list.SelectedItem = list.Items.Cast<object>().First(x => x.ToString()!.Contains("Text = Before,"));
                var text = Children<TextBox>(dialog).Single(); Check(text.Text == "Before", "Selecting a mapped run loads original text");
                var preview = FindButton(dialog, "Preview replacement"); var apply = FindButton(dialog, "Apply reviewed replacement");
                Check(preview.IsEnabled && !apply.IsEnabled, "Apply requires a prepared and reviewed candidate");
                text.Text = "After edit";
                var wider = Children<CheckBox>(dialog).Single(c => (string)c.Content == "Allow replacement wider than the original run"); wider.IsChecked = true;
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => preview.IsEnabled);
                var images = Children<Image>(dialog).ToArray();
                Check(images.Length == 2 && images.All(i => i.Source is not null), "Dialog displays current and actual candidate renders");
                var slider = Children<Slider>(dialog).Single(); var originalWidth = images[0].Width;
                slider.Value = 2;
                Check(images.All(i => i.Width == originalWidth * 2), "Preview zoom enlarges both pages for detailed inspection");
                slider.Value = 1;
                Check(session.Revision == revision && !apply.IsEnabled, "Preview does not commit or bypass review");
                var reviewed = Children<CheckBox>(dialog).Single(c => (string)c.Content == "I reviewed the actual candidate page");
                reviewed.IsChecked = true; reviewed.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); Check(apply.IsEnabled, "Review confirmation enables apply");
                text.Text = "After"; Check(!apply.IsEnabled && images[1].Source is null && reviewed.IsChecked == false, "Changing replacement invalidates preview and review");
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => preview.IsEnabled && images[1].Source is not null);
                Children<ScrollViewer>(dialog).First().ScrollToBottom(); await Task.Delay(40);
                owner.SetTheme(ShellTheme.Light); Capture(dialog, Path.Combine(output, "mapped-text-light.png"));
                owner.SetTheme(ShellTheme.Dark); Capture(dialog, Path.Combine(output, "mapped-text-dark.png"));
                reviewed.IsChecked = true; reviewed.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(() => !dialog.IsVisible);
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        var result = dialog.ShowDialog();
        if (failure is not null) throw new Exception("Mapped text UI failed", failure);
        Check(result == true, "Real apply button commits reviewed mapped text");
        Check(session.Revision == revision + 1 && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After"), "Dialog commits the exact changed replacement");
        await session.UndoAsync(); Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Before"), "Dialog edit participates in undo");
        await session.RedoAsync(); var saved = Path.Combine(output, "saved.pdf"); await session.SaveAsync(saved);
        Check(File.Exists(saved) && RecoveryStore.Fingerprint(source) == hash, "Dialog edit saves while preserving the source");
        await owner.CloseTabAsync(owner.ActiveTab!); owner.Close();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} structured text UI checks: {output}");
    }
    static Button FindButton(DependencyObject root, string content) => Children<Button>(root).Single(b => b.Content is string text && text == content);
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var item in Children<T>(child)) yield return item; }
    }
    static async Task Wait(Func<bool> ready)
    { var watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(25); if (!ready()) throw new TimeoutException("Mapped text UI timed out"); }
    static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content; if (content is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        content.Measure(new Size(950, 820)); content.Arrange(new Rect(0, 0, 950, 820)); content.UpdateLayout();
        var image = new RenderTargetBitmap(950, 820, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
