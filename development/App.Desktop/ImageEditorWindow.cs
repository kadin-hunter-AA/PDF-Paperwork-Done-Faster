using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

internal sealed class ImageEditorWindow : Window
{
    readonly DocumentSession session;
    readonly int page;
    readonly ListBox objects=new() { MinHeight=160 };
    readonly Image pageImage=new() { Stretch=System.Windows.Media.Stretch.Uniform,MaxWidth=620,MaxHeight=580,HorizontalAlignment=HorizontalAlignment.Left };
    readonly TextBox left=CoordinateBox(),bottom=CoordinateBox(),right=CoordinateBox(),top=CoordinateBox();
    readonly TextBlock status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10) };
    readonly Button replace=new() { Content="Replace selected image",MinHeight=44,IsEnabled=false };
    readonly Button transform=new() { Content="Apply placement",MinHeight=44,IsEnabled=false };
    readonly Button delete=new() { Content="Delete selected image",MinHeight=44,IsEnabled=false };
    readonly Button add=new() { Content="Add image…",MinHeight=44 };
    bool working;
    bool pageEditable;
    double pageWidth,pageHeight;

    public ImageEditorWindow(DocumentSession session,int page, string? selectedObjectId = null)
    {
        this.session=session; this.page=page;
        Title=$"Edit images — page {page+1}"; Width=960; Height=720; MinWidth=720; MinHeight=560; FontSize=16;
        SetResourceReference(BackgroundProperty,"AppBackground"); SetResourceReference(ForegroundProperty,"TextBrush");
        var root=new DockPanel { Margin=new Thickness(24) }; Content=root;
        var header=new StackPanel(); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text=$"Edit images · page {page+1}",FontSize=24,FontWeight=FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text="Select a top-level image to replace, move, resize, or delete it. Added and replaced images are embedded in the document. Rotated pages and rotated, clipped, or nested images are not supported in this workflow.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12) });
        var footer=new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer); footer.Children.Add(status);
        var content=new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(0.9,GridUnitType.Star),MinWidth=270 }); content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.1,GridUnitType.Star),MinWidth=300 });
        var leftPanel=new StackPanel(); content.Children.Add(leftPanel);
        leftPanel.Children.Add(new TextBlock { Text="Images on this page",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,4) });
        objects.DisplayMemberPath=nameof(ImageItem.Label); System.Windows.Automation.AutomationProperties.SetName(objects,"Top-level images on the current page"); leftPanel.Children.Add(objects);
        leftPanel.Children.Add(new TextBlock { Text="Placement in PDF points",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,4) });
        var coords=new Grid(); for(var i=0;i<4;i++)coords.ColumnDefinitions.Add(new ColumnDefinition());
        AddCoordinate(coords,"Left",left,0);AddCoordinate(coords,"Bottom",bottom,1);AddCoordinate(coords,"Right",right,2);AddCoordinate(coords,"Top",top,3);leftPanel.Children.Add(coords);
        var actions=new UniformGrid { Columns=2,Rows=2,Margin=new Thickness(0,8,0,0) };
        actions.Children.Add(add);actions.Children.Add(replace);actions.Children.Add(transform);actions.Children.Add(delete);leftPanel.Children.Add(actions);
        System.Windows.Automation.AutomationProperties.SetName(add,"Add image from file");
        System.Windows.Automation.AutomationProperties.SetName(replace,"Replace selected image from file");
        System.Windows.Automation.AutomationProperties.SetName(transform,"Apply selected image placement");
        System.Windows.Automation.AutomationProperties.SetName(delete,"Delete selected image");
        System.Windows.Automation.AutomationProperties.SetName(left,"Image left position in PDF points");
        System.Windows.Automation.AutomationProperties.SetName(bottom,"Image bottom position in PDF points");
        System.Windows.Automation.AutomationProperties.SetName(right,"Image right position in PDF points");
        System.Windows.Automation.AutomationProperties.SetName(top,"Image top position in PDF points");
        var pagePanel=new StackPanel { Margin=new Thickness(22,0,0,0) }; var pageScroll=new ScrollViewer { Content=pagePanel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        Grid.SetColumn(pageScroll,1);content.Children.Add(pageScroll);
        pagePanel.Children.Add(new TextBlock { Text="Committed page",FontWeight=FontWeights.SemiBold });
        pagePanel.Children.Add(new TextBlock { Text="This rendered page reflects the current committed session state.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8) });
        pagePanel.Children.Add(pageImage); System.Windows.Automation.AutomationProperties.SetName(pageImage,"Rendered current PDF page");
        root.Children.Add(content);
        objects.SelectionChanged+=(_,_)=>{ if(objects.SelectedItem is ImageItem item) { SetCoordinates(item.Left,item.Bottom,item.Right,item.Top); SetButtons(item.CanEdit); if(!item.CanEdit)status.Text=string.IsNullOrWhiteSpace(item.EditReason)?"This image cannot be safely edited in this workflow.":item.EditReason; } else SetButtons(false); };
        add.Click+=async (_,_)=>await AddImageAsync(); replace.Click+=async (_,_)=>await ReplaceImageAsync();
        transform.Click+=async (_,_)=>await TransformImageAsync(); delete.Click+=async (_,_)=>await DeleteImageAsync();
        Loaded+=async (_,_)=>{ if(Owner is not null){Resources.MergedDictionaries.Add(Owner.Resources);Icon=Owner.Icon;} await LoadPageAsync(); if(selectedObjectId is not null) objects.SelectedItem=objects.Items.Cast<ImageItem>().FirstOrDefault(x=>x.Id==selectedObjectId); };
        Closing+=(_,e)=>{if(working)e.Cancel=true;};
    }

    static TextBox CoordinateBox()=>new() { MinHeight=44,Margin=new Thickness(0,0,6,0),VerticalContentAlignment=VerticalAlignment.Center };
    static void AddCoordinate(Grid grid,string label,TextBox box,int column)
    {
        var stack=new StackPanel();stack.Children.Add(new TextBlock { Text=label,Margin=new Thickness(0,0,0,4) });stack.Children.Add(box);
        Grid.SetColumn(stack,column);grid.Children.Add(stack);System.Windows.Automation.AutomationProperties.SetName(box,$"{label} coordinate in PDF points");
    }
    async Task LoadPageAsync(int? selectObjectIndex=null)
    {
        var wasWorking=working;
        working=true;
        try
        {
            var data=await session.ExtractAsync(page); pageWidth=data.GetProperty("Width").GetDouble();pageHeight=data.GetProperty("Height").GetDouble();pageEditable=data.GetProperty("Rotation").GetInt32()==0;
            var list=data.GetProperty("Objects").EnumerateArray().Where(o=>o.GetProperty("Type").GetInt32()==3)
                .Select(o=>new ImageItem(o.GetProperty("Id").GetString()!,o.TryGetProperty("HasBounds",out var hb)&&hb.GetBoolean(),
                    Read(o,"Left"),Read(o,"Bottom"),Read(o,"Right"),Read(o,"Top"),
                    o.TryGetProperty("ImageEditReason",out var reason)?reason.GetString()??"":""))
                .ToArray();
            objects.ItemsSource=list;
            if(selectObjectIndex is int index) objects.SelectedItem=list.FirstOrDefault(x=>ParseIndex(x.Id)==index);
            if(objects.SelectedItem is not ImageItem selected)
            {
                var width=Math.Min(180,pageWidth*0.4);var height=width*0.66;
                SetCoordinates(72,72,Math.Min(pageWidth-1,72+width),Math.Min(pageHeight-1,72+height));
            }
            var scale=Math.Min(1200/Math.Max(1,pageWidth),Math.Min(1200/Math.Max(1,pageHeight),Math.Sqrt(1_500_000/Math.Max(1,pageWidth*pageHeight))));
            var rendered=await session.RenderAsync(page,Math.Max(1,(int)(pageWidth*scale)),Math.Max(1,(int)(pageHeight*scale)));
            pageImage.Source=BitmapSource.Create(rendered.GetProperty("Width").GetInt32(),rendered.GetProperty("Height").GetInt32(),96,96,System.Windows.Media.PixelFormats.Bgr32,null,
                Convert.FromBase64String(rendered.GetProperty("Bgra").GetString()!),rendered.GetProperty("Stride").GetInt32());
            if(pageImage.Source.CanFreeze) pageImage.Source.Freeze();
            status.Text=!pageEditable?"Image editing on rotated pages is not supported in this workflow.":list.Length==0?"No top-level images were found. Use Add image to insert a PNG, JPEG, BMP, or TIFF file.":$"{list.Length} top-level image(s) found. Select one to edit, or add another.";
        }
        catch(Exception ex){status.Text=ex.Message;}
        finally{working=wasWorking;SetButtons(objects.SelectedItem is ImageItem item&&item.CanEdit);}
    }
    static double Read(System.Text.Json.JsonElement o,string name)=>o.TryGetProperty(name,out var value)&&value.ValueKind==System.Text.Json.JsonValueKind.Number?value.GetDouble():0;
    void SetCoordinates(double l,double b,double r,double t){left.Text=Format(l);bottom.Text=Format(b);right.Text=Format(r);top.Text=Format(t);}
    static string Format(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    bool TryPlacement(out double l,out double b,out double r,out double t)
    {
        l=b=r=t=0;
        var valid=Parse(left.Text,out l)&&Parse(bottom.Text,out b)&&Parse(right.Text,out r)&&Parse(top.Text,out t)&&
            l>=0&&b>=0&&r<=pageWidth&&t<=pageHeight&&r>l&&t>b;
        if(!valid)status.Text="Enter a finite, nonempty rectangle inside the current page, using decimal points.";
        return valid;
    }
    static bool Parse(string text,out double value)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);
    void SetButtons(bool enabled){replace.IsEnabled=enabled&&!working;transform.IsEnabled=enabled&&!working;delete.IsEnabled=enabled&&!working;add.IsEnabled=!working&&pageEditable;}
    static int ParseIndex(string id)=>int.TryParse(id.Split(':').LastOrDefault(),NumberStyles.None,CultureInfo.InvariantCulture,out var index)?index:-1;
    static OpenFileDialog ImagePicker()=>new() { Title="Choose an image",Filter="Supported images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*",CheckFileExists=true,Multiselect=false };
    ImageItem? Selected=>objects.SelectedItem as ImageItem;

    async Task AddImageAsync()
    {
        if(working)return;
        var dialog=ImagePicker(); if(dialog.ShowDialog(this)!=true)return;
        if(!TryPlacement(out var l,out var b,out var r,out var t))return;
        await RunEditAsync(async()=>
        {
            var decoded=await WicImageDecoder.DecodeAsync(dialog.FileName);
            var maxWidth=pageWidth-l;var maxHeight=pageHeight-b;
            var targetWidth=Math.Min(r-l,maxWidth);var targetHeight=targetWidth*decoded.Height/decoded.Width;
            if(targetHeight>maxHeight){targetHeight=maxHeight;targetWidth=targetHeight*decoded.Width/decoded.Height;}
            if(targetWidth<=0||targetHeight<=0)throw new InvalidOperationException("There is no room for this image at the chosen page position.");
            r=l+targetWidth;t=b+targetHeight;SetCoordinates(l,b,r,t);
            await session.AddImageAsync(page,decoded.Bgra,decoded.Width,decoded.Height,l,b,r,t);
            await LoadPageAsync();
        });
    }
    async Task ReplaceImageAsync()
    {
        if(working||Selected is not ImageItem item)return;
        var dialog=ImagePicker();if(dialog.ShowDialog(this)!=true)return;
        await RunEditAsync(async()=>
        {
            var decoded=await WicImageDecoder.DecodeAsync(dialog.FileName);
            var oldRatio=(item.Right-item.Left)/(item.Top-item.Bottom);var newRatio=(double)decoded.Width/decoded.Height;
            if(Math.Abs(oldRatio-newRatio)>0.01)throw new InvalidOperationException("Replacement images must match the selected image's aspect ratio so the original placement stays intact.");
            await session.ReplaceImageAsync(page,item.Id,decoded.Bgra,decoded.Width,decoded.Height);
            await LoadPageAsync(ParseIndex(item.Id));
        });
    }
    async Task TransformImageAsync()
    {
        if(working||Selected is not ImageItem item||!TryPlacement(out var l,out var b,out var r,out var t))return;
        await RunEditAsync(async()=>{await session.TransformImageAsync(page,item.Id,l,b,r,t);await LoadPageAsync(ParseIndex(item.Id));});
    }
    async Task DeleteImageAsync()
    {
        if(working||Selected is not ImageItem item)return;
        await RunEditAsync(async()=>{await session.DeleteImageAsync(page,item.Id);await LoadPageAsync();});
    }
    async Task RunEditAsync(Func<Task> edit)
    {
        working=true;SetButtons(false);status.Text="Applying the image change and saving a recovery checkpoint…";
        try{await edit();}
        catch(Exception ex){await LoadPageAsync();status.Text=ex.Message;}
        finally{working=false;SetButtons(Selected is ImageItem item&&item.CanEdit);}
    }
    sealed record ImageItem(string Id,bool HasBounds,double Left,double Bottom,double Right,double Top,string EditReason)
    {
        public bool CanEdit=>HasBounds&&EditReason.Length==0&&Right>Left&&Top>Bottom;
        public string Label=>$"Image · {Left:0.##}, {Bottom:0.##} to {Right:0.##}, {Top:0.##} pt"+(CanEdit?"":" · unsupported");
    }
}
