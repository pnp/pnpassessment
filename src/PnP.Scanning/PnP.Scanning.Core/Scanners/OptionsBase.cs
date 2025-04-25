using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal string Mode { get; set; }

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
            else if (request.Mode.Equals(Services.Mode.Classic.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var property in request.Properties)
                {
                    if (property.Property == ClassicComponent.Workflow.ToString())
                    {
                        (options as ClassicOptions).Workflow = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.InfoPath.ToString())
                    {
                        (options as ClassicOptions).InfoPath = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.AzureACS.ToString())
                    {
                        (options as ClassicOptions).AzureACS = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.SharePointAddIns.ToString())
                    {
                        (options as ClassicOptions).SharePointAddIns = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.Pages.ToString())
                    {
                        (options as ClassicOptions).Pages = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.Lists.ToString())
                    {
                        (options as ClassicOptions).Lists = bool.Parse(property.Value);
                    }
                    else if (property.Property == ClassicComponent.Extensibility.ToString())
                    {
                        (options as ClassicOptions).Extensibility = bool.Parse(property.Value);
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
            else if (mode.Equals(Services.Mode.Classic.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new ClassicOptions
                {
                    Mode = mode,
                };
            }
            else if (mode.Equals(Services.Mode.InfoPath.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new InfoPathOptions
                {
                    Mode = mode,
                };
            }
            else if (mode.Equals(Services.Mode.AddInsACS.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new AddInACSOptions
                {
                    Mode = mode,
                };
            }
            else if (mode.Equals(Services.Mode.Alerts.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new AlertsOptions
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
