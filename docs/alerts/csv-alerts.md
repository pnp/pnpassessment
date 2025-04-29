# alerts.csv file details

## Summary

This csv file contains information about the SharePoint Alerts usage that has been assessed.

## Columns

The following columns are included:

Column|Description
------|-----------
AlertId | Unique identifier of the alert.
AlertTitle | Title of the alert.
AlertType | Type of the alert (e.g., List, Item, etc.).
Status | Status of the alert (e.g., On, Off).
DeliveryChannel | Delivery channels for the alert (e.g., Email, SMS).
EventTypeAlertFrequency | Frequency of the alert (e.g., Immediate, Daily, Weekly).
CAMLQuery | CAML filter applied for the alert.
Filter | Additional filter criteria for the alert.
UserLoginName | Login name of the user receiving the alert.
UserName | Display name of the user receiving the alert.
UserPrincipalType | Type of the user principal (e.g., User, Group).
UserEmail | Email address of the user receiving the alert.
ListUrl | Server-relative URL of the list the alert is related to.
ListId | Unique identifier of the list the alert is related to.
ListTitle | Title of the list the alert is related to.
AlertTemplateName | Template name of the alert.
AlertTime | (Not yet used) Time to send out the alert (for alerts with frequency set to Weekly). 
ListItemId | (Not yet used) Id of the item the alert is related to.
ScanId | Id of the assessment.
SiteUrl | Fully qualified site collection URL.
WebUrl | Relative URL of this web.
