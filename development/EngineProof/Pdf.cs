using System.Runtime.InteropServices;

public sealed class PdfPasswordRequiredException() : InvalidOperationException(
    "This PDF requires a password or the password is incorrect.");

sealed partial class Pdf : IDisposable
{
    public nint Handle { get; }
    private Pdf(nint handle) { Check(handle != 0, "document handle"); Handle = handle; }
    public static void Initialize() => Native.FPDF_InitLibrary();
    public static Pdf Create() => new(Native.FPDF_CreateNewDocument());
    public static Pdf Open(string path, string? password = null)
    {
        // PDFium's Windows file opener otherwise rejects valid recovery paths
        // beyond MAX_PATH, even when managed file operations accept them.
        var nativePath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && !nativePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            nativePath = nativePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + nativePath[2..] : @"\\?\" + nativePath;
        var handle = Native.FPDF_LoadDocument(nativePath, password);
        if (handle != 0) return new(handle);
        var error = Native.FPDF_GetLastError();
        if (error == 4) throw new PdfPasswordRequiredException();
        throw new InvalidOperationException(
            $"PDFium could not open this file (native error {error}). The file may be damaged, unsupported, or not a PDF.");
    }
    public int PageCount => Native.FPDF_GetPageCount(Handle);
    public PageHandle NewPage() => new(Native.FPDFPage_New(Handle, PageCount, 612, 792), Handle);
    public PageHandle Page(int index) => new(Native.FPDF_LoadPage(Handle, index), Handle);
    public void Dispose() => Native.FPDF_CloseDocument(Handle);
    public static void Check(bool ok, string operation) { if (!ok) throw new InvalidOperationException($"Failed: {operation}"); }
    public static void Check(int ok, string operation) => Check(ok != 0, operation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlock(nint self, nint data, uint size);
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileWrite { public int Version; public nint Callback; }
    public void Save(string path, uint flags = 2)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        Exception? failure = null;
        WriteBlock callback = (_, data, size) =>
        {
            try { var bytes = new byte[checked((int)size)]; Marshal.Copy(data, bytes, 0, bytes.Length); stream.Write(bytes); return 1; }
            catch (Exception e) { failure = e; return 0; }
        };
        var writer = new FileWrite { Version = 1, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var success = Native.FPDF_SaveAsCopy(Handle, ref writer, flags);
        GC.KeepAlive(callback);
        if (failure != null) throw new IOException("Save callback failed", failure);
        Check(success, "save copy");
        stream.Flush(true);
    }
    public sealed class PageHandle : IDisposable
    {
        public nint Handle { get; }
        private readonly nint document;
        public PageHandle(nint handle, nint doc) { Check(handle != 0, "page handle"); Handle = handle; document = doc; }
        public void Dispose() => Native.FPDF_ClosePage(Handle);
        public void Generate() => Check(Native.FPDFPage_GenerateContent(Handle), "generate content");
        public void AddText(string text, double x, double y)
        {
            var obj = Native.FPDFPageObj_NewTextObj(document, "Helvetica", 18);
            Check(obj != 0, "new text object");
            try
            {
                Check(Native.FPDFText_SetText(obj, text), "set text");
                Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
                Native.FPDFPage_InsertObject(Handle, obj);
            }
            catch { Native.FPDFPageObj_Destroy(obj); throw; }
        }
        /// <summary>Adds a single Helvetica line centred vertically in the box with the given left padding,
        /// refusing text that does not fit instead of clipping or overflowing it.</summary>
        public void AddTextInBox(string text, double left, double bottom, double right, double top, double padding, double size)
        {
            var obj = Native.FPDFPageObj_NewTextObj(document, "Helvetica", (float)size);
            Check(obj != 0, "new text object");
            try
            {
                Check(Native.FPDFText_SetText(obj, text), "set text");
                Check(Native.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t), "measure text");
                double width = r - l, height = t - b;
                if (!(width > 0) || !(height > 0))
                    throw new InvalidOperationException("The text has no measurable width. Use printable characters.");
                if (width + 2 * padding > right - left || height + 2 * padding > top - bottom)
                    throw new InvalidOperationException("The text does not fit the box. Enlarge the box or shorten the text.");
                var x = left + padding - l;
                var y = bottom + ((top - bottom) - height) / 2 - b;
                Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
                Native.FPDFPage_InsertObject(Handle, obj);
            }
            catch { Native.FPDFPageObj_Destroy(obj); throw; }
        }
        public string Text()
        {
            var text = Native.FPDFText_LoadPage(Handle);
            Check(text != 0, "text page");
            try
            {
                var count = Native.FPDFText_CountChars(text);
                Check(count >= 0, "character count");
                var buffer = new ushort[count + 1];
                var copied = Native.FPDFText_GetText(text, 0, count, buffer);
                return new string(buffer.Take(Math.Max(0, copied - 1)).Select(x => (char)x).ToArray());
            }
            finally { Native.FPDFText_ClosePage(text); }
        }
        public void Render(string path)
        {
            const int width = 612, height = 792;
            var bitmap = Native.FPDFBitmap_Create(width, height, 0);
            Check(bitmap != 0, "bitmap");
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                Native.FPDF_RenderPageBitmap(bitmap, Handle, 0, 0, width, height, 0, 1);
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var data = new byte[stride * height];
                Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), data, 0, data.Length);
                using var file = File.Create(path);
                file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                { var i = y * stride + x * 4; file.WriteByte(data[i + 2]); file.WriteByte(data[i + 1]); file.WriteByte(data[i]); }
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
    }
    internal static class Native
    {
        private const string Dll = "pdfium";
        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll, EntryPoint = "FPDF_GetLastError")] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern void FPDF_DestroyLibrary();
        [DllImport(Dll)] public static extern nint FPDF_CreateNewDocument();
        // PDFium expects UTF-8 for both the file name and password, including on Windows.
        [DllImport(Dll, EntryPoint = "FPDF_LoadDocument")]
        public static extern nint FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(nint doc);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(nint doc);
        [DllImport(Dll)] public static extern nint FPDFPage_New(nint doc, int index, double width, double height);
        [DllImport(Dll)] public static extern nint FPDF_LoadPage(nint doc, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(nint page);
        [DllImport(Dll)] public static extern int FPDFPage_CountObjects(nint page);
        [DllImport(Dll)] public static extern nint FPDFPage_GetObject(nint page, int index);
        [DllImport(Dll)] public static extern int FPDFPageObj_GetType(nint obj);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern nint FPDFPageObj_NewTextObj(nint doc, string font, float size);
        [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FPDFText_SetText(nint obj, string text);
        [DllImport(Dll)] public static extern void FPDFPageObj_Transform(nint obj, double a, double b, double c, double d, double e, double f);
        [DllImport(Dll)] public static extern void FPDFPage_InsertObject(nint page, nint obj);
        [DllImport(Dll)] public static extern int FPDFPageObj_GetBounds(nint obj, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(nint obj);
        [DllImport(Dll)] public static extern int FPDFPage_GenerateContent(nint page);
        [DllImport(Dll)] public static extern nint FPDFText_LoadPage(nint page);
        [DllImport(Dll)] public static extern void FPDFText_ClosePage(nint text);
        [DllImport(Dll)] public static extern int FPDFText_CountChars(nint text);
        [DllImport(Dll)] public static extern int FPDFText_GetText(nint text, int start, int count, [Out] ushort[] buffer);
        [DllImport(Dll)] public static extern uint FPDFText_GetUnicode(nint text, int index);
        [DllImport(Dll)] public static extern int FPDFText_GetCharBox(nint text, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Dll)] internal static extern int FPDF_SaveAsCopy(nint doc, ref FileWrite writer, uint flags);
        [DllImport(Dll)] public static extern nint FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(nint bitmap);
        [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(nint bitmap, int x, int y, int width, int height, uint color);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Dll)] public static extern nint FPDFBitmap_GetBuffer(nint bitmap);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(nint bitmap);
    }
}
