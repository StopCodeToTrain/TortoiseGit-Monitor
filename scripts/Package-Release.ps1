param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'TortoiseGitMonitor\obj\Releases'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = 'TortoiseGit-Monitor-v' + $Version + '-windows'
$stagingDirectory = Join-Path $OutputDirectory $packageName
$zipPath = Join-Path $OutputDirectory ($packageName + '.zip')
if ((Test-Path -LiteralPath $stagingDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw '发行文件已存在，请指定新的 OutputDirectory，避免覆盖已验证的产物。'
}

$binary = Join-Path $repoRoot 'TortoiseGitMonitor\bin\Release\TortoiseGitMonitor.exe'
if (!(Test-Path -LiteralPath $binary) -or !(Test-Path -LiteralPath ($binary + '.config'))) {
    throw '请先构建 Release，必须同时存在 exe 和 exe.config。'
}
if ((Get-Item -LiteralPath $binary).VersionInfo.ProductVersion -ne $Version) {
    throw '二进制 ProductVersion 与发行版本不一致。'
}

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Copy-Item -LiteralPath $binary, ($binary + '.config') -Destination $stagingDirectory
foreach ($relativePath in @('LICENSE', 'README.md', 'CHANGELOG.md', 'docs\INSTALL.md',
                           'docs\screenshots\settings.png', 'docs\screenshots\sync.png', 'tests\VERIFICATION.md')) {
    $destination = Join-Path $stagingDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot $relativePath) -Destination $destination
}

$utf8 = New-Object Text.UTF8Encoding($false)
$checksums = Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relativePath = $_.FullName.Substring($stagingDirectory.Length + 1).Replace('\', '/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relativePath
}
[IO.File]::WriteAllLines((Join-Path $stagingDirectory 'FILE_CHECKSUMS.sha256'), [string[]]$checksums, $utf8)
Compress-Archive -LiteralPath $stagingDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $zipHash + '  ' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine, $utf8)
Get-Item -LiteralPath $zipPath, (Join-Path $OutputDirectory 'SHA256SUMS.txt') | Select-Object FullName, Length
