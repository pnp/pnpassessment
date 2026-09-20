[CmdletBinding()]
param(
    [switch]$Live,
    [string]$Tenant,
    [Guid]$ApplicationId = [Guid]::Empty,
    [string]$TenantId,
    [ValidateSet('Application', 'Device', 'Interactive')]
    [string]$AuthMode = 'Application',
    [string]$CertPath,
    [string]$CertFile,
    [SecureString]$CertPassword,
    [string[]]$Sites,
    [string[]]$ExpectedPageUrl,
    [switch]$HomePageOnly,
    [switch]$FailOnCoverageGap,
    [Guid]$ExistingScanId = [Guid]::Empty,
    [int]$Threads = 2,
    [int]$TimeoutMinutes = 120,
    [string]$ArtifactsPath,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'src\PnP.Scanning\PnP.Scanning.Core.Tests\PnP.Scanning.Core.Tests.csproj'
$processProject = Join-Path $repoRoot 'src\PnP.Scanning\PnP.Scanning.Process\PnP.Scanning.Process.csproj'
$pnpCoreProject = Join-Path (Split-Path -Parent $repoRoot) 'pnpcore\src\sdk\PnP.Core\PnP.Core.csproj'

if (-not $ArtifactsPath) {
    $ArtifactsPath = Join-Path $repoRoot 'artifacts\native-aspx-validation'
}
if (-not $ReportPath) {
    $ReportPath = Join-Path $repoRoot ('artifacts\native-aspx-live\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Read-RequiredValue {
    param([Parameter(Mandatory)][string]$Prompt)

    do { $value = Read-Host $Prompt } while ([string]::IsNullOrWhiteSpace($value))
    return $value.Trim()
}

function ConvertFrom-SecureValue {
    param([Parameter(Mandatory)][SecureString]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Initialize-Sqlite {
    param([Parameter(Mandatory)][string]$ExecutableDirectory)

    $nativeDirectory = Join-Path $ExecutableDirectory 'runtimes\win-x64\native'
    if (Test-Path -LiteralPath $nativeDirectory) {
        $env:PATH = $nativeDirectory + [IO.Path]::PathSeparator + $env:PATH
    }
    foreach ($assembly in @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )) {
        $path = Join-Path $ExecutableDirectory $assembly
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required SQLite assembly was not found: $path"
        }
        Add-Type -Path $path
    }
    [SQLitePCL.Batteries_V2]::Init()
}

function Get-ScanStatus {
    param(
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][Guid]$ScanId
    )

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Database;Mode=ReadOnly;Pooling=False")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT Status FROM Scans WHERE lower(ScanId)=lower($scanId)'
        [void]$command.Parameters.AddWithValue('$scanId', $ScanId.ToString('D'))
        $value = $command.ExecuteScalar()
        if ($null -eq $value -or $value -is [DBNull]) { return $null }
        return [int]$value
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-Assessment {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $captured = [Collections.Generic.List[string]]::new()
    & $Executable @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $captured.Add($line)
        Write-Host $line
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($Arguments[0]) exited with code $LASTEXITCODE."
    }
    return $captured.ToArray()
}

if (-not (Test-Path -LiteralPath $pnpCoreProject)) {
    throw "PnP Core must be checked out beside pnpassessment. Missing: $pnpCoreProject"
}

New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null
Write-Host 'Running the offline native discovery regression suite...'
Invoke-DotNet @(
    'test', $testProject,
    '--configuration', 'Release',
    '--artifacts-path', $ArtifactsPath,
    '--filter', 'Category=NativeScanIntegration',
    '--logger', 'console;verbosity=minimal'
)

if (-not $Live) {
    Write-Host 'Offline validation passed. Re-run with -Live to perform an authenticated scan.' -ForegroundColor Green
    return
}

Write-Host 'Building the assessment executable used by the live validation...'
Invoke-DotNet @(
    'build', $processProject,
    '--configuration', 'Release',
    '--artifacts-path', $ArtifactsPath,
    '--no-restore'
)

$assessmentExe = Get-ChildItem -LiteralPath $ArtifactsPath -Recurse -File -Filter 'microsoft365-assessment.exe' |
    Where-Object FullName -Match 'PnP\.Scanning\.Process' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $assessmentExe) {
    throw "microsoft365-assessment.exe was not found under $ArtifactsPath."
}
$executableDirectory = Split-Path -Parent $assessmentExe
Initialize-Sqlite -ExecutableDirectory $executableDirectory

