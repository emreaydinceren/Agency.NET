<#
.SYNOPSIS
    Interactive configuration step for the Agency demo console app.
.DESCRIPTION
    Walks you through configuring an LLM endpoint (and, optionally, GitHub
    tools), then applies the answers as process environment variables and
    saves the non-secret answers to .quickstart.json for next time. Nothing
    is written to appsettings or user-secrets. Does not build or launch
    anything - see RunConsole.ps1 for that.

    Can be run directly to (re)configure: ./SetupConsole.ps1
    Normally you don't need to call this yourself - RunConsole.ps1 calls it
    automatically the first time, and reuses your saved answers after that.

    Non-interactive / CI usage:
        ./SetupConsole.ps1 -NonInteractive -BaseUrl <url> -Model <model> -ApiKey <key>

    Preview what would happen without saving anything:
        ./SetupConsole.ps1 -DryRun -NonInteractive -BaseUrl <url> -Model <model> -ApiKey <key>
#>

param(
    [string]$BaseUrl,
    [string]$Model,
    [string]$EmbeddingModel,
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

# Set (and left as $false when everything goes fine) so a caller that dot-sources
# this script can tell whether to abort - using `exit` here would kill the caller's
# whole session/process instead of just this script when dot-sourced.
$SetupFailed = $false

function Get-MaskedSecret {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return "(not set)" }
    if ($Value.Length -le 3) { return "***" }
    return "$($Value.Substring(0, 3))***"
}

# Safely reads an optional property from the saved-answers object. Older
# .quickstart.json files won't have newer fields (e.g. EmbeddingModel was added
# after BaseUrl/Model/GitHubEnabled) - plain dot-access on a missing property
# throws under Set-StrictMode, which some users' profiles enable globally.
function Get-SavedProperty {
    param($Saved, [string]$Name)
    if (-not $Saved) { return $null }
    $prop = $Saved.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
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
Write-Host "🤖 👋 Hi! I'm the Agency quickstart - let's get the demo console configured." -ForegroundColor Cyan
Write-Host "   I'll ask a couple of quick questions, then apply the settings for this run." -ForegroundColor White
Write-Host "   Just press Enter on any question to accept the suggested default." -ForegroundColor DarkGray
Write-Host ""

# ── Preflight: dotnet SDK ────────────────────────────────────────────────────

Write-Title "⚙️  Preflight check"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Warn "❌ I couldn't find 'dotnet' on your PATH."
    Write-Info "Install the .NET SDK (v10 or later) from https://dotnet.microsoft.com/download, then run this script again."
    $SetupFailed = $true
    return
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
    $SetupFailed = $true
    return
}

Write-Ok "✅ Found .NET SDK v$dotnetVersionRaw - good to go!"

# ── Interview: LLM base URL ──────────────────────────────────────────────────

Write-Title "🔌 Where does your LLM live?"
Write-Info "This is the OpenAI-compatible base URL the agent will send model requests to."
Write-Info "Examples - match the pattern, not just the port:"
Write-Info "  LM Studio -> http://localhost:1234/v1"
Write-Info "  Ollama    -> http://localhost:11434/v1"
Write-Info "  OpenAI    -> https://api.openai.com/v1"
Write-Info "A local server (LM Studio/Ollama) needs no real API key; a cloud provider will."
Write-Info ""
Write-Info "Careful: LM Studio's own UI shows you a curl example for ITS OWN native API, e.g."
Write-Info "'http://localhost:1234/api/v1/chat' - that is NOT what goes here. Drop the '/api'"
Write-Info "and everything after '/v1': use 'http://localhost:1234/v1' (same host and port)."

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

while ($resolvedBaseUrl -match '/api/v1(/.*)?$') {
    Write-Warn "⚠️  LM Studio does support the OpenAI API - just not at this path. '/api/v1' (and"
    Write-Warn "   anything under it, like '/api/v1/chat') is LM Studio's own native REST API - the"
    Write-Warn "   'quick copy curl' snippet in its UI, with a different request/response shape than"
    Write-Warn "   OpenAI's. Its OpenAI-compatible endpoint lives at '/v1' instead (e.g."
    Write-Warn "   'http://localhost:1234/v1') - Agency needs that one; '/api/v1...' 404s here."

    if ($NonInteractive) { break }

    $suggestedFix = $resolvedBaseUrl -replace '/api/v1(/.*)?$', '/v1'
    $resolvedBaseUrl = Resolve-Answer -ParamValue $null -Default $suggestedFix -PromptText "  Base URL (re-enter - suggested fix shown)"
}

