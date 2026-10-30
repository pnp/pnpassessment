#nullable enable
using System.Text.Json;
using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners.Classic;
using PnP.Scanning.Core.Scanners.Classic.Pages;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

/// <summary>Checks that the export adapter delegates classification and does not add a competing rule set or raw-mask acquisition claim.</summary>
public sealed class ClassicAspxExportClassifierTests
{
    private const string ModernApplication = "B6917CB1-93A0-4B97-A84D-7CF49975D4EC";
    private const string PublishingContentType = "0x01010007FF3E057FA8AB4AA42FCB67B453FFC101";
    private const string ModernContentType = "0x0101009D1CB255DA76424F860D91F20E6C411800";

    /// <summary>Creates available item observations with the exact field-state contract used by acquisition.</summary>
    private static SPFile ItemFile(Dictionary<string, object> fields)
    {
        return new SPFile
        {
            ItemMetadataReadState = "Available",
            ItemMetadataFieldStatesJson = JsonSerializer.Serialize(fields.ToDictionary(
                field => field.Key, field => field.Value == null ? "null" : "value")),
            ClientSideApplicationId = fields.GetValueOrDefault("ClientSideApplicationId") as string,
            HtmlFileType = fields.GetValueOrDefault("HTML_x0020_File_x0020_Type") as string,
            FileType = fields.GetValueOrDefault("File_x0020_Type") as string,
            BSN = fields.GetValueOrDefault("BSN") as string,
            WikiFieldHasValue = fields.TryGetValue("WikiField", out var wiki) ? wiki != null : null
        };
    }

    /// <summary>Compares the adapter directly with the existing method while retaining the existing webpart-before-modern and modern-before-wiki priorities.</summary>
    [Theory]
    [InlineData("SharePoint.WebPartPage.Document", ModernApplication, true, "WebPartPage")]
    [InlineData(null, ModernApplication, true, "ModernPage")]
    [InlineData(null, null, true, "WikiPage")]
    [InlineData(null, null, false, "ASPXPage")]
    public void AvailableFieldsDelegateToCurrentAssessmentMethod(string? htmlType, string? application, bool wiki, string expected)
    {
        var fields = new Dictionary<string, object>
        {
            ["HTML_x0020_File_x0020_Type"] = htmlType!,
            ["ClientSideApplicationId"] = application!,
            ["WikiField"] = wiki ? string.Empty : null!
        };
        var result = ClassicAspxExportClassifier.Classify(ItemFile(fields));
        result.PageType.Should().Be(PageScanComponent.GetPageType(fields)).And.Be(expected);
        result.State.Should().Be("ItemMetadata");
    }

    /// <summary>Preserves the scanner's publishing content-type override rather than creating a new modern-conflict policy.</summary>
    [Fact]
    public void PublishingOverrideStillPrecedesExistingFieldMethod()
    {
        var file = ItemFile(new Dictionary<string, object> { ["ClientSideApplicationId"] = ModernApplication });
        file.ContentTypeId = PublishingContentType;
        SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(file.ContentTypeId).Should().BeTrue();
        var result = ClassicAspxExportClassifier.Classify(file);
        result.PageType.Should().Be("PublishingPage");
        result.IsModern.Should().BeFalse();
    }

    /// <summary>Does not add a content-type modern override to an available item snapshot; the existing item classifier remains authoritative in this branch.</summary>
    [Fact]
    public void AvailableItemDoesNotAcquireNewContentTypePrecedence()
    {
        var file = ItemFile(new Dictionary<string, object> { ["BSN"] = "" });
        file.ContentTypeId = ModernContentType;
        var result = ClassicAspxExportClassifier.Classify(file);
        result.PageType.Should().Be("WikiPage");
        result.IsModern.Should().BeFalse();
    }

