
[CmdletBinding()]
param(
    [string]$Compiler = $env:SKILLHUB_ISCC,
    [string]$OutputDirectory,
    [string]$SignCommand = $env:SKILLHUB_SETUP_SIGN_COMMAND,
    [switch]$TestPackage
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() defaults, so the default is resolved here.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'dist' }
if (-not $Compiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $Compiler = if ($command) { $command.Source } else { "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
    if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
        # Per-user or custom-folder installs (for example through winget) record their folder in the uninstall registry.
        $registered = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup*6*' -and $_.InstallLocation } |
            ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($registered) { $Compiler = $registered }
    }
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Install Inno Setup 6 or set SKILLHUB_ISCC.' }

$projectRoot = Join-Path $PSScriptRoot 'src\SkillSync.Daemon'
$project = Join-Path $projectRoot 'SkillSync.Daemon.csproj'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('skillhub-package-' + [guid]::NewGuid().ToString('N'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($stage -eq $output) { throw 'Choose a separate setup output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$setup = Join-Path $output 'SkillHub-Setup.exe'
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stage --nologo
    if ($LASTEXITCODE -ne 0) { throw "SkillHub publish failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'skillhub-v2.ico') -Destination $stage
    $sourceWeb = Join-Path $projectRoot 'wwwroot'
    $publishedWeb = Join-Path $stage 'wwwroot'
    $files = @(Get-ChildItem -LiteralPath $sourceWeb -File -Recurse)
    if ($files.Count -ne @(Get-ChildItem -LiteralPath $publishedWeb -File -Recurse).Count) { throw 'Published asset count differs from source.' }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourceWeb.Length).TrimStart('\','/')
        $published = Join-Path $publishedWeb $relative
        if ((Get-FileHash -LiteralPath $published).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Published asset differs from source: $relative"
        }
    }
    foreach ($notice in @('LICENSE','NOTICE','THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice) -Destination $stage
    }
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'SkillHub.dll')).Version.ToString(3)
    $manifest = foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
        [ordered]@{ path = $file.FullName.Substring($stage.Length).TrimStart('\','/').Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    }
    [ordered]@{ version = $version; architecture = 'win-x64'; files = @($manifest) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'install-manifest.json') -Encoding utf8
    $arguments = @('/Qp', "/DPayload=$stage", "/DOutput=$output", "/DProductVersion=$version")
    if ($TestPackage) { $arguments += "/DTestPackage" }
    if ($SignCommand) {
        $arguments += @('/DReleaseSigning', "/Sskillhub=$SignCommand")
    }
    & $Compiler @arguments (Join-Path $PSScriptRoot 'installer\SkillHub.iss')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Setup compilation failed ($LASTEXITCODE)." }
    if ($SignCommand -and (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') { throw 'Setup signature did not verify.' }
    "$((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant())  SkillHub-Setup.exe" |
        Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
    Write-Host "Built $setup and its SHA-256 checksum."
}
finally {
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $resolved = [IO.Path]::GetFullPath($stage)
    if ((Split-Path -Parent $resolved) -eq $temp -and (Split-Path -Leaf $resolved) -match '^skillhub-package-[a-f0-9]{32}$' -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
