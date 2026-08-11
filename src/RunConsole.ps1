<#
.SYNOPSIS
    Visitor-facing quickstart script for the Agency demo console app.
.DESCRIPTION
    First run: walks you through SetupConsole.ps1's interactive configuration
    (LLM endpoint, optional GitHub tools), then builds and launches the
    Agency.Harness.Console demo. Once .quickstart.json exists, later runs
    reuse those saved answers automatically and skip straight to build/launch
    - pass -Reconfigure to run through the questions again.

    Run from src\: .\RunConsole.ps1

    Force re-running the interactive setup: .\RunConsole.ps1 -Reconfigure

    Non-interactive / CI usage:
        .\RunConsole.ps1 -NonInteractive -BaseUrl <url> -Model <model> -ApiKey <key>

    Preview what would happen without building or launching anything:
        .\RunConsole.ps1 -DryRun -NonInteractive -BaseUrl <url> -Model <model> -ApiKey <key>
#>

param(
    [string]$BaseUrl,
    [string]$Model,
    [string]$ApiKey,
    [string]$GitHubToken,
    [switch]$DryRun,
    [switch]$NonInteractive,
    [switch]$Reconfigure
)

# Resolve the absolute path to the project directory (script lives in src\)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$setupScriptPath = Join-Path $scriptDir "SetupConsole.ps1"
$savedConfigPath = Join-Path $scriptDir ".quickstart.json"

# ── Run setup (interactively the first time, silently thereafter) ───────────
# Dot-sourced (not `&`-invoked) so the environment variables SetupConsole.ps1
# sets - and the $SetupFailed signal it uses instead of `exit` - land in this
# script's scope rather than a throwaway child scope.

$needsInteractiveSetup = $Reconfigure -or -not (Test-Path $savedConfigPath)

$setupArgs = @{
    NonInteractive = ($NonInteractive -or -not $needsInteractiveSetup)
}
if ($BaseUrl) { $setupArgs.BaseUrl = $BaseUrl }
if ($Model) { $setupArgs.Model = $Model }
if ($ApiKey) { $setupArgs.ApiKey = $ApiKey }
if ($GitHubToken) { $setupArgs.GitHubToken = $GitHubToken }
if ($DryRun) { $setupArgs.DryRun = $true }

. $setupScriptPath @setupArgs

if ($SetupFailed) {
    exit 1
}

# ── Build & launch ────────────────────────────────────────────────────────────

$consoleProjectRelative = "Harness\Agency.Harness.Console\Agency.Harness.Console.csproj"
$consoleOutputDir = Join-Path $scriptDir "Harness\Agency.Harness.Console\bin\Release\net10.0"
$buildCommandDisplay = "dotnet build `"$consoleProjectRelative`" --configuration Release"
# Launch from the build-output directory: shared-appsettings.json is a linked file that only lands
# next to appsettings.json in the output, and the host resolves config relative to its working
# directory - running from the source project folder would miss the shared file and fail at startup.
$runCommandDisplay = "dotnet Agency.Harness.Console.dll  (from $consoleOutputDir)"

if ($DryRun) {
    Write-Title "🔍 Dry run - here's what I would do"
    Write-Info "Build : $buildCommandDisplay"
    Write-Info "Launch: $runCommandDisplay"
    Write-Host ""
    Write-Ok "✅ Dry run complete. No build or launch was performed."
    exit 0
}

Write-Title "🔨 Building..."
Push-Location $scriptDir
try {
    dotnet build $consoleProjectRelative --configuration Release
    $buildExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($buildExitCode -ne 0) {
    Write-Warn "❌ Build failed (exit code $buildExitCode). See the output above for details."
    exit 1
}

Write-Ok "✅ Build succeeded."

Write-Host ""
Write-Host "🚀 Launching the Agency console - type your message, or /exit to quit." -ForegroundColor Green
Push-Location $consoleOutputDir
try {
    dotnet Agency.Harness.Console.dll
} finally {
    Pop-Location
}
