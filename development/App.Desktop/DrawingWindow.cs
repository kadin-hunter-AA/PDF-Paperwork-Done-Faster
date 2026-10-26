using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>Coordinate and rendered-page placement for native PDF line, arrow, rectangle and ellipse annotations.</summary>
internal sealed class DrawingWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    readonly ComboBox kind = new() { MinHeight = 44, MinWidth = 180 };
    readonly TextBox left = CoordinateBox(), bottom = CoordinateBox(), right = CoordinateBox(), top = CoordinateBox();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    readonly TextBox boxText = new() { MinHeight = 44, MaxLength = 200, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StackPanel boxTextPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button apply = new() { Content = "Add drawing", MinHeight = 44 };
    bool working, placing;
    IReadOnlyList<(double X, double Y)>? placedStroke;

    public DrawingWindow(DocumentSession session, int page)
    {
        this.session = session;
        this.page = page;
        Title = $"Add drawing — page {page + 1}";
        Width = 560; Height = 620; MinWidth = 480; MinHeight = 420; FontSize = 16;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(apply);
        System.Windows.Automation.AutomationProperties.SetName(status, "Drawing validation and operation status");
        System.Windows.Automation.AutomationProperties.SetName(apply, "Add drawing");
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = $"Add drawing · page {page + 1}", FontSize = 24, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "Add a pen stroke, line, arrow, rectangle or ellipse annotation. Choose Place on page to draw it (lines and arrows run in the direction you drag), or enter bounds in PDF points from the bottom-left corner; coordinate-entered lines and arrows run from the bottom-left to the top-right. Pen strokes need Place on page. A text box adds one line of text inside a bordered rectangle. Arrows include a two-stroke head. Use Undo to remove a change. Annotations do not redact page content.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16)
        });
        body.Children.Add(new TextBlock { Text = "Drawing type", Margin = new Thickness(0, 0, 0, 4) });
        kind.Items.Add("Line"); kind.Items.Add("Arrow"); kind.Items.Add("Rectangle"); kind.Items.Add("Ellipse"); kind.Items.Add("Pen"); kind.Items.Add("Text box");
        kind.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(kind, "Native PDF drawing annotation type");
        body.Children.Add(kind);
        boxTextPanel.Children.Add(new TextBlock { Text = "Text box text (one line, basic ASCII, 12 pt Helvetica; refused if it does not fit)", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        boxTextPanel.Children.Add(boxText);
        System.Windows.Automation.AutomationProperties.SetName(boxText, "Text box text");
        body.Children.Add(boxTextPanel);
        kind.SelectionChanged += (_, _) => boxTextPanel.Visibility = (string?)kind.SelectedItem == "Text box" ? Visibility.Visible : Visibility.Collapsed;
        var place = new Button { Content = "Place on page…", MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(place, "Place drawing bounds on rendered page");
        body.Children.Add(place);
        place.Click += (_, _) =>
        {
            if (working) return;
            var placement = new DrawingPlacementWindow(session, page, (string)kind.SelectedItem!) { Owner = this };
            if (placement.ShowDialog() != true) return;
            var bounds = placement.Bounds;
            placing = true;
            placedStroke = placement.HasStroke ? placement.Stroke.ToArray() : null;
            left.Text = bounds.Left.ToString("R", CultureInfo.InvariantCulture);
            bottom.Text = bounds.Top.ToString("R", CultureInfo.InvariantCulture);
            right.Text = bounds.Right.ToString("R", CultureInfo.InvariantCulture);
            top.Text = bounds.Bottom.ToString("R", CultureInfo.InvariantCulture);
            placing = false;
            status.Text = placedStroke is null ? "Bounds selected from the page. Review them, then choose Add drawing to commit."
                : "Stroke captured from the page. Review it, then choose Add drawing to commit.";
        };
        kind.SelectionChanged += (_, _) => placedStroke = null;
        foreach (var box in new[] { left, bottom, right, top })
            box.TextChanged += (_, _) => { if (!placing) placedStroke = null; };
        body.Children.Add(new TextBlock { Text = "Bounds in PDF points", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        var fields = new Grid();
        for (var i = 0; i < 4; i++) fields.ColumnDefinitions.Add(new ColumnDefinition());
        AddCoordinate(fields, "Left", left, 0); AddCoordinate(fields, "Bottom", bottom, 1);
        AddCoordinate(fields, "Right", right, 2); AddCoordinate(fields, "Top", top, 3);
        body.Children.Add(fields);
        status.Text = "Bounds are checked against the selected page before an annotation is added.";
        apply.Click += async (_, _) => await ApplyAsync();
        Loaded += (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
        };
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }

    static TextBox CoordinateBox() => new() { MinHeight = 44, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };

    static void AddCoordinate(Grid grid, string label, TextBox box, int column)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(box);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        System.Windows.Automation.AutomationProperties.SetName(box, $"{label} coordinate in PDF points");
    }

    static bool TryCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    async Task ApplyAsync()
    {
        if (working) return;
        if ((string)kind.SelectedItem! == "Pen" && placedStroke is null)
        {
            status.Text = "Choose Place on page and draw the pen stroke first.";
            return;
        }
        if ((string)kind.SelectedItem! == "Text box" && string.IsNullOrWhiteSpace(boxText.Text))
        {
            status.Text = "Enter the text for the text box first.";
            return;
        }
        if (!TryCoordinate(left.Text, out var l) || !TryCoordinate(bottom.Text, out var b) ||
            !TryCoordinate(right.Text, out var r) || !TryCoordinate(top.Text, out var t) || r <= l || t <= b)
        {
            status.Text = "Enter finite coordinates using decimal points, with Right greater than Left and Top greater than Bottom.";
            return;
        }
        working = true;
        apply.IsEnabled = false;
        kind.IsEnabled = false; boxText.IsEnabled = false;
        try
        {
            if ((string)kind.SelectedItem! == "Text box") await session.AddTextBoxAsync(page, boxText.Text, l, b, r, t);
            else if (placedStroke is not null) await session.AddInkAsync(page, (string)kind.SelectedItem!, placedStroke);
            else await session.AddDrawingAsync(page, (string)kind.SelectedItem!, l, b, r, t);
            working = false;
            DialogResult = true;
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { working = false; apply.IsEnabled = true; kind.IsEnabled = true; boxText.IsEnabled = true; }
    }
}
