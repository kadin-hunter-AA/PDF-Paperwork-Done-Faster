using System.IO;
using System.IO.MemoryMappedFiles;
using System.Printing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace HunterPdf.Desktop;

/// <summary>Streams sheets to a printer through GDI StartDoc/StartPage: EMF pages are played back (vector), raster pages use StretchDIBits.
/// The printer DC comes from the PrintTicket via prntvpt's DEVMODE conversion (falls back to the driver default plus copies/duplex/orientation).</summary>
internal sealed class GdiPrintSink : IPrintSink, IDisposable
{
    const int HorzRes = 8, VertRes = 10, LogPixelsX = 88, LogPixelsY = 90, DcCopies = 18;
    const uint DmOrientation = 1, DmCopies = 0x100, DmDuplex = 0x1000;
    readonly string printer;
    readonly nint devMode;
    readonly int devModeSize;
    nint dc;
    bool landscape, jobStarted, sheetOpen;

    public PrintDeviceInfo Device { get; }

    internal GdiPrintSink(PrintQueue queue, PrintTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(queue);
        printer = queue.FullName;
        devMode = BuildDevMode(printer, ticket, out devModeSize);
        var copies = ticket?.CopyCount ?? 1;
        // Let the driver replicate copies when it can; otherwise the pipeline prints the whole job once per copy.
        var driverMax = DeviceCapabilitiesW(printer, null, DcCopies, 0, devMode);
        var software = copies > Math.Max(1, driverMax) ? copies : 1;
        if (software > 1) SetShort(devMode, 86, 1);
        dc = CreateDCW("WINSPOOL", printer, 0, devMode);
        if (dc == 0) throw new InvalidOperationException("The selected printer could not be opened.");
        landscape = ReadShort(devMode, 76) == 2;
        var (w, h) = Area();
        Device = new PrintDeviceInfo(landscape ? h : w, landscape ? w : h, landscape, software);
    }

    (double W, double H) Area() =>
        (GetDeviceCaps(dc, HorzRes) * 72d / Math.Max(1, GetDeviceCaps(dc, LogPixelsX)), GetDeviceCaps(dc, VertRes) * 72d / Math.Max(1, GetDeviceCaps(dc, LogPixelsY)));

