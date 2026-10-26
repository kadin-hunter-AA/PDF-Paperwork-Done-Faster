using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HunterPdf.Desktop;

/// <summary>One row in the Annotations or On this page lists. Selecting it shows its page.</summary>
internal sealed record SidePanelEntry(int Page, string Kind, string Detail)
{
    public string Label => Detail.Length == 0 ? Kind : $"{Kind}: {Detail}";
    public string PageLabel => string.Create(CultureInfo.CurrentCulture, $"Page {Page + 1}");
    public string AutomationName => $"{Label}, {PageLabel}";
}

public partial class MainWindow
{
    const int MaxListedAnnotations = 2000;
    (OpenDocumentTab Tab, long Revision)? annotationsLoaded;
    (OpenDocumentTab Tab, long Revision, int Page)? pageContentsLoaded;
    bool sidePanelLoading, sidePanelStale;

    internal IReadOnlyList<SidePanelEntry> AnnotationEntries => (AnnotationList.ItemsSource as IReadOnlyList<SidePanelEntry>) ?? [];
    internal IReadOnlyList<SidePanelEntry> PageContentEntries => (PageContentList.ItemsSource as IReadOnlyList<SidePanelEntry>) ?? [];

    internal static string AnnotationKind(int subtype) => subtype switch
    {
        1 => "Note", 2 => "Link", 3 => "Text box", 4 => "Line", 5 => "Rectangle", 6 => "Ellipse", 7 => "Polygon", 8 => "Polyline",
        9 => "Highlight", 10 => "Underline", 11 => "Squiggly underline", 12 => "Strikeout", 13 => "Stamp", 14 => "Caret", 15 => "Drawing",
        16 => "Popup", 17 => "Attachment", 18 => "Sound", 19 => "Movie", 20 => "Form field", 21 => "Screen", 25 => "Watermark", 28 => "Redaction",
        _ => "Annotation"
    };

    async void SidePanelTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SidePanelTabs)) return;
        await RefreshSidePanelAsync();
    }

    /// <summary>Loads the visible side-panel list if the document, revision or (for On this page) the page changed.</summary>
    internal async Task RefreshSidePanelAsync()
    {
        if (!initialized) return;
        // A change made while a list is loading is picked up when that load finishes.
        if (sidePanelLoading) { sidePanelStale = true; return; }
        do
        {
            sidePanelStale = false;
            await LoadSidePanelAsync();
        }
        while (sidePanelStale);
    }

    async Task LoadSidePanelAsync()
    {
        var tab = active;
        if (tab is null)
        {
            AnnotationList.ItemsSource = PageContentList.ItemsSource = null;
            AnnotationsStatus.Text = PageContentsStatus.Text = "Open a document to list its contents.";
            annotationsLoaded = null; pageContentsLoaded = null;
            return;
        }
        sidePanelLoading = true;
        try
        {
            if (ReferenceEquals(SidePanelTabs.SelectedItem, AnnotationsTab) && annotationsLoaded != (tab, tab.Session.Revision))
                await LoadAnnotationsAsync(tab);
            else if (ReferenceEquals(SidePanelTabs.SelectedItem, PageContentsTab) && pageContentsLoaded != (tab, tab.Session.Revision, tab.CurrentPage))
                await LoadPageContentsAsync(tab);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AnnotationsStatus.Text = PageContentsStatus.Text = error.Message;
        }
        finally { sidePanelLoading = false; }
    }

    async Task LoadAnnotationsAsync(OpenDocumentTab tab)
    {
        var revision = tab.Session.Revision;
        var entries = new List<SidePanelEntry>(); var truncated = false;
        for (var page = 0; page < tab.Pages.Count && entries.Count < MaxListedAnnotations; page++)
        {
            AnnotationsStatus.Text = string.Create(CultureInfo.CurrentCulture, $"Reading page {page + 1} of {tab.Pages.Count}…");
            var data = await tab.Session.ExtractAsync(page);
            if (active != tab || tab.Session.Revision != revision) return;
            foreach (var value in data.GetProperty("Annotations").EnumerateArray())
            {
                var subtype = value.GetProperty("Subtype").GetInt32();
                if (subtype is 16 or 20) continue; // Popups belong to their note; form fields are in the Fields panel.
                if (entries.Count >= MaxListedAnnotations) { truncated = true; break; }
                entries.Add(new SidePanelEntry(page, AnnotationKind(subtype), Summary(value.TryGetProperty("Contents", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)));
            }
            truncated |= data.TryGetProperty("AnnotationsTruncated", out var limited) && limited.GetBoolean();
        }
        AnnotationList.ItemsSource = entries;
        AnnotationsStatus.Text = entries.Count == 0 ? "No annotations in this document."
            : string.Create(CultureInfo.CurrentCulture, $"{entries.Count:N0} annotation{(entries.Count == 1 ? "" : "s")}{(truncated ? " (list limited)" : "")}. Select one to show its page.");
        annotationsLoaded = (tab, revision);
    }

    async Task LoadPageContentsAsync(OpenDocumentTab tab)
    {
        var revision = tab.Session.Revision; var page = tab.CurrentPage;
        PageContentsStatus.Text = "Reading this page…";
        var data = await tab.Session.ExtractAsync(page);
        if (active != tab || tab.Session.Revision != revision || tab.CurrentPage != page) return;
        var entries = new List<SidePanelEntry>(); int texts = 0, images = 0, notes = 0;
        foreach (var value in data.GetProperty("Objects").EnumerateArray())
        {
            var type = value.GetProperty("Type").GetInt32();
            if (type == 1)
            {
                var text = Summary(value.GetProperty("Text").GetString());
                if (text.Length == 0) continue;
                texts++;
                var font = value.GetProperty("FontName").GetString() ?? "";
                var size = value.GetProperty("FontSize").GetDouble();
                entries.Add(new SidePanelEntry(page, font.Length == 0 ? "Text" : string.Create(CultureInfo.CurrentCulture, $"Text ({font}, {size:0.#} pt)"), text));
            }
            else if (type == 3)
            {
                images++;
                entries.Add(new SidePanelEntry(page, "Image", string.Create(CultureInfo.CurrentCulture,
                    $"{value.GetProperty("Right").GetDouble() - value.GetProperty("Left").GetDouble():0} × {value.GetProperty("Top").GetDouble() - value.GetProperty("Bottom").GetDouble():0} pt")));
            }
        }
        foreach (var value in data.GetProperty("Annotations").EnumerateArray())
        {
            var subtype = value.GetProperty("Subtype").GetInt32();
            if (subtype is 16 or 20) continue;
            notes++;
            entries.Add(new SidePanelEntry(page, AnnotationKind(subtype), Summary(value.TryGetProperty("Contents", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)));
        }
        PageContentList.ItemsSource = entries;
        PageContentsStatus.Text = string.Create(CultureInfo.CurrentCulture,
            $"Page {page + 1}: {texts:N0} text object{(texts == 1 ? "" : "s")}, {images:N0} image{(images == 1 ? "" : "s")}, {notes:N0} annotation{(notes == 1 ? "" : "s")}.");
        pageContentsLoaded = (tab, revision, page);
    }

    static string Summary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    void SidePanelEntry_Activated(object sender, MouseButtonEventArgs e) => ShowSelectedEntryPage(sender);

    void SidePanelEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ShowSelectedEntryPage(sender); e.Handled = true;
    }

    void ShowSelectedEntryPage(object sender)
    {
        if (sender is ListBox { SelectedItem: SidePanelEntry entry } && active is not null && entry.Page < active.Pages.Count) GoToPage(entry.Page);
    }
}
