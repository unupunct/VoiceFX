; VoiceFX Studio installer (Inno Setup 6). Build with: installer\build.ps1
#define AppName "VoiceFX Studio"
#define AppVersion "1.0.0"
#define AppExe "VoiceFX.exe"

[Setup]
AppId={{6B1F3C52-7A0E-4C55-9B8D-3F6A2C0E5D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=VoiceFX
DefaultDirName={autopf}\VoiceFX Studio
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=VoiceFXStudio-Setup-{#AppVersion}
SetupIconFile=..\src\VoiceFX.App\Assets\voicefx.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
InfoAfterFile=..\docs\SETUP-NOTES.txt

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Launch VoiceFX at Windows &startup (minimized to tray)"; Flags: unchecked
Name: "cablepage"; Description: "Open the VB-CABLE download page (free virtual-microphone driver needed for other apps to hear the effects)"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VoiceFXStudio"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "https://vb-audio.com/Cable/"; Description: "Open VB-CABLE download page"; Flags: shellexec postinstall skipifsilent; Tasks: cablepage
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im {#AppExe} /f"; Flags: runhidden; RunOnceId: "KillApp"
