using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    const int FormTypeNone = 0;
    const int FormTypeAcroForm = 1;
    const int FormTypeXfaFull = 2;
    const int FormTypeXfaForeground = 3;
    const int RenderAnnotations = 0x01;
    const int RenderPrinting = 0x800;

    object RenderPage(Pdf pdf, int pageIndex, int width, int height, bool printing)
    {
        CheckPage(pdf, pageIndex);
        var formType = FormFillNative.FPDF_GetFormType(pdf.Handle);
        if (formType is FormTypeXfaFull or FormTypeXfaForeground)
            throw new InvalidOperationException("XFA form rendering is not supported. The page was not rendered without its form values.");
        if (formType != FormTypeNone && formType != FormTypeAcroForm)
            throw new InvalidOperationException("This PDF uses an unsupported form type. The page was not rendered without its form values.");

        using var page = pdf.Page(pageIndex);
        using var form = formType == FormTypeAcroForm ? new FormFillPage(pdf.Handle, page.Handle, pageIndex) : null;
        var bitmap = Pdf.Native.FPDFBitmap_Create(width, height, 0);
        Pdf.Check(bitmap != 0, "bitmap");
        try
        {
            Pdf.Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
            var flags = RenderAnnotations | (printing ? RenderPrinting : 0);
            Pdf.Native.FPDF_RenderPageBitmap(bitmap, page.Handle, 0, 0, width, height, 0, flags);
            if (form is not null)
                FormFillNative.FPDF_FFLDraw(form.Handle, bitmap, page.Handle, 0, 0, width, height,
                    0, flags);
            var stride = Pdf.Native.FPDFBitmap_GetStride(bitmap);
            var bytes = new byte[checked(stride * height)];
            Marshal.Copy(Pdf.Native.FPDFBitmap_GetBuffer(bitmap), bytes, 0, bytes.Length);
            return new { Width = width, Height = height, Stride = stride, Bgra = Convert.ToBase64String(bytes) };
        }
        finally { Pdf.Native.FPDFBitmap_Destroy(bitmap); }
    }

    public object RenderForPrint(Guid id, long revision, int index, int width, int height)
    {
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 8_000_000)
            throw new InvalidOperationException("Render size exceeds limit.");
        return RenderPage(IsolatedRenderDocument(id, revision), index, width, height, printing: true);
    }

    sealed class FormFillPage : IDisposable
    {
        internal nint Handle { get; }
        readonly nint page;
        readonly nint memory;
        bool pageRegistered;

        internal FormFillPage(nint document, nint page, int pageIndex)
        {
            this.page = page;
            var info = FormFillInfo.Create();
            memory = Marshal.AllocHGlobal(Marshal.SizeOf<FormFillInfo>());
            Marshal.StructureToPtr(info, memory, false);
            FormFillCallbacks.Contexts[memory] = new CallbackContext(document, page, pageIndex);
            nint initializedHandle = 0;
            try
            {
                initializedHandle = FormFillNative.FPDFDOC_InitFormFillEnvironment(document, memory);
                Handle = initializedHandle;
                if (Handle == 0)
                    throw new InvalidOperationException("PDF form rendering could not be initialized. No page image was returned.");
                FormFillNative.FORM_OnAfterLoadPage(page, Handle);
                pageRegistered = true;
            }
            catch
            {
                if (initializedHandle != 0) FormFillNative.FPDFDOC_ExitFormFillEnvironment(initializedHandle);
                FormFillCallbacks.Contexts.TryRemove(memory, out _);
                Marshal.FreeHGlobal(memory);
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (pageRegistered) FormFillNative.FORM_OnBeforeClosePage(page, Handle);
            }
            finally
            {
                try { if (Handle != 0) FormFillNative.FPDFDOC_ExitFormFillEnvironment(Handle); }
                finally
                {
                    FormFillCallbacks.Contexts.TryRemove(memory, out _);
                    Marshal.FreeHGlobal(memory);
                }
            }
        }
    }

    sealed record CallbackContext(nint Document, nint Page, int PageIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct FormFillInfo
    {
        public int Version;
        public nint Release, Invalidate, OutputSelectedRect, SetCursor, SetTimer, KillTimer, GetLocalTime;
        public nint OnChange, GetPage, GetCurrentPage, GetRotation, ExecuteNamedAction, SetTextFieldFocus;
        public nint DoUriAction, DoGoToAction, JsPlatform;
        public int XfaDisabled;
        public nint DisplayCaret, GetCurrentPageIndex, SetCurrentPage, GotoUrl, GetPageViewRect;
        public nint PageEvent, PopupMenu, OpenFile, EmailTo, UploadTo, GetPlatform, GetLanguage;
        public nint DownloadFromUrl, PostRequestUrl, PutRequestUrl, OnFocusChange, DoUriActionWithKeyboardModifier;

        internal static FormFillInfo Create()
        {
            if (IntPtr.Size != 8 || Marshal.SizeOf<FormFillInfo>() != 280 ||
                Marshal.OffsetOf<FormFillInfo>(nameof(Release)).ToInt32() != 8 ||
                Marshal.OffsetOf<FormFillInfo>(nameof(XfaDisabled)).ToInt32() != 136 ||
                Marshal.OffsetOf<FormFillInfo>(nameof(DoUriActionWithKeyboardModifier)).ToInt32() != 272)
                throw new PlatformNotSupportedException("The PDFium form-fill ABI layout does not match the pinned Windows x64 header.");
            return new FormFillInfo
            {
                Version = 1,
                Invalidate = FormFillCallbacks.Pointer(FormFillCallbacks.Invalidate),
                SetCursor = FormFillCallbacks.Pointer(FormFillCallbacks.SetCursor),
                SetTimer = FormFillCallbacks.Pointer(FormFillCallbacks.SetTimer),
                KillTimer = FormFillCallbacks.Pointer(FormFillCallbacks.KillTimer),
                GetLocalTime = FormFillCallbacks.Pointer(FormFillCallbacks.GetLocalTime),
                GetPage = FormFillCallbacks.Pointer(FormFillCallbacks.GetPage),
                GetCurrentPage = FormFillCallbacks.Pointer(FormFillCallbacks.GetCurrentPage),
                GetRotation = FormFillCallbacks.Pointer(FormFillCallbacks.GetRotation),
                ExecuteNamedAction = FormFillCallbacks.Pointer(FormFillCallbacks.ExecuteNamedAction),
                XfaDisabled = 1
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PdfSystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    static class FormFillCallbacks
    {
        internal static readonly ConcurrentDictionary<nint, CallbackContext> Contexts = new();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void InvalidateCallback(nint self, nint page, double left, double top, double right, double bottom);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CursorCallback(nint self, int cursor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TimerCallback(nint self, int milliseconds, nint callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void KillTimerCallback(nint self, int timerId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PdfSystemTime LocalTimeCallback(nint self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint GetPageCallback(nint self, nint document, int pageIndex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint GetCurrentPageCallback(nint self, nint document);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RotationCallback(nint self, nint page);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NamedActionCallback(nint self, nint action);

        internal static readonly InvalidateCallback Invalidate = (_, _, _, _, _, _) => { };
        internal static readonly CursorCallback SetCursor = (_, _) => { };
        // Rendering never runs JavaScript; refusing timers prevents a document action from escaping the render-only path.
        internal static readonly TimerCallback SetTimer = (_, _, _) => 0;
        internal static readonly KillTimerCallback KillTimer = (_, _) => { };
        internal static readonly LocalTimeCallback GetLocalTime = _ =>
        {
            var now = DateTime.Now;
            return new PdfSystemTime
            {
                Year = checked((ushort)(now.Year - 1900)), Month = checked((ushort)(now.Month - 1)),
                DayOfWeek = (ushort)now.DayOfWeek, Day = (ushort)now.Day, Hour = (ushort)now.Hour,
                Minute = (ushort)now.Minute, Second = (ushort)now.Second, Milliseconds = (ushort)now.Millisecond
            };
        };
        internal static readonly GetPageCallback GetPage = (self, document, pageIndex) =>
            Contexts.TryGetValue(self, out var context) && context.Document == document && context.PageIndex == pageIndex
                ? context.Page : 0;
        internal static readonly GetCurrentPageCallback GetCurrentPage = (self, document) =>
            Contexts.TryGetValue(self, out var context) && context.Document == document ? context.Page : 0;
        internal static readonly RotationCallback GetRotation = (_, page) => Rotation(page);
        internal static readonly NamedActionCallback ExecuteNamedAction = (_, _) => { };

        internal static nint Pointer(Delegate callback) => Marshal.GetFunctionPointerForDelegate(callback);
    }

    static class FormFillNative
    {
        const string Dll = "pdfium";
        [DllImport(Dll, EntryPoint = "FPDF_GetFormType", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_GetFormType(nint document);
        [DllImport(Dll, EntryPoint = "FPDFDOC_InitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint FPDFDOC_InitFormFillEnvironment(nint document, nint formFillInfo);
        [DllImport(Dll, EntryPoint = "FORM_OnAfterLoadPage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FORM_OnAfterLoadPage(nint page, nint formHandle);
        [DllImport(Dll, EntryPoint = "FORM_OnBeforeClosePage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FORM_OnBeforeClosePage(nint page, nint formHandle);
        [DllImport(Dll, EntryPoint = "FPDFDOC_ExitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFDOC_ExitFormFillEnvironment(nint formHandle);
        [DllImport(Dll, EntryPoint = "FPDF_FFLDraw", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_FFLDraw(nint formHandle, nint bitmap, nint page, int startX, int startY,
            int sizeX, int sizeY, int rotation, int flags);
    }
}
