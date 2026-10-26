using System.Text.Json;
namespace HunterPdf.Core;
public sealed record Request(Guid RequestId, Guid DocumentId, long Revision, string Operation, JsonElement Arguments);
public sealed record Response(Guid RequestId, Guid DocumentId, long Revision, bool Success, JsonElement Data, string? Error, string? ErrorCode = null);
public static class WireErrorCodes {
    public const string PasswordRequired = "password_required";
}
public static class Wire {
    public static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value);
    public static Request Create(Guid document, long revision, string operation, object args) => new(Guid.NewGuid(), document, revision, operation, Data(args));
}
