using System.Runtime.InteropServices;

sealed partial class Pdf
{
    nint ownedRenderBytes;

    internal Pdf CloneForRendering(string? password)
    {
        const long maximumBytes = 128L * 1024 * 1024;
        using var output = new MemoryStream();
        Exception? failure = null;
        WriteBlock callback = (_, data, size) =>
        {
            try
            {
                if (output.Length + size > maximumBytes) throw new IOException("The isolated native preview exceeds its memory limit.");
                var buffer = new byte[checked((int)size)]; Marshal.Copy(data, buffer, 0, buffer.Length); output.Write(buffer); return 1;
            }
            catch (Exception error) { failure = error; return 0; }
        };
        var writer = new FileWrite { Version = 1, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var result = Native.FPDF_SaveAsCopy(Handle, ref writer, 2);
        GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("The isolated render copy could not be created.", failure);
        Check(result, "create isolated render copy");
        var bytes = output.ToArray();
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var copy = new Pdf(RenderCopyNative.LoadMemory(memory, (nuint)bytes.Length, password));
            copy.ownedRenderBytes = memory;
            return copy;
        }
        catch { Marshal.FreeHGlobal(memory); throw; }
    }
    internal void ReleaseRenderBytes()
    {
        if (ownedRenderBytes == 0) return;
        Marshal.FreeHGlobal(ownedRenderBytes); ownedRenderBytes = 0;
    }
    static class RenderCopyNative
    {
        [DllImport("pdfium", EntryPoint = "FPDF_LoadMemDocument64")]
        internal static extern nint LoadMemory(nint data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    }
}
