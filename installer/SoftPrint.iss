#define AppName "SoftPrint"
; Manter alinhado a VERSION e SoftPrint.Domain/SoftPrintVersion.cs
; Este arquivo deve ser UTF-8 com BOM (Inno Setup Unicode).
#define AppVersion "1.0.19"

[Setup]
AppId={{A8BC75EF-2131-48A1-BA66-1B5EB2261F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=SoftPrint
AppCopyright=Copyright (C) 2026 SoftPrint
DefaultDirName={autopf}\SoftPrint
DefaultGroupName=SoftPrint
OutputBaseFilename=SoftPrint-Setup
ArchitecturesAllowed=x86 x64compatible
CloseApplications=force
RestartApplications=no
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableFinishedPage=yes
LicenseFile=termos-de-uso.txt
SetupLogging=yes
VersionInfoProductName=SoftPrint
VersionInfoDescription=Instalador do SoftPrint
ShowLanguageDialog=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
WelcomeLabel1=Bem-vindo ao instalador do SoftPrint
WelcomeLabel2=Este assistente instala o SoftPrint, o painel local de impressão, neste computador.%n%nFeche outros programas antes de continuar. Na próxima tela você precisa aceitar os termos de uso e confirmar a política de privacidade.

[Files]
Source: "..\dist\windows-modern-x64\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: IsModernWindows and IsWin64
Source: "..\dist\windows-modern-x86\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: IsModernWindows and not IsWin64
Source: "..\dist\windows-legacy-x64\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: not IsModernWindows and IsWin64
Source: "..\dist\windows-legacy-x86\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: not IsModernWindows and not IsWin64
Source: "termos-de-uso.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "politica-de-privacidade.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "politica-de-privacidade.txt"; Flags: dontcopy

[Icons]
Name: "{group}\SoftPrint"; Filename: "{app}\SoftPrint.exe"; Check: IsModernWindows
Name: "{group}\SoftPrint Legacy"; Filename: "{app}\SoftPrint.Legacy.exe"; Check: not IsModernWindows
Name: "{group}\Termos de uso"; Filename: "{app}\docs\termos-de-uso.txt"
Name: "{group}\Política de privacidade"; Filename: "{app}\docs\politica-de-privacidade.txt"
Name: "{autodesktop}\SoftPrint"; Filename: "{app}\SoftPrint.exe"; Tasks: desktopicon; Check: IsModernWindows
Name: "{autodesktop}\SoftPrint Legacy"; Filename: "{app}\SoftPrint.Legacy.exe"; Tasks: desktopicon; Check: not IsModernWindows

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"

[InstallDelete]
Type: files; Name: "{group}\AutoPrint.lnk"
Type: files; Name: "{autodesktop}\AutoPrint.lnk"

[Run]
Filename: "{app}\SoftPrint.exe"; Description: "Abrir SoftPrint"; Flags: nowait postinstall skipifsilent; Check: IsModernWindows
Filename: "{app}\SoftPrint.Legacy.exe"; Description: "Abrir SoftPrint Legacy"; Flags: nowait postinstall skipifsilent; Check: not IsModernWindows

[Code]
var
  PrivacyPage: TOutputMsgMemoWizardPage;
  PrivacyCheck: TNewCheckBox;

function IsModernWindows: Boolean;
begin
  Result := GetWindowsVersion >= $0A000000;
end;

function LoadPrivacyText: String;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  ExtractTemporaryFile('politica-de-privacidade.txt');
  if LoadStringsFromFile(ExpandConstant('{tmp}\politica-de-privacidade.txt'), Lines) then
  begin
    Result := '';
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if I > 0 then
        Result := Result + #13#10;
      Result := Result + Lines[I];
    end;
  end
  else
    Result := 'Não foi possível carregar a política de privacidade.';
end;

procedure InitializeWizard;
begin
  PrivacyPage := CreateOutputMsgMemoPage(
    wpLicense,
    'Política de privacidade',
    'O SoftPrint trata dados neste computador. Leia antes de continuar.',
    'A instalação só avança se você confirmar que leu esta política.',
    LoadPrivacyText);

  PrivacyCheck := TNewCheckBox.Create(PrivacyPage);
  PrivacyCheck.Parent := PrivacyPage.Surface;
  PrivacyCheck.Caption := 'Li e concordo com a política de privacidade do SoftPrint';
  PrivacyCheck.Top := PrivacyPage.SurfaceHeight - ScaleY(22);
  PrivacyCheck.Left := 0;
  PrivacyCheck.Width := PrivacyPage.SurfaceWidth;
  PrivacyCheck.Height := ScaleY(22);
  PrivacyPage.RichEditViewer.Height := PrivacyCheck.Top - ScaleY(8);

  { Atualização automática / silent: não bloqueia por falta de clique no checkbox. }
  if WizardSilent then
    PrivacyCheck.Checked := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := WizardSilent and (PrivacyPage <> nil) and (PageID = PrivacyPage.ID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if WizardSilent then
    Exit;
  if (PrivacyPage <> nil) and (CurPageID = PrivacyPage.ID) and (not PrivacyCheck.Checked) then
  begin
    MsgBox('Marque a confirmação da política de privacidade para continuar.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure MigrateAutoPrintRunKey;
var
  ExeName: string;
begin
  if IsModernWindows then
    ExeName := ExpandConstant('"{app}\SoftPrint.exe" --tray')
  else
    ExeName := ExpandConstant('"{app}\SoftPrint.Legacy.exe" --tray');

  if RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AutoPrint') then
  begin
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SoftPrint', ExeName);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AutoPrint');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MigrateAutoPrintRunKey;
end;
