; Baton installer: per user (no administrator rights), in %LOCALAPPDATA%\Programs\Baton, English and Italian.
; Built by tools\build-installer.ps1 (and by the release workflow):
;   ISCC.exe /DAppVersion=1.3.0 /DSourceDir=<folder with Baton.exe> /DOutputDir=<folder> installer\Baton.iss
; What the app registers (browser host, baton: links, notifications, start with Windows) is removed by Baton.exe itself
; (--uninstall), and only where it points to this copy; the copies and data found before installing come from
; Baton.exe --setup-check, and a portable copy's data is imported by Baton.exe --import-data. The script only asks.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\installer"
#endif
; The browser extension shipped with the program (tools\build-packages.ps1 builds it first).
#ifndef ExtensionDir
  #define ExtensionDir "..\dist\chromium"
#endif
#ifndef FirefoxPackage
  #define FirefoxPackage "..\dist\baton-firefox-" + AppVersion + ".xpi"
#endif

[Setup]
; Never change the AppId: "Installed apps", upgrades and Baton.exe (SystemRegistration.UninstallKey) rely on it.
AppId={{27469723-AE14-4420-B2B4-189B8BE709AB}
AppName=Baton
AppVersion={#AppVersion}
AppVerName=Baton {#AppVersion}
AppPublisher=Andrea Azzarello
AppPublisherURL=https://github.com/AndreaAzzarello/baton
AppSupportURL=https://github.com/AndreaAzzarello/baton/issues
AppUpdatesURL=https://github.com/AndreaAzzarello/baton/releases
AppCopyright=Copyright (c) 2026 Andrea Azzarello
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Baton
VersionInfoDescription=Baton setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Baton
DisableDirPage=yes
DefaultGroupName=Baton
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Baton-Setup-{#AppVersion}
SetupIconFile=..\src\Baton.App\app.ico
UninstallDisplayIcon={app}\Baton.exe
UninstallDisplayName=Baton
; Light or dark like Windows, as the app.
WizardStyle=modern dynamic
; Baton's own icon from the brand kit, as it is (docs\brand\kit), instead of Inno Setup's pictures: never stretched,
; smaller than the image areas (202x386 and 58x58 at 100%), so it is centered whole on the panel's colour.
WizardImageFile=..\docs\brand\kit\03_Icone_Windows\BATON_windows_128.png
WizardImageStretch=no
WizardImageBackColor=$FFFFFF
WizardImageBackColorDynamicDark=$202020
WizardSmallImageFile=..\docs\brand\kit\03_Icone_Windows\BATON_windows_48.png
WizardImageFileDynamicDark=..\docs\brand\kit\03_Icone_Windows\BATON_windows_128.png
WizardSmallImageFileDynamicDark=..\docs\brand\kit\03_Icone_Windows\BATON_windows_48.png
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
en.PreviousTitle=Data and other copies of Baton
en.PreviousSubtitle=What Setup found on this PC
en.PreviousIntro=Nothing is deleted: other copies and their data stay where they are.
en.PortableImport=Copy the downloads and settings of the portable copy in %1 (recommended)
en.LegacyFound=DownloadManager (Baton's name up to 1.1) data was found: it is copied to Baton at the first start, and the old folder stays as it is.
en.CopiesFound=Baton is also in %1. From now on the browser extension, notifications and start with Windows use the installed copy.
en.CloseRunning=Baton is running. Close it (notification area icon › Exit), then press OK.
en.Importing=Copying the data of the portable copy...
en.ImportFailed=The data of the portable copy could not be copied (it is untouched): the installed Baton starts with an empty list. You can import it later with More › Import in the app.
en.KeepData=Keep your Baton data (download list, settings and logs in %1)?%n%nYes: keep it, a later installation finds everything again.%nNo: delete it.%n%nDownloaded files are never deleted.
it.PreviousTitle=Dati e altre copie di Baton
it.PreviousSubtitle=Cosa ha trovato l'installazione su questo PC
it.PreviousIntro=Non viene cancellato nulla: le altre copie e i loro dati restano dove sono.
it.PortableImport=Copia download e impostazioni della copia portable in %1 (consigliato)
it.LegacyFound=Sono stati trovati i dati di DownloadManager (il nome di Baton fino alla 1.1): vengono copiati in Baton al primo avvio, e la cartella precedente resta com'è.
it.CopiesFound=Baton si trova anche in %1. Da ora estensione del browser, notifiche e avvio con Windows useranno la copia installata.
it.CloseRunning=Baton è aperto. Chiudilo (icona nell'area di notifica › Esci), poi premi OK.
it.Importing=Copia dei dati della copia portable...
it.ImportFailed=Non è stato possibile copiare i dati della copia portable (sono rimasti intatti): Baton installato parte con l'elenco vuoto. Puoi importarli in seguito con Altro › Importa nell'app.
it.KeepData=Tenere i dati di Baton (elenco dei download, impostazioni e registri in %1)?%n%nSì: vengono tenuti, una nuova installazione ritrova tutto.%nNo: vengono eliminati.%n%nI file scaricati non vengono mai eliminati.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Files dropped from the extension in a newer version must not stay behind (the browser would load them).
Type: filesandordirs; Name: "{app}\extension"
; A package the app's updater had begun to unpack (it starts over from scratch anyway).
Type: filesandordirs; Name: "{app}\extension.new"

[Files]
Source: "{#SourceDir}\Baton.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
; Chromium browsers load the folder ("Load unpacked"); Firefox the package. An upgrade replaces both ([InstallDelete]).
Source: "{#ExtensionDir}\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#FirefoxPackage}"; DestDir: "{app}"; DestName: "baton-firefox.xpi"; Flags: ignoreversion

[Icons]
; The identity of the app's notifications: the shortcut and the windows group under it.
Name: "{autoprograms}\Baton"; Filename: "{app}\Baton.exe"; AppUserModelID: "Baton.App"
Name: "{autodesktop}\Baton"; Filename: "{app}\Baton.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Baton.exe"; Description: "{cm:LaunchProgram,Baton}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Browser host, baton: links, notification identity and start with Windows, where they point to this copy.
Filename: "{app}\Baton.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[UninstallDelete]
; Left by the app's own updates (the previous version, kept for "restore previous version").
Type: files; Name: "{app}\Baton.exe.old"
Type: files; Name: "{app}\Baton.exe.failed"
Type: filesandordirs; Name: "{app}\extension.old"
Type: filesandordirs; Name: "{app}\extension.new"
Type: filesandordirs; Name: "{app}\extension.failed"
Type: files; Name: "{app}\baton-firefox.xpi.old"
; The extension as the app's updater put it in place: not in the installer's own list of files.
Type: filesandordirs; Name: "{app}\extension"
Type: files; Name: "{app}\baton-firefox.xpi"
Type: dirifempty; Name: "{app}"

[Code]
var
  PortableData: String;
  PreviousPage: TInputOptionWizardPage;

function Report(const Exe, InstalledExe: String; var Running: Boolean; var Legacy, Portable: String; var Copies: String): Boolean;
var
  ReportFile: String;
  Lines: TArrayOfString;
  I, Code: Integer;
begin
  Result := False;
  Running := False;
  Legacy := '';
  Portable := '';
  Copies := '';
  ReportFile := ExpandConstant('{tmp}\setup-check.txt');
  DeleteFile(ReportFile);
  if not Exec(Exe, '--setup-check "' + ReportFile + '" "' + InstalledExe + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Exit;
  if not LoadStringsFromFile(ReportFile, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Lines[I] = 'running=1' then
      Running := True
    else if Pos('legacy=', Lines[I]) = 1 then
      Legacy := Copy(Lines[I], 8, MaxInt)
    else if Pos('portable=', Lines[I]) = 1 then
      Portable := Copy(Lines[I], 10, MaxInt)
    else if Pos('copy=', Lines[I]) = 1 then
    begin
      if Copies <> '' then
        Copies := Copies + ', ';
      Copies := Copies + Copy(Lines[I], 6, MaxInt);
    end;
  end;
  Result := True;
end;

procedure InitializeWizard;
var
  Exe, Legacy, Copies, Text: String;
  Running: Boolean;
begin
  PortableData := '';
  ExtractTemporaryFile('Baton.exe');
  Exe := ExpandConstant('{tmp}\Baton.exe');
  if not Report(Exe, WizardDirValue + '\Baton.exe', Running, Legacy, PortableData, Copies) then
    Exit;
  // Any open copy (also 1.2, also elsewhere) would keep the single instance: the installed app could not start.
  while Running and not WizardSilent do
  begin
    if MsgBox(CustomMessage('CloseRunning'), mbInformation, MB_OKCANCEL) = IDCANCEL then
      Break;
    Report(Exe, WizardDirValue + '\Baton.exe', Running, Legacy, PortableData, Copies);
  end;

  Text := CustomMessage('PreviousIntro');
  if Legacy <> '' then
    Text := Text + #13#10#13#10 + CustomMessage('LegacyFound');
  if Copies <> '' then
    Text := Text + #13#10#13#10 + FmtMessage(CustomMessage('CopiesFound'), [Copies]);
  if (Legacy = '') and (Copies = '') and (PortableData = '') then
    Exit;
  PreviousPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('PreviousTitle'), CustomMessage('PreviousSubtitle'), Text, False, False);
  if PortableData <> '' then
  begin
    PreviousPage.Add(FmtMessage(CustomMessage('PortableImport'), [ExtractFileDir(PortableData)]));
    PreviousPage.Values[0] := True;
  end;
end;

function ImportWanted: Boolean;
begin
  // Silent installations (winget) import too: it only copies, and only when the installed app has no data yet.
  Result := (PortableData <> '') and ((PreviousPage = nil) or PreviousPage.Values[0]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and ImportWanted then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('Importing');
    if not Exec(ExpandConstant('{app}\Baton.exe'), '--import-data "' + PortableData + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      SuppressibleMsgBox(CustomMessage('ImportFailed'), mbError, MB_OK, IDOK);
  end;
end;

function InitializeUninstall: Boolean;
var
  Exe, Legacy, Portable, Copies: String;
  Running: Boolean;
begin
  Result := True;
  Exe := ExpandConstant('{app}\Baton.exe');
  if UninstallSilent or not FileExists(Exe) then
    Exit;
  while Report(Exe, Exe, Running, Legacy, Portable, Copies) and Running do
  begin
    if MsgBox(CustomMessage('CloseRunning'), mbInformation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  // The data folder is kept unless the user asks otherwise; a silent uninstall (winget) always keeps it. A test run
  // (BATON_DATA, as for the app) can only ever reach its own folder.
  Data := GetEnv('BATON_DATA');
  if Data = '' then
    Data := ExpandConstant('{localappdata}\Baton');
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and DirExists(Data) then
  begin
    if MsgBox(FmtMessage(CustomMessage('KeepData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
      DelTree(Data, True, True, True);
  end;
end;
