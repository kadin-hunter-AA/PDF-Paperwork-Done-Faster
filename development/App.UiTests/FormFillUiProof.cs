using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HunterPdf.Core;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

static class FormFillUiProof
{
    internal static async Task RunAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "ui-form-fill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var engine = new AdvancedEngineLocation(Path.Combine(AppContext.BaseDirectory, "advanced", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "advanced", "worker.py"));
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var fixtureHash = RecoveryStore.Fingerprint(fixture);
        var source = Path.Combine(output, "existing-form.pdf");
        MakeFormFixture(python, fixture, source);
        var sourceHash = RecoveryStore.Fingerprint(source);

        var dialogs = new TestDialogs();
        var contrast = false;
        var shellStore = new RecoveryStore(Path.Combine(output, "shell-recovery"));
        var shell = new MainWindow(worker, shellStore, dialogs, () => contrast)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        shell.SetTheme(ShellTheme.Light);
        shell.Show();
        await WaitUntilAsync(() => !shell.IsBusy, "The host window did not finish loading.");
        await shell.OpenPathAsync(source);
        var session = shell.CurrentSession ?? throw new Exception("The test form did not open in the host window.");
        await session.AuthorizeSignedCopyAsync();
        var initialRevision = session.Revision;
        var initialFields = await session.GetFormFieldsAsync(0);
        Check(initialFields.GetProperty("Fields").EnumerateArray().Any(field =>
                field.GetProperty("Name").GetString() == "sample_name" &&
                field.GetProperty("Type").GetString() == "Text"),
            "The real dialog starts from the selected document's existing AcroForm text field");

        var cancelDialog = new FormFillWindow(session, 0, engine) { Owner = shell };
        shell.SetTheme(ShellTheme.Light);
        var cancelResult = ShowAndInteract(cancelDialog, async dialog =>
        {
            var fields = Find<ListBox>(dialog, "Existing form fields on the current page");
            await WaitUntilAsync(() => fields.IsEnabled && fields.Items.Count >= 2,
                "The form dialog did not load its page fields.");
            fields.SelectedIndex = 0;
            var selectedName = SelectedFieldName(fields);
            Check(selectedName == "sample_name", "Field selection loads the existing text field's name");
            var value = Find<TextBox>(dialog, "New value for the selected existing form field");
            var apply = Find<Button>(dialog, "Apply existing form field value");
            Check(value.IsEnabled && value.Text == "Corpus value", "Selecting a supported text field loads its saved value");
            value.Text = "CANCEL THIS VALUE";
            Check(apply.IsEnabled, "Changing the selected field enables the real Apply button");
            var heading = Heading(dialog);
            Check(HasContrast(heading, dialog, 4.5), "Light-theme form heading meets the text contrast threshold");
            Capture(dialog, Path.Combine(output, "form-fill-light.png"));
            FindButton(dialog, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !dialog.IsVisible, "The form dialog did not close from its Cancel button.");
        });
        Check(cancelResult == false && session.Revision == initialRevision && !session.IsDirty,
            "Cancel closes the real dialog without committing the edited field value");

