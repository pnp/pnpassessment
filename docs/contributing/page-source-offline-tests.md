# Reproducing physical Page source tests on Linux

Run these offline tests only in an authorized work repository and its own local
temporary/delivery directories. Do not read a predecessor's writable workspace.
The read-only PnP Core dependency remains at its managed 1.18.0 commit
`1f07296b186698c3cc9ca8580f00af36c0f3f4f5`; an upstream literal tag is not assumed.
The original solution includes Core, Process, Core.Tests and PnP Core/Auth/Admin.
Do not edit the solution, remove tests, change target frameworks or use major
runtime roll-forward to make a run pass.

The following PowerShell recipe uses the supplied Linux x64 SDK 10.0.303 unchanged
and official .NET/ASP.NET Core 8.0.25 shared frameworks for the net8.0 test host.
All links are created **inside your workspace**; their targets are read-only.
This does not install a new SDK or mutate the container image.

## 1. Assign paths and verify the local runtime archive

Replace only the three authorized paths below. They are not credentials.
The work repository must already be prepared under `repos/pnpassessment`.

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$work = '<your-authorized-workspace>'
$dependency = '<your-managed-read-only-pnpcore-directory>'
$delivery = '<your-assigned-private-delivery-directory>'
$repo = Join-Path $work 'repos/pnpassessment'
$scratch = Join-Path $work '.tmp'
$runtime = Join-Path $scratch 'tools/dotnet-8.0.25'
$downloads = Join-Path $scratch 'downloads'
New-Item -ItemType Directory -Force $runtime,$downloads,$delivery | Out-Null
if ((git -C $dependency rev-parse HEAD).Trim() -ne
    '1f07296b186698c3cc9ca8580f00af36c0f3f4f5') { throw 'Wrong dependency pin' }
if (git -C $dependency status --porcelain) { throw 'Dependency is not clean' }

$url = 'https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/8.0.25/aspnetcore-runtime-8.0.25-linux-x64.tar.gz'
$sha512 = 'ddb66ac366252ab382271241b3e53a75201d2c848c9ec870a27fb178a6db18e4d949b9896a3d8530d03d255f4fd51d635367bedda3d9f3c677cb596784dbcb9c'
$archive = Join-Path $downloads 'aspnetcore-runtime-8.0.25-linux-x64.tar.gz'
curl --fail --show-error --location --proto '=https' --tlsv1.2 `
    --max-time 180 --max-filesize 134217728 --output $archive $url
if ((Get-FileHash -Algorithm SHA512 $archive).Hash -ine $sha512) {
    throw 'Official runtime archive checksum mismatch'
}
tar -xzf $archive -C $runtime
```

The SHA512 is the official release-metadata value for the **non-composite**
`aspnetcore-runtime-linux-x64.tar.gz` entry in release 8.0.25. To independently
check it, download
`https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json`
with an 8 MiB/60-second bound and select that exact release, RID and entry name.
It includes both shared frameworks and the native runtime host.
Record URL, version, checksum, archive size, architecture, limits and extraction
directory before executing tests. Keep archives out of Git.

## 2. Map the unchanged SDK and read-only dependency

The mappings below match the supplied SDK's 10.0.11 host/runtime. Check the
supplied paths first if your authorized image differs; do not substitute an SDK.

```powershell
foreach ($name in @('sdk','packs','sdk-manifests','templates')) {
    $target = Join-Path $runtime $name
    if (!(Test-Path $target)) {
        New-Item -ItemType SymbolicLink -Path $target `
            -Target "/usr/share/dotnet/$name" | Out-Null
    }
}
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App')) {
    $target = Join-Path $runtime "shared/$framework/10.0.11"
    if (!(Test-Path $target)) {
        New-Item -ItemType SymbolicLink -Path $target `
            -Target "/usr/share/dotnet/shared/$framework/10.0.11" | Out-Null
    }
}
$fxr = Join-Path $runtime 'host/fxr/10.0.11'
if (!(Test-Path $fxr)) {
    New-Item -ItemType SymbolicLink -Path $fxr `
        -Target '/usr/share/dotnet/host/fxr/10.0.11' | Out-Null
}
$mapping = Join-Path $work 'repos/pnpcore'
if (!(Test-Path $mapping)) {
    New-Item -ItemType SymbolicLink -Path $mapping -Target $dependency | Out-Null
}
```

The inherited solution and ProjectReference relative paths now resolve without
dependency edits. If a mapping already exists, verify its exact authorized target
before proceeding. A link is not a permission grant.

## 3. Redirect every output and select the test host

Set these in the **same PowerShell process** that runs the commands. A global
`OutputPath` shared by all projects/frameworks is not safe. Early properties
redirect restore/project-extension paths; late targets override the dependency's
explicit documentation path. Configuration and target framework remain separate.

```powershell
$env:PATH = "$runtime" + [IO.Path]::PathSeparator + $env:PATH
$env:DOTNET_ROOT = $runtime
$env:DOTNET_ROOT_X64 = $runtime
$env:DOTNET_ROLL_FORWARD = 'LatestPatch'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_HOME = Join-Path $scratch 'cli-home'
$env:NUGET_PACKAGES = Join-Path $scratch 'nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $scratch 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $scratch 'nuget-plugins'
$env:TMPDIR = Join-Path $scratch 'tmp'
$env:TMP = $env:TMPDIR
$env:TEMP = $env:TMPDIR
$env:ControlledOutputRoot = Join-Path $scratch 'build'
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,`
    $env:NUGET_HTTP_CACHE_PATH,$env:NUGET_PLUGINS_CACHE_PATH,$env:TMPDIR,`
    $env:ControlledOutputRoot | Out-Null
