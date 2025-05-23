> [!Note]
> The `Expired`, `AppDomain` and `RedirectUri` fields do require some more context for correct interpretation. `Expired` or `HasExpired` in the CSV files: this value is set depending on the discovered validity of the keycredentials set on the service/app principal. There however are cases when **there's no validity found** (so expiration date equal to '01/01/0001 00:00:00') which can happen because of:
> 
> - The principal was created using developing Add-Ins with Visual Studio and after deployment the app was not granted permissions or the deployment failed. Usually these also have a localhost `AppDomain` and an empty `RedirectUri`. These show up as `Expired` = true.
> - The principal was a "regular" Entra app that was granted permissions via appinv.aspx. In this case the `AppDomain` and `RedirectUri` fields are empty just as is the validity. These show up as `Expired` = false as the keycredentials are set on the app principal. The assessment tool is not reading the app principal in this case.
> - Using Microsoft Graph PowerShell or Microsoft Graph APIs the keycredentails on the service principal were cleared. These show up as `Expired` = true.
