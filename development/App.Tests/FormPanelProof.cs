using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class FormPanelProof
{
    const string BuildScript = """
import sys, re, pymupdf
dst = sys.argv[1]
d = pymupdf.open(); d.new_page(); d.new_page()
p1, p2 = d[0], d[1]
p2.set_rotation(90)
def add(page, kind, name, rect, **kw):
    w = pymupdf.Widget(); w.field_type = kind; w.field_name = name; w.rect = pymupdf.Rect(*rect)
    for k, v in kw.items(): setattr(w, k, v)
    return page.add_widget(w)
T = pymupdf
add(p1, T.PDF_WIDGET_TYPE_TEXT, "txt", (50, 50, 250, 72), text_fontsize=12)
add(p1, T.PDF_WIDGET_TYPE_TEXT, "multi", (50, 90, 250, 170), field_flags=T.PDF_TX_FIELD_IS_MULTILINE, text_fontsize=11)
add(p1, T.PDF_WIDGET_TYPE_TEXT, "short", (50, 190, 150, 212), text_maxlen=5, text_fontsize=12)
add(p1, T.PDF_WIDGET_TYPE_CHECKBOX, "chk", (300, 50, 320, 70))
kids = [add(p1, T.PDF_WIDGET_TYPE_RADIOBUTTON, "rad" + str(i), (300 + i * 40, 100, 320 + i * 40, 120), field_value=False) for i in range(3)]
add(p1, T.PDF_WIDGET_TYPE_COMBOBOX, "combo", (50, 230, 250, 252), choice_values=["Alpha", "Beta", "Gamma"], text_fontsize=12)
add(p1, T.PDF_WIDGET_TYPE_COMBOBOX, "ecombo", (50, 270, 250, 292), choice_values=["One", "Two"], field_flags=T.PDF_CH_FIELD_IS_COMBO | T.PDF_CH_FIELD_IS_EDIT, text_fontsize=12)
add(p1, T.PDF_WIDGET_TYPE_LISTBOX, "list1", (50, 310, 200, 370), choice_values=["a", "b", "c", "d"])
add(p1, T.PDF_WIDGET_TYPE_LISTBOX, "listm", (250, 310, 400, 370), choice_values=["p", "q", "r", "s"], field_flags=T.PDF_CH_FIELD_IS_MULTI_SELECT)
add(p2, T.PDF_WIDGET_TYPE_TEXT, "ro", (50, 50, 250, 72), field_value="locked", field_flags=T.PDF_FIELD_IS_READ_ONLY, text_fontsize=12)
calc = add(p2, T.PDF_WIDGET_TYPE_TEXT, "total", (50, 100, 250, 122), text_fontsize=12)
add(p2, T.PDF_WIDGET_TYPE_TEXT, "page2", (50, 150, 250, 172), text_fontsize=12)
d.xref_set_key(calc.xref, "AA", "<< /C << /S /JavaScript /JS (event.value = 1;) >> >>")
# A real radio group: one parent field, three kid widgets with distinct on-states.
parent = d.get_new_xref()
d.update_object(parent, "<< /FT /Btn /Ff 32768 /T (rad) /V /Off /Kids [" + " ".join("%d 0 R" % k.xref for k in kids) + "] >>")
for k, nm in zip(kids, ("red", "green", "blue")):
    yes = re.search(r"/Yes (\d+) 0 R", d.xref_object(k.xref)).group(1)
    off = re.search(r"/Off (\d+) 0 R", d.xref_object(k.xref)).group(1)
    for key in ("T", "FT", "Ff", "V"):
        o = pymupdf.mupdf.pdf_load_object(pymupdf._as_pdf_document(d), k.xref)
        pymupdf.mupdf.pdf_dict_dels(o, key)
    d.xref_set_key(k.xref, "Parent", "%d 0 R" % parent)
    d.xref_set_key(k.xref, "AP", "<< /N << /Off %s 0 R /%s %s 0 R >> >>" % (off, nm, yes))
    d.xref_set_key(k.xref, "AS", "/Off")
cat = d.pdf_catalog()
fields = d.xref_get_key(cat, "AcroForm/Fields")[1]
for k in kids: fields = fields.replace("%d 0 R" % k.xref, "")
fields = fields.rstrip("]") + " %d 0 R]" % parent
d.xref_set_key(cat, "AcroForm/Fields", fields)
d.save(dst)
""";

    const string XfaScript = """
import sys
from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, DictionaryObject, NameObject, TextStringObject, DecodedStreamObject
src, dst, empty = sys.argv[1], sys.argv[2], sys.argv[3] == "empty"
writer = PdfWriter()
if empty:
    writer.add_blank_page(300, 300)
    writer._root_object[NameObject("/AcroForm")] = DictionaryObject({NameObject("/Fields"): ArrayObject()})
else:
    writer.clone_document_from_reader(PdfReader(src))
stream = DecodedStreamObject(); stream.set_data(b"<xdp:xdp xmlns:xdp='http://ns.adobe.com/xdp/'/>")
writer._root_object["/AcroForm"][NameObject("/XFA")] = ArrayObject([TextStringObject("datasets"), writer._add_object(stream)])
with open(dst, "wb") as f: writer.write(f)
""";

    const string ReadScript = """
import sys, json
from pypdf import PdfReader
out = {}
for name, field in (PdfReader(sys.argv[1]).get_fields() or {}).items():
    v = field.get("/V")
    if isinstance(v, list): v = [str(x) for x in v]
    elif v is None: v = ""
    else: v = str(v)
    out[name] = v
print(json.dumps(out))
""";

    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "form-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var location = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
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
            throw new Exception("Not refused: " + message);
        }
        string Py(string script, params string[] args)
        {
            var file = Path.Combine(work, "s" + Guid.NewGuid().ToString("N")[..8] + ".py");
            File.WriteAllText(file, script);
            return RunPython(python, [file, .. args]);
        }

        var input = Path.Combine(work, "panel-form.pdf");
        Py(BuildScript, input);
        var inputHash = RecoveryStore.Fingerprint(input);
        var expected = new (string Name, string Type, double L, double B, double R, double T, int Page)[]
        {
            ("txt", "Text", 50, 770, 250, 792, 0), ("multi", "Text", 50, 672, 250, 752, 0), ("short", "Text", 50, 630, 150, 652, 0),
            ("chk", "Checkbox", 300, 772, 320, 792, 0), ("rad", "Radio", 300, 722, 320, 742, 0),
            ("combo", "Combo", 50, 590, 250, 612, 0), ("ecombo", "Combo", 50, 550, 250, 572, 0),
            ("list1", "List", 50, 472, 200, 532, 0), ("listm", "List", 250, 472, 400, 532, 0),
            ("ro", "Text", 50, 770, 250, 792, 1), ("total", "Text", 50, 720, 250, 742, 1), ("page2", "Text", 50, 670, 250, 692, 1),
        };

        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, input);
        var startRevision = session.Revision;

        var list = await session.ListAllFormFieldsAsync(location);
        Check(list.FieldCount == expected.Length && list.Fields.Count == expected.Length && !list.Truncated && !list.HasXfa && !list.XfaOnly,
            "ListFields reports every terminal field of the two-page form without truncation");
        Check(list.Fields.Select(f => f.FullName).SequenceEqual(expected.Select(e => e.Name)), "Fields are listed in page order");
        foreach (var e in expected)
        {
            var f = list.Fields.Single(x => x.FullName == e.Name);
            var w = f.Widgets[0];
            Check(f.Type == e.Type && w.Page == e.Page && Math.Abs(w.Left - e.L) < .01 && Math.Abs(w.Bottom - e.B) < .01 &&
                  Math.Abs(w.Right - e.R) < .01 && Math.Abs(w.Top - e.T) < .01, $"ListFields type and unrotated widget rectangle match for {e.Name}");
        }
        FormFieldInfo Field(FormFieldList l, string n) => l.Fields.Single(f => f.FullName == n);
        Check(Field(list, "rad").Options.Select(o => o.Export).SequenceEqual(["red", "green", "blue"]) && Field(list, "rad").Value == "" && Field(list, "rad").Widgets.Count == 3,
            "Radio group lists its three export values, three widgets and no selection");
        Check(Field(list, "combo").Options.Select(o => o.Export).SequenceEqual(["Alpha", "Beta", "Gamma"]) && !Field(list, "combo").Editable &&
              Field(list, "ecombo").Editable && Field(list, "listm").MultiSelect && !Field(list, "list1").MultiSelect,
            "Combo options, editable flag and multi-select flag are reported");
        Check(Field(list, "multi").Multiline && Field(list, "short").MaxLength == 5 && Field(list, "ro").ReadOnly && Field(list, "ro").Value == "locked" &&
              Field(list, "chk").Value == "false" && Field(list, "chk").Options.Single().Export == "Yes",
            "Multiline, MaxLength, ReadOnly, current value and checkbox on-state are reported");
        Check(Field(list, "total").Calculated && !Field(list, "txt").Calculated && Field(list, "total").HasJavaScript,
            "The /AA /C field is flagged as calculated with JavaScript");

        var pageInfo = (await session.PageInfoAsync()).GetProperty("Pages").EnumerateArray().ToArray();
        var dims = pageInfo.Select(p => (W: (int)Math.Round(p.GetProperty("Width").GetDouble() * 2), H: (int)Math.Round(p.GetProperty("Height").GetDouble() * 2))).ToArray();
        Check(dims[0].H > dims[0].W && dims[1].W > dims[1].H, "PDFium reports page 2 as rotated 90 degrees (landscape display)");
        async Task<byte[][]> RenderBoth(DocumentSession s) => [
            Convert.FromBase64String((await s.RenderAsync(0, dims[0].W, dims[0].H)).GetProperty("Bgra").GetString()!),
            Convert.FromBase64String((await s.RenderAsync(1, dims[1].W, dims[1].H)).GetProperty("Bgra").GetString()!)];
        // Maps an unrotated PDF rectangle to display pixels (2x); page 2 is rotated 90 degrees clockwise.
        (int X0, int Y0, int X1, int Y1) Pixels(FormWidget w)
        {
            const double pageH = 842;
            return w.Page == 1
                ? ((int)(w.Bottom * 2) - 1, (int)(w.Left * 2) - 1, (int)(w.Top * 2) + 1, (int)(w.Right * 2) + 1)
                : ((int)(w.Left * 2) - 1, (int)((pageH - w.Top) * 2) - 1, (int)(w.Right * 2) + 1, (int)((pageH - w.Bottom) * 2) + 1);
        }
        bool Differs(byte[][] a, byte[][] b, FormFieldInfo f)
        {
            var w = f.FullName == "rad" ? f.Widgets[1] : f.Widgets[0]; // the selected radio choice is the second widget (green)
            var (x0, y0, x1, y1) = Pixels(w);
            var width = dims[w.Page].W; var height = dims[w.Page].H;
            for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
                for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
                {
                    var i = (y * width + x) * 4;
                    if (a[w.Page][i] != b[w.Page][i] || a[w.Page][i + 1] != b[w.Page][i + 1] || a[w.Page][i + 2] != b[w.Page][i + 2]) return true;
                }
            return false;
        }
        var blank = await RenderBoth(session);

        var values = new Dictionary<string, string[]>
        {
            ["txt"] = ["Hello café"], ["multi"] = [string.Join(' ', Enumerable.Repeat("word", 30)) + "\nlast line"], ["short"] = ["abcde"],
            ["chk"] = ["true"], ["rad"] = ["green"], ["combo"] = ["Beta"], ["ecombo"] = ["Custom entry"], ["list1"] = ["b"],
            ["listm"] = ["p", "r"], ["total"] = ["42"], ["page2"] = ["Second page"],
        };
        var result = await session.FillFormFieldsAsync(location, values, startRevision);
        Check(result.Applied == values.Count && result.Changed == values.Count && session.Revision == startRevision + 1 && session.IsDirty,
            "FillFields applies all eleven values as one durable revision and reports the changed count");
        Check(result.Warnings.Any(w => w.Contains("calculations", StringComparison.Ordinal)), "Warnings tell the user about calculations that were not run");
        Check(result.Values["listm"].SequenceEqual(["p", "r"]) && result.Values["rad"].Single() == "green" && result.Values["chk"].Single() == "true" &&
              result.Values["ecombo"].Single() == "Custom entry", "The result carries values read back from the written file");
        var afterList = await session.ListAllFormFieldsAsync(location);
        Check(Field(afterList, "txt").Value == "Hello café" && Field(afterList, "chk").Value == "true" && Field(afterList, "rad").Value == "green" &&
              Field(afterList, "combo").Value == "Beta" && Field(afterList, "ecombo").Value == "Custom entry" && Field(afterList, "list1").Selected.SequenceEqual(["b"]) &&
              Field(afterList, "listm").Selected.SequenceEqual(["p", "r"]) && Field(afterList, "short").Value == "abcde" &&
              Field(afterList, "page2").Value == "Second page" && Field(afterList, "multi").Value.EndsWith("last line") && Field(afterList, "ro").Value == "locked",
            "ListFields on the committed checkpoint shows every filled value and the untouched read-only value");
        Check(afterList.NeedAppearances, "NeedAppearances is set so other viewers refresh the fields");
        Check(RecoveryStore.Fingerprint(input) == inputHash, "Filling leaves the selected source PDF byte-for-byte unchanged");

        var saved = Path.Combine(work, "panel-saved.pdf");
        var filled = await RenderBoth(session);
        await session.SaveAsync(saved);
        var independent = JsonDocument.Parse(Py(ReadScript, saved)).RootElement;
        Check(independent.GetProperty("txt").GetString() == "Hello café" && independent.GetProperty("chk").GetString() == "/Yes" &&
              independent.GetProperty("rad").GetString() == "/green" && independent.GetProperty("combo").GetString() == "Beta" &&
              independent.GetProperty("ecombo").GetString() == "Custom entry" && independent.GetProperty("list1").GetString() == "b" &&
              independent.GetProperty("listm").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["p", "r"]) &&
              independent.GetProperty("short").GetString() == "abcde" && independent.GetProperty("ro").GetString() == "locked",
            "Independent pypdf get_fields reads the same values from the saved PDF");
        foreach (var name in values.Keys)
            Check(Differs(blank, filled, Field(afterList, name)), $"PDFium session render changes inside the widget rectangle of {name}");

        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopen-recovery")), saved))
        {
            var reopenedList = await reopened.ListAllFormFieldsAsync(location);
            Check(Field(reopenedList, "rad").Value == "green" && Field(reopenedList, "listm").Selected.SequenceEqual(["p", "r"]) && Field(reopenedList, "txt").Value == "Hello café",
                "A fresh session on the saved PDF lists the filled values");
            var reopenedPixels = await RenderBoth(reopened);
            foreach (var name in values.Keys)
                Check(Differs(blank, reopenedPixels, Field(reopenedList, name)), $"Saved and reopened PDF renders filled pixels for {name}");
        }

        await session.UndoAsync();
        var undone = await session.ListAllFormFieldsAsync(location);
        Check(Field(undone, "txt").Value == "" && Field(undone, "chk").Value == "false" && Field(undone, "rad").Value == "" &&
              Field(undone, "list1").Selected.Count == 0 && Field(undone, "listm").Selected.Count == 0 && Field(undone, "combo").Value == "" && session.CanRedo,
            "Undo restores the original empty values");
        var undoneRender = await RenderBoth(session);
        Check(undoneRender.Zip(blank).All(p => p.First.AsSpan().SequenceEqual(p.Second)), "Undo restores the original unfilled render");
        await session.RedoAsync();
        Check(Field(await session.ListAllFormFieldsAsync(location), "rad").Value == "green" && session.CanUndo, "Redo reapplies the filled values");

        var second = await session.FillFormFieldsAsync(location, new Dictionary<string, string[]>
            { ["rad"] = [""], ["chk"] = ["false"], ["txt"] = [""], ["list1"] = [], ["combo"] = ["Gamma"] }, session.Revision);
        var cleared = await session.ListAllFormFieldsAsync(location);
        Check(second.Changed == 5 && Field(cleared, "rad").Value == "" && Field(cleared, "chk").Value == "false" && Field(cleared, "txt").Value == "" &&
              Field(cleared, "list1").Selected.Count == 0 && Field(cleared, "combo").Value == "Gamma" && Field(cleared, "listm").Selected.Count == 2,
            "Clearing a radio group, checkbox, text and list box works and leaves other fields untouched");

        var revision = session.Revision;
        async Task Refuse(string message, Dictionary<string, string[]> v, bool argument = false)
        {
            if (argument) await Rejected<ArgumentException>(() => session.FillFormFieldsAsync(location, v, session.Revision), message);
            else await Rejected<InvalidOperationException>(() => session.FillFormFieldsAsync(location, v, session.Revision), message);
            Check(session.Revision == revision, message + " (revision unchanged)");
        }
        await Refuse("Read-only field refused", new() { ["ro"] = ["x"] });
        await Refuse("Unknown field refused", new() { ["nope"] = ["x"] });
        await Refuse("Radio value outside its options refused", new() { ["rad"] = ["purple"] });
        await Refuse("Combo value outside its options refused", new() { ["combo"] = ["Delta"] });
        await Refuse("Value longer than MaxLength refused", new() { ["short"] = ["abcdef"] });
        await Refuse("Several values for a single-select list refused", new() { ["list1"] = ["a", "b"] });
        await Refuse("Several values for a text field refused", new() { ["txt"] = ["a", "b"] });
        await Refuse("List value outside its options refused", new() { ["listm"] = ["p", "zzz"] });
        await Refuse("Checkbox value other than true or false refused", new() { ["chk"] = ["maybe"] });
        await Refuse("Line break in single-line text refused", new() { ["txt"] = ["a\nb"] });
        await Refuse("One bad value stops the whole batch", new() { ["txt"] = ["fine"], ["ro"] = ["x"] });
        Check(Field(await session.ListAllFormFieldsAsync(location), "txt").Value == "", "A refused batch applies none of its values");
        await Refuse("Empty request refused", new(), true);
        await Refuse("Oversized value refused before the engine runs", new() { ["txt"] = [new string('x', 4097)] }, true);
        await Refuse("More than 500 fields refused before the engine runs",
            Enumerable.Range(0, 501).ToDictionary(i => "f" + i, _ => new[] { "x" }), true);
        await Rejected<InvalidOperationException>(() => session.FillFormFieldsAsync(location, new Dictionary<string, string[]> { ["txt"] = ["x"] }, revision - 1),
            "Stale revision refused");

        var xfaPath = Path.Combine(work, "xfa-form.pdf");
        Py(XfaScript, input, xfaPath, "full");
        var xfaHash = RecoveryStore.Fingerprint(xfaPath);
        using (var xfa = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "xfa-recovery")), xfaPath))
        {
            var xfaList = await xfa.ListAllFormFieldsAsync(location);
            Check(xfaList.HasXfa && !xfaList.XfaOnly && xfaList.FieldCount == expected.Length, "ListFields reports HasXfa for an XFA-bearing form");
            var xfaRevision = xfa.Revision;
            await Rejected<InvalidOperationException>(() => xfa.FillFormFieldsAsync(location, new Dictionary<string, string[]> { ["txt"] = ["x"] }, xfaRevision),
                "XFA form fill refused");
            Check(xfa.Revision == xfaRevision && RecoveryStore.Fingerprint(xfaPath) == xfaHash, "XFA refusal leaves the session and source unchanged");
        }
        var xfaOnlyPath = Path.Combine(work, "xfa-only.pdf");
        Py(XfaScript, input, xfaOnlyPath, "empty");
        using (var xfaOnly = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "xfa-only-recovery")), xfaOnlyPath))
        {
            var l = await xfaOnly.ListAllFormFieldsAsync(location);
            Check(l.HasXfa && l.XfaOnly && l.FieldCount == 0, "An XFA document with no AcroForm fields is reported as XfaOnly");
            await Rejected<InvalidOperationException>(() => xfaOnly.FillFormFieldsAsync(location, new Dictionary<string, string[]> { ["txt"] = ["x"] }, xfaOnly.Revision),
                "XFA-only document fill refused");
        }

        Check(RecoveryStore.Fingerprint(input) == inputHash, "The original fixture hash is unchanged at the end of the proof");
        session.Dispose();
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} form-panel checks. Evidence: {work}");
    }

    static string RunPython(string python, string[] args)
    {
        var start = new ProcessStartInfo(python)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new Exception("Could not start the isolated Python helper.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Python helper failed ({process.ExitCode}): {stderr}");
        return stdout.Result;
    }
}
