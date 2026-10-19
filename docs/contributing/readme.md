# I want to help 🙋‍♂️

If you want to join our team and help, then feel free to check the issue list for planned work or create an issue with sugggested improvements.

## EF Core migration tips

Generate migrations, designers, and snapshots with EF tooling rather than hand-editing generated models. Use tooling matching the project's EF Core version and the default Debug configuration.

From the repository root:

```powershell
dotnet ef migrations add <Name> `
    --project .\src\PnP.Scanning\PnP.Scanning.Core `
    --startup-project .\src\PnP.Scanning\PnP.Scanning.Process `
    --context ScanContext --output-dir Storage\DatabaseMigration
```

- Regenerate unmerged migrations with `migrations remove` then `add`; never force rollback of shared databases or rewrite deployed migrations.
- For schema-equivalent regeneration, preserve the migration ID and filenames when existing scan databases use them.
- Run migration upgrade/downgrade tests (see [Running the tests](running-tests.md)) and `migrations has-pending-model-changes` with the same project/context arguments.
