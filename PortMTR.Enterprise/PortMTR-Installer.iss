; ============================================================
;  PortMTR Enterprise — Inno Setup Installer Script
;  Build: iscc PortMTR-Installer.iss
;  Requires: Inno Setup 6.3+  https://jrsoftware.org/isinfo.php
;            npcap-1.79.exe   https://npcap.com
;            GeoLite2-City.mmdb (optional, MaxMind account required)
; ============================================================

#define AppName      "PortMTR Enterprise"
#define AppVersion   "1.0.0"
#define AppPublisher "PortMTR"
#define AppExeName   "PortMTR.Enterprise.exe"
#define AppGUID      "{{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"

[Setup]
AppId={#AppGUID}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/your-org/portmtr-enterprise
AppSupportURL=https://github.com/your-org/portmtr-enterprise/issues
AppUpdatesURL=https://github.com/your-org/portmtr-enterprise/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; Require administrator for install (raw socket access needs system-level install)
PrivilegesRequired=admin
OutputDir=dist
OutputBaseFilename=PortMTR-Enterprise-{#AppVersion}-Setup
SetupIconFile=assets\icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageFile=assets\wizard-large.bmp
MinVersion=10.0.17763   ; Windows 10 1809+

; Code signing (uncomment and fill in for production)
; SignTool=signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f "cert.pfx" /p "password" $f

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";    Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main executable (publish self-contained first: dotnet publish -c Release -r win-x64 --self-contained)
Source: "..\PortMTR.Enterprise\bin\Release\net8.0-windows\win-x64\publish\*"; \
        DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Npcap installer (bundled silently)
Source: "redist\npcap-1.79.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

; GeoIP database (optional — skip if not present)
Source: "redist\GeoLite2-City.mmdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; VC++ Redistributable (for .NET runtime native components)
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}";  Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";      Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Install Visual C++ Redistributable silently
Filename: "{tmp}\vc_redist.x64.exe"; \
    Parameters: "/install /passive /norestart"; \
    StatusMsg: "Installing Visual C++ Redistributable..."; \
    Flags: waituntilterminated

; Install Npcap if not already present
Filename: "{tmp}\npcap-1.79.exe"; \
    Parameters: "/loopback_support=yes /winpcap_mode=yes /admin_only=no /dot11_support=no /S"; \
    StatusMsg: "Installing Npcap packet capture driver..."; \
    Flags: waituntilterminated; \
    Check: NpcapNotInstalled

; Launch application after install
Filename: "{app}\{#AppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Do not uninstall Npcap automatically — other apps may need it
; Users can uninstall Npcap separately via Add/Remove Programs

[Registry]
; Register for Windows' "Open with" context menu
Root: HKLM; Subkey: "SOFTWARE\{#AppPublisher}\{#AppName}"; \
     ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"
Root: HKLM; Subkey: "SOFTWARE\{#AppPublisher}\{#AppName}"; \
     ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"

[Code]
// ── Npcap detection ────────────────────────────────────────────────────────
function NpcapNotInstalled(): Boolean;
var
  version: String;
begin
  // Npcap registers itself under this key
  if RegQueryStringValue(HKLM, 'SOFTWARE\Npcap', 'Version', version) then
    Result := False   // Already installed
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\Npcap', 'Version', version) then
    Result := False
  else
    Result := True;   // Not found — install it
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
  if not DotNetRuntimeInstalled() then
    MsgBox(
      '.NET 8 Desktop Runtime was not detected on this machine.' + #13#10 +
      'PortMTR Enterprise is built as a self-contained application, so it bundles' + #13#10 +
      'its own .NET runtime and will still work correctly.' + #13#10#13#10 +
      'If you experience issues, download .NET 8 from:' + #13#10 +
      'https://dotnet.microsoft.com/download/dotnet/8.0',
      'Information', MB_OK or MB_ICONINFORMATION);
end;

// ── Confirm Npcap requirement ──────────────────────────────────────────────
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpWelcome then
    if NpcapNotInstalled() then
      MsgBox(
        'Npcap will be installed automatically.' + #13#10 +
        'Npcap is required for UDP traceroute packet capture.' + #13#10 +
        'ICMP and TCP modes work without it.',
        'Npcap Installation', MB_OK or MB_ICONINFORMATION);
end;
