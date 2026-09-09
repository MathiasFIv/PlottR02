<#
.SYNOPSIS
    DEVELOPER-ONLY: generate a local code-coverage HTML report and open it.

.DESCRIPTION
    This script is a convenience tool for developers to view coverage on their
    own machine. It is NOT part of the CI/CD pipeline (CI inlines its own steps
    in .github/workflows/ci.yml; this script is never invoked there).

    What it does:
      1. Restores the local .NET tool (reportgenerator) from .config/dotnet-tools.json
      2. Runs the test suite with the XPlat Code Coverage collector (coverlet)
      3. Generates a fresh HTML report with ReportGenerator
      4. Opens the report in your default browser

    It always regenerates from scratch, so you never see a stale report.

.PARAMETER Configuration
    Build configuration to test against. Defaults to Release.

.PARAMETER NoOpen
    Generate the report but do NOT auto-open the browser.

.EXAMPLE
    ./scripts/dev/coverage.ps1

.EXAMPLE
    ./scripts/dev/coverage.ps1 -Configuration Debug -NoOpen
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'

# Resolve the repo root from this script's location (scripts/dev -> repo root).
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$coverageDir = Join-Path $repoRoot 'coverage'
$resultsDir  = Join-Path $coverageDir 'results'
$reportDir   = Join-Path $coverageDir 'report'
$runSettings = Join-Path $repoRoot 'coverage.runsettings'
$solution    = Join-Path $repoRoot 'PlottR0.2.slnx'
$indexFile   = Join-Path $reportDir 'index.html'

Write-Host "== Repo root : $repoRoot"
Write-Host "== Coverage  : $coverageDir"

try {
    # 1. Restore local .NET tools (reportgenerator) from the manifest.
    Write-Host '== Restoring local .NET tools (reportgenerator)...'
    Push-Location $repoRoot
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit code $LASTEXITCODE)." }
    Pop-Location

    # 2. Clean the WHOLE coverage dir so re-runs are never stale.
    Write-Host '== Cleaning previous coverage output...'
    if (Test-Path -LiteralPath $coverageDir) {
        Remove-Item -LiteralPath $coverageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

    # 3. Run tests with the XPlat Code Coverage collector.
    Write-Host '== Running tests with XPlat Code Coverage collector...'
    & dotnet test $solution `
        --configuration $Configuration `
        --collect:"XPlat Code Coverage" `
        --settings $runSettings `
        --results-directory $resultsDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit code $LASTEXITCODE)." }

    # 4. Convert all Cobertura XML reports into a single HTML report.
    Write-Host '== Generating HTML report with ReportGenerator...'
    $coberturaFiles = Get-ChildItem -Path $resultsDir -Recurse -Filter 'coverage.cobertura.xml' -File
    if (-not $coberturaFiles) {
        throw 'No coverage.cobertura.xml files found under ./coverage/results.'
    }

    $reportTypes = @(
        'HtmlInline_AzurePipelines_Dark'
        'TextSummary'
    )

    $reportsGlob = (Join-Path $resultsDir '**/coverage.cobertura.xml')

    & dotnet reportgenerator `
        "-reports:$reportsGlob" `
        "-targetdir:$reportDir" `
        "-reporttypes:$($reportTypes -join ';')" `
        '-verbosity:Info'
    if ($LASTEXITCODE -ne 0) { throw "reportgenerator failed (exit code $LASTEXITCODE)." }

    # 5. Report outcome + summary.
    $summaryFile = Join-Path $reportDir 'Summary.txt'
    if (Test-Path -LiteralPath $summaryFile) {
        Write-Host ''
        Write-Host '== Coverage summary:'
        Get-Content -LiteralPath $summaryFile
    }

    Write-Host ''
    Write-Host '== Coverage report:'
    Write-Host "   $indexFile"

    # 6. Open the report in the default browser (unless -NoOpen).
    if (-not $NoOpen) {
        if (Test-Path -LiteralPath $indexFile) {
            Write-Host '   Opening in your default browser...'
            Start-Process -FilePath $indexFile
        }
        else {
            Write-Warning "index.html not found at $reportDir; report created but not auto-opened."
        }
    }
}
catch {
    Write-Error "Coverage generation failed: $($_.Exception.Message)"
    exit 1
}
