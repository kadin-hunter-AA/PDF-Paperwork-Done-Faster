using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class FormFillIntegrationProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "form-fill-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var helper = Path.Combine(root, "App.Advanced", "worker.py");
        var location = new AdvancedEngineLocation(python, helper);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception(message);
        }

        var sourceHash = RecoveryStore.Fingerprint(fixture);
        var input = Path.Combine(work, "safe-inherited-form.pdf");
        MakeFixture(python, fixture, input, 0);
        var inputHash = RecoveryStore.Fingerprint(input);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, input);
        Check(session.SignatureCount == 1, "Test form retains the corpus's empty signature widget");
        await session.AuthorizeSignedCopyAsync();
        Check(session.SignedCopyAuthorized, "Existing signed-copy authorization is recorded before editing");
        var initialRevision = session.Revision;
        var before = await session.GetFormFieldsAsync(0);
        Check(before.GetProperty("Fields").EnumerateArray().Any(field =>
                field.GetProperty("Name").GetString() == "sample_name" &&
                field.GetProperty("Type").GetString() == "Text" &&
                !field.GetProperty("ReadOnly").GetBoolean() && !field.GetProperty("Password").GetBoolean()),
            "PDFium confirms the selected page contains the named plain text widget");

        const string requested = "café – déjà vu";
        await session.FillFormAsync(location, 0, "sample_name", requested, initialRevision);
        Check(session.Revision == initialRevision + 1 && session.IsDirty,
            "Accepted form fill advances the durable session history and marks the document dirty");
        Check(RecoveryStore.Fingerprint(input) == inputHash && RecoveryStore.Fingerprint(fixture) == sourceHash,
            "Filling preserves both selected source bytes and frozen reference bytes");
        var after = await session.GetFormFieldsAsync(0);
        Check(after.GetProperty("Fields").EnumerateArray().Single(field =>
                field.GetProperty("Name").GetString() == "sample_name").GetProperty("Value").GetString() == requested,
            "PDFium reopens the committed checkpoint with the exact Unicode field value");

        var checkboxBefore = after.GetProperty("Fields").EnumerateArray().Single(field =>
            field.GetProperty("Name").GetString() == "sample_check");
        Check(checkboxBefore.GetProperty("Type").GetString() == "Checkbox" &&
              !checkboxBefore.GetProperty("ReadOnly").GetBoolean(),
            "PDFium identifies the existing single checkbox as editable metadata");
        var checkboxRevision = session.Revision;
        var pixelsOff = (await session.RenderAsync(0, 700, 900)).GetProperty("Bgra").GetString();
        await session.FillFormAsync(location, 0, "sample_check", "true", checkboxRevision);
        Check(session.Revision == checkboxRevision + 1 && session.IsDirty,
            "Checkbox fill commits one durable session revision");
        var checkboxAfter = (await session.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
            .Single(field => field.GetProperty("Name").GetString() == "sample_check");
        Check(checkboxAfter.GetProperty("Value").GetString() is { Length: > 0 } checkedValue &&
              checkedValue is not ("Off" or "/Off"),
            "Independent PDFium inspection reads the selected checkbox value after commit");
        var pixelsOn = (await session.RenderAsync(0, 700, 900)).GetProperty("Bgra").GetString();
        Check(pixelsOn != pixelsOff,
            "PDFium form rendering changes when the saved checkbox appearance is selected");

        var recoveryId = session.Id;
        session.Dispose();
        using (var recovered = await DocumentSession.RecoverAsync(worker, store, recoveryId))
        {
            var recoveredField = (await recovered.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_name");
            Check(recovered.Revision == initialRevision + 2 && recovered.IsDirty &&
                  recoveredField.GetProperty("Value").GetString() == requested,
                "Crash-style recovery restores the committed text value");
            await recovered.UndoAsync();
            var undoCheckbox = (await recovered.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_check");
            Check(recovered.CanRedo && !IsChecked(undoCheckbox.GetProperty("Value")),
                "Undo restores the checkbox's original off state");
            await recovered.UndoAsync();
            var undoField = (await recovered.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_name");
            Check(recovered.CanRedo && undoField.GetProperty("Value").GetString() == "Corpus value",
                "Undo restores the preceding field value");
            await recovered.RedoAsync();
            var redoField = (await recovered.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_name");
            Check(redoField.GetProperty("Value").GetString() == requested,
                "Redo restores the filled field value");
            await recovered.RedoAsync();
            var redoCheckbox = (await recovered.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_check");
            Check(IsChecked(redoCheckbox.GetProperty("Value")), "Redo restores the selected checkbox value");
            var saved = Path.Combine(work, "saved-form.pdf");
            await recovered.SaveAsync(saved);
            Check(!recovered.IsDirty && File.Exists(saved), "Saving the filled form clears session dirty state");
            using var reopened = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(work, "saved-reopen-recovery")), saved);
            var savedValue = (await reopened.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_name")
                .GetProperty("Value").GetString();
            Check(savedValue == requested, "Saved PDF reopens through PDFium with the exact filled value");
            var savedCheckbox = (await reopened.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_check");
            Check(IsChecked(savedCheckbox.GetProperty("Value")),
                "Saved PDF reopens through PDFium with the selected checkbox value");
            var reopenedPixels = (await reopened.RenderAsync(0, 700, 900)).GetProperty("Bgra").GetString();
            Check(reopenedPixels != pixelsOff, "The saved/reopened checkbox appearance renders through PDFium");
            await reopened.AuthorizeSignedCopyAsync();
            await reopened.FillFormAsync(location, 0, "sample_check", "false", reopened.Revision);
            var uncheckedField = (await reopened.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_check");
            Check(!IsChecked(uncheckedField.GetProperty("Value")),
                "Explicit false intent selects the widget's existing off appearance state");
            var pixelsUnchecked = (await reopened.RenderAsync(0, 700, 900)).GetProperty("Bgra").GetString();
            Check(pixelsUnchecked == pixelsOff,
                "PDFium renders the existing off appearance after an independent false-intent update");
        }

        var refusalInput = Path.Combine(work, "readonly-form.pdf");
        MakeFixture(python, fixture, refusalInput, 1);
        var refusalHash = RecoveryStore.Fingerprint(refusalInput);
        using (var readOnly = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(work, "readonly-recovery")), refusalInput))
        {
            await readOnly.AuthorizeSignedCopyAsync();
            var revision = readOnly.Revision;
            await Rejected<InvalidOperationException>(
                () => readOnly.FillFormAsync(location, 0, "sample_name", "blocked", revision),
                "Read-only text fields are refused by the guarded fill engine");
            Check(readOnly.Revision == revision && RecoveryStore.Fingerprint(refusalInput) == refusalHash,
                "Read-only refusal leaves session revision unchanged");
        }

        var passwordInput = Path.Combine(work, "password-form.pdf");
        MakeFixture(python, fixture, passwordInput, 1 << 13);
        var passwordHash = RecoveryStore.Fingerprint(passwordInput);
        using (var password = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(work, "password-recovery")), passwordInput))
        {
            await password.AuthorizeSignedCopyAsync();
            var revision = password.Revision;
            var passwordField = (await password.GetFormFieldsAsync(0)).GetProperty("Fields").EnumerateArray()
                .Single(field => field.GetProperty("Name").GetString() == "sample_name");
            Check(passwordField.GetProperty("Password").GetBoolean() && passwordField.GetProperty("Value").ValueKind == JsonValueKind.Null,
                "PDFium inspection withholds a password field value");
            await Rejected<InvalidOperationException>(
                () => password.FillFormAsync(location, 0, "sample_name", "blocked", revision),
                "Password text fields are refused before the sidecar runs");
            Check(password.Revision == revision && RecoveryStore.Fingerprint(passwordInput) == passwordHash,
                "Password refusal preserves session revision and source bytes");
        }

        var staleInput = Path.Combine(work, "stale-form.pdf");
        MakeFixture(python, fixture, staleInput, 0);
        using (var stale = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(work, "stale-recovery")), staleInput))
        {
            await stale.AuthorizeSignedCopyAsync();
            var callsCanOnlyBeObservedByNoRevisionChange = stale.Revision;
            await stale.RotatePageAsync(0, 1);
            var current = stale.Revision;
            await Rejected<InvalidOperationException>(
                () => stale.FillFormAsync(location, 0, "sample_name", "stale", callsCanOnlyBeObservedByNoRevisionChange),
                "Stale form-fill selection is rejected before the sidecar runs");
            Check(stale.Revision == current, "Stale form-fill refusal leaves the current revision intact");
        }

        var cancelInput = Path.Combine(work, "cancel-form.pdf");
        MakeFixture(python, fixture, cancelInput, 0);
        var cancelHash = RecoveryStore.Fingerprint(cancelInput);
        using (var cancel = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(work, "cancel-recovery")), cancelInput))
        using (var cancellation = new CancellationTokenSource())
        {
            await cancel.AuthorizeSignedCopyAsync();
            var revision = cancel.Revision;
            cancellation.Cancel();
            await Rejected<OperationCanceledException>(
                () => cancel.FillFormAsync(location, 0, "sample_name", "canceled", revision, cancellation.Token),
                "Canceled form fill is stopped before candidate publication");
            Check(cancel.Revision == revision && !cancel.IsDirty &&
                  RecoveryStore.Fingerprint(cancelInput) == cancelHash,
                "Cancellation leaves the current session and source unchanged");
        }

        Check(RecoveryStore.Fingerprint(fixture) == sourceHash, "Integration proof leaves the frozen source PDF unchanged");
        var results = new { Passed = checks };
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} form-fill integration checks. Evidence: {work}");
    }

    static bool IsChecked(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return !string.IsNullOrEmpty(text) && text is not ("Off" or "/Off");
    }

    static void MakeFixture(string python, string source, string destination, int flags)
    {
        const string script = """
            import sys
            from pypdf import PdfReader, PdfWriter
            from pypdf.generic import ArrayObject, DictionaryObject, NameObject, NumberObject, FloatObject, TextStringObject, DecodedStreamObject
            src, dst, flags = sys.argv[1], sys.argv[2], int(sys.argv[3])
            writer = PdfWriter()
            writer.clone_document_from_reader(PdfReader(src))
            page = writer.pages[0]
            annots = page['/Annots']
            target_ref = None
            target_widget = None
            target_field = None
            for ref in list(annots):
                widget = ref.get_object()
                parent_ref = widget.get('/Parent')
                field = parent_ref.get_object() if parent_ref is not None else widget
                name = field.get('/T')
                if name == 'sample_name':
                    target_ref, target_widget, target_field = ref, widget, field
            if target_ref is None:
                raise RuntimeError('test text field is absent')
            acro = writer._root_object[NameObject('/AcroForm')].get_object()
            parent = DictionaryObject()
            for key in ('/FT','/T','/Ff','/V','/DV','/MaxLen','/DA'):
                if key in target_field:
                    parent[NameObject(key)] = target_field[key]
                    del target_field[NameObject(key)]
            parent[NameObject('/Ff')] = NumberObject(flags)
            parent[NameObject('/Kids')] = ArrayObject([target_ref])
            parent_ref = writer._add_object(parent)
            target_widget[NameObject('/Parent')] = parent_ref
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
            annots.append(widget_ref)
            acro[NameObject('/Fields')].append(widget_ref)
            with open(dst, 'wb') as stream:
                writer.write(stream)
            """;
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);
        start.ArgumentList.Add(flags.ToString());
        using var process = Process.Start(start) ?? throw new Exception("Could not start the isolated Python fixture generator.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception($"Isolated form fixture generation failed ({process.ExitCode}): {stderr} {stdout}");
    }
}
