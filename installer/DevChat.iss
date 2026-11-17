; DevChat — инсталятор (Inno Setup)
; Сборка: iscc DevChat.iss  (нужен Inno Setup 6+: https://jrsoftware.org/isdl.php)

#define MyAppName "DevChat"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DevChat"
#define MyAppExeName "DevChat.App.exe"
#define ExtId "pcghepfbmhphgolngdcigcnedmjojhoj"

[Setup]
AppId={{8F4A7B2C-3D5E-4F6A-9B8C-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputBaseFilename=DevChat-Setup-{#MyAppVersion}
OutputDir=.
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; приложение (self-contained, .NET включён)
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; CRX-расширение (для автоустановки через реестр)
Source: "extension.crx"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Registry]
; Автоустановка расширения Chrome через реестр (Chrome подхватит при следующем старте)
Root: HKCU; Subkey: "Software\Google\Chrome\Extensions\{#ExtId}"; ValueType: String; ValueName: "path"; ValueData: "{app}\extension.crx"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Google\Chrome\Extensions\{#ExtId}"; ValueType: String; ValueName: "manifest_version"; ValueData: "2"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
