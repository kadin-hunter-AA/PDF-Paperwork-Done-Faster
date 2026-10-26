using System.IO;
using System.Windows;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

/// <summary>Confirms only PDFium's password-required error opens the password prompt.</summary>
static class PdfOpenErrorUiProof
{
    internal static async Task RunAsync(string root)
    {
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var encrypted = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "password-aes256.pdf");
        var malformed = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "malformed.pdf");
        var output = Path.Combine(root, "artifacts", "ui-pdf-open-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var dialogs = new ProofDialogs();
        var window = new MainWindow(worker, new RecoveryStore(Path.Combine(output, "recovery")), dialogs)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 1000,
            Height = 720,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            await WaitUntilAsync(() => !window.IsBusy);
            await window.RunUiAsync(() => window.OpenPathAsync(malformed), "Opening malformed-PDF proof…");
            if (dialogs.PasswordCalls != 0)
                throw new Exception("A malformed PDF incorrectly displayed the password prompt.");
            if (dialogs.Messages.Count != 1 || !dialogs.Messages[0].Contains("PDFium could not open this file", StringComparison.Ordinal))
                throw new Exception("The malformed PDF did not display its informative generic open error.");

            await window.RunUiAsync(() => window.OpenPathAsync(encrypted), "Opening encrypted-PDF proof…");
            if (dialogs.PasswordCalls != 1 || window.Documents.Count != 0)
                throw new Exception("An encrypted PDF did not prompt exactly once and remain closed when the user cancelled.");
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, "results.json"), System.Text.Json.JsonSerializer.Serialize(new
            { MalformedPromptCount = 0, EncryptedPromptCount = dialogs.PasswordCalls, CancelLeavesNoOpenDocument = true }));
            Console.WriteLine("PASS PDF open error UI proof: malformed input shows its open error without a password prompt; encrypted input prompts once.");
        }
        finally { window.Close(); }
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("The PDF open error UI proof did not become idle.");
    }

    sealed class ProofDialogs : IDocumentDialogs
    {
        internal int PasswordCalls { get; private set; }
        internal List<string> Messages { get; } = [];
        public string? OpenPath() => null;
        public string? SavePath(DocumentSession session) => null;
        public MessageBoxResult UnsavedChoice(DocumentSession session) => MessageBoxResult.Cancel;
        public bool ConfirmOverwrite() => false;
        public bool ConfirmSignedCopy() => false;
        public MessageBoxResult RecoveryChoice(RecoveryEntry entry) => MessageBoxResult.No;
        public string? Password(string message) { PasswordCalls++; return null; }
        public void Information(string message, string title) => Messages.Add(title + ": " + message);
        public bool Confirm(string message, string title) => false;
    }
}
