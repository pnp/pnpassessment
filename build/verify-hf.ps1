#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Foundation', 'Release')][string]$Suite = 'Foundation',
    [string]$OutputDirectory,
    [switch]$NoRestore,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$hfRepo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hfSibling = Join-Path (Split-Path $hfRepo -Parent) 'pnpcore'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $hfRepo '.temp/hf-acceptance' }
$hfOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $hfOutput -Force | Out-Null
$hfTests = Join-Path $hfRepo 'src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj'
$hfCore = Join-Path $hfRepo 'src/PnP.Scanning/PnP.Scanning.Core/PnP.Scanning.Core.csproj'
$hfProcess = Join-Path $hfRepo 'src/PnP.Scanning/PnP.Scanning.Process/PnP.Scanning.Process.csproj'
$hfFramework = @('-p:TargetFrameworks=net8.0', '-p:TargetFramework=net8.0')
$hfPreviousEnvironment = @{}
$hfEnvironment = @{
    HF_TEST_ROOT = (Join-Path $hfOutput 'fixture-data')
    HF_RECEIPT_DIRECTORY = $hfOutput
    HF_KEEP_FIXTURES = '1'
    TEMP = (Join-Path $hfOutput 'temp')
    TMP = (Join-Path $hfOutput 'temp')
}

function Invoke-HFDotnet {
    param([string[]]$Arguments, [string]$Log)
    & dotnet @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $Log -Tail 35 | Write-Host
        throw "dotnet failed; see $Log"
    }
}

