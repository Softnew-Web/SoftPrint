#define AppName "SoftPrint"
; Manter alinhado a VERSION e SoftPrint.Domain/SoftPrintVersion.cs
; Este arquivo deve ser UTF-8 com BOM (Inno Setup Unicode).
#define AppVersion "1.0.35"
#define AppPublisher "Softnew"
#define AppURL "https://softnewinfo.com.br"

[Setup]
AppId={{A8BC75EF-2131-48A1-BA66-1B5EB2261F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
AppCopyright=Copyright (C) 2026 {#AppPublisher}

; Instalacao
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
AllowNoIcons=yes
ChangesAssociations=no
CreateAppDir=yes
CloseApplications=force
RestartApplications=no
SetupLogging=yes
ShowLanguageDialog=no
MinVersion=6.1.7601

; Saida
OutputBaseFilename=SoftPrint-Setup
OutputDir=Output

; Compressao
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Desinstalacao
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\SoftPrint.exe

; Versao do executavel de setup
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}

; UI
WizardStyle=modern
WizardSizePercent=120
WizardImageFile=wizard-sidebar.bmp
WizardSmallImageFile=wizard-small.bmp
SetupIconFile=setup-icon.ico

; Paginas
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
DisableFinishedPage=no
AlwaysShowDirOnReadyPage=yes
AlwaysShowGroupOnReadyPage=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
WelcomeLabel1=Bem-vindo ao instalador do {#AppName}
WelcomeLabel2=Este assistente instala o {#AppName} {#AppVersion}, o painel local de impressao, neste computador.%n%nFeche outros programas antes de continuar. Na proxima tela voce precisa aceitar os termos de uso e confirmar a politica de privacidade.
FinishedHeadingLabel=Instalacao do {#AppName} concluida
FinishedLabel=O {#AppName} foi instalado com sucesso neste computador. Clique em Concluir para fechar o assistente.

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na area de trabalho"; GroupDescription: "Atalhos adicionais:"
Name: "startwithwindows"; Description: "Iniciar {#AppName} automaticamente com o Windows"; GroupDescription: "Opcoes de inicializacao:"
Name: "windowsService"; Description: "Manter a impressao ativa depois do logoff (servico do Windows)"; GroupDescription: "Opcoes de inicializacao:"; Flags: checked; Check: IsAdminInstallMode and IsModernWindows

[Files]
Source: "..\dist\windows-modern-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Check: IsModernWindows and IsWin64
Source: "..\dist\windows-modern-x86\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Check: IsModernWindows and not IsWin64
Source: "..\dist\windows-legacy-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Check: not IsModernWindows and IsWin64
Source: "..\dist\windows-legacy-x86\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Check: not IsModernWindows and not IsWin64
Source: "termos-de-uso.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "politica-de-privacidade.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "politica-de-privacidade.txt"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\SoftPrint.exe"; Comment: "Abrir painel de impressao SoftPrint"; Check: IsModernWindows
Name: "{group}\{#AppName} Legacy"; Filename: "{app}\SoftPrint.Legacy.exe"; Comment: "Abrir painel de impressao SoftPrint (Windows 7/8)"; Check: not IsModernWindows
Name: "{group}\Termos de uso"; Filename: "{app}\docs\termos-de-uso.txt"
Name: "{group}\Politica de privacidade"; Filename: "{app}\docs\politica-de-privacidade.txt"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SoftPrint.exe"; Tasks: desktopicon; Check: IsModernWindows
Name: "{autodesktop}\{#AppName} Legacy"; Filename: "{app}\SoftPrint.Legacy.exe"; Tasks: desktopicon; Check: not IsModernWindows

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\SoftPrint.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startwithwindows; Check: IsModernWindows
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\SoftPrint.Legacy.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startwithwindows; Check: not IsModernWindows
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "AutoPrint"; Flags: deletevalue

[InstallDelete]
Type: files; Name: "{group}\AutoPrint.lnk"
Type: files; Name: "{autodesktop}\AutoPrint.lnk"

[Run]
Filename: "{app}\SoftPrint.exe"; Parameters: "--install-service --quiet"; StatusMsg: "Registrando o servico do SoftPrint..."; Flags: runhidden waituntilterminated; Tasks: windowsService; Check: IsModernWindows
Filename: "{app}\SoftPrint.exe"; Description: "Abrir {#AppName} agora"; Flags: nowait postinstall skipifsilent shellexec; Check: IsModernWindows
Filename: "{app}\SoftPrint.Legacy.exe"; Description: "Abrir {#AppName} agora"; Flags: nowait postinstall skipifsilent shellexec; Check: not IsModernWindows

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SoftPrint"; Flags: runhidden; RunOnceId: "StopSoftPrintSvc"
Filename: "{app}\SoftPrint.exe"; Parameters: "--uninstall-service --quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSoftPrintSvc"; Check: IsModernWindows

[Code]
var
  PrivacyPage: TOutputMsgMemoWizardPage;
  PrivacyCheck: TNewCheckBox;
  IsUpgrade: Boolean;
  PreviousVersion: String;

function IsModernWindows: Boolean;
begin
  Result := GetWindowsVersion >= $0A000000;
end;

function GetInstalledVersion(out InstallDir: String): String;
var
  RegKey: String;
  Ver: String;
begin
  Result := '';
  InstallDir := '';
  RegKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8BC75EF-2131-48A1-BA66-1B5EB2261F11}_is1';
  if RegQueryStringValue(HKCU, RegKey, 'DisplayVersion', Ver) then
  begin
    Result := Ver;
    RegQueryStringValue(HKCU, RegKey, 'InstallLocation', InstallDir);
  end
  else if RegQueryStringValue(HKLM, RegKey, 'DisplayVersion', Ver) then
  begin
    Result := Ver;
    RegQueryStringValue(HKLM, RegKey, 'InstallLocation', InstallDir);
  end;
end;

function InitializeSetup: Boolean;
var
  InstallDir: String;
begin
  Result := True;
  PreviousVersion := GetInstalledVersion(InstallDir);
  IsUpgrade := PreviousVersion <> '';
end;

procedure InitializeWizard;
var
  InstallDir: String;
begin
  GetInstalledVersion(InstallDir);
  if (InstallDir <> '') then
    WizardForm.DirEdit.Text := InstallDir;

  PrivacyPage := CreateOutputMsgMemoPage(
    wpLicense,
    'Politica de privacidade',
    'O SoftPrint trata dados neste computador. Leia antes de continuar.',
    'A instalacao so avanca se voce confirmar que leu esta politica.',
    '');

  PrivacyCheck := TNewCheckBox.Create(PrivacyPage);
  PrivacyCheck.Parent := PrivacyPage.Surface;
  PrivacyCheck.Caption := 'Li e concordo com a politica de privacidade do SoftPrint';
  PrivacyCheck.Top := PrivacyPage.SurfaceHeight - ScaleY(22);
  PrivacyCheck.Left := 0;
  PrivacyCheck.Width := PrivacyPage.SurfaceWidth;
  PrivacyCheck.Height := ScaleY(22);
  PrivacyPage.RichEditViewer.Height := PrivacyCheck.Top - ScaleY(8);

  if WizardSilent then
    PrivacyCheck.Checked := True;
end;

procedure LoadPrivacyIntoPage;
var
  Lines: TArrayOfString;
  I: Integer;
  Text: String;
begin
  ExtractTemporaryFile('politica-de-privacidade.txt');
  if LoadStringsFromFile(ExpandConstant('{tmp}\politica-de-privacidade.txt'), Lines) then
  begin
    Text := '';
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if I > 0 then Text := Text + #13#10;
      Text := Text + Lines[I];
    end;
    PrivacyPage.RichEditViewer.Lines.Text := Text;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (PrivacyPage <> nil) and (CurPageID = PrivacyPage.ID) then
    LoadPrivacyIntoPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := WizardSilent and (PrivacyPage <> nil) and (PageID = PrivacyPage.ID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if WizardSilent then Exit;
  if (PrivacyPage <> nil) and (CurPageID = PrivacyPage.ID) and (not PrivacyCheck.Checked) then
  begin
    MsgBox('Marque a confirmacao da politica de privacidade para continuar.', mbError, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S: String;
begin
  if IsUpgrade then
    S := 'Modo: Atualizacao (' + PreviousVersion + ' para {#AppVersion})' + NewLine + NewLine
  else
    S := 'Modo: Nova instalacao' + NewLine + NewLine;

  if IsModernWindows then
    S := S + 'Edicao: {#AppName} {#AppVersion} (Windows 10+)' + NewLine
  else
    S := S + 'Edicao: {#AppName} Legacy {#AppVersion} (Windows 7/8)' + NewLine;

  S := S + NewLine + MemoDirInfo;
  if MemoGroupInfo <> '' then S := S + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then S := S + NewLine + MemoTasksInfo;
  Result := S;
end;

procedure KillSoftPrint;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop SoftPrint', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SoftPrint.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SoftPrint.Legacy.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1200);
end;

procedure MigrateAutoPrintRunKey;
var
  ExeName: String;
begin
  if IsModernWindows then
    ExeName := ExpandConstant('"{app}\SoftPrint.exe" --tray')
  else
    ExeName := ExpandConstant('"{app}\SoftPrint.Legacy.exe" --tray');

  if RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AutoPrint') then
  begin
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}', ExeName);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AutoPrint');
  end;
end;

procedure RestartSoftPrintServiceIfInstalled;
var
  ResultCode: Integer;
begin
  if not IsModernWindows then Exit;
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query SoftPrint', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Exit;
  if ResultCode <> 0 then Exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'start SoftPrint', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillSoftPrint;
  if CurStep = ssPostInstall then
  begin
    MigrateAutoPrintRunKey;
    RestartSoftPrintServiceIfInstalled;
  end;
end;