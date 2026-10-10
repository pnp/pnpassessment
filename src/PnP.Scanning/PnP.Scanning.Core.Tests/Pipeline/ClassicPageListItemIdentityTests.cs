using System.Reflection;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using Xunit;
using PnP.Scanning.Core.Pipeline.Collection.Module;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "ClassicPagePipeline")]
public sealed class ClassicPageListItemIdentityTests
{
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

    [Fact]
    public void Conflicting_field_identity_cannot_replace_modeled_identity()
    {
        var values = (TransientDictionary)Activator.CreateInstance(typeof(TransientDictionary), nonPublic: true);
        values.Add("ID", 99);
        Assert.Throws<InvalidDataException>(() => ClassicPageOnlineSource.ItemFields(Item(23, values)));
    }

    private static IListItem Item(int id, TransientDictionary values)
    {
        var item = DispatchProxy.Create<IListItem, ItemProxy>();
        ((ItemProxy)(object)item).ReturnedId = id;
        ((ItemProxy)(object)item).Values = values;
        return item;
    }
    public class ItemProxy : DispatchProxy
    {
        public int ReturnedId;
        public TransientDictionary Values;
        protected override object Invoke(MethodInfo targetMethod, object[] args) => targetMethod.Name switch
        {
            "get_Id" => ReturnedId,
            "get_Values" => Values,
            _ => throw new InvalidOperationException("Unexpected SDK access: " + targetMethod.Name),
        };
    }
}
