
General
-------
To setup "initial" status delete current cs files in the DatabaseMigration folder and call from the package manager console:

Add-Migration -Name v0.2.0 -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

After making a model change call call below to add a new migration step.
Add-Migration -Name <model change name> -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

Alternative way to remove migration classes:
Remove-Migration -Project PnP.Scanning.Core -StartupProject PnP.Scanning.Process

When developing
---------------
After adding a table you've called

Add-Migration -Name v1.5.0 -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

Before releasing you're making additional database changes. To get them included do:
- Delete generated migration step (e.g. 20221222133309_v1.5.0.cs)
- Clean new tables/changes from ScanContextModelSnapshot.cs
- Run again: Add-Migration -Name v1.5.0 -Project PnP.Scanning.Core -OutputDir Storage\DatabaseMigration -StartupProject PnP.Scanning.Process

Given you're previously created databases might already have been updated it can be needed to drop these