$scanId = $ExistingScanId
if ($scanId -eq [Guid]::Empty) {
    if ([string]::IsNullOrWhiteSpace($Tenant)) {
        $Tenant = Read-RequiredValue 'SharePoint tenant host (for example contoso.sharepoint.com)'
    }
    while ($ApplicationId -eq [Guid]::Empty) {
        $candidate = Read-RequiredValue 'Entra application id'
        if (-not [Guid]::TryParse($candidate, [ref]$ApplicationId)) {
            Write-Warning 'Enter a valid GUID.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($TenantId)) {
        $TenantId = Read-Host 'Entra tenant id (blank lets the assessment resolve it)'
    }
    if (-not $Sites -or $Sites.Count -eq 0) {
        $siteInput = Read-Host 'Site collection URL(s), comma-separated (blank runs tenant enumeration)'
        if (-not [string]::IsNullOrWhiteSpace($siteInput)) {
            $Sites = @($siteInput.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
                ForEach-Object { $_.Trim() })
        }
    }

    $plainCertificatePassword = $null
    $startArguments = $null
    try {
        $startArguments = [Collections.Generic.List[string]]::new()
        foreach ($value in @(
            'start', '--mode', 'Classic', '--classicinclude', 'Pages', '--tenant', $Tenant,
            '--applicationid', $ApplicationId.ToString('D'), '--authmode', $AuthMode,
            '--threads', $Threads.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--skipusageinformation'
        )) { $startArguments.Add([string]$value) }
        if (-not [string]::IsNullOrWhiteSpace($TenantId)) {
            $startArguments.Add('--tenantid')
            $startArguments.Add($TenantId)
        }
        if ($Sites -and $Sites.Count -gt 0) {
            $startArguments.Add('--siteslist')
            $startArguments.Add(($Sites -join ','))
        }
        if ($HomePageOnly) { $startArguments.Add('--homepageonly') }

        if ($AuthMode -eq 'Application') {
            if ([string]::IsNullOrWhiteSpace($CertPath) -and [string]::IsNullOrWhiteSpace($CertFile)) {
                $CertPath = Read-Host "Certificate store path (recommended, for example My|CurrentUser|thumbprint; blank uses a PFX)"
                if ([string]::IsNullOrWhiteSpace($CertPath)) {
                    $CertFile = Read-RequiredValue 'PFX file path'
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
                $startArguments.Add('--certpath')
                $startArguments.Add($CertPath)
            }
            else {
                $resolvedCertFile = (Resolve-Path -LiteralPath $CertFile).Path
                $startArguments.Add('--certfile')
                $startArguments.Add($resolvedCertFile)
                if ($null -eq $CertPassword) {
                    $CertPassword = Read-Host 'PFX password' -AsSecureString
                }
                $plainCertificatePassword = ConvertFrom-SecureValue $CertPassword
                $startArguments.Add('--certpassword')
                $startArguments.Add($plainCertificatePassword)
            }
        }

        Write-Host 'Starting the live Classic Pages assessment...'
        $startOutput = Invoke-Assessment -Executable $assessmentExe -Arguments $startArguments.ToArray()
    }
    finally {
        if ($null -ne $startArguments) {
            $passwordIndex = $startArguments.IndexOf('--certpassword')
            if ($passwordIndex -ge 0 -and $passwordIndex + 1 -lt $startArguments.Count) {
                $startArguments[$passwordIndex + 1] = ''
            }
            $startArguments.Clear()
        }
        $plainCertificatePassword = $null
    }

    $startText = $startOutput -join "`n"
    $scanMatch = [regex]::Match($startText,
        '(?i)Assessment id\s*=\s*(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})')
    if (-not $scanMatch.Success) {
        throw 'The assessment started, but its ScanId could not be parsed from the command output.'
    }
    $scanId = [Guid]$scanMatch.Groups['id'].Value
}
else {
    Write-Host "Reusing existing ScanId: $scanId"
}

$scanDatabase = Join-Path $executableDirectory (Join-Path $scanId.ToString('D') 'assessment.db')
Write-Host "ScanId: $scanId"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$lastStatus = $null
do {
    if ((Get-Date) -ge $deadline) {
        throw "Timed out after $TimeoutMinutes minutes waiting for assessment $scanId."
    }
    if (Test-Path -LiteralPath $scanDatabase) {
        try { $status = Get-ScanStatus -Database $scanDatabase -ScanId $scanId }
        catch { $status = $null }
        if ($null -ne $status -and $status -ne $lastStatus) {
            $statusName = @{ 1 = 'Queued'; 2 = 'Running'; 3 = 'Finished'; 4 = 'Pausing'; 5 = 'Paused'; 6 = 'Terminated' }[$status]
            Write-Host "Assessment status: $statusName"
            $lastStatus = $status
        }
        if ($status -eq 3) { break }
        if ($status -in 5, 6) { throw "Assessment ended in status $statusName." }
    }
    Start-Sleep -Seconds 10
} while ($true)

New-Item -ItemType Directory -Force -Path $ReportPath | Out-Null
Write-Host 'Exporting the native CSV report...'
Invoke-Assessment -Executable $assessmentExe -Arguments @(
    'report', '--id', $scanId.ToString('D'), '--mode', 'CsvOnly', '--path', $ReportPath, '--open', 'false'
) | Out-Null

$discoveryCsv = Get-ChildItem -LiteralPath $ReportPath -Recurse -File -Filter 'discovery.csv' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $discoveryCsv) { throw "discovery.csv was not produced under $ReportPath." }

$rows = @(Import-Csv -LiteralPath $discoveryCsv)
$pages = @($rows | Where-Object RowType -EQ 'Page')
if ($pages.Count -eq 0) { throw 'The live scan did not discover any physical ASPX pages.' }

$missingIdentity = @($pages | Where-Object {
    [string]::IsNullOrWhiteSpace($_.SiteCollectionId) -or
    [string]::IsNullOrWhiteSpace($_.WebId) -or
    [string]::IsNullOrWhiteSpace($_.FileUniqueId)
})
if ($missingIdentity.Count -gt 0) {
    throw "$($missingIdentity.Count) discovered page row(s) are missing SiteCollectionId, WebId or FileUniqueId."
}

$duplicateIdentity = @($pages | Group-Object {
    ($_.SiteCollectionId + '|' + $_.WebId + '|' + $_.FileUniqueId).ToLowerInvariant()
} | Where-Object Count -GT 1)
if ($duplicateIdentity.Count -gt 0) {
    throw "$($duplicateIdentity.Count) duplicate Site/Web/file identity group(s) were found."
}

$duplicateUrl = @($pages | Group-Object {
    ($_.SiteUrl + '|' + $_.WebUrl + '|' + $_.Url).ToLowerInvariant()
} | Where-Object Count -GT 1)
if ($duplicateUrl.Count -gt 0) {
    throw "$($duplicateUrl.Count) duplicate physical URL group(s) were found."
}

$lostOwnership = @($pages | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.ListItemId) -and [string]::IsNullOrWhiteSpace($_.ListId)
})
if ($lostOwnership.Count -gt 0) {
    throw "$($lostOwnership.Count) list-backed page row(s) lost their owning ListId."
}

