using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

public enum DiscoveryVerdict { CompleteTenantVerified, CompleteAuthorizedSurface, CompleteDeclaredSubset, Incomplete, Unknown }
