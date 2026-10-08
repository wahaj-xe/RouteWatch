; ============================================================
;  RouteWatch — Inno Setup Installer Script
;  Build: iscc RouteWatch-Installer.iss
;  Requires: Inno Setup 6.0+  https://jrsoftware.org/isinfo.php
; ============================================================

#define AppName      "RouteWatch"
#define AppVersion   "1.0.0"
#define AppPublisher "RouteWatch"
#define AppExeName   "RouteWatch.exe"
#define AppGUID      "{{D2C3B4A5-E6F7-8901-BCDE-FA1234567890}"

[Setup]
AppId={#AppGUID}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/routewatch/routewatch
AppSupportURL=https://github.com/routewatch/routewatch/issues
AppUpdatesURL=https://github.com/routewatch/routewatch/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; Require administrator for install (raw socket access & firewall rules)
PrivilegesRequired=admin
OutputDir=..\artifacts
OutputBaseFilename=RouteWatch-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Resources\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";    Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main executable and self-contained runtime dependencies
Source: "bin\Publish\win-x64-portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Optional Npcap installer (bundled silently if present in redist/)
Source: "redist\npcap-1.79.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

; Optional GeoIP database (bundled if present in redist/)
Source: "redist\GeoLite2-City.mmdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Optional VC++ Redistributable
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}";  Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";      Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Install Visual C++ Redistributable silently if bundled
Filename: "{tmp}\vc_redist.x64.exe"; \
    Parameters: "/install /passive /norestart"; \
    StatusMsg: "Installing Visual C++ Redistributable..."; \
    Flags: waituntilterminated; \
    Check: VcRedistExists

; Install Npcap if bundled and not already installed
Filename: "{tmp}\npcap-1.79.exe"; \
    Parameters: "/loopback_support=yes /winpcap_mode=yes /admin_only=no /dot11_support=no /S"; \
    StatusMsg: "Installing Npcap packet capture driver..."; \
    Flags: waituntilterminated; \
    Check: ShouldInstallNpcap

; Launch application after install
Filename: "{app}\{#AppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[Registry]
; Register for Windows
Root: HKLM; Subkey: "SOFTWARE\{#AppPublisher}\{#AppName}"; \
     ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\{#AppPublisher}\{#AppName}"; \
     ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey

[Code]
// ── Npcap detection ────────────────────────────────────────────────────────
function NpcapNotInstalled(): Boolean;
var
  version: String;
begin
  if RegQueryStringValue(HKLM, 'SOFTWARE\Npcap', 'Version', version) then
    Result := False
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\Npcap', 'Version', version) then
    Result := False
  else
    Result := True;
end;

function VcRedistExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\vc_redist.x64.exe'));
end;

function ShouldInstallNpcap(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\npcap-1.79.exe')) and NpcapNotInstalled();
end;

// ── .NET 8 Desktop Runtime check ───────────────────────────────────────────
function DotNetRuntimeInstalled(): Boolean;
var
  runtimes: TArrayOfString;
  i: Integer;
  regKey: String;
begin
  Result := False;
  regKey := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetValueNames(HKLM, regKey, runtimes) then
    for i := 0 to GetArrayLength(runtimes) - 1 do
      if Pos('8.', runtimes[i]) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

procedure InitializeWizard;
begin
  // RouteWatch is self-contained and packs the runtime natively.
end;

// ── Confirm Npcap recommendation ───────────────────────────────────────────
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpWelcome then
  begin
    if ShouldInstallNpcap() then
    begin
      MsgBox(
        'Npcap packet capture driver will be installed automatically for enhanced UDP traceroute capture.' + #13#10 +
        'Native ICMP and TCP modes work without Npcap.',
        mbInformation, MB_OK);
    end;
  end;
end;
