# workflows.csv file details

## Summary

This csv file contains information about the 2013 workflows that have been assessed.

## Columns

The following columns are included:

Column|Description
------|-----------
DefinitionId | Workflow definition id
SubscriptionId | Id of the subscription created for this workflow
ListUrl | If the workflow is scoped to a list this contains the url of that list
ListTitle | If the workflow is scoped to a list this contains the title of that list
ListId | If the workflow is scoped to a list this contains the id of that list
ContentTypeId | If the workflow is scoped to a content type this contains the id of that content type
ContentTypeName | If the workflow is scoped to a content type this contains the name of that content type
IsOOBWorkflow | Always false for SharePoint 2013 workflows
Scope | Workflows are either connected to a site, list or content type
RestrictToType | 2013 workflows can be restricted to be used exclusively for lists or sites, which is indicated in tis column
Enabled | Is the workflow enabled
ConsiderUpgradingToFlow | Indicates whether this workflow is a good candidate for Microsoft Flow migration. The used criteria are: it's not an OOB workflow, it has subscriptions and it's enabled
DefinitionName | The name of the workflow definition
DefinitionDescription | Workflow definition description
SubscriptionName | Name of the subscription created for this workflow
HasSubscriptions | Is this workflow being used or is it just defined
ActionCount | Number of actions detected in this workflow (not available when `--workflowanalyze:false` was specified)
UsedActions | List of the unique actions used in this workflow (not available when `--workflowanalyze:false` was specified)
ToFLowMappingPercentage | % indicating how well the detected actions are upgradable to Microsoft Flow (not available when `--workflowanalyze:false` was specified)
UnsupportedActionCount | Number of actions that are not compatible with Microsoft Flow (not available when `--workflowanalyze:false` was specified)
UnsupportedActionsInFlow | List of unique actions which are not compatible with Microsoft Flow (not available when `--workflowanalyze:false` was specified)
UsedTriggers | List of the workflow triggers that trigger this workflow to start (not available when `--workflowanalyze:false` was specified)
LastSubscriptionEdit | When was the workflow subscription last changed
LastDefinitionEdit | When was this workflow definition last changed
TotalInstances | Total instances of this workflow in the last 30 days
StartedInstances | Total started instances of this workflow in the last 30 days
NotStartedInstances | Total not started instances of this workflow in the last 30 days
CancelledInstances | Total cancelled instances of this workflow in the last 30 days
CancellingInstances | Total cancelling instances of this workflow in the last 30 days
SuspendedInstances | Total suspended instances of this workflow in the last 30 days
TerminatedInstances | Total terminated instances of this workflow in the last 30 days
CompletedInstances | Total completed instances of this workflow in the last 30 days
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
