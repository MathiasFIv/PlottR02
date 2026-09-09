<#
.SYNOPSIS
    Generates code coverage + an HTML report for the PlottR0.2 solution.

.DESCRIPTION
    Runs the test suite with the XPlat Code Coverage data collector (coverlet),
    then invokes ReportGenerator to turn the resulting Cobertura XML files into
    a local HTML report.

    Report-only: this script never fails the build on coverage thresholds.

    The HTML report is local-only and must never be committed (see .gitignore).

.PARAMETER Configuration
    Build configuration to test against. Defaults to Release.

.EXAMPLE
    ./scripts/coverage.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Resolve the repo root from this script's location.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$coverageDir = Join-Path $repoRoot 'coverage'
$resultsDir  = Join-Path $coverageDir 'results'
$reportDir   = Join-Path $coverageDir 'report'
$runSettings = Join-Path $repoRoot 'coverage.runsettings'
$solution    = Join-Path $repoRoot 'PlottR0.2.slnx'

Write-Host "== Repo root : $repoRoot"
Write-Host "== Coverage  : $coverageDir"

try {
    # 1. Restore local .NET tools (reportgenerator) from .config/dotnet-tools.json.
    Write-Host '== Restoring local .NET tools (reportgenerator)...'
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit code $LASTEXITCODE)." }

    # 2. Clean the coverage output directory so re-runs are idempotent.
    Write-Host '== Cleaning coverage output directory...'
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
        'Cobertura'
        'TextSummary'
    )

    $reportsGlob = (Join-Path $resultsDir '**/coverage.cobertura.xml')

    & dotnet reportgenerator `
        "-reports:$reportsGlob" `
        "-targetdir:$reportDir" `
        "-reporttypes:$($reportTypes -join ';')" `
        '-verbosity:Info'
    if ($LASTEXITCODE -ne 0) { throw "reportgenerator failed (exit code $LASTEXITCODE)." }

    # 5. Print where the report landed.
    $indexFile = Join-Path $reportDir 'index.html'
    if (Test-Path -LiteralPath $indexFile) {
        Write-Host ''
        Write-Host '== Coverage report generated:'
        Write-Host "   $indexFile"
        Write-Host "   (open in a browser: `"$indexFile`")"
    }
    else {
        Write-Warning "reportgenerator completed but index.html was not found at $reportDir"
    }
}
catch {
    Write-Error "Coverage generation failed: $($_.Exception.Message)"
    exit 1
}
