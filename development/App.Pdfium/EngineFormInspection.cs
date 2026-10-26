using System.Runtime.InteropServices;

namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    const int FormFieldText = 6;
    const int FormFieldSignature = 7;
    const int FormFieldXfaFirst = 8;
    const int FormFlagReadOnly = 1;
    const int FormFlagPassword = 1 << 13;
    const int AnnotationFlagReadOnly = 1 << 6;
    const int AnnotationWidget = 20;

    public object InspectFormFields(Guid id, long revision, int pageIndex)
    {
        // Form-fill initialization can generate appearances. Keep all such
        // read operations on the isolated view model, never editable state.
        var pdf = IsolatedRenderDocument(id, revision);
        CheckPage(pdf, pageIndex);
        var formType = FormFillNative.FPDF_GetFormType(pdf.Handle);
        if (formType is FormTypeXfaFull or FormTypeXfaForeground)
            throw new InvalidOperationException("XFA form inspection is not supported.");
        if (formType != FormTypeAcroForm)
            return new { Page = pageIndex, Fields = Array.Empty<object>() };

        using var page = pdf.Page(pageIndex);
        using var form = new FormFillPage(pdf.Handle, page.Handle, pageIndex);
        var fields = new List<object>();
        var count = Pdf.Objects.FPDFPage_GetAnnotCount(page.Handle);
        if (count < 0 || count > 100_000) throw new InvalidOperationException("The page has an unsupported annotation count.");
        var inspectedWidgets = 0;
        var totalStringBytes = 0;
        for (var index = 0; index < count; index++)
        {
            var annotation = Pdf.Objects.FPDFPage_GetAnnot(page.Handle, index);
            if (annotation == 0) continue;
            try
            {
                if (Pdf.Objects.FPDFAnnot_GetSubtype(annotation) != AnnotationWidget) continue;
                if (++inspectedWidgets > 1_000) throw new InvalidOperationException("The page has too many form widgets to inspect safely.");
                var type = FormInspectionNative.FPDFAnnot_GetFormFieldType(form.Handle, annotation);
                if (type < 0) continue;
                var flags = FormInspectionNative.FPDFAnnot_GetFormFieldFlags(form.Handle, annotation);
                var name = ReadFormString((buffer, size) => FormInspectionNative.FPDFAnnot_GetFormFieldName(form.Handle, annotation, buffer, size));
                var password = type == FormFieldText && (flags & FormFlagPassword) != 0;
                // Never fetch password or signature values. Unknown and XFA fields are metadata-only.
                var value = type is FormFieldText or 2 or 3 or 4 or 5 && !password
                    ? ReadFormString((buffer, size) => FormInspectionNative.FPDFAnnot_GetFormFieldValue(form.Handle, annotation, buffer, size))
                    : null;
                totalStringBytes = checked(totalStringBytes + (name is null ? 0 : System.Text.Encoding.Unicode.GetByteCount(name)) +
                    (value is null ? 0 : System.Text.Encoding.Unicode.GetByteCount(value)));
                if (totalStringBytes > 1_048_576) throw new InvalidOperationException("Form field metadata exceeds the inspection size limit.");
                var readOnly = (flags & FormFlagReadOnly) != 0 || (Pdf.Objects.FPDFAnnot_GetFlags(annotation) & AnnotationFlagReadOnly) != 0;
                var kind = type switch
                {
                    1 => "PushButton", 2 => "Checkbox", 3 => "RadioButton", 4 => "ComboBox",
                    5 => "ListBox", FormFieldText => "Text", FormFieldSignature => "Signature",
                    >= FormFieldXfaFirst => "XFA", _ => "Unknown"
                };
                fields.Add(new { Index = index, Name = name, Value = value, Type = kind, ReadOnly = readOnly,
                    Password = password, Editable = false, UnsupportedReason = "AcroForm writing is not available with this pinned PDFium API." });
            }
            finally { Pdf.Objects.FPDFPage_CloseAnnot(annotation); }
        }
        return new { Page = pageIndex, Fields = fields };
    }

    delegate uint ReadFormStringDelegate(nint buffer, uint size);
    static string? ReadFormString(ReadFormStringDelegate read)
    {
        var size = read(0, 0);
        if (size == 0) return null;
        if (size < 2 || size > 65_536 || (size & 1) != 0)
            throw new InvalidOperationException("A form field string exceeds the inspection limit or is malformed.");
        var bufferSize = checked(size + 2);
        var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            var copied = read(buffer, bufferSize);
            if (copied != size || (copied & 1) != 0)
                throw new InvalidOperationException("A form field string changed or was malformed during inspection.");
            var chars = new char[checked((int)copied / 2)];
            Marshal.Copy(buffer, chars, 0, chars.Length);
            var length = Array.IndexOf(chars, '\0');
            return new string(chars, 0, length < 0 ? chars.Length : length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static class FormInspectionNative
    {
        const string Dll = "pdfium";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFAnnot_GetFormFieldType(nint form, nint annotation);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFAnnot_GetFormFieldFlags(nint form, nint annotation);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFAnnot_GetFormFieldName(nint form, nint annotation, nint buffer, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFAnnot_GetFormFieldValue(nint form, nint annotation, nint buffer, uint size);
    }
}
