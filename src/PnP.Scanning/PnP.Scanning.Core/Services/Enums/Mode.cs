namespace PnP.Scanning.Core.Services
{
    internal enum Mode
    {
#if DEBUG
        Test,
#endif
        Syntex,
        Workflow
    }
}