        shell.SetTheme(ShellTheme.Dark);
        var applyDialog = new FormFillWindow(session, 0, engine) { Owner = shell };
        var applyResult = ShowAndInteract(applyDialog, async dialog =>
        {
            var fields = Find<ListBox>(dialog, "Existing form fields on the current page");
            await WaitUntilAsync(() => fields.IsEnabled && fields.Items.Count >= 2,
                "The dark-theme form dialog did not load its page fields.");
            fields.SelectedIndex = 0;
            Check(SelectedFieldName(fields) == "sample_name", "Dark-theme dialog selection still identifies the same field");
            var value = Find<TextBox>(dialog, "New value for the selected existing form field");
            value.Text = "café – déjà vu";
            var apply = Find<Button>(dialog, "Apply existing form field value");
            Check(apply.IsEnabled, "The Apply button is available for a changed supported field");
            Check(HasContrast(Heading(dialog), dialog, 4.5), "Dark-theme form heading meets the text contrast threshold");
            Capture(dialog, Path.Combine(output, "form-fill-dark.png"));

            contrast = true;
            shell.ApplyEffectiveTheme();
            dialog.UpdateLayout();
            var contrastHeading = Heading(dialog);
            var systemText = SystemColors.WindowTextBrush as SolidColorBrush;
            var actualText = contrastHeading.Foreground as SolidColorBrush;
            Check(shell.FindName("ContrastLabel") is TextBlock contrastLabel && contrastLabel.Visibility == Visibility.Visible,
                "The host announces high-contrast mode while the dialog is active");
            Check(systemText is not null && actualText is not null && systemText.Color == actualText.Color,
                "The dialog heading uses the Windows high-contrast system text color");
            Capture(dialog, Path.Combine(output, "form-fill-high-contrast.png"));
            contrast = false;
            shell.ApplyEffectiveTheme();

            var status = Descendants<TextBlock>(dialog).Single(text => text.Text.StartsWith("Select an existing", StringComparison.Ordinal) ||
                text.Text.StartsWith("Validating the changed field", StringComparison.Ordinal));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !dialog.IsVisible || !status.Text.StartsWith("Validating the changed field", StringComparison.Ordinal),
                "The form-fill Apply operation exceeded its bounded UI wait.", TimeSpan.FromSeconds(30));
            if (dialog.IsVisible) throw new InvalidOperationException("Form fill was refused in the dialog: " + status.Text);
        });
        Check(applyResult == true && session.Revision == initialRevision + 1 && session.IsDirty,
            "Applying through the real dialog commits one durable document revision");
        shell.ActiveTab?.Refresh();
        var filled = await session.GetFormFieldsAsync(0);
        Check(filled.GetProperty("Fields").EnumerateArray().Single(field =>
                field.GetProperty("Name").GetString() == "sample_name").GetProperty("Value").GetString() == "café – déjà vu",
            "The committed form value is visible after the dialog closes");

        var undoRevision = session.Revision;
        Check(session.CanUndo, "The form fill enables the host Undo command");
        ApplicationCommands.Undo.Execute(null, shell);
        await WaitUntilAsync(() => !shell.IsBusy && session.Revision > undoRevision,
            "The host Undo command did not complete.");
        shell.ActiveTab?.Refresh();
        var undone = await session.GetFormFieldsAsync(0);
        Check(undone.GetProperty("Fields").EnumerateArray().Single(field =>
                field.GetProperty("Name").GetString() == "sample_name").GetProperty("Value").GetString() == "Corpus value",
            "The real host Undo command restores the original form value");
        var undoAgainRevision = session.Revision;
        ApplicationCommands.Redo.Execute(null, shell);
        await WaitUntilAsync(() => !shell.IsBusy && session.Revision > undoAgainRevision,
            "The host Redo command did not complete.");
        shell.ActiveTab?.Refresh();
        var redone = await session.GetFormFieldsAsync(0);
        Check(redone.GetProperty("Fields").EnumerateArray().Single(field =>
                field.GetProperty("Name").GetString() == "sample_name").GetProperty("Value").GetString() == "café – déjà vu",
            "The real host Redo command restores the filled form value");

        var saved = Path.Combine(output, "saved-form.pdf");
        dialogs.NextSave = saved;
        ApplicationCommands.SaveAs.Execute(null, shell);
        await WaitUntilAsync(() => File.Exists(saved) && !shell.IsBusy && !session.IsDirty,
            "The host Save As command did not finish within its bound.");
        Check(File.Exists(saved), "The host Save As workflow writes the filled PDF");
        var oldTab = shell.ActiveTab ?? throw new Exception("The saved form tab disappeared.");
        await shell.CloseTabAsync(oldTab);
        await shell.OpenPathAsync(saved);
        var reopened = shell.CurrentSession ?? throw new Exception("The saved PDF did not reopen in the host window.");
        var reopenedValue = (await reopened.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
            .Single(field => field.GetProperty("Name").GetString() == "sample_name").GetProperty("Value").GetString();
        Check(reopenedValue == "café – déjà vu", "The saved PDF reopens through the host with the exact Unicode field value");

        await reopened.AuthorizeSignedCopyAsync();
        var checkboxDialog = new FormFillWindow(reopened, 0, engine) { Owner = shell };
        var checkboxResult = ShowAndInteract(checkboxDialog, async dialog =>
        {
            var list = Find<ListBox>(dialog, "Existing form fields on the current page");
            await WaitUntilAsync(() => list.IsEnabled && list.Items.Count >= 3,
                "The form dialog did not load the checkbox field.");
            list.SelectedItem = list.Items.Cast<object>().Single(item => SelectedFieldName(item) == "sample_check");
            var control = Find<CheckBox>(dialog, "New checked state for the selected existing checkbox field");
            Check(control.IsEnabled && control.IsChecked == false,
                "The dialog exposes an existing unchecked checkbox as an editable checkbox control");
            control.IsChecked = true;
            var apply = Find<Button>(dialog, "Apply existing form field value");
            Check(apply.IsEnabled, "Changing the checkbox enables the real Apply button");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = Descendants<TextBlock>(dialog).Single(text => text.Text.StartsWith("Select an existing", StringComparison.Ordinal) ||
                text.Text.StartsWith("Validating the changed field", StringComparison.Ordinal));
            await WaitUntilAsync(() => !dialog.IsVisible || !status.Text.StartsWith("Validating the changed field", StringComparison.Ordinal),
                "The checkbox Apply operation exceeded its bounded UI wait.", TimeSpan.FromSeconds(30));
            if (dialog.IsVisible) throw new InvalidOperationException("Checkbox fill was refused in the dialog: " + status.Text);
        });
        Check(checkboxResult == true, "Applying through the real dialog commits the existing checkbox");
        var committedCheck = (await reopened.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
            .Single(field => field.GetProperty("Name").GetString() == "sample_check");
        Check(IsChecked(committedCheck.GetProperty("Value")), "PDFium independently reads the checked value after UI commit");
        var savedCheckbox = Path.Combine(output, "saved-checked-form.pdf");
        dialogs.NextSave = savedCheckbox;
        ApplicationCommands.SaveAs.Execute(null, shell);
        await WaitUntilAsync(() => File.Exists(savedCheckbox) && !shell.IsBusy && !reopened.IsDirty,
            "The host did not save the checked form within its bound.");
        await shell.CloseTabAsync(shell.ActiveTab!);
        await shell.OpenPathAsync(savedCheckbox);
        var reopenedChecked = shell.CurrentSession ?? throw new Exception("The checked PDF did not reopen in the host window.");
        var reopenedCheckboxField = (await reopenedChecked.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
            .Single(field => field.GetProperty("Name").GetString() == "sample_check");
        Check(IsChecked(reopenedCheckboxField.GetProperty("Value")), "The saved checkbox state survives host save/reopen");
        var checkedRender = (await reopenedChecked.RenderAsync(0, 700, 900)).GetProperty("Bgra").GetString();
        Check(!string.IsNullOrEmpty(checkedRender), "The reopened checked form produces a PDFium form render");
        Check(RecoveryStore.Fingerprint(source) == sourceHash && RecoveryStore.Fingerprint(fixture) == fixtureHash,
            "The UI workflow preserves both generated input and frozen fixture bytes");

        await shell.CloseTabAsync(shell.ActiveTab!);
        shell.Close();
        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
        {
            Passed = checks,
            Captures = new[] { "form-fill-light.png", "form-fill-dark.png", "form-fill-high-contrast.png" },
            Cancellation = "Dialog Cancel button was exercised before apply; pending-engine cancellation was not simulated."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} real form-fill UI checks. Evidence: {output}");
    }

    static bool? ShowAndInteract(FormFillWindow dialog, Func<FormFillWindow, Task> interaction)
    {
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try { await interaction(dialog); }
            catch (Exception error)
            {
                failure = error;
                if (dialog.IsVisible) dialog.Close();
            }
        };
        var result = dialog.ShowDialog();
        if (failure is not null) throw new Exception("Form-fill UI interaction failed.", failure);
        return result;
    }

    static async Task WaitUntilAsync(Func<bool> ready, string message, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)))
            await Task.Delay(20);
        if (!ready()) throw new TimeoutException(message);
    }

    static T Find<T>(Window dialog, string automationName) where T : FrameworkElement =>
        Descendants<T>(dialog).Single(element => AutomationProperties.GetName(element) == automationName);

    static Button FindButton(Window dialog, string content) =>
        Descendants<Button>(dialog).Single(button => button.Content?.ToString() == content);

    static string SelectedFieldName(ListBox fields)
    {
        var selected = fields.SelectedItem ?? throw new Exception("The form dialog did not select a field.");
        return selected.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(selected)?.ToString() ?? "";
    }

    static string SelectedFieldName(object item) =>
        item.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)!.GetValue(item)?.ToString() ?? "";

    static bool IsChecked(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return !string.IsNullOrEmpty(text) && text is not ("Off" or "/Off");
    }

    static TextBlock Heading(FormFillWindow dialog) => Descendants<TextBlock>(dialog)
        .First(text => text.Text.StartsWith("Fill an existing form field", StringComparison.Ordinal));

    static bool HasContrast(TextBlock text, Window window, double threshold)
    {
        if (text.Foreground is not SolidColorBrush foreground || window.Background is not SolidColorBrush background)
            return false;
        return ContrastRatio(foreground.Color, background.Color) >= threshold;
    }

    static double ContrastRatio(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var s = value / 255d;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
        if (parent is Window window && window.Content is DependencyObject content && VisualTreeHelper.GetChildrenCount(parent) == 0)
        {
            if (content is T typed) yield return typed;
            foreach (var nested in Descendants<T>(content)) yield return nested;
        }
    }

    static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        if (content is Panel surface) surface.SetResourceReference(Panel.BackgroundProperty, "AppBackground");
        const int width = 620, height = 700;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static void MakeFormFixture(string python, string source, string destination)
    {
        const string script = """
            from pypdf import PdfReader, PdfWriter
            from pypdf.generic import ArrayObject, DictionaryObject, NameObject, NumberObject, FloatObject, TextStringObject, DecodedStreamObject
            import sys
            reader = PdfReader(sys.argv[1])
            writer = PdfWriter()
            writer.clone_document_from_reader(reader)
            page = writer.pages[0]
            target_ref = target_widget = target_field = None
            for ref in page['/Annots']:
                widget = ref.get_object()
                if widget.get('/Subtype') != '/Widget':
                    continue
                parent_ref = widget.get('/Parent')
                field = parent_ref.get_object() if parent_ref is not None else widget
                if field.get('/T') == 'sample_name':
                    target_ref, target_widget, target_field = ref, widget, field
                    break
            if target_ref is None:
                raise RuntimeError('sample text widget not found')
            parent = DictionaryObject()
            for key in ('/FT', '/T', '/Ff', '/V', '/DV', '/MaxLen', '/DA'):
                if key in target_field:
                    parent[NameObject(key)] = target_field[key]
                    del target_field[NameObject(key)]
            parent[NameObject('/Kids')] = ArrayObject([target_ref])
            parent_ref = writer._add_object(parent)
            target_widget[NameObject('/Parent')] = parent_ref
            acro = writer._root_object[NameObject('/AcroForm')].get_object()
            acro[NameObject('/Fields')] = ArrayObject(
                [ref for ref in acro[NameObject('/Fields')] if ref != target_ref] + [parent_ref])
            def appearance(data):
                stream = DecodedStreamObject()
                stream.set_data(data)
                stream.update({NameObject('/Type'): NameObject('/XObject'), NameObject('/Subtype'): NameObject('/Form'),
                               NameObject('/BBox'): ArrayObject([FloatObject(0), FloatObject(0), FloatObject(20), FloatObject(20)]),
                               NameObject('/Resources'): DictionaryObject()})
                return writer._add_object(stream)
            off = appearance(b'q 1 w 0 G 1 1 18 18 re S Q')
            on = appearance(b'q 1 w 0 G 1 1 18 18 re S 3 10 m 8 4 l 17 16 l S Q')
            widget = DictionaryObject({
                NameObject('/Type'): NameObject('/Annot'), NameObject('/Subtype'): NameObject('/Widget'),
                NameObject('/FT'): NameObject('/Btn'), NameObject('/T'): TextStringObject('sample_check'),
                NameObject('/Rect'): ArrayObject([FloatObject(420), FloatObject(700), FloatObject(440), FloatObject(720)]),
                NameObject('/F'): NumberObject(4), NameObject('/Ff'): NumberObject(0),
                NameObject('/V'): NameObject('/Off'), NameObject('/AS'): NameObject('/Off'),
                NameObject('/AP'): DictionaryObject({NameObject('/N'): DictionaryObject({NameObject('/Off'): off, NameObject('/Yes'): on})})})
            widget_ref = writer._add_object(widget)
            page['/Annots'].append(widget_ref)
            acro[NameObject('/Fields')].append(widget_ref)
            with open(sys.argv[2], 'wb') as stream:
                writer.write(stream)
            """;
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);
        using var process = Process.Start(start) ?? throw new Exception("Could not start the explicit companion Python fixture generator.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception($"Form UI fixture generation failed ({process.ExitCode}): {stderr} {stdout}");
    }
}
