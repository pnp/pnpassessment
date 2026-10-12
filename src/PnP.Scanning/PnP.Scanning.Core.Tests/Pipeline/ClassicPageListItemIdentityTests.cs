using System.Reflection;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using Xunit;
using PnP.Scanning.Core.Pipeline.Collection.Module;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies that modeled list item IDs are captured and conflicting field IDs are rejected.
/// </summary>
[Trait("Category", "ClassicPagePipeline")]
public sealed class ClassicPageListItemIdentityTests
{
    /// <summary>
    /// Verifies that modeled item identity is persisted even when the SDK field bag omits ID.
    /// </summary>
    [Fact]
    public void List_stream_identity_survives_when_SDK_values_omit_ID()
    {
        // PnP Core 1.18 ListDataAsStreamHandler sets item.Id and skips ID in Values.
        // Reproduce that actual SDK shape, rather than the old fixture containing ID twice.
        var values = (TransientDictionary)Activator.CreateInstance(typeof(TransientDictionary), nonPublic: true);
        values.Add("FileRef", "/sites/ClassicPubDemo/SitePages/Home.aspx");
        values.Add("WikiField", "<p>Original wiki content</p>");
        var item = Item(23, values);
        var captured = ClassicPageOnlineSource.ItemFields(item);
        Assert.Equal(23, captured["ID"].ToValue());
        Assert.Equal("/sites/ClassicPubDemo/SitePages/Home.aspx", captured["FileRef"].Text);
        Assert.Equal("<p>Original wiki content</p>", captured["WikiField"].Text);
        Assert.False(values.ContainsKey("ID"));
    }

    /// <summary>
    /// Verifies rejection of an ID field that conflicts with the modeled item identity.
    /// </summary>
    [Fact]
    public void Conflicting_field_identity_cannot_replace_modeled_identity()
    {
        var values = (TransientDictionary)Activator.CreateInstance(typeof(TransientDictionary), nonPublic: true);
        values.Add("ID", 99);
        Assert.Throws<InvalidDataException>(() => ClassicPageOnlineSource.ItemFields(Item(23, values)));
    }

    /// <summary>
    /// Creates the modeled list item proxy with the requested identity and field values.
    /// </summary>
    private static IListItem Item(int id, TransientDictionary values)
    {
        var item = DispatchProxy.Create<IListItem, ItemProxy>();
        ((ItemProxy)(object)item).ReturnedId = id;
        ((ItemProxy)(object)item).Values = values;
        return item;
    }
    /// <summary>
    /// Implements only the SDK item identity and values required by source field capture.
    /// </summary>
    public class ItemProxy : DispatchProxy
    {
        public int ReturnedId;
        public TransientDictionary Values;
        /// <summary>
        /// Returns the configured modeled ID or value bag and rejects unsupported SDK calls.
        /// </summary>
        protected override object Invoke(MethodInfo targetMethod, object[] args) => targetMethod.Name switch
        {
            "get_Id" => ReturnedId,
            "get_Values" => Values,
            _ => throw new InvalidOperationException("Unexpected SDK access: " + targetMethod.Name),
        };
    }
}
