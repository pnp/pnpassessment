using System.Text.Json;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class PageHandlerEvidence
    {
        public string DeclaredInherits { get; set; }
        public string BaseType { get; set; }
        public string TypeSource { get; set; }
        public string Status { get; set; }
        public PageSourceEvidence Source { get; set; }
        public string ConfigurationScope { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }

        internal string HandlerValue => Status switch
        {
            "Declared" => BaseType,
            "ReadFailed" or "ParseFailed" or "Unavailable" => $"ERROR: {Status} ({ErrorCode})",
            _ => null,
        };

        internal string ToJson() => JsonSerializer.Serialize(this);

        internal static PageHandlerEvidence Failure(string status, string code, string detail,
            string declaredInherits = null) => new()
        {
            Status = status,
            ErrorCode = code,
            ErrorDetail = detail,
            DeclaredInherits = declaredInherits,
        };
    }

    internal sealed class PageSourceEvidence
    {
        public string Method { get; set; }
        public Guid? FileUniqueId { get; set; }
        public string ServerRelativeUrl { get; set; }
        public string CustomizationStatus { get; set; }
        public long? ExpectedLength { get; set; }
        public int? BytesRead { get; set; }
    }

    internal sealed class PageSourceReadResult
    {
        internal string Content { get; init; }
        internal PageSourceEvidence Source { get; init; }
        internal PageHandlerEvidence Failure { get; init; }
    }

    internal sealed record PageBaseTypeConfiguration(string Value, string Scope);
}
