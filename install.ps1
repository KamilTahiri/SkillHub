$ErrorActionPreference = "Stop"

Write-Host "Stopping any running SkillHub / legacy skillsync processes..." -ForegroundColor Cyan
Get-Process -Name "SkillHub","skillsync","SkillSync.Daemon" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Wait until port 45880 is completely released
for ($i = 0; $i -lt 15; $i++) {
  $inUse = Get-NetTCPConnection -LocalPort 45880 -State Listen -ErrorAction SilentlyContinue
  if (-not $inUse) { break }
  Get-Process -Name "SkillHub","skillsync","SkillSync.Daemon" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 300
}

$InstallDir = Join-Path $env:LOCALAPPDATA "SkillHub"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# Purge old published wwwroot, legacy icon files, and WebView2 HTTP/JS caches
$PublishedWwwroot = Join-Path $InstallDir "wwwroot"
if (Test-Path $PublishedWwwroot) {
  Remove-Item -Path $PublishedWwwroot -Recurse -Force -ErrorAction SilentlyContinue
}
$OldIco = Join-Path $InstallDir "skillhub.ico"
if (Test-Path $OldIco) {
  Remove-Item -Path $OldIco -Force -ErrorAction SilentlyContinue
}

foreach ($cacheSub in @("Cache", "Code Cache", "GPUCache")) {
  $cacheDir = Join-Path $InstallDir "WebView2Data\EBWebView\Default\$cacheSub"
  if (Test-Path $cacheDir) {
    Remove-Item -Path $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

$ProjectIco = Join-Path $PSScriptRoot "src\SkillSync.Daemon\skillhub-v2.ico"
if (-not (Test-Path $ProjectIco)) {
  Write-Host "Generating multi-resolution isometric prism icon (skillhub-v2.ico)..." -ForegroundColor Cyan
  dotnet run --project "$PSScriptRoot\src\SkillSync.Daemon\SkillSync.Daemon.csproj" -- generate-ico "$ProjectIco"
}

Write-Host "Publishing SkillHub Desktop App (with embedded skillhub-v2.ico) to $InstallDir..." -ForegroundColor Cyan
dotnet publish "$PSScriptRoot\src\SkillSync.Daemon\SkillSync.Daemon.csproj" `
  -c Release `
  -o $InstallDir `
  -p:PublishSingleFile=false `
  --nologo

Copy-Item -Path $ProjectIco -Destination (Join-Path $InstallDir "skillhub-v2.ico") -Force

$ExePath = Join-Path $InstallDir "SkillHub.exe"
Write-Host "Configuring shortcuts, OS auto-start & MCP clients..." -ForegroundColor Cyan
Start-Process -FilePath $ExePath -ArgumentList "install","--bin-path",$ExePath,"--no-spawn" -Wait

Write-Host "Launching SkillHub Desktop Application..." -ForegroundColor Green
try {
  Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$ExePath`"" } | Out-Null
} catch {
  Start-Process -FilePath $ExePath
}
Write-Host "SkillHub is now installed and running!" -ForegroundColor Green
