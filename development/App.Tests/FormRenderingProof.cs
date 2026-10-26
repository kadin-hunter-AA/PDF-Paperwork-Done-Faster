using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class FormRenderingProof
{
    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Form rendering proof failed: " + message);
            checks.Add(message);
        }

        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var fixtureHash = RecoveryStore.Fingerprint(fixture);
        var work = Path.Combine(output, "form-rendering");
        Directory.CreateDirectory(work);
        var sourceCopy = Path.Combine(work, "interactive-source.pdf");
        File.Copy(fixture, sourceCopy);
        var sourceCopyHash = RecoveryStore.Fingerprint(sourceCopy);
        var saved = Path.Combine(work, "interactive-saved.pdf");
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");

        var fixtureFields = ReadFixtureFields(root, fixture);
        Check(fixtureFields.SampleValue == "Corpus value", "Frozen compatibility fixture has the expected AcroForm text value");
        Check(fixtureFields.SignatureType == "/Sig" && !fixtureFields.SignatureValuePresent,
            "Frozen compatibility fixture has an unsigned signature field without a signature value");

        const int width = 612;
        const int height = 792;
        // The fixture's sample_name widget rectangle is [50, 640, 350, 670] on
        // a 612 by 792 point page; this probes its interior, away from the border.
        static int CountDarkWidgetPixels(JsonElement image)
        {
            const int imageWidth = 612;
            const int left = 55;
            const int right = 345;
            const int top = 126;
            const int bottom = 150;
            var stride = image.GetProperty("Stride").GetInt32();
            var bytes = Convert.FromBase64String(image.GetProperty("Bgra").GetString()!);
            var count = 0;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var pixel = y * stride + x * 4;
                if (bytes[pixel] < 100 && bytes[pixel + 1] < 100 && bytes[pixel + 2] < 100) count++;
            }
            if (stride < imageWidth * 4 || bytes.Length != stride * image.GetProperty("Height").GetInt32())
                throw new Exception("Form rendering proof failed: worker returned an invalid bitmap layout.");
            return count;
        }

        static void CheckWidgetText(CheckAction check, JsonElement image, string label)
        {
            var darkPixels = CountDarkWidgetPixels(image);
            check(darkPixels >= 20, $"{label} draws the form value in the expected widget area ({darkPixels} dark pixels)");
        }

        using (var session = await DocumentSession.OpenAsync(worker, store, sourceCopy))
        {
            Check(session.PageCount == 1, "Interactive compatibility fixture opens as one page");
            try
            {
                var viewer = await session.RenderAsync(0, width, height);
                CheckWidgetText(Check, viewer, "Viewer Render");
                var print = await session.RenderForPrintAsync(0, width, height);
                CheckWidgetText(Check, print, "Print RenderForPrint");
            }
            catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
            {
                throw new Exception("Form rendering proof could not exercise the fixture's AcroForm. " +
                    "The fixture must use the supported AcroForm type; unsupported form types must fail explicitly. " + error.Message, error);
            }

            await session.SaveAsync(saved);
            await session.CloseAsync();
        }

        Check(RecoveryStore.Fingerprint(fixture) == fixtureHash && RecoveryStore.Fingerprint(sourceCopy) == sourceCopyHash,
            "Rendering and ordinary Save As preserve frozen fixture and session source bytes");
        var savedFields = ReadFixtureFields(root, saved);
        Check(savedFields.SampleValue == fixtureFields.SampleValue &&
              savedFields.SignatureType == fixtureFields.SignatureType && !savedFields.SignatureValuePresent,
            "Ordinary saved PDF retains the widget value and unsigned signature field according to pypdf");

        using (var reopened = await DocumentSession.OpenAsync(worker, store, saved))
        {
            Check(reopened.PageCount == 1, "Ordinary saved PDF reopens in the document worker");
            CheckWidgetText(Check, await reopened.RenderAsync(0, width, height), "Reopened viewer Render");
            CheckWidgetText(Check, await reopened.RenderForPrintAsync(0, width, height), "Reopened print RenderForPrint");
            await reopened.CloseAsync();
        }

        Check(RecoveryStore.Fingerprint(fixture) == fixtureHash,
            "Form render, save, and reopen leave the frozen compatibility fixture unchanged");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} form rendering checks. Evidence: {work}");
    }

    delegate void CheckAction(bool condition, string message);

    sealed record FixtureFields(string SampleValue, string? SignatureType, bool SignatureValuePresent);

    static FixtureFields ReadFixtureFields(string root, string pdfPath)
    {
        const string script = "import json,sys; from pypdf import PdfReader; r=PdfReader(sys.argv[1]); " +
            "acro=r.trailer['/Root'].get('/AcroForm'); acro=acro.get_object() if acro else None; " +
            "fields=r.get_fields() or {}; value=fields.get('sample_name',{}).get('/V'); " +
            "sig=fields.get('unsigned_signature',{}); " +
            "print(json.dumps({'acroform': acro is not None, 'xfa': acro.get('/XFA') is not None if acro else False, " +
            "'value': value, 'signature_type': sig.get('/FT'), 'signature_value_present': '/V' in sig}))";
        var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(pdfPath);
        var packagePath = Path.Combine(root, "tools", "python-packages");
        var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
            ? packagePath
            : packagePath + Path.PathSeparator + existing;
        using var process = Process.Start(start) ?? throw new Exception("Form rendering proof could not start Python/pypdf.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception($"Form rendering proof pypdf inspection failed (exit {process.ExitCode}): {stderr}");
        using var result = JsonDocument.Parse(stdout);
        var data = result.RootElement;
        if (!data.GetProperty("acroform").GetBoolean() || data.GetProperty("xfa").GetBoolean())
            throw new InvalidOperationException("The compatibility fixture is not a supported AcroForm. XFA and non-AcroForm types are not accepted by this proof.");
        return new FixtureFields(
            data.GetProperty("value").GetString() ?? "",
            data.GetProperty("signature_type").GetString(),
            data.GetProperty("signature_value_present").GetBoolean());
    }
}
