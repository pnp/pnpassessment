# Azure ACS principal details

Using this report page you'll be able to see all details of a selected Azure ACS Principal, Azure ACS principals are a legacy auth concept used to grant applications (e.g. Provider Hosted SharePoint Add-Ins) access to SharePoint.

> [!Important]
> Use the top filters for `Title`, `App Id`, `Expired` and `App-Only` to filter down to **one** selected Azure ACS Principal. Use the `Selected ACS Principals` value to confirm the filtering.

Once filtered you'll find three sections with information.

## ACS Principal details

Provider Hosted SharePoint Add-Ins use an Azure ACS principal (a.k.a application) to enable the Add-In provider code to call back into the needed SharePoint APIs (e.g. the Add-In will add a list item). The details of the used Azure ACS principal are shown in this section, key fields are:

- **Expiration date**: By default the Azure ACS principals are generated with a two year valid secret, this field shows the expiration date of the most recent secret. If the secret has expired it means the SharePoint Add-In is not able to read/update data in SharePoint Online and is in a broken state
- **Use App-Only**: Can this ACS principal be used in scenarios that require application permissions?

When a principal was granted access to specific sites you can see the sites in the shown table, if the table lists `<tenant>` then the Azure ACS principal was also granted a tenant scoped permission.

More details about the returned field and their possible values can be found in the documentation of the generated [Azure ACS principals csv file](csv-classicacsprincipals.md).

## Site collection scoped permissions

Azure ACS principals are configured with permission scopes detailing what content they can access and what right they have for that content. If an Azure ACS principal is granted access to one or more specific site collections, specific webs or specific lists then you'll be able to see the respective site collection, web and list id's with their associated right in the shown table.

More details about the returned field and their possible values can be found in the documentation of the generated [Azure ACS site collection scoped permissions csv file](csv-classicacsprincipalsitescopedpermissions.md).

## Tenant scoped permissions

Azure ACS principals are configured with permission scopes detailing what content they can access and what right they have for that content. If an Azure ACS principal is granted access to content across the complete tenant then you'll be able to see the content scope and associated right in the shown table.

More details about the returned field and their possible values can be found in the documentation of the generated [Azure ACS site collection scoped permissions csv file](csv-classicacsprincipaltenantcopedpermissions.md).

## Sample page

![Azure ACS Principal details](../images/addinsacsacsdetail.png)
