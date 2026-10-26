using System.Security.Cryptography;
using System.Text.Json;

static class EdgeProof
{
    public static void Run(string output)
    {
        var results=new List<object>();
        foreach(var name in new[]{"ligature-mapped","missing-unicode-map","scan-real-text","public-hello-world","signed"})
        {
            int count;bool hasText;bool? ligature=null;
            using(var doc=Pdf.Open(Path.Combine(output,name+".pdf")))
            {
                count=doc.PageCount;Pdf.Check(count==1,"edge fixture pages");
                using(var page=doc.Page(0))
                {
                    var text=page.Text();hasText=!string.IsNullOrWhiteSpace(text);
                    if(name=="scan-real-text")Pdf.Check(!hasText,"scanned text not falsely searchable");
                    if(name=="ligature-mapped"){ligature=text.Contains("ﬁ")||text.Contains("fi");Pdf.Check(ligature==true&&text.Contains("Café"),"ligature mapped extraction");}
                    if(name=="public-hello-world")Pdf.Check(text.Contains("Hello, world!"),"public fixture text");
                    page.Render(Path.Combine(output,name+"-before.ppm"));
                }
                doc.Save(Path.Combine(output,name+"-saved.pdf"));
            }
            using(var doc=Pdf.Open(Path.Combine(output,name+"-saved.pdf")))using(var page=doc.Page(0)){page.Render(Path.Combine(output,name+"-after.ppm"));}
            Pdf.Check(Hash(Path.Combine(output,name+"-before.ppm"))==Hash(Path.Combine(output,name+"-after.ppm")),"edge native render preservation");
            results.Add(new{name,count,hasExtractableText=hasText,ligatureRecognized=ligature,renderPreserved=true,note=name=="signed"?"Rewrite is test-only; signature invalidation must be inspected independently":name=="missing-unicode-map"?"No editing capability claimed without usable Unicode maps":"Synthetic fixture capability only"});
        }
        File.WriteAllText(Path.Combine(output,"native-edge-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: native edge fixtures open/save/reopen and retain rendered appearance.");
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
