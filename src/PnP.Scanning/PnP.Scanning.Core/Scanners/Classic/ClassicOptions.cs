namespace PnP.Scanning.Core.Scanners
{
    internal class ClassicOptions : OptionsBase
    {
        internal bool InfoPath { get; set; }

        internal bool Workflow { get; set; }

        internal bool AzureACS { get; set; }

        internal bool SharePointAddIns { get; set; }

        internal bool Pages { get; set; }

        internal bool Lists { get; set; }

        internal bool Extensibility { get; set; }
    }
}
