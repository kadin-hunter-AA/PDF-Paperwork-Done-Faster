using System.Text.Json;
using System.Diagnostics;
using HunterPdf.Core;

static class FormEditingProof
{
    public static async Task RunAsync(string root, string output)
    {
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var originalHash = RecoveryStore.Fingerprint(fixture);
        var directory = Path.Combine(output, "form-inspection");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "interactive-source.pdf");
        File.Copy(fixture, copy);
        var copyHash = RecoveryStore.Fingerprint(copy);
        var passwordPdf = Path.Combine(directory, "password-field.pdf");
        MakePasswordFixture(root, fixture, passwordPdf);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        using var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(directory, "recovery")), copy);
        var result = await session.GetFormFieldsAsync(0);
        var fields = result.GetProperty("Fields").EnumerateArray().ToArray();
        var sample = fields.Single(field => field.GetProperty("Name").GetString() == "sample_name");
        if (sample.GetProperty("Type").GetString() != "Text" || sample.GetProperty("Value").GetString() != "Corpus value")
            throw new Exception("Form inspection did not return the expected AcroForm text field metadata.");
        if (sample.GetProperty("Editable").GetBoolean() || sample.GetProperty("UnsupportedReason").GetString() is null)
            throw new Exception("Form inspection incorrectly advertised field writing support.");
        var signature = fields.Single(field => field.GetProperty("Name").GetString() == "unsigned_signature");
        if (signature.GetProperty("Type").GetString() != "Signature" || signature.GetProperty("Value").ValueKind != JsonValueKind.Null)
            throw new Exception("Signature field inspection exposed a value or returned the wrong field type.");
        if (session.Revision != 0 || RecoveryStore.Fingerprint(copy) != copyHash || RecoveryStore.Fingerprint(fixture) != originalHash)
            throw new Exception("Read-only form inspection changed the session revision or source bytes.");
        await session.CloseAsync();

        using (var passwordSession = await DocumentSession.OpenAsync(worker,
                   new RecoveryStore(Path.Combine(directory, "password-recovery")), passwordPdf))
        {
            var passwordResult = await passwordSession.GetFormFieldsAsync(0);
            var passwordFields = passwordResult.GetProperty("Fields").EnumerateArray();
            if (passwordFields.Count() != 2)
                throw new Exception("Non-widget annotations were incorrectly returned as form fields.");
            var passwordField = passwordFields.Single(field => field.GetProperty("Name").GetString() == "sample_name");
            if (!passwordField.GetProperty("Password").GetBoolean() || passwordField.GetProperty("Value").ValueKind != JsonValueKind.Null)
                throw new Exception("Password field inspection exposed its value or failed to identify the password flag.");
            await passwordSession.CloseAsync();
        }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new
        {
            Passed = new[]
            {
                "AcroForm field names, types, and plain text values are inspected through PDFium",
                "Form writing is explicitly marked unsupported",
                "Signature values are never exposed",
                "Password field values are never exposed",
                "Inspection leaves the session revision and source PDFs unchanged"
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS 5 form inspection checks. Evidence: {directory}");
    }

    static void MakePasswordFixture(string root, string source, string destination)
    {
        const string script = "import sys; from pypdf import PdfReader, PdfWriter; from pypdf.annotations import Text; " +
            "r=PdfReader(sys.argv[1]); w=PdfWriter(); w.clone_document_from_reader(r); " +
            "w.update_page_form_field_values(w.pages[0], {'sample_name':'SECRET PASSWORD VALUE'}, flags=1<<13, auto_regenerate=False); " +
            "w.add_annotation(page_number=0, annotation=Text(rect=(10,10,20,20), text='ordinary note')); " +
            "f=open(sys.argv[2],'wb'); w.write(f); f.close()";
        var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(script); start.ArgumentList.Add(source); start.ArgumentList.Add(destination);
        var packagePath = Path.Combine(root, "tools", "python-packages");
        var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? packagePath : packagePath + Path.PathSeparator + existing;
        using var process = Process.Start(start) ?? throw new Exception("Could not start Python to create the isolated password-field proof PDF.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Could not create the password-field proof PDF (exit {process.ExitCode}): {stderr}");
    }
}
