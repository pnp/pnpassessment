using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal string Mode { get; private set; }

        internal static OptionsBase FromScannerInput(StartRequest request)
        {
            var options = NewOptions(request.Mode);

            // PER SCAN COMPONENT: configure scan component option handling here
            if (request.Mode.Equals(Services.Mode.Syntex.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var property in request.Properties)
                {
                    if (property.Property == Constants.StartSyntexFull)
                    {
                        (options as SyntexOptions).DeepScan = bool.Parse(property.Value);
                    }
                }
            }
            else if (request.Mode.Equals(Services.Mode.Workflow.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var property in request.Properties)
                {
                    if (property.Property == Constants.StartWorkflowAnalyze)
                    {
                        (options as WorkflowOptions).Analyze = bool.Parse(property.Value);
                    }
                }
            }
#if DEBUG
            // Assign other inputs
            else if (request.Mode.Equals(Services.Mode.Test.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                (options as TestOptions).TestNumberOfSites = int.Parse(request.Properties.First().Value);
            }
#endif

            return options;
        }

        private static OptionsBase NewOptions(string mode)
        {
            // PER SCAN COMPONENT: configure scan component option handling here
            if (mode.Equals(Services.Mode.Syntex.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new SyntexOptions
                {
                    Mode = mode,
                };
            }
            else if (mode.Equals(Services.Mode.Workflow.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowOptions
                {
                    Mode = mode,
                };
            }
#if DEBUG
            else if (mode.Equals(Services.Mode.Test.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new TestOptions
                {
                    Mode = mode,
                };
            }
#endif

            throw new Exception("Unsupported assessment mode passed in");
        }
    }
}
