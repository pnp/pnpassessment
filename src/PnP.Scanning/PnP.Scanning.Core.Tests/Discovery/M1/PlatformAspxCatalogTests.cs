using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PnP.Scanning.Core.Discovery.M1;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

public class PlatformAspxCatalogTests
{
    private static byte[] Catalog(string entries) => Encoding.UTF8.GetBytes("{\"schemaVersion\":\"platform-aspx-catalog/v1\",\"catalogVersion\":\"public-test-v1\",\"source\":\"synthetic-test\",\"entries\":[" + entries + "]}");
    private const string Member = "{\"id\":\"zero-binding\",\"deploymentIdentity\":\"synthetic-deployment\",\"culture\":\"neutral\",\"path\":\"/_layouts/15/example.aspx\"}";

    [Fact]
    public void ExactInputBytesVersionAndDigestAreFrozen()
    {
        var bytes = Catalog(Member);
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var catalog = PlatformAspxCatalog.Parse(bytes);
        bytes[0] = 0;
        catalog.Digest.Should().Be(expected);
        var restored = PlatformAspxCatalog.FromFrozenPayload(catalog.FrozenPayload);
        restored.Digest.Should().Be(expected);
        restored.Version.Should().Be("public-test-v1");
        restored.Source.Should().Be("synthetic-test");
    }

    [Fact]
    public void UnreferencedMemberRemainsAnInventoryCandidate()
    {
        var entry = PlatformAspxCatalog.Parse(Catalog(Member)).Entries.Should().ContainSingle().Subject;
        entry.AspxKey.Should().StartWith("platform:");
        entry.Hive.Should().Be("hive16");
        entry.DeclaredInherits.Should().BeNull();
        entry.HandlerEvidenceSource.Should().BeNull();
    }

    [Fact]
    public void LayoutHivesRemainDistinctIdentities()
    {
        var old = Member.Replace("/_layouts/15/", "/_layouts/");
        var catalog = PlatformAspxCatalog.Parse(Catalog(old + "," + Member));
        catalog.Entries.Select(e => e.AspxKey).Distinct().Should().HaveCount(2);
        catalog.Entries.Select(e => e.Hive).Should().BeEquivalentTo("hive14", "hive16");
        catalog.ResolvePath("/_layouts/example.aspx").Should().ContainSingle();
        catalog.ResolvePath("/_layouts/15/example.aspx").Should().ContainSingle();
    }

    [Theory]
    [InlineData("/_layouts/16/example.aspx")]
    [InlineData("/_layouts/14/example.aspx")]
    [InlineData("/_layouts/15/../example.aspx")]
    [InlineData("/_layouts/15/example.aspx?id=1")]
    [InlineData("/_layouts/15/example.aspx#part")]
    [InlineData("/_layouts/15/%2fexample.aspx")]
    [InlineData("/_layouts/15/file%3Fparameter.aspx")]
    [InlineData("/_layouts/15/file%23anchor.aspx")]
    [InlineData("https://unit.invalid/_layouts/15/example.aspx")]
    public void InvalidOrInventedNamespaceDoesNotBecomeAPlatformIdentity(string path)
    {
        Action parse = () => PlatformAspxCatalog.Parse(Catalog(Member.Replace("/_layouts/15/example.aspx", path)));
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AliasCannotCollapseDifferentHives()
    {
        var row = Member.TrimEnd('}') + ",\"aliases\":[\"/_layouts/example.aspx\"]}";
        Action parse = () => PlatformAspxCatalog.Parse(Catalog(row));
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SameHiveAliasesResolveOneVersionedMember()
    {
        var row = Member.TrimEnd('}') + ",\"aliases\":[\"/_layouts/15/alias.aspx\"]}";
        var catalog = PlatformAspxCatalog.Parse(Catalog(row));
        catalog.ResolvePath("/_layouts/15/alias.aspx").Single().AspxKey.Should().Be(catalog.Entries.Single().AspxKey);
    }

    [Fact]
    public void DuplicateJsonMetadataIsRejectedInsteadOfTakingLastValue()
    {
        var text = Encoding.UTF8.GetString(Catalog(Member)).Replace("\"catalogVersion\":", "\"CATALOGVERSION\":\"other\",\"catalogVersion\":");
        Action parse = () => PlatformAspxCatalog.Parse(Encoding.UTF8.GetBytes(text));
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DuplicateMemberIdentityIsRejected()
    {
        Action parse = () => PlatformAspxCatalog.Parse(Catalog(Member + "," + Member));
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HandlerReferenceNeedsItsOwnVersionedEvidenceSource()
    {
        var row = Member.TrimEnd('}') + ",\"declaredInherits\":\"System.Web.UI.Page\"}";
        Action parse = () => PlatformAspxCatalog.Parse(Catalog(row));
        parse.Should().Throw<ArgumentException>();
    }
}
