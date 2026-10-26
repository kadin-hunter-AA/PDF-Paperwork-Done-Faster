namespace HunterPdf.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            System.Windows.MessageBox.Show(
                "PDF - Paperwork Done Faster requires Windows 11.",
                "Windows 11 required",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }
}
