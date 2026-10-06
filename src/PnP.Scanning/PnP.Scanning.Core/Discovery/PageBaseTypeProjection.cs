namespace PnP.Scanning.Core.Discovery;

internal enum PageTypeSource { Unknown, Declared, ConfiguredDefault, FrameworkDefault }

/// <summary>A projected source declaration/default, never verified CLR assignability or an observed Handler.</summary>
internal sealed record PageBaseTypeProjection(AspxSourceReadResult SourceRead, PageDirectiveParseResult Parse,
    string DeclaredInherits, string BaseType, PageTypeSource TypeSource, string Reason,
    PageBaseTypeConfigurationEvaluation Configuration)
{
    internal const string FrameworkPageBaseType = "System.Web.UI.Page";
    internal string NormalizedInherits => Parse.NormalizedInherits;
    internal bool IsFrameworkDefaultAssumption => TypeSource == PageTypeSource.FrameworkDefault;
    internal bool IsReliableParse => SourceRead?.IsReliableSource == true &&
        (Parse.IsReliableDeclaration || Parse.IsReliableAbsence);
    internal bool IsVerifiedAbsence => IsReliableParse && Parse.IsReliableAbsence;

    internal static PageBaseTypeProjection Inspect(AspxSourceReadResult read, PageBaseTypeConfiguration configuration = null)
    {
        var parsed = PageDirectiveParser.Parse(read?.DecodedText);
        var configured = (configuration ?? new()).Evaluate(read?.PhysicalIdentity);
        PageBaseTypeProjection Result(string baseType, PageTypeSource source, string reason) =>
            new(read, parsed, parsed.DeclaredInherits, baseType, source, reason, configured);
        if (read?.IsReliableSource != true)
            return Result(null, PageTypeSource.Unknown, read == null ? "SourceReadResultNotReturned" :
                "SourceAcquisitionNotReliable:" + string.Join(';', read.TransportState.ToString(), read.TransportReason,
                    read.CaptureState.ToString(), read.ContentState.ToString(), read.ContentReason, read.Decoding.State.ToString(), read.Decoding.Reason,
                    read.PhysicalIdentity.Reason, read.IdentityComparisonReason));
        // Declaration precedence does not depend on configuration or the narrow CLR-family binder.
        if (parsed.IsReliableDeclaration) return Result(parsed.NormalizedInherits, PageTypeSource.Declared, "ExplicitPageInherits;NotRuntimeTypeProof");
        if (!parsed.IsReliableAbsence) return Result(null, PageTypeSource.Unknown, "PageDirectiveNotReliable:" + parsed.Reason);
        if (configured.KnowledgeState is PageConfigurationKnowledge.Conflicting or PageConfigurationKnowledge.Unsupported)
            return Result(null, PageTypeSource.Unknown, "DefaultSuppressed:" + configured.Reason);
        if (configured.KnowledgeState == PageConfigurationKnowledge.EffectiveOverride &&
            configured.Applicability == PageConfigurationApplicability.Applicable)
            return Result(configured.EffectivePageBaseType.Trim(), PageTypeSource.ConfiguredDefault,
                "ApplicableFrozenPagesPageBaseType;NotRuntimeTypeProof");
        return Result(FrameworkPageBaseType, PageTypeSource.FrameworkDefault,
            "FrameworkDefaultAssumption;NoKnownApplicableOverride;" + configured.Reason);
    }
}
