﻿﻿using FluentAssertions;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline.Analysis.Page.WebPartMapping
{
    /// <summary>
    /// Verifies the embedded mapping catalog, built-in type recognition and mappable web part classifications.
    /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
    /// </summary>
    public class WebPartMappingManagerTests
    {
        // Assembly-qualified types straight out of the legacy WebParts constants. The exact strings
        // (including version/culture/token) are what the mapping file keys on.
        private const string ContentEditorType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string XsltListViewType = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ScriptEditorType = "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private readonly WebPartMappingManager manager = new();

        /// <summary>
        /// Verifies that a supported built-in web part has a usable mapping.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Theory]
        [InlineData(ContentEditorType)]
        [InlineData(XsltListViewType)]
        public void Mapping_KnownWebPart_IsMappable(string webPartType)
        {
            // OOB web parts with a modern equivalent must report as mappable.
            manager.IsMappable(webPartType).Should().BeTrue();
        }

        /// <summary>
        /// Verifies case-insensitive lookup of known mappable web part types.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Fact]
        public void Mapping_KnownWebPart_IsMappable_IsCaseInsensitive()
        {
            // The legacy lookup is case-insensitive on the full type string.
            manager.IsMappable(ContentEditorType.ToUpperInvariant()).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an unknown web part type has no mapping.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Fact]
        public void Mapping_UnknownWebPart_IsNotMappable()
        {
            manager.IsMappable("Contoso.Custom.WebParts.MyCustomWebPart, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000")
                .Should().BeFalse();
        }

        /// <summary>
        /// Verifies that missing type information cannot imply a supported mapping.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Mapping_NullOrEmptyType_IsNotMappable(string webPartType)
        {
            manager.IsMappable(webPartType).Should().BeFalse();
            manager.InMappingFile(webPartType).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that catalog recognition and actual mappability remain distinct for Script Editor.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Fact]
        public void Mapping_ScriptEditor_InFileButNotMappable()
        {
            // Parity: ScriptEditor is present in the file but its community mapping is stripped on
            // load, so it counts as "in mapping file" yet not "mappable".
            manager.InMappingFile(ScriptEditorType).Should().BeTrue();
            manager.IsMappable(ScriptEditorType).Should().BeFalse();
        }

        /// <summary>
        /// Verifies the mapping for a wiki embedded-content inventory part.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
        [Fact]
        public void Mapping_WikiEmbedPart_IsMappable()
        {
            // Regression guard against re-introducing the stale 2019 mapping file: WikiEmbedPart was
            // only added as a mappable web part in the PnP.Framework 1.0.2111.0 (Nov 2021) version,
            // which is the newest copy that exists. Wiki pages with an embedded doc/video emit this
            // placeholder, so its mappability directly affects the page's mapping percentage.
            manager.IsMappable("SharePointPnP.Modernization.WikiEmbedPart").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that the embedded mapping XML loads a nonempty catalog.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/WebPartMapping/WebPartMappingManagerTests.cs</c> to verify the unchanged embedded mapping catalog in the analysis module.</para>
        /// </summary>
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
