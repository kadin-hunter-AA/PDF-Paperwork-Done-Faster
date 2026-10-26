using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HunterPdf.Desktop;

/// <summary>Visual signatures, initials and stamps placed on the page with one click. These are images, not digital signatures.</summary>
public partial class MainWindow
{
    /// <summary>An image waiting for the user to click where it goes, with its default width in PDF points.</summary>
    internal sealed record PendingStamp(SignatureImage Image, string Label, double WidthPoints);

    PendingStamp? pendingStamp;
    SignatureStore? signatureStore;

    internal PendingStamp? PendingPlacement => pendingStamp;
    internal SignatureStore Signatures => signatureStore ??= new SignatureStore(
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(recovery.Root)!, "Signatures"));

    internal void BeginPlacement(PendingStamp stamp)
    {
        ResetPageToolState();
        if (IsEditMode) SetPageTool(PageTool.Select, false);
        pendingStamp = stamp;
        foreach (var layer in selectionLayers) layer.Cursor = Cursors.Cross;
        notice = "Click on the page where the " + stamp.Label + " should go. Esc cancels.";
        if (active is not null) UpdateDocumentStatus(active);
    }

    void CancelPlacement()
    {
        if (pendingStamp is null) return;
        pendingStamp = null;
        ApplyPageToolCursor();
        notice = null;
        if (active is not null) UpdateDocumentStatus(active);
    }

    /// <summary>Places the pending image centered on the clicked point, kept inside the page, at its natural aspect ratio.</summary>
    internal async Task PlacePendingAsync(OpenDocumentTab tab, int pageIndex, Point position, double viewWidth, double viewHeight)
    {
        if (pendingStamp is not { } stamp || active != tab || pageIndex < 0 || pageIndex >= tab.Pages.Count) return;
        var page = tab.Pages[pageIndex];
        var revision = tab.Session.Revision;
        if (!await PrepareDirectAnnotationAsync(tab, pageIndex, revision)) return;
        var width = Math.Min(stamp.WidthPoints, page.Width * .9);
        var height = width * stamp.Image.Height / stamp.Image.Width;
        if (height > page.Height * .9) { height = page.Height * .9; width = height * stamp.Image.Width / stamp.Image.Height; }
        // Width and height are in displayed-page points. Position the image there, then map the rectangle into
        // user space (rotation and visible-box origin); the engine rotates the image matrix so it reads upright.
        var cx = Math.Clamp(position.X, 0, viewWidth) * page.Width / viewWidth;
        var cy = Math.Clamp(position.Y, 0, viewHeight) * page.Height / viewHeight;
        var dx = Math.Clamp(cx - width / 2, 0, page.Width - width);
        var dy = Math.Clamp(cy - height / 2, 0, page.Height - height);
        var footprint = DisplayRectToPdf(page, dx, dy, dx + width, dy + height);
        double left = footprint.Left, bottom = footprint.Top, right = footprint.Right, top = footprint.Bottom;
        pendingStamp = null;
        ApplyPageToolCursor();
        await tab.Session.AddImageAsync(pageIndex, stamp.Image.Bgra, stamp.Image.Width, stamp.Image.Height, left, bottom, right, top);
        tab.CurrentPage = pageIndex;
        notice = char.ToUpper(stamp.Label[0], CultureInfo.CurrentCulture) + stamp.Label[1..] +
            " placed as an image (not a digital signature). Ctrl+Z removes it; Edit content → Edit or add images moves or resizes it.";
    }

    async Task<bool> PlacementMouseDownAsync(Canvas layer, OpenDocumentTab tab, int pageIndex, MouseButtonEventArgs e)
    {
        if (pendingStamp is null) return false;
        e.Handled = true;
        var position = e.GetPosition(layer);
        await RunUiAsync(() => PlacePendingAsync(tab, pageIndex, position, layer.ActualWidth, layer.ActualHeight), "Placing " + pendingStamp.Label + "…");
        return true;
    }

    void CaptureSignature(SignatureKind kind)
    {
        if (active is null || busy) return;
        var window = new SignatureCaptureWindow(Signatures, kind) { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } image)
            BeginPlacement(new PendingStamp(image, image.Kind == SignatureKind.Initials ? "initials" : "signature", image.Kind == SignatureKind.Initials ? 60 : 160));
    }

    void AddSignature_Click(object sender, RoutedEventArgs e) => CaptureSignature(SignatureKind.Signature);
    void AddInitials_Click(object sender, RoutedEventArgs e) => CaptureSignature(SignatureKind.Initials);

    void SavedSignature_Click(object sender, RoutedEventArgs e)
    {
        if (active is null || busy) return;
        var saved = Signatures.List();
        if (saved.Count == 0) { dialogs.Information("No saved signatures yet. Use Add signature and keep \"Save for later\" checked.", "Saved signatures"); return; }
        if (PickSavedSignature(saved) is { } chosen && Signatures.Load(chosen.Id) is { } image)
            BeginPlacement(new PendingStamp(image, chosen.Kind == SignatureKind.Initials ? "initials" : "signature", chosen.Kind == SignatureKind.Initials ? 60 : 160));
    }

    void DateStamp_Click(object sender, RoutedEventArgs e)
    {
        if (active is null || busy) return;
        BeginPlacement(new PendingStamp(StampImages.Date(DateTime.Now.ToString("d", CultureInfo.CurrentCulture), 96), "date", 90));
    }

    void CheckStamp_Click(object sender, RoutedEventArgs e) { if (active is not null && !busy) BeginPlacement(new PendingStamp(StampImages.Check(96), "check mark", 14)); }
    void CrossStamp_Click(object sender, RoutedEventArgs e) { if (active is not null && !busy) BeginPlacement(new PendingStamp(StampImages.Cross(96), "cross mark", 14)); }

    SavedSignature? PickSavedSignature(IReadOnlyList<SavedSignature> saved)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Saved signatures", Width = 460, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackground");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(list, "Saved signatures");
        foreach (var item in saved)
        {
            var image = Signatures.Load(item.Id);
            var row = new DockPanel { Margin = new Thickness(4), Tag = item };
            row.Children.Add(new TextBlock { Text = item.Kind == SignatureKind.Initials ? "Initials" : "Signature", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            if (image is not null)
                row.Children.Add(new Border
                {
                    Background = Brushes.White, Padding = new Thickness(4), Height = 56, HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), Stretch = Stretch.Uniform }
                });
            list.Items.Add(row);
        }
        list.SelectedIndex = 0;
        SavedSignature? chosen = null;
        var use = new Button { Content = "Place", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
        var remove = new Button { Content = "Delete", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
        use.Click += (_, _) => { if (list.SelectedItem is DockPanel { Tag: SavedSignature s }) { chosen = s; dialog.DialogResult = true; } };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is DockPanel { Tag: SavedSignature s }) { chosen = s; dialog.DialogResult = true; } };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not DockPanel { Tag: SavedSignature s } row) return;
            Signatures.Delete(s.Id); list.Items.Remove(row);
            if (list.Items.Count == 0) dialog.DialogResult = false; else list.SelectedIndex = 0;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(remove); buttons.Children.Add(cancel); buttons.Children.Add(use);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var header = new TextBlock { Text = "Visual signatures are images placed on the page. They are not digital signatures.", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(header); panel.Children.Add(buttons); panel.Children.Add(list);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? chosen : null;
    }
}
