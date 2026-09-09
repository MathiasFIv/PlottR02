#!/usr/bin/env bash
#
# coverage.sh — cross-platform coverage + HTML report for the PlottR0.2 solution.
#
# Linux/macOS counterpart to scripts/coverage.ps1:
#   1. dotnet tool restore        (reportgenerator from .config/dotnet-tools.json)
#   2. clean ./coverage
#   3. dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings
#   4. reportgenerator -> HTML report at ./coverage/report
#
# Report-only: this script never fails on coverage thresholds.
# The HTML report is local-only (see .gitignore — coverage/ is ignored).
#
# Usage: ./scripts/coverage.sh [Debug|Release]
set -euo pipefail

CONFIGURATION="${1:-Release}"

# Resolve the repo root from this script's location.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

COVERAGE_DIR="$REPO_ROOT/coverage"
RESULTS_DIR="$COVERAGE_DIR/results"
REPORT_DIR="$COVERAGE_DIR/report"
RUN_SETTINGS="$REPO_ROOT/coverage.runsettings"
SOLUTION="$REPO_ROOT/PlottR0.2.slnx"

echo "== Repo root : $REPO_ROOT"
echo "== Coverage  : $COVERAGE_DIR"

# 1. Restore local .NET tools (reportgenerator) from the manifest.
echo "== Restoring local .NET tools (reportgenerator)..."
dotnet tool restore

# 2. Clean the coverage output directory so re-runs are idempotent.
echo "== Cleaning coverage output directory..."
rm -rf "$COVERAGE_DIR"
mkdir -p "$RESULTS_DIR" "$REPORT_DIR"

# 3. Run tests with the XPlat Code Coverage collector.
echo "== Running tests with XPlat Code Coverage collector..."
dotnet test "$SOLUTION" \
    --configuration "$CONFIGURATION" \
    --collect:"XPlat Code Coverage" \
    --settings "$RUN_SETTINGS" \
    --results-directory "$RESULTS_DIR"

# 4. Convert all Cobertura XML reports into a single HTML report.
echo "== Generating HTML report with ReportGenerator..."
if ! ls "$RESULTS_DIR"/**/coverage.cobertura.xml >/dev/null 2>&1; then
    echo "ERROR: No coverage.cobertura.xml files found under ./coverage/results." >&2
    exit 1
fi

dotnet reportgenerator \
    "-reports:$RESULTS_DIR/**/coverage.cobertura.xml" \
    "-targetdir:$REPORT_DIR" \
    "-reporttypes:HtmlInline_AzurePipelines_Dark;Cobertura;TextSummary" \
    "-verbosity:Info"

# 5. Print where the report landed.
INDEX_FILE="$REPORT_DIR/index.html"
if [ -f "$INDEX_FILE" ]; then
    echo ""
    echo "== Coverage report generated:"
    echo "   $INDEX_FILE"
    echo "   (open in a browser: \"$INDEX_FILE\")"
else
    echo "WARNING: reportgenerator completed but index.html was not found in $REPORT_DIR" >&2
fi
