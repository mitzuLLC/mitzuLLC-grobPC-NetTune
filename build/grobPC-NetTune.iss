; grobPC NetTune installer (Inno Setup). CI invokes:  iscc /DSrcDir=<publish|protected> build\grobPC-NetTune.iss
; Produces dist\grobPC-NetTune-setup.exe — installs to Program Files\grobPC NetTune, shortcuts, uninstaller.

#ifndef SrcDir
  #define SrcDir "publish"
#endif
#ifndef OutDir
  #define OutDir "dist"
#endif
#define AppVer "2.0.0"

[Setup]
AppName=grobPC NetTune
AppVersion={#AppVer}
AppPublisher=mitzu LLC
DefaultDirName={autopf}\grobPC NetTune
DefaultGroupName=grobPC NetTune
UninstallDisplayIcon={app}\grobPC-NetTune.exe
OutputDir={#OutDir}
OutputBaseFilename=grobPC-NetTune-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
; a running NetTune is detected: setup / uninstall ask to exit it first
AppMutex=Global\grobPC-NetTune-mitzu

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\grobPC NetTune"; Filename: "{app}\grobPC-NetTune.exe"
Name: "{autodesktop}\grobPC NetTune"; Filename: "{app}\grobPC-NetTune.exe"

[Run]
Filename: "{app}\grobPC-NetTune.exe"; Description: "Launch grobPC NetTune"; Flags: nowait postinstall skipifsilent
