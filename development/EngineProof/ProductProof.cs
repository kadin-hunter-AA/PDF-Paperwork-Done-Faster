using System.Security.Cryptography;
static class ProductProof
{
    public static void Run(string output)
    {
        foreach(var name in new[]{"mixed-styles-languages","markup-gallery"})
        {
            int count;
            using(var doc=Pdf.Open(Path.Combine(output,name+".pdf")))
            {
                count=doc.PageCount;
                for(int i=0;i<count;i++){using var page=doc.Page(i);Pdf.Check(page.Text().Length>0,"product corpus text");page.Render(Path.Combine(output,$"{name}-{i}-before.ppm"));}
                doc.Save(Path.Combine(output,name+"-saved.pdf"));
            }
            using(var doc=Pdf.Open(Path.Combine(output,name+"-saved.pdf")))
            {
                Pdf.Check(doc.PageCount==count,"product corpus saved page count");
                for(int i=0;i<count;i++){using var page=doc.Page(i);var after=Path.Combine(output,$"{name}-{i}-after.ppm");page.Render(after);Pdf.Check(Hash(after)==Hash(Path.Combine(output,$"{name}-{i}-before.ppm")),"product fixture render preserved");}
            }
        }
        Console.WriteLine("PASS: product corpus native save/reopen renders preserved.");
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
