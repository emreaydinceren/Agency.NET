[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solutionPath = Join-Path $PSScriptRoot '..\Agency.slnx'
$resolvedSolutionPath = (Resolve-Path -LiteralPath $solutionPath).Path
$proxyProject = Join-Path $PSScriptRoot '..\Utils\Agency.Utils.HttpCacheProxy\Agency.Utils.HttpCacheProxy.csproj'

Write-Host 'Starting HTTP Cache Proxy...'
$proxy = Start-Process -FilePath 'dotnet' `
    -ArgumentList 'run', '--project', $proxyProject, '--configuration', 'Release', '--no-build' `
    -PassThru -NoNewWindow

$ready = $false
for ($i = 1; $i -le 20; $i++) {
    Start-Sleep -Seconds 1
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new('localhost', 12345)
        $tcp.Close()
        Write-Host "Proxy ready after ${i}s"
        $ready = $true
        break
    }
    catch { }
}

if (-not $ready) {
    Write-Warning 'Proxy did not start within 20s — running tests without cache.'
}

try {
    $arguments = @(
        'test'
        $resolvedSolutionPath
        '--filter'
        'Category=Functional'
    ) + $DotnetArgs

    & dotnet @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    if (-not $proxy.HasExited) {
        $proxy.Kill($true)
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}