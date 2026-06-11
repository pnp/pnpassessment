﻿﻿using FluentAssertions;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.WebPartMapping
{
    /// <summary>
    /// T4 — the ported web part mapping module. These tests exercise the pure mapping lookup against
    /// the embedded <c>webpartmapping.xml</c>: no CSOM, no live SharePoint. They guard both the
    /// embedded-resource wiring and the "is mappable" parity definition carried over from the legacy
    /// Modernization Scanner.
    /// </summary>
    public class WebPartMappingManagerTests
    {
        // Assembly-qualified types straight out of the legacy WebParts constants. The exact strings
        // (including version/culture/token) are what the mapping file keys on.
        private const string ContentEditorType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string XsltListViewType = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ScriptEditorType = "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private readonly WebPartMappingManager manager = new();

        [Theory]
        [InlineData(ContentEditorType)]
        [InlineData(XsltListViewType)]
        public void Mapping_KnownWebPart_IsMappable(string webPartType)
        {
            // OOB web parts with a modern equivalent must report as mappable.
            manager.IsMappable(webPartType).Should().BeTrue();
        }

        [Fact]
        public void Mapping_KnownWebPart_IsMappable_IsCaseInsensitive()
        {
            // The legacy lookup is case-insensitive on the full type string.
            manager.IsMappable(ContentEditorType.ToUpperInvariant()).Should().BeTrue();
        }

        [Fact]
        public void Mapping_UnknownWebPart_IsNotMappable()
        {
            manager.IsMappable("Contoso.Custom.WebParts.MyCustomWebPart, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000")
                .Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Mapping_NullOrEmptyType_IsNotMappable(string webPartType)
        {
            manager.IsMappable(webPartType).Should().BeFalse();
            manager.InMappingFile(webPartType).Should().BeFalse();
        }

        [Fact]
        public void Mapping_ScriptEditor_InFileButNotMappable()
        {
            // Parity: ScriptEditor is present in the file but its community mapping is stripped on
            // load, so it counts as "in mapping file" yet not "mappable".
            manager.InMappingFile(ScriptEditorType).Should().BeTrue();
            manager.IsMappable(ScriptEditorType).Should().BeFalse();
        }

        [Fact]
        public void Mapping_WikiEmbedPart_IsMappable()
        {
            // Regression guard against re-introducing the stale 2019 mapping file: WikiEmbedPart was
            // only added as a mappable web part in the PnP.Framework 1.0.2111.0 (Nov 2021) version,
            // which is the newest copy that exists. Wiki pages with an embedded doc/video emit this
            // placeholder, so its mappability directly affects the page's mapping percentage.
            manager.IsMappable("SharePointPnP.Modernization.WikiEmbedPart").Should().BeTrue();
        }

        [Fact]
        public void Mapping_EmbeddedXml_ParsesNonZeroEntries()
        {
            // Guards the embedded-resource wiring: if the resource name or build action is wrong the
            // model deserializes to null/empty and this fails fast.
            manager.Model.Should().NotBeNull();
            manager.Model.WebParts.Should().NotBeNullOrEmpty();
        }
    }
}
