#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Pages', 'Release')][string]$Suite = 'Pages',
    [string]$OutputDirectory,
    [switch]$NoRestore,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$cpRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cpSibling = Join-Path (Split-Path $cpRepo -Parent) 'pnpcore'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $cpRepo '.temp/classicpage-acceptance' }
$cpOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $cpOutput -Force | Out-Null
$cpTests = Join-Path $cpRepo 'src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj'
$cpCore = Join-Path $cpRepo 'src/PnP.Scanning/PnP.Scanning.Core/PnP.Scanning.Core.csproj'
$cpProcess = Join-Path $cpRepo 'src/PnP.Scanning/PnP.Scanning.Process/PnP.Scanning.Process.csproj'
$cpFramework = @('-p:TargetFrameworks=net8.0', '-p:TargetFramework=net8.0')
$cpSavedEnvironment = @{}
$cpEnvironment = @{
    HF_TEST_ROOT = (Join-Path $cpOutput 'fixture-data')
    HF_RECEIPT_DIRECTORY = $cpOutput
    HF_KEEP_FIXTURES = '1'
    TEMP = (Join-Path $cpOutput 'temp')
    TMP = (Join-Path $cpOutput 'temp')
}
function Invoke-PageDotnet {
    param([string[]]$Arguments, [string]$Log)
    & dotnet @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $Log -Tail 25 | Write-Host
        throw "dotnet failed; see $Log"
    }
}
Push-Location $cpRepo
try {
    $cpSdk = (& dotnet --version).Trim()
    if (-not $cpSdk.StartsWith('8.0.')) { throw 'Select .NET 8 using global.json in the workflow parent directory.' }
    $cpCommit = (& git rev-parse HEAD).Trim()
    $cpDirty = @(& git status --porcelain)
    if ($cpDirty.Count -gt 0 -and -not $AllowDirty) { throw 'Commit the implementation first, or use -AllowDirty for development checks.' }
    $cpDependency = (& git -C $cpSibling rev-parse HEAD).Trim()
    if ($cpDependency -ne '1f07296b186698c3cc9ca8580f00af36c0f3f4f5') { throw 'The sibling PnP Core checkout must be the immutable 1.18.0 release.' }
    $cpEnvironment.HF_SOURCE_COMMIT = $cpCommit
    foreach ($cpName in $cpEnvironment.Keys) {
        $cpSavedEnvironment[$cpName] = [Environment]::GetEnvironmentVariable($cpName, 'Process')
        [Environment]::SetEnvironmentVariable($cpName, $cpEnvironment[$cpName], 'Process')
    }
    New-Item -ItemType Directory -Path $cpEnvironment.TEMP -Force | Out-Null
    if (-not $NoRestore) { Invoke-PageDotnet -Arguments (@('restore', $cpTests) + $cpFramework) -Log (Join-Path $cpOutput 'restore.log') }
    $cpAssets = Get-Content -LiteralPath (Join-Path (Split-Path $cpCore -Parent) 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $cpPackages = @($cpAssets.packageFolders.Keys)[0]
    $cpGrpc = Join-Path $cpPackages 'grpc.tools/2.61.0'
    $cpPlatform = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macosx' } else { 'linux' }
    $cpArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $cpProtoc = Join-Path $cpGrpc "tools/$($cpPlatform)_$cpArch/protoc$(if ($IsWindows) { '.exe' })"
    $cpPlugin = Join-Path $cpGrpc "tools/$($cpPlatform)_$cpArch/grpc_csharp_plugin$(if ($IsWindows) { '.exe' })"
    if (-not (Test-Path -LiteralPath $cpProtoc) -or -not (Test-Path -LiteralPath $cpPlugin)) { throw 'The resolved Grpc.Tools compiler/plugin are unavailable.' }
    $cpBuild = $cpFramework + @("-p:Protobuf_ProtocFullPath=$cpProtoc", "-p:gRPC_PluginFullPath=$cpPlugin", "-p:Protobuf_StandardImportsPath=$(Join-Path $cpGrpc 'build/native/include')")
    $cpArguments = @('test', $cpTests, '-c', 'Release', '--no-restore') + $cpBuild
    if ($Suite -eq 'Pages') { $cpArguments += @('--filter', 'Category=ClassicPagePipeline|Category=HFFoundation') }
    $cpArguments += @('--logger', 'trx;LogFileName=results.trx', '--results-directory', $cpOutput)
    Invoke-PageDotnet -Arguments $cpArguments -Log (Join-Path $cpOutput 'tests.log')
    $cpTools = Join-Path $cpOutput 'tools'
    $cpEf = Join-Path $cpTools "dotnet-ef$(if ($IsWindows) { '.exe' })"
    if (-not (Test-Path -LiteralPath $cpEf)) { Invoke-PageDotnet -Arguments @('tool', 'install', 'dotnet-ef', '--version', '8.0.3', '--tool-path', $cpTools) -Log (Join-Path $cpOutput 'ef-install.log') }
    & $cpEf migrations has-pending-model-changes --project $cpCore --startup-project $cpProcess --configuration Release --framework net8.0 --no-build *> (Join-Path $cpOutput 'model-check.log')
    if ($LASTEXITCODE -ne 0) { throw 'EF model and committed migrations differ; see model-check.log.' }
    [xml]$cpTrx = Get-Content -LiteralPath (Join-Path $cpOutput 'results.trx') -Raw
    $cpCases = @($cpTrx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $cpScenarios = @('T28', 'T29', 'T30', 'T31', 'T32', 'T33') + @(1..13 | ForEach-Object { 'CP{0:D2}' -f $_ })
    $cpAcceptance = foreach ($cpScenario in $cpScenarios) {
        $cpRows = @($cpCases | Where-Object { $_.testName -like "*$($cpScenario)_*" })
        if ($cpRows.Count -eq 0 -or @($cpRows | Where-Object outcome -ne 'Passed').Count -gt 0) { throw "Missing or unsuccessful evidence for $cpScenario." }
        @{ scenario = $cpScenario; result = 'PASS'; tests = @($cpRows | ForEach-Object testName) }
    }
    $cpReceipt = @{
        kind = 'Classic Pages pipeline verification'; result = 'PASS'; suite = $Suite; sourceCommit = $cpCommit
        foundationCommit = '823e852a6a57275b8905a64c7cad63819d156f69'; workingTreeClean = ($cpDirty.Count -eq 0)
        runtime = @{ sdk = $cpSdk; targetFramework = 'net8.0'; efTool = '8.0.3'; pnpCoreTag = '1.18.0'; pnpCoreCommit = $cpDependency }
        resolvedLibraries = @($cpAssets.libraries.Keys | Sort-Object)
        tests = @{ total = $cpCases.Count; passed = @($cpCases | Where-Object outcome -eq 'Passed').Count; failed = @($cpCases | Where-Object outcome -ne 'Passed').Count }
        acceptance = @($cpAcceptance); modelCheck = 'PASS'; liveTenant = 'NOT_RUN'; handler = 'OUT_OF_SCOPE'
        artifacts = @('results.trx', 'tests.log', 'model-check.log', 'classicpage-native-receipt.json', 'classicpage-native-assessment.db', 'hf-fixture-receipt.json', 'hf-native-assessment.db') | ForEach-Object {
            @{ file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $cpOutput $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        completedUtc = [DateTime]::UtcNow.ToString('O')
    }
    $cpReceipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $cpOutput 'verification-receipt.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $cpOutput 'tests.log') -Tail 7 | Write-Host
    Write-Host "Classic Pages receipt: $(Join-Path $cpOutput 'verification-receipt.json')"
}
finally {
    foreach ($cpName in $cpSavedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($cpName, $cpSavedEnvironment[$cpName], 'Process') }
    Pop-Location
}
