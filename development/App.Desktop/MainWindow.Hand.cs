using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HunterPdf.Desktop;

/// <summary>Hand tool: drag to pan the page. H chooses it, V returns to Select, and holding Space pans temporarily.</summary>
public partial class MainWindow
{
    bool handTool, spacePan;
    Point? panStart;
    double panHorizontal, panVertical;

    internal bool IsHandTool => handTool;
    internal bool IsPanning => panStart is not null;
    /// <summary>UI proofs raise synthetic key events, which cannot set the real keyboard's modifier state.</summary>
    internal ModifierKeys? ModifiersForTest { get; set; }
    internal string SelectedTextForTest => selectedText;
    ModifierKeys CurrentModifiers => ModifiersForTest ?? Keyboard.Modifiers;

    void InitializeHandTool()
    {
        DocumentScroll.PreviewMouseLeftButtonDown += Pan_MouseDown;
        DocumentScroll.PreviewMouseMove += Pan_MouseMove;
        DocumentScroll.PreviewMouseLeftButtonUp += (_, e) => { if (panStart is not null) { EndPan(); e.Handled = true; } };
        DocumentScroll.LostMouseCapture += (_, _) => EndPan();
        PreviewKeyDown += Hand_PreviewKeyDown;
        PreviewKeyUp += (_, e) => { if (e.Key == Key.Space && spacePan) { spacePan = false; ApplyPageToolCursor(); e.Handled = true; } };
        Deactivated += (_, _) => { spacePan = false; EndPan(); ApplyPageToolCursor(); };
    }

    void HandTool_Click(object sender, RoutedEventArgs e) => SetHandTool(HandToolToggle.IsChecked == true);

    internal void SetHandTool(bool on)
    {
        SetPageTool(PageTool.Select, false);
        handTool = on;
        HandToolToggle.IsChecked = on;
        SelectModeToggle.IsChecked = !on;
        ApplyPageToolCursor();
    }

    void Hand_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || active is null || busy || inlineEditor is not null || CurrentModifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return;
        switch (e.Key)
        {
            // Space still presses a focused button; it pans only while the page or a panel list has focus.
            case Key.Space when Keyboard.FocusedElement is not ButtonBase:
                if (!spacePan && !e.IsRepeat) { spacePan = true; ApplyPageToolCursor(); }
                e.Handled = true;
                break;
            case Key.H when !ThumbnailList.IsKeyboardFocusWithin:
                SetHandTool(true); e.Handled = true;
                break;
            case Key.V when !ThumbnailList.IsKeyboardFocusWithin:
                SetHandTool(false); e.Handled = true;
                break;
        }
    }

    void Pan_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((!handTool && !spacePan) || active is null || busy || inlineEditor is not null) return;
        // Leave the scroll bars to their own dragging.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, DocumentScroll); node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is ScrollBar) return;
        panStart = e.GetPosition(DocumentScroll);
        panHorizontal = DocumentScroll.HorizontalOffset; panVertical = DocumentScroll.VerticalOffset;
        DocumentScroll.CaptureMouse();
        Mouse.OverrideCursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    void Pan_MouseMove(object sender, MouseEventArgs e)
    {
        if (panStart is not Point start) return;
        var now = e.GetPosition(DocumentScroll);
        DocumentScroll.ScrollToHorizontalOffset(panHorizontal - (now.X - start.X));
        DocumentScroll.ScrollToVerticalOffset(panVertical - (now.Y - start.Y));
        e.Handled = true;
    }

    void EndPan()
    {
        if (panStart is null) return;
        panStart = null;
        Mouse.OverrideCursor = null;
        if (DocumentScroll.IsMouseCaptured) DocumentScroll.ReleaseMouseCapture();
    }
}
