<#
.SYNOPSIS
    Interactive setup script that configures user secrets for the Agency solution.
.DESCRIPTION
    Prompts you for each secret with an explanation of what it's for,
    then writes them to the correct user-secrets folder via `dotnet user-secrets set`.
    Run from src\: .\SetupLocal.ps1
#>

# ── Helpers ──────────────────────────────────────────────────────────────────

function Write-Title { param([string]$Text) Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Info  { param([string]$Text) Write-Host "  $Text" -ForegroundColor White }
function Write-Ok    { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Skip   { param([string]$Text) Write-Host "  $Text" -ForegroundColor DarkGray }

# Resolve the absolute path to the project directory (script lives in src\)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

# Determine whether to use -p (project dir) or -id (secret GUID) per project
function Invoke-Secret {
    param(
        [string]$ProjectDir,       # relative to src\ (e.g. "..\Agency.Console")
        [string]$SecretId,         # UserSecretsId from the csproj
        [string]$Key,
        [string]$Value
    )
    $fullProjectDir = Join-Path $scriptDir $ProjectDir
    dotnet user-secrets set -p $fullProjectDir $Key $Value 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Set '$Key' in $SecretId"
    } else {
        Write-Host "  WARNING: Failed to set secret for $SecretId (is the project restored?)" -ForegroundColor Yellow
    }
}

# ── 1. PostgreSQL connection string ──────────────────────────────────────────

Write-Title "PostgreSQL Connection String"
Write-Info "Used by: Sql.Postgres.Test, GraphRAG.Code.Postgres.Test,"
Write-Info "         VectorStore.Sql.Postgres.Test, KeyValueStore.Sql.Postgres.Test"
Write-Info "These projects share one user-secrets folder: AgencySecrets"
Write-Info "Connection format: Host=<host>;Port=5432;Username=<user>;Password=<pass>;Database=<db>"
Write-Host ""
Write-Host "  (press Enter to use the default dev instance)" -ForegroundColor DarkGray
$connStr = Read-Host "  Connection string"
if (-not $connStr) { $connStr = "Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db" }

Invoke-Secret -ProjectDir "Sql\Agency.Sql.Postgres.Test" -SecretId "AgencySecrets" -Key "ConnectionStrings:PostgreSql" -Value $connStr

# ── 2. LLM API keys ─────────────────────────────────────────────────────────

Write-Title "LLM Test API Keys"
Write-Info "Used by: Llm.Test (functional tests for Claude & OpenAI clients)"
Write-Info "These go into the same AgencySecrets folder."

Write-Host ""
Write-Info "You can leave blank to skip (tests will fail until you set them manually)."

$openAiKey = Read-Host "  OpenAI API Key"
if ($openAiKey) {
    Invoke-Secret -ProjectDir "Llm\Agency.Llm.Test" -SecretId "AgencySecrets" -Key "LlmTest:OpenAI:ApiKey" -Value $openAiKey
} else {
    Write-Skip "Skipped OpenAI key"
}

$claudeKey = Read-Host "  Claude API Key"
if ($claudeKey) {
    Invoke-Secret -ProjectDir "Llm\Agency.Llm.Test" -SecretId "AgencySecrets" -Key "LlmTest:Claude:ApiKey" -Value $claudeKey
} else {
    Write-Skip "Skipped Claude key"
}

# ── 3. OpenTelemetry config (Harness Console) ───────────────────────────────

Write-Title "OpenTelemetry Configuration (Harness.Console)"
Write-Info "Used by: Harness.Console for file-based tracing, metrics, and logging."
Write-Info "These values go into the 'OpenTelemetry' section of that project's user secrets."
Write-Host ""

$serviceName = Read-Host "  Service name"
if (-not $serviceName) { $serviceName = "Agency.Harness.Console" }
Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:ServiceName" -Value $serviceName

$traceEnabled = Read-Host "  Enable trace export? (Y/N)"
$traceEnabled = $traceEnabled.Trim().ToUpper() -eq "Y"
Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Traces:Enabled" -Value $traceEnabled

if ($traceEnabled) {
    $tracePrefix = Read-Host "  Trace file prefix"
    if (-not $tracePrefix) { $tracePrefix = "traces" }
    Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Traces:FilePrefix" -Value $tracePrefix

    $sampling = Read-Host "  Trace sampling ratio (0.0 - 1.0, 1.0 = always)"
    if (-not $sampling) { $sampling = "1.0" }
    Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Traces:SamplingRatio" -Value $sampling
}

$metricEnabled = Read-Host "  Enable metric export? (Y/N)"
$metricEnabled = $metricEnabled.Trim().ToUpper() -eq "Y"
Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Metrics:Enabled" -Value $metricEnabled

if ($metricEnabled) {
    $exportMs = Read-Host "  Metric export interval in ms (default 15000)"
    if (-not $exportMs) { $exportMs = "15000" }
    Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Metrics:ExportIntervalMs" -Value $exportMs
}

$logEnabled = Read-Host "  Enable log (Serilog) export? (Y/N)"
$logEnabled = $logEnabled.Trim().ToUpper() -eq "Y"
Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Logs:Enabled" -Value $logEnabled

if ($logEnabled) {
    $logPrefix = Read-Host "  Log file prefix (default 'app')"
    if (-not $logPrefix) { $logPrefix = "app" }
    Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Logs:FilePrefix" -Value $logPrefix

    $minLevel = Read-Host "  Minimum log level (Verbose/Debug/Information/Warning/Error/Fatal, default Information)"
    if (-not $minLevel) { $minLevel = "Information" }
    Invoke-Secret -ProjectDir "Harness\Agency.Harness.Console" -SecretId "agency-harness-console" -Key "OpenTelemetry:FileExport:Logs:MinimumLevel" -Value $minLevel
}

# ── 4. GitHub Personal Access Token ─────────────────────────────────────────

Write-Title "GitHub Personal Access Token"
Write-Info "Used by: Harness.Console's 'github' MCP server (issues, PRs, repos via github-mcp-server)."
Write-Info "Optional — only needed if you want GitHub tools in the console. Requires Docker."
Write-Info "This goes into the same AgencySecrets folder."
Write-Host ""
Write-Info "You can leave blank to skip (RunConsole.ps1 can still prompt for one per-run instead)."

$githubToken = Read-Host "  GitHub Personal Access Token"
if ($githubToken) {
    Invoke-Secret -ProjectDir "Sql\Agency.Sql.Postgres.Test" -SecretId "AgencySecrets" -Key "GitHub:PersonalAccessToken" -Value $githubToken
} else {
    Write-Skip "Skipped GitHub token"
}

# ── Done ─────────────────────────────────────────────────────────────────────

Write-Title "Done!"
Write-Ok "User secrets configured. You can now run:"
Write-Ok "  dotnet build   src\Agency.slnx"
Write-Ok "  dotnet test    src\Agency.slnx --filter 'Category!=Functional'"
Write-Ok ""
Write-Info "For functional tests with a local LM Studio, also set:"
Write-Info "  dotnet user-secrets set -p src\Llm\Agency.Llm.Test"
Write-Info "  'LlmTest:Endpoint' 'http://llm-host.example:1234'"
Write-Host ""
