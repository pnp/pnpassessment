using System.Reflection;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "ClassicPagePipeline")]
public sealed class ClassicPageModeledCollectionTests
{
    [Fact]
    public void Folder_capture_reads_requested_SDK_members_without_remote_projection()
    {
        var folders = Collection<IFolderCollection>("FolderCollection");
        var folder = Model<IFolder>("Folder");
        var id = Guid.NewGuid();
        Set(folder, "UniqueId", id);
        Set(folder, "Name", "Pages");
        Set(folder, "ServerRelativeUrl", "/sites/a/Pages");
        Add(folders, folder);

        // The actual SDK query provider rejects Select to another element type.
        Assert.Throws<InvalidCastException>(() => folders.Select(x => x.Name).ToArray());
        var captured = Assert.Single(PnPContextSharePointAspxRestClient.FolderFacts(folders));
        Assert.Equal(id.ToString("D"), captured.UniqueId);
        Assert.Equal("Pages", captured.Name);
        Assert.Equal("/sites/a/Pages", captured.ServerRelativeUrl);
    }

    [Fact]
    public void File_capture_preserves_requested_identity_and_customization_without_authentication()
    {
        var files = Collection<IFileCollection>("FileCollection");
        var file = Model<IFile>("File");
        var id = Guid.NewGuid();
        Set(file, "UniqueId", id);
        Set(file, "Name", "default.aspx");
        Set(file, "ServerRelativeUrl", "/sites/a/default.aspx");
        Set(file, "CustomizedPageStatus", CustomizedPageStatus.Customized);
        Add(files, file);

        Assert.Throws<InvalidCastException>(() => files.Select(x => x.Name).ToArray());
        var captured = Assert.Single(PnPContextSharePointAspxRestClient.FileFacts(files));
        Assert.Equal(id.ToString("D"), captured.UniqueId);
        Assert.Equal("default.aspx", captured.Name);
        Assert.Equal("/sites/a/default.aspx", captured.ServerRelativeUrl);
        Assert.Equal("Customized", captured.CustomizedPageStatus);
        Assert.Empty(PnPContextSharePointAspxRestClient.FileFacts(Collection<IFileCollection>("FileCollection")));
    }

    // Use the pinned SDK's real models and query provider with no PnPContext/authentication.
    private static T Model<T>(string name) => (T)Activator.CreateInstance(
        typeof(PnPContext).Assembly.GetType("PnP.Core.Model.SharePoint." + name), nonPublic: true);

    private static T Collection<T>(string name) => (T)Activator.CreateInstance(
        typeof(PnPContext).Assembly.GetType("PnP.Core.Model.SharePoint." + name),
        BindingFlags.Public | BindingFlags.Instance, binder: null,
        args: new object[] { null, null, null }, culture: null);

    private static void Set(object model, string property, object value) =>
        model.GetType().GetProperty(property).SetValue(model, value);

    private static void Add<T>(object collection, T item) =>
        collection.GetType().GetMethod("Add", new[] { typeof(T) }).Invoke(collection, new object[] { item });
}
