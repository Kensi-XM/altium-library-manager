; Installer of Altium Library Manager, built with Inno Setup 6 (build-release.ps1 or the release workflow).
; Installs for the current user, so it needs no administrator rights.

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{7C0E5E3B-6D2F-4B57-9C5A-2F8F1D6A4E11}
AppName=Altium Library Manager
AppVersion={#AppVersion}
AppVerName=Altium Library Manager {#AppVersion}
DefaultDirName={autopf}\Altium Library Manager
DefaultGroupName=Altium Library Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=AltiumLibraryManager-{#AppVersion}-Setup
SetupIconFile=..\Altium Library Manager\Assets\app.ico
UninstallDisplayIcon={app}\Altium Library Manager.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Altium Library Manager"; Filename: "{app}\Altium Library Manager.exe"
Name: "{autodesktop}\Altium Library Manager"; Filename: "{app}\Altium Library Manager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Altium Library Manager.exe"; Description: "Start Altium Library Manager"; Flags: nowait postinstall skipifsilent

[Code]
// The app reads Access files through the 64-bit Access Database Engine, which can't be installed from here:
// the user is told, rather than finding out from an error later
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardSilent and not RegKeyExists(HKCR64, 'Microsoft.ACE.OLEDB.12.0') then
    MsgBox('To open Access databases, the app needs the 64-bit Microsoft Access Database Engine, which wasn''t found on this computer.' + #13#10#13#10 +
           'Install "Microsoft Access Database Engine 2016 Redistributable" (accessdatabaseengine_X64.exe) from the Microsoft website.',
           mbInformation, MB_OK);
end;