Push-Location $hfRepo
try {
    $hfSdk = (& dotnet --version).Trim()
    if (-not $hfSdk.StartsWith('8.0.')) {
        throw 'Select a .NET 8 SDK using global.json in the workflow parent directory before running this script.'
    }
    $hfCommit = (& git rev-parse HEAD).Trim()
    $hfDirty = @(& git status --porcelain)
    if ($hfDirty.Count -gt 0 -and -not $AllowDirty) { throw 'Commit the implementation first, or use -AllowDirty for development checks.' }
    $hfDependency = (& git -C $hfSibling rev-parse HEAD).Trim()
    if ($hfDependency -ne '1f07296b186698c3cc9ca8580f00af36c0f3f4f5') {
        throw 'The sibling pnpcore checkout must be the immutable 1.18.0 release (1f07296b186698c3cc9ca8580f00af36c0f3f4f5).'
    }
    $hfEnvironment.HF_SOURCE_COMMIT = $hfCommit
    foreach ($hfName in $hfEnvironment.Keys) {
        $hfPreviousEnvironment[$hfName] = [Environment]::GetEnvironmentVariable($hfName, 'Process')
        [Environment]::SetEnvironmentVariable($hfName, $hfEnvironment[$hfName], 'Process')
    }
    New-Item -ItemType Directory -Path $hfEnvironment.TEMP -Force | Out-Null

    if (-not $NoRestore) {
        Invoke-HFDotnet -Arguments (@('restore', $hfTests) + $hfFramework) -Log (Join-Path $hfOutput 'restore.log')
    }
    $hfAssets = Get-Content -LiteralPath (Join-Path (Split-Path $hfCore -Parent) 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $hfPackagesRoot = @($hfAssets.packageFolders.Keys)[0]
    $hfGrpc = Join-Path $hfPackagesRoot 'grpc.tools/2.61.0'
    $hfPlatform = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macosx' } else { 'linux' }
    $hfArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $hfProtoc = Join-Path $hfGrpc "tools/$($hfPlatform)_$hfArch/protoc$(if ($IsWindows) { '.exe' })"
    $hfPlugin = Join-Path $hfGrpc "tools/$($hfPlatform)_$hfArch/grpc_csharp_plugin$(if ($IsWindows) { '.exe' })"
    if (-not (Test-Path -LiteralPath $hfProtoc) -or -not (Test-Path -LiteralPath $hfPlugin)) {
        throw "Grpc.Tools 2.61.0 does not provide tools at the resolved platform paths: $hfProtoc / $hfPlugin"
    }
    # Restricting the multi-target SDK sibling to net8.0 can make NuGet props conditional.
    # Supply these per invocation, without changing SDK sources, package caches or global tools.
    $hfBuild = $hfFramework + @(
        "-p:Protobuf_ProtocFullPath=$hfProtoc",
        "-p:gRPC_PluginFullPath=$hfPlugin",
        "-p:Protobuf_StandardImportsPath=$(Join-Path $hfGrpc 'build/native/include')"
    )
    $hfArguments = @('test', $hfTests, '--configuration', 'Release', '--no-restore') + $hfBuild
    if ($Suite -eq 'Foundation') { $hfArguments += @('--filter', 'Category=HFFoundation') }
    $hfArguments += @('--logger', 'trx;LogFileName=results.trx', '--results-directory', $hfOutput)
    Invoke-HFDotnet -Arguments $hfArguments -Log (Join-Path $hfOutput 'tests.log')

    $hfTools = Join-Path $hfOutput 'tools'
    $hfEf = Join-Path $hfTools "dotnet-ef$(if ($IsWindows) { '.exe' })"
    if (-not (Test-Path -LiteralPath $hfEf)) {
        Invoke-HFDotnet -Arguments @('tool', 'install', 'dotnet-ef', '--version', '8.0.3', '--tool-path', $hfTools) -Log (Join-Path $hfOutput 'ef-install.log')
    }
    & $hfEf migrations has-pending-model-changes --project $hfCore --startup-project $hfProcess --configuration Release --framework net8.0 --no-build *> (Join-Path $hfOutput 'model-check.log')
    if ($LASTEXITCODE -ne 0) { throw "EF model check failed; see $(Join-Path $hfOutput 'model-check.log')" }

    [xml]$hfTrx = Get-Content -LiteralPath (Join-Path $hfOutput 'results.trx') -Raw
    $hfCases = @($hfTrx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $hfAcceptance = foreach ($hfScenario in @('T28', 'T29', 'T30', 'T31', 'T32', 'T33')) {
        $hfRows = @($hfCases | Where-Object { $_.testName -like "*$($hfScenario)_*" })
        if ($hfRows.Count -eq 0 -or @($hfRows | Where-Object outcome -ne 'Passed').Count -gt 0) {
            throw "Scenario $hfScenario has missing or unsuccessful acceptance evidence."
        }
        @{ scenario = $hfScenario; result = 'PASS'; tests = @($hfRows | ForEach-Object { $_.testName }) }
    }
    $hfReceipt = @{
        kind = 'H-F verification receipt'
        result = 'PASS'
        suite = $Suite
        sourceCommit = $hfCommit
        workingTreeClean = ($hfDirty.Count -eq 0)
        definitionSha256 = '446e75af6788aac73c3cb93d6cc51abec4a600b43bc9efa49a9b8500f43f2bee'
        runtime = @{ sdk = $hfSdk; targetFramework = 'net8.0'; efTool = '8.0.3'; pnpCoreTag = '1.18.0'; pnpCoreCommit = $hfDependency }
        resolvedLibraries = @($hfAssets.libraries.Keys | Sort-Object)
        tests = @{ total = $hfCases.Count; passed = @($hfCases | Where-Object outcome -eq 'Passed').Count; failed = @($hfCases | Where-Object outcome -ne 'Passed').Count }
        acceptance = @($hfAcceptance)
        modelCheck = 'PASS'
        artifacts = @('results.trx', 'tests.log', 'model-check.log', 'hf-fixture-receipt.json', 'hf-native-assessment.db') | ForEach-Object {
            $hfFile = Join-Path $hfOutput $_
            @{ file = $_; sha256 = (Get-FileHash -LiteralPath $hfFile -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        businessAcceptance = 'C1-C3/A1-A4/T26 NOT_RUN'
        completedUtc = [DateTime]::UtcNow.ToString('O')
    }
    $hfReceipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $hfOutput 'verification-receipt.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $hfOutput 'tests.log') -Tail 8 | Write-Host
    Write-Host "H-F receipt: $(Join-Path $hfOutput 'verification-receipt.json')"
}
finally {
    foreach ($hfName in $hfPreviousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($hfName, $hfPreviousEnvironment[$hfName], 'Process')
    }
    Pop-Location
}
