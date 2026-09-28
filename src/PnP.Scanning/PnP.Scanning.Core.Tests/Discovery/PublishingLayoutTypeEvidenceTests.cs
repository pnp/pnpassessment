using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class PublishingLayoutTypeEvidenceTests
{
    internal const string Root = PublishingLayoutTypeCatalog.RootName + ", " + PublishingLayoutTypeCatalog.PublishingAssembly;
    internal const string FixtureAssembly = "Synthetic.Layouts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    internal static string Identity(string name) => "Synthetic." + name + ", " + FixtureAssembly;
    internal static string Source(string identity) => "<%@ Page Language='C#' Inherits='" + identity + "' %>";

    [Theory]
    [InlineData("16.0.0.0")]
    [InlineData("15.0.0.0")]
    public void Exact_qualified_root_is_source_evidence_not_runtime_observation(string version)
    {
        var result = PublishingLayoutTypeEvidence.Inspect(Source(Root.Replace("16.0.0.0", version)), new());
        result.Decision.Should().Be("Member");
        result.SourceStatus.Should().Be("Available");
        result.ResolutionStatus.Should().Be("Resolved");
        result.Ancestry.Should().ContainSingle().Which.Provenance.Should().Be("WellKnownPublishingLayoutIdentity:v1");
    }

    [Theory]
    [InlineData("Direct", "Member", "RootOrProvenSubclass", 2)]
    [InlineData("Indirect", "Member", "RootOrProvenSubclass", 3)]
    [InlineData("Outside", "NonMember", "ProvenOutsideFamily", 2)]
    [InlineData("PublishingLayoutPageLookalike", "NonMember", "ProvenOutsideFamily", 2)]
    [InlineData("Incomplete", "Unknown", "IncompleteAncestry", 1)]
    [InlineData("Missing", "Unknown", "UnresolvedIdentity", 0)]
    public void Deployed_assembly_metadata_proves_edges_without_loading_or_executing_code(
        string name, string decision, string reason, int edges)
    {
        using var fixture = new MetadataFixture();
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { fixture.Path });
        catalog.Errors.Should().BeEmpty();
        var snapshot = PublishingLayoutTypeCatalog.FromJson(catalog.ToJson());
        var result = PublishingLayoutTypeEvidence.Inspect(Source(Identity(name)), snapshot);
        result.Decision.Should().Be(decision);
        result.Reason.Should().Be(reason);
        result.Ancestry.Should().HaveCount(edges);
        foreach (var edge in result.Ancestry.Where(edge => edge.Identity.StartsWith("Synthetic.")))
            edge.Provenance.Should().Contain("ECMA335:").And.Contain("SHA256=");
    }

    [Theory]
    [InlineData(null, "SourceUnavailable")]
    [InlineData("", "SourceUnavailable")]
    [InlineData("<html>No raw directive</html>", "PageDirectiveMissing")]
    [InlineData("<%@ Page Language='C#' %>", "InheritsMissing")]
    [InlineData("<%@ Page Inherits='Unknown.Type' %>", "UnresolvedIdentity")]
    [InlineData("<%@ Page Inherits='A' Inherits='B' %>", "AmbiguousPageAttribute")]
    [InlineData("<%@ Page Inherits='A' %><%@ Page Inherits='B' %>", "AmbiguousPageDirective")]
    [InlineData("<%@ Page Inherits='A' Broken %>", "MalformedPageDirective")]
    [InlineData("<%@ Page Inherits='A' CodeFile='page.cs' %>", "DynamicCompilationUnsupported")]
    public void Missing_ambiguous_and_unresolved_source_remains_unknown(string source, string reason)
    {
        var result = PublishingLayoutTypeEvidence.Inspect(source, new());
        result.Decision.Should().Be("Unknown");
        result.ResolutionStatus.Should().Be("Unknown");
        result.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData("Microsoft.SharePoint.Publishing.PublishingLayoutPage")]
    [InlineData("Microsoft.SharePoint.Publishing.PublishingLayoutPage, Microsoft.SharePoint.Publishing")]
    [InlineData("Microsoft.SharePoint.Publishing.PublishingLayoutPage, Other, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c")]
    [InlineData("Microsoft.SharePoint.Publishing.PublishingLayoutPage, Microsoft.SharePoint.Publishing, Version=16.0.0.0, Culture=neutral, PublicKeyToken=null")]
    [InlineData("Microsoft.SharePoint.Publishing.PublishingLayoutPageExtra, Microsoft.SharePoint.Publishing, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c")]
    public void Names_and_partial_or_wrong_assembly_identities_are_not_anchors(string identity)
    {
        PublishingLayoutTypeEvidence.Inspect(Source(identity), new()).Decision.Should().Be("Unknown");
    }

    [Fact]
    public void Comments_markup_and_handler_delegation_cannot_supply_the_files_type()
    {
        var source = "<%--" + Source(Root) + "--%>" + Source(PublishingLayoutTypeCatalog.ObjectIdentity) +
            "<script runat='server'>HttpContext.Current.Handler = new PublishingLayoutPage();</script>";
        PublishingLayoutTypeEvidence.Inspect(source, new()).Decision.Should().Be("NonMember");
        PublishingLayoutTypeEvidence.Inspect("<html>" + Source(Root) + "</html>", new()).Decision.Should().Be("Unknown");
        PublishingLayoutTypeEvidence.Inspect(Source(Root.Replace("PublishingLayoutPage", "Publishing<%--text--%>LayoutPage")), new())
            .Decision.Should().Be("Unknown", "comments inside an attribute must not manufacture a known identity");
    }

    [Fact]
    public void Conflicting_assembly_metadata_is_not_resolved_by_file_order()
    {
        using var first = new MetadataFixture();
        using var second = new MetadataFixture(directBase: PublishingLayoutTypeCatalog.ObjectIdentity);
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { first.Path, second.Path });
        var result = PublishingLayoutTypeEvidence.Inspect(Source(Identity("Direct")), catalog);
        result.Decision.Should().Be("Unknown");
        result.Reason.Should().Be("ConflictingAncestry");
        result.Ancestry.Should().HaveCount(2);
    }

    [Fact]
    public void Missing_assembly_is_recorded_without_inventing_a_chain()
    {
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".dll") });
        catalog.Errors.Should().ContainSingle();
        var result = PublishingLayoutTypeEvidence.Inspect(Source(Identity("Direct")), catalog);
        result.Decision.Should().Be("Unknown");
        result.CatalogErrors.Should().Equal(catalog.Errors);
    }

    [Theory]
    [InlineData(null, "/outside/file.aspx")]
    [InlineData("0x01010007FF3E057FA8AB4AA42FCB67B453FFC1", "/_catalogs/masterpage/file.aspx")]
    [InlineData("0x01010007ff3e057fa8ab4aa42fcb67b453ffc1001234", "/Pages/file.aspx")]
    public async Task Purpose_content_type_descendants_and_location_are_not_type_evidence(string contentType, string url)
    {
        using var fixture = new MetadataFixture();
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { fixture.Path });
        foreach (var (type, decision) in new[] { ("Direct", "Member"), ("Outside", "NonMember"), ("Missing", "Unknown") })
        {
            var row = new ClassicPageDiscovery { RowType = "Page", ContentTypeId = contentType, Url = url };
            AspxAssetPurpose.Apply(row, contentType);
            var purpose = row.AssetPurpose;
            await PublishingLayoutTypeEvidence.AcquireAsync(row, (_, _) => Task.FromResult(Source(Identity(type))), catalog, default);
            row.PublishingLayoutFamily.Should().Be(decision);
            row.AssetPurpose.Should().Be(purpose, "this foundation does not change purpose or routing predicates");
            row.PageTypeEvidenceOrigin.Should().Be("DeclaredSource");
            PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().Be(decision == "Member");
        }
    }

    // Minimal real ECMA-335 images exercise the same reader used for production inputs.
    // There are no method bodies and these images are never loaded as executable assemblies.
    internal sealed class MetadataFixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "layout-types-" + Guid.NewGuid() + ".dll");

        internal MetadataFixture(string directBase = Root)
        {
            var metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("Synthetic.Layouts.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
            metadata.AddAssembly(metadata.GetOrAddString("Synthetic.Layouts"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var direct = Add("Direct", Reference(directBase));
            Add("Indirect", direct);
            Add("Outside", Reference(PublishingLayoutTypeCatalog.ObjectIdentity));
            Add("PublishingLayoutPageLookalike", Reference(PublishingLayoutTypeCatalog.ObjectIdentity));
            Add("Incomplete", Reference("Absent.Base, Absent, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
            var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
                new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
            var image = new BlobBuilder();
            pe.Serialize(image);
            File.WriteAllBytes(Path, image.ToArray());

            TypeDefinitionHandle Add(string name, EntityHandle parent) => metadata.AddTypeDefinition(TypeAttributes.Public,
                metadata.GetOrAddString("Synthetic"), metadata.GetOrAddString(name), parent,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            TypeReferenceHandle Reference(string identity)
            {
                int comma = identity.IndexOf(',');
                var type = identity[..comma];
                var assembly = new AssemblyName(identity[(comma + 1)..].Trim());
                var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(assembly.Name), assembly.Version,
                    default, metadata.GetOrAddBlob(assembly.GetPublicKeyToken()), 0, default);
                int dot = type.LastIndexOf('.');
                return metadata.AddTypeReference(reference, metadata.GetOrAddString(type[..dot]), metadata.GetOrAddString(type[(dot + 1)..]));
            }
        }

        public void Dispose() => File.Delete(Path);
    }
}