    public Task BeginJobAsync(string jobName, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = string.IsNullOrWhiteSpace(jobName) ? "PDF" : jobName };
        if (StartDocW(dc, ref info) <= 0) throw new InvalidOperationException("The printer could not start the print job.");
        jobStarted = true;
    }, cancellation);

    public Task BeginSheetAsync(SheetPlan plan, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        if (plan.Landscape != landscape)
        {
            SetShort(devMode, 76, (short)(plan.Landscape ? 2 : 1));
            SetFields(devMode, DmOrientation);
            if (ResetDCW(dc, devMode) == 0) throw new InvalidOperationException("The printer could not change page orientation.");
            landscape = plan.Landscape;
        }
        if (StartPage(dc) <= 0) throw new InvalidOperationException("The printer could not start a page.");
        sheetOpen = true;
    }, cancellation);

    public Task DrawPageAsync(PrintPlacement p, PrintPagePayload payload, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var xs = GetDeviceCaps(dc, LogPixelsX) / 72d;
        var ys = GetDeviceCaps(dc, LogPixelsY) / 72d;
        // Plan coordinates are relative to the printable area (the GDI origin).
        int X(double v) => (int)Math.Round(v * xs);
        int Y(double v) => (int)Math.Round(v * ys);
        var save = SaveDC(dc);
        try
        {
            IntersectClipRect(dc, X(p.Clip.X), Y(p.Clip.Y), X(p.Clip.Right), Y(p.Clip.Bottom));
            var left = X(p.Target.X); var top = Y(p.Target.Y);
            var width = Math.Max(1, X(p.Target.Right) - left); var height = Math.Max(1, Y(p.Target.Bottom) - top);
            if (payload.IsEmf)
            {
                var emf = GetEnhMetaFileW(payload.Path);
                if (emf == 0) throw new InvalidOperationException("The prepared vector page could not be read.");
                try
                {
                    var rect = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
                    if (!PlayEnhMetaFile(dc, emf, ref rect)) throw new InvalidOperationException("The printer rejected the vector page.");
                }
                finally { DeleteEnhMetaFile(emf); }
            }
            else
            {
                using var map = MemoryMappedFile.CreateFromFile(payload.Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var bits = view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)view.PointerOffset;
                {
                    var bmi = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = payload.PixelWidth, Height = -payload.PixelHeight, Planes = 1, BitCount = 32 };
                    SetStretchBltMode(dc, 4); // HALFTONE
                    SetBrushOrgEx(dc, 0, 0, 0);
                    if (StretchDIBits(dc, left, top, width, height, 0, 0, payload.PixelWidth, payload.PixelHeight, bits, ref bmi, 0, 0x00CC0020) == 0)
                        throw new InvalidOperationException("The printer rejected the page image.");
                }
            }
        }
        finally { RestoreDC(dc, save); }
    }, cancellation);

    public Task EndSheetAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        if (!sheetOpen) return;
        sheetOpen = false;
        if (EndPage(dc) <= 0) throw new InvalidOperationException("The printer stopped the job.");
    });

    public Task EndJobAsync(bool completed) => Task.Run(() =>
    {
        if (!jobStarted) return;
        jobStarted = false;
        if (completed) { if (sheetOpen) { sheetOpen = false; EndPage(dc); } if (EndDoc(dc) <= 0) throw new InvalidOperationException("The printer could not finish the print job."); }
        else AbortDoc(dc);
    });

    public void Dispose()
    {
        if (jobStarted) { AbortDoc(dc); jobStarted = false; }
        if (dc != 0) { DeleteDC(dc); dc = 0; }
        if (devMode != 0) Marshal.FreeHGlobal(devMode);
    }

    static nint BuildDevMode(string printer, PrintTicket? ticket, out int size)
    {
        size = 0;
        var converted = ticket is null ? 0 : TryConvertTicket(printer, ticket, out size);
        if (converted != 0) return converted;
        var needed = DocumentPropertiesW(0, 0, printer, 0, 0, 0);
        if (needed <= 0) throw new InvalidOperationException("The printer settings could not be read.");
        var memory = Marshal.AllocHGlobal(needed);
        if (DocumentPropertiesW(0, 0, printer, memory, 0, 2) < 0) { Marshal.FreeHGlobal(memory); throw new InvalidOperationException("The printer settings could not be read."); }
        size = needed;
        if (ticket is not null)
        {
            if (ticket.CopyCount is { } c) { SetShort(memory, 86, (short)Math.Clamp(c, 1, 999)); SetFields(memory, DmCopies); }
            if (ticket.PageOrientation is { } o) { SetShort(memory, 76, (short)(o is PageOrientation.Landscape or PageOrientation.ReverseLandscape ? 2 : 1)); SetFields(memory, DmOrientation); }
            if (ticket.Duplexing is { } d)
            {
                SetShort(memory, 94, (short)(d switch { Duplexing.TwoSidedLongEdge => 2, Duplexing.TwoSidedShortEdge => 3, _ => 1 })); SetFields(memory, DmDuplex);
            }
        }
        return memory;
    }

    static nint TryConvertTicket(string printer, PrintTicket ticket, out int size)
    {
        size = 0;
        nint provider = 0;
        try
        {
            if (PTOpenProvider(printer, 1, out provider) != 0) return 0;
            var xml = ticket.GetXmlStream().ToArray();
            if (CreateStreamOnHGlobal(0, true, out var stream) != 0) return 0;
            try
            {
                stream.Write(xml, xml.Length, nint.Zero);
                stream.Seek(0, 0, nint.Zero);
                if (PTConvertPrintTicketToDevMode(provider, stream, 1, 2, out var length, out var native, out _) != 0 || native == 0 || length < 128) return 0;
                try
                {
                    var copy = Marshal.AllocHGlobal(length);
                    var bytes = new byte[length]; Marshal.Copy(native, bytes, 0, length); Marshal.Copy(bytes, 0, copy, length);
                    size = length;
                    return copy;
                }
                finally { PTReleaseMemory(native); }
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException) { return 0; }
        finally { if (provider != 0) PTCloseProvider(provider); }
    }

    static short ReadShort(nint p, int offset) => Marshal.ReadInt16(p, offset);
    static void SetShort(nint p, int offset, short value) => Marshal.WriteInt16(p, offset, value);
    static void SetFields(nint p, uint flags) => Marshal.WriteInt32(p, 72, Marshal.ReadInt32(p, 72) | (int)flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DocInfo { public int Size; public string Name; public string? Output; public string? Datatype; public int Type; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ColorsUsed, ColorsImportant; }

    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern nint CreateDCW(string driver, string device, nint output, nint devMode);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern int StartDocW(nint dc, ref DocInfo info);
    [DllImport("gdi32")] static extern int EndDoc(nint dc);
    [DllImport("gdi32")] static extern int AbortDoc(nint dc);
    [DllImport("gdi32")] static extern int StartPage(nint dc);
    [DllImport("gdi32")] static extern int EndPage(nint dc);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern nint ResetDCW(nint dc, nint devMode);
    [DllImport("gdi32")] static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("gdi32")] static extern int SaveDC(nint dc);
    [DllImport("gdi32")] static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32")] static extern int IntersectClipRect(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern nint GetEnhMetaFileW(string path);
    [DllImport("gdi32")] static extern bool DeleteEnhMetaFile(nint emf);
    [DllImport("gdi32")] static extern bool PlayEnhMetaFile(nint dc, nint emf, ref Rect rect);
    [DllImport("gdi32")] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32")] static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32")] static extern int StretchDIBits(nint dc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc, nint bits, ref BitmapInfoHeader info, uint usage, uint rop);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode)] static extern int DocumentPropertiesW(nint window, nint printer, string name, nint output, nint input, int mode);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode)] static extern int DeviceCapabilitiesW(string device, string? port, int capability, nint output, nint devMode);
    [DllImport("prntvpt")] static extern int PTOpenProvider([MarshalAs(UnmanagedType.LPWStr)] string printer, int version, out nint provider);
    [DllImport("prntvpt")] static extern int PTCloseProvider(nint provider);
    [DllImport("prntvpt")] static extern int PTReleaseMemory(nint buffer);
    [DllImport("prntvpt")] static extern int PTConvertPrintTicketToDevMode(nint provider, IStream ticket, int defaultType, int scope, out int length, out nint devMode, [MarshalAs(UnmanagedType.BStr)] out string? error);
    [DllImport("ole32")] static extern int CreateStreamOnHGlobal(nint global, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
}
