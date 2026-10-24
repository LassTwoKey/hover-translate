; Установщик HoverTranslate (Inno Setup 6). Собирается из build.ps1:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<папка publish> /DOutputDir=<куда> /DRepoDir=<корень репозитория> HoverTranslate.iss
; Прав администратора не нужно: установка в %LocalAppData%\Programs\HoverTranslate.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\dist"
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif

#define AppName "HoverTranslate"
#define AppExe "HoverTranslate.exe"

[Setup]
; Не менять: по этому GUID установщик находит прежнюю версию при обновлении.
AppId={{6F1D2C8A-4B7E-4E55-9A0C-3D5B8E7F1A42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=HoverTranslate
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile={#RepoDir}\src\HoverTranslate.App\Assets\HoverTranslate.ico
UninstallDisplayIcon={app}\{#AppExe}
; Лицензии и атрибуция данных — на странице перед установкой.
InfoBeforeFile={#RepoDir}\NOTICE.txt
Compression=lzma2/normal
SolidCompression=no
WizardStyle=modern
; Работающую копию закрываем сами (см. [Code]) ключом --quit, а не диспетчером перезапуска.
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать {#AppName} при входе в Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Значок на рабочем столе"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoDir}\NOTICE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Тот же ключ и формат значения, что у переключателя в настройках программы (Autostart.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Сгенерированное программой рядом с exe (если появится) — не оставлять.
Type: filesandordirs; Name: "{app}"

[Code]
function OpenMutexW(DesiredAccess: Cardinal; InheritHandle: Integer; Name: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

// Работающая копия держит этот мьютекс (SingleInstance.cs) до выхода из процесса.
function AppIsRunning: Boolean;
var
  Handle: THandle;
begin
  Handle := OpenMutexW($00100000 { SYNCHRONIZE }, 0, 'Local\HoverTranslate.SingleInstance');
  Result := Handle <> 0;
  if Result then
    CloseHandle(Handle);
end;

// Закрыть работающий экземпляр: --quit подаёт сигнал уже запущенной копии, затем ждём её выхода,
// иначе файлы ещё заняты и деинсталлятор не сможет их удалить.
procedure QuitRunningApp;
var
  ResultCode, Waited: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\{#AppExe}');
  if FileExists(ExePath) and AppIsRunning then
  begin
    Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Waited := 0;
    while AppIsRunning and (Waited < 15000) do
    begin
      Sleep(200);
      Waited := Waited + 200;
    end;
    Sleep(500); // отпустить дескрипторы файлов
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    QuitRunningApp
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Удалить также историю, избранное, настройки и кэш переводов?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Если вы планируете поставить программу заново, нажмите «Нет» — данные сохранятся.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
