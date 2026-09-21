using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal enum DiscoverySourceKind { RawListLibraryFiles, WebRootFiles, ListFormBackingFiles, ListViewBackingFiles, WebWelcomePage }
