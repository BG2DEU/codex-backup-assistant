param(
    [string]$Version = "1.0.0-preview.1",
    [string]$ReleaseRoot = "artifacts\release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "artifacts\desktop-preview"
$releaseDir = Join-Path $repoRoot (Join-Path $ReleaseRoot "CodexBackupAssistant-$Version")
$exeSource = Join-Path $publishDir "CodexBackup.App.exe"
$exeTarget = Join-Path $releaseDir "CodexBackupAssistant.exe"
$releaseRootFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ReleaseRoot))
$releaseDirFull = [System.IO.Path]::GetFullPath($releaseDir)
$zipPath = "$releaseDirFull.zip"
$zipHashPath = Join-Path $releaseRootFull "CodexBackupAssistant-$Version.zip.sha256.txt"

if (-not $releaseDirFull.StartsWith($releaseRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a release directory outside the release root."
}

dotnet publish (Join-Path $repoRoot "src\CodexBackup.App\CodexBackup.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Copy-Item -LiteralPath $exeSource -Destination $exeTarget -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination (Join-Path $releaseDir "README.md") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "SECURITY.md") -Destination (Join-Path $releaseDir "SECURITY.md") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "docs") -Destination (Join-Path $releaseDir "docs") -Recurse -Force

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $exeTarget
$hash.Hash | Set-Content -Encoding ASCII -LiteralPath (Join-Path $releaseDir "SHA256.txt")

$manifest = [ordered]@{
    Version = $Version
    Executable = "CodexBackupAssistant.exe"
    Sha256 = $hash.Hash
    CreatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz")
}
$manifest.GetEnumerator() |
    ForEach-Object { "$($_.Key)=$($_.Value)" } |
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $releaseDir "RELEASE-MANIFEST.txt")

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $releaseDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
"$($zipHash.Hash)  $([System.IO.Path]::GetFileName($zipPath))" |
    Set-Content -Encoding ASCII -LiteralPath $zipHashPath

Write-Host "ReleaseDir=$releaseDir"
Write-Host "Exe=$exeTarget"
Write-Host "Sha256=$($hash.Hash)"
Write-Host "Zip=$zipPath"
Write-Host "ZipSha256=$($zipHash.Hash)"
Write-Host "ZipSha256File=$zipHashPath"