if ($resolvedBaseUrl -notmatch '/v1/?$') {
    $suggestedFix = "$($resolvedBaseUrl.TrimEnd('/'))/v1"
    Write-Warn "⚠️  That URL doesn't end in '/v1' - LM Studio and Ollama serve their OpenAI-compatible"
    Write-Warn "   chat API there (e.g. 'http://localhost:1234/v1'). Without it, requests land on a route"
    Write-Warn "   these servers don't recognize - and LM Studio, in particular, responds 200 anyway with"
    Write-Warn "   an empty/garbled result instead of a clear 404, so a missing '/v1' is very easy to miss."

    if ($NonInteractive) {
        $resolvedBaseUrl = $suggestedFix
        Write-Warn "   Auto-appending '/v1' -> $resolvedBaseUrl"
    } else {
        $resolvedBaseUrl = Resolve-Answer -ParamValue $null -Default $suggestedFix -PromptText "  Base URL (re-enter - suggested fix shown)"
    }
}

# ── Interview: model name ────────────────────────────────────────────────────

Write-Title "🧠 Which model should it use?"
Write-Info "This must be a model your endpoint actually serves - check LM Studio's or"
Write-Info "Ollama's loaded model, or your cloud provider's model list, if unsure."

$defaultModel = "google/gemma-4-e2b"
if (-not $Model -and $saved -and $saved.Model) { $defaultModel = $saved.Model }

$resolvedModel = Resolve-Answer -ParamValue $Model -Default $defaultModel -PromptText "  Model name"

# ── Interview: embedding model ───────────────────────────────────────────────

Write-Title "🧬 Which embedding model should it use?"
Write-Info "Used by /add-file and /add-folder to index documents for semantic_search."
Write-Info "This must be a *separate* model from the chat model above, actually loaded"
Write-Info "on your endpoint - check LM Studio's or Ollama's loaded models, or your"
Write-Info "cloud provider's embedding model list, if unsure."

$defaultEmbeddingModel = "text-embedding-nomic-embed-text-v1.5"
$savedEmbeddingModel = Get-SavedProperty -Saved $saved -Name 'EmbeddingModel'
if (-not $EmbeddingModel -and $savedEmbeddingModel) { $defaultEmbeddingModel = $savedEmbeddingModel }

$resolvedEmbeddingModel = Resolve-Answer -ParamValue $EmbeddingModel -Default $defaultEmbeddingModel -PromptText "  Embedding model name"

# Best-effort check against the endpoint's /models list. This is exactly the failure
# mode that silently broke /add-file before: an embedding model id configured but
# never loaded, with no error until a chunk upsert fails deep in the ingestion
# pipeline. Never blocks setup - just a heads-up when the endpoint is reachable.
if (-not $DryRun) {
    $modelsUrl = "$($resolvedBaseUrl.TrimEnd('/'))/models"
    try {
        $modelsResponse = Invoke-RestMethod -Uri $modelsUrl -Method Get -TimeoutSec 3 -ErrorAction Stop
        $loadedIds = @($modelsResponse.data | ForEach-Object { $_.id })
        if ($loadedIds.Count -gt 0 -and ($loadedIds -notcontains $resolvedEmbeddingModel)) {
            Write-Warn "⚠️  '$resolvedEmbeddingModel' isn't in the models your endpoint currently reports as loaded."
            Write-Warn "   /add-file and /add-folder will fail until it's loaded (e.g. via LM Studio's 'lms load' command)."
        } elseif ($loadedIds.Count -gt 0) {
            Write-Ok "✅ '$resolvedEmbeddingModel' is loaded and ready."
        }
    } catch {
        Write-Skip "💡 Couldn't reach $modelsUrl to verify the embedding model is loaded - skipping check."
    }
}

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
    Write-Ok "✅ Configuration saved. RunConsole.ps1 will reuse it automatically next time."
} else {
    Write-Ok "✅ Dry run complete. Configuration was not saved."
}
