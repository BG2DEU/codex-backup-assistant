[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Get-TreeHashes {
    param([Parameter(Mandatory = $true)][string[]]$Roots)

    $hashes = @{}
    foreach ($root in $Roots) {
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)) {
            $hashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $hashes
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$probePath = Join-Path $repositoryRoot 'tools\Invoke-DiscoveryProbe.ps1'
$fixtureRoot = Join-Path $env:TEMP ('codex-backup-discovery-smoke-' + [Guid]::NewGuid().ToString('N'))
$projectsRoot = Join-Path $fixtureRoot 'Projects'
$projectRoot = Join-Path $projectsRoot '演示项目'
$codexRoot = Join-Path $fixtureRoot '.codex'
$outputPath = Join-Path $fixtureRoot 'output\discovery.json'

try {
    [void](New-Item -ItemType Directory -Path $projectRoot -Force)
    [void](New-Item -ItemType Directory -Path (Join-Path $codexRoot 'sessions\2026\06\17') -Force)
    [void](New-Item -ItemType Directory -Path (Join-Path $codexRoot 'cache') -Force)

    Set-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Encoding UTF8 -Value '{"name":"fixture"}'
    Set-Content -LiteralPath (Join-Path $projectRoot 'notes.txt') -Encoding UTF8 -Value 'untracked fixture file'
    Set-Content -LiteralPath (Join-Path $projectRoot '.env') -Encoding UTF8 -Value 'FIXTURE_SECRET=DO_NOT_EXPORT_CONTENT'
    Set-Content -LiteralPath (Join-Path $codexRoot 'sessions\2026\06\17\session.jsonl') -Encoding UTF8 -Value '{"type":"fixture"}'
    Set-Content -LiteralPath (Join-Path $codexRoot 'auth.json') -Encoding UTF8 -Value '{"token":"AUTH_SENTINEL_MUST_NOT_APPEAR"}'
    Set-Content -LiteralPath (Join-Path $codexRoot 'config.toml') -Encoding UTF8 -Value 'model = "fixture"'
    Set-Content -LiteralPath (Join-Path $codexRoot 'cache\rebuildable.tmp') -Encoding UTF8 -Value 'cache'

    & git -C $projectRoot init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize Git fixture.'
    }

    $sourceRoots = @($projectRoot, $codexRoot)
    $beforeHashes = Get-TreeHashes -Roots $sourceRoots

    & $probePath `
        -ProjectRoots @($projectsRoot) `
        -CodexRoot $codexRoot `
        -MaxDiscoveryDepth 4 `
        -LargeFileThresholdBytes 1 `
        -OutputPath $outputPath | Out-Null

    Assert-True -Condition (Test-Path -LiteralPath $outputPath -PathType Leaf) -Message 'Probe did not create the explicit output file.'

    $jsonText = Get-Content -LiteralPath $outputPath -Raw -Encoding UTF8
    $report = $jsonText | ConvertFrom-Json
    Assert-True -Condition ($report.schemaVersion -eq '1.0') -Message 'Unexpected report schema version.'
    Assert-True -Condition ($report.summary.projectCount -eq 1) -Message 'Expected exactly one project.'
    Assert-True -Condition ($report.summary.gitProjectCount -eq 1) -Message 'Expected one Git project.'
    Assert-True -Condition ($report.summary.gitProjectsWithChanges -eq 1) -Message 'Expected the fixture repository to have changes.'
    Assert-True -Condition ($report.summary.gitProjectsWithoutRemote -eq 1) -Message 'Expected the fixture repository to have no remote.'

    $sessionsItem = @($report.codex.items | Where-Object name -eq 'sessions')
    $authItem = @($report.codex.items | Where-Object name -eq 'auth.json')
    $cacheItem = @($report.codex.items | Where-Object name -eq 'cache')
    Assert-True -Condition ($sessionsItem.Count -eq 1 -and $sessionsItem[0].policy -eq 'IncludePortableAndNative') -Message 'Sessions policy is incorrect.'
    Assert-True -Condition ($authItem.Count -eq 1 -and $authItem[0].policy -eq 'ExcludeCredential') -Message 'Authentication policy is incorrect.'
    Assert-True -Condition ($cacheItem.Count -eq 1 -and $cacheItem[0].policy -eq 'ExcludeVolatile') -Message 'Cache policy is incorrect.'
    Assert-True -Condition (-not $jsonText.Contains('AUTH_SENTINEL_MUST_NOT_APPEAR')) -Message 'Authentication content leaked into the report.'
    Assert-True -Condition (-not $jsonText.Contains('DO_NOT_EXPORT_CONTENT')) -Message 'Project secret content leaked into the report.'

    $afterHashes = Get-TreeHashes -Roots $sourceRoots
    Assert-True -Condition ($beforeHashes.Count -eq $afterHashes.Count) -Message 'Source file count changed during discovery.'
    foreach ($path in $beforeHashes.Keys) {
        Assert-True -Condition ($afterHashes.ContainsKey($path)) -Message "Source file disappeared: $path"
        Assert-True -Condition ($beforeHashes[$path] -eq $afterHashes[$path]) -Message "Source file content changed: $path"
    }

    [pscustomobject][ordered]@{
        status = 'Passed'
        projectCount = $report.summary.projectCount
        codexItemCount = @($report.codex.items).Count
        sourceFileCount = $beforeHashes.Count
        warningCount = $report.summary.warningCount
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

