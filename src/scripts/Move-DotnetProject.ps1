<#
.SYNOPSIS
    Moves a .NET project to a new location and updates all references.

.DESCRIPTION
    Copies a .NET project directory to a new destination, then updates:
      - .slnx solution files (XML format)
      - .sln solution files (classic format)
      - ProjectReference entries in all .csproj, .fsproj, and .vbproj files

.PARAMETER Source
    Path to the project folder to move (e.g. "src\Agency.Common").

.PARAMETER Destination
    Target folder path for the project (e.g. "src\Shared\Agency.Common").

.PARAMETER SolutionRoot
    Root folder to search for solution and project files. Defaults to the parent of Source.

.PARAMETER DeleteSource
    When specified, removes the original project folder after a successful move.

.PARAMETER WhatIf
    Preview changes without writing any files or deleting anything.

.EXAMPLE
    .\Move-DotnetProject.ps1 -Source src\Agency.Common -Destination src\Shared\Agency.Common -SolutionRoot src -DeleteSource

.EXAMPLE
    .\Move-DotnetProject.ps1 -Source src\Agency.Common -Destination src\Shared\Agency.Common -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$Source,

    [Parameter(Mandatory)]
    [string]$Destination,

    [string]$SolutionRoot,

    [switch]$DeleteSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ──────────────────────────────────────────────────────────────

$SourceAbs      = (Resolve-Path -LiteralPath $Source).Path
$DestinationAbs = [System.IO.Path]::GetFullPath($Destination)

if (-not $SolutionRoot) {
    $SolutionRoot = Split-Path $SourceAbs -Parent
}
$SolutionRootAbs = (Resolve-Path -LiteralPath $SolutionRoot).Path

Write-Host "Source      : $SourceAbs"
Write-Host "Destination : $DestinationAbs"
Write-Host "Search root : $SolutionRootAbs"
Write-Host ""

if ($SourceAbs -eq $DestinationAbs) {
    Write-Error "Source and destination are the same path."
}

# ── Helper: make a relative path between two absolute paths ────────────────────