$unfinished = @($pages | Where-Object { [string]::IsNullOrWhiteSpace($_.AssessmentStatus) })
if ($unfinished.Count -gt 0) {
    throw "$($unfinished.Count) page row(s) have no terminal AssessmentStatus."
}

if ($HomePageOnly) {
    $selectedHomePages = @($pages | Where-Object {
        $_.HomePage -eq 'True' -and $_.AssessmentStatus -ne 'NotSelected'
    })
    if ($selectedHomePages.Count -eq 0) {
        throw '--homepageonly produced no selected HomePage row.'
    }
}

foreach ($expected in $ExpectedPageUrl) {
    if (-not ($pages.Url -contains $expected)) {
        throw "Expected physical page was not discovered: $expected"
    }
}

$gapStatuses = @('Partial', 'Denied', 'Failed', 'Cancelled', 'Unknown', 'Pending')
$coverageGaps = @($rows | Where-Object {
    $_.RowType -eq 'Scope' -and $_.DiscoveryStatus -in $gapStatuses
})
if ($coverageGaps.Count -gt 0) {
    $coverageGaps | Select-Object ScopeType, Url, DiscoveryStatus, ErrorCodes, ErrorDetail |
        Format-Table -AutoSize | Out-Host
    if ($FailOnCoverageGap) {
        throw "$($coverageGaps.Count) incomplete discovery scope row(s) were found."
    }
    Write-Warning "$($coverageGaps.Count) incomplete discovery scope row(s) remain; inspect discovery.csv."
}

Write-Host "Live ASPX validation passed. Pages: $($pages.Count); discovery.csv: $discoveryCsv" -ForegroundColor Green
