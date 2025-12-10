; Inno Setup script for ISPLedger
; Basic installer - adjust paths and options before building

[Setup]
AppName=ISPLedger
AppVersion=2.1.1
AppPublisher=Sabuj Sheikh
DefaultDirName={pf}\ISPLedger
DefaultGroupName=ISPLedger
DisableProgramGroupPage=no
OutputDir=dist
OutputBaseFilename=ISPLedger_Setup
Compression=lzma
SolidCompression=yes
DisableDirPage=no
WizardStyle=modern

; Use provided icon
SetupIconFile=WPF_APP\app.ico

[Files]
; Main WPF executable and supporting files - ensure you build Release and adjust the path
; Include published WPF output if available (recommended: run `dotnet publish -c Release -r win-x64` first)
; Preferred: publish folder created by dotnet publish
Source: "WPF_APP\bin\Release\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Fallback: include build output files (exe/dll) if publish folder not used
Source: "WPF_APP\\bin\\Release\\net8.0-windows\\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "WPF_APP\\app.ico"; DestDir: "{app}"; Flags: ignoreversion
; Include bundled web assets from the WPF project (wwwroot)
Source: "WPF_APP\\wwwroot\\*"; DestDir: "{app}\\wwwroot"; Flags: recursesubdirs createallsubdirs
; Include WebView2 bootstrapper if present in tools\ (downloaded by package script)
Source: "tools\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion

[Icons]
Name: "{group}\ISPLedger"; Filename: "{app}\\ISPLedger.exe"; IconFilename: "{app}\\app.ico"
Name: "{commondesktop}\ISPLedger"; Filename: "{app}\\ISPLedger.exe"; Tasks: desktopicon; IconFilename: "{app}\\app.ico"
Name: "{group}\Uninstall ISPLedger"; Filename: "{uninstallexe}"

[Run]
; Ensure WebView2 runtime is installed (silent). This may prompt for elevation.
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent"; Flags: waituntilterminated runascurrentuser; StatusMsg: "Installing required WebView2 runtime..."; Check: FileExists('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe')
Filename: "{app}\\ISPLedger.exe"; Description: "Launch ISPLedger"; Flags: nowait postinstall skipifsilent

[Tasks]
Name: desktopicon; Description: Create a &desktop icon; GroupDescription: Additional icons:

[UninstallDelete]
Type: filesandordirs; Name: "{app}\\wwwroot"

