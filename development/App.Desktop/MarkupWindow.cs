using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class MarkupWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    readonly object[] initialQuads;
    readonly ComboBox kind = new() { MinHeight=44, MinWidth=180 };
    readonly TextBox left = CoordinateBox(), bottom = CoordinateBox(), right = CoordinateBox(), top = CoordinateBox();
    readonly TextBox comment = new() { MinHeight=88, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Margin=new Thickness(0,0,0,8) };
    readonly TextBlock commentLabel = new() { Text="Sticky note comment", FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,10,0,4) };
    readonly TextBlock notesHeader = new() { Text="Existing sticky notes (loading…)", FontWeight=FontWeights.SemiBold };
    readonly ListBox existingNotes = new() { MaxHeight=144, MinHeight=64, Margin=new Thickness(0,4,0,6) };
    readonly TextBox existingNoteText = new() { MinHeight=80, MaxHeight=140, IsReadOnly=true, TextWrapping=TextWrapping.Wrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto, AcceptsReturn=true };
    readonly ListBox rectangles = new() { MinHeight=90 };
    readonly TextBlock status = new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,10,0,10) };
    readonly Button add = new() { Content="Add rectangle", MinHeight=44 };
    readonly Button apply = new() { Content="Apply markup", MinHeight=44, IsEnabled=false };
    readonly Button manage = new() { Content="Edit or delete existing annotations…", MinHeight=44, Margin=new Thickness(0,0,0,12) };
    readonly List<RectangleSpec> entries = new();
    bool working;

    public MarkupWindow(DocumentSession session,int page,object[]? initialQuads=null)
    {
        this.session=session; this.page=page; this.initialQuads=initialQuads??Array.Empty<object>();
        Title=$"Add annotation — page {page+1}"; Width=620; Height=760; MinWidth=500; MinHeight=620; FontSize=16;
        SetResourceReference(BackgroundProperty,"AppBackground"); SetResourceReference(ForegroundProperty,"TextBrush");
        notesHeader.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
        var root=new DockPanel { Margin=new Thickness(24) }; Content=root;
        var header=new StackPanel(); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text=$"Add annotation · page {page+1}", FontSize=24, FontWeight=FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text="Add text markup or a sticky note to the current page. Annotations do not remove or permanently redact page content. Notes use PDF page coordinates and are not movable or resizable here.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,10,0,12) });
        var footer=new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status); footer.Children.Add(apply);
        var body=new StackPanel();
        root.Children.Add(new ScrollViewer { Content=body, VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        existingNotes.DisplayMemberPath=nameof(ExistingNote.Label);
        System.Windows.Automation.AutomationProperties.SetName(existingNotes,"Existing sticky notes on current page");
        System.Windows.Automation.AutomationProperties.SetName(existingNoteText,"Selected sticky note comment, read only");
        var notesPanel=new StackPanel { Margin=new Thickness(0,4,0,8) };
        notesPanel.Children.Add(existingNotes);
        notesPanel.Children.Add(existingNoteText);
        var notesExpander=new Expander { Header=notesHeader, Content=notesPanel, IsExpanded=false, Margin=new Thickness(0,0,0,8) };
        System.Windows.Automation.AutomationProperties.SetName(notesExpander,"Read existing sticky notes on current page");
        body.Children.Add(notesExpander);
        System.Windows.Automation.AutomationProperties.SetName(manage,"Edit or delete existing annotations on this page");
        body.Children.Add(manage);
        var drawings = new Button { Content = "Add line, rectangle or ellipse…", MinHeight = 44, Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(drawings, "Add a native PDF line, rectangle or ellipse annotation");
        body.Children.Add(drawings);
        drawings.Click += (_, _) =>
        {
            if (working) return;
            var window = new DrawingWindow(session, page) { Owner = this };
            if (window.ShowDialog() == true) DialogResult = true;
        };
        manage.Click+=(_,_)=>
        {
            if(working)return;
            var manager=new AnnotationManagerWindow(session,page) { Owner=this };
            if(manager.ShowDialog()==true) DialogResult=true;
        };
        body.Children.Add(new TextBlock { Text="Annotation type", Margin=new Thickness(0,0,0,4) });
        kind.Items.Add("Highlight"); kind.Items.Add("Underline"); kind.Items.Add("Strikeout"); kind.Items.Add("Sticky note");
        kind.SelectedIndex=0;
        System.Windows.Automation.AutomationProperties.SetName(kind,"Annotation type"); body.Children.Add(kind);
        body.Children.Add(commentLabel);
        System.Windows.Automation.AutomationProperties.SetName(comment,"Sticky note comment"); body.Children.Add(comment);
        body.Children.Add(new TextBlock { Text="Page rectangle in PDF points", FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,14,0,4) });
        var fields=new Grid();
        for(var i=0;i<4;i++) fields.ColumnDefinitions.Add(new ColumnDefinition());
        AddCoordinate(fields,"Left",left,0); AddCoordinate(fields,"Bottom",bottom,1); AddCoordinate(fields,"Right",right,2); AddCoordinate(fields,"Top",top,3);
        body.Children.Add(fields); body.Children.Add(add);
        body.Children.Add(new TextBlock { Text="Page rectangles", FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,14,0,4) });
        rectangles.DisplayMemberPath=nameof(RectangleSpec.Label); System.Windows.Automation.AutomationProperties.SetName(rectangles,"Annotation rectangles to apply"); body.Children.Add(rectangles);
        var remove=new Button { Content="Remove selected rectangle", MinHeight=44, Margin=new Thickness(0,6,0,0) }; body.Children.Add(remove);
        add.Click+=(_,_)=>AddRectangleFromFields(); remove.Click+=(_,_)=>RemoveSelected();
        rectangles.SelectionChanged+=(_,_)=>UpdateApplyEnabled();
        existingNotes.SelectionChanged+=(_,_)=>ShowSelectedNote();
        kind.SelectionChanged+=(_,_)=>UpdateTypeUi();
        comment.TextChanged+=(_,_)=>UpdateApplyEnabled();
        apply.Click+=async (_,_)=>await ApplyAsync();
        Loaded+=async (_,_)=>
        {
            if(Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon=Owner.Icon; }
            LoadInitial();
            await LoadExistingNotesAsync();
        };
        Closing+=(_,e)=>{ if(working)e.Cancel=true; };
    }

    static TextBox CoordinateBox()=>new() { MinHeight=44, Margin=new Thickness(0,0,8,0), VerticalContentAlignment=VerticalAlignment.Center };
    static void AddCoordinate(Grid grid,string label,TextBox box,int column)
    {
        var panel=new StackPanel { Margin=new Thickness(0,0,6,0) }; panel.Children.Add(new TextBlock { Text=label, Margin=new Thickness(0,0,0,4) }); panel.Children.Add(box);
        Grid.SetColumn(panel,column); grid.Children.Add(panel); System.Windows.Automation.AutomationProperties.SetName(box,$"{label} coordinate in PDF points");
    }
    void LoadInitial()
    {
        foreach(var item in initialQuads)
        {
            try
            {
                double Read(string name)=>Convert.ToDouble(item.GetType().GetProperty(name)?.GetValue(item),CultureInfo.InvariantCulture);
                entries.Add(new RectangleSpec(Read("Left"),Read("Bottom"),Read("Right"),Read("Top")));
            }
            catch { /* Invalid selection geometry can be entered manually below. */ }
        }
        RefreshEntries(); UpdateTypeUi(); status.Text=entries.Count==0?"Enter a page rectangle, or select text in the page view first.":$"{entries.Count} selected text rectangle(s) ready. You can add more rectangles manually.";
    }
    void AddRectangleFromFields()
    {
        if(working)return;
        var isNote=(string?)kind.SelectedItem=="Sticky note";
        if((isNote&&entries.Count>=1)||entries.Count>=100) { status.Text=isNote?"A sticky note uses one rectangle.":"Apply up to 100 rectangles per operation."; return; }
        if(!TryCoordinate(left.Text,out var l)||!TryCoordinate(bottom.Text,out var b)||!TryCoordinate(right.Text,out var r)||!TryCoordinate(top.Text,out var t)||r<=l||t<=b)
        { status.Text="Enter finite coordinates using decimal points, with Right greater than Left and Top greater than Bottom."; return; }
        entries.Add(new RectangleSpec(l,b,r,t)); RefreshEntries(); left.Clear(); bottom.Clear(); right.Clear(); top.Clear(); status.Text="Rectangle added. Page bounds will be checked before the annotation is applied.";
    }
    void RemoveSelected()
    {
        if(working)return;
        if(rectangles.SelectedItem is RectangleSpec selected) { entries.Remove(selected); RefreshEntries(); status.Text="Selected rectangle removed."; }
    }
    void RefreshEntries()
    {
        rectangles.ItemsSource=null; rectangles.ItemsSource=entries.ToArray(); UpdateApplyEnabled();
    }
    void UpdateTypeUi()
    {
        var isNote=(string?)kind.SelectedItem=="Sticky note";
        commentLabel.Visibility=isNote?Visibility.Visible:Visibility.Collapsed;
        comment.Visibility=isNote?Visibility.Visible:Visibility.Collapsed;
        if(isNote) add.Content="Add note rectangle";
        else add.Content="Add rectangle";
        UpdateApplyEnabled();
    }
    async Task LoadExistingNotesAsync()
    {
        existingNotes.ItemsSource=Array.Empty<ExistingNote>();
        existingNoteText.Clear();
        notesHeader.Text="Existing sticky notes (loading…)";
        try
        {
            var inspection=await session.ExtractAsync(page);
            var annotations=inspection.GetProperty("Annotations").EnumerateArray();
            var notes=annotations.Where(annotation=>annotation.GetProperty("Subtype").GetInt32()==1)
                .Select((annotation,index)=>new ExistingNote(index+1,
                    annotation.TryGetProperty("ContentsAvailable",out var available)&&!available.GetBoolean()
                        ? "[Comment exceeds this viewer's bounded read limit.]"
                        : annotation.TryGetProperty("Contents",out var contents)?contents.GetString()??"":""))
                .ToArray();
            existingNotes.ItemsSource=notes;
            notesHeader.Text=$"Existing sticky notes ({notes.Length})";
            if(inspection.TryGetProperty("AnnotationsTruncated",out var limited)&&limited.GetBoolean())
                notesHeader.Text+=" · first 1,000 annotations only";
            if(notes.Length==0) existingNoteText.Text="No sticky notes on this page.";
            else existingNotes.SelectedIndex=0;
        }
        catch(Exception ex)
        {
            notesHeader.Text="Existing sticky notes (unavailable)";
            existingNoteText.Text="Existing notes could not be loaded.";
            status.Text=$"Could not load existing sticky notes: {ex.Message}";
        }
    }
    void ShowSelectedNote()
    {
        if(existingNotes.SelectedItem is not ExistingNote note) return;
        const int maximumDisplayedCharacters=16_000;
        existingNoteText.Text=note.Content.Length<=maximumDisplayedCharacters
            ? note.Content
            : note.Content[..maximumDisplayedCharacters]+"\n\n[Comment truncated for display.]";
    }
    void UpdateApplyEnabled()=>apply.IsEnabled=!working&&entries.Count>0&&((string?)kind.SelectedItem!="Sticky note"||(!string.IsNullOrWhiteSpace(comment.Text)&&entries.Count==1));
    static bool TryCoordinate(string text,out double value)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);
    async Task ApplyAsync()
    {
        if(working||entries.Count==0)return;
        if((string?)kind.SelectedItem=="Sticky note"&&string.IsNullOrWhiteSpace(comment.Text)){status.Text="Enter the sticky note comment before applying.";return;}
        working=true; add.IsEnabled=false; manage.IsEnabled=false; kind.IsEnabled=false; rectangles.IsEnabled=false; comment.IsEnabled=false; apply.IsEnabled=false;
        try
        {
            if((string?)kind.SelectedItem=="Sticky note")
            {
                var x=entries[0]; await session.AddCommentAsync(page,comment.Text,x.Left,x.Bottom,x.Right,x.Top);
            }
            else
            {
                var quads=entries.Select(x=>(object)new { x.Left,x.Bottom,x.Right,x.Top }).ToArray();
                await session.AddMarkupAsync(page,(string)kind.SelectedItem!,quads);
            }
            working=false; DialogResult=true;
        }
        catch(Exception ex) { status.Text=ex.Message; }
        finally { working=false; add.IsEnabled=true; manage.IsEnabled=true; kind.IsEnabled=true; rectangles.IsEnabled=true; comment.IsEnabled=true; UpdateApplyEnabled(); }
    }
    sealed record RectangleSpec(double Left,double Bottom,double Right,double Top)
    { public string Label=>$"Left {Left:0.##}, Bottom {Bottom:0.##}, Right {Right:0.##}, Top {Top:0.##} pt"; }
    sealed record ExistingNote(int AnnotationNumber,string Content)
    { public string Label=>$"Sticky note {AnnotationNumber}"; }
}
