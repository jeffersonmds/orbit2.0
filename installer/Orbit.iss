; Inno Setup - instalador do Orbit 2.0
; Gere com: installer\build.ps1  (publica self-contained e compila este script)

#define AppName "Orbit 2.0"
#define AppVersion "2.0.0"
#define AppExe "Orbit.exe"
#define PublishDir "..\Orbit\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish"
; Mata só o Orbit instalado em {app}: o 1.0 tem o mesmo nome de processo (taskkill /IM não serve).
#define KillOrbit "-NoProfile -Command Get-Process Orbit -EA 0 | ? {{ $_.Path -like '{app}\*' } | Stop-Process -Force"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
; AppId fixo (distinto do 1.0): reinstalar por cima faz upgrade no lugar.
AppId={{5E2B9C41-7D3A-4F8E-A1B6-2C9D0E4F7A13}
AppPublisher=Jefferson
DefaultDirName={autopf}\Orbit2
DefaultGroupName={#AppName}
; Por usuário: sem UAC, e o Run do registro fica no HKCU certo.
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Orbit\Assets\orbit.ico
OutputDir=.
OutputBaseFilename=Orbit2Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "startup"; Description: "Iniciar o Orbit 2.0 com o Windows (substitui o 1.0)"; GroupDescription: "Inicialização:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Mesma chave que Services/Autostart usa (Run\Orbit2), pra o toggle da bandeja enxergar.
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Orbit2"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Orbit"; Flags: deletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o Orbit 2.0"; Flags: nowait postinstall skipifsilent

; Fecha o Orbit antes de apagar os arquivos, senão o exe fica travado.
[UninstallRun]
Filename: "powershell.exe"; Parameters: "{#KillOrbit}"; Flags: runhidden; RunOnceId: "KillOrbit2"

; Remove dados do usuário (settings, tokens da ponte, histórico do clipboard, logs).
; O %AppData%\Orbit do 1.0 não é tocado.
[UninstallDelete]
; Atalho do Explorer "Enviar para → Dispositivo (Orbit)" (o Orbit cria com a ponte ligada).
Type: files; Name: "{usersendto}\iPhone (Orbit).lnk"
Type: files; Name: "{usersendto}\Dispositivo (Orbit).lnk"
Type: filesandordirs; Name: "{userappdata}\Orbit2"
Type: filesandordirs; Name: "{app}"

[Code]
// Reinstalar por cima: fecha o Orbit rodando antes de copiar. Fechamento gracioso não
// serve (fechar só esconde) — mata à força. %AppData%\Orbit2 fica intacto: reinstalar preserva tudo.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe', ExpandConstant('{#StringChange(KillOrbit, "'", "''")}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
