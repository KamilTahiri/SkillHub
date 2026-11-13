
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Setup,
    [string]$SourceWeb = (Join-Path $PSScriptRoot '..\..\src\SkillSync.Daemon\wwwroot')
)

$ErrorActionPreference = 'Stop'
$setupPath = (Resolve-Path -LiteralPath $Setup).Path
if ((Get-Item -LiteralPath $setupPath).VersionInfo.ProductName.Trim() -ne 'SkillHub Setup Smoke Test') {
    throw 'Build the test-identity installer with -TestPackage -OutputDirectory dist/setup-test.'
}
$SourceWeb = (Resolve-Path -LiteralPath $SourceWeb).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('skillhub-setup-test-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $work 'SkillHub'
$testHome = Join-Path $work 'home'
[IO.Directory]::CreateDirectory($testHome) | Out-Null

function Run-Setup($file, $argsToPass) {
    $proc = Start-Process -FilePath $file -ArgumentList $argsToPass -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) { throw "Setup operation failed ($($proc.ExitCode))." }
}

function Assert-Payload {
    $manifest = Get-Content -LiteralPath (Join-Path $install 'install-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.files.Count -lt 20) { throw 'The manifest is incomplete.' }
    foreach ($file in $manifest.files) {
        $path = Join-Path $install $file.path
        if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Installed hash mismatch: $($file.path)." }
    }
    $expected = @(Get-ChildItem -LiteralPath $SourceWeb -File -Recurse)
    $web = Join-Path $install 'wwwroot'
    if (@(Get-ChildItem -LiteralPath $web -File -Recurse).Count -ne $expected.Count) { throw 'Incorrect installed web file count.' }
    foreach ($file in $expected) {
        $relative = $file.FullName.Substring($SourceWeb.Length).TrimStart('\','/')
        if ((Get-FileHash -LiteralPath (Join-Path $web $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Installed web asset differs from source: $relative."
        }
    }
}

$flags = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',"/DIR=""$install""")
try {
    Run-Setup $setupPath $flags
    Assert-Payload
    $exe = Join-Path $install 'SkillHub.exe'
    $marker = Join-Path $install 'user-data-marker.txt'
    Set-Content -LiteralPath $marker -Value 'Keep user files'
    $damaged = Join-Path $install 'wwwroot\index.html'
    Set-Content -LiteralPath $damaged -Value 'Simulate a damaged earlier web asset'
    Run-Setup $setupPath $flags
    Assert-Payload
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Upgrade lost an unowned file.' }

    $daemon = Start-Process -FilePath $exe -ArgumentList 'daemon','--background','--port','0','--home',('"' + $testHome + '"') -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $listener = Get-NetTCPConnection -OwningProcess $daemon.Id -State Listen -ErrorAction SilentlyContinue |
                Where-Object LocalAddress -eq '127.0.0.1' | Select-Object -First 1
            if ($listener) { break }
            if ($daemon.HasExited) { throw "The installed daemon exited ($($daemon.ExitCode))." }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $listener) { throw 'The installed daemon did not start its local listener.' }
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$($listener.LocalPort)/" -UseBasicParsing -TimeoutSec 10
        if ($response.StatusCode -ne 200 -or $response.Content -notmatch 'explore-screen') { throw 'The installed application did not serve the Workbench.' }
    }
    finally {
        if (-not $daemon.HasExited) { Stop-Process -Id $daemon.Id; $daemon.WaitForExit() }
    }
    $uninstall = Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' | Select-Object -First 1
    Run-Setup $uninstall.FullName @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    if (Test-Path -LiteralPath $exe) { throw 'Uninstall left the executable in place.' }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Uninstall removed an unowned file.' }
    Write-Host 'Setup install, asset verification, repair, startup, and uninstall passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if ((Split-Path -Parent $resolved) -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -and
        (Split-Path -Leaf $resolved) -match '^skillhub-setup-test-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
