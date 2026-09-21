namespace PnP.Scanning.Core.Discovery;

internal interface IAspxReferenceAcquisitionProvider
{
    AspxReferenceCollector ReferenceCollector { get; }
}
