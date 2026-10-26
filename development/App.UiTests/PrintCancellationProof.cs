using System.IO;
using System.Text.Json;
using HunterPdf.Desktop;

namespace HunterPdf.UiTests;

// Exercises streaming, cancellation and failure boundaries with fake sinks/sources. Nothing is sent to a printer.
internal static class PrintCancellationProof
{
    internal static async Task RunAsync(string root)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks.Add(name);
        }
        var output = Path.Combine(root, "artifacts", "print-cancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var sizes = Enumerable.Repeat(new PrintPageSize(612, 792), 30).ToArray();
        var all = Enumerable.Range(0, 30).ToArray();
        var options = new PrintOptions();

        // 1. A 30-page job streams every page; only one payload is alive at a time; memory stays bounded.
        {
            var source = new FakeSource(output, 4 * 1024 * 1024);
            var sink = new FakeSink();
            GC.Collect(); var baseline = GC.GetTotalMemory(true);
            long peak = 0; // Live memory after collection: uncollected garbage depends on the machine's GC timing.
            var summary = await PrintPipeline.RunJobAsync(sink, source, all, sizes, options, "test", _ => peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline), CancellationToken.None);
            Check(sink.Drawn.Count == 30 && sink.Sheets == 30 && summary.Pages == 30 && sink.Completed == true, "A 30-page job submits every page to the sink and completes");
            Check(source.MaxLive == 1 && source.Live == 0, "Pages are rendered, submitted and released one at a time");
            Check(Directory.GetFiles(output, "*.tmp").Length == 0, "Every per-page payload file is deleted after submission");
            Check(peak < 64L * 1024 * 1024, $"Managed memory stays bounded across 30 pages (peak growth {peak / 1024} KB)");
            Check(sink.Drawn.Select(d => d.PageIndex).SequenceEqual(all), "Pages are submitted in order");
        }
        // 2. Progress reports "Printing page n of m".
        {
            var messages = new List<string>();
            await PrintPipeline.RunJobAsync(new FakeSink(), new FakeSource(output, 16), all.Take(3).ToArray(), sizes, options, "t", p => messages.Add(p.ToString()), CancellationToken.None);
            Check(messages.SequenceEqual(new[] { "Printing page 1 of 3…", "Printing page 2 of 3…", "Printing page 3 of 3…" }), "Progress reads Printing page n of m");
        }
        // 3. Cancel midway stops further pages and aborts the job.
        using (var cancellation = new CancellationTokenSource())
        {
            var source = new FakeSource(output, 16);
            var sink = new FakeSink { OnDraw = n => { if (n == 12) cancellation.Cancel(); } };
            try { await PrintPipeline.RunJobAsync(sink, source, all, sizes, options, "t", null, cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(sink.Drawn.Count == 12 && source.Rendered == 12, "Cancellation midway stops further pages from rendering or printing");
            Check(sink.Completed == false && sink.Ended, "Cancellation aborts the print job instead of completing it");
            Check(source.Live == 0, "Cancellation releases the in-flight page");
        }
        // 4. Cancel while a page is rendering never draws it.
        using (var cancellation = new CancellationTokenSource())
        {
            var source = new FakeSource(output, 16) { OnRender = n => { if (n == 3) cancellation.Cancel(); } };
            var sink = new FakeSink();
            try { await PrintPipeline.RunJobAsync(sink, source, all, sizes, options, "t", null, cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(sink.Drawn.Count == 2 && sink.Completed == false, "Cancellation during rendering never submits that page");
        }
        // 5. Cancel before start never opens the job.
        {
            var sink = new FakeSink();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await PrintPipeline.RunJobAsync(sink, new FakeSource(output, 16), all, sizes, options, "t", null, cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(!sink.Began && !sink.Ended, "Cancellation before submission never reaches the print sink");
        }
        // 6. A sink failure aborts the job and surfaces the error.
        {
            var sink = new FakeSink { OnDraw = n => { if (n == 5) throw new InvalidOperationException("The printer stopped the job."); } };
            try { await PrintPipeline.RunJobAsync(sink, new FakeSource(output, 16), all, sizes, options, "t", null, CancellationToken.None); throw new Exception("Failure swallowed"); }
            catch (InvalidOperationException) { }
            Check(sink.Completed == false && sink.Ended && sink.Drawn.Count == 5, "A printer failure aborts the job and stops further pages");
        }
        // 7. Software copies repeat the whole job; driver copies do not.
        {
            var sink = new FakeSink { SoftwareCopies = 2 };
            await PrintPipeline.RunJobAsync(sink, new FakeSource(output, 16), all.Take(3).ToArray(), sizes, options, "t", null, CancellationToken.None);
            Check(sink.Drawn.Count == 6 && sink.Sheets == 6, "Copies the printer cannot replicate print the job once per copy");
        }
        // 8. Fallback and reduced-resolution notes reach the summary; N-up groups pages per sheet.
        {
            var sink = new FakeSink();
            var summary = await PrintPipeline.RunJobAsync(sink, new FakeSource(output, 16) { Note = "Page printed as an image." }, all.Take(5).ToArray(), sizes,
                options with { PagesPerSheet = 2, AsImage = true }, "t", null, CancellationToken.None);
            Check(summary.Sheets == 3 && summary.ImagePages == 5 && summary.Notes.Count == 5 && sink.Drawn.Count == 5, "N-up groups pages per sheet and notes are reported");
        }
        // 9. Invalid options and ranges are refused.
        {
            foreach (var bad in new[] { options with { PagesPerSheet = 3 }, options with { Scaling = PrintScaling.Custom, CustomPercent = 0 }, options with { Copies = 0 } })
            {
                try { await PrintPipeline.RunJobAsync(new FakeSink(), new FakeSource(output, 16), all, sizes, bad, "t", null, CancellationToken.None); throw new Exception("Invalid options accepted"); }
                catch (InvalidOperationException) { }
            }
            try { await PrintPipeline.RunJobAsync(new FakeSink(), new FakeSource(output, 16), new[] { 99 }, sizes, options, "t", null, CancellationToken.None); throw new Exception("Bad page accepted"); }
            catch (InvalidOperationException) { }
            Check(true, "Invalid options and pages are refused before printing");
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            Passed = checks,
            Scope = "Streaming job runner with fake sink and source. No physical printer, spooler or native print dialog."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} print cancellation checks. Evidence: {output}");
    }

    internal sealed class FakeSink : IPrintSink
    {
        internal List<PrintPlacement> Drawn { get; } = new();
        internal int Sheets;
        internal bool Began, Ended;
        internal bool? Completed;
        internal Action<int>? OnDraw;
        internal int SoftwareCopies = 1;
        public PrintDeviceInfo Device => new(612, 792, false, SoftwareCopies);
        public Task BeginJobAsync(string jobName, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Began = true; return Task.CompletedTask; }
        public Task BeginSheetAsync(SheetPlan plan, CancellationToken cancellation) { Sheets++; return Task.CompletedTask; }
        public Task DrawPageAsync(PrintPlacement placement, PrintPagePayload payload, CancellationToken cancellation)
        {
            Drawn.Add(placement);
            if (!File.Exists(payload.Path)) throw new InvalidOperationException("Payload missing");
            OnDraw?.Invoke(Drawn.Count);
            return Task.CompletedTask;
        }
        public Task EndSheetAsync(CancellationToken cancellation) => Task.CompletedTask;
        public Task EndJobAsync(bool completed) { Ended = true; Completed = completed; return Task.CompletedTask; }
    }

    internal sealed class FakeSource(string directory, int bytes) : IPrintPageSource
    {
        int live, counter;
        internal int Live => live;
        internal int MaxLive, Rendered;
        internal Action<int>? OnRender;
        internal string? Note;
        public Task<PrintPagePayload> RenderAsync(int pageIndex, PrintPageSize size, double scale, bool preferVector, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Rendered++;
            OnRender?.Invoke(Rendered);
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, $"fake{Interlocked.Increment(ref counter)}.tmp");
            File.WriteAllBytes(path, new byte[bytes]);
            MaxLive = Math.Max(MaxLive, Interlocked.Increment(ref live));
            return Task.FromResult<PrintPagePayload>(new TrackedPayload(this, preferVector, path, Note));
        }
        sealed class TrackedPayload : PrintPagePayload
        {
            readonly FakeSource owner;
            internal TrackedPayload(FakeSource owner, bool emf, string path, string? note) : base(emf, path, 1, 1, note) { this.owner = owner; }
            public override void Dispose() { base.Dispose(); Interlocked.Decrement(ref owner.live); }
        }
    }
}
