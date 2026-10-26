using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class WorkflowProof
{
    public static async Task RunAsync(string root)
    {
        var output = Path.Combine(root,"artifacts","workflow-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var source = Path.Combine(root,"fixtures","corpus-v1","engine","page-source.pdf");
        var hash = RecoveryStore.Fingerprint(source);
        var p0 = PageObjectHitTest.ToPdf(50, 100, 600, 800, 300, 400, 0);
        var p1 = PageObjectHitTest.ToPdf(100, 50, 800, 600, 400, 300, 1);
        var p2 = PageObjectHitTest.ToPdf(50, 100, 600, 800, 300, 400, 2);
        var p3 = PageObjectHitTest.ToPdf(100, 50, 800, 600, 400, 300, 3);
        if (p0 != (100d, 600d) || p1 != (100d, 200d) || p2 != (500d, 200d) || p3 != (500d, 600d))
            throw new Exception("Click-to-edit PDF coordinates honor zoom and all page rotations");
        using (var hitData = System.Text.Json.JsonDocument.Parse("{\"Objects\":[{\"Id\":\"text\",\"Type\":1,\"HasBounds\":true,\"Left\":10,\"Bottom\":10,\"Right\":90,\"Top\":90},{\"Id\":\"image\",\"Type\":3,\"HasBounds\":true,\"Left\":20,\"Bottom\":20,\"Right\":80,\"Top\":80}]}"))
        {
            if (PageObjectHitTest.Find(hitData.RootElement, 50, 50)?.GetProperty("Id").GetString() != "image" ||
                PageObjectHitTest.Find(hitData.RootElement, 15, 15)?.GetProperty("Id").GetString() != "text" ||
                PageObjectHitTest.Find(hitData.RootElement, 100, 100) is not null)
                throw new Exception("Click-to-edit selects topmost bounded object and ignores empty page space");
        }
        using var session = await DocumentSession.OpenAsync(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"),new RecoveryStore(Path.Combine(output,"recovery")),source);
        var styleOwner = new MainWindow(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"),new RecoveryStore(Path.Combine(output,"shell-recovery")),new TestDialogs());
        var editor = new ObjectEditorWindow(session,0);
        editor.Resources.MergedDictionaries.Add(styleOwner.Resources);
        ShellThemeResources.Apply(editor.Resources,ShellTheme.Light,false);
        await (Task)typeof(ObjectEditorWindow).GetMethod("LoadAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,null)!;
        var list = Descendants<ListBox>(editor).Single();
        if(list.Items.Count == 0) throw new Exception("Real existing text objects are presented by the editor");
        list.SelectedIndex = 0;
        var replacement = Descendants<TextBox>(editor).Single();
        if(string.IsNullOrWhiteSpace(replacement.Text)) throw new Exception("Selecting an existing text object loads its actual text");
        if(!Descendants<Button>(editor).Single(b=>b.Content?.ToString()=="Replace selected text").IsEnabled) throw new Exception("A supported unclipped text object can be edited through the real UI");
        var editorContent=(FrameworkElement)editor.Content;
        editorContent.Measure(new Size(760,580)); editorContent.Arrange(new Rect(0,0,760,580)); editorContent.UpdateLayout();
        var committedImage = Descendants<Image>(editorContent).Single(i => AutomationProperties.GetName(i) == "Rendered current committed PDF page");
        var candidateImage = Descendants<Image>(editorContent).Single(i => AutomationProperties.GetName(i) == "Isolated rendered page with proposed text replacement");
        if(committedImage.Source is null) throw new Exception("Text editor shows the committed page render");
        await WaitForAsync(() => candidateImage.Source is not null && candidateImage.Visibility == Visibility.Visible,
            "A selected text replacement produces an isolated candidate page preview");
        var chosenObjectId = (await session.ExtractAsync(0)).GetProperty("Objects").EnumerateArray()
            .First(o => o.GetProperty("Type").GetInt32() == 1 && o.TryGetProperty("Text", out _) && o.GetProperty("CanEdit").GetBoolean())
            .GetProperty("Id").GetString()!;
        var clickedEditor = new ObjectEditorWindow(session, 0, selectedObjectId: chosenObjectId);
        clickedEditor.Resources.MergedDictionaries.Add(styleOwner.Resources);
        ShellThemeResources.Apply(clickedEditor.Resources, ShellTheme.Light, false);
        await (Task)typeof(ObjectEditorWindow).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(clickedEditor, null)!;
        var clickedContent = (FrameworkElement)clickedEditor.Content;
        clickedContent.Measure(new Size(760, 580)); clickedContent.Arrange(new Rect(0, 0, 760, 580)); clickedContent.UpdateLayout();
        var clickedList = Descendants<ListBox>(clickedEditor).Single();
        if (clickedList.SelectedItem is null || clickedList.SelectedItem.GetType().GetProperty("Id")!.GetValue(clickedList.SelectedItem) as string != chosenObjectId)
            throw new Exception("A clicked PDF text object is selected when the editor opens");
        var clickedCandidate = Descendants<Image>(clickedEditor).Single(i => AutomationProperties.GetName(i) == "Isolated rendered page with proposed text replacement");
        await WaitForAsync(() => clickedCandidate.Source is not null && clickedCandidate.Visibility == Visibility.Visible,
            "Preselected clicked text starts its isolated candidate preview after loading");
        if(session.IsDirty || RecoveryStore.Fingerprint(source) != hash)
            throw new Exception("Rendering a proposed text replacement leaves the committed document and source PDF unchanged");
        Capture(editor,Path.Combine(output,"existing-text-light.png"),760,580);
        var previewScroll=Descendants<ScrollViewer>(editorContent).Single(s=>Descendants<Image>(s).Any(i=>AutomationProperties.GetName(i)=="Isolated rendered page with proposed text replacement"));
        previewScroll.ScrollToVerticalOffset(180); editorContent.UpdateLayout();
        Capture(editor,Path.Combine(output,"existing-text-candidate.png"),760,580);
        var combine = new CombineWindow(); combine.Resources.MergedDictionaries.Add(styleOwner.Resources); ShellThemeResources.Apply(combine.Resources,ShellTheme.Light,false);
        if(CombineWindow.ValidatePageRange("", 4).Length != 0 || CombineWindow.ValidatePageRange("1,3-4", 4).Length != 0)
            throw new Exception("Combine accepts blank all-pages and valid page ranges");
        if(!CombineWindow.ValidatePageRange("1,5", 4).Contains("outside this PDF", StringComparison.Ordinal) ||
           !CombineWindow.ValidatePageRange("1,,2", 4).Contains("malformed", StringComparison.Ordinal) ||
           !CombineWindow.ValidatePageRange("1-2,2", 4).Contains("more than once", StringComparison.Ordinal))
            throw new Exception("Combine reports malformed, duplicate and file-bounded page ranges");
        if(CombineWindow.ValidatePageRange("2147483647", int.MaxValue).Length != 0 || CombineWindow.ValidatePageRange("1", 50_001).Length != 0 ||
           !CombineWindow.ValidatePageRange("", 50_001).Contains("50,000-page", StringComparison.Ordinal) ||
           !CombineWindow.ValidatePageRange("1-50001", 50_001).Contains("50,000-page", StringComparison.Ordinal) ||
           !CombineWindow.ValidatePageRange("1-2147483647", int.MaxValue).Contains("50,000-page", StringComparison.Ordinal))
            throw new Exception("Combine applies the 50,000-page limit without rejecting small ranges from larger inputs");
        var inputType = typeof(CombineWindow).GetNestedType("InputFile", BindingFlags.NonPublic)!;
        var input = Activator.CreateInstance(inputType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { "C:\\PDFs\\range-validation-example.pdf", 4 }, null)!;
        ((System.Collections.IList)typeof(CombineWindow).GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combine)!).Add(input);
        // The dialog stays hidden in the offscreen proof, so measuring Window itself does not
        // run its layout pass. Measure its content directly to realize data-template controls.
        var combineContent = (FrameworkElement)combine.Content;
        combineContent.Measure(new Size(800,580)); combineContent.Arrange(new Rect(0,0,800,580)); combineContent.UpdateLayout();
        var rangeField = Descendants<TextBox>(combineContent).Single();
        if(AutomationProperties.GetName(rangeField) != "Page range for range-validation-example.pdf; blank means all 4 pages" ||
           !AutomationProperties.GetHelpText(rangeField).Contains("leave blank for all 4 pages", StringComparison.Ordinal))
            throw new Exception("Each input has an accessible file-specific page-range label and help text");
        rangeField.Text = "1,3-4";
        var status = typeof(CombineWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combine) as TextBlock;
        if(status?.Text.Contains("3 selected pages", StringComparison.Ordinal) != true)
            throw new Exception("Combine updates selected-page count from the edited row");
        rangeField.Text = "1,5";
        if(!Descendants<TextBlock>(combine).Any(t => t.Text.Contains("outside this PDF, which has 4 pages", StringComparison.Ordinal)) ||
           status?.Text.Contains("range-validation-example.pdf", StringComparison.Ordinal) != true)
            throw new Exception("Combine displays a file-specific page-range validation error");
        rangeField.Text = "";
        var labels = Descendants<Button>(combine).Select(b=>b.Content?.ToString()).ToArray();
        foreach(var label in new[]{"Add PDFs","Move up","Move down","Remove","Save combined PDF…","Cancel job"})
            if(!labels.Contains(label)) throw new Exception("Combine has a labeled keyboard alternative: "+label);
        Capture(combine,Path.Combine(output,"combine-light.png"),800,580);
        ShellThemeResources.Apply(combine.Resources,ShellTheme.Dark,false);
        Capture(combine,Path.Combine(output,"combine-dark.png"),800,580);
        var markup = new MarkupWindow(session,0);
        markup.Resources.MergedDictionaries.Add(styleOwner.Resources);
        ShellThemeResources.Apply(markup.Resources,ShellTheme.Light,false);
        typeof(MarkupWindow).GetMethod("LoadInitial",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(markup,null);
        var markupContent=(FrameworkElement)markup.Content;
        markupContent.Measure(new Size(620,620)); markupContent.Arrange(new Rect(0,0,620,620)); markupContent.UpdateLayout();
        Capture(markup,Path.Combine(output,"markup-light.png"),620,620);
        var markupKind=Descendants<ComboBox>(markupContent).Single(c=>AutomationProperties.GetName(c)=="Annotation type");
        if(markupKind.Items.Cast<object>().Select(x=>x.ToString()).SequenceEqual(new[]{"Highlight","Underline","Strikeout","Sticky note"})==false)
            throw new Exception("Annotations expose supported text markup and sticky note types");
        var coordinates=Descendants<TextBox>(markupContent).ToDictionary(AutomationProperties.GetName);
        foreach(var label in new[]{"Left coordinate in PDF points","Bottom coordinate in PDF points","Right coordinate in PDF points","Top coordinate in PDF points"})
            if(!coordinates.ContainsKey(label)) throw new Exception("Markup exposes labeled coordinates for each page rectangle edge");
        var markupList=Descendants<ListBox>(markupContent).Single(l=>AutomationProperties.GetName(l)=="Annotation rectangles to apply");
        var addMarkup=Descendants<Button>(markupContent).Single(b=>b.Content?.ToString()=="Add rectangle");
        var applyMarkup=Descendants<Button>(markupContent).Single(b=>b.Content?.ToString()=="Apply markup");
        coordinates["Left coordinate in PDF points"].Text="20";
        coordinates["Bottom coordinate in PDF points"].Text="30";
        coordinates["Right coordinate in PDF points"].Text="10";
        coordinates["Top coordinate in PDF points"].Text="40";
        addMarkup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(markupList.Items.Count!=0 || applyMarkup.IsEnabled) throw new Exception("Markup rejects page rectangles with reversed edges");
        coordinates["Right coordinate in PDF points"].Text="60";
        addMarkup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(markupList.Items.Count!=1 || !applyMarkup.IsEnabled) throw new Exception("Markup adds a valid rectangle and enables Apply");
        var removeMarkup=Descendants<Button>(markupContent).Single(b=>b.Content?.ToString()=="Remove selected rectangle");
        markupList.SelectedIndex=0;
        removeMarkup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(markupList.Items.Count!=0 || applyMarkup.IsEnabled) throw new Exception("Markup can remove a rectangle before applying the annotation");

        markupKind.SelectedItem="Sticky note";
        var commentBox=Descendants<TextBox>(markupContent).Single(t=>AutomationProperties.GetName(t)=="Sticky note comment");
        if(commentBox.Visibility!=Visibility.Visible || applyMarkup.IsEnabled)
            throw new Exception("Selecting Sticky note shows its comment field and keeps Apply disabled until the note is complete");
        coordinates["Left coordinate in PDF points"].Text="50";
        coordinates["Bottom coordinate in PDF points"].Text="50";
        coordinates["Right coordinate in PDF points"].Text="90";
        coordinates["Top coordinate in PDF points"].Text="75";
        var addNoteRectangle=Descendants<Button>(markupContent).Single(b=>b.Content?.ToString()=="Add note rectangle");
        addNoteRectangle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(markupList.Items.Count!=1 || applyMarkup.IsEnabled)
            throw new Exception("Sticky note accepts one page rectangle and still requires comment text");
        const string stickyNoteText="Review this clause — confirm the date.\nSecond line.";
        commentBox.Text=stickyNoteText;
        if(!applyMarkup.IsEnabled) throw new Exception("Sticky note enables Apply when its comment and rectangle are valid");
        var revisionBeforeNote=session.Revision;
        var modalTimeout=new DispatcherTimer { Interval=TimeSpan.FromSeconds(15) };
        modalTimeout.Tick+=(_,_)=>
        {
            modalTimeout.Stop();
            typeof(MarkupWindow).GetField("working",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(markup,false);
            markup.Close();
        };
        modalTimeout.Start();
        _ = markup.Dispatcher.BeginInvoke(new Action(()=>applyMarkup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))),DispatcherPriority.Loaded);
        var noteDialogResult=markup.ShowDialog();
        modalTimeout.Stop();
        if(noteDialogResult!=true || session.Revision!=revisionBeforeNote+1)
            throw new Exception("Applying a sticky note through the modal annotation UI commits one session checkpoint");
        var liveNote=(await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Single(a=>a.GetProperty("Subtype").GetInt32()==1&&a.GetProperty("Contents").GetString()==stickyNoteText);
        if(liveNote.GetProperty("Contents").GetString()!=stickyNoteText || Math.Abs(liveNote.GetProperty("Left").GetDouble()-50)>0.01 ||
           Math.Abs(liveNote.GetProperty("Top").GetDouble()-75)>0.01)
            throw new Exception("Sticky note text and entered page rectangle reach the committed PDF annotation");
        await session.UndoAsync();
        if((await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Any(a=>a.GetProperty("Subtype").GetInt32()==1&&a.GetProperty("Contents").GetString()==stickyNoteText))
            throw new Exception("Undo removes the sticky note added through the annotation UI");
        await session.RedoAsync();
        var stickyPath=Path.Combine(output,"sticky-note-ui.pdf");
        await session.SaveAsync(stickyPath);
        if(RecoveryStore.Fingerprint(source)!=hash) throw new Exception("Saving the UI-created sticky note preserves the source PDF");
        using(var reopened=await DocumentSession.OpenAsync(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"),
            new RecoveryStore(Path.Combine(output,"sticky-note-reopen-recovery")),stickyPath))
        {
            var reopenedNote=(await reopened.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Single(a=>a.GetProperty("Subtype").GetInt32()==1&&a.GetProperty("Contents").GetString()==stickyNoteText);
            if(reopenedNote.GetProperty("Contents").GetString()!=stickyNoteText || Math.Abs(reopenedNote.GetProperty("Left").GetDouble()-50)>0.01 ||
               Math.Abs(reopenedNote.GetProperty("Top").GetDouble()-75)>0.01)
                throw new Exception("Saved UI-created sticky note text and rectangle survive independent reopen");
            var noteReader=new MarkupWindow(reopened,0) { ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000 };
            noteReader.Resources.MergedDictionaries.Add(styleOwner.Resources);
            ShellThemeResources.Apply(noteReader.Resources,ShellTheme.Light,false);
            var readerContent=(FrameworkElement)noteReader.Content;
            noteReader.Show();
            try
            {
                readerContent.Measure(new Size(620,760)); readerContent.Arrange(new Rect(0,0,620,760)); readerContent.UpdateLayout();
                var notesExpander=Descendants<Expander>(readerContent).Single(e=>AutomationProperties.GetName(e)=="Read existing sticky notes on current page");
                notesExpander.IsExpanded=true;
                readerContent.Measure(new Size(620,760)); readerContent.Arrange(new Rect(0,0,620,760)); readerContent.UpdateLayout();
                var notesList=Descendants<ListBox>(readerContent).Single(l=>AutomationProperties.GetName(l)=="Existing sticky notes on current page");
                var noteText=Descendants<TextBox>(readerContent).Single(t=>AutomationProperties.GetName(t)=="Selected sticky note comment, read only");
                bool HasSavedUiNote()=>notesList.Items.Cast<object>().Any(item=>item.GetType().GetProperty("Content",BindingFlags.Public|BindingFlags.Instance)!.GetValue(item)?.ToString()==stickyNoteText);
                await WaitForAsync(HasSavedUiNote,"The reopened annotation UI loads the saved sticky note");
                var savedUiNote=notesList.Items.Cast<object>().Single(item=>item.GetType().GetProperty("Content",BindingFlags.Public|BindingFlags.Instance)!.GetValue(item)?.ToString()==stickyNoteText);
                notesList.SelectedItem=savedUiNote;
                readerContent.Measure(new Size(620,760)); readerContent.Arrange(new Rect(0,0,620,760)); readerContent.UpdateLayout();
                Capture(noteReader,Path.Combine(output,"sticky-note-reader-light.png"),620,760);
                if(!noteText.IsReadOnly || noteText.Text!=stickyNoteText)
                    throw new Exception("Reopened annotation UI displays the saved comment in a labeled read-only field");
                ShellThemeResources.Apply(noteReader.Resources,ShellTheme.Dark,false);
                Capture(noteReader,Path.Combine(output,"sticky-note-reader-dark.png"),620,760);
            }
            finally { noteReader.Close(); }
            await reopened.CloseAsync();
        }

        var imageSource=Path.Combine(root,"fixtures","corpus-v1","engine","objects-original.pdf");
        var imageHash=RecoveryStore.Fingerprint(imageSource);
        using var imageSession=await DocumentSession.OpenAsync(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"),
            new RecoveryStore(Path.Combine(output,"image-recovery")),imageSource);
        var imageEditor=new ImageEditorWindow(imageSession,0);
        imageEditor.Resources.MergedDictionaries.Add(styleOwner.Resources); ShellThemeResources.Apply(imageEditor.Resources,ShellTheme.Light,false);
        var loadImages=typeof(ImageEditorWindow).GetMethod("LoadPageAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)loadImages.Invoke(imageEditor,new object?[]{null})!;
        var imageContent=(FrameworkElement)imageEditor.Content;
        imageContent.Measure(new Size(960,720)); imageContent.Arrange(new Rect(0,0,960,720)); imageContent.UpdateLayout();
        var imageList=Descendants<ListBox>(imageContent).Single(l=>AutomationProperties.GetName(l)=="Top-level images on the current page");
        if(imageList.Items.Count!=2) throw new Exception("Image editor lists both existing top-level images on the current page");
        var currentPageImage=Descendants<Image>(imageContent).Single(i=>AutomationProperties.GetName(i)=="Rendered current PDF page");
        if(currentPageImage.Source is null) throw new Exception("Image editor shows the committed page render");
        foreach(var name in new[]{"Add image from file","Replace selected image from file","Apply selected image placement","Delete selected image"})
            if(!Descendants<Button>(imageContent).Any(b=>AutomationProperties.GetName(b)==name)) throw new Exception("Image editor exposes the labeled action: "+name);
        Capture(imageEditor,Path.Combine(output,"image-edit-light.png"),960,720);
        imageList.SelectedIndex=0;
        var imageCoords=Descendants<TextBox>(imageContent).ToDictionary(AutomationProperties.GetName);
        var originalLeft=double.Parse(imageCoords["Image left position in PDF points"].Text,System.Globalization.CultureInfo.InvariantCulture);
        var originalRight=double.Parse(imageCoords["Image right position in PDF points"].Text,System.Globalization.CultureInfo.InvariantCulture);
        imageCoords["Image left position in PDF points"].Text=(originalLeft+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        imageCoords["Image right position in PDF points"].Text=(originalRight+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var applyPlacement=Descendants<Button>(imageContent).Single(b=>AutomationProperties.GetName(b)=="Apply selected image placement");
        bool ImageUiWorking()=>(bool)typeof(ImageEditorWindow).GetField("working",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(imageEditor)!;
        var imageRevision=imageSession.Revision;
        applyPlacement.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>imageSession.Revision>imageRevision && applyPlacement.IsEnabled && !ImageUiWorking(),"Image placement edit commits and finishes refreshing through the UI");
        var transformedImages=(await imageSession.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Where(o=>o.GetProperty("Type").GetInt32()==3).ToArray();
        if(!transformedImages.Any(o=>Math.Abs(o.GetProperty("Left").GetDouble()-(originalLeft+1))<0.01))
            throw new Exception("Image placement applies the entered page coordinates");
        await imageSession.UndoAsync();
        await (Task)loadImages.Invoke(imageEditor,new object?[]{null})!;
        imageList=Descendants<ListBox>((FrameworkElement)imageEditor.Content).Single(l=>AutomationProperties.GetName(l)=="Top-level images on the current page");
        imageList.SelectedIndex=0;
        var deleteImage=Descendants<Button>((FrameworkElement)imageEditor.Content).Single(b=>AutomationProperties.GetName(b)=="Delete selected image");
        var imageStatus=typeof(ImageEditorWindow).GetField("status",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(imageEditor) as TextBlock;
        try { await WaitForAsync(()=>deleteImage.IsEnabled && !ImageUiWorking(),"Freshly selected image enables Delete after undo refresh"); }
        catch { throw new Exception($"Delete image button is disabled after refreshing and selecting an image (list={imageList.Items.Count}, selected={imageList.SelectedIndex}, status={imageStatus?.Text})"); }
        imageRevision=imageSession.Revision; deleteImage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try { await WaitForAsync(()=>imageSession.Revision>imageRevision && imageList.Items.Count==1 && !ImageUiWorking(),"Delete image commits and finishes refreshing through the UI"); }
        catch { throw new Exception($"Delete image did not commit (revision={imageSession.Revision}/{imageRevision}, list={imageList.Items.Count}, selected={imageList.SelectedIndex}, enabled={deleteImage.IsEnabled}, status={imageStatus?.Text})"); }
        var remaining=(await imageSession.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(o=>o.GetProperty("Type").GetInt32()==3);
        if(remaining!=1) throw new Exception("Deleting a selected image removes that top-level image");
        await imageSession.UndoAsync();
        if(imageSession.IsDirty || RecoveryStore.Fingerprint(imageSource)!=imageHash ||
            (await imageSession.ExtractAsync(0)).GetProperty("Objects").EnumerateArray().Count(o=>o.GetProperty("Type").GetInt32()==3)!=2)
            throw new Exception("Image placement/delete undo restores both images and preserves the source PDF");

        var pagesSource=Path.Combine(root,"fixtures","corpus-v1","engine","page-source.pdf");
        var pagesHash=RecoveryStore.Fingerprint(pagesSource);
        using var pagesSession=await DocumentSession.OpenAsync(Path.Combine(root,"App.Worker","bin","Debug","net10.0","App.Worker.exe"),
            new RecoveryStore(Path.Combine(output,"page-recovery")),pagesSource);
        var pageWindow=new PageOrganizerWindow(pagesSession,0) { ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000 };
        pageWindow.Resources.MergedDictionaries.Add(styleOwner.Resources); ShellThemeResources.Apply(pageWindow.Resources,ShellTheme.Light,false); pageWindow.Show();
        var pageContent=(FrameworkElement)pageWindow.Content;
        pageContent.Measure(new Size(760,620)); pageContent.Arrange(new Rect(0,0,760,620)); pageContent.UpdateLayout();
        var pageList=Descendants<ListBox>(pageContent).Single(l=>AutomationProperties.GetName(l)=="Pages in current document order");
        await WaitForAsync(()=>pageList.Items.Count==2,"Page organizer loads the actual document order");
        var pagePreview=Descendants<Image>(pageContent).Single();
        await WaitForAsync(()=>pagePreview.Source is not null,"Selecting a page renders its preview");
        Capture(pageWindow,Path.Combine(output,"page-organizer-light.png"),760,620);
        foreach(var name in new[]{"Move selected page up","Move selected page down","Insert a blank page before the selected page","Insert a blank page after the selected page","Duplicate the selected page after itself","Import selected pages from another PDF after the selected page","Delete the selected page","Extract selected pages to a new PDF","Split the whole document into one PDF per page in a new folder"})
            if(!Descendants<Button>(pageContent).Any(b=>AutomationProperties.GetName(b)==name)) throw new Exception("Page organizer exposes the labeled action: "+name);
        var importRange=Descendants<TextBox>(pageContent).Single(t=>AutomationProperties.GetName(t)=="Source PDF page range to import");
        if(!AutomationProperties.GetHelpText(importRange).Contains("1,3-5",StringComparison.Ordinal) || !AutomationProperties.GetHelpText(importRange).Contains("every page",StringComparison.Ordinal))
            throw new Exception("Page import range field explains selective and all-page syntax to assistive technology");
        var rangeParser=typeof(PageOrganizerWindow).GetMethod("TryResolveImportPageRange",BindingFlags.Static|BindingFlags.NonPublic)!;
        object?[] rangeArgs=["1,3-4",5,null,null];
        if(!(bool)rangeParser.Invoke(null,rangeArgs)! || !((int[])rangeArgs[2]!).SequenceEqual([0,2,3]))
            throw new Exception("Selective page import resolves ranges in the requested order");
        object?[] duplicateRangeArgs=["1,1",5,null,null];
        if((bool)rangeParser.Invoke(null,duplicateRangeArgs)! || !((string)duplicateRangeArgs[3]!).Contains("appears more than once",StringComparison.Ordinal))
            throw new Exception("Selective page import rejects duplicate page selections");
        if(pageList.SelectionMode!=SelectionMode.Extended) throw new Exception("Page organizer supports selecting multiple pages for extraction");
        var extractPagesButton=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Extract selected pages to a new PDF");
        var splitPagesButton=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Split the whole document into one PDF per page in a new folder");
        pageList.SelectedItems.Add(pageList.Items[0]); pageList.SelectedItems.Add(pageList.Items[1]);
        if(pageList.SelectedItems.Count!=2 || !extractPagesButton.IsEnabled || !splitPagesButton.IsEnabled ||
            AutomationProperties.GetName(pageList)!="Pages in current document order")
            throw new Exception("Export controls expose accessible names and enable for a multi-page selection");
        pageList.SelectedItems.Clear(); pageList.SelectedIndex=0;
        var beforeMove0=(await pagesSession.RenderAsync(0,306,396)).GetProperty("Bgra").GetString();
        var beforeMove1=(await pagesSession.RenderAsync(1,306,396)).GetProperty("Bgra").GetString();
        pageList.SelectedIndex=1;
        var moveUp=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Move selected page up");
        var pageRevision=pagesSession.Revision; moveUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>pagesSession.Revision>pageRevision && pageWindow.SelectedPage==0,"Move up commits and selects the moved page");
        if((await pagesSession.RenderAsync(0,306,396)).GetProperty("Bgra").GetString()!=beforeMove1)
            throw new Exception("Move up places the selected page first in the rendered document order");
        var moveDown=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Move selected page down");
        pageRevision=pagesSession.Revision; moveDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>pagesSession.Revision>pageRevision && pageWindow.SelectedPage==1,"Move down commits and selects the moved page");
        if((await pagesSession.RenderAsync(0,306,396)).GetProperty("Bgra").GetString()!=beforeMove0)
            throw new Exception("Move down restores the first page's prior content");
        await pagesSession.UndoAsync(); await pagesSession.UndoAsync();
        var insertBefore=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Insert a blank page before the selected page");
        pageList.SelectedIndex=0; pageRevision=pagesSession.Revision; insertBefore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>pagesSession.Revision>pageRevision && pageWindow.SelectedPage==1,"Insert blank before commits and preserves selection of the original page");
        if((await pagesSession.PageInfoAsync()).GetProperty("Pages").GetArrayLength()!=3)
            throw new Exception("Insert blank before adds a page to the current document");
        await pagesSession.UndoAsync();
        pageWindow.Close();
        pageWindow=new PageOrganizerWindow(pagesSession,0) { ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000 };
        pageWindow.Resources.MergedDictionaries.Add(styleOwner.Resources); ShellThemeResources.Apply(pageWindow.Resources,ShellTheme.Light,false); pageWindow.Show();
        pageContent=(FrameworkElement)pageWindow.Content;
        pageList=Descendants<ListBox>(pageContent).Single(l=>AutomationProperties.GetName(l)=="Pages in current document order");
        await WaitForAsync(()=>pageList.Items.Count==2,"Reopening the organizer reflects the undo checkpoint before duplication");
        var duplicatePage=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Duplicate the selected page after itself");
        pageList.SelectedIndex=1;
        pageRevision=pagesSession.Revision; duplicatePage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>pagesSession.Revision>pageRevision && pageWindow.SelectedPage==2 && pageList.Items.Count==3,
            "Duplicate page commits through the organizer and selects the new copy");
        var duplicateInfo=(await pagesSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
        var duplicateOriginalText=(await pagesSession.ExtractAsync(1)).GetProperty("Text").GetString();
        var duplicateCopyText=(await pagesSession.ExtractAsync(2)).GetProperty("Text").GetString();
        if(duplicateInfo[1].GetProperty("Width").GetDouble()!=duplicateInfo[2].GetProperty("Width").GetDouble() || duplicateOriginalText!=duplicateCopyText)
            throw new Exception("Duplicate page retains the selected page's geometry and text");
        await pagesSession.UndoAsync(); pageWindow.Close();
        pageWindow=new PageOrganizerWindow(pagesSession,0) { ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000 };
        pageWindow.Resources.MergedDictionaries.Add(styleOwner.Resources); ShellThemeResources.Apply(pageWindow.Resources,ShellTheme.Light,false); pageWindow.Show();
        pageContent=(FrameworkElement)pageWindow.Content;
        pageList=Descendants<ListBox>(pageContent).Single(l=>AutomationProperties.GetName(l)=="Pages in current document order");
        await WaitForAsync(()=>pageList.Items.Count==2,"Reopening the organizer reflects the duplicate undo checkpoint before deletion");
        var deletePage=Descendants<Button>(pageContent).Single(b=>AutomationProperties.GetName(b)=="Delete the selected page");
        pageList.SelectedIndex=1;
        if(!deletePage.IsEnabled) throw new Exception("Delete page is enabled for a selected page when the document has multiple pages");
        pageRevision=pagesSession.Revision; deletePage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(()=>pagesSession.Revision>pageRevision && pageWindow.SelectedPage==0 && !deletePage.IsEnabled,
            "Delete page commits through the organizer and disables when only the final page remains");
        if((await pagesSession.PageInfoAsync()).GetProperty("Pages").GetArrayLength()!=1)
            throw new Exception("Delete page removes only the selected page");
        await pagesSession.UndoAsync(); pageWindow.Close();
        if(pagesSession.IsDirty || RecoveryStore.Fingerprint(pagesSource)!=pagesHash ||
            (await pagesSession.PageInfoAsync()).GetProperty("Pages").GetArrayLength()!=2)
            throw new Exception("Undo restores page order and count without changing the source PDF");
        if(RecoveryStore.Fingerprint(source)!=hash || session.IsDirty) throw new Exception("Opening edit/combine views must not mutate the PDF");
        Console.WriteLine("PASS workflow UI: real text selection, labeled combine ordering and source preservation. Evidence: "+output);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i); if(child is T item) yield return item;
            foreach(var nested in Descendants<T>(child)) yield return nested;
        }
        if(parent is Window w && w.Content is DependencyObject content && VisualTreeHelper.GetChildrenCount(parent)==0) {
            if(content is T item) yield return item;
            foreach(var nested in Descendants<T>(content)) yield return nested;
        }
    }
    static void Capture(Window window,string path,int width,int height)
    {
        var panel=(FrameworkElement)window.Content;
        if(panel is Panel surface) surface.SetResourceReference(Panel.BackgroundProperty,"AppBackground");
        panel.Measure(new Size(width,height)); panel.Arrange(new Rect(0,0,width,height)); panel.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(panel);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); encoder.Save(stream);
    }
    static async Task WaitForAsync(Func<bool> condition,string failure)
    {
        var timeout=DateTime.UtcNow.AddSeconds(15);
        while(!condition() && DateTime.UtcNow<timeout) await Task.Delay(25);
        if(!condition()) throw new Exception(failure);
    }
}


