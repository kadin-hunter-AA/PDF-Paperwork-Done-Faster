using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace HunterPdf.Desktop;

/// <summary>Task menus in the command row: each is a toggle button over a popup of ordinary command buttons.</summary>
public partial class MainWindow
{
    void MenuPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: UIElement child })
            Dispatcher.BeginInvoke(new Action(() => child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))), System.Windows.Threading.DispatcherPriority.Input);
    }

    void MenuPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Popup popup) return;
        popup.IsOpen = false;
        (popup.PlacementTarget as UIElement)?.Focus();
        e.Handled = true;
    }

    // Any command chosen from a menu closes it; the command itself runs through the button's own handler.
    void MenuPopup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Popup popup && e.OriginalSource is ButtonBase) popup.IsOpen = false;
    }

    void MenuTool_Click(object sender, RoutedEventArgs e)
    {
        if (active is not null && sender is Button { CommandParameter: string name } && Enum.TryParse<PageTool>(name, out var tool))
            SetPageTool(tool, true);
    }
}

internal sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
