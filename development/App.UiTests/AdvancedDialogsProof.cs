using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class AdvancedDialogsProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-advanced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var engine = new AdvancedEngineLocation(Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "advanced", "worker.py"));
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        var owner = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "shell")), new TestDialogs())
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        owner.Show();
        await WaitAsync(() => !owner.IsBusy);
        var original = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var originalHash = RecoveryStore.Fingerprint(original);
        using var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "sign-recovery")), original);
        var revision = session.Revision;
        owner.SetTheme(ShellTheme.Light);
        var cancelled = new SigningWindow(session, engine) { Owner = owner };
        Check(Interact(cancelled, async () =>
        {
            Check(!Find<Button>(cancelled, "Create a new certificate-signed PDF copy").IsEnabled, "Signing requires a certificate and new destination");
            Capture(cancelled, Path.Combine(output, "signing-light.png"), 640, 760);
            Buttons(cancelled).Single(b => b.Content?.ToString() == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !cancelled.IsVisible);
        }) == false, "Cancel closes the signing dialog without changing the document");
        var pfx = Path.Combine(output, "disposable.pfx");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            MakePfx(engine.PythonExecutable, pfx, secret);
            owner.SetTheme(ShellTheme.Dark);
            var signing = new SigningWindow(session, engine) { Owner = owner };
            PasswordBox? passwordField = null;
            var signedPath = Path.Combine(output, "signed.pdf");
            Check(Interact(signing, async () =>
            {
                Find<TextBox>(signing, "Signing certificate file").Text = pfx;
                Find<TextBox>(signing, "New signed PDF destination").Text = signedPath;
                passwordField = Find<PasswordBox>(signing, "Signing certificate password");
                passwordField.Password = secret;
                var apply = Find<Button>(signing, "Create a new certificate-signed PDF copy");
                Check(apply.IsEnabled, "A selected certificate and destination enable signing");
                Capture(signing, Path.Combine(output, "signing-dark.png"), 640, 760);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var signingStatus = (TextBlock)typeof(SigningWindow).GetField("status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(signing)!;
                await WaitAsync(() => !signing.IsVisible || (signingStatus.Text.Length > 0 && signingStatus.Text != "Creating and validating the signed copy…"), 185);
                if (signing.IsVisible) throw new Exception("Signing dialog refused the operation: " + signingStatus.Text);
            }) == true, "The real signing Apply button creates a signed copy");
            Check(File.Exists(signedPath) && signing.Result is { IntegrityValid: true, CryptographicallyValid: true,
                CertificateTrustEstablished: false, RevocationEstablished: false, TrustedTimestamp: false }, "Signing UI reports integrity without claiming trust or trusted time");
            Check(passwordField?.Password.Length == 0, "The password field is cleared after signing");
            Check(session.Revision == revision && !session.IsDirty && RecoveryStore.Fingerprint(original) == originalHash,
                "Signing through the dialog preserves session state and source bytes");
        }
        finally { if (File.Exists(pfx)) File.Delete(pfx); secret = ""; }
        var scan = Path.Combine(root, "fixtures", "corpus-v1", "edge", "scan-real-text.pdf");
        var scanHash = RecoveryStore.Fingerprint(scan);
        using var scanSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "ocr-recovery")), scan);
        owner.SetTheme(ShellTheme.Light);
        var ocr = new OcrWindow(scanSession, engine, Path.Combine(AppContext.BaseDirectory, "advanced", "tessdata", "eng.traineddata"),
            Path.Combine(root, "native", "bin", "pdfium.dll")) { Owner = owner };
        Check(Interact(ocr, async () =>
        {
            Capture(ocr, Path.Combine(output, "ocr-light.png"), 600, 520);
            Find<Button>(ocr, "Recognize text in this document locally").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !ocr.IsVisible, 60);
        }) == true, "The real OCR Apply button commits a searchable layer");
        Check(scanSession.IsDirty && (await scanSession.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Invoice number 12345"),
            "OCR dialog result is searchable through PDFium");
        Check(RecoveryStore.Fingerprint(scan) == scanHash, "OCR dialog preserves the frozen scan source");
        var redactionSource = Path.Combine(output, "redaction-source.pdf");
        MakeRedactionSource(engine.PythonExecutable, root, redactionSource);
        var redactionHash = RecoveryStore.Fingerprint(redactionSource);
        using var redactionSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "redaction-recovery")), redactionSource);
        var redaction = new RedactionWindow(redactionSession, 0, engine) { Owner = owner };
        var redactionPath = Path.Combine(output, "redacted.pdf");
        Check(Interact(redaction, async () =>
        {
            var prepare = Find<Button>(redaction, "Prepare a permanent redaction preview");
            var publish = Find<Button>(redaction, "Export the reviewed permanent redaction to a new PDF");
            var image = Find<Image>(redaction, "Actual PDFium-rendered redaction candidate");
            await WaitAsync(() => prepare.IsEnabled);
            Check(!publish.IsEnabled && image.Source is null, "Redaction export is disabled until an actual preview is prepared and reviewed");
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => image.Source is not null);
            Check(!publish.IsEnabled && !redactionSession.IsDirty, "A prepared preview still requires review and leaves the document unchanged");
            Capture(redaction, Path.Combine(output, "redaction-light.png"), 1020, 900);
            var left = Find<TextBox>(redaction, "Redaction left in PDF points from top-left");
            left.Text = "73";
            Check(image.Source is null && !publish.IsEnabled, "Changing a rectangle invalidates the reviewed redaction candidate");
            left.Text = "72";
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => image.Source is not null);
            Find<CheckBox>(redaction, "Confirm review of the redaction result").IsChecked = true;
            Check(publish.IsEnabled, "Review confirmation enables redaction export");
            owner.SetTheme(ShellTheme.Dark);
            Capture(redaction, Path.Combine(output, "redaction-dark.png"), 1020, 900);
            await redaction.PublishToAsync(redactionPath);
            await WaitAsync(() => !redaction.IsVisible);
        }) == true, "The real redaction dialog exports its reviewed candidate to a new file");
        Check(redaction.Result is { MetadataRemoved: true } && File.Exists(redactionPath) && !redactionSession.IsDirty &&
            RecoveryStore.Fingerprint(redactionSource) == redactionHash, "Redaction UI preserves source and session state while publishing a new PDF");
        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(output, "redacted-reopen")), redactionPath))
        {
            var text = (await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(!text.Contains("REDACTION_TARGET_ALPHA") && text.Contains("PUBLIC_AFTER_MARKER"), "The dialog's saved output reopens without selected text and retains unrelated text");
        }
        owner.Close();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} advanced dialog checks. Evidence: {output}");
    }
    static bool? Interact(Window dialog, Func<Task> action)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        var result = dialog.ShowDialog();
        if (failure is not null) throw new Exception("Advanced dialog interaction failed.", failure);
        return result;
    }
    static async Task WaitAsync(Func<bool> ready, int seconds = 15)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(25);
        if (!ready()) throw new TimeoutException("Advanced dialog did not complete in its expected time.");
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var item in Children<T>(child)) yield return item;
        }
    }
    static T Find<T>(DependencyObject root, string name) where T : DependencyObject => Children<T>(root).Single(c => AutomationProperties.GetName(c) == name);
    static IEnumerable<Button> Buttons(DependencyObject root) => Children<Button>(root);
    static void Capture(Window window, string path, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        if (root is Panel surface) surface.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    static void MakePfx(string python, string path, string password)
    {
        const string script = """
            import sys,datetime
            from pathlib import Path
            from cryptography import x509
            from cryptography.x509.oid import NameOID
            from cryptography.hazmat.primitives import hashes,serialization
            from cryptography.hazmat.primitives.asymmetric import rsa
            from cryptography.hazmat.primitives.serialization import pkcs12
            secret=sys.stdin.readline().rstrip("\n").encode("utf-8")
            key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
            name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,"Disposable UI proof")])
            now=datetime.datetime.now(datetime.timezone.utc)
            cert=(x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
                .serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1))
                .not_valid_after(now+datetime.timedelta(days=7)).sign(key,hashes.SHA256()))
            Path(sys.argv[1]).write_bytes(pkcs12.serialize_key_and_certificates(b"UI proof",key,cert,None,serialization.BestAvailableEncryption(secret)))
            """;
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Disposable signing credential process did not start.");
        process.StandardInput.WriteLine(password); process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Disposable signing credential generation failed.");
    }

    static void MakeRedactionSource(string python, string root, string path)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", "import sys; from pathlib import Path; sys.path.insert(0,str(Path(sys.argv[1])/'RedactionProof')); import proof; proof.make_primary_source(Path(sys.argv[2]))", root, path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Redaction source generator did not start.");
        _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Redaction UI source generation failed.");
    }
}
