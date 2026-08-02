; Inno Setup script for GitVault.
;
; Build the payload first, then compile this:
;   dotnet publish src/GitVault.App/GitVault.App.csproj -c Release -r win-x64 --self-contained true ^
;       -p:PublishSingleFile=true -p:PublishTrimmed=false -p:InvariantGlobalization=false ^
;       -o artifacts/win-x64
;   iscc build/windows/gitvault.iss /DSourceDir=..\..\artifacts\win-x64 /DAppVersion=0.1.0
;
; GitVault installs per-user by default. It reads and writes the current user's git and SSH
; configuration, so a machine-wide install would give it nothing extra and would need elevation
; the application does not otherwise want.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\win-x64"
#endif

#ifndef Architecture
  #define Architecture "x64"
#endif

[Setup]
AppId={{7C2F1A54-3B9E-4F62-9D0A-6E3C8B5A1D77}
AppName=GitVault
AppVersion={#AppVersion}
AppPublisher=GitVault contributors
DefaultDirName={autopf}\GitVault
DefaultGroupName=GitVault
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installers
OutputBaseFilename=GitVault-{#AppVersion}-{#Architecture}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode={#Architecture}
UninstallDisplayName=GitVault
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
; Simplified Chinese is not shipped with Inno Setup; the file is available from the
; unofficial translations repository and should be placed next to this script.
#if FileExists("ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\GitVault.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\GitVault"; Filename: "{app}\GitVault.exe"
Name: "{autodesktop}\GitVault"; Filename: "{app}\GitVault.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitVault.exe"; Description: "{cm:LaunchProgram,GitVault}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only GitVault's own cache is removed. Settings, profiles and snapshots are deliberately kept:
; a snapshot is the user's route back from a change GitVault made, and uninstalling the tool is
; not a reason to throw that away.
Type: files; Name: "{userappdata}\GitVault\cache.json"
