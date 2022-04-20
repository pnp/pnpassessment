#!/usr/bin/env pwsh

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

#docfx metadata ./dev/docs/docfx.json --warningsAsErrors $args
docfx build ./main/docs/docfx.json --warningsAsErrors $args

# Copy the created site to the pnpcoredocs folder (= clone of the gh-pages branch)
Remove-Item ./gh-pages/using-the-assessment-tool/* -Recurse -Force
Remove-Item ./gh-pages/sharepoint-syntex/* -Recurse -Force
Remove-Item ./gh-pages/contributing/* -Recurse -Force
Remove-Item ./gh-pages/images/* -Recurse -Force
copy-item -Force -Recurse ./main/docs/_site/* -Destination ./gh-pages
