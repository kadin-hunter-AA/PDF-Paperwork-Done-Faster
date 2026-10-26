using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class AnnotationManagerWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    readonly ListBox list = new() { MinHeight = 120, MaxHeight = 240 };
    readonly TextBox comment = new() { MinHeight = 100, MaxHeight = 180, MaxLength = 4000, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsEnabled = false };
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly Button update = new() { Content = "Save selected note comment", MinHeight = 44, IsEnabled = false };
    readonly Button delete = new() { Content = "Delete selected annotation", MinHeight = 44, IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox moveX = new() { MinHeight = 44, Text = "0", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox moveY = new() { MinHeight = 44, Text = "0", VerticalContentAlignment = VerticalAlignment.Center };
    readonly Button move = new() { Content = "Move selected drawing", MinHeight = 44, IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
    readonly ComboBox color = new() { MinHeight = 44, Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox lineWidth = new() { MinHeight = 44 };
    readonly Button restyle = new() { Content = "Apply style to selected drawing", MinHeight = 44, IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
    static readonly (string Name, int R, int G, int B)[] Palette = [("Indigo", 51, 23, 139), ("Red", 200, 30, 30), ("Green", 20, 120, 60), ("Black", 0, 0, 0), ("Orange", 230, 120, 0)];
    readonly Button close = new() { Content = "Close", MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
    CancellationTokenSource? operation;
    bool working, closeRequested;
    long loadedRevision;

    internal AnnotationManagerWindow(DocumentSession session, int page)
    {
        this.session = session; this.page = page;
        Title = $"Manage annotations — page {page + 1}"; Width = 640; Height = 860; MinWidth = 500; MinHeight = 560; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(update); footer.Children.Add(delete); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = $"Manage annotations · page {page + 1}", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Edit an existing sticky-note comment, move or restyle an unlocked rectangle, ellipse or line/pen stroke, or delete one unlocked sticky note, line stroke, rectangle, ellipse, highlight, underline or strikeout. Documents containing linked annotation popups/replies are protected. Locked annotations, links and form fields are also protected. Every accepted change supports Undo and recovery; this does not redact page content.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        list.DisplayMemberPath = nameof(Entry.Label); body.Children.Add(list); body.Children.Add(details);
        body.Children.Add(new TextBlock { Text = "Move the selected rectangle, ellipse or line/pen stroke by a distance in PDF points (right and up are positive). It stays on the page.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var moveGrid = new UniformGrid { Columns = 2 };
        moveGrid.Children.Add(Labeled("Right (points)", moveX)); moveGrid.Children.Add(Labeled("Up (points)", moveY));
        body.Children.Add(moveGrid); body.Children.Add(move);
        body.Children.Add(new TextBlock { Text = "Restyle the selected drawing's color and line width.", Margin = new Thickness(0, 14, 0, 6) });
        foreach (var entry in Palette) color.Items.Add(entry.Name);
        foreach (var width in new[] { "1", "2", "3", "5", "8" }) lineWidth.Items.Add(width);
        color.SelectedIndex = 0; lineWidth.SelectedIndex = 1;
        var styleGrid = new UniformGrid { Columns = 2 };
        styleGrid.Children.Add(Labeled("Color", color)); styleGrid.Children.Add(Labeled("Line width (points)", lineWidth));
        body.Children.Add(styleGrid); body.Children.Add(restyle);
        body.Children.Add(new TextBlock { Text = "Selected sticky-note comment", Margin = new Thickness(0, 0, 0, 6) }); body.Children.Add(comment);
        foreach (var text in body.Children.OfType<TextBlock>()) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        details.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(list, "Existing annotations on the current page"); AutomationProperties.SetName(comment, "Edit selected sticky-note comment");
        AutomationProperties.SetName(update, "Save the selected existing sticky-note comment"); AutomationProperties.SetName(delete, "Delete the selected existing annotation");
        AutomationProperties.SetName(moveX, "Move selected drawing right in PDF points"); AutomationProperties.SetName(moveY, "Move selected drawing up in PDF points");
        AutomationProperties.SetName(move, "Move the selected existing drawing"); AutomationProperties.SetName(color, "Selected drawing color");
        AutomationProperties.SetName(lineWidth, "Selected drawing line width in points"); AutomationProperties.SetName(restyle, "Apply color and width to the selected existing drawing");
        list.SelectionChanged += (_, _) => Select(); comment.TextChanged += (_, _) => UpdateButtons();
        update.Click += async (_, _) => await ApplyAsync(Action.Update); delete.Click += async (_, _) => await ApplyAsync(Action.Delete);
        move.Click += async (_, _) => await ApplyAsync(Action.Move); restyle.Click += async (_, _) => await ApplyAsync(Action.Restyle);
        moveX.TextChanged += (_, _) => UpdateButtons(); moveY.TextChanged += (_, _) => UpdateButtons();
        close.Click += (_, _) => { if (working) { closeRequested = true; operation?.Cancel(); } else DialogResult = false; };
        Loaded += async (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
            await LoadAsync();
        };
        Closing += (_, args) => { if (working) { args.Cancel = true; closeRequested = true; operation?.Cancel(); } };
        Closed += (_, _) => operation?.Dispose();
    }
    async Task LoadAsync()
    {
        Begin(); status.Text = "Reading existing annotations…";
        try
        {
            var eligibility = await session.GetAnnotationManagementStatusAsync(operation!.Token);
            var supported = eligibility.GetProperty("Supported").GetBoolean();
            var data = await session.ExtractAsync(page, operation.Token); loadedRevision = session.Revision;
            list.ItemsSource = data.GetProperty("Annotations").EnumerateArray().Select(value => Read(value, supported)).ToArray();
            status.Text = list.Items.Count == 0 ? "No annotations on this page." : "Select an annotation to inspect its supported actions.";
            if (!supported) status.Text = eligibility.GetProperty("Reason").GetString() ?? "This document is protected in the annotation manager.";
            if (data.TryGetProperty("AnnotationsTruncated", out var limited) && limited.GetBoolean()) status.Text += " Only the first 1,000 annotations are listed.";
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { status.Text = "Reading annotations was cancelled."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { End(); }
    }
    static Entry Read(JsonElement value, bool supported)
    {
        var subtype = value.GetProperty("Subtype").GetInt32();
        return new Entry(value.GetProperty("Id").GetString()!, value.GetProperty("Index").GetInt32(), subtype,
            supported && value.GetProperty("CanDelete").GetBoolean(), value.GetProperty("ContentsAvailable").GetBoolean(), value.GetProperty("Contents").GetString() ?? "",
            $"Left {value.GetProperty("Left").GetDouble():0.##}, bottom {value.GetProperty("Bottom").GetDouble():0.##}, right {value.GetProperty("Right").GetDouble():0.##}, top {value.GetProperty("Top").GetDouble():0.##} pt");
    }
    void Select()
    {
        if (list.SelectedItem is not Entry entry) { comment.Clear(); UpdateButtons(); return; }
        comment.Text = entry.ContentsAvailable ? entry.Contents : "[Comment exceeds this viewer's bounded read limit.]";
        details.Text = entry.Bounds + ". " + (entry.CanDelete ? "Deletion is reversible with Undo." : "This annotation is protected in this workflow.");
        if (entry.Subtype == 1 && entry.Contents.Length > 4000) details.Text += " Comment editing is limited to 4,000 characters; this longer comment remains read-only.";
        UpdateButtons();
    }
    void UpdateButtons()
    {
        var selected = list.SelectedItem as Entry;
        comment.IsEnabled = !working && selected?.Subtype == 1;
        comment.IsReadOnly = selected?.CanEdit != true;
        update.IsEnabled = !working && selected?.CanEdit == true && !string.IsNullOrWhiteSpace(comment.Text) && comment.Text != selected.Contents;
        delete.IsEnabled = !working && selected?.CanDelete == true;
        var drawing = !working && selected?.IsDrawing == true;
        move.IsEnabled = drawing && TryMove(out var dx, out var dy) && (dx != 0 || dy != 0);
        restyle.IsEnabled = drawing;
        moveX.IsEnabled = moveY.IsEnabled = color.IsEnabled = lineWidth.IsEnabled = drawing;
    }
    bool TryMove(out double dx, out double dy)
    {
        dy = 0;
        return double.TryParse(moveX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out dx) && double.IsFinite(dx) &&
               double.TryParse(moveY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out dy) && double.IsFinite(dy);
    }
    static StackPanel Labeled(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(text); panel.Children.Add(control);
        return panel;
    }
    enum Action { Update, Delete, Move, Restyle }
    void Begin() { operation?.Dispose(); operation = new CancellationTokenSource(); working = true; list.IsEnabled = false; UpdateButtons(); close.Content = "Cancel operation"; }
    void End() { working = false; list.IsEnabled = true; UpdateButtons(); close.Content = "Close"; if (closeRequested && IsVisible) DialogResult = false; }
    async Task ApplyAsync(Action action)
    {
        var removing = action == Action.Delete;
        if (working || list.SelectedItem is not Entry entry || !entry.CanDelete ||
            (action == Action.Update && !entry.CanEdit) || (action is Action.Move or Action.Restyle && !entry.IsDrawing)) return;
        if (!TryMove(out var dx, out var dy)) { dx = dy = 0; if (action == Action.Move) { status.Text = "Enter finite move distances in PDF points."; return; } }
        var chosen = Palette[Math.Max(0, color.SelectedIndex)];
        var width = double.Parse((string)lineWidth.SelectedItem!, CultureInfo.InvariantCulture);
        if (session.Revision != loadedRevision) { status.Text = "The document changed. Close and reopen the annotation list."; return; }
        var contents = comment.Text;
        Begin(); status.Text = action switch { Action.Delete => "Deleting the selected annotation…", Action.Move => "Moving the selected drawing…", Action.Restyle => "Restyling the selected drawing…", _ => "Updating the selected note…" };
        try
        {
            if (removing) await session.DeleteAnnotationAsync(page, entry.Id, operation!.Token);
            else if (action == Action.Move) await session.MoveDrawingAsync(page, entry.Id, dx, dy, operation!.Token);
            else if (action == Action.Restyle) await session.RestyleDrawingAsync(page, entry.Id, chosen.R, chosen.G, chosen.B, width, operation!.Token);
            else await session.UpdateCommentAsync(page, entry.Id, contents, operation!.Token);
            working = false; DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            status.Text = "The operation was cancelled. No change was accepted. Close and reopen this list before trying again.";
            list.ItemsSource = Array.Empty<Entry>();
        }
        catch (Exception error)
        {
            status.Text = error.Message + " Close and reopen this list before trying again.";
            // A rejected native edit discards its worker; all loaded IDs expire.
            list.ItemsSource = Array.Empty<Entry>();
        }
        finally { End(); }
    }
    sealed record Entry(string Id, int Index, int Subtype, bool CanDelete, bool ContentsAvailable, string Contents, string Bounds)
    {
        public bool IsDrawing => CanDelete && Subtype is 5 or 6 or 15;
        public bool CanEdit => Subtype == 1 && CanDelete && ContentsAvailable && Contents.Length <= 4000;
        public string Label => $"{Index + 1}. {Subtype switch { 1 => "Sticky note", 2 => "Link", 5 => "Rectangle", 6 => "Ellipse", 9 => "Highlight", 10 => "Underline", 12 => "Strikeout", 15 => "Line stroke", 20 => "Form field", _ => "Annotation type " + Subtype }}{(CanDelete ? "" : " · protected")}";
    }
}
