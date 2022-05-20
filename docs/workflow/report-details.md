# Details

SharePoint 2013 workflows are typically scoped to a list, although that workflows can also be scoped to a site. A workflow consists out of a definition and possible one or more subscriptions of that definition. Workflows having a subscription can be run. This page will help you identify the available workflows allowing you to filter them by site collection or web and providing detailed information that will help you assess the need to upgrade this workflow to Power Automate.

You can use the presented filter to scope down to the workflows you're interested in. In table these columns are presented:

Column name | Description
------------|------------
Web URL | The fully qualified URL of the web hosting the workflow
Workflow definition name | Definition name of the workflow
Workflow definition ID | ID of the workflow definition
Scope | Workflow scope, will be List or Site
Subscriptions | Does this workflow have a subscription?
Subscription name | Name of the workflow subscription
Runs | Lists the number workflow instances that have been started, completed, cancelled or terminated in the last 30 days
Replace | Should this workflow be considered for replacement by Power Automate
List URL | Server relative URL of the list this workflow is connected to
ContentType | If a workflow is connected to a specific content type on a list then this column will contain the content type name
Upgradability | Percentage indication of how good the actions used in this workflow can be upgraded to Power Automate. "Add a Comment", "Log to History List" and "Set Workflow Status" workflow actions are excluded from the calculation as they're not relevant for the Power Automate platform (not available when `--workflowanalyze:false` was specified)
Unsupported actions count | The number of actions which cannot fully be migrated to Power Automate (not available when `--workflowanalyze:false` was specified)
Unsupported actions on Power Automate | The discovered actions which cannot fully be migrated to Power Automate (not available when `--workflowanalyze:false` was specified)
Actions used | The discovered list of workflow actions (not available when `--workflowanalyze:false` was specified)

## Sample page

![workflow details](../images/workflowdetails.png)
