<#
.SYNOPSIS
    Visitor-facing quickstart script for the Agency demo console app.
.DESCRIPTION
    Walks you through configuring an LLM endpoint (and, optionally, GitHub
    tools), then builds and launches the Agency.Harness.Console demo. Nothing
    is written to appsettings or user-secrets - configuration is applied as
    process environment variables for this run only.

    Run from src\: .\RunConsole.ps1

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
    [switch]$NonInteractive
)

# ── Helpers ──────────────────────────────────────────────────────────────────

function Write-Title { param([string]$Text) Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Info  { param([string]$Text) Write-Host "  $Text" -ForegroundColor White }
function Write-Ok    { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Skip  { param([string]$Text) Write-Host "  $Text" -ForegroundColor DarkGray }
function Write-Warn  { param([string]$Text) Write-Host "  $Text" -ForegroundColor Yellow }

# Resolve the absolute path to the project directory (script lives in src\)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$savedConfigPath = Join-Path $scriptDir ".quickstart.json"

function Get-MaskedSecret {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return "(not set)" }
    if ($Value.Length -le 3) { return "***" }
    return "$($Value.Substring(0, 3))***"
}

# Cheap, non-blocking check for whether something is listening on a local port.
function Test-PortOpen {
    param([string]$ComputerName, [int]$Port, [int]$TimeoutMs = 300)
    $client = $null
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $iar = $client.BeginConnect($ComputerName, $Port, $null, $null)
        return ($iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false) -and $client.Connected)
    } catch {
        return $false
    } finally {
        if ($client) { $client.Close() }
    }
}

# Returns the explicit param if given, otherwise the default (never prompting
# when -NonInteractive is set).
function Resolve-Answer {
    param(
        [string]$ParamValue,
        [string]$Default,
        [string]$PromptText
    )
    if ($ParamValue) { return $ParamValue }
    if ($NonInteractive) { return $Default }
    $answer = Read-Host "$PromptText [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer
}

# ── Load previously saved (non-secret) answers, if any ──────────────────────

$saved = $null
if (Test-Path $savedConfigPath) {
    try { $saved = Get-Content $savedConfigPath -Raw | ConvertFrom-Json } catch { $saved = $null }
}

# ── Welcome ──────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "🤖 👋 Hi! I'm the Agency quickstart - let's get the demo console running." -ForegroundColor Cyan
Write-Host "   I'll ask a couple of quick questions, then build and launch it for you." -ForegroundColor White
Write-Host "   Just press Enter on any question to accept the suggested default." -ForegroundColor DarkGray
Write-Host ""

# ── Preflight: dotnet SDK ────────────────────────────────────────────────────

Write-Title "⚙️  Preflight check"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Warn "❌ I couldn't find 'dotnet' on your PATH."
    Write-Info "Install the .NET SDK (v10 or later) from https://dotnet.microsoft.com/download, then run this script again."
    exit 1
}

$dotnetVersionRaw = $null
try { $dotnetVersionRaw = (& dotnet --version 2>$null | Select-Object -Last 1) } catch { $dotnetVersionRaw = $null }

$dotnetMajor = 0
if ($dotnetVersionRaw) {
    try { $dotnetMajor = [int]($dotnetVersionRaw.Split('.')[0]) } catch { $dotnetMajor = 0 }
}

if ($dotnetMajor -lt 10) {
    Write-Warn "❌ Found dotnet SDK v$dotnetVersionRaw, but this demo needs .NET 10 or later."
    Write-Info "Grab the latest SDK from https://dotnet.microsoft.com/download, then run this script again."
    exit 1
}

Write-Ok "✅ Found .NET SDK v$dotnetVersionRaw - good to go!"

# ── Interview: LLM base URL ──────────────────────────────────────────────────

Write-Title "🔌 Where does your LLM live?"
Write-Info "This is the OpenAI-compatible base URL the agent will send model requests to."
Write-Info "A local server like LM Studio (port 1234) or Ollama (port 11434) needs no real"
Write-Info "API key. A cloud provider (any OpenAI-compatible endpoint) will need your real key."

$defaultBaseUrl = "http://llm.test:1234/v1"
if (-not $BaseUrl) {
    if ($saved -and $saved.BaseUrl) {
        $defaultBaseUrl = $saved.BaseUrl
        Write-Skip "💡 Using the base URL you saved last time as the suggested default."
    } elseif (-not $NonInteractive) {
        if (Test-PortOpen -ComputerName "localhost" -Port 1234) {
            $defaultBaseUrl = "http://localhost:1234/v1"
            Write-Skip "💡 Found something answering on localhost:1234 (looks like LM Studio) - suggesting it."
        } elseif (Test-PortOpen -ComputerName "localhost" -Port 11434) {
            $defaultBaseUrl = "http://localhost:11434/v1"
            Write-Skip "💡 Found something answering on localhost:11434 (looks like Ollama) - suggesting it."
        }
    }
}

$resolvedBaseUrl = Resolve-Answer -ParamValue $BaseUrl -Default $defaultBaseUrl -PromptText "  Base URL"

