# CCD-414 ASPX live acquisition implementation evidence

## Verdict and scope

Verdict: **PASS for product implementation and synthetic/offline verification; live tenant behavior remains unverified by design.**

This ref adds the Assessment product entry point required by CCD-394 v3 without changing the physical semantics of `aspx-discovery-output/v2`, `aspx-discovery/v2`, or `aspx-discovery-sqlite/v2`. It adds separate `aspx-reference-output/v1` / `aspx-reference-sqlite/v1` and seals `aspx-acquisition-verdict/v1` only after both companion volumes are written and hashed.

This task did not run CUPCollect, authenticate to a tenant, or claim tenant-wide completeness. Live tenant validation is a separate dependent task.

## Refs and normative input

- Product branch: `paperclip/ccd-414-live-provider` (the final full commit SHA is recorded on CCD-414 and its commit work product).
- Product baseline: `705d72a11d3fd2a14ad1624e2861721d0c51715c`, tree `ef0de60e341cb4ba5920ac0ab4cab92c4cafbf84`.
- PnP.Core compatibility ref: `1f07296b186698c3cc9ca8580f00af36c0f3f4f5`, tree `0534cd65f8e942671886ae0f6820580f81031572`.
- Contract artifact: `artifacts/ccd-394/aspx-surface-applicability-denominator-v3.md`, size `40550`, SHA-256 `de492c8da56c788e092fd8a83eee9662de7da49e95a3768866656b13cfccf982`.
- Contract revision: `7d44f61d-919e-478a-ab8e-bb4c8f9b4b4a`.

## KB workflow

The installed CCD Internal KB was queried before implementation with scenario filtering. The selected entries were:

- `kb/private/team/ccm/environment/prepare-pnp-modernization-assessment.md`
- `kb/private/team/ccm/classic-page-corpus/collect-classic-page-relationship-model.md`
- `kb/private/team/ccm/page-compare/page-discovery-runbook.md`
- `kb/spo/scenarios/interfaces/spo-interfaces-mcp-tool-discovery-auth-filter-v1.md`

The implementation follows the reusable KB rules that denied evidence is not absence, authentication inputs stay provider-managed at runtime, and captured/derived/unavailable states remain distinct. No verified contradiction or missing reusable product fact was found, so no KB contribution was required.

## Product implementation

### Live provider and authority adapters

- `SharePointLiveAspxDiscoveryProvider : IAspxDiscoveryProvider` in `SharePointLiveAspxDiscoveryProvider.cs` is backed by `IPnPContextFactory`, `IAuthenticationProvider`, `PnPContext.AuthenticationProvider`, and `PnPContext.RestClient.Client`.
- `PnPContextSharePointAspxRestClientFactory` reuses the Assessment-supported PnP Core context/authentication path.
- Root and recursive subweb acquisition uses unfiltered `GET .../_api/web/webs` authority.
- All-list acquisition uses `GET .../_api/web/lists?$select=Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,DefaultViewUrl&$expand=RootFolder` with an empty `$filter`.
- `AspxListApplicabilityPolicy` uses actual `List.BaseType` as the sole raw-library admission authority. `BaseTemplate`, `Hidden`, catalog state, title, and path are retained only as provenance.
- Actual `BaseType=1` registers recursive ordinary files and the physical `Forms` tree. Every web registers independent Web-root traversal.
- Every list, for every BaseType, uses the exact authenticated REST Forms and Views endpoints from CCD-394 v3. Non-1 BaseType locators that resolve to a physical `SPFile` are emitted as v2 `ListFormBackingFiles` / `ListViewBackingFiles` observations.
- Every web reads `RootFolder.WelcomePage` with explicit success-empty, denied, failed, non-ASPX, linked physical, and unknown reference dispositions.

### Pagination, authority, and failure semantics

- `AspxPaginationContract` validates zero-based continuous ordinals, request/next token hash binding, terminal-without-token, loop/reorder detection, and outstanding-token exhaustion.
- Raw continuation tokens are not written to output; only irreversible token hashes and bounded endpoint metadata are stored.
- `AspxSurfaceDenominatorRow` persists actual method, endpoint, select, filter, permission/visibility boundary, expectedCount Known/Unknown, observed count, pagination chain hash, outstanding count, authority/build/registry bindings, and aggregate effect.
- Transport, HTTP, parse, identity-resolution, permission, and registry failures fail soft at the affected surface and fail closed in the aggregate.
- A denied child with a null token is not Complete. `expectedCount=Unknown` with observed zero is not Empty. Unknown/incompatible registry or build makes the aggregate Unknown.

### Three-volume compatibility

