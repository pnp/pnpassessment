#Requires -RunAsAdministrator

# Sign the binaries
Write-Host "Signing the binaries..."
q:\github\SharePointPnP\CodeSigning\PnP\sign-pnpbinaries.ps1 -SignJson pnpassessment