    /// <summary>Retains the subtle existing BSN omitted/null/empty distinction instead of treating all absent-looking values as equal.</summary>
    [Theory]
    [InlineData("omitted", null, "ASPXPage")]
    [InlineData("null", null, "ASPXPage")]
    [InlineData("value", "", "WikiPage")]
    [InlineData("value", "raw", "ASPXPage")]
    public void FieldPresenceRestoresExistingFallback(string state, string? bsn, string expected)
    {
        var file = new SPFile
        {
            ItemMetadataReadState = "Available",
            ItemMetadataFieldStatesJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["BSN"] = state }),
            BSN = bsn,
            ClientSideApplicationId = ModernApplication,
            HtmlFileType = "SharePoint.WebPartPage.Document"
        };
        ClassicAspxExportClassifier.Classify(file).PageType.Should().Be(expected);
    }

    /// <summary>Maps the retained Wiki Boolean to an empty nonnull value or null, while respecting the original omitted/null/value field-state envelope.</summary>
    [Theory]
    [InlineData("omitted", true, "ASPXPage")]
    [InlineData("null", true, "ASPXPage")]
    [InlineData("value", true, "WikiPage")]
    [InlineData("value", false, "ASPXPage")]
    public void WikiBooleanDoesNotRetainSourceHtml(string state, bool hasValue, string expected)
    {
        var file = new SPFile
        {
            ItemMetadataReadState = "Available",
            ItemMetadataFieldStatesJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["WikiField"] = state }),
            WikiFieldHasValue = hasValue
        };
        var result = ClassicAspxExportClassifier.Classify(file);
        result.PageType.Should().Be(expected);
        result.State.Should().Be("ItemMetadata");
    }

    /// <summary>Leaves the existing Delve-blog marker to the original field classifier rather than encoding a second rule in the adapter.</summary>
    [Fact]
    public void DelveMarkerStillUsesExistingFieldClassification()
    {
        var fields = new Dictionary<string, object> { ["File_x0020_Type"] = "pointpub" };
        ClassicAspxExportClassifier.Classify(ItemFile(fields)).PageType.Should().Be(PageScanComponent.GetPageType(fields)).And.Be("DelveBlogPage");
    }

    /// <summary>Uses the unchanged provider content-type method when item metadata was not acquired, including its unknown and modern outcomes.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("0x01010901", "WebPartPage")]
    [InlineData("0x010109", "BasicPage")]
    [InlineData("0x010108", "WikiPage")]
    [InlineData("0x010105", "MasterPage")]
    [InlineData("0x0101", "OtherAspxContentType")]
    [InlineData(ModernContentType, "ModernSitePage")]
    [InlineData(PublishingContentType, "PublishingPage")]
    public void MissingMetadataUsesOnlyExistingContentTypeFallback(string? contentType, string? expected)
    {
        var file = new SPFile { ContentTypeId = contentType, ItemMetadataReadState = "Unavailable" };
        var result = ClassicAspxExportClassifier.Classify(file);
        result.PageType.Should().Be(SharePointLiveAspxDiscoveryProvider.InferPageType(contentType)).And.Be(expected);
        result.State.Should().Be(expected == null ? "Unknown" : "FileOnlyFallback");
        result.IsModern.Should().Be(expected == "ModernSitePage");
    }

    /// <summary>Retains malformed or inconsistent item snapshots via the existing content-type fallback rather than throwing or creating a classification rule.</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"BSN\":\"value\"}")]
    [InlineData("{\"BSN\":true}")]
    [InlineData("{\"BSN\":\"unknown\"}")]
    public void UnrestorableSnapshotDoesNotDropTheFile(string states)
    {
        var file = new SPFile { ItemMetadataReadState = "Available", ItemMetadataFieldStatesJson = states, ContentTypeId = "0x010109" };
        var result = ClassicAspxExportClassifier.Classify(file);
        result.PageType.Should().Be("BasicPage");
        result.State.Should().Be("FileOnlyFallback");
    }

    /// <summary>Interprets the original server zero/one field without claiming it contains the other DocFlags bits.</summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void OriginalNoExecuteFieldDeterminesDisposition(string raw, bool expected)
    {
        var result = ClassicAspxPoisonAnalysis.AnalyzeRaw(raw, "Available", "SPO.ListItem.NoExecute");
        result.IsPoisoned.Should().Be(expected);
        result.PoisonState.Should().Be(expected ? "Poisoned" : "NotPoisoned");
        result.RenderDisposition.Should().Be(expected ? "NoExecuteDownload" : "NotBlockedByNoExecute");
        ClassicAspxPoisonAnalysis.EvidenceKind.Should().Be("ServerNoExecuteField");
    }

    /// <summary>Does not coerce missing text, Boolean spellings, padding, other integers or masks into the server's original zero/one field.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("16777216")]
    [InlineData("0x01000000")]
    public void MissingOrInvalidNoExecuteTextRemainsUnknown(string? raw)
    {
        var result = ClassicAspxPoisonAnalysis.AnalyzeRaw(raw, "Available", "SPO.ListItem.NoExecute");
        result.IsPoisoned.Should().BeNull();
        result.PoisonState.Should().Be("Unknown");
        result.RenderDisposition.Should().Be("Unknown");
    }

    /// <summary>Requires the actual source and an available, conflict-free read rather than deriving a false value from unavailable metadata.</summary>
    [Theory]
    [InlineData("0", "Unavailable", "SPO.ListItem.NoExecute", false)]
    [InlineData("1", "NotAttempted", "SPO.ListItem.NoExecute", false)]
    [InlineData("0", "NotRequested", "SPO.ListItem.NoExecute", false)]
    [InlineData("1", "Available", null, false)]
    [InlineData("0", "Available", "OtherSource", false)]
    [InlineData("1", "Available", "NoExecute", false)]
    [InlineData("1", "Available", "SPO.ListItem.NoExecute", true)]
    public void UnavailableOrUntrustedNoExecuteEvidenceIsUnknown(string raw, string state, string? source, bool conflict)
    {
        var result = ClassicAspxPoisonAnalysis.AnalyzeRaw(raw, state, source, conflict);
        result.IsPoisoned.Should().BeNull();
        result.PoisonState.Should().Be("Unknown");
        result.RenderDisposition.Should().Be("Unknown");
    }

    /// <summary>Leaves the original field text intact even when it cannot be interpreted as available server NoExecute evidence.</summary>
    [Theory]
    [InlineData("1", "Available", "SPO.ListItem.NoExecute", true)]
    [InlineData("0", "Available", "SPO.ListItem.NoExecute", false)]
    [InlineData("01", "Available", "SPO.ListItem.NoExecute", null)]
    [InlineData("1", "Unavailable", "SPO.ListItem.NoExecute", null)]
    [InlineData("1", "Available", null, null)]
    public void EntityAnalysisNeverRewritesOriginalNoExecuteText(string raw, string state, string? source, bool? expected)
    {
        var file = new SPFile { NoExecuteRaw = raw, NoExecuteReadState = state, NoExecuteSource = source };
        ClassicAspxPoisonAnalysis.Analyze(file).IsPoisoned.Should().Be(expected);
        file.NoExecuteRaw.Should().Be(raw);
    }

    /// <summary>Uses the writer's existing per-field conflict evidence to fail closed without rejecting unrelated metadata changes.</summary>
    [Theory]
    [InlineData("NoExecuteRaw", true)]
    [InlineData("NoExecuteSource", true)]
    [InlineData("NoExecuteReadState", true)]
    [InlineData("SetupPath", false)]
    public void NoExecuteConflictsRemainUnknown(string field, bool unknown)
    {
        var file = new SPFile
        {
            NoExecuteRaw = "1", NoExecuteReadState = "Available", NoExecuteSource = "SPO.ListItem.NoExecute",
            ProvenanceJson = JsonSerializer.Serialize(new[] { new { observation = "metadata_changed", field, before = "0", after = "1" } })
        };
        ClassicAspxPoisonAnalysis.Analyze(file).IsPoisoned.Should().Be(unknown ? null : true);
    }
}
