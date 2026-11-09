#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using System.Text.Json;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal static class ClassicComponentStorage
{
    internal static async Task ResetAsync(Guid assessment, ClassicPageWebSource web, CancellationToken token)
    {
        using var db = new ScanContext(assessment);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await DeleteAsync<ClassicList>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicInfoPath>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<Workflow>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicExtensibility>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicUserCustomAction>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicWebSummary>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await transaction.CommitAsync(token);
    }

    private static Task<int> DeleteAsync<T>(ScanContext db, Guid id, string site, string web, CancellationToken token)
        where T : BaseScanResult => db.Set<T>().Where(x => x.ScanId == id && x.SiteUrl == site && x.WebUrl == web).ExecuteDeleteAsync(token);

    internal static async Task<ClassicComponentSource> CaptureAsync(Guid assessment, ClassicPageWebSource web, CancellationToken token)
    {
        using var db = new ScanContext(assessment);
        var rows = new Dictionary<string, JsonElement[]>();
        async Task Read<T>() where T : BaseScanResult
        {
            var values = await db.Set<T>().AsNoTracking()
                .Where(x => x.ScanId == assessment && x.SiteUrl == web.SiteUrl && x.WebUrl == web.WebUrl).ToListAsync(token);
            rows.Add(typeof(T).Name, values.Select(value => JsonSerializer.SerializeToElement(value)).ToArray());
        }
        await Read<ClassicList>();
        await Read<ClassicInfoPath>();
        await Read<Workflow>();
        await Read<ClassicExtensibility>();
        await Read<ClassicUserCustomAction>();
        await Read<ClassicWebSummary>();
        return new(web.SiteUrl, web.WebUrl, SourceReadState.Complete, rows);
    }
}
