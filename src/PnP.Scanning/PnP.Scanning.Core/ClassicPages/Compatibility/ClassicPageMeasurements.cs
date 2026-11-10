#nullable enable
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Establishes measurement availability from sealed acquisition states and immutable analysis diagnostics, never default numeric values.
/// </summary>
internal sealed record ClassicPageMeasurements(
    bool PartCountKnown,
    bool MappingKnown,
    bool HomeFallbackKnown,
    string WebPartsReadState,
    string MappingReadState,
    string HomeFallbackReadState)
{
    /// <summary>
    /// Distinguishes a read empty part set from withheld file-only mapping and proves both halves of a completed home fallback.
    /// </summary>
    internal static ClassicPageMeasurements FromCaptured(
        FilePageEvidence input,
        ClassicPageRow page,
        ClassicPageDiscovery disposition,
        WebFacts web,
        int publishedParts)
    {
        bool partFailure = disposition.ErrorStage?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Contains("WebPartAssessment", StringComparer.Ordinal) == true;
        bool assessed = input.FileLevelOnly || input.MetadataState.Succeeded;
        bool partsKnown = assessed && input.WebPartsState.Succeeded && !partFailure;
        bool readinessType =
            (page.AssessmentPageType ?? page.PageType) is "WikiPage" or "WebPartPage" or "PublishingPage";
        bool mappingKnown = partsKnown && (input.FileLevelOnly ? publishedParts > 0 : readinessType);
        bool fallbackKnown = page.HomePage == true && !input.FileLevelOnly && readinessType && partsKnown &&
                             web.HomeFallbackState.Succeeded && input.HomeFallbackState.Succeeded;
        return new(
            partsKnown,
            mappingKnown,
            fallbackKnown,
            input.WebPartsState.Status,
            mappingKnown
                ? "Computed"
                : partsKnown && input.FileLevelOnly && publishedParts == 0
                    ? "WithheldFileOnlyEmpty"
                    : "Unavailable",
            input.HomeFallbackState.Status);
    }
}