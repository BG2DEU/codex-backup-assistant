[CmdletBinding()]
param(
    [string[]]$ProjectRoots = @(
        (Join-Path $HOME 'Documents'),
        (Join-Path $HOME 'Desktop')
    ),
    [string]$CodexRoot = (Join-Path $HOME '.codex'),
    [ValidateRange(1, 32)]
    [int]$MaxDiscoveryDepth = 6,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$LargeFileThresholdBytes = 1GB,
    [string]$OutputPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Warnings = New-Object 'System.Collections.Generic.List[object]'
$script:IgnoredDiscoveryDirectories = @(
    '.git', 'node_modules', '.venv', 'venv', 'dist', 'build', 'target',
    '__pycache__', '.idea', '.vs', '.gradle', '.next', '.nuxt'
)
$script:ExactSecretNames = @(
    '.env', '.env.local', '.env.production', '.env.development',
    'id_rsa', 'id_ed25519', 'auth.json', '.cockpit_codex_auth.json'
)
$script:SecretExtensions = @('.pfx', '.p12', '.pem', '.key', '.kdbx')
$script:ExactProjectMarkers = @(
    'package.json', 'pyproject.toml', 'requirements.txt', 'Cargo.toml',
    'go.mod', 'pom.xml', 'build.gradle', 'build.gradle.kts'
)

function Add-ProbeWarning {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Message
    )

    [void]$script:Warnings.Add([pscustomobject][ordered]@{
        stage = $Stage
        path = $Path
        message = $Message
    })
}

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath -eq $pathRoot) {
        return $fullPath
    }

    return $fullPath.TrimEnd([char[]]@('\', '/'))
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $candidatePath = Get-NormalizedPath -Path $Candidate
    $parentPath = Get-NormalizedPath -Path $Parent
    if ($candidatePath.Equals($parentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $parentPath + [System.IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PotentialSecretFileName {
    param([Parameter(Mandatory = $true)][string]$Name)

    foreach ($secretName in $script:ExactSecretNames) {
        if ($Name.Equals($secretName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    $extension = [System.IO.Path]::GetExtension($Name)
    foreach ($secretExtension in $script:SecretExtensions) {
        if ($extension.Equals($secretExtension, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-RelativePathSafe {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPath = (Get-NormalizedPath -Path $Root) + [System.IO.Path]::DirectorySeparatorChar
    $rootUri = New-Object System.Uri($rootPath)
    $pathUri = New-Object System.Uri((Get-NormalizedPath -Path $Path))
    $relative = $rootUri.MakeRelativeUri($pathUri).ToString()
    return [System.Uri]::UnescapeDataString($relative).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Get-SafeTreeStats {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][long]$LargeFileThreshold
    )

    $fileCount = [long]0
    $directoryCount = [long]0
    $totalBytes = [long]0
    $reparsePointCount = [long]0
    $largeFiles = New-Object 'System.Collections.Generic.List[object]'
    $potentialSecretFiles = New-Object 'System.Collections.Generic.List[string]'
    $localWarnings = New-Object 'System.Collections.Generic.List[object]'
    $stack = New-Object System.Collections.Stack
    $stack.Push((Get-NormalizedPath -Path $Root))

    while ($stack.Count -gt 0) {
        $current = [string]$stack.Pop()
        try {
            $entries = [System.IO.Directory]::EnumerateFileSystemEntries($current)
            foreach ($entry in $entries) {
                try {
                    $attributes = [System.IO.File]::GetAttributes($entry)
                    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        $reparsePointCount++
                        continue
                    }

                    if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                        $directoryCount++
                        $stack.Push($entry)
                        continue
                    }

                    $file = New-Object System.IO.FileInfo($entry)
                    $fileCount++
                    $totalBytes += $file.Length
                    $relativePath = Get-RelativePathSafe -Root $Root -Path $file.FullName

                    if ($file.Length -ge $LargeFileThreshold) {
                        [void]$largeFiles.Add([pscustomobject][ordered]@{
                            relativePath = $relativePath
                            bytes = $file.Length
                        })
                    }

                    if (Test-PotentialSecretFileName -Name $file.Name) {
                        [void]$potentialSecretFiles.Add($relativePath)
                    }
                }
                catch {
                    [void]$localWarnings.Add([pscustomobject][ordered]@{
                        path = $entry
                        message = $_.Exception.Message
                    })
                }
            }
        }
        catch {
            [void]$localWarnings.Add([pscustomobject][ordered]@{
                path = $current
                message = $_.Exception.Message
            })
        }
    }

    $largest = @($largeFiles | Sort-Object bytes -Descending | Select-Object -First 20)
    return [pscustomobject][ordered]@{
        fileCount = $fileCount
        directoryCount = $directoryCount
        bytes = $totalBytes
        reparsePointCount = $reparsePointCount
        largeFiles = $largest
        potentialSecretFiles = @($potentialSecretFiles | Sort-Object -Unique)
        warnings = @($localWarnings | ForEach-Object { $_ })
    }
}

function Get-FileStats {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][long]$LargeFileThreshold
    )

    $largeFiles = @()
    if ($File.Length -ge $LargeFileThreshold) {
        $largeFiles = @([pscustomobject][ordered]@{
            relativePath = $File.Name
            bytes = $File.Length
        })
    }

    $potentialSecretFiles = @()
    if (Test-PotentialSecretFileName -Name $File.Name) {
        $potentialSecretFiles = @($File.Name)
    }

    return [pscustomobject][ordered]@{
        fileCount = [long]1
        directoryCount = [long]0
        bytes = [long]$File.Length
        reparsePointCount = [long]0
        largeFiles = $largeFiles
        potentialSecretFiles = $potentialSecretFiles
        warnings = @()
    }
}

function Get-CodexPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$IsDirectory
    )

    $lowerName = $Name.ToLowerInvariant()

    if ($lowerName -like 'auth.json*' -or
        $lowerName -like '.cockpit_codex_auth.json*' -or
        $lowerName -eq '.sandbox-secrets' -or
        $lowerName -eq 'installation_id' -or
        $lowerName -eq 'cap_sid' -or
        $lowerName -like 'chrome-native-hosts*.json') {
        return 'ExcludeCredential'
    }

    if ($IsDirectory -and $lowerName -in @(
        '.tmp', 'tmp', 'cache', '.sandbox', '.sandbox-bin', 'browser',
        'computer-use', 'node_repl', 'process_manager', 'ambient-suggestions'
    )) {
        return 'ExcludeVolatile'
    }

    if ($lowerName -in @('sessions', 'archived_sessions')) {
        return 'IncludePortableAndNative'
    }

    if ($lowerName -in @(
        'rules', 'skills', 'memories', 'generated_images', 'plugins', 'sqlite'
    ) -or
        $lowerName -eq 'agents.md' -or
        $lowerName -like 'config.toml*' -or
        $lowerName -like '.codex-global-state.json*' -or
        $lowerName -like 'session_index.jsonl*' -or
        $lowerName -like 'goals_*.sqlite*' -or
        $lowerName -like 'logs_*.sqlite*' -or
        $lowerName -like 'memories_*.sqlite*' -or
        $lowerName -like 'state_*.sqlite*') {
        return 'Include'
    }

    if ($lowerName -eq 'models_cache.json' -or $lowerName -eq 'vendor_imports') {
        return 'InventoryOnly'
    }

    return 'UnknownReviewRequired'
}

function Get-CodexInventory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][long]$LargeFileThreshold
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        Add-ProbeWarning -Stage 'codex' -Path $Root -Message 'Codex root was not found.'
        return [pscustomobject][ordered]@{
            found = $false
            root = $Root
            items = @()
            totals = [pscustomobject][ordered]@{ fileCount = 0; bytes = 0 }
        }
    }

    $items = New-Object 'System.Collections.Generic.List[object]'
    $totalFiles = [long]0
    $totalBytes = [long]0

    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop | Sort-Object Name)) {
        $isDirectory = $item.PSIsContainer
        try {
            if ($isDirectory) {
                $stats = Get-SafeTreeStats -Root $item.FullName -LargeFileThreshold $LargeFileThreshold
            }
            else {
                $stats = Get-FileStats -File $item -LargeFileThreshold $LargeFileThreshold
            }

            $totalFiles += $stats.fileCount
            $totalBytes += $stats.bytes
            foreach ($warning in $stats.warnings) {
                Add-ProbeWarning -Stage 'codex-stats' -Path $warning.path -Message $warning.message
            }

            [void]$items.Add([pscustomobject][ordered]@{
                name = $item.Name
                kind = if ($isDirectory) { 'Directory' } else { 'File' }
                policy = Get-CodexPolicy -Name $item.Name -IsDirectory $isDirectory
                fileCount = $stats.fileCount
                directoryCount = $stats.directoryCount
                bytes = $stats.bytes
                reparsePointCount = $stats.reparsePointCount
                potentialSecretFileCount = @($stats.potentialSecretFiles).Count
                largeFiles = @($stats.largeFiles)
            })
        }
        catch {
            Add-ProbeWarning -Stage 'codex-item' -Path $item.FullName -Message $_.Exception.Message
        }
    }

    return [pscustomobject][ordered]@{
        found = $true
        root = (Get-NormalizedPath -Path $Root)
        items = @($items | ForEach-Object { $_ })
        totals = [pscustomobject][ordered]@{
            fileCount = $totalFiles
            bytes = $totalBytes
        }
    }
}