- Existing physical v2 records, store schema, canonical file-key algorithm, and output type remain unchanged.
- `AspxReferenceStore` writes a separate SQLite v1 store with exact reference-manifest resume binding.
- `AspxReferenceCollector` writes typed reference observations without adding reference rows or enums to physical v2.
- Reference-only/unavailable/virtual records cannot carry physical identity. Linked physical references require a real file ID and the one matching v2 canonical inventory key.
- Multiple references can point to one physical key without increasing the physical file count.
- `AspxAcquisitionRuntime` writes physical and reference outputs, hashes both files, then writes the sealed aggregate envelope.
- `AspxAcquisitionEnvelopeValidator` provides exact-version dispatch and rejects missing companions, hash/ref/run/scope/snapshot drift, and legacy physical-only completion claims.

### Production CLI

- `aspx-acquisition` is the authenticated live entry point. It accepts site collection URLs, existing Assessment authentication options, immutable physical manifest, independent registry, authority/build/fence inputs, and separate physical/reference/aggregate paths.
- `aspx-inventory` remains the explicit sealed manifest/offline mode.
- `Program.ConfigureCliHost` now registers PnP Core services for live CLI context creation.
- The two ASPX commands skip the unrelated update/version network probe before command execution.

CLI help receipt:

```text
Description:
  Runs authenticated SharePoint live ASPX acquisition and writes physical v2, reference v1, and aggregate v1 volumes. Use aspx-inventory for explicit offline manifest replay.

Usage:
  microsoft365-assessment aspx-acquisition [options]
```

The help lists required `--site`, authentication, registry, physical database/output, reference database/output, aggregate output, platform build, snapshot fence, visibility, and authority arguments.

## Fixtures and verification

Focused v3 fixtures are in `AspxAcquisitionV3Tests.cs`.

- Pagination: P1-P5.
- Registry/build: R1-R5.
- Identity/join: I1-I5.
- Version/store/reader compatibility: V1-V7.
- Forms/Views and non-1 physical resolution: F1-F8.
- Immutable NotApplicable binding: N1-N4.
- Actual BaseType authority: A1-A2.
- Positive fixtures include actual BaseType 1 with an unknown template and a GenericList Form locator resolving to a physical ASPX file.
- The synthetic live-provider run writes all five concrete artifacts: physical SQLite/JSON, reference SQLite/JSON, and aggregate JSON.

Validated Windows runner:

```text
Q:\src\paperclip-ccd281-dotnet-8.0.424-win-x64\dotnet.exe
```

Native-path build mirror:

```text
Q:\src\paperclip-ccd414-edfed3ac\assessment
```

Commands executed:

```powershell
dotnet.exe build src/PnP.Scanning/PnP.Scanning.Core/PnP.Scanning.Core.csproj -c Release --nologo -p:TargetFramework=net8.0 -p:TargetFrameworks=net8.0 --no-restore
dotnet.exe build src/PnP.Scanning/PnP.Scanning.Process/PnP.Scanning.Process.csproj -c Release --nologo -p:TargetFramework=net8.0 -p:TargetFrameworks=net8.0 --no-restore
dotnet.exe test src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --nologo -p:TargetFramework=net8.0 -p:TargetFrameworks=net8.0 --no-restore --filter FullyQualifiedName~Discovery.Aspx
dotnet.exe src/PnP.Scanning/PnP.Scanning.Process/bin/Release/net8.0/microsoft365-assessment.dll aspx-acquisition --help
```

Results:

- Core build: PASS, 0 errors.
- Production CLI build: PASS, 0 errors.
- Combined existing/new ASPX discovery tests: PASS, 41/41, 0 failed, 0 skipped.
- Focused CCD-394 v3 tests: PASS, 11/11, including the synthetic three-volume acquisition run.
- CLI help: PASS; live and offline entry points are explicit.
- Runner-only warnings: SourceLink could not locate repository metadata through the temporary Q: junction. Product compilation and tests were unaffected.

## Read versus executed

Read:

- CCD-394 v3 contract artifact.
- Assessment discovery, storage, CLI, authentication, and existing ASPX tests at the stated baseline.
- PnP.Core context, REST client, list/file/folder models, and compatibility ref source.
- Selected CCD Internal KB scenarios listed above.

Executed:

- Local/native-path build and synthetic tests only.
- CLI help only; no live command invocation with tenant inputs.

Not executed:

- CUPCollect or any other tenant scan.
- Browser, Edge, WAM/MSAL, or tenant authentication.
- Live Forms/Views endpoint validation by BaseType.
- Live personal-view visibility validation.
- Live registry/platform-build binding against a tenant response.

## Remaining live-only unknowns

- Whether the target tenant/build returns every Forms endpoint shape for each BaseType and every documented next-link flavor.
- Effective personal-view visibility for the live delegated identity.
- Tenant-specific permission/visibility gaps, throttling, and snapshot drift.
- Live setup/virtual registry compatibility and runtime counterexamples.
- Tenant-wide completeness; synthetic fixtures cannot close it.

The dependent live validation task must supply a compatible independent registry, observed platform build, immutable scope authority, snapshot fence, and authorized identity context. Any mismatch remains Unknown rather than being reinterpreted as Complete or Empty.
