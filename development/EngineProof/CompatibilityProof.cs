using System.Security.Cryptography;
using System.Text.Json;

static class CompatibilityProof
{
    public static void Run(string output)
    {
        var passes=new List<string>();
        foreach(var name in new[]{"interactive-features","graphics-boxes"})
        {
            var original=Path.Combine(output,name+".pdf");
            using(var doc=Pdf.Open(original))
            {
                Pdf.Check(doc.PageCount==1,"compatibility fixture page count");
                using(var page=doc.Page(0)){Pdf.Check(page.Text().Length>0,"fixture text");page.Render(Path.Combine(output,name+"-before.ppm"));}
                doc.Save(Path.Combine(output,name+"-saved.pdf"));
            }
            using(var doc=Pdf.Open(Path.Combine(output,name+"-saved.pdf")))
            using(var page=doc.Page(0)){page.Render(Path.Combine(output,name+"-after.ppm"));}
            Pdf.Check(Hash(Path.Combine(output,name+"-before.ppm"))==Hash(Path.Combine(output,name+"-after.ppm")),"unchanged render after native save");
            passes.Add(name+": open/save/reopen/render pixels unchanged");
        }
        var encrypted=Path.Combine(output,"password-aes256.pdf");
        foreach(var password in new string?[]{null,"incorrect-password"})
        {
            var doc=Pdf.Native.FPDF_LoadDocument(encrypted,password);
            if(doc!=0){Pdf.Native.FPDF_CloseDocument(doc);throw new InvalidOperationException("Protected fixture opened without correct password");}
        }
        using(var doc=Pdf.Open(encrypted,"corpus-user"))
        {
            using(var page=doc.Page(0)){Pdf.Check(page.Text().Contains("Password protected fixture"),"correct password unlocks text");page.Render(Path.Combine(output,"password-preview.ppm"));}
            doc.Save(Path.Combine(output,"password-aes256-saved.pdf"));
        }
        using(var doc=Pdf.Open(Path.Combine(output,"password-aes256-saved.pdf"),"corpus-user"))
        using(var page=doc.Page(0)){Pdf.Check(page.Text().Contains("Password protected fixture"),"encrypted save/reopen text");}
        passes.Add("AES256 missing/wrong password rejected; correct password and save/reopen succeed");
        File.WriteAllText(Path.Combine(output,"native-compatibility.json"),JsonSerializer.Serialize(new{passes,status="PROTOTYPE_COMPATIBILITY_CHECKS_PASSED"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: protected documents and interactive/graphics native save-reopen checks.");
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