function Get-ProjectMarkerReasons {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $reasons = New-Object 'System.Collections.Generic.List[string]'
    if (Test-Path -LiteralPath (Join-Path $Directory '.git')) {
        [void]$reasons.Add('GitRepository')
    }

    try {
        foreach ($file in [System.IO.Directory]::EnumerateFiles($Directory)) {
            $name = [System.IO.Path]::GetFileName($file)
            if ($name -in $script:ExactProjectMarkers -or
                $name.EndsWith('.sln', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.csproj', [System.StringComparison]::OrdinalIgnoreCase)) {
                [void]$reasons.Add('Marker:' + $name)
            }
        }
    }
    catch {
        Add-ProbeWarning -Stage 'project-markers' -Path $Directory -Message $_.Exception.Message
    }

    return @($reasons | ForEach-Object { $_ } | Sort-Object -Unique)
}

function Find-ProjectCandidates {
    param(
        [Parameter(Mandatory = $true)][string[]]$Roots,
        [Parameter(Mandatory = $true)][int]$MaxDepth
    )

    $candidateMap = @{}

    foreach ($rootInput in $Roots) {
        if ([string]::IsNullOrWhiteSpace($rootInput)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $rootInput -PathType Container)) {
            Add-ProbeWarning -Stage 'project-root' -Path $rootInput -Message 'Project root was not found.'
            continue
        }

        $root = Get-NormalizedPath -Path $rootInput
        $queue = New-Object 'System.Collections.Generic.Queue[object]'
        $queue.Enqueue([pscustomobject]@{ path = $root; depth = 0 })

        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            $currentPath = [string]$current.path
            $currentDepth = [int]$current.depth
            $reasons = @(Get-ProjectMarkerReasons -Directory $currentPath)

            if ($reasons.Count -gt 0) {
                $key = $currentPath.ToLowerInvariant()
                if (-not $candidateMap.ContainsKey($key)) {
                    $candidateMap[$key] = [pscustomobject][ordered]@{
                        path = $currentPath
                        reasons = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                        isGit = $false
                    }
                }

                foreach ($reason in $reasons) {
                    [void]$candidateMap[$key].reasons.Add($reason)
                    if ($reason -eq 'GitRepository') {
                        $candidateMap[$key].isGit = $true
                    }
                }
            }

            if ($currentDepth -ge $MaxDepth) {
                continue
            }

            try {
                foreach ($child in [System.IO.Directory]::EnumerateDirectories($currentPath)) {
                    try {
                        $name = [System.IO.Path]::GetFileName($child)
                        if ($name -in $script:IgnoredDiscoveryDirectories) {
                            continue
                        }

                        $attributes = [System.IO.File]::GetAttributes($child)
                        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                            continue
                        }

                        $queue.Enqueue([pscustomobject]@{
                            path = $child
                            depth = $currentDepth + 1
                        })
                    }
                    catch {
                        Add-ProbeWarning -Stage 'project-child' -Path $child -Message $_.Exception.Message
                    }
                }
            }
            catch {
                Add-ProbeWarning -Stage 'project-discovery' -Path $currentPath -Message $_.Exception.Message
            }
        }
    }

    $allCandidates = @($candidateMap.Values)
    $gitRoots = @($allCandidates | Where-Object isGit | ForEach-Object path)
    $selected = New-Object 'System.Collections.Generic.List[object]'

    foreach ($candidate in $allCandidates) {
        if (-not $candidate.isGit) {
            $insideGitRoot = $false
            foreach ($gitRoot in $gitRoots) {
                if (Test-PathWithin -Candidate $candidate.path -Parent $gitRoot) {
                    $insideGitRoot = $true
                    break
                }
            }

            if ($insideGitRoot) {
                continue
            }
        }

        [void]$selected.Add([pscustomobject][ordered]@{
            path = $candidate.path
            reasons = @($candidate.reasons | ForEach-Object { $_ } | Sort-Object)
            isGit = [bool]$candidate.isGit
        })
    }

    return @($selected | ForEach-Object { $_ } | Sort-Object path)
}

