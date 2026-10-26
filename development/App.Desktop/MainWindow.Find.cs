using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace HunterPdf.Desktop;

/// <summary>One find result: the first and last character indexes of the match in the page's text layout.</summary>
internal sealed record SearchMatch(int Page, int First, int Last);

/// <summary>Every match for one query and option set on one document revision, in document order once complete.</summary>
internal sealed class SearchResults(string query, bool matchCase, bool wholeWord, long revision)
{
    public string Query { get; } = query;
    public bool MatchCase { get; } = matchCase;
    public bool WholeWord { get; } = wholeWord;
    public long Revision { get; } = revision;
    public List<SearchMatch> Matches { get; } = new();
    public bool Complete { get; set; }
    public int Current { get; set; } = -1;

    public bool IsFor(string query, bool matchCase, bool wholeWord, long revision) =>
        Query == query && MatchCase == matchCase && WholeWord == wholeWord && Revision == revision;
}

public partial class MainWindow
{
    const int MaxSearchMatches = 10_000;
    CancellationTokenSource? searchScan;

    internal string SearchMatchLabel => MatchCount.Text;

    void MatchOption_Click(object sender, RoutedEventArgs e)
    {
        // Changing an option starts a new search the next time the user asks for a match.
        if (active is not null) active.Search = null;
        MatchCount.Text = ""; MatchPages.Text = "";
        SearchBox.Focus();
    }

    void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        searchScan?.Cancel();
        if (active is not null) active.Search = null;
        MatchCount.Text = ""; MatchPages.Text = "";
    }

    async void PreviousMatch_Click(object sender, RoutedEventArgs e) => await SearchAsync(-1);

    void Find_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible || (CurrentModifiers & ModifierKeys.Alt) == 0) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var option = key switch { Key.C => MatchCaseToggle, Key.W => WholeWordToggle, _ => null };
        if (option is null) return;
        option.IsChecked = option.IsChecked != true;
        MatchOption_Click(option, e);
        e.Handled = true;
    }

    /// <summary>Moves to the next (direction 1) or previous (-1) match, scanning the document the first time.</summary>
    internal async Task SearchAsync(int direction = 1)
    {
        var tab = active;
        if (tab is null || string.IsNullOrWhiteSpace(SearchBox.Text) || tab.Pages.Count == 0) return;
        var query = SearchBox.Text;
        bool matchCase = MatchCaseToggle.IsChecked == true, wholeWord = WholeWordToggle.IsChecked == true;
        var results = tab.Search;
        if (results is null || !results.IsFor(query, matchCase, wholeWord, tab.Session.Revision))
        {
            results = await ScanAsync(tab, query, matchCase, wholeWord);
            if (results is null || active != tab) return;
        }
        if (results.Matches.Count == 0) { ShowMatchState(results); return; }
        results.Current = results.Current < 0
            ? FirstMatchFrom(results, tab.CurrentPage, direction)
            : (results.Current + direction + results.Matches.Count) % results.Matches.Count;
        await ShowMatchAsync(tab, results);
    }

    static int FirstMatchFrom(SearchResults results, int page, int direction)
    {
        if (direction > 0)
        {
            var next = results.Matches.FindIndex(m => m.Page >= page);
            return next < 0 ? 0 : next;
        }
        var previous = results.Matches.FindLastIndex(m => m.Page <= page);
        return previous < 0 ? results.Matches.Count - 1 : previous;
    }

    /// <summary>Reads every page's text once and records all matches. A new query or closing Find cancels it.</summary>
    async Task<SearchResults?> ScanAsync(OpenDocumentTab tab, string query, bool matchCase, bool wholeWord)
    {
        searchScan?.Cancel();
        var scan = searchScan = new CancellationTokenSource();
        var results = new SearchResults(query, matchCase, wholeWord, tab.Session.Revision);
        var comparison = matchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        try
        {
            for (var page = 0; page < tab.Pages.Count && results.Matches.Count < MaxSearchMatches; page++)
            {
                MatchCount.Text = string.Create(CultureInfo.CurrentCulture, $"Searching page {page + 1} of {tab.Pages.Count}…");
                // Not passed to the worker: cancelling a worker request restarts the worker process.
                var layout = await tab.Session.ExtractTextLayoutAsync(page);
                if (scan.IsCancellationRequested || active != tab || tab.Session.Revision != results.Revision) return null;
                FindInPage(layout, page, query, comparison, wholeWord, results.Matches);
            }
        }
        finally { if (ReferenceEquals(searchScan, scan)) searchScan = null; }
        results.Complete = true;
        tab.Search = results;
        return results;
    }

    static void FindInPage(JsonElement layout, int page, string query, StringComparison comparison, bool wholeWord, List<SearchMatch> matches)
    {
        var text = new StringBuilder(); var owners = new List<int>();
        foreach (var c in layout.GetProperty("Characters").EnumerateArray())
        {
            var value = c.GetProperty("Text").GetString() ?? ""; var index = c.GetProperty("Index").GetInt32();
            text.Append(value);
            for (var i = 0; i < value.Length; i++) owners.Add(index);
        }
        var flat = text.ToString();
        for (var at = flat.IndexOf(query, comparison); at >= 0 && matches.Count < MaxSearchMatches; at = flat.IndexOf(query, at + 1, comparison))
        {
            var end = at + query.Length;
            if (wholeWord && ((at > 0 && IsWordCharacter(flat[at - 1])) || (end < flat.Length && IsWordCharacter(flat[end])))) continue;
            matches.Add(new SearchMatch(page, owners[at], owners[end - 1]));
        }
    }

    static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    async Task ShowMatchAsync(OpenDocumentTab tab, SearchResults results)
    {
        var match = results.Matches[results.Current];
        var layout = await tab.Session.ExtractTextLayoutAsync(match.Page);
        if (active != tab || tab.Search != results) return;
        var descriptor = tab.Pages[match.Page];
        foreach (var other in tab.Pages) if (!ReferenceEquals(other, descriptor)) other.Characters = null;
        descriptor.Characters = layout.GetProperty("Characters").EnumerateArray().Select(c => new PdfTextCharacter(c.GetProperty("Index").GetInt32(), c.GetProperty("Text").GetString() ?? "", c.GetProperty("HasBounds").GetBoolean(),
            c.GetProperty("Left").GetDouble(), c.GetProperty("Bottom").GetDouble(), c.GetProperty("Right").GetDouble(), c.GetProperty("Top").GetDouble())).ToList();
        selectionPage = match.Page; selectionAnchor = match.First; selectionEnd = match.Last;
        GoToPage(match.Page);
        await RenderPageAsync(tab, match.Page, CancellationToken.None);
        if (match.Page < selectionLayers.Count) DrawTextSelection(selectionLayers[match.Page], descriptor);
        ShowMatchState(results);
    }

    void ShowMatchState(SearchResults results)
    {
        SearchStatus.Text = "";
        if (results.Matches.Count == 0) { MatchCount.Text = "No matches"; MatchPages.Text = ""; return; }
        var limited = results.Matches.Count >= MaxSearchMatches ? "+" : "";
        MatchCount.Text = string.Create(CultureInfo.CurrentCulture, $"{results.Current + 1} of {results.Matches.Count:N0}{limited}");
        var pages = results.Matches.Select(m => m.Page + 1).Distinct().ToArray();
        MatchPages.Text = pages.Length == 1 ? $"on page {pages[0]}"
            : string.Create(CultureInfo.CurrentCulture, $"on {pages.Length:N0} pages");
        MatchPages.ToolTip = "Pages: " + string.Join(", ", pages.Take(200)) + (pages.Length > 200 ? "…" : "");
    }
}
