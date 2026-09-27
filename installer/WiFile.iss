; WiFile installer (Inno Setup 6)
#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif

[Setup]
AppId={{8C4E4F2B-7E0B-4C55-9A57-3E0F3D6A1B21}
AppName=WiFile
AppVersion={#AppVersion}
AppVerName=WiFile {#AppVersion}
AppPublisher=shahin
AppPublisherURL=https://github.com/shahind/WiFile
AppSupportURL=https://github.com/shahind/WiFile/issues
AppUpdatesURL=https://github.com/shahind/WiFile/releases
AppCopyright=Copyright (C) 2026 shahin. Licensed under the GNU GPL v3.
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}
VersionInfoCompany=shahin
VersionInfoDescription=WiFile Setup - share files and chat on the local network
VersionInfoProductName=WiFile
#ifdef Sign
SignTool=wifisign
SignedUninstaller=yes
#endif
DefaultDirName={autopf}\WiFile
DefaultGroupName=WiFile
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=WiFile-Setup-{#AppVersion}
SetupIconFile=..\assets\WiFile.ico
UninstallDisplayIcon={app}\WiFile.exe
UninstallDisplayName=WiFile
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Admin is needed once, only to open Windows Firewall for WiFile on the local network.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
; .NET Framework 4.8 ships with Windows 10 1903+ and Windows 11.
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\out\stage\WiFile.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\out\stage\WiFile.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\WiFile"; Filename: "{app}\WiFile.exe"
Name: "{autodesktop}\WiFile"; Filename: "{app}\WiFile.exe"; Tasks: desktopicon

[Run]
; Allow WiFile (TCP + UDP) through Windows Firewall on every network profile, so discovery works
; even when Windows classifies the Wi-Fi as "Public".
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""WiFile"""; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""WiFile"" dir=in action=allow program=""{app}\WiFile.exe"" enable=yes profile=any"; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "{app}\WiFile.exe"; Description: "Start WiFile now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im WiFile.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""WiFile"""; Flags: runhidden; RunOnceId: "DelFirewall"

[Registry]
; Remove the per-user autostart entry the app creates on first run.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WiFile"; ValueType: none; Flags: uninsdeletevalue dontcreatekey

[Code]
function IsDotNet48Installed(): Boolean;
var
  release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release) and (release >= 528040);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    MsgBox('WiFile needs .NET Framework 4.8, which is included with Windows 10 (May 2019 update or newer) and Windows 11.' + #13#10 +
           'Please run Windows Update and try again.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code: Integer;
begin
  // Stop a running copy (it lives in the tray) so the exe can be replaced.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im WiFile.exe', '', SW_HIDE, ewWaitUntilTerminated, code);
  Result := '';
end;
