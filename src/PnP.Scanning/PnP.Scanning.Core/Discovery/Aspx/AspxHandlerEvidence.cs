namespace PnP.Scanning.Core.Discovery.Aspx;

internal enum AspxHandlerSourceKind
{
    Unknown,
    ActualFileBytes,
    RenderedHtml,
    PathOnly
}

internal enum AspxHandlerReadState
{
    Available,
    Denied,
    Unavailable,
    Truncated,
    NotRequested
}

internal enum AspxHandlerParseState
{
    NotParsed,
    Declared,
    NoInherits,
    Malformed,
    InvalidEncoding,
    UnsupportedSource
}

internal enum AspxHandlerDefaultProvenance
{
    None,
    FrameworkDefaultAssumptionNotConfigResolved
}

/// <summary>
/// Source facts only, never a classic/modern or user/non-user classification.
/// A framework default is an explicitly labelled assumption: web.config was not resolved.
/// DeclaredInherits retains the source attribute value; normalization only trims its edges.
/// CLR case and assembly qualification are preserved and no type or assembly is loaded.
/// </summary>
internal sealed record AspxHandlerEvidence(
    AspxHandlerReadState ReadState,
    AspxHandlerParseState ParseState,
    string DeclaredInherits,
    string NormalizedInherits,
    string EffectiveInherits,
    AspxHandlerSourceKind Source,
    AspxHandlerDefaultProvenance DefaultProvenance,
    string ReasonCode = null);
