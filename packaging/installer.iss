; Inno Setup script: a per-user install with an icon to come back to, since the app on the phone needs renewing every 7 days.
; Built by .github/workflows/release.yml, after publishing the program into out\bin.

#define AppName "Ultimate MP3 Player Installer"
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#define AppExe "UltimateMP3PlayerInstaller.exe"

[Setup]
AppId={{7B1E4C92-5A3D-4F06-B8E7-2C96D1A04F3B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Davide
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; (WSL 2 itself asks for this build)
MinVersion=10.0.19041
OutputDir=..\out
; The README's download button relies on this name.
OutputBaseFilename=UltimateMP3Player-Installer-windows-x64-setup
SetupIconFile=..\src\Installer\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=8
LZMABlockSize=32768
WizardStyle=modern dark windows11 includetitlebar
WizardSizePercent=110
DisableWelcomePage=no
WizardImageFile=images\large-100.png,images\large-125.png,images\large-150.png,images\large-200.png,images\large-250.png
WizardSmallImageFile=images\small-100.png,images\small-125.png,images\small-150.png,images\small-200.png,images\small-250.png
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Messages]
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%nIt puts Ultimate MP3 Player on your iPhone with just the cable and your Apple ID, showing you every step. It stays here because the app needs renewing every 7 days: one click.
it.WelcomeLabel2=Verrà installato [name/ver] sul computer.%n%nMette Ultimate MP3 Player sul tuo iPhone con il cavo e il tuo Apple ID, mostrandoti ogni passaggio. Resta qui perché ogni 7 giorni l'app va rinnovata: basta un clic.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\out\bin\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licenses"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The private Linux, with the sign-in inside it, and everything downloaded: uninstalling leaves nothing behind.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; RunOnceId: "RemoveEverything"; Flags: runhidden waituntilterminated
