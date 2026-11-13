
[CmdletBinding()]
param([string]$Compiler = $env:SKILLHUB_ISCC)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot 'dist'
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Compiler $Compiler -OutputDirectory $directory
$setup = Join-Path $directory 'SkillHub-Setup.exe'
$process = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "SkillHub setup exited with code $($process.ExitCode)." }

$installed = Join-Path $env:LOCALAPPDATA 'SkillHub'
$manifest = Get-Content -LiteralPath (Join-Path $installed 'install-manifest.json') -Raw | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($installed) + '\'
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $installed $file.path))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid installation manifest entry $($file.path)." }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Installed file differs from its manifest: $($file.path)." }
}

$exe = Join-Path $installed 'SkillHub.exe'
Start-Process -FilePath $exe -WorkingDirectory $installed
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $window = Get-Process -Name SkillHub -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 -and $_.Responding } |
        Select-Object -First 1
    if ($window) { break }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
if (-not $window) { throw "SkillHub was installed, but its desktop window did not become ready." }
Write-Host "Installed SkillHub $($manifest.version) ($($manifest.files.Count) files verified). The native window is open."
