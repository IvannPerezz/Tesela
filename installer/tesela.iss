; Instalador de Tesela (Inno Setup 6). Se genera con build-installer.ps1, que deja todo lo que se instala en
; installer\staging.

#define AppName "Tesela"
#define AppVersion "1.0"
#define AppPublisher "IvannPerezz"
#define AppURL "https://github.com/IvannPerezz/Tesela"

[Setup]
AppId={{6C2F1B7E-3D4A-4E8B-9A51-7D0C2E9B4F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}.0.0
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=tesela-setup-v{#AppVersion}
SetupIconFile=..\settings\Assets\Tesela.ico
UninstallDisplayIcon={app}\Tesela.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-large.bmp,wizard-large-2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.RunNow=Run Tesela now
spanish.RunNow=Ejecutar Tesela ahora
english.ViewReadme=View Readme
spanish.ViewReadme=Ver el Léame
english.ReadmeUrl={#AppURL}#readme
spanish.ReadmeUrl={#AppURL}/blob/main/README.es.md

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Tesela.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Tesela.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tesela.exe"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{cm:ReadmeUrl}"; Description: "{cm:ViewReadme}"; Flags: postinstall shellexec skipifsilent nowait runasoriginaluser

[UninstallRun]
; Cierra Tesela (y el proceso de sensores) y quita la tarea programada de los sensores, si se creó
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM TeselaSettings.exe"; Flags: runhidden; RunOnceId: "CloseSettings"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Tesela.exe"; Flags: runhidden; RunOnceId: "CloseTesela"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Tesela\Sensores"" /F"; Flags: runhidden; RunOnceId: "DeleteSensorTask"

[Registry]
; "Iniciar Tesela con Windows" lo guarda Tesela; al desinstalar se quita
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Tesela"; ValueType: none; Flags: uninsdeletevalue

[Code]
// Antes de copiar los archivos, Tesela (y su proceso de sensores) se cierran para poder sustituirlos
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM TeselaSettings.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Tesela.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(800);
  Result := '';
end;
