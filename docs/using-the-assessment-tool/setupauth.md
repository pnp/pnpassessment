# Configure authentication

The Microsoft 365 Assessment tool uses Azure AD based authentication and requires a configured Azure AD application to run. The Microsoft 365 Assessment tool supports both application permissions (app-only) and delegated (user) permissions and various ways to authenticate.

> [!Important]
> If you want the assessment tool to read all sites in your tenant then using application permissions is strongly recommended as that's the only way to guarantee that the Microsoft 365 Assessment tool can read all the sites. When you want to only assess a couple of sites and your account has permissions to these sites then using delegated permissions is an option.

## Setting up the Azure AD application

A configured Azure AD application is a pre-requisite for using the Microsoft 365 Assessment tool. When you [start an assessment](assess-start.md) you'll be able to specify the Azure AD application to use and how you want to authenticate. When you don't specify an Azure AD application when starting an assessment the **PnP Management Shell** application will be assumed, if you're using a recent [PnP PowerShell](https://pnp.github.io/powershell/) version then this application most likely was setup.

> [!Note]
> It's strongly recommended to create a dedicated Azure AD application so you can limit the needed permissions to what's needed by the module you're using in the Microsoft 365 Assessment tool.

### Permissions required

The Microsoft 365 Assessment tool aims to be able to perform the assessment task at hand using minimal read permissions, but for certain assessments not all features work when using minimal permissions. To understand which Microsoft Graph and SharePoint permissions are required please checkout the permission requirements of the respective modules.

- [SharePoint Syntex adoption](../sharepoint-syntex/requirements.md)

### Creating an Azure AD application using PnP PowerShell

 Using [PnP PowerShell](https://pnp.github.io/powershell/) creating an Azure AD application becomes really simple. Below cmdlet will create a new Azure AD application, will create a new self-signed certificate and will hookup that cert with the created Azure AD application. Finally the right permissions are configured and you're prompted to consent these permissions.

> [!Important]
> If you encounter errors during below steps it's likely that you do not have the needed permissions. Please contact your tenant / Azure AD admins for help.

```PowerShell
# Sample for the SharePoint Syntex adoption module. Remove the application/delegated permissions depending on your needs
# and update the Tenant and Username properties to match your environment
Register-PnPAzureADApp -ApplicationName Microsoft365AssessmentToolForSyntex `
                       -Tenant contoso.onmicrosoft.com `
                       -Store CurrentUser `
                       -GraphApplicationPermissions "Sites.Read.All" `
                       -SharePointApplicationPermissions "Sites.FullControl.All" `
                       -GraphDelegatePermissions "Sites.Read.All", "User.Read" `
                       -SharePointDelegatePermissions "AllSites.Manage" `
                       -Username "joe@contoso.onmicrosoft.com" `
                       -Interactive
```

> [!Note]
> Replace `contoso.onmicrosoft.com` with your Azure AD tenant name and ensure you replace `joe@contoso.onmicrosoft.com` with the user id that's an Azure AD admin (or global admin). If you're unsure what your Azure AD tenant name is then go to https://aad.portal.azure.com/#blade/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/Overview check for the value of **Primary domain**.

Once you've pressed enter on above command you'll be prompted to sign-in and you should sign-in using the user you've specified for the `Username` argument. After that's done the Azure AD application will be created and configured, followed by a wait of 60 seconds to ensure the creation has been propagated across all systems. The final step is the admin consent flow: you'll again be prompted to sign-in with the specified admin user, followed by consent dialog showing the permissions that are being granted to the application. Press **Accept** to finalize the consent flow. In the resulting output you'll get some key information:

```text
Pfx file               : D:\assessment\Microsoft365AssessmentToolForSyntexDemo1.pfx
Cer file               : D:\assessment\Microsoft365AssessmentToolForSyntexDemo1.cer
AzureAppId/ClientId    : 95610f5d-729a-4cd1-9ad7-1fa9052e50dd
Certificate Thumbprint : 165CCE93E08FD3CD85B7B25D5E91C05B1D1E49FE
```

Running the `Register-PnPAzureADApp` did not only create and configure the Azure AD application, it also did create a certificate for the application permission flow. This certificate has been added to the current user's certificate store, under the personal node. You can use `certmgr` on the command line to open up the local user's certificate store.

> [!Note]
> The certificate is also exported as PFX file on the file system, feel free to delete this exported file as it's easier to use the certificate from the certificate store.

When you now want to use the certificate for an assessment operation you need to set `--authmode` to `application` and tell the assessment tool which certificate to use via the certificate path parameter: `--certpath "My|CurrentUser|165CCE93E08FD3CD85B7B25D5E91C05B1D1E49FE"`. Next to that you also need to specify the Azure AD application to use via the `--applicationid` argument. More details on how to configure authentication when starting an assessment can be found [here](assess-start.md#authentication-configuration).

> [!Important]
> Notice that the last part in the string is the certificate thumbprint to use.