function Get-RelativePath([string]$From, [string]$To) {
    # Ensure trailing separator so [Uri] treats $From as a directory
    if (-not $From.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $From += [System.IO.Path]::DirectorySeparatorChar
    }
    $fromUri = [Uri]$From
    $toUri   = [Uri]$To
    $rel     = $fromUri.MakeRelativeUri($toUri).ToString()
    # Uri uses forward slashes; convert to OS separator
    return [Uri]::UnescapeDataString($rel).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

# ── Helper: remap an absolute path under the source tree to the new destination ─
#
# Returns the new relative path (from $FromDir) when $AbsPath is inside
# $normalizedSource. Returns $null when $AbsPath does not need remapping.

function Get-RemappedPath([string]$AbsPath, [string]$FromDir) {
    if (-not $AbsPath.StartsWith($normalizedSource, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    $relative   = $AbsPath.Substring($normalizedSource.Length).TrimStart('\', '/')
    $newAbsPath = Join-Path $DestinationAbs $relative
    return Get-RelativePath -From $FromDir -To $newAbsPath
}

# ── Helper: apply regex path-remapping to a text file and save if changed ──────
#
# Finds all regex matches in $File, remaps any that point inside the source tree,
# logs the changes, and writes the file back (respecting -WhatIf).

function Update-TextFilePathRefs {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [System.IO.FileInfo]$File,
        [string]$Pattern,
        [string]$ChangeLabel
    )

    $fileDir        = $File.DirectoryName
    $content        = Get-Content -LiteralPath $File.FullName -Raw
    $script:changed = $false

    $newContent = [regex]::Replace($content, $Pattern, {
        param($match)
        $rawPath = $match.Value
        $absPath = [System.IO.Path]::GetFullPath((Join-Path $fileDir $rawPath))
        $newRel  = Get-RemappedPath -AbsPath $absPath -FromDir $fileDir
        if ($null -eq $newRel) { return $rawPath }

        Write-Host "[$($File.Name)] $ChangeLabel"
        Write-Host "  Old: $rawPath"
        Write-Host "  New: $newRel"

        $script:changed = $true
        return $newRel
    })

    if ($script:changed) {
        if ($PSCmdlet.ShouldProcess($File.FullName, "Save $($File.Name)")) {
            [System.IO.File]::WriteAllText($File.FullName, $newContent, [System.Text.UTF8Encoding]::new($false))
        }
        Write-Host "  Saved $($File.FullName)"
        Write-Host ""
        $script:changed = $false
    }
}

# ── Step 1: Copy project files ─────────────────────────────────────────────────

if (Test-Path -LiteralPath $DestinationAbs) {
    Write-Warning "Destination already exists: $DestinationAbs"
} else {
    if ($PSCmdlet.ShouldProcess($DestinationAbs, 'Create directory')) {
        New-Item -ItemType Directory -Path $DestinationAbs -Force | Out-Null
    }
}

Write-Host "Copying files..."
$items = Get-ChildItem -LiteralPath $SourceAbs -Recurse -Force
foreach ($item in $items) {
    $relativePart  = $item.FullName.Substring($SourceAbs.Length).TrimStart('\', '/')
    $targetPath    = Join-Path $DestinationAbs $relativePart

    if ($item.PSIsContainer) {
        if ($PSCmdlet.ShouldProcess($targetPath, 'Create subdirectory')) {
            New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
        }
    } else {
        if ($PSCmdlet.ShouldProcess($targetPath, 'Copy file')) {
            Copy-Item -LiteralPath $item.FullName -Destination $targetPath -Force
        }
        Write-Verbose "  Copied: $relativePart"
    }
}
Write-Host "  Done."
Write-Host ""

# ── Step 2: Fix outbound ProjectReferences inside the moved project files ──────
#
# The copied project files still contain paths that were relative to the source
# location. References that point to projects that also moved are fine (relative
# distance unchanged). References that point outside the moved folder must be
# re-relativized from the new location.

$movedProjectFiles = Get-ChildItem -LiteralPath $DestinationAbs -Recurse -File |
    Where-Object { $_.Extension -in '.csproj', '.fsproj', '.vbproj' }

$normalizedSource = $SourceAbs.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$normalizedDest   = $DestinationAbs.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

foreach ($proj in $movedProjectFiles) {
    $newProjDir = $proj.DirectoryName

    # Corresponding original directory in the source tree
    $relFromDest = $newProjDir.Substring($DestinationAbs.Length).TrimStart('\', '/')
    $origProjDir = if ($relFromDest) { Join-Path $SourceAbs $relFromDest } else { $SourceAbs }

    $content = Get-Content -LiteralPath $proj.FullName -Raw
    $fileChanged = $false

    $pattern = '(?<=<ProjectReference[^>]*\sInclude=")([^"]+)(?=")'

    $newContent = [regex]::Replace($content, $pattern, {
        param($match)
        $refPathRaw = $match.Value

        # Resolve the reference as if the file were still in its original source location
        $refAbs = [System.IO.Path]::GetFullPath((Join-Path $origProjDir $refPathRaw))

        # Reference points to another project that moved alongside this one → unchanged
        if ($refAbs.StartsWith($normalizedSource, [System.StringComparison]::OrdinalIgnoreCase) -or
            $refAbs.StartsWith($normalizedDest,   [System.StringComparison]::OrdinalIgnoreCase)) {
            return $refPathRaw
        }

        # Reference points outside the moved folder → re-relativize from new location
        $newRelative = Get-RelativePath -From $newProjDir -To $refAbs

        Write-Host "[$($proj.Name)] Fixing outbound ProjectReference:"
        Write-Host "  Old: $refPathRaw"
        Write-Host "  New: $newRelative"

        $script:fileChanged = $true
        return $newRelative
    })

    if ($script:fileChanged -or $fileChanged) {
        if ($PSCmdlet.ShouldProcess($proj.FullName, 'Fix outbound ProjectReferences in moved file')) {
            [System.IO.File]::WriteAllText($proj.FullName, $newContent, [System.Text.UTF8Encoding]::new($false))
        }
        Write-Host "  Saved $($proj.FullName)"
        Write-Host ""
        $script:fileChanged = $false
    }
}

# ── Step 3: Update .slnx files ────────────────────────────────────────────────

$slnxFiles = Get-ChildItem -LiteralPath $SolutionRootAbs -Filter '*.slnx' -Recurse -File

foreach ($slnx in $slnxFiles) {
    [xml]$xml   = Get-Content -LiteralPath $slnx.FullName -Raw
    $slnxDir    = $slnx.DirectoryName
    $changed    = $false

    $nodes = $xml.SelectNodes('//Project[@Path]')
    foreach ($node in $nodes) {
        $projPathRaw = $node.GetAttribute('Path')
        $projAbs         = [System.IO.Path]::GetFullPath((Join-Path $slnxDir $projPathRaw))
        $newProjRelative = Get-RemappedPath -AbsPath $projAbs -FromDir $slnxDir

        if ($null -ne $newProjRelative) {
            Write-Host "[$($slnx.Name)] Updating project path:"
            Write-Host "  Old: $projPathRaw"
            Write-Host "  New: $newProjRelative"

            if ($PSCmdlet.ShouldProcess($slnx.FullName, "Update path '$projPathRaw' -> '$newProjRelative'")) {
                $node.SetAttribute('Path', $newProjRelative)
                $changed = $true
            }
        }
    }

    if ($changed) {
        if ($PSCmdlet.ShouldProcess($slnx.FullName, 'Save .slnx')) {
            $settings                      = [System.Xml.XmlWriterSettings]::new()
            $settings.Indent               = $true
            $settings.IndentChars          = '  '
            $settings.OmitXmlDeclaration   = $true
            $settings.NewLineChars         = "`n"
            $settings.Encoding             = [System.Text.UTF8Encoding]::new($false)  # no BOM

            $writer = [System.Xml.XmlWriter]::Create($slnx.FullName, $settings)
            try { $xml.Save($writer) } finally { $writer.Dispose() }
        }
        Write-Host "  Saved $($slnx.FullName)"
    }
    Write-Host ""
}

# ── Step 4: Update .sln files ─────────────────────────────────────────────────

$slnFiles = Get-ChildItem -LiteralPath $SolutionRootAbs -Filter '*.sln' -Recurse -File

$slnPattern = '(?<=Project\("[^"]*"\)\s*=\s*"[^"]*",\s*")([^"]+\.(?:csproj|fsproj|vbproj))(?=")'
foreach ($sln in $slnFiles) {
    Update-TextFilePathRefs -File $sln -Pattern $slnPattern -ChangeLabel 'Updating project path:'
}

# ── Step 5: Update ProjectReferences in all project files ─────────────────────

$projectFiles = Get-ChildItem -LiteralPath $SolutionRootAbs -Recurse -File |
    Where-Object { $_.Extension -in '.csproj', '.fsproj', '.vbproj' }

$projRefPattern = '(?<=<ProjectReference[^>]*\sInclude=")([^"]+)(?=")'
foreach ($proj in $projectFiles) {
    Update-TextFilePathRefs -File $proj -Pattern $projRefPattern -ChangeLabel 'Updating ProjectReference:'
}

# ── Step 6: Optionally delete source ──────────────────────────────────────────

if ($DeleteSource) {
    if ($PSCmdlet.ShouldProcess($SourceAbs, 'Delete original project folder')) {
        Remove-Item -LiteralPath $SourceAbs -Recurse -Force
        Write-Host "Deleted original folder: $SourceAbs"
    }
}

Write-Host ""
Write-Host "Move complete."
