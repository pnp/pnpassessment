using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

public enum DiscoveryTerminalOutcome { Pending, Complete, Empty, PolicyExcluded, Denied, Failed, Truncated, Cancelled, Unknown }
