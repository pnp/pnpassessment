<#
.SYNOPSIS
Formats only changed C# files with the pinned CleanupCode reformat-only profile.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseRef
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $files = @(git diff --name-only --diff-filter=ACM "$BaseRef...HEAD" -- '*.cs')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the changed C# file list.'
    }
    if ($files.Count -eq 0) {
        Write-Host 'No changed C# files.'
        return
    }

    $paths = @($files | ForEach-Object {
        Join-Path $repositoryRoot $_.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    })
    Write-Host "Formatting $($paths.Count) changed C# files."
    dotnet tool run jb -- cleanupcode --profile='Built-in: Reformat Code' --no-updates --no-buildin-settings --verbosity=WARN @paths
    if ($LASTEXITCODE -ne 0) {
        throw "CleanupCode failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