function Invoke-GitReadOnly {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousOptionalLocks = $env:GIT_OPTIONAL_LOCKS
    try {
        $env:GIT_OPTIONAL_LOCKS = '0'
        $output = @(& git -C $Repository @Arguments 2>$null)
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            exitCode = $exitCode
            output = $output
        }
    }
    finally {
        $env:GIT_OPTIONAL_LOCKS = $previousOptionalLocks
    }
}

function Get-GitSummary {
    param([Parameter(Mandatory = $true)][string]$Repository)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Add-ProbeWarning -Stage 'git' -Path $Repository -Message 'Git executable was not found.'
        return [pscustomobject][ordered]@{ available = $false }
    }

    $branchResult = Invoke-GitReadOnly -Repository $Repository -Arguments @('branch', '--show-current')
    $statusResult = Invoke-GitReadOnly -Repository $Repository -Arguments @('status', '--porcelain=v1', '-uall')
    $remoteResult = Invoke-GitReadOnly -Repository $Repository -Arguments @('remote')

    if ($statusResult.exitCode -ne 0) {
        Add-ProbeWarning -Stage 'git-status' -Path $Repository -Message 'Git status failed.'
    }

    $statusLines = @($statusResult.output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $untrackedCount = @($statusLines | Where-Object { $_.StartsWith('??') }).Count
    $remoteCount = @($remoteResult.output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count

    return [pscustomobject][ordered]@{
        available = $true
        branch = if ($branchResult.output.Count -gt 0) { [string]$branchResult.output[0] } else { '' }
        changedEntryCount = $statusLines.Count
        untrackedEntryCount = $untrackedCount
        remoteCount = $remoteCount
        hasRemote = ($remoteCount -gt 0)
    }
}

function Get-ProjectInventory {
    param(
        [Parameter(Mandatory = $true)][object[]]$Candidates,
        [Parameter(Mandatory = $true)][long]$LargeFileThreshold
    )

    $projects = New-Object 'System.Collections.Generic.List[object]'
    foreach ($candidate in $Candidates) {
        Write-Progress -Activity 'Scanning project metadata' -Status $candidate.path
        try {
            $stats = Get-SafeTreeStats -Root $candidate.path -LargeFileThreshold $LargeFileThreshold
            foreach ($warning in $stats.warnings) {
                Add-ProbeWarning -Stage 'project-stats' -Path $warning.path -Message $warning.message
            }

            $git = if ($candidate.isGit) {
                Get-GitSummary -Repository $candidate.path
            }
            else {
                [pscustomobject][ordered]@{ available = $false }
            }

            [void]$projects.Add([pscustomobject][ordered]@{
                name = [System.IO.Path]::GetFileName($candidate.path)
                path = $candidate.path
                discoveryReasons = @($candidate.reasons | ForEach-Object { $_ })
                git = $git
                stats = [pscustomobject][ordered]@{
                    fileCount = $stats.fileCount
                    directoryCount = $stats.directoryCount
                    bytes = $stats.bytes
                    reparsePointCount = $stats.reparsePointCount
                    potentialSecretFileCount = @($stats.potentialSecretFiles).Count
                    potentialSecretFiles = @($stats.potentialSecretFiles | Select-Object -First 50)
                    largeFiles = @($stats.largeFiles)
                }
            })
        }
        catch {
            Add-ProbeWarning -Stage 'project' -Path $candidate.path -Message $_.Exception.Message
        }
    }
    Write-Progress -Activity 'Scanning project metadata' -Completed

    return @($projects | ForEach-Object { $_ } | Sort-Object path)
}

function Get-StorageInventory {
    $storage = New-Object 'System.Collections.Generic.List[object]'
    foreach ($drive in @(Get-PSDrive -PSProvider FileSystem | Where-Object Root | Sort-Object Name)) {
        $diskNumber = $null
        $diskName = $null
        $fileSystem = $null
        $driveType = $null

        if ($drive.Name -match '^[A-Za-z]$') {
            try {
                $partition = Get-Partition -DriveLetter $drive.Name -ErrorAction Stop
                $disk = $partition | Get-Disk -ErrorAction Stop
                $volume = Get-Volume -DriveLetter $drive.Name -ErrorAction Stop
                $diskNumber = $disk.Number
                $diskName = $disk.FriendlyName
                $fileSystem = $volume.FileSystem
                $driveType = [string]$volume.DriveType
            }
            catch {
                Add-ProbeWarning -Stage 'storage' -Path $drive.Root -Message $_.Exception.Message
            }
        }

        [void]$storage.Add([pscustomobject][ordered]@{
            name = $drive.Name
            root = $drive.Root
            usedBytes = [long]$drive.Used
            freeBytes = [long]$drive.Free
            diskNumber = $diskNumber
            diskName = $diskName
            fileSystem = $fileSystem
            driveType = $driveType
        })
    }

    return @($storage | ForEach-Object { $_ })
}

$normalizedProjectRoots = @(
    $ProjectRoots |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { Get-NormalizedPath -Path $_ } |
        Sort-Object -Unique
)

$codexInventory = Get-CodexInventory -Root $CodexRoot -LargeFileThreshold $LargeFileThresholdBytes
$projectCandidates = @(Find-ProjectCandidates -Roots $normalizedProjectRoots -MaxDepth $MaxDiscoveryDepth)
$projectInventory = @(Get-ProjectInventory -Candidates $projectCandidates -LargeFileThreshold $LargeFileThresholdBytes)
$storageInventory = @(Get-StorageInventory)

$sameDiskGroups = @(
    $storageInventory |
        Where-Object { $null -ne $_.diskNumber } |
        Group-Object diskNumber |
        Where-Object Count -gt 1 |
        ForEach-Object {
            [pscustomobject][ordered]@{
                diskNumber = [int]$_.Name
                driveRoots = @($_.Group.root)
            }
        }
)

$gitProjects = @($projectInventory | Where-Object { $_.git.available })
$report = [pscustomobject][ordered]@{
    schemaVersion = '1.0'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    probeVersion = '0.1.0'
    host = [pscustomobject][ordered]@{
        osVersion = [System.Environment]::OSVersion.VersionString
        is64BitOperatingSystem = [System.Environment]::Is64BitOperatingSystem
        powerShellVersion = $PSVersionTable.PSVersion.ToString()
    }
    scanParameters = [pscustomobject][ordered]@{
        projectRoots = $normalizedProjectRoots
        codexRoot = (Get-NormalizedPath -Path $CodexRoot)
        maxDiscoveryDepth = $MaxDiscoveryDepth
        largeFileThresholdBytes = $LargeFileThresholdBytes
    }
    storage = [pscustomobject][ordered]@{
        volumes = $storageInventory
        samePhysicalDiskGroups = $sameDiskGroups
    }
    codex = $codexInventory
    projects = $projectInventory
    summary = [pscustomobject][ordered]@{
        projectCount = $projectInventory.Count
        gitProjectCount = $gitProjects.Count
        gitProjectsWithChanges = @($gitProjects | Where-Object { $_.git.changedEntryCount -gt 0 }).Count
        gitProjectsWithoutRemote = @($gitProjects | Where-Object { -not $_.git.hasRemote }).Count
        warningCount = $script:Warnings.Count
    }
    warnings = @($script:Warnings | ForEach-Object { $_ })
}

$json = $report | ConvertTo-Json -Depth 12
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = [System.IO.Path]::GetDirectoryName($fullOutputPath)
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and
        -not [System.IO.Directory]::Exists($outputDirectory)) {
        [void][System.IO.Directory]::CreateDirectory($outputDirectory)
    }

    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($fullOutputPath, $json, $utf8WithoutBom)

    [pscustomobject][ordered]@{
        outputPath = $fullOutputPath
        projectCount = $report.summary.projectCount
        gitProjectsWithChanges = $report.summary.gitProjectsWithChanges
        gitProjectsWithoutRemote = $report.summary.gitProjectsWithoutRemote
        codexBytes = $report.codex.totals.bytes
        warningCount = $report.summary.warningCount
    }
}
else {
    $json
}
