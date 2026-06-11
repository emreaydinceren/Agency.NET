[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solutionPath = Join-Path $PSScriptRoot '..\Agency.slnx'
$resolvedSolutionPath = (Resolve-Path -LiteralPath $solutionPath).Path

$arguments = @(
    'test'
    $resolvedSolutionPath
    '--filter'
    'Category=Functional'
) + $DotnetArgs

& dotnet @arguments
exit $LASTEXITCODE
