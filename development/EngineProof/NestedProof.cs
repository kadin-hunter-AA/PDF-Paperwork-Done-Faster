using System.Text.Json;

static class NestedProof
{
    public static void Run(string output)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "nested-cases.json")));
        var passed = new List<string>();
        foreach (var item in manifest.RootElement.EnumerateArray())
        {
            string Value(string name) => item.GetProperty(name).GetString()!;
            var destination = Path.Combine(output, Value("destination"));
            bool reject = item.GetProperty("reject").GetBoolean(), rejected = false;
            try
            {
                Pdf.ReplaceNestedText(Path.Combine(output, Value("source")), destination,
                    new(item.GetProperty("pageIndex").GetInt32(),
                        item.GetProperty("formPath").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                        item.GetProperty("textIndex").GetInt32(), Value("expectedText"), Value("sourceSha256")),
                    Value("replacement"));
            }
            catch (InvalidOperationException error) when (reject)
            {
                Pdf.Check(error.Message.Contains(Value("expectedError")), "expected rejection reason " + Value("name"));
                rejected = true;
            }
            Pdf.Check(reject == rejected, "nested case " + Value("name"));
            Pdf.Check(File.Exists(destination) != reject, "nested publish/rejection " + Value("name"));
            if (!reject)
            {
                using var doc = Pdf.Open(destination);
                using (var page = doc.Page(0))
                    page.Render(Path.ChangeExtension(destination, "ppm"));
                doc.Save(Path.Combine(output, Value("name") + "-resaved.pdf"));
                using var reopened = Pdf.Open(Path.Combine(output, Value("name") + "-resaved.pdf"));
                using var savedPage = reopened.Page(item.GetProperty("pageIndex").GetInt32());
                Pdf.Check(savedPage.Text().Contains(Value("replacement")), "nested native resave persistence");
            }
            passed.Add(Value("name"));
            Console.WriteLine("PASS: nested " + Value("name"));
        }
        var first = manifest.RootElement[0];
        var source = Path.Combine(output, first.GetProperty("source").GetString()!);
        var existing = Path.Combine(output, first.GetProperty("destination").GetString()!);
        foreach (var protectedPath in new[] { source, existing })
        {
            var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(protectedPath));
            bool rejected = false;
            try { Pdf.ReplaceNestedText(source, protectedPath, new(0, [0, 0], 1, "Before",
                first.GetProperty("sourceSha256").GetString()!), "After edit"); }
            catch (InvalidOperationException error) when (error.Message.Contains("requires a new destination")) { rejected = true; }
            Pdf.Check(rejected && before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(protectedPath))),
                "existing destination protected");
        }
        Pdf.Check(!Directory.EnumerateDirectories(output, ".nested-*").Any(), "temporary files cleaned after success and rejection");
        passed.Add("source and existing destination overwrite rejected");
        passed.Add("temporary files cleaned");
        File.WriteAllText(Path.Combine(output, "native-nested.json"), JsonSerializer.Serialize(new
        {
            status = "PASS", engine = "PDFium chromium/8086 + pypdf 6.1.3 structural adapter", passed
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
