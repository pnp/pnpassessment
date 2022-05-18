# Microsoft 365 Assessment tool configuration

For the majority of users no additional configuration is needed, but for example when you're using a tenant in another cloud or using vanity URLs additional configuration is needed before the Microsoft 365 Assessment tool can be used.

## The `appsettings.json` configuration file

Additional configuration settings are all defined via a settings file named `appsettings.json`. By default this file is not present, so when you need additional configuration you first need to start with adding a file named `appsettings.json` in the same location as where you've put the assessment tool `microsoft365-assessment.exe`. The empty starting contents for this file are shown below:

```json
{
  "CustomSettings": {
  }
}
```

A fully configured file could look like this:

```json
{
  "PnPCore": {
    "Environment": "USGovernment"
  },
  "CustomSettings": {
    "Environment": "USGovernment",    
    "Port": 7887,
    "AdminCenterUrl": "https://spo-admin.contoso.com",
    "MySiteHostUrl": "https://my.contoso.com"
  }
}
```

## Cloud environment configuration

The assessment tool can be used to run against tenants hosted in other cloud environments by specifying the environment to use in the settings file as shown below.

```json
{
  "PnPCore": {
    "Environment": "USGovernment"
  },
  "CustomSettings": {
    "Environment": "USGovernment"    
  }
}
```

Valid values for environment are: `Production`, `PreProduction` (Microsoft internal only), `USGovernment` (a.k.a GCC), `USGovernmentHigh` (a.k.a GCC High), `USGovernmentDoD` (a.k.a DoD), `China` and `Germany`.

## Vanity URL configuration

A handful tenants use custom URLs due to historic reasons (so called vanity URLs) and when you want to use the Microsoft 365 Assessment tool you'll need to let the tool know these custom URLs by specifying them in the settings file:

```json
{
  "CustomSettings": {
    "AdminCenterUrl": "https://spo-admin.contoso.com",
    "MySiteHostUrl": "https://my.contoso.com"
  }
}
```

> [!Important]
> Both URLs have to specified when you're using vanity URLs.

Next to the using the configuration file you also need to either use the `--sitesfile` or `--siteslist` arguments when [starting a new assessment](assess-start.md). Enumerating all site collections in a vanity URL tenant will be added in a future release.

## Using a different port than 25010

The Microsoft 365 Assessment tool process accepts commands by listening on localhost port 25010. For most computers this port is a free and can be used, but if for some reason you prefer to use another port than that's possible as well:

```json
{
  "CustomSettings": {
    "Port": 7887
  }
}
```

## Disabling telemetry

When using the Microsoft 365 Assessment tool basic telemetry information is sent to Microsoft. This information is used to understand how the tool is being used which helps us improve the Microsoft 365 Assessment tool going forward. If you however prefer to not sent and telemetry information then that's possible by adding an environment variable named `PNP_DISABLETELEMETRY` with a value of `true`. After restarting the Microsoft 365 Assessment tool no telemetry data will be sent anymore.
