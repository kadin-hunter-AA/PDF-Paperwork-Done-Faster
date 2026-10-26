using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class ObjectEditorWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    string? selectedObjectId;
    readonly ListBox objects = new() { DisplayMemberPath = "Text", MinHeight = 130 };
    readonly TextBox replacement = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 88 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
    readonly Image pageImage = new() { Stretch = Stretch.Uniform, MaxWidth = 420, MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Image candidateImage = new() { Stretch = Stretch.Uniform, MaxWidth = 420, MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left, Visibility=Visibility.Collapsed };
    readonly TextBlock previewDetails = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) };
    CancellationTokenSource? previewCancellation;
    int previewWidth=1000, previewHeight=1000;
    readonly Button commit = new() { Content = "Replace selected text", MinHeight = 44, IsEnabled = false };
    bool working;
    public ObjectEditorWindow(DocumentSession session,int page, AdvancedEngineLocation? mappedEngine = null, string? selectedObjectId = null)
    {
        this.session = session; this.page = page; this.selectedObjectId = selectedObjectId;
        Title = $"Edit existing text — page {page+1}"; Width = 760; Height = 580; MinWidth = 560; MinHeight = 480; FontSize = 16;
        SetResourceReference(BackgroundProperty,"AppBackground"); SetResourceReference(ForegroundProperty,"TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = $"Edit existing text · page {page+1}", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Select a real text object below. Replacement changes that object and keeps its position. This is not permanent redaction.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) });
        if (mappedEngine is not null)
        {
            var more = new Button { Content = "More PDFs: nested text and embedded fonts…", MinHeight = 44 };
            header.Children.Add(more);
            more.Click += (_, _) =>
            {
                if (working) return;
                previewCancellation?.Cancel();
                var editor = new StructuredTextWindow(session, page, mappedEngine) { Owner = this };
                if (editor.ShowDialog() == true) DialogResult = true;
            };
        }
        var footer = new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "Replacement text", Margin = new Thickness(0,12,0,6) }); footer.Children.Add(replacement); footer.Children.Add(status); footer.Children.Add(commit);
        System.Windows.Automation.AutomationProperties.SetName(commit,"Replace selected text");
        var content = new Grid { Margin = new Thickness(0,8,0,0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star), MinWidth=200 });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1,GridUnitType.Star), MinWidth=220 });
        objects.MinWidth = 190;
        content.Children.Add(objects);
        var preview = new StackPanel { Margin = new Thickness(18,0,0,0) };
        var previewScroll = new ScrollViewer { Content=preview, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        Grid.SetColumn(previewScroll,1); content.Children.Add(previewScroll);
        preview.Children.Add(new TextBlock { Text="Committed page", FontWeight=FontWeights.SemiBold });
        preview.Children.Add(new TextBlock { Text="The page image shows the current document. The isolated candidate render updates as you type; it does not change this document.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,4,0,8) });
        preview.Children.Add(pageImage);
        System.Windows.Automation.AutomationProperties.SetName(pageImage,"Rendered current committed PDF page");
        preview.Children.Add(new TextBlock { Text="Isolated candidate page render", FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,14,0,0) });
        preview.Children.Add(candidateImage);
        System.Windows.Automation.AutomationProperties.SetName(candidateImage,"Isolated rendered page with proposed text replacement");
        preview.Children.Add(previewDetails);
        root.Children.Add(content);
        System.Windows.Automation.AutomationProperties.SetName(objects,"Existing text objects on the current page");
        System.Windows.Automation.AutomationProperties.SetName(replacement,"Replacement text for selected PDF object");
        objects.SelectionChanged += async (_,_) => { if(objects.SelectedItem is TextObject item) { replacement.Text = item.Text; status.Text = item.CanEdit ? "Review the replacement before applying. Save As preserves your original; Undo restores this edit." : item.Reason; commit.IsEnabled = item.CanEdit; UpdateDraftPreview(item); await RenderCandidateAsync(item); } };
        replacement.TextChanged += async (_,_) => { if(objects.SelectedItem is TextObject item) { UpdateDraftPreview(item); await RenderCandidateAsync(item); } };
        commit.Click += async (_,_) => await CommitAsync();
        Loaded += async (_,_) => { if(Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } await LoadAsync(); };
        Closing += (_,e) => { if(working) e.Cancel = true; else { previewCancellation?.Cancel(); previewCancellation=null; } };
    }
    async Task LoadAsync()
    {
        var wasWorking = working;
        working = true;
        try { var data = await session.ExtractAsync(page); objects.ItemsSource = data.GetProperty("Objects").EnumerateArray()
            .Where(o=>o.GetProperty("Type").GetInt32()==1 && o.TryGetProperty("Text",out _))
            .Select(o=>new TextObject(o.GetProperty("Id").GetString()!,o.GetProperty("Text").GetString() ?? "",o.GetProperty("CanEdit").GetBoolean(),o.GetProperty("EditReason").GetString() ?? "This text object is unsupported.",
                o.TryGetProperty("FontName",out var fn)?fn.GetString()??"":"",o.TryGetProperty("FontSize",out var fs)?fs.GetSingle():0,
                o.TryGetProperty("HasBounds",out var hasBounds)&&hasBounds.GetBoolean(),
                o.TryGetProperty("HasBounds",out var hb)&&hb.GetBoolean()?o.GetProperty("Left").GetSingle():0,
                o.TryGetProperty("HasBounds",out hb)&&hb.GetBoolean()?o.GetProperty("Bottom").GetSingle():0,
                o.TryGetProperty("HasBounds",out hb)&&hb.GetBoolean()?o.GetProperty("Right").GetSingle():0,
                o.TryGetProperty("HasBounds",out hb)&&hb.GetBoolean()?o.GetProperty("Top").GetSingle():0)).ToArray();
            var scale=Math.Min(1200d/Math.Max(1,data.GetProperty("Width").GetDouble()),Math.Min(1000d/Math.Max(1,data.GetProperty("Height").GetDouble()),Math.Sqrt(1_500_000d/Math.Max(1,data.GetProperty("Width").GetDouble()*data.GetProperty("Height").GetDouble()))));
            previewWidth=Math.Max(1,(int)(data.GetProperty("Width").GetDouble()*scale)); previewHeight=Math.Max(1,(int)(data.GetProperty("Height").GetDouble()*scale));
            var rendered=await session.RenderAsync(page,previewWidth,previewHeight);
            pageImage.Source=BitmapSource.Create(rendered.GetProperty("Width").GetInt32(),rendered.GetProperty("Height").GetInt32(),96,96,PixelFormats.Bgr32,null,Convert.FromBase64String(rendered.GetProperty("Bgra").GetString()!),rendered.GetProperty("Stride").GetInt32());
            if(pageImage.Source.CanFreeze) pageImage.Source.Freeze();
            status.Text = objects.Items.Count == 0 ? "No supported top-level text objects were found. Scans and outlined lettering need separate tools." : "Choose a text object to edit. Nested text and unsupported fonts are rejected safely.";
        } catch(Exception ex) { status.Text = ex.Message; } finally { working = wasWorking; }
        if (selectedObjectId is not null)
        {
            var selected = objects.Items.Cast<TextObject>().FirstOrDefault(item => string.Equals(item.Id, selectedObjectId, StringComparison.Ordinal));
            selectedObjectId = null;
            if (selected is not null) objects.SelectedItem = selected;
            else status.Text = "The clicked text object is no longer available. Choose an object from the list.";
        }
    }
    async Task CommitAsync()
    {
        if(working || objects.SelectedItem is not TextObject item || !item.CanEdit) return;
        previewCancellation?.Cancel(); previewCancellation=null; candidateImage.Source=null; candidateImage.Visibility=Visibility.Collapsed;
        working = true; commit.IsEnabled = false; objects.IsEnabled = false; replacement.IsEnabled = false;
        try { await session.ReplaceTextAsync(page,item.Id,replacement.Text); working = false; DialogResult = true; }
        catch(Exception ex) {
            var draft = replacement.Text;
            await LoadAsync();
            // Worker object IDs may be regenerated by rollback. Prefer the refreshed index, then
            // use the full extracted object signature only when it identifies exactly one object.
            // Text alone is never an identity because pages can contain repeated words/runs.
            var objectIndex = ParseObjectIndex(item.Id);
            var refreshed = objects.Items.Cast<TextObject>().ToArray();
            var sameIndex = refreshed.FirstOrDefault(o=>ParseObjectIndex(o.Id)==objectIndex);
            var sameSignature = refreshed.Where(o=>SameObjectSignature(item,o)).ToArray();
            objects.SelectedItem = sameIndex is not null && SameObjectSignature(item,sameIndex)
                ? sameIndex
                : sameSignature.Length == 1 ? sameSignature[0] : null;
            replacement.Text = draft;
            status.Text = objects.SelectedItem is null
                ? ex.Message + " The prior object could not be identified uniquely after refresh; select it again from the list."
                : ex.Message;
        }
        finally { working = false; commit.IsEnabled = objects.SelectedItem is TextObject selected && selected.CanEdit; objects.IsEnabled = true; replacement.IsEnabled = true; }
    }
    sealed record TextObject(string Id,string Text,bool CanEdit,string Reason,string FontName,float FontSize,bool HasBounds,float Left,float Bottom,float Right,float Top)
    { public string AutomationName => Text; public string FullPath => ""; }
    static int ParseObjectIndex(string id)=>int.TryParse(id.Split(':').LastOrDefault(),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var index)?index:-1;
    static bool SameObjectSignature(TextObject a,TextObject b)=>a.Text==b.Text&&a.FontName==b.FontName&&a.FontSize==b.FontSize&&a.HasBounds==b.HasBounds&&
        (!a.HasBounds||(Math.Abs(a.Left-b.Left)<0.01f&&Math.Abs(a.Bottom-b.Bottom)<0.01f&&Math.Abs(a.Right-b.Right)<0.01f&&Math.Abs(a.Top-b.Top)<0.01f));
    void UpdateDraftPreview(TextObject item)
    {
        var font=item.FontName.Contains("Times",StringComparison.OrdinalIgnoreCase)?"Times New Roman":item.FontName.Contains("Courier",StringComparison.OrdinalIgnoreCase)?"Courier New":"Arial";
        previewDetails.Text=item.HasBounds?$"Object bounds: {item.Left:0.##}, {item.Bottom:0.##} to {item.Right:0.##}, {item.Top:0.##} pt · {font}, {item.FontSize:0.##} pt":"Object bounds are unavailable.";
    }
    async Task RenderCandidateAsync(TextObject item)
    {
        previewCancellation?.Cancel();
        candidateImage.Source=null; candidateImage.Visibility=Visibility.Collapsed;
        if(working || !item.CanEdit || string.IsNullOrWhiteSpace(replacement.Text)) return;
        var cts=new CancellationTokenSource(); previewCancellation=cts;
        try
        {
            var result=await session.PreviewTextAsync(page,item.Id,replacement.Text,previewWidth,previewHeight,cts.Token);
            if(cts.IsCancellationRequested || !ReferenceEquals(previewCancellation,cts)) return;
            var image=BitmapSource.Create(result.GetProperty("Width").GetInt32(),result.GetProperty("Height").GetInt32(),96,96,PixelFormats.Bgr32,null,Convert.FromBase64String(result.GetProperty("Bgra").GetString()!),result.GetProperty("Stride").GetInt32());
            if(image.CanFreeze) image.Freeze(); candidateImage.Source=image; candidateImage.Visibility=Visibility.Visible;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!cts.IsCancellationRequested) previewDetails.Text+=$" · Candidate preview unavailable: {ex.Message}"; }
        finally { if(ReferenceEquals(previewCancellation,cts)) previewCancellation=null; cts.Dispose(); }
    }
}