# ── Interview: model name ────────────────────────────────────────────────────

Write-Title "🧠 Which model should it use?"
Write-Info "This must be a model your endpoint actually serves - check LM Studio's or"
Write-Info "Ollama's loaded model, or your cloud provider's model list, if unsure."

$defaultModel = "google/gemma-4-e2b"
if (-not $Model -and $saved -and $saved.Model) { $defaultModel = $saved.Model }

$resolvedModel = Resolve-Answer -ParamValue $Model -Default $defaultModel -PromptText "  Model name"

# ── Interview: API key ───────────────────────────────────────────────────────

Write-Title "🔑 API key"
Write-Info "Local servers (LM Studio, Ollama) usually accept any dummy value here."
Write-Info "A cloud provider will need your real API key."

$defaultApiKey = "lm-studio"
if (-not $ApiKey -and $env:AGENCY_API_KEY) {
    $defaultApiKey = $env:AGENCY_API_KEY
    Write-Skip "💡 Using AGENCY_API_KEY from your environment as the default."
}

$resolvedApiKey = Resolve-Answer -ParamValue $ApiKey -Default $defaultApiKey -PromptText "  API key"

# ── Interview: optional GitHub MCP tool ──────────────────────────────────────

Write-Title "🔌 Optional: GitHub tools"
Write-Info "The demo can expose GitHub tools (issues, PRs, repos) to the agent via the"
Write-Info "official GitHub MCP server. That needs Docker running locally and a GitHub"
Write-Info "Personal Access Token."

$defaultGitHubEnabled = $false
if ($saved -and $null -ne $saved.GitHubEnabled) { $defaultGitHubEnabled = [bool]$saved.GitHubEnabled }

$resolvedGitHubToken = $null
if ($GitHubToken) {
    $githubEnabled = $true
    $resolvedGitHubToken = $GitHubToken
} else {
    $defaultAnswer = if ($defaultGitHubEnabled) { "Y" } else { "N" }
    if ($NonInteractive) {
        $githubEnabled = $defaultGitHubEnabled
    } else {
        $answer = Read-Host "  Enable GitHub tools? (y/N) [$defaultAnswer]"
        if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $defaultAnswer }
        $githubEnabled = $answer.Trim().ToUpper() -eq "Y"
    }

    if ($githubEnabled) {
        if ($env:GITHUB_PERSONAL_ACCESS_TOKEN) {
            $resolvedGitHubToken = $env:GITHUB_PERSONAL_ACCESS_TOKEN
            Write-Skip "💡 Using GITHUB_PERSONAL_ACCESS_TOKEN from your environment."
        } elseif (-not $NonInteractive) {
            $resolvedGitHubToken = Read-Host "  GitHub Personal Access Token"
        }
    }
}

if ($githubEnabled) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Warn "⚠️  docker wasn't found on your PATH - GitHub tools will be skipped at runtime."
        Write-Warn "   That's safe, the app degrades gracefully without them."
    }
    if (-not $resolvedGitHubToken) {
        Write-Warn "⚠️  No GitHub token available - GitHub tools will be skipped at runtime."
    } else {
        Write-Ok "✅ GitHub tools enabled."
    }
} else {
    Write-Skip "Skipped GitHub tools."
}

# ── Apply configuration as process environment variables ────────────────────

Write-Title "⚙️  Applying configuration for this run"

$claudeBaseUrl = $resolvedBaseUrl -replace '/v1/?$', ''

$env:LLmClients__OpenAI__BaseUrl = $resolvedBaseUrl
$env:LLmClients__Claude__BaseUrl = $claudeBaseUrl
$env:LLmClients__ApiKey = $resolvedApiKey
$env:Agent__DefaultModel = $resolvedModel
if ($githubEnabled -and $resolvedGitHubToken) {
    $env:GITHUB_PERSONAL_ACCESS_TOKEN = $resolvedGitHubToken
}

Write-Info "LLmClients__OpenAI__BaseUrl  = $resolvedBaseUrl"
Write-Info "LLmClients__Claude__BaseUrl  = $claudeBaseUrl"
Write-Info "LLmClients__ApiKey           = $(Get-MaskedSecret $resolvedApiKey)"
Write-Info "Agent__DefaultModel          = $resolvedModel"
if ($githubEnabled -and $resolvedGitHubToken) {
    Write-Info "GITHUB_PERSONAL_ACCESS_TOKEN = $(Get-MaskedSecret $resolvedGitHubToken)"
}

# ── Persist the non-secret answers for next time ─────────────────────────────

if (-not $DryRun) {
    $toSave = [ordered]@{
        BaseUrl       = $resolvedBaseUrl
        Model         = $resolvedModel
        GitHubEnabled = $githubEnabled
    }
    try {
        $toSave | ConvertTo-Json | Set-Content -Path $savedConfigPath -Encoding UTF8
    } catch {
        Write-Warn "⚠️  Couldn't save your answers to $savedConfigPath for next time."
    }
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
