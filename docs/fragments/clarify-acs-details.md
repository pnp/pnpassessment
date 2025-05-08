> [!Note]
> The `Expired`, `AppDomain` and `RedirectUri` fields do require some more context for correct interpretation. `Expired` or `HasExpired` in the CSV files: this value is set depending on the discovered validity of the key credentials set on the service/app principal. There however are cases when there's no validity found which can happen because of:
> 
> - The principal was created using developing Add-Ins with Visual Studio and after deployment the app was not granted permissions or the deployment failed. These show up as `Expired` = true.
> - The principal was a "regular" Entra app that was granted permissions via appinv.aspx. In this case the `AppDomain` and `RedirectUri` fields are empty just as is the validity. These show up as `Expired` = false as there's no service principal in this case
> - Using Microsoft Graph PowerShell or Microsoft Graph APIs the keycredentails on the service principal were cleared. These show up as `Expired` = true.
