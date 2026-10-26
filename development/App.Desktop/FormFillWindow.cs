using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class FormFillWindow : Window
{
    readonly DocumentSession session;
    readonly AdvancedEngineLocation engine;
    readonly int page;
    readonly ListBox fields = new() { MinHeight = 100, MaxHeight = 200 };
    readonly TextBox value = new() { MinHeight = 44, MaxLength = 4096, IsEnabled = false };
    readonly CheckBox checkbox = new() { Content = "Checked", MinHeight = 44, IsEnabled = false, Visibility = Visibility.Collapsed };
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly Button apply = new() { Content = "Apply field value", MinHeight = 44, IsEnabled = false, IsDefault = true };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
    CancellationTokenSource? operation;
    long loadedRevision;
    bool working, closeAfterCancellation, closed;

    internal FormFillWindow(DocumentSession session, int page, AdvancedEngineLocation engine)
    {
        this.session = session; this.page = page; this.engine = engine;
        Title = $"Fill existing form — page {page + 1}";
        Width = 620; Height = 700; MinWidth = 500; MinHeight = 520; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel();
        footer.Children.Add(status); footer.Children.Add(apply); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = $"Fill an existing form field · page {page + 1}", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Supports plain text fields with one unrotated widget and single checkboxes with explicit on/off appearances. Password, read-only, calculated, rich-text, radio and choice fields are refused. Other fields and page content are preserved. This does not create form fields.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        body.Children.Add(new TextBlock { Text = "Existing fields", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        fields.DisplayMemberPath = nameof(Field.Label); body.Children.Add(fields); body.Children.Add(details);
        body.Children.Add(new TextBlock { Text = "New field value", Margin = new Thickness(0, 0, 0, 6) }); body.Children.Add(value); body.Children.Add(checkbox);
        foreach (var text in body.Children.OfType<TextBlock>()) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        details.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(fields, "Existing form fields on the current page");
        AutomationProperties.SetName(value, "New value for the selected existing form field");
        AutomationProperties.SetName(checkbox, "New checked state for the selected existing checkbox field");
        AutomationProperties.SetName(apply, "Apply existing form field value");
        fields.SelectionChanged += (_, _) => SelectField();
        value.TextChanged += (_, _) => UpdateApply();
        checkbox.Checked += (_, _) => UpdateApply(); checkbox.Unchecked += (_, _) => UpdateApply();
        apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => CancelOrClose();
        Loaded += async (_, _) =>
        {
            if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; }
            await LoadAsync();
        };
        Closing += (_, args) =>
        {
            if (working) { args.Cancel = true; closeAfterCancellation = true; operation?.Cancel(); }
        };
        Closed += (_, _) => { closed = true; operation?.Dispose(); };
    }

    async Task LoadAsync()
    {
        BeginOperation(); status.Text = "Reading the existing fields…";
        try
        {
            var result = await session.GetFormFieldsAsync(page, operation!.Token);
            loadedRevision = session.Revision;
            fields.ItemsSource = result.GetProperty("Fields").EnumerateArray().Select(ReadField).ToArray();
            status.Text = fields.Items.Count == 0 ? "No existing form fields were found on this page." : "Select an existing text field or checkbox. Unsupported field types remain read-only.";
            if (fields.Items.Count > 0) fields.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { status.Text = "Reading fields was cancelled."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { EndOperation(); }
    }

    static Field ReadField(JsonElement item) => new(
        item.GetProperty("Name").GetString() ?? "", item.GetProperty("Type").GetString() ?? "Unknown",
        item.GetProperty("Value").GetString() ?? "", item.GetProperty("ReadOnly").GetBoolean(), item.GetProperty("Password").GetBoolean());

    void SelectField()
    {
        checkbox.IsChecked = false;
        checkbox.Visibility = Visibility.Collapsed;
        if (fields.SelectedItem is not Field field) { value.Clear(); value.IsEnabled = false; checkbox.IsEnabled = false; return; }
        if (field.Type == "Checkbox")
        {
            value.Clear(); value.IsEnabled = false;
            checkbox.Visibility = Visibility.Visible;
            checkbox.IsChecked = field.Checked;
            checkbox.IsEnabled = !working && field.CanAttemptFill;
            details.Text = field.CanAttemptFill ? "The adapter will accept this change only when the existing widget has one explicit on appearance and one explicit off appearance." : "This checkbox value is unavailable or the field cannot be safely changed.";
        }
        else
        {
            value.Text = field.Value;
            value.IsEnabled = !working && field.CanAttemptFill;
            checkbox.IsEnabled = false;
            details.Text = field.CanAttemptFill ? "The adapter will check field actions, font coverage and available space before accepting a change." : $"{field.Type} field; filling this field is unavailable.";
        }
        UpdateApply();
    }
    void UpdateApply() => apply.IsEnabled = !working && fields.SelectedItem is Field field && field.CanAttemptFill &&
        (field.Type == "Checkbox" ? checkbox.IsChecked != field.Checked : value.Text != field.Value);
    void BeginOperation()
    {
        operation?.Dispose(); operation = new CancellationTokenSource(); working = true;
        fields.IsEnabled = false; value.IsEnabled = false; apply.IsEnabled = false;
        checkbox.IsEnabled = false;
        cancel.Content = "Cancel operation";
    }
    void EndOperation()
    {
        working = false; fields.IsEnabled = true;
        value.IsEnabled = fields.SelectedItem is Field field && field.CanAttemptFill && field.Type == "Text";
        checkbox.IsEnabled = fields.SelectedItem is Field checkboxField && checkboxField.CanAttemptFill && checkboxField.Type == "Checkbox";
        UpdateApply(); cancel.Content = "Cancel";
        if (closeAfterCancellation && !closed) DialogResult = false;
    }
    async Task ApplyAsync()
    {
        if (working || fields.SelectedItem is not Field field || !field.CanAttemptFill) return;
        var replacement = field.Type == "Checkbox" ? (checkbox.IsChecked == true ? "true" : "false") : value.Text;
        BeginOperation(); status.Text = "Validating the changed field and its appearance…";
        try
        {
            await session.FillFormAsync(engine, page, field.Name, replacement, loadedRevision, operation!.Token);
            working = false; DialogResult = true;
        }
        catch (OperationCanceledException) { status.Text = "Filling was cancelled. No change was accepted."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { EndOperation(); }
    }
    void CancelOrClose()
    {
        if (working) { closeAfterCancellation = true; operation?.Cancel(); }
        else DialogResult = false;
    }
    sealed record Field(string Name, string Type, string Value, bool ReadOnly, bool Password)
    {
        public bool Checked => Value.Length > 0 && !string.Equals(Value, "Off", StringComparison.OrdinalIgnoreCase) && !string.Equals(Value, "/Off", StringComparison.OrdinalIgnoreCase);
        public bool CanAttemptFill => Name.Length > 0 && !ReadOnly &&
            (Type == "Text" && !Password || Type == "Checkbox");
        public string Label => $"{(Name.Length == 0 ? "Unnamed field" : Name)} · {Type}{(ReadOnly ? " · read only" : "")}{(Password ? " · password" : "")}";
    }
}
