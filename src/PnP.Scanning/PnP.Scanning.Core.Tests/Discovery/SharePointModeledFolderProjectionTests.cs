using FluentAssertions;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SharePointModeledFolderProjectionTests
{
    [Fact]
    public void Modeled_folder_children_are_projected_without_the_query_provider()
    {
        var collection = new FakeFolderCollection(new[]
        {
            Folder(Guid.Parse("11111111-1111-1111-1111-111111111111"), "SitePages", "/sites/a/SitePages"),
            Folder(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Lists", "/sites/a/Lists"),
        });

        var folders = PnPContextSharePointAspxRestClient.MapFolders(collection);

        folders.Should().HaveCount(2);
        folders[0].UniqueId.Should().Be("11111111-1111-1111-1111-111111111111");
        folders[0].Name.Should().Be("SitePages");
        folders[0].ServerRelativeUrl.Should().Be("/sites/a/SitePages");
        collection.ProviderRequested.Should().BeFalse(
            "projecting an IQueryable model collection is rejected by the PnP Core query provider");
    }

    [Fact]
    public void Modeled_folder_files_are_projected_without_the_query_provider()
    {
        var collection = new FakeFileCollection(new[]
        {
            File(Guid.Parse("33333333-3333-3333-3333-333333333333"), "default.aspx",
                "/sites/a/default.aspx", CustomizedPageStatus.Uncustomized),
            File(Guid.Parse("44444444-4444-4444-4444-444444444444"), "home.aspx",
                "/sites/a/home.aspx", CustomizedPageStatus.Customized),
        });

        var files = PnPContextSharePointAspxRestClient.MapFiles(collection);

        files.Should().HaveCount(2);
        files[0].UniqueId.Should().Be("33333333-3333-3333-3333-333333333333");
        files[0].Name.Should().Be("default.aspx");
        files[0].ServerRelativeUrl.Should().Be("/sites/a/default.aspx");
        files[0].CustomizedPageStatus.Should().Be("Uncustomized");
        files[1].CustomizedPageStatus.Should().Be("Customized");
        collection.ProviderRequested.Should().BeFalse(
            "projecting an IQueryable model collection is rejected by the PnP Core query provider");
    }

    private static IFolder Folder(Guid uniqueId, string name, string serverRelativeUrl) =>
        ModelProxy<IFolder>.Create(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["UniqueId"] = uniqueId,
            ["Name"] = name,
            ["ServerRelativeUrl"] = serverRelativeUrl,
        });

    private static IFile File(Guid uniqueId, string name, string serverRelativeUrl,
        CustomizedPageStatus status) =>
        ModelProxy<IFile>.Create(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["UniqueId"] = uniqueId,
            ["Name"] = name,
            ["ServerRelativeUrl"] = serverRelativeUrl,
            ["CustomizedPageStatus"] = status,
        });

    /// <summary>
    /// Minimal stand-in for a PnP Core model, supplying only the properties the modeled web-root
    /// walk reads. The real interfaces carry too many members to implement by hand.
    /// </summary>
    public class ModelProxy<TModel> : DispatchProxy
    {
        private Dictionary<string, object> values;

        internal static TModel Create(Dictionary<string, object> values)
        {
            var proxy = Create<TModel, ModelProxy<TModel>>();
            ((ModelProxy<TModel>)(object)proxy).values = values;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            // The projection asks whether an optional property was expanded before reading it, so
            // the proxy answers from whichever values this instance was given.
            if (targetMethod.Name == nameof(IDataModel<TModel>.IsPropertyAvailable))
                return MemberName(args[0] as LambdaExpression) is string member && values.ContainsKey(member);

            return targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) &&
                values.TryGetValue(targetMethod.Name[4..], out var value)
                    ? value
                    : throw new NotSupportedException(targetMethod.Name);
        }

        private static string MemberName(LambdaExpression expression) =>
            (expression?.Body is UnaryExpression unary ? unary.Operand : expression?.Body)
                is MemberExpression member ? member.Member.Name : null;
    }

    /// <summary>
    /// Reproduces how PnP Core exposes an expanded child collection: the items are already
    /// materialised, but the collection is also an <see cref="IQueryable{T}"/> whose provider
    /// refuses projections exactly like <c>BaseQueryProvider.CreateQuery&lt;TResult&gt;</c>.
    /// </summary>
    public abstract class FakeModelCollection<TModel> : IQueryable<TModel>, IDataModelCollection<TModel>,
        IDataModelCollectionLoad<TModel>, IDataModelCollectionDeleteByGuidId, ISupportModules<TModel>
    {
        private readonly IReadOnlyList<TModel> items;

        protected FakeModelCollection(IReadOnlyList<TModel> items) => this.items = items;

        internal bool ProviderRequested { get; private set; }

        public IQueryProvider Provider
        {
            get
            {
                ProviderRequested = true;
                return new RejectingQueryProvider<TModel>();
            }
        }

        public Type ElementType => typeof(TModel);

        public Expression Expression => Expression.Constant(this);

        public bool Requested { get; set; } = true;

        public int Length => items.Count;

        public IEnumerable RequestedItems => items;

        public IDataModelParent Parent { get; set; }

        public PnPContext PnPContext { get; set; }

        public IEnumerator<TModel> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Clear() => throw new NotSupportedException();

        public Task LoadAsync(params Expression<Func<TModel, object>>[] selectors) =>
            throw new NotSupportedException();

        public Task<IBatchResult> LoadBatchAsync(Batch batch,
            params Expression<Func<TModel, object>>[] selectors) => throw new NotSupportedException();

        public void DeleteById(Guid id) => throw new NotSupportedException();

        public Task DeleteByIdAsync(Guid id) => throw new NotSupportedException();

        public void DeleteByIdBatch(Guid id) => throw new NotSupportedException();

        public Task DeleteByIdBatchAsync(Guid id) => throw new NotSupportedException();

        public void DeleteByIdBatch(Batch batch, Guid id) => throw new NotSupportedException();

        public Task DeleteByIdBatchAsync(Batch batch, Guid id) => throw new NotSupportedException();
    }

    private sealed class RejectingQueryProvider<TModel> : IQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => new RejectingQueryable<TModel>(this);

        // Mirrors PnP.Core BaseQueryProvider.CreateQuery<TResult>: the guard only compares the
        // expression type to TResult, so a Select() projection reaches the cast and fails there.
        public IQueryable<TResult> CreateQuery<TResult>(Expression expression) =>
            (IQueryable<TResult>)CreateQuery(expression);

        public object Execute(Expression expression) => throw new NotSupportedException();

        public TResult Execute<TResult>(Expression expression) => throw new NotSupportedException();
    }

    private sealed class RejectingQueryable<TModel> : IQueryable<TModel>
    {
        public RejectingQueryable(IQueryProvider provider) => Provider = provider;

        public Type ElementType => typeof(TModel);

        public Expression Expression => Expression.Constant(this);

        public IQueryProvider Provider { get; }

        public IEnumerator<TModel> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class FakeFolderCollection : FakeModelCollection<IFolder>, IFolderCollection
    {
        public FakeFolderCollection(IReadOnlyList<IFolder> items) : base(items) { }

        public IAsyncEnumerator<IFolder> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IFolder Add(string name) => throw new NotSupportedException();

        public Task<IFolder> AddAsync(string name) => throw new NotSupportedException();

        public IFolder AddBatch(string name) => throw new NotSupportedException();

        public Task<IFolder> AddBatchAsync(string name) => throw new NotSupportedException();

        public IFolder AddBatch(Batch batch, string name) => throw new NotSupportedException();

        public Task<IFolder> AddBatchAsync(Batch batch, string name) => throw new NotSupportedException();
    }

    public sealed class FakeFileCollection : FakeModelCollection<IFile>, IFileCollection
    {
        public FakeFileCollection(IReadOnlyList<IFile> items) : base(items) { }

        public IAsyncEnumerator<IFile> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IFile Add(string name, Stream content, bool overwrite = false) =>
            throw new NotSupportedException();

        public Task<IFile> AddAsync(string name, Stream content, bool overwrite = false) =>
            throw new NotSupportedException();

        public IFile AddTemplateFile(string serverRelativeUrl, TemplateFileType templateFileType) =>
            throw new NotSupportedException();

        public Task<IFile> AddTemplateFileAsync(string serverRelativeUrl, TemplateFileType templateFileType) =>
            throw new NotSupportedException();
    }
}
