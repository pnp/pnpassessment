-- DDL from PR #167 at 1e7829fe48d8d74a9aff3bc98da82c525de88998:
-- 20260928091529_ClassicPublishingLayoutTypeEvidence.Up (EF Core 8.0.3).
-- Applied only by compatibility tests. These columns are not part of the active model.
ALTER TABLE Scans ADD COLUMN PublishingLayoutRuleVersion INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Scans ADD COLUMN PublishingLayoutTypeCatalogJson TEXT NULL;
ALTER TABLE ClassicPageDiscoveries ADD COLUMN DeclaredPageType TEXT NULL;
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PageTypeEvidenceJson TEXT NULL;
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PageTypeEvidenceOrigin TEXT NOT NULL DEFAULT 'None';
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PageTypeReason TEXT NOT NULL DEFAULT 'NotEvaluated';
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PageTypeResolutionStatus TEXT NOT NULL DEFAULT 'Unknown';
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PageTypeSourceStatus TEXT NOT NULL DEFAULT 'Unknown';
ALTER TABLE ClassicPageDiscoveries ADD COLUMN PublishingLayoutFamily TEXT NOT NULL DEFAULT 'Unknown';
ALTER TABLE ClassicPageDiscoveries ADD COLUMN ResolvedPageType TEXT NULL;
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260928091529_ClassicPublishingLayoutTypeEvidence', '8.0.3');
