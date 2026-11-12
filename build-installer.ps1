$ErrorActionPreference = "Stop"

$DistDir = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

Write-Host "Building self-contained single-file SkillHub-Setup.exe into $DistDir..." -ForegroundColor Cyan
dotnet publish "$PSScriptRoot\src\SkillSync.Daemon\SkillSync.Daemon.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $DistDir `
  --nologo

$BuiltExe = Join-Path $DistDir "SkillHub.exe"
$SetupExe = Join-Path $DistDir "SkillHub-Setup.exe"
if (Test-Path $BuiltExe) {
  Copy-Item $BuiltExe $SetupExe -Force
  Write-Host "Created standalone installer: $SetupExe" -ForegroundColor Green
  Write-Host "Run '$SetupExe --install --launch-ui' on any Windows machine to install SkillHub." -ForegroundColor Green
}

