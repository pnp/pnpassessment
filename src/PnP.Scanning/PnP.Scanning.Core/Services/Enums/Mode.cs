namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Defines supported assessment modes, including the Classic Pages collection and analysis pipeline selection.
    /// </summary>
    internal enum Mode
    {
#if DEBUG
        Test,
#endif
        //Syntex,
        Workflow,
        InfoPath,
        Classic,
        AddInsACS,
        Alerts,
        ClassicPage
    }
}
