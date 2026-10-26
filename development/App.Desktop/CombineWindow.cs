using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

public sealed class CombineWindow : Window
{
    public string? OutputPath { get; private set; }
    readonly ObservableCollection<InputFile> inputs = new();
    readonly ListBox list = new() { MinHeight = 180 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    readonly Button cancel = new() { Content = "Cancel job", MinHeight = 44, Padding = new Thickness(16, 6, 16, 6), IsEnabled = false };
    readonly CheckBox keepBookmarks = new() { Content = "Keep bookmarks (one bookmark per file, with its own bookmarks inside)", Margin = new Thickness(0, 0, 0, 12) };
    readonly AdvancedEngineLocation? engine = PageToolsEngine.Installed();
    CancellationTokenSource? job;
    public CombineWindow()
    {
        Title = "Combine PDFs"; Width = 800; Height = 580; MinWidth = 600; MinHeight = 480;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 16;
        Loaded += (_,_) => { if(Owner is not null) Resources.MergedDictionaries.Add(Owner.Resources); };
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/App.Desktop;component/Assets/PdfEditor-256.png"));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Combine PDFs", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Add files, arrange their order, then save a new PDF. Original files stay intact.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) });
        header.Children.Add(actions);
        Button Action(string title, RoutedEventHandler handler) { var b = new Button { Content = title, MinHeight = 44, Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,8,0) }; b.Click += handler; actions.Children.Add(b); return b; }
        Action("Add PDFs", async (_,_) => await AddAsync());
        Action("Move up", (_,_) => Move(-1)); Action("Move down", (_,_) => Move(1));
        Action("Remove", (_,_) => { if(list.SelectedItem is InputFile f) inputs.Remove(f); UpdateCount(); });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(new TextBlock { Text = "Page content is imported. Unless Keep bookmarks is checked, document bookmarks are not preserved. Forms, attachments and links are not promised to survive, and digital signatures never do. Signed and encrypted inputs are unsupported.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        if(engine is null) { keepBookmarks.IsEnabled = false; keepBookmarks.ToolTip = "Needs the companion PDF engine, which is not installed with this copy of the app."; keepBookmarks.Content += " - unavailable: the companion PDF engine is not installed"; }
        AutomationProperties.SetName(keepBookmarks, "Keep bookmarks in the combined PDF"); footer.Children.Add(keepBookmarks);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal }; footer.Children.Add(bottom);
        var save = new Button { Content = "Save combined PDF…", MinHeight = 44, Padding = new Thickness(16,6,16,6), Margin = new Thickness(0,0,12,0) }; save.Click += async (_,_) => await CombineAsync(); bottom.Children.Add(save);
        cancel.Click += (_,_) => job?.Cancel(); bottom.Children.Add(cancel);
        list.ItemsSource = inputs; list.ItemTemplate = BuildInputTemplate(); list.SetResourceReference(Control.BackgroundProperty,"PanelBrush"); list.SetResourceReference(Control.ForegroundProperty,"TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(list,"PDF files in output order"); root.Children.Add(list);
        Closing += (_,e) => { if(job is not null) { job.Cancel(); e.Cancel = true; status.Text = "Cancelling… Close the window after the job stops."; } };
        UpdateCount();
    }
    DataTemplate BuildInputTemplate()
    {
        var card = new FrameworkElementFactory(typeof(StackPanel)); card.SetValue(FrameworkElement.MarginProperty, new Thickness(8));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(InputFile.Label))); title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); card.AppendChild(title);
        var row = new FrameworkElementFactory(typeof(DockPanel)); row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, "Pages"); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); label.SetValue(FrameworkElement.MinWidthProperty, 58d); label.SetValue(DockPanel.DockProperty, Dock.Left); row.AppendChild(label);
        var range = new FrameworkElementFactory(typeof(TextBox)); range.SetValue(FrameworkElement.MinHeightProperty, 44d); range.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center); range.SetValue(Control.PaddingProperty, new Thickness(8, 4, 8, 4)); range.SetBinding(TextBox.TextProperty, new Binding(nameof(InputFile.PageRange)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); range.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(InputFile.RangeAutomationName))); range.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(InputFile.RangeHelpText))); range.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateCount())); row.AppendChild(range); card.AppendChild(row);
        var hint = new FrameworkElementFactory(typeof(TextBlock)); hint.SetValue(TextBlock.TextProperty, "Use pages like 1,3-5. Leave blank to include all pages."); hint.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); hint.SetValue(FrameworkElement.MarginProperty, new Thickness(58, 4, 0, 0)); hint.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); card.AppendChild(hint);
        var error = new FrameworkElementFactory(typeof(TextBlock)); error.SetBinding(TextBlock.TextProperty, new Binding(nameof(InputFile.RangeError)) { StringFormat = "{0}" }); error.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); error.SetValue(FrameworkElement.MarginProperty, new Thickness(58, 2, 0, 0)); error.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); card.AppendChild(error);
        return new DataTemplate { VisualTree = card };
    }
    WorkerClient Worker() { var w = new WorkerClient(Path.Combine(AppContext.BaseDirectory,"worker","App.Worker.exe")); w.Start(); return w; }
    bool ChoosePath(Microsoft.Win32.CommonDialog picker)
    {
        try { return picker.ShowDialog(this) == true; }
        catch(Exception ex) { status.Text = ex.Message; return false; }
    }
    async Task AddAsync()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf", Multiselect = true };
        if(!ChoosePath(picker)) return;
        actions.IsEnabled = false;
        try { using var worker = Worker(); foreach(var path in picker.FileNames) {
            var result = await worker.SendAsync(Wire.Create(Guid.NewGuid(),0,"InspectCombineInput",new { Path = path }));
            if(!result.Success) throw new InvalidOperationException($"Cannot add {Path.GetFileName(path)}: {result.Error}");
            inputs.Add(new InputFile(path,result.Data.GetProperty("PageCount").GetInt32()));
        } UpdateCount(); } catch(Exception ex) { status.Text = ex.Message; } finally { actions.IsEnabled = true; }
    }
    void Move(int offset) { var i = list.SelectedIndex; if(i >= 0 && i+offset >= 0 && i+offset < inputs.Count) { inputs.Move(i,i+offset); list.SelectedIndex = i+offset; } }
    void UpdateCount()
    {
        var invalid = inputs.Where(f => f.RangeError.Length > 0).ToArray();
        status.Text = invalid.Length == 0
            ? $"{inputs.Count} files · {inputs.Sum(f => (long)f.SelectedPageCount)} selected pages. Output follows the listed order. Blank ranges include every page."
            : $"Fix page ranges for: {string.Join(", ", invalid.Select(f => System.IO.Path.GetFileName(f.Path)))}.";
    }
    async Task CombineAsync()
    {
        if(job is not null) return;
        if(inputs.Count < 2) { status.Text = "Add at least two PDFs."; return; }
        UpdateCount();
        var invalid = inputs.FirstOrDefault(f => f.RangeError.Length > 0);
        if(invalid is not null) { status.Text = $"{System.IO.Path.GetFileName(invalid.Path)}: {invalid.RangeError}"; list.SelectedItem = invalid; return; }
        if(inputs.Sum(f => (long)f.SelectedPageCount) > 50_000) { status.Text = "Combine supports up to 50,000 selected pages per output."; return; }
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PDF files|*.pdf", DefaultExt = ".pdf", FileName = "Combined.pdf", OverwritePrompt = true };
        if(!ChoosePath(picker)) return;
        if(File.Exists(picker.FileName)) { status.Text = "Choose a new filename. Combine never replaces an existing file."; return; }
        job = new CancellationTokenSource(); actions.IsEnabled = false; list.IsEnabled = false; cancel.IsEnabled = true; status.Text = "Combining and validating saved pages…";
        try {
            if(keepBookmarks.IsChecked == true && engine is not null) {
                var kept = await CombineWithBookmarks.RunAsync(engine, Path.Combine(AppContext.BaseDirectory,"worker","App.Worker.exe"), inputs.Select(f => new CombineInput(f.Path, f.PageRange.Trim())).ToArray(), picker.FileName, true, job.Token);
                OutputPath = kept.Path; status.Text = $"Saved {kept.PageCount} pages with bookmarks to {OutputPath}";
                if(MessageBox.Show(this,"Open the combined PDF?","PDF saved",MessageBoxButton.YesNo,MessageBoxImage.Information) == MessageBoxResult.Yes) { job.Dispose(); job = null; DialogResult = true; }
                return;
            }
            using var worker = Worker();
            var result = await worker.SendAsync(Wire.Create(Guid.NewGuid(),0,"Combine",new { Inputs = inputs.Select(f=>new { f.Path, Range = f.PageRange.Trim() }).ToArray(), Path = picker.FileName }),job.Token);
            if(!result.Success) throw new InvalidOperationException(result.Error);
            OutputPath = picker.FileName; status.Text = $"Saved {result.Data.GetProperty("PageCount").GetInt32()} pages to {OutputPath}";
            if(MessageBox.Show(this,"Open the combined PDF?","PDF saved",MessageBoxButton.YesNo,MessageBoxImage.Information) == MessageBoxResult.Yes) { job.Dispose(); job = null; DialogResult = true; }
        } catch(OperationCanceledException) { status.Text = "Job cancelled. Check the chosen output path if cancellation occurred during final save."; }
        catch(Exception ex) { status.Text = ex.Message; }
        finally { job?.Dispose(); job = null; actions.IsEnabled = true; list.IsEnabled = true; cancel.IsEnabled = false; }
    }
    public static string ValidatePageRange(string range, int pageCount)
    {
        if(string.IsNullOrWhiteSpace(range)) return pageCount > 50_000 ? "This input exceeds the 50,000-page Combine limit." : "";
        var seen = new HashSet<int>();
        foreach(var raw in range.Split(',')) {
            var part = raw.Trim(); if(part.Length == 0) return "A page range is empty or malformed.";
            var ends = part.Split('-');
            if(ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1) return "Use page numbers such as 1,3-5; page numbers start at 1.";
            var last = first;
            if(ends.Length == 2 && (!int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first)) return "Use ascending page ranges such as 2-5.";
            if(last > pageCount) return $"Page {last} is outside this PDF, which has {pageCount} pages.";
            if((long)seen.Count + last - first + 1 > 50_000) return "A range exceeds the 50,000-page Combine limit.";
            for(long page = first; page <= last; page++) if(!seen.Add((int)page)) return $"Page {page} appears more than once in this range.";
        }
        return "";
    }
    sealed class InputFile : INotifyPropertyChanged {
        string pageRange = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public InputFile(string path,int pages) { Path = path; Pages = pages; }
        public string Path { get; }
        public int Pages { get; }
        public string PageRange { get => pageRange; set { if(pageRange == value) return; pageRange = value; Changed(nameof(PageRange)); Changed(nameof(RangeError)); Changed(nameof(RangeHelpText)); Changed(nameof(SelectedPageCount)); } }
        public string RangeError => ValidatePageRange(PageRange, Pages);
        public int SelectedPageCount => string.IsNullOrWhiteSpace(PageRange) ? Pages : RangeError.Length == 0 ? PageRange.Split(',').Sum(part => { var ends=part.Trim().Split('-'); var first=int.Parse(ends[0],CultureInfo.InvariantCulture); var last=ends.Length==1?first:int.Parse(ends[1],CultureInfo.InvariantCulture); return last-first+1; }) : 0;
        public string Label => $"{System.IO.Path.GetFileName(Path)} — {Pages} pages\n{Path}";
        public string RangeAutomationName => $"Page range for {System.IO.Path.GetFileName(Path)}; blank means all {Pages} pages";
        public string RangeHelpText => RangeError.Length == 0 ? $"Enter pages such as 1,3-5, or leave blank for all {Pages} pages." : RangeError;
        void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

