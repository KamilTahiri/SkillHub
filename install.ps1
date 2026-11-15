
[CmdletBinding()]
param([string]$Compiler = $env:SKILLHUB_ISCC, [switch]$PurgeCache)

$ErrorActionPreference = 'Stop'
$installed = Join-Path $env:LOCALAPPDATA 'SkillHub'
$directory = Join-Path $PSScriptRoot 'dist'
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Compiler $Compiler -OutputDirectory $directory

# Stop the running desktop/tray daemon so setup can replace its files.
$running = Get-Process -Name SkillHub -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

# -PurgeCache also deletes SkillHub's GitHub index cache; indexes are rebuilt from GitHub on next use.
if ($PurgeCache) {
    $githubCache = Join-Path $env:USERPROFILE '.skillhub\cache\github'
    if (Test-Path -LiteralPath $githubCache) { Remove-Item -LiteralPath $githubCache -Recurse -Force }
}

# Clear only WebView2's HTTP and script caches. Local storage (unsaved drafts) and preferences stay.
$webCache = Join-Path $installed 'WebView2Data\EBWebView'
if (Test-Path -LiteralPath $webCache) {
    Get-ChildItem -LiteralPath $webCache -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'Cache', 'Code Cache', 'GPUCache', 'Service Worker' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
$setup = Join-Path $directory 'SkillHub-Setup.exe'
$process = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "SkillHub setup exited with code $($process.ExitCode)." }

$manifest = Get-Content -LiteralPath (Join-Path $installed 'install-manifest.json') -Raw | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($installed) + '\'
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $installed $file.path))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid installation manifest entry $($file.path)." }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Installed file differs from its manifest: $($file.path)." }
}

# The installed web assets must match this checkout byte for byte.
$source = Join-Path $PSScriptRoot 'src\SkillSync.Daemon\wwwroot'
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = $file.FullName.Substring($source.Length).TrimStart([char]'\')
    $target = Join-Path (Join-Path $installed 'wwwroot') $relative
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
        throw "Installed web asset does not match the checkout: wwwroot\$relative."
    }
}

$exe = Join-Path $installed 'SkillHub.exe'
Start-Process -FilePath $exe -WorkingDirectory $installed
$deadline = [DateTime]::UtcNow.AddSeconds(45)
do {
    $window = Get-Process -Name SkillHub -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 -and $_.Responding } |
        Select-Object -First 1
    if ($window) { break }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
if (-not $window) { throw "SkillHub was installed, but its desktop window did not become ready." }
Write-Host "Installed SkillHub $($manifest.version) ($($manifest.files.Count) files verified). The native window is open."
