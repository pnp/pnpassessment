using FluentAssertions;
using PnP.Scanning.Core.Services;
using Xunit;

namespace PnP.Scanning.Core.Tests.Services;

/// <summary>Checks which coverage rows the report warning counts as locations with missing files.</summary>
public sealed class IncompleteListingRulesTests
{
    private const string Site = "https://contoso.sharepoint.com/sites/a";
    private const string Web = "/";

    private const string ExpectedRefusal =
        "{\"surfaceId\":\"forms:f\",\"applicabilityRuleId\":\"sharepoint-user-information-list-forms-http-400\"}";

    private const string NoRule = "{\"surfaceId\":\"files:a\",\"applicabilityRuleId\":null}";

    /// <summary>Builds one request row below a scope.</summary>
    private static IncompleteListingRules.OpenRow Request(
        string key,
        string parent,
        string method,
        string evidence = NoRule) =>
        new(Site, Web, "surface:" + key, "scope:" + parent, "Surface", method, evidence);

    /// <summary>Builds one scope or gap row.</summary>
    private static IncompleteListingRules.OpenRow Scope(string key, string type = "Folder") =>
        new(Site, Web, key, "scope:parent-of-" + key, type, "WebRootFiles", null);

    /// <summary>Names the scopes whose second file listing finished.</summary>
    private static HashSet<(string SiteUrl, string WebUrl, string ParentScopeKey)> Completed(params string[] scopes) =>
        scopes.Select(scope => (Site, Web, "scope:" + scope)).ToHashSet();

    /// <summary>Checks that a failed first file listing counts only while the second listing did not finish.</summary>
    [Theory]
    [InlineData("DocumentLibraryFilesAuthority")]
    [InlineData("ListRootFilesAuthority")]
    [InlineData("PhysicalFormsTreeAuthority")]
    public void FailedFirstFileListingCountsOnlyWithoutACompletedSecondListing(string method)
    {
        IncompleteListingRules.OpenRow[] open = { Request("a", "a", method) };

        IncompleteListingRules.CountLocations(open, Completed("a")).Should().Be(0);
        IncompleteListingRules.CountLocations(open, Completed("b")).Should().Be(1);
        IncompleteListingRules.CountLocations(open, Completed()).Should().Be(1);
    }

    /// <summary>Checks that the second listing covers the file listing of its own web only.</summary>
    [Fact]
    public void SecondListingOfAnotherWebCoversNothing()
    {
        IncompleteListingRules.OpenRow[] open = { Request("a", "a", "DocumentLibraryFilesAuthority") };
        HashSet<(string SiteUrl, string WebUrl, string ParentScopeKey)> anotherWeb =
            new() { (Site, "/sub", "scope:a") };

        IncompleteListingRules.CountLocations(open, anotherWeb).Should().Be(1);
    }

    /// <summary>Checks that other requests of a folder count although its second file listing finished.</summary>
    [Fact]
    public void OtherRequestsOfTheFolderStillCount()
    {
        IncompleteListingRules.OpenRow[] open =
        {
            Request("folders", "a", "RecursiveFolderAuthority"),
            Request("second", "a", IncompleteListingRules.SecondFileListing),
        };

        IncompleteListingRules.CountLocations(open, Completed("a")).Should().Be(2);
    }

    /// <summary>Checks that a refusal a rule expects, and the scope that failed only because of it, stay out.</summary>
    [Fact]
    public void ExpectedRefusalAndItsScopeAreNotCounted()
    {
        IncompleteListingRules.OpenRow[] open =
        {
            Scope("scope:forms"), Request("forms", "forms", "AllListFormsAuthority", ExpectedRefusal),
        };

        IncompleteListingRules.CountLocations(open, Completed()).Should().Be(0);
    }

    /// <summary>Checks that a scope with another failed request counts together with that request.</summary>
    [Fact]
    public void ScopeWithAnotherFailedRequestCounts()
    {
        IncompleteListingRules.OpenRow[] open =
        {
            Scope("scope:forms"), Request("forms", "forms", "AllListFormsAuthority", ExpectedRefusal),
            Request("views", "forms", "AllListViewsAuthority"),
        };

        IncompleteListingRules.CountLocations(open, Completed()).Should().Be(2);
    }

    /// <summary>Checks that scopes and gaps without requests count, whatever their evidence holds.</summary>
    [Fact]
    public void ScopesAndGapsWithoutRequestsCount()
    {
        IncompleteListingRules.OpenRow[] open =
        {
            Scope("scope:denied"), Scope("gap:missing", null),
            new(Site, Web, "scope:own-evidence", null, "Folder", "WebRootFiles", ExpectedRefusal),
        };

        IncompleteListingRules.CountLocations(open, Completed("denied")).Should().Be(3);
    }

    /// <summary>Checks that only readable evidence naming a rule marks a refusal as expected.</summary>
    [Theory]
    [InlineData(ExpectedRefusal, true)]
    [InlineData(NoRule, false)]
    [InlineData("{\"applicabilityRuleId\":\"\"}", false)]
    [InlineData("{\"applicabilityRuleId\":7}", false)]
    [InlineData("[\"applicabilityRuleId\"]", false)]
    [InlineData("{not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyReadableEvidenceNamingARuleMarksARefusalAsExpected(string evidence, bool expected)
    {
        IncompleteListingRules.RefusalExpected(Request("a", "a", "AllListFormsAuthority", evidence))
            .Should()
            .Be(expected);
    }
}
