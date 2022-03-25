
To setup "initial" status delete current cs files in the DatabaseMigration folder and call from the package manager console:

Add-Migration -Name v0.2.0 -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

After making a model change call call below to add a new migration step.
Add-Migration -Name <model change name> -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

Alternative way to remove migration classes:
Remove-Migration -Project PnP.Scanning.Core -StartupProject PnP.Scanning.Process