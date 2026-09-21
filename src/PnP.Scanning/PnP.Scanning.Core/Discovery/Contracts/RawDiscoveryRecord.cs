using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal sealed record RawDiscoveryRecord(
    string SourceObjectId,
    string FileUniqueId,
    string ContainerStableId,
    string FileName,
    string PhysicalLocator,
    bool LocatorIsGuaranteedPhysicalFilePath,
    string PermissionContext,
    IReadOnlyDictionary<string, string> Metadata = null,
    Guid? SiteCollectionId = null,
    Guid? WebId = null,
    Guid? ListId = null,
    Guid? FolderUniqueId = null,
    int? ListItemId = null,
    bool? HomePage = null,
    string ContentTypeId = null,
    string PageType = null,
    bool? LibraryHidden = null,
    string CustomizedPageStatusRaw = null,
    string ObservationMethod = null,
    string SelectionState = null,
    string WelcomePageStatus = null,
    string ScanId = null,
    string SiteUrl = null,
    string WebUrl = null);
