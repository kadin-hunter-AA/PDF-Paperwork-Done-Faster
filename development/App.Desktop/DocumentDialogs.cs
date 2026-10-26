using System.Windows;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal interface IDocumentDialogs
{
    string? OpenPath();
    string? SavePath(DocumentSession session);
    MessageBoxResult UnsavedChoice(DocumentSession session);
    bool ConfirmOverwrite();
    bool ConfirmSignedCopy();
    MessageBoxResult RecoveryChoice(RecoveryEntry entry);
    string? Password(string message);
    void Information(string message, string title);
    bool Confirm(string message, string title);
}

internal sealed class DocumentDialogs(Window owner) : IDocumentDialogs
{
    public string? OpenPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? SavePath(DocumentSession session)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF files|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true,
            FileName = System.IO.Path.GetFileNameWithoutExtension(session.OriginalPath) + "-edited.pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(session.LastSavePath)
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public MessageBoxResult UnsavedChoice(DocumentSession session) => MessageBox.Show(owner,
        $"Save changes to {System.IO.Path.GetFileName(session.LastSavePath)} before closing? Choose No to discard them, or Cancel to keep working.",
        "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    public bool ConfirmOverwrite() => MessageBox.Show(owner,
        "Replace the saved PDF with the current version? A backup of its previous version will be kept.",
        "Save PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public bool ConfirmSignedCopy() => MessageBox.Show(owner,
        "This PDF contains signature fields, which may be unsigned. Editing a copy invalidates any existing signatures for the edited copy. Signature validity is not verified. The original will be protected. Edit a copy?",
        "Signed document", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public MessageBoxResult RecoveryChoice(RecoveryEntry entry) => MessageBox.Show(owner,
        $"{System.IO.Path.GetFileName(entry.OriginalPath)} was not closed normally. {(entry.IsDirty ? "It has recoverable unsaved changes." : "Its checkpoint is available.")} Recover it? Choose No to leave it for later.",
        "Recover document", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    public void Information(string message, string title) => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public bool Confirm(string message, string title) => MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public string? Password(string message)
    {
        var dialog = new Window { Owner = owner, Title = "PDF password", Width = 430, Height = 200,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var password = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4) };
        var ok = new Button { Content = "Open", IsDefault = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => password.Focus();
        return dialog.ShowDialog() == true ? password.Password : null;
    }
}
