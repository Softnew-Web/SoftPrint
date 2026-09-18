#define AppName "SoftPrint"
#define AppVersion "1.0.0"

[Setup]
AppId={{A8BC75EF-2131-48A1-BA66-1B5EB2261F11}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\SoftPrint
DefaultGroupName=SoftPrint
OutputBaseFilename=SoftPrint-Setup
ArchitecturesAllowed=x86 x64compatible
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\dist\windows-modern-x64\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: IsModernWindows and IsWin64
Source: "..\dist\windows-modern-x86\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: IsModernWindows and not IsWin64
Source: "..\dist\windows-legacy-x64\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: not IsModernWindows and IsWin64
Source: "..\dist\windows-legacy-x86\*"; DestDir: "{app}"; Flags: recursesubdirs; Check: not IsModernWindows and not IsWin64

[Icons]
Name: "{group}\SoftPrint"; Filename: "{app}\SoftPrint.exe"; Check: IsModernWindows
Name: "{group}\SoftPrint Legacy"; Filename: "{app}\SoftPrint.Legacy.exe"; Check: not IsModernWindows
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
function IsModernWindows: Boolean;
begin
  Result := GetWindowsVersion >= $0A000000;
end;

procedure MigrateAutoPrintRunKey;
var
  ExeName: string;
begin
  if IsModernWindows then
    ExeName := ExpandConstant('"{app}\SoftPrint.exe"')
  else
    ExeName := ExpandConstant('"{app}\SoftPrint.Legacy.exe"');

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