$env:DirectoryBuildPropsPath = Join-Path $scratch 'controlled-build.props'
$env:DirectoryBuildTargetsPath = Join-Path $scratch 'controlled-build.targets'
@'
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$(ControlledOutputRoot)/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
    <BaseOutputPath>$(ControlledOutputRoot)/bin/$(MSBuildProjectName)/</BaseOutputPath>
    <DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
'@ | Set-Content $env:DirectoryBuildPropsPath
@'
<Project>
  <PropertyGroup>
    <DocumentationFile Condition="'$(DocumentationFile)' != ''">$(ControlledOutputRoot)/doc/$(MSBuildProjectName)/$(Configuration)/$(TargetFramework)/$(MSBuildProjectName).xml</DocumentationFile>
  </PropertyGroup>
  <Target Name="CreateControlledDocumentationDirectory" BeforeTargets="CoreCompile" Condition="'$(DocumentationFile)' != ''">
    <MakeDir Directories="$([System.IO.Path]::GetDirectoryName('$(DocumentationFile)'))" />
  </Target>
</Project>
'@ | Set-Content $env:DirectoryBuildTargetsPath
$env:RunSettingsFilePath = Join-Path $scratch 'runtime.runsettings'
$env:VSTestResultsDirectory = $delivery
@"
<RunSettings>
  <RunConfiguration>
    <DotNetHostPath>$runtime/dotnet</DotNetHostPath>
    <ResultsDirectory>$delivery</ResultsDirectory>
    <EnvironmentVariables>
      <DOTNET_ROOT>$runtime</DOTNET_ROOT>
      <DOTNET_ROOT_X64>$runtime</DOTNET_ROOT_X64>
      <DOTNET_ROLL_FORWARD>LatestPatch</DOTNET_ROLL_FORWARD>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
"@ | Set-Content $env:RunSettingsFilePath
dotnet --info
if ((dotnet --version).Trim() -ne '10.0.303') { throw 'Supplied SDK changed' }
```

Use XML-safe absolute paths in generated XML. Preserve the image/storage binding
and workspace/tool-home persistence; never write obj/bin/generated/docs/cache
outputs into the read-only dependency. Validate that it remains clean and that no
obj/bin directories appeared there after the run.

## 4. Run the full solution and the independent category

From the **actual solution directory**, on the final committed source tree:

```powershell
Set-Location (Join-Path $repo 'src/PnP.Scanning')
git rev-parse HEAD
git status --short
dotnet sln PnP.Scanning.sln list
dotnet build -c Release
dotnet test -c Release --logger 'trx;LogFileName=cp1-release.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'Category=PageInherits' --logger 'trx;LogFileName=cp1-fixtures.trx'
```

The category includes acquisition, runtime-host proof, parser/default,
persistence/CSV/upgrade/restart and integrated input-to-native-output fixtures,
including backup/restore/downgrade. The full solution also retains the inherited
family, routing, accounting and all other original Release tests.

`AspxSourceRuntimeTests` asserts major version 8 for both shared frameworks and
prints their assembly locations in TRX. They must resolve to the local 8.0.25
directories, not major roll-forward to the SDK's runtime. A build can succeed
without a working net8.0 test host: inspect this proof and actual nonzero test
counts, not just `dotnet --info` or process exit.

Capture complete output separately for each command, actual UTC start/end, exit
codes and TRX pass/fail/skip totals. Record the exact source commit, directory,
SDK/runtime, mappings/settings and dependency pin. Do not reuse a log/TRX filename
from another run without retaining its earlier result. A failed/unavailable
run or zero discovered tests is not PASS. Keep warnings and failures in the
private evidence record instead of hiding them.

For an isolated source rollback run, use a separate per-project output root
under your own scratch copy, unwind only the delivered source delta, verify the
tree against its declared baseline, and rerun the **full original** Release
solution. Do not change shared refs, the managed dependency or later consumers.

These tests are synthetic/offline. They do not authorize live/tenant requests,
server diagnostics, remote Git writes, a PR, checkpoint acceptance or publication.
Live claims require separate bounded authorization/readback; if the original
controlled test cohort is used, retain `User-Agent: testtraffic-smr`.
