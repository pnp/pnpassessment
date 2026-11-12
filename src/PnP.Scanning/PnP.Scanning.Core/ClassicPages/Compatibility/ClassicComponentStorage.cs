using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
#nullable enable
using PnP.Scanning.Core.Storage;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using System.Text.Json;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Resets incomplete component writes and captures existing component rows as immutable evidence.
/// </summary>
internal static class ClassicComponentStorage
{
    /// <summary>
    /// Atomically removes incomplete retained component writes for one Web before retry.
    /// </summary>
    internal static async Task ResetAsync(Guid assessment, WebFacts web, CancellationToken token)
    {
        using ScanContext? db = new ScanContext(assessment);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction =
            await db.Database.BeginTransactionAsync(token);
        await DeleteAsync<ClassicList>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicInfoPath>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<Workflow>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicExtensibility>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicUserCustomAction>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await DeleteAsync<ClassicWebSummary>(db, assessment, web.SiteUrl, web.WebUrl, token);
        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// Deletes only rows belonging to the selected assessment and Web scope.
    /// </summary>
    private static Task<int> DeleteAsync<T>(ScanContext db, Guid id, string site, string web, CancellationToken token)
        where T : BaseScanResult =>
        db.Set<T>().Where(x => x.ScanId == id && x.SiteUrl == site && x.WebUrl == web).ExecuteDeleteAsync(token);

    /// <summary>
    /// Detaches supported existing component rows as owned immutable collection evidence.
    /// </summary>
    internal static async Task<ClassicComponentSource> CaptureAsync(
        Guid assessment,
        WebFacts web,
        CancellationToken token)
    {
        using ScanContext? db = new ScanContext(assessment);
        Dictionary<string, JsonElement[]>? rows = new Dictionary<string, JsonElement[]>();

        async Task Read<T>() where T : BaseScanResult
        {
            List<T>? values = await db.Set<T>()
                .AsNoTracking()
                .Where(x => x.ScanId == assessment && x.SiteUrl == web.SiteUrl && x.WebUrl == web.WebUrl)
                .ToListAsync(token);
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