using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using HunterPdf.Core;
using Microsoft.Win32;

namespace HunterPdf.Desktop;

internal sealed class SigningWindow : Window
{
    readonly DocumentSession session;
    readonly AdvancedEngineLocation engine;
    readonly long revision;
    readonly TextBox certificate = new() { IsReadOnly = true, MinHeight = 44 };
    readonly PasswordBox password = new() { MaxLength = 4096, MinHeight = 44 };
    readonly TextBox destination = new() { IsReadOnly = true, MinHeight = 44 };
    readonly Button browseCertificate = new() { Content = "Choose certificate…", MinHeight = 44 };
    readonly Button browseDestination = new() { Content = "Choose new signed PDF…", MinHeight = 44 };
    readonly Button apply = new() { Content = "Create signed copy", MinHeight = 44, IsEnabled = false, IsDefault = true };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly CancellationTokenSource cancellation = new();
    bool working, closeRequested;
    internal SignedCopyResult? Result { get; private set; }

    internal SigningWindow(DocumentSession session, AdvancedEngineLocation engine)
    {
        this.session = session; this.engine = engine; revision = session.Revision;
        Title = "Create certificate-signed copy"; Width = 640; Height = 760; MinWidth = 500; MinHeight = 540; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(apply); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Text(string text, bool heading = false)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = heading ? 24 : 16 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); body.Children.Add(label);
        }
        Text("Create a digitally signed copy", true);
        Text("Uses your local PFX or P12 certificate and private key. Supports a currently valid RSA certificate of at least 2048 bits and an unsigned, unencrypted PDF. Existing certificate signatures and co-signing are unsupported.");
        Text("The new file is checked for cryptographic integrity. Certificate trust, revocation and trusted timestamps are not established. This is a certificate signature; no drawn signature mark is added. Your open document and save history stay unchanged.");
        Text("Certificate file"); body.Children.Add(certificate); body.Children.Add(browseCertificate);
        Text("Certificate password"); body.Children.Add(password);
        Text("New output PDF"); body.Children.Add(destination); body.Children.Add(browseDestination);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(certificate, "Signing certificate file"); AutomationProperties.SetName(password, "Signing certificate password");
        AutomationProperties.SetName(destination, "New signed PDF destination"); AutomationProperties.SetName(apply, "Create a new certificate-signed PDF copy");
        browseCertificate.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Signing certificates (*.pfx;*.p12)|*.pfx;*.p12", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) == true) certificate.Text = dialog.FileName;
        };
        browseDestination.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "PDF files (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true,
                FileName = Path.GetFileNameWithoutExtension(session.LastSavePath) + "-signed.pdf" };
            if (dialog.ShowDialog(this) == true) destination.Text = dialog.FileName;
        };
        certificate.TextChanged += (_, _) => UpdateApply(); destination.TextChanged += (_, _) => UpdateApply();
        apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => { if (working) { closeRequested = true; cancellation.Cancel(); } else DialogResult = false; };
        Loaded += (_, _) => { if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } };
        Closing += (_, args) => { if (working) { args.Cancel = true; closeRequested = true; cancellation.Cancel(); } };
        Closed += (_, _) => { password.Clear(); cancellation.Dispose(); };
    }
    void UpdateApply() => apply.IsEnabled = !working && !cancellation.IsCancellationRequested && certificate.Text.Length > 0 && destination.Text.Length > 0;
    async Task ApplyAsync()
    {
        if (working || !apply.IsEnabled) return;
        working = true; UpdateApply(); browseCertificate.IsEnabled = browseDestination.IsEnabled = password.IsEnabled = false;
        cancel.Content = "Cancel signing"; status.Text = "Creating and validating the signed copy…";
        var suppliedPassword = password.Password; password.Clear();
        try
        {
            Result = await session.CreateSignedCopyAsync(engine, destination.Text, certificate.Text, suppliedPassword, revision, cancellation.Token);
            working = false; DialogResult = true;
        }
        catch (OperationCanceledException) { status.Text = "Signing was cancelled before publication."; }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            suppliedPassword = ""; working = false; UpdateApply();
            browseCertificate.IsEnabled = browseDestination.IsEnabled = password.IsEnabled = true; cancel.Content = "Close";
            if (closeRequested && IsVisible) DialogResult = false;
        }
    }
}
