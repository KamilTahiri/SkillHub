
#ifndef Payload
  #error "Pass the staged publish directory with /DPayload"
#endif
#ifndef Output
  #error "Pass the setup output directory with /DOutput"
#endif
#ifndef ProductVersion
  #error "Pass the built application version with /DProductVersion"
#endif

#ifdef TestPackage
  #define ProductName "SkillHub Setup Smoke Test"
#else
  #define ProductName "SkillHub"
#endif

[Setup]
#ifdef TestPackage
AppId=SkillHubSetupSmokeTest
#else
AppId={{989E9751-1C92-4424-B44B-90E4E4B25225}
#endif
AppName={#ProductName}
AppVersion={#ProductVersion}
VersionInfoProductName={#ProductName}
VersionInfoVersion={#ProductVersion}
AppPublisher=SkillHub
AppPublisherURL=https://github.com/KamilTahiri/SkillHub
DefaultDirName={localappdata}\{#ProductName}
DefaultGroupName=SkillHub
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Output}
OutputBaseFilename=SkillHub-Setup
UninstallDisplayIcon={app}\SkillHub.exe
SetupIconFile={#Payload}\skillhub-v2.ico
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=SkillHub.exe
RestartApplications=no
#ifdef ReleaseSigning
SignTool=skillhub
SignedUninstaller=yes
#endif

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifndef TestPackage
[Icons]
Name: "{group}\SkillHub"; Filename: "{app}\SkillHub.exe"; WorkingDir: "{app}"; IconFilename: "{app}\skillhub-v2.ico"

[Run]
Filename: "{app}\SkillHub.exe"; Description: "Open SkillHub"; Flags: nowait postinstall skipifsilent

#endif

[Code]
const
  WebViewClient = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasRuntime(Root: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, WebViewClient, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0');
end;

function InitializeSetup(): Boolean;
begin
  Result := HasRuntime(HKLM32) or HasRuntime(HKCU32) or HasRuntime(HKLM64) or HasRuntime(HKCU64);
  if not Result then
    SuppressibleMsgBox('Install the Microsoft Edge WebView2 Evergreen Runtime, then run SkillHub Setup again.' + #13#10 +
      'https://developer.microsoft.com/microsoft-edge/webview2/', mbError, MB_OK, IDOK);
end;
