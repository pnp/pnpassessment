
# How to add a new assessment module

To add a new assessment module, follow these steps. The tool is not modular by design, so adding a new module requires changes in multiple places in the codebase.

## Step 1: Define the new name for the module

- Decide on a name for your new module (e.g. "FeatureX")
- Update the `Module` enum in PnP.Scanning\PnP.Scanning.Core\Services\Enums to include your new module name

## Step 2: Core assessment code

- Add a folder for your module under PnP.Scanning\PnP.Scanning.Core\Scanners (e.g. FeatureX)
- Create a new class for your scanner, inheriting from `ScannerBase` (e.g. `FeatureXScanner`)
- Create a new options class, inheriting from `OptionsBase` (e.g. `FeatureXOptions`)
- Create a new scan component class (e.g. `FeatureXScanComponent`)
- Adjust the `NewScanner` method in `ScannerBase` to include your new scanner

## Step 3: Storage and data handling

- Add one or more data model classes under PnP.Scanning\PnP.Scanning.Core\Storage\Model, inheriting from `BaseScanResult`
- Extend the `ScanContext` class to include the new data model classes
- Extend the `StorageManager` class with methods for writing and reading data, and include a method to drop incomplete web scan data (update in `DropIncompleteWebScanDataAsync`)
- Deal with database upgrades by adding migration scripts in the PnP.Scanning\PnP.Scanning.Core\Storage\DatabaseMigration folder. Check the readme.txt file in that folder for instructions on how to do this.

## Step 4: ( = Optional) handle assement module options

- If your module has specific options, these are coded in the Options class (e.g. `FeatureXOptions`)
- To pass options from command line, update the `StartCommandHandler.cs` class in PnP.Scanning\PnP.Scanning.Process\Commands\Handlers

## Step 5: Reporting

- Add folder (e.g. FeatureX) under src\pnp.scanning\reports
- Update the `ReportManager` class to include the definition of csv files, link to embedded report file, export data to csv
- Follow the instructions in the `instructions for creating and updating power bi pbit files.md` and `readme.md` files in the PnP.Scanning\Reports folder to create the report

## Step 6: Update the documentation

- Create a folder under the docs folder for your new module (e.g. "FeatureX")
- Copy the structure from an existing module (e.g. "Alerts") and update the files accordingly
- Update the `supportedassessments.md` file in docs\fragments to include your new module
- Update the `docfx.json` file to include your new module in the documentation build

## Step 7: (optional) Update the telemetry

- Update the `TelemetryManager` class in PnP.Scanning\PnP.Scanning.Core\Services\Telemetry to include your new module by updating the `LogScanEndAsync` method and adding a new `PopulateFeatureXMetricsAsync` method


**Note:** by searching for the string "PER SCAN COMPONENT" in the codebase, you can find all places where you need to add your new module.
