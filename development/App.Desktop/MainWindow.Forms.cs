using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>The Fields panel: every form field in the document, grouped by page, edited together and applied as one undoable change.</summary>
public partial class MainWindow
{
    sealed class FieldEditor(FormFieldInfo field, FrameworkElement control, Func<string[]?> read)
    {
        public FormFieldInfo Field { get; } = field;
        public FrameworkElement Control { get; } = control;
        /// <summary>The edited value, or null when it is unchanged.</summary>
        public Func<string[]?> Read { get; } = read;
    }

    (OpenDocumentTab Tab, long Revision, FormFieldList List)? loadedFields;
    readonly List<FieldEditor> fieldEditors = new();

    internal IReadOnlyList<string> FieldEditorNames => fieldEditors.Select(e => e.Field.FullName).ToArray();

    async void FieldsTab_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != FieldsTab || active is null || busy) return;
        if (loadedFields is { } loaded && loaded.Tab == active && loaded.Revision == active.Session.Revision) return;
        await RunUiAsync(LoadFieldsAsync, "Reading form fields…");
    }

    async void ReloadFields_Click(object sender, RoutedEventArgs e) => await RunUiAsync(LoadFieldsAsync, "Reading form fields…");

    internal async Task LoadFieldsAsync()
    {
        if (active is not { } tab) return;
        FieldsHost.Children.Clear(); fieldEditors.Clear(); loadedFields = null;
        if (!AdvancedEngineInstalled) { FieldsStatus.Text = "Form filling needs the companion engine, which is not installed."; return; }
        var revision = tab.Session.Revision;
        var list = await tab.Session.ListAllFormFieldsAsync(AdvancedEngine);
        if (active != tab || tab.Session.Revision != revision) return;
        loadedFields = (tab, revision, list);
        BuildFieldEditors(list);
        FieldsStatus.Text = list.XfaOnly ? "This is an XFA form. Its fields cannot be filled here."
            : list.HasXfa ? "This form also contains XFA data. Filling is refused to keep the two versions consistent."
            : list.FieldCount == 0 ? "This PDF has no form fields."
            : $"{list.FieldCount} field{(list.FieldCount == 1 ? "" : "s")}{(list.Truncated ? " (first ones shown)" : "")}. Change values, then Apply. Calculations and scripts in the form are not run.";
        ApplyFieldsButton.IsEnabled = list.FieldCount > 0 && !list.HasXfa;
    }

    void BuildFieldEditors(FormFieldList list)
    {
        foreach (var group in list.Fields.GroupBy(f => f.Widgets.Count == 0 ? -1 : f.Widgets.Min(w => w.Page)).OrderBy(g => g.Key))
        {
            FieldsHost.Children.Add(new TextBlock
            {
                Text = group.Key < 0 ? "Other fields" : "Page " + (group.Key + 1), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4)
            });
            foreach (var field in group) FieldsHost.Children.Add(BuildFieldEditor(field));
        }
    }

    FrameworkElement BuildFieldEditor(FormFieldInfo field)
    {
        var label = field.FullName + (field.Required ? " *" : "");
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6), Tag = field.FullName };
        var editable = !field.ReadOnly && field.Type is "Text" or "Checkbox" or "Radio" or "Combo" or "List" && !(field.Type == "Text" && field.Password);
        FrameworkElement control;
        Func<string[]?> read;
        switch (field.Type)
        {
            case "Checkbox":
                var check = new CheckBox { Content = label, IsChecked = field.Value == "true" };
                control = check;
                read = () => (check.IsChecked == true ? "true" : "false") == field.Value ? null : [check.IsChecked == true ? "true" : "false"];
                break;
            case "Radio" or "Combo" when !field.Editable || field.Type == "Radio":
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                var choice = new ComboBox { ItemsSource = (field.Type == "Radio" ? new[] { new FormOption("", "(none)") } : []).Concat(field.Options).ToArray(), DisplayMemberPath = nameof(FormOption.Display) };
                choice.SelectedItem = ((IEnumerable<FormOption>)choice.ItemsSource).FirstOrDefault(o => o.Export == field.Value || o.Display == field.Value);
                control = choice;
                read = () => choice.SelectedItem is FormOption o && o.Export != field.Value ? [o.Export] : null;
                break;
            case "Combo":
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                var editableChoice = new ComboBox { IsEditable = true, ItemsSource = field.Options.Select(o => o.Display).ToArray(), Text = field.Value };
                control = editableChoice;
                read = () => editableChoice.Text == field.Value ? null : [field.Options.FirstOrDefault(o => o.Display == editableChoice.Text)?.Export ?? editableChoice.Text];
                break;
            case "List":
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                var listBox = new ListBox
                {
                    ItemsSource = field.Options, DisplayMemberPath = nameof(FormOption.Display), MaxHeight = 120,
                    SelectionMode = field.MultiSelect ? SelectionMode.Multiple : SelectionMode.Single
                };
                foreach (var option in field.Options.Where(o => field.Selected.Contains(o.Export) || field.Selected.Contains(o.Display))) listBox.SelectedItems.Add(option);
                control = listBox;
                read = () =>
                {
                    var now = listBox.SelectedItems.OfType<FormOption>().Select(o => o.Export).ToArray();
                    var before = field.Options.Where(o => field.Selected.Contains(o.Export) || field.Selected.Contains(o.Display)).Select(o => o.Export).ToArray();
                    return now.Length > 0 && !now.Order().SequenceEqual(before.Order()) ? now : null;
                };
                break;
            case "Text":
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                var box = new TextBox { Text = field.Password ? "" : field.Value, AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = field.Multiline ? 60 : 0, MaxLength = field.MaxLength > 0 ? field.MaxLength : 4096 };
                control = box;
                read = () => box.Text == field.Value || field.ValueTruncated && box.Text.Length == field.Value.Length ? null : [box.Text];
                break;
            default:
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                control = new TextBlock { Text = field.Type == "Signature" ? "Signature field. Use Forms & sign to add a signature." : "Button (not editable here).",
                    TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") };
                read = () => null;
                break;
        }
        control.IsEnabled = editable || control is TextBlock;
        AutomationProperties.SetName(control, field.FullName + " form field");
        if (field.ReadOnly) control.ToolTip = "Read-only field";
        else if (field.Calculated) control.ToolTip = "This field is calculated by the form in other PDF apps; calculations are not run here.";
        panel.Children.Add(control);
        fieldEditors.Add(new FieldEditor(field, control, editable ? read : () => null));
        return panel;
    }

    internal async Task ApplyFieldsAsync()
    {
        if (active is not { } tab || loadedFields is not { } loaded || loaded.Tab != tab) return;
        if (loaded.Revision != tab.Session.Revision) { FieldsStatus.Text = "The document changed. Reload the fields and enter the values again."; return; }
        var values = fieldEditors.Select(e => (e.Field.FullName, Value: e.Read())).Where(p => p.Value is not null)
            .ToDictionary(p => p.FullName, p => p.Value!, StringComparer.Ordinal);
        if (values.Count == 0) { FieldsStatus.Text = "No values changed."; return; }
        if (!await AllowEditingAsync(tab)) return;
        var result = await tab.Session.FillFormFieldsAsync(AdvancedEngine, values, loaded.Revision);
        notice = $"{result.Changed} field{(result.Changed == 1 ? "" : "s")} filled. Ctrl+Z undoes it." + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "");
        await LoadFieldsAsync();
        if (result.Warnings.Count > 0) FieldsStatus.Text = string.Join(" ", result.Warnings);
    }

    async void ApplyFields_Click(object sender, RoutedEventArgs e) => await RunUiAsync(ApplyFieldsAsync, "Filling form fields…");

    /// <summary>A click on a form widget in Select mode moves the Fields panel to that field.</summary>
    bool FocusFieldAt(OpenDocumentTab tab, int pageIndex, Point position, double viewWidth, double viewHeight)
    {
        if (loadedFields is not { } loaded || loaded.Tab != tab || loaded.Revision != tab.Session.Revision || pageIndex >= tab.Pages.Count) return false;
        var page = tab.Pages[pageIndex];
        var point = PageObjectHitTest.ToPdf(position.X, position.Y, page.Width, page.Height, viewWidth, viewHeight, page.Rotation, page.OriginX, page.OriginY);
        var (x, y) = (point.X, point.Y);
        var editor = fieldEditors.FirstOrDefault(e => e.Field.Widgets.Any(w => w.Page == pageIndex && x >= w.Left && x <= w.Right && y >= w.Bottom && y <= w.Top));
        if (editor is null) return false;
        if (NavigationToggle.IsChecked != true) { NavigationToggle.IsChecked = true; ApplyPanelVisibility(); }
        FieldsTab.IsSelected = true;
        editor.Control.BringIntoView();
        editor.Control.Focus();
        return true;
    }

    internal bool FocusFieldForTest(OpenDocumentTab tab, int pageIndex, Point position, double viewWidth, double viewHeight) =>
        FocusFieldAt(tab, pageIndex, position, viewWidth, viewHeight);

    internal void SetFieldValueForTest(string name, object value)
    {
        var control = fieldEditors.First(e => e.Field.FullName == name).Control;
        switch (control)
        {
            case CheckBox check: check.IsChecked = (bool)value; break;
            case ComboBox { IsEditable: true } combo: combo.Text = (string)value; break;
            case ComboBox combo: combo.SelectedItem = ((IEnumerable<FormOption>)combo.ItemsSource).First(o => o.Export == (string)value || o.Display == (string)value); break;
            case ListBox list:
                list.SelectedItems.Clear();
                foreach (var option in ((IEnumerable<FormOption>)list.ItemsSource).Where(o => ((string[])value).Contains(o.Export))) list.SelectedItems.Add(option);
                break;
            case TextBox box: box.Text = (string)value; break;
        }
    }
}
