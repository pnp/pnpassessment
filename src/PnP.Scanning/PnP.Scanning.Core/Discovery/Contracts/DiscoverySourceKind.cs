using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

// ListItemDisplayFormPages covers pages that own no file of their own: a list item addressed through a
// shared display form as "<displayForm>?ID=<n>". It is acquired from the list item stream, not from a
// physical file enumeration, so one form file backs N of these pages.
internal enum DiscoverySourceKind { RawListLibraryFiles, WebRootFiles, ListFormBackingFiles, ListViewBackingFiles, WebWelcomePage, ListItemDisplayFormPages }
