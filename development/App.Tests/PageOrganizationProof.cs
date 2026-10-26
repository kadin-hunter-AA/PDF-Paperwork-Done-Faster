using HunterPdf.Core;

static class PageOrganizationProof
{
    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception(message);
        }

        var work = Path.Combine(output, "page-organization"); Directory.CreateDirectory(work);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        var copy = Path.Combine(work, "source.pdf"); File.Copy(source, copy);
        var sourceHash = RecoveryStore.Fingerprint(source);
        var copyHash = RecoveryStore.Fingerprint(copy);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");

        using var session = await DocumentSession.OpenAsync(worker, store, copy);
        Check(session.PageCount == 2 && !session.IsDirty, "Opening establishes the original two-page baseline");
        await session.MovePagesAsync([1], 0);
        Check(session.PageCount == 2 && session.IsDirty && session.CanUndo, "Reordering commits a reversible checkpoint without changing page count");
        var first = (await session.ExtractAsync(0)).GetProperty("Text").GetString()!;
        var second = (await session.ExtractAsync(1)).GetProperty("Text").GetString()!;
        Check(first.Contains("Source page two", StringComparison.Ordinal) && second.Contains("Source page one", StringComparison.Ordinal),
            "In-place reorder changes page order while retaining page content");
        await session.UndoAsync();
        Check(session.PageCount == 2 && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page one", StringComparison.Ordinal),
            "Undo restores original page order and count");
        await session.RedoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal),
            "Redo reapplies page order");
        await Rejected<InvalidOperationException>(() => session.MovePagesAsync([0, 0], 0), "Duplicate page index rejected");
        await Rejected<InvalidOperationException>(() => session.MovePagesAsync([0], 2), "Out-of-range destination rejected");
        await Rejected<InvalidOperationException>(() => session.MovePagesAsync([] , 0), "Empty page selection rejected");
        await Rejected<InvalidOperationException>(() => session.MovePagesAsync(Enumerable.Range(0, 50_001).ToArray(), 0), "Move request above the 50,000-index limit rejected");
        Check(session.PageCount == 2 && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal),
            "Rejected reorder preserves current checkpoint and order");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Rejected<OperationCanceledException>(() => session.MovePagesAsync([1], 0, canceled.Token), "Canceled reorder is surfaced");
        }
        Check(session.PageCount == 2 && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal),
            "Canceled reorder preserves checkpoint and page order");

        var duplicateSource = Path.Combine(work, "duplicate-source.pdf"); File.Copy(source, duplicateSource);
        var duplicateSourceHash = RecoveryStore.Fingerprint(duplicateSource);
        var frozenSourceHash = RecoveryStore.Fingerprint(source);
        var duplicatePath = Path.Combine(work, "duplicated-pages.pdf");
        string[] expectedDuplicateText;
        double[] expectedDuplicateWidths;
        using (var duplicateSession = await DocumentSession.OpenAsync(worker, store, duplicateSource))
        {
            var originalPages = (await duplicateSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            var originalText = new[]
            {
                (await duplicateSession.ExtractAsync(0)).GetProperty("Text").GetString()!,
                (await duplicateSession.ExtractAsync(1)).GetProperty("Text").GetString()!
            };
            var originalWidths = originalPages.Select(page => page.GetProperty("Width").GetDouble()).ToArray();
            var revision = duplicateSession.Revision;
            await duplicateSession.DuplicatePagesAsync([1, 0], 1);
            expectedDuplicateText = [originalText[0], originalText[1], originalText[0], originalText[1]];
            expectedDuplicateWidths = [originalWidths[0], originalWidths[1], originalWidths[0], originalWidths[1]];
            Check(duplicateSession.PageCount == 4 && duplicateSession.Revision == revision + 1 && duplicateSession.CanUndo,
                "Duplicating selected pages commits one reversible checkpoint and increments the page count");
            var actualText = new string[4];
            for (var i = 0; i < actualText.Length; i++) actualText[i] = (await duplicateSession.ExtractAsync(i)).GetProperty("Text").GetString()!;
            Check(actualText.SequenceEqual(expectedDuplicateText),
                "Duplicate insertion preserves requested selection order and all original page text");
            var actualWidths = (await duplicateSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray()
                .Select(page => page.GetProperty("Width").GetDouble()).ToArray();
            Check(actualWidths.SequenceEqual(expectedDuplicateWidths),
                "Duplicated pages retain source geometry at each insertion position");
            await duplicateSession.UndoAsync();
            Check(duplicateSession.PageCount == 2 && duplicateSession.Revision == revision + 2 && duplicateSession.CanRedo,
                "Undo removes the duplicated pages and restores the two-page checkpoint");
            await duplicateSession.RedoAsync();
            Check(duplicateSession.PageCount == 4 && duplicateSession.Revision == revision + 3,
                "Redo restores the duplicated page checkpoint");
            await duplicateSession.SaveAsync(duplicatePath);
            await duplicateSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(source) == frozenSourceHash && RecoveryStore.Fingerprint(duplicateSource) == duplicateSourceHash,
            "Page duplication preserves frozen fixture and session source bytes");
        using (var duplicateReopened = await DocumentSession.OpenAsync(worker, store, duplicatePath))
        {
            Check(duplicateReopened.PageCount == 4, "Saved duplicated pages reopen with the expected count");
            var reopenedText = new string[4];
            for (var i = 0; i < reopenedText.Length; i++) reopenedText[i] = (await duplicateReopened.ExtractAsync(i)).GetProperty("Text").GetString()!;
            var reopenedWidths = (await duplicateReopened.PageInfoAsync()).GetProperty("Pages").EnumerateArray()
                .Select(page => page.GetProperty("Width").GetDouble()).ToArray();
            Check(reopenedText.SequenceEqual(expectedDuplicateText) && reopenedWidths.SequenceEqual(expectedDuplicateWidths),
                "Saved and reopened duplication retains page order, text, and geometry");
            await duplicateReopened.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(duplicateSource) == duplicateSourceHash,
            "Save and reopen leave the duplicated session source PDF unchanged");

        var unsafeDuplicate = Path.Combine(work, "unsafe-duplicate-source.pdf"); File.Copy(
            Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf"), unsafeDuplicate);
        var unsafeDuplicateHash = RecoveryStore.Fingerprint(unsafeDuplicate);
        using (var unsafeSession = await DocumentSession.OpenAsync(worker, store, unsafeDuplicate))
        {
            var revision = unsafeSession.Revision;
            await unsafeSession.AuthorizeSignedCopyAsync();
            await Rejected<InvalidOperationException>(() => unsafeSession.DuplicatePagesAsync([0], 0),
                "Duplicating a page with annotations or widgets is refused as unsafe");
            Check(unsafeSession.PageCount == 1 && unsafeSession.Revision == revision && !unsafeSession.IsDirty && !unsafeSession.CanUndo,
                "Unsafe duplicate refusal rolls back to the original checkpoint without publishing a revision");
            await unsafeSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(unsafeDuplicate) == unsafeDuplicateHash,
            "Unsafe duplicate refusal preserves source bytes");

        var dimensions = (await session.PageInfoAsync()).GetProperty("Pages").EnumerateArray().First();
        var width = dimensions.GetProperty("Width").GetDouble();
        var height = dimensions.GetProperty("Height").GetDouble();
        await session.InsertBlankPageAsync(1, width, height);
        Check(session.PageCount == 3 && session.CanUndo, "Blank insertion increments page count only after checkpoint commit");
        Check(string.IsNullOrWhiteSpace((await session.ExtractAsync(1)).GetProperty("Text").GetString()), "Inserted page has no text content");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal) &&
              (await session.ExtractAsync(2)).GetProperty("Text").GetString()!.Contains("Source page one", StringComparison.Ordinal),
            "Blank insertion preserves existing page order and content");
        await session.UndoAsync();
        Check(session.PageCount == 2, "Undo removes inserted page by restoring the prior checkpoint");
        await session.RedoAsync();
        Check(session.PageCount == 3, "Redo restores inserted page count");

        await Rejected<InvalidOperationException>(() => session.InsertBlankPageAsync(4, width, height), "Out-of-range insertion rejected");
        await Rejected<InvalidOperationException>(() => session.InsertBlankPageAsync(1, 0, height), "Out-of-range page dimensions rejected");
        Check(session.PageCount == 3, "Rejected insertion preserves page count");

        var saved = Path.Combine(work, "page-organized.pdf"); await session.SaveAsync(saved);
        Check(RecoveryStore.Fingerprint(source) == sourceHash && RecoveryStore.Fingerprint(copy) == copyHash,
            "Save preserves the frozen input and the session source bytes");
        using (var reopened = await DocumentSession.OpenAsync(worker, store, saved))
        {
            Check(reopened.PageCount == 3, "Saved page organization reopens with the expected page count");
            Check(string.IsNullOrWhiteSpace((await reopened.ExtractAsync(1)).GetProperty("Text").GetString()), "Saved blank page independently reopens");
            Check((await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Source page two", StringComparison.Ordinal) &&
                  (await reopened.ExtractAsync(2)).GetProperty("Text").GetString()!.Contains("Source page one", StringComparison.Ordinal),
                "Saved/reopened output retains reordered source pages");
            await reopened.CloseAsync();
        }

        var signed = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        using (var signedSession = await DocumentSession.OpenAsync(worker, store, signed))
        {
            await Rejected<SignedDocumentException>(() => signedSession.MovePagesAsync([0], 0), "Signed reorder requires explicit signed-copy consent");
            await Rejected<SignedDocumentException>(() => signedSession.InsertBlankPageAsync(0, width, height), "Signed page operation requires explicit signed-copy consent");
            Check(!signedSession.IsDirty, "Rejected signed page operation leaves checkpoint unchanged");
            await signedSession.CloseAsync();
        }

        var interactive = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var interactiveHash = RecoveryStore.Fingerprint(interactive);
        using (var interactiveSession = await DocumentSession.OpenAsync(worker, store, interactive))
        {
            var before = (await interactiveSession.BookmarksAsync()).GetProperty("Bookmarks").EnumerateArray().First();
            Check(before.GetProperty("Page").GetInt32() == 0, "Interactive fixture bookmark initially targets its only page");
            await Rejected<SignedDocumentException>(() => interactiveSession.InsertBlankPageAsync(0, 612, 792),
                "Unsigned signature field is protected until signed-copy consent is explicit");
            Check(!interactiveSession.IsDirty && interactiveSession.PageCount == 1,
                "Rejected insertion on a document with a signature field preserves its checkpoint");
            await interactiveSession.AuthorizeSignedCopyAsync();
            await interactiveSession.InsertBlankPageAsync(0, 612, 792);
            var after = (await interactiveSession.BookmarksAsync()).GetProperty("Bookmarks").EnumerateArray().First();
            Check(after.GetProperty("Page").GetInt32() == 1 && interactiveSession.PageCount == 2,
                "Blank insertion preserves a bookmark target to the original page object");
            var organized = Path.Combine(work, "interactive-inserted.pdf"); await interactiveSession.SaveAsync(organized);
            using var reopenedInteractive = await DocumentSession.OpenAsync(worker, store, organized);
            var reopenedBookmark = (await reopenedInteractive.BookmarksAsync()).GetProperty("Bookmarks").EnumerateArray().First();
            Check(reopenedBookmark.GetProperty("Page").GetInt32() == 1 && reopenedInteractive.PageCount == 2,
                "Saved interactive page insertion reopens with bookmark destination intact");
            Check((await reopenedInteractive.ExtractAsync(1)).GetProperty("Annotations").GetArrayLength() > 0,
                "Original page annotations and form widgets remain attached after insertion");
            await reopenedInteractive.CloseAsync();
            await interactiveSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(interactive) == interactiveHash, "Interactive compatibility fixture remains unchanged");

        var deleteCopy = Path.Combine(work, "delete-source.pdf"); File.Copy(source, deleteCopy);
        var deleteCopyHash = RecoveryStore.Fingerprint(deleteCopy);
        var deletedPath = Path.Combine(work, "deleted-page.pdf");
        string deletedText;
        using (var deleteSession = await DocumentSession.OpenAsync(worker, store, deleteCopy))
        {
            var originalFirst = (await deleteSession.ExtractAsync(0)).GetProperty("Text").GetString()!;
            var originalSecond = (await deleteSession.ExtractAsync(1)).GetProperty("Text").GetString()!;
            var revision = deleteSession.Revision;
            await deleteSession.DeletePagesAsync([1]);
            Check(deleteSession.PageCount == 1 && deleteSession.Revision == revision + 1 && deleteSession.CanUndo,
                "Deleting the current page commits one reversible checkpoint and updates the page count");
            Check((await deleteSession.ExtractAsync(0)).GetProperty("Text").GetString() == originalFirst,
                "Deleting a page preserves the remaining page content");
            await deleteSession.UndoAsync();
            Check(deleteSession.PageCount == 2 && (await deleteSession.ExtractAsync(1)).GetProperty("Text").GetString() == originalSecond,
                "Undo restores the deleted page from its full checkpoint");
            await deleteSession.RedoAsync();
            Check(deleteSession.PageCount == 1, "Redo reapplies page deletion");
            var deletedRevision = deleteSession.Revision;
            await Rejected<InvalidOperationException>(() => deleteSession.DeletePagesAsync([0]),
                "Deleting the final page is rejected");
            Check(deleteSession.PageCount == 1 && deleteSession.Revision == deletedRevision && deleteSession.IsDirty,
                "Rejected final-page deletion preserves the committed checkpoint and revision");
            deletedText = (await deleteSession.ExtractAsync(0)).GetProperty("Text").GetString()!;
            await deleteSession.SaveAsync(deletedPath);
            await deleteSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(deleteCopy) == deleteCopyHash, "Page deletion preserves its source PDF bytes");
        using (var deletedReopened = await DocumentSession.OpenAsync(worker, store, deletedPath))
        {
            Check(deletedReopened.PageCount == 1 && (await deletedReopened.ExtractAsync(0)).GetProperty("Text").GetString() == deletedText,
                "Saved deletion reopens with the same readable page");
            await deletedReopened.CloseAsync();
        }

        var numericDestination = Path.Combine(work, "numeric-destination.pdf");
        CreateNumericDestinationPdf(numericDestination);
        var numericHash = RecoveryStore.Fingerprint(numericDestination);
        using (var numericSession = await DocumentSession.OpenAsync(worker, store, numericDestination))
        {
            var revision = numericSession.Revision;
            await Rejected<InvalidOperationException>(() => numericSession.MovePagesAsync([2], 0),
                "Move that cannot preserve an ordinal internal link target is rejected");
            Check(numericSession.Revision == revision && !numericSession.IsDirty && numericSession.PageCount == 3,
                "Rejected navigation remap publishes no checkpoint or revision");
            var restoredWidths = (await numericSession.PageInfoAsync()).GetProperty("Pages")
                .EnumerateArray().Select(page => page.GetProperty("Width").GetDouble()).ToArray();
            Check(restoredWidths.SequenceEqual([612d, 700d, 800d]),
                "Rejected navigation remap reloads the original page order from the checkpoint");
            await numericSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(numericDestination) == numericHash,
            "Rejected navigation remap preserves its original source bytes");

        var importTarget = Path.Combine(work, "import-target.pdf"); File.Copy(source, importTarget);
        var importSource = Path.Combine(work, "import-source.pdf"); File.Copy(source, importSource);
        var importTargetHash = RecoveryStore.Fingerprint(importTarget);
        var importSourceHash = RecoveryStore.Fingerprint(importSource);
        var importOutput = Path.Combine(work, "imported-pages.pdf");
        string[] importExpectedText;
        double[] importExpectedWidths;
        using (var importSession = await DocumentSession.OpenAsync(worker, store, importTarget))
        {
            var beforeText = new[] { (await importSession.ExtractAsync(0)).GetProperty("Text").GetString()!, (await importSession.ExtractAsync(1)).GetProperty("Text").GetString()! };
            var sourcePages = (await importSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            var beforeWidths = sourcePages.Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            var revision = importSession.Revision;
            await importSession.InsertPagesAsync(importSource, [0, 1], 1);
            importExpectedText = [beforeText[0], beforeText[0], beforeText[1], beforeText[1]];
            importExpectedWidths = [beforeWidths[0], beforeWidths[0], beforeWidths[1], beforeWidths[1]];
            Check(importSession.PageCount == 4 && importSession.Revision == revision + 1 && importSession.CanUndo,
                "Import commits every selected source page as one reversible checkpoint");
            var actual = new string[4]; for (var i = 0; i < actual.Length; i++) actual[i] = (await importSession.ExtractAsync(i)).GetProperty("Text").GetString()!;
            var widths = (await importSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray().Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            Check(actual.SequenceEqual(importExpectedText) && widths.SequenceEqual(importExpectedWidths),
                "Imported page order, readable content, and geometry match source pages");
            await importSession.UndoAsync();
            Check(importSession.PageCount == 2 && importSession.CanRedo, "Undo removes all imported pages together");
            await importSession.RedoAsync();
            Check(importSession.PageCount == 4, "Redo restores the imported page checkpoint");
            await importSession.SaveAsync(importOutput);
            await importSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(importTarget) == importTargetHash && RecoveryStore.Fingerprint(importSource) == importSourceHash && RecoveryStore.Fingerprint(source) == sourceHash,
            "Import and save preserve both source PDFs and the frozen fixture bytes");
        using (var importedReopened = await DocumentSession.OpenAsync(worker, store, importOutput))
        {
            var text = new string[4]; for (var i = 0; i < text.Length; i++) text[i] = (await importedReopened.ExtractAsync(i)).GetProperty("Text").GetString()!;
            var widths = (await importedReopened.PageInfoAsync()).GetProperty("Pages").EnumerateArray().Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            Check(text.SequenceEqual(importExpectedText) && widths.SequenceEqual(importExpectedWidths), "Saved import reopens with page order, content, and geometry intact");
            await importedReopened.CloseAsync();
        }

        // Selective import exercises the exact ordered subset produced by the
        // organizer's 1-based range parser, without importing the other source page.
        var selectiveTarget = Path.Combine(work, "selective-import-target.pdf"); File.Copy(source, selectiveTarget);
        var selectiveSource = Path.Combine(work, "selective-import-source.pdf"); File.Copy(source, selectiveSource);
        var selectiveTargetHash = RecoveryStore.Fingerprint(selectiveTarget);
        var selectiveSourceHash = RecoveryStore.Fingerprint(selectiveSource);
        var selectiveOutput = Path.Combine(work, "selective-imported-pages.pdf");
        using (var selectiveSession = await DocumentSession.OpenAsync(worker, store, selectiveTarget))
        {
            var targetText = new[]
            {
                (await selectiveSession.ExtractAsync(0)).GetProperty("Text").GetString()!,
                (await selectiveSession.ExtractAsync(1)).GetProperty("Text").GetString()!
            };
            // Source page 2 is selected alone, so the resulting order must be
            // target page 1, source page 2, target page 2.
            var sourceSession = await DocumentSession.OpenAsync(worker, store, selectiveSource);
            var selectedSourceText = (await sourceSession.ExtractAsync(1)).GetProperty("Text").GetString()!;
            var sourcePageWidths = (await sourceSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray()
                .Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            var targetWidths = (await selectiveSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray()
                .Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            var revision = selectiveSession.Revision;
            await selectiveSession.InsertPagesAsync(selectiveSource, [1], 1);
            var expected = new[] { targetText[0], selectedSourceText, targetText[1] };
            var actual = new string[3];
            for (var i = 0; i < actual.Length; i++) actual[i] = (await selectiveSession.ExtractAsync(i)).GetProperty("Text").GetString()!;
            var widths = (await selectiveSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray()
                .Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            Check(selectiveSession.PageCount == 3 && selectiveSession.Revision == revision + 1 && actual.SequenceEqual(expected),
                "Selective import inserts only the requested source page after the current page in one checkpoint");
            Check(widths.SequenceEqual([targetWidths[0], sourcePageWidths[1], targetWidths[1]]),
                "Selective import preserves selected-page geometry and neighboring target-page order");
            await selectiveSession.UndoAsync();
            Check(selectiveSession.PageCount == 2 && selectiveSession.CanRedo,
                "Undo removes the selective import without removing either original target page");
            await selectiveSession.RedoAsync();
            await selectiveSession.SaveAsync(selectiveOutput);
            await selectiveSession.CloseAsync();
            await sourceSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(selectiveTarget) == selectiveTargetHash &&
              RecoveryStore.Fingerprint(selectiveSource) == selectiveSourceHash,
            "Selective import and save preserve both input PDF byte streams");
        using (var selectiveReopened = await DocumentSession.OpenAsync(worker, store, selectiveOutput))
        {
            var text = new string[3];
            for (var i = 0; i < text.Length; i++) text[i] = (await selectiveReopened.ExtractAsync(i)).GetProperty("Text").GetString()!;
            Check(selectiveReopened.PageCount == 3 && text.SequenceEqual(
                  [importExpectedText[0], importExpectedText[2], importExpectedText[2]]),
                "Saved selective import reopens with only the requested source page in the correct position");
            await selectiveReopened.CloseAsync();
        }
        using (var signedImport = await DocumentSession.OpenAsync(worker, store, signed))
        {
            await Rejected<SignedDocumentException>(() => signedImport.InsertPagesAsync(importSource, [0], 0), "Import into signed documents requires signed-copy consent");
            Check(!signedImport.IsDirty && signedImport.PageCount == 1, "Signed import refusal leaves its checkpoint unchanged");
            await signedImport.CloseAsync();
        }
        using (var unsafeSourceImport = await DocumentSession.OpenAsync(worker, store, importTarget))
        {
            var revision = unsafeSourceImport.Revision;
            await Rejected<InvalidOperationException>(() => unsafeSourceImport.InsertPagesAsync(unsafeDuplicate, [0], 0), "Import refuses interactive form-bearing source PDFs");
            Check(unsafeSourceImport.PageCount == 2 && unsafeSourceImport.Revision == revision && !unsafeSourceImport.IsDirty,
                "Unsafe import source refusal rolls back without publishing a checkpoint");
            await unsafeSourceImport.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(unsafeDuplicate) == unsafeDuplicateHash,
            "Refusing an interactive import source leaves its bytes unchanged");

        var exportSource = Path.Combine(work, "export-source.pdf"); File.Copy(source, exportSource);
        var exportHash = RecoveryStore.Fingerprint(exportSource);
        var exportPath = Path.Combine(work, "extracted.pdf");
        string[] exportTexts; double[] exportWidths; double[] exportHeights;
        using (var exportSession = await DocumentSession.OpenAsync(worker, store, exportSource))
        {
            exportTexts = new[] { (await exportSession.ExtractAsync(0)).GetProperty("Text").GetString()!, (await exportSession.ExtractAsync(1)).GetProperty("Text").GetString()! };
            var info = (await exportSession.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            exportWidths = info.Select(p => p.GetProperty("Width").GetDouble()).ToArray();
            exportHeights = info.Select(p => p.GetProperty("Height").GetDouble()).ToArray();
            var result = await exportSession.ExtractPagesAsync([1, 0], exportPath);
            Check(result.GetProperty("PageCount").GetInt32() == 2 && File.Exists(exportPath), "Selected-page extraction creates one new PDF with the reported page count");
            await Rejected<InvalidOperationException>(() => exportSession.ExtractPagesAsync([0], exportPath), "Extraction refuses an existing output PDF");
            Check(!exportSession.IsDirty && exportSession.PageCount == 2, "Extraction leaves the open source session unchanged");
            await exportSession.CloseAsync();
        }
        using (var extracted = await DocumentSession.OpenAsync(worker, store, exportPath))
        {
            var text = new[] { (await extracted.ExtractAsync(0)).GetProperty("Text").GetString()!, (await extracted.ExtractAsync(1)).GetProperty("Text").GetString()! };
            var info = (await extracted.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
            Check(extracted.PageCount == 2 && text.SequenceEqual(exportTexts.Reverse()), "Extracted PDF reopens with selected pages in requested order and retained text");
            Check(info.Select(p => p.GetProperty("Width").GetDouble()).SequenceEqual(exportWidths.Reverse()) && info.Select(p => p.GetProperty("Height").GetDouble()).SequenceEqual(exportHeights.Reverse()),
                "Extracted PDF retains each selected page's geometry");
            await extracted.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(exportSource) == exportHash, "Extraction and output reopen preserve the source PDF bytes");

        var splitParent = Path.Combine(work, "split-parent"); Directory.CreateDirectory(splitParent);
        var splitFolder = Path.Combine(splitParent, "pages");
        using (var splitSession = await DocumentSession.OpenAsync(worker, store, exportSource))
        {
            var result = await splitSession.SplitPagesAsync(splitFolder);
            Check(result.GetProperty("PageCount").GetInt32() == 2 && Directory.Exists(splitFolder), "Split creates a new output folder and reports every source page");
            var files = Directory.GetFiles(splitFolder, "*.pdf").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            Check(files.Select(Path.GetFileName).SequenceEqual(new[] { "page-0001.pdf", "page-0002.pdf" }), "Split emits one deterministically named PDF per page");
            for (var i = 0; i < files.Length; i++)
            {
                using var one = await DocumentSession.OpenAsync(worker, store, files[i]);
                Check(one.PageCount == 1 && (await one.ExtractAsync(0)).GetProperty("Text").GetString() == exportTexts[i], $"Split output {i + 1} reopens with its corresponding page content");
                await one.CloseAsync();
            }
            await Rejected<InvalidOperationException>(() => splitSession.SplitPagesAsync(splitFolder), "Split refuses an existing output folder");
            Check(Directory.GetFiles(splitFolder).Length == 2 && Directory.GetFileSystemEntries(splitParent).Length == 1,
                "Existing-folder refusal preserves prior output and leaves no partial or staging folder");
            await splitSession.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(exportSource) == exportHash, "Split preserves source PDF bytes");

        File.WriteAllText(Path.Combine(work, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} page organization checks. Evidence: {work}");
    }

    static void CreateNumericDestinationPdf(string path)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Annots [6 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 700 792] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 800 792] /Resources << >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [20 20 80 60] /Border [0 0 0] /Dest [2 /Fit] >>",
        };
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = System.Text.Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
    }
}

