# Configure authentication

The Microsoft 365 Assessment tool uses Azure AD based authentication and requires a configured Azure AD application to run. The Microsoft 365 Assessment tool supports both application permissions (app-only) and delegated (user) permissions and various ways to authenticate.

> [!Important]
> If you want the assessment tool to read all sites in your tenant then using application permissions is strongly recommended as that's the only way to guarantee that the Microsoft 365 Assessment tool can read all the sites. When you want to only assess a couple of sites and your account has permissions to these sites then using delegated permissions is an option.

## Setting up the Azure AD application

A configured Azure AD application is a pre-requisite for using the Microsoft 365 Assessment tool. When you [start an assessment](assess-start.md) you'll be able to specify the Azure AD application to use and how you want to authenticate. When you don't specify an Azure AD application when starting an assessment the **PnP Management Shell** application will be assumed, if you're using a recent [PnP PowerShell](https://pnp.github.io/powershell/) version then this application most likely was setup.

> [!Note]
> It's strongly recommended to create a dedicated Azure AD application so you can limit the needed permissions to what's needed by the module you're using in the Microsoft 365 Assessment tool.

### Permissions required

The Microsoft 365 Assessment tool aims to be able to perform the assessment task at hand using minimal read permissions, but for certain assessments not all features work when using minimal permissions. To understand which Microsoft Graph and SharePoint permissions are required please checkout the authentication page of the respective modules.

- [SharePoint Syntex adoption](../sharepoint-syntex/authrequirements.md)

### Creating an Azure AD application using PnP PowerShell

 Using [PnP PowerShell](https://pnp.github.io/powershell/) creating an Azure AD application becomes really simple. Below cmdlet will create a new Azure AD application, will create a new self-signed certificate and will hookup that cert with the created Azure AD application. Finally the right permissions are configured and you're prompted to consent these permissions.

> [!Important]
> If you encounter errors during below steps it's likely that you do not have the needed permissions. Please contact your tenant / Azure AD admins for help.

```PowerShell
# Sample for the SharePoint Syntex adoption module
Register-PnPAzureADApp -ApplicationName Microsoft365AssessmentToolForSyntex `
                       -Tenant contoso.onmicrosoft.com `
                       -Store CurrentUser `
                       # Keep these if you want to use application permissions
                       -GraphApplicationPermissions "Sites.Read.All" `
                       -SharePointApplicationPermissions "Sites.FullControl.All" `
                       # Keep these if you want to use delegated permissions
                       -GraphDelegatePermissions "Sites.Read.All", "User.Read" `
                       -SharePointDelegatePermissions "AllSites.Manage" `
                       -Username "joe@contoso.onmicrosoft.com" `
                       -Interactive
```

> [!Note]
> Replace contoso.onmicrosoft.com with your Azure AD tenant name and ensure you replace joe@contoso.onmicrosoft.com with the user id that's an Azure AD admin (or global admin)
