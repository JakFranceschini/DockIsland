; =====================================================================
;  Instalador do DockIsland  (Inno Setup 6.3 ou mais novo)
;  Gere com criar_instalador.bat  ->  instalador\DockIsland-Setup.exe
; =====================================================================
#define MyAppName      "DockIsland"
#define MyAppVersion   "1.0.0"
#define MyAppExe       "DockIsland.exe"
#define MyMutex        "DockIslandSingleton"
#define RunKey         "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{6F0B7C1E-3D5A-4B8E-9C42-D0C15A1A7E91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=admin
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=instalador
OutputBaseFilename=DockIsland-Setup
UninstallDisplayIcon={app}\{#MyAppExe}
SetupIconFile=icone.ico
AppMutex={#MyMutex}
CloseApplications=no
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar o DockIsland junto com o Windows"
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "publish_setup\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Mesma chave que a opção "iniciar com o Windows" dos Ajustes do programa
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExe}"" --autostart"; Tasks: autostart
; (o atraso padrão do Windows na inicialização é mantido: o programa abre depois que a área de trabalho assentou)

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Abrir o DockIsland agora"; Flags: nowait postinstall skipifsilent

[Code]
// Pede ao programa (se estiver aberto) para sair sozinho: ele devolve a barra de tarefas do Windows,
// o que não acontece se o processo for encerrado à força.
procedure AskAppToQuit(const ExePath: String);
var
  ResultCode, I: Integer;
begin
  if (ExePath = '') or (not FileExists(ExePath)) then Exit;
  if not CheckForMutexes('{#MyMutex}') then Exit;
  Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 40 do
  begin
    if not CheckForMutexes('{#MyMutex}') then Break;
    Sleep(150);
  end;
end;

function InitializeSetup(): Boolean;
var
  Cmd: String;
begin
  // atualização: acha o programa instalado pela entrada de inicialização automática
  if RegQueryStringValue(HKCU, '{#RunKey}', '{#MyAppName}', Cmd) then
    AskAppToQuit(RemoveQuotes(Cmd))
  else
    AskAppToQuit(ExpandConstant('{autopf}\{#MyAppName}\{#MyAppExe}'));
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  AskAppToQuit(ExpandConstant('{app}\{#MyAppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, '{#RunKey}', '{#MyAppName}');

  if CurUninstallStep = usPostUninstall then
    if not UninstallSilent then
      if MsgBox('Remover também suas configurações, ícones, categorias e histórico do DockIsland?' + #13#10 +
                '(pasta %AppData%\DockIsland)', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\{#MyAppName}'), True, True, True);
end;
