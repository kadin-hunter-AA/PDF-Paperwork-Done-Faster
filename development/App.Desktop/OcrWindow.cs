using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using HunterPdf.Core;

namespace HunterPdf.Desktop;

/// <summary>Local OCR dialog: language packs, page choice, deskew, progress with cancel, and a result summary.</summary>
internal sealed class OcrWindow : Window
{
    readonly DocumentSession session;
    readonly AdvancedEngineLocation engine;
    readonly string[] tessdataDirectories;
    readonly int currentPage;
    readonly long revision;
    readonly Button apply = new() { Content = "Add searchable text", MinHeight = 44, IsDefault = true };
    readonly Button cancel = new() { Content = "Cancel", MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button more = new() { Content = "Get more languages…", MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly ProgressBar progress = new() { Height = 14, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    readonly StackPanel languagePanel = new();
    readonly RadioButton allScanned = new() { Content = "All scanned pages without text", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    readonly RadioButton thisPage = new() { Margin = new Thickness(0, 4, 0, 4) };
    readonly CheckBox deskew = new() { Content = "Straighten slightly tilted scans before recognizing", Margin = new Thickness(0, 4, 0, 4) };
    readonly CancellationTokenSource cancellation = new();
    bool working, closeRequested;

    /// <param name="model">Path of the bundled eng.traineddata; its folder is the bundled language folder.</param>
    /// <param name="native">Unused here (kept for the existing call site).</param>
    /// <param name="currentPage">Zero-based page shown in the viewer, for the "this page only" choice.</param>
    internal OcrWindow(DocumentSession session, AdvancedEngineLocation engine, string model, string native, int currentPage = 0)
    {
        this.session = session; this.engine = engine; revision = session.Revision;
        this.currentPage = Math.Clamp(currentPage, 0, Math.Max(0, session.PageCount - 1));
        tessdataDirectories = [Path.GetDirectoryName(Path.GetFullPath(model))!, TessdataCatalog.UserFolder];
        Title = "Recognize scanned text"; Width = 620; Height = 700; MinWidth = 500; MinHeight = 520; FontSize = 16;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(progress); footer.Children.Add(apply); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "Make a scan searchable", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"Recognition runs locally on this computer. Pages that already contain text are left untouched and reported. Your scans keep looking exactly the same; recognized text is invisible and may contain mistakes, so check it with Search afterwards. Undo removes all of it at once. This document has {session.PageCount} page{(session.PageCount == 1 ? "" : "s")}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        body.Children.Add(Heading("Languages (choose up to four)"));
        body.Children.Add(languagePanel); body.Children.Add(more);
        body.Children.Add(Heading("Pages"));
        thisPage.Content = $"Current page only (page {this.currentPage + 1})";
        body.Children.Add(allScanned); body.Children.Add(thisPage);
        body.Children.Add(Heading("Options"));
        body.Children.Add(deskew);
        body.Children.Add(new TextBlock { Text = "Sideways or upside-down scans are detected automatically by trial recognition, which can add a few seconds per page.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 14 });
        foreach (var text in body.Children.OfType<TextBlock>()) text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var control in new Control[] { allScanned, thisPage, deskew }) control.SetResourceReference(ForegroundProperty, "TextBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(apply, "Recognize text in this document locally");
        AutomationProperties.SetName(more, "Download more OCR languages");
        AutomationProperties.SetName(progress, "OCR progress");
        RefreshLanguages();
        apply.Click += async (_, _) => await ApplyAsync();
        more.Click += (_, _) => ShowMoreLanguages();
        cancel.Click += (_, _) => { if (working) { closeRequested = true; cancellation.Cancel(); } else DialogResult = false; };
        Loaded += (_, _) => { if (Owner is not null) { Resources.MergedDictionaries.Add(Owner.Resources); Icon = Owner.Icon; } };
        Closing += (_, args) => { if (working) { args.Cancel = true; closeRequested = true; cancellation.Cancel(); } };
        Closed += (_, _) => cancellation.Dispose();
    }

    TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return block;
    }

    void RefreshLanguages()
    {
        var selected = languagePanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet();
        languagePanel.Children.Clear();
        foreach (var language in TessdataCatalog.Installed(tessdataDirectories))
        {
            var box = new CheckBox { Content = language.Name, Tag = language.Code, Margin = new Thickness(0, 4, 0, 4),
                IsChecked = selected.Contains(language.Code) || selected.Count == 0 && language.Code == "eng" };
            box.SetResourceReference(ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(box, language.Name + " language");
            languagePanel.Children.Add(box);
        }
        more.IsEnabled = !working && TessdataCatalog.Languages.Any(l => !languagePanel.Children.OfType<CheckBox>().Any(c => (string)c.Tag == l.Code));
    }

    void ShowMoreLanguages()
    {
        var menu = new ContextMenu { PlacementTarget = more };
        foreach (var language in TessdataCatalog.Languages.Where(l => !languagePanel.Children.OfType<CheckBox>().Any(c => (string)c.Tag == l.Code)))
        {
            var item = new MenuItem { Header = $"{language.Name} ({language.SizeBytes / 1048576.0:0.0} MB)…" };
            var chosen = language;
            item.Click += async (_, _) => await DownloadAsync(chosen);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    async Task DownloadAsync(TessdataLanguage language)
    {
        var answer = MessageBox.Show(this,
            $"Download the {language.Name} language pack ({language.SizeBytes / 1048576.0:0.0} MB)?\n\nIt is fetched once from the public Tesseract language-data project on GitHub (raw.githubusercontent.com), checked against a fixed fingerprint, and stored for your Windows account only. No document content is sent.",
            "Download language pack", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        working = true; apply.IsEnabled = false; more.IsEnabled = false;
        progress.Visibility = Visibility.Visible; progress.Maximum = language.SizeBytes; progress.Value = 0;
        status.Text = $"Downloading {language.Name}…";
        try
        {
            await TessdataCatalog.DownloadAsync(language, consent: true, new Progress<long>(bytes => progress.Value = bytes), cancellation.Token);
            status.Text = $"{language.Name} is ready.";
        }
        catch (OperationCanceledException) { status.Text = "Download cancelled."; }
        catch (Exception error) { status.Text = error is IOException or InvalidOperationException ? error.Message : "The language pack could not be downloaded."; }
        finally { working = false; apply.IsEnabled = true; progress.Visibility = Visibility.Collapsed; RefreshLanguages(); if (closeRequested && IsVisible) DialogResult = false; }
    }

    async Task ApplyAsync()
    {
        if (working) return;
        var languages = languagePanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (languages.Count is < 1 or > 4) { status.Text = "Choose between one and four languages."; return; }
        working = true; apply.IsEnabled = false; more.IsEnabled = false; cancel.Content = "Cancel recognition";
        progress.Visibility = Visibility.Visible; progress.Value = 0; progress.Maximum = 1; progress.IsIndeterminate = true;
        status.Text = "Preparing recognition…";
        try
        {
            var options = new OcrPagesOptions(thisPage.IsChecked == true ? [currentPage] : null, languages, tessdataDirectories, deskew.IsChecked == true);
            var report = new Progress<(int done, int total)>(p =>
            {
                progress.IsIndeterminate = false; progress.Maximum = Math.Max(1, p.total); progress.Value = p.done;
                status.Text = $"Recognized {p.done} of {p.total} page{(p.total == 1 ? "" : "s")}. You can cancel; nothing is kept until all pages finish.";
            });
            var result = await session.RecognizePagesAsync(engine, options, revision, report, cancellation.Token);
            var lines = new List<string> { $"Recognized text on {result.Recognized.Count} page{(result.Recognized.Count == 1 ? "" : "s")}." };
            if (result.SkippedHasText.Count > 0) lines.Add($"{result.SkippedHasText.Count} page{(result.SkippedHasText.Count == 1 ? "" : "s")} skipped because {(result.SkippedHasText.Count == 1 ? "it already has" : "they already have")} text (page {string.Join(", ", result.SkippedHasText.Select(p => p + 1))}).");
            if (result.NoTextFound.Count > 0) lines.Add($"No text was found on page {string.Join(", ", result.NoTextFound.Select(p => p + 1))}.");
            if (result.SkippedNoImage.Count > 0) lines.Add($"Page {string.Join(", ", result.SkippedNoImage.Select(p => p + 1))} has no scanned image.");
            if (result.SkippedTooLarge.Count > 0) lines.Add($"Page {string.Join(", ", result.SkippedTooLarge.Select(p => p + 1))} is too large to recognize.");
            var turned = result.Recognized.Where(r => r.RotationDegrees != 0).Select(r => r.Page + 1).ToList();
            if (turned.Count > 0) lines.Add($"Turned scans were detected on page {string.Join(", ", turned)}; the text layer follows the scan, and the page view itself is unchanged.");
            Summary = string.Join(" ", lines);
            working = false; DialogResult = true;
        }
        catch (OperationCanceledException) { status.Text = "Recognition was cancelled. No change was accepted."; }
        catch (Exception error) { status.Text = error is ArgumentException or InvalidOperationException or IOException ? error.Message : "Recognition failed safely. No change was accepted."; }
        finally
        {
            working = false; progress.Visibility = Visibility.Collapsed; progress.IsIndeterminate = false;
            apply.IsEnabled = !cancellation.IsCancellationRequested; cancel.Content = "Close"; RefreshLanguages();
            if (closeRequested && IsVisible) DialogResult = false;
        }
    }

    /// <summary>Plain-language result of the last successful run (pages recognized and skipped).</summary>
    internal string Summary { get; private set; } = "";
